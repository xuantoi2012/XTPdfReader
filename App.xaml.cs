using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Velopack;
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

        /// <summary>Cửa sổ đọc — entry point và Application.MainWindow. Cửa sổ ghép nhiều file do chính nó
        /// tạo khi cần (ReaderWindow.OpenMergeWindow), App không biết tới.</summary>
        private ReaderWindow? _reader;
        private Mutex? _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // PHẢI đứng đầu tiên: Velopack tự bắt các cờ --veloapp-install/--veloapp-updated/--veloapp-uninstall
            // mà trình cài/gỡ/cập nhật gọi app kèm theo (tạo/xoá shortcut...) rồi tự thoát luôn nếu gặp — chạy
            // sau logic khác (single-instance mutex, mở cửa sổ...) sẽ làm các bước đó chạy nhầm lúc cài/gỡ.
            VelopackApp.Build().Run();

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

            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
            if (!createdNew)
            {
                // Đã có 1 cửa sổ đang mở rồi — tiến trình NÀY chỉ chuyển tiếp
                // đường dẫn PDF (nếu có) rồi thoát ngay, giữ nguyên cửa sổ cũ.
                if (incomingPaths.Length > 0) ForwardToRunningInstance(incomingPaths);
                Shutdown();
                return;
            }

            StartIncomingPdfPipeServer();

            ThemeService.ApplySaved();
            // Đối soát registry "View PDF" của pdfFactory (nếu user đã bật) — âm thầm, không hỏi.
            if (MergeAppSettingsStore.GetPdfFactoryViewEnabled()) PdfFactoryIntegrationService.ReconcileRegistry();

            AppSettings.ApplyRuntime();
            PdfThumbnailService.StartMemoryPolicy();
            _reader = new ReaderWindow();
            MainWindow = _reader;
            _reader.Show();
            if (DiagnosticsLog.Begin())
            {
                var logTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
                    { Interval = TimeSpan.FromSeconds(DiagnosticsLog.SnapshotSeconds) };
                logTimer.Tick += (_, _) => DiagnosticsLog.Snapshot("snapshot", DiagnosticsReport.Build());
                logTimer.Start();
            }
            if (incomingPaths.Length > 0) _ = _reader.Session.OpenFilesInReaderAsync(incomingPaths);

            // Chờ 1 chút cho app ổn định rồi mới âm thầm kiểm tra bản mới ở nền — không chặn khởi động, không
            // làm gì nếu chạy từ debug/không mạng/chưa có release (xem AppUpdateService).
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => AppUpdateService.CheckInBackgroundAsync());
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
                .Where(a => string.Equals(Path.GetExtension(a), ".pdf", StringComparison.OrdinalIgnoreCase))
                .Where(File.Exists)
                .Select(a =>
                {
                    try { return Path.GetFullPath(a); }
                    catch { return a; }
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        private static void ForwardToRunningInstance(string[] paths)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(timeout: 3000);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                foreach (var path in paths)
                    writer.WriteLine(path);
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
                        string? path;
                        while ((path = await reader.ReadLineAsync()) != null)
                        {
                            if (!string.IsNullOrWhiteSpace(path))
                                paths.Add(path);
                        }

                        if (paths.Count > 0)
                            Dispatcher.Invoke(() =>
                            {
                                if (_reader != null) _ = _reader.Session.OpenFilesInReaderAsync(paths);
                                _reader?.Activate(); // đưa lên trước cho user thấy, KHÔNG đổi WindowState/kích thước hiện có
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
