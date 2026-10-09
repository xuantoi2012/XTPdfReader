using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp
{
    /// <summary>
    /// XT PDF Merge — app desktop bình thường (không chạy nền/khay, không tự
    /// theo dõi thư mục nữa): user tự kéo-thả file PDF vào cửa sổ. Đóng cửa
    /// sổ là thoát hẳn app.
    ///
    /// Riêng khi user bật tuỳ chọn "Dùng ứng dụng này khi nhấn View PDF trong
    /// pdfFactory" (xem PdfFactoryIntegrationService), pdfFactory sẽ TỰ CHẠY
    /// exe này kèm đường dẫn PDF mỗi lần bấm "View PDF file". Vì mỗi lần bấm
    /// sẽ chạy 1 tiến trình MỚI, vẫn cần single-instance: nếu cửa sổ đang mở
    /// sẵn (user đang kéo-thả sắp xếp dở), tiến trình mới chỉ chuyển tiếp
    /// đường dẫn PDF cho tiến trình đầu qua named pipe rồi thoát ngay — vừa
    /// tránh mở trùng cửa sổ, vừa GIỮ NGUYÊN state cửa sổ hiện có (kích
    /// thước/vị trí/các window PDF đang dở) thay vì mở mới đè lên.
    /// </summary>
    public partial class App : Application
    {
        private const string SingleInstanceMutexName = "XTPdfMergeApp_SingleInstance";
        private const string PipeName = "XTPdfMergeApp_IncomingPdfPipe";
        private const string OpenPrefix = "open|";

        /// <summary>Cửa sổ đọc — entry point và Application.MainWindow. Cửa sổ ghép nhiều file do chính nó
        /// tạo khi cần (ReaderWindow.OpenMergeWindow), App không biết tới.</summary>
        private ReaderWindow? _reader;
        private Mutex? _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Services.Loc.Initialize(); // the language of the interface (saved choice, or the language of Windows the first time)
            _ = Task.Run(() =>
            {
                // Task Manager and Windows lists show the name cached for the exe (MuiCache); an exe that was once named after its file keeps that name until it is rewritten.
                try
                {
                    string exe = Environment.ProcessPath ?? "";
                    if (exe.Length == 0) return;
                    using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache");
                    key.SetValue(exe + ".FriendlyAppName", "PDF Reader Pro");
                    key.SetValue(exe + ".ApplicationCompany", "XT Solution");
                }
                catch { /* cosmetic only */ }
            });

            // Cửa sổ đọc (ReaderWindow) là cửa sổ chính: đóng nó = thoát app (nó tự đóng cửa sổ ghép phụ).
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            // Lưới an toàn để debug: exception ném ra từ 1 Task "fire-and-forget" (không ai await)
            // trong .NET hiện đại KHÔNG làm crash app nữa — bị nuốt âm thầm, không cách nào biết
            // được nếu không tự bắt/log ở đúng chỗ (đã gặp đúng kiểu này khi debug tile render ở
            // Reader). Chỉ LOG, không set Handled/SetObserved theo hướng che giấu crash thật —
            // hành vi crash (nếu có) giữ nguyên như cũ, chỉ thêm khả năng NHÌN THẤY exception.
            TaskScheduler.UnobservedTaskException += (_, ex) => LogUnhandledException("UnobservedTaskException", ex.Exception);
            DispatcherUnhandledException += (_, ex) => LogUnhandledException("DispatcherUnhandledException", ex.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, ex) => LogUnhandledException("AppDomainUnhandledException", ex.ExceptionObject as Exception);

            string[] incomingPaths = GetIncomingPdfPaths(e.Args);

            // XTPDF_SECOND_INSTANCE=1 (test captures only): run beside a normal instance without taking over its mutex or file pipe.
            bool secondInstance = Environment.GetEnvironmentVariable("XTPDF_SECOND_INSTANCE") == "1";
            _singleInstanceMutex = new Mutex(initiallyOwned: true, secondInstance ? SingleInstanceMutexName + "_test" + Environment.ProcessId : SingleInstanceMutexName, out bool createdNew);
            if (!createdNew)
            {
                // Đã có 1 cửa sổ đang mở rồi — tiến trình NÀY chỉ chuyển tiếp
                // đường dẫn PDF (nếu có) rồi thoát ngay, giữ nguyên cửa sổ cũ.
                if (incomingPaths.Length > 0) ForwardToRunningInstance(incomingPaths);
                Shutdown();
                return;
            }

            if (!secondInstance) StartIncomingPdfPipeServer();

            // The splash first, so there is something on screen at once (also when a PDF was double-clicked); the rest of the start-up
            // follows as soon as the splash has been drawn.
            ShutdownMode = ShutdownMode.OnExplicitShutdown; // the splash must not become the main window
            _splash = new Controls.StartupSplash();
            _splash.Show();
            // The splash is topmost: it must not sit over a prompt (restore the last session, a PDF password...), so it goes as soon as
            // any other window opens.
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
            {
                if (_splash is not null && sender is Window w && w != _splash && w != _reader) DismissSplash();
            }));
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => ContinueStartup(incomingPaths)));
        }

        private Controls.StartupSplash? _splash;

        private void DismissSplash()
        {
            var splash = _splash;
            _splash = null;
            splash?.FadeOutAndClose();
        }

        private void ContinueStartup(string[] incomingPaths)
        {
            _splash?.SetStatus("Loading the interface…");
            ThemeService.ApplySaved();
            if (!PassLicenseGate()) { Shutdown(); return; }
            StartLicenseWatch();
            // Đối soát registry "View PDF" của pdfFactory (nếu user đã bật) — âm thầm, không hỏi.
            if (MergeAppSettingsStore.GetPdfFactoryViewEnabled()) PdfFactoryIntegrationService.ReconcileRegistry();

            AppSettings.ApplyRuntime();
            PdfThumbnailService.StartMemoryPolicy();
            _reader = new ReaderWindow();
            MainWindow = _reader;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            _reader.Show();
            _splash?.SetStatus(incomingPaths.Length > 0 ? "Opening the document…" : "Almost ready…");
            if (DiagnosticsLog.Begin())
            {
                var logTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
                    { Interval = TimeSpan.FromSeconds(DiagnosticsLog.SnapshotSeconds) };
                logTimer.Tick += (_, _) => DiagnosticsLog.Snapshot("snapshot", DiagnosticsReport.Build());
                logTimer.Start();
            }
            _ = FinishSplashAsync(_reader.StartSessionAsync(incomingPaths));

            // Chờ 1 chút cho app ổn định rồi mới âm thầm kiểm tra bản mới ở nền — không chặn khởi động, không
            // làm gì nếu chạy từ debug/không mạng/chưa có release (xem AppUpdateService).
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => AppUpdateService.CheckInBackgroundAsync());
        }

        /// <summary>Closes the splash when the session is up, the first opened file (if any) has its first page on screen and the window has been
        /// laid out; never later than 20 s, so a stuck start cannot hide the app behind it.</summary>
        private async Task FinishSplashAsync(Task session)
        {
            var splash = _splash;
            try
            {
                await Task.WhenAny(session, Task.Delay(TimeSpan.FromSeconds(20))); // a failed session reports itself; WhenAny does not rethrow
                if (_reader is { OpenWaitActive: true })
                {
                    var opened = new TaskCompletionSource();
                    void Done() { opened.TrySetResult(); }
                    _reader.OpenWaitEnded += Done;
                    if (_reader.OpenWaitActive) await Task.WhenAny(opened.Task, Task.Delay(TimeSpan.FromSeconds(20)));
                    _reader.OpenWaitEnded -= Done;
                }
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            catch { /* close the splash anyway */ }
            finally { if (_splash == splash) DismissSplash(); }
        }

        /// <summary>Trial / license check before the first window. A saved sign-in is renewed quietly first (a renewal bought since the last run
        /// must not show a dialog); the dialog opens only when the app may not run. False = the user gave up, the app ends.</summary>
        private bool PassLicenseGate()
        {
            var state = Licensing.LicenseManager.Reload();
            if (!state.AllowsUse)
                try { Task.Run(() => Licensing.LicenseManager.RefreshAsync()).Wait(TimeSpan.FromSeconds(8)); } catch { /* the dialog explains */ }
            if (Licensing.LicenseManager.Current.AllowsUse) return true;
            // The license window must not become the main window: closing it would end the app before the reader exists.
            var mode = ShutdownMode;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _splash?.Hide(); // it is topmost and would sit over the sign-in window
            bool ok = Controls.LicenseWindow.Gate();
            ShutdownMode = mode;
            MainWindow = null;
            if (ok) _splash?.Show();
            return ok;
        }

        /// <summary>While the app runs: renew the token about daily, and lock again if the trial or license ends during the session.</summary>
        private void StartLicenseWatch()
        {
            if (!Licensing.LicenseConfig.IsConfigured) return;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
            timer.Tick += async (_, _) =>
            {
                if (Licensing.LicenseManager.ShouldRefresh) await Licensing.LicenseManager.RefreshAsync();
                if (Licensing.LicenseManager.Reload().AllowsUse) return;
                timer.Stop();
                if (!Controls.LicenseWindow.Gate()) Shutdown();
                else timer.Start();
            };
            timer.Start();
            if (Licensing.LicenseManager.ShouldRefresh) _ = Licensing.LicenseManager.RefreshAsync();
        }

        private static void LogUnhandledException(string kind, Exception? ex)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "XTPdfMergeApp_UnhandledException.log");
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} | {kind}:{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { /* best-effort debug log only */ }
        }

        private static string[] GetIncomingPdfPaths(string[] args)
            => args
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Where(a => string.Equals(Path.GetExtension(a), ".pdf", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetExtension(a), ".xtset", StringComparison.OrdinalIgnoreCase))
                .Where(File.Exists)
                .Select(a =>
                {
                    try { return Path.GetFullPath(a); }
                    catch { return a; }
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);

        private static void ForwardToRunningInstance(string[] paths)
        {
            try
            {
                // This process was just started by the user (a double-click), so it may hand the foreground to the running copy: without it Windows only flashes the taskbar button.
                AllowSetForegroundWindow(-1);
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(timeout: 3000);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                // "open|" = the user opened these files (double-click, Open with): they go to the Reader. Plain lines are what the virtual printer delivers: they go to the merge inbox.
                foreach (var path in paths)
                    writer.WriteLine(OpenPrefix + path);
            }
            catch { }
        }

        /// <summary>Lắng nghe named pipe nền — tiến trình pdfFactory chạy SAU (khi cửa sổ đã mở sẵn) sẽ gửi đường dẫn PDF qua đây thay vì tự mở cửa sổ riêng. CHỈ thêm file vào cửa sổ hiện có, KHÔNG đổi WindowState/kích thước — giữ nguyên state đang thao tác dở.</summary>
        private void StartIncomingPdfPipeServer()
        {
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                        await server.WaitForConnectionAsync();
                        using var reader = new StreamReader(server);
                        var paths = new System.Collections.Generic.List<string>();
                        var opened = new System.Collections.Generic.List<string>();
                        string? path;
                        while ((path = await reader.ReadLineAsync()) != null)
                        {
                            if (string.IsNullOrWhiteSpace(path)) continue;
                            if (path.StartsWith(OpenPrefix, StringComparison.Ordinal)) opened.Add(path[OpenPrefix.Length..]);
                            else paths.Add(path);
                        }

                        if (paths.Count > 0 || opened.Count > 0)
                            Dispatcher.Invoke(() =>
                            {
                                if (paths.Count > 0) _reader?.ReceiveIncomingPdfs(paths);
                                if (opened.Count > 0) _reader?.ReceiveOpenRequest(opened);
                            });
                    }
                    catch { await Task.Delay(500); }
                }
            });
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AnnotationWorkingCopy.DeleteAll();
            // Chặn render PDFium mới + đợi lệnh đang chạy dở kết thúc TRƯỚC khi ProcessExit
            // gọi FPDF_DestroyLibrary() — tránh crash ExecutionEngineException do 1 thread khác
            // còn đang gọi vào PDFium (FPDF_LoadPage...) sau khi thư viện native đã bị huỷ.
            // 5s (trước là 2s): continuous mode có thể đang xếp hàng tới 20+ tile cùng lúc (mỗi
            // tile qua PDFium tuần tự do thư viện không an toàn đa luồng), rút hết hàng đợi có thể
            // mất hơn 2s — 2s cũ dễ hết hạn giữa chừng, để lại lệnh treo lơ lửng đúng lúc thư viện
            // bị huỷ (xem PdfThumbnailService._inFlightPublicCalls).
            if (DiagnosticsLog.Enabled)
            {
                DiagnosticsLog.Snapshot("thoát, trước dọn RAM", DiagnosticsReport.Build());
                DiagnosticsReport.CollectNow(); // đóng document PDFium + GC, để log có số sau khi dọn
                DiagnosticsLog.Snapshot("thoát, sau dọn RAM", DiagnosticsReport.Build());
            }
            PdfThumbnailService.PrepareForShutdown(TimeSpan.FromSeconds(5));
            BlankPageService.Cleanup();

            _singleInstanceMutex?.Dispose();
            base.OnExit(e);
        }
    }
}
