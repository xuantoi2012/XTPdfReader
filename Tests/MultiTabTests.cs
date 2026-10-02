using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XTPdfMergeApp;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    static void TestMultipleTabLayout(string folder)
    {
        var app = CreateReaderTestApplication();
        var reader = new ReaderWindow { Width = 1280, Height = 820, Left = 2240, Top = 106 };
        app.MainWindow = reader;
        var frame = new DispatcherFrame();
        Exception? failure = null;
        reader.Show();
        reader.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            try
            {
                await reader.Session.OpenFilesInReaderAsync(Directory.GetFiles(folder, "*.pdf").Order().Take(16));
                var tabs = (ListBox)reader.FindName("ReaderDocumentTabs");
                var start = (FrameworkElement)reader.FindName("ReaderStartButton");
                var overflow = (Button)reader.FindName("ReaderTabsOverflowButton");
                var strip = (FrameworkElement)reader.FindName("ReaderDocumentTabsStrip");
                var home = (FrameworkElement)reader.FindName("StartPage");
                Check(reader.Session.Documents.Count == 16, "All 16 documents opened");
                foreach (double width in new[] { 760d, 1280, 1920 })
                {
                    reader.Width = width;
                    await Task.Delay(100);
                    reader.UpdateLayout();
                    Check(start.IsVisible && start.TransformToAncestor(strip).Transform(new Point()).X == 0, "Start remains pinned at " + width);
                    Check(overflow.IsVisible && tabs.Items.Count < 16, "Overflow replaces clipped tabs at " + width);
                    var containers = Enumerable.Range(0, tabs.Items.Count).Select(i => (ListBoxItem)tabs.ItemContainerGenerator.ContainerFromIndex(i)).ToArray();
                    Check(containers.Length > 0 && containers.Max(c => c.ActualWidth) - containers.Min(c => c.ActualWidth) < 1, "Visible tabs have equal widths at " + width);
                    double right = tabs.TransformToAncestor(strip).Transform(new Point(tabs.ActualWidth, 0)).X;
                    double left = overflow.TransformToAncestor(strip).Transform(new Point()).X;
                    Check(Math.Abs(right - left) < 1, "Overflow is directly beside last visible tab at " + width);
                }

                reader.Width = 1280;
                Invoke("ReaderTabsOverflow_Click", overflow, new RoutedEventArgs());
                Check(overflow.ContextMenu?.Items.Count == 16, "Overflow lists every document");
                var first = reader.Session.Documents[0];
                var last = reader.Session.Documents[^1];
                ((MenuItem)overflow.ContextMenu!.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                overflow.ContextMenu!.IsOpen = false;
                await Task.Delay(150);
                Check(ReferenceEquals(tabs.SelectedItem, first) && tabs.Items.Contains(first), "Selecting hidden first tab makes it visible and active");
                Invoke("SelectDocumentTab", last);
                await Task.Delay(150);
                Check(ReferenceEquals(tabs.SelectedItem, last) && tabs.Items.Contains(last), "Selecting hidden last tab makes it visible and active");
                Invoke("ShowStart", true);
                Check(home.IsVisible && tabs.SelectedItem == null, "Start clears document selection");
                Invoke("SelectDocumentTab", last);
                Check(!home.IsVisible && ReferenceEquals(tabs.SelectedItem, last), "Returning from Start restores document selection");

                var panel = (ReaderSidePanel)reader.FindName("ReaderSidePanel");
                foreach (string name in new[] { "ThumbnailTabButton", "BookmarkTabButton", "LayerTabButton", "CommentsTabButton", "FindTabButton" })
                {
                    var button = (XTStyle.Controls.XTButton)panel.FindName(name);
                    Check(string.IsNullOrEmpty(button.Text) && button.ToolTip != null, name + " is icon-only with tooltip");
                    Check(button.Template.FindName("ToolbarLabel", button) == null, name + " has no reserved label row");
                }

                reader.EditHost!.CloseDocument(last);
                await Task.Delay(100);
                Check(!tabs.Items.Contains(last) && reader.Session.Documents.Count == 15, "Closing selected tab updates strip");
                reader.Session.Undo();
                await Task.Delay(150);
                Check(reader.Session.Documents.Contains(last) && reader.Session.Documents.Count == 16, "Undo restores closed document");
                while (reader.Session.Documents.Count > 1) reader.EditHost.CloseDocument(reader.Session.Documents[^1]);
                await Task.Delay(100);
                Check(!overflow.IsVisible && tabs.Items.Count == 1 && start.IsVisible, "Overflow disappears when no longer needed");
                reader.EditHost.CloseDocument(reader.Session.Documents[0]);
                await Task.Delay(250);
                Check(tabs.Items.Count == 0 && start.IsVisible, "Start survives closing all documents");
                var view = (ContinuousPdfView)reader.FindName("ReaderContinuousView");
                Check(view.Pages.Count == 0, "Closing all tabs releases viewer page state");
                var warmup = (DispatcherTimer?)typeof(ReaderWindow).GetField("_thumbnailWarmupTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reader);
                Check(warmup?.IsEnabled != true, "Closing all tabs stops scheduled thumbnail work");
                Check(PdfThumbnailService.CachedDocumentCount == 0, "Closing all tabs releases native document cache");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                reader.Session.Documents.Clear();
                reader.ShutdownReader();
                reader.Close();
                PdfThumbnailService.PrepareForShutdown(TimeSpan.FromSeconds(5));
                frame.Continue = false;
            }
        }));
        Dispatcher.PushFrame(frame);
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

        void Invoke(string method, params object[] args)
            => typeof(ReaderWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(reader, args);
    }

    static void ProfileMultipleTabs(string folder, string label)
    {
        var app = CreateReaderTestApplication();
        AppSettings.ApplyRuntime();
        Console.WriteLine($"PDFium instances: {PdfiumPool.Count}");
        PdfThumbnailService.StartMemoryPolicy();
        var reader = new ReaderWindow { Width = 1280, Height = 820, Left = 2240, Top = 106 };
        app.MainWindow = reader;
        var frame = new DispatcherFrame();
        Exception? failure = null;
        reader.Show();
        reader.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            using var process = Process.GetCurrentProcess();
            long peakPrivate = 0;
            var tickDelays = new List<double>();
            long previousTick = Stopwatch.GetTimestamp();
            var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (_, _) =>
            {
                long now = Stopwatch.GetTimestamp();
                tickDelays.Add(Math.Max(0, Stopwatch.GetElapsedTime(previousTick, now).TotalMilliseconds - 50));
                previousTick = now;
                process.Refresh();
                peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
            };
            timer.Start();
            try
            {
                process.Refresh();
                long initial = process.PrivateMemorySize64;
                TimeSpan cpu = process.TotalProcessorTime;
                var paths = Directory.GetFiles(folder, "*.pdf").Order().Take(16).ToArray();
                var opening = Stopwatch.StartNew();
                await reader.Session.OpenFilesInReaderAsync(paths);
                double openMs = opening.Elapsed.TotalMilliseconds;
                await Task.Delay(3000);
                foreach (var group in reader.Session.Documents.Where(g => g.Pages.Count == 0))
                    Console.WriteLine(group.HeaderText);
                process.Refresh();
                long openedPrivate = process.PrivateMemorySize64;
                var view = (ContinuousPdfView)reader.FindName("ReaderContinuousView");
                var switches = new List<object>();
                foreach (int i in new[] { 0, 7, 15, 1, 8, 0 }.Where(i => i < reader.Session.Documents.Count))
                {
                    var group = reader.Session.Documents[i];
                    var watch = Stopwatch.StartNew();
                    await reader.ShowPageAsync(group, group.Pages[0]);
                    while ((!ReferenceEquals(view.Pages, group.Pages) || !HasRenderedPage(view, group.Pages[0])) && watch.ElapsedMilliseconds < 10000)
                        await Task.Delay(16);
                    switches.Add(new { Tab = i + 1, SharpMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1), Rendered = ReferenceEquals(view.Pages, group.Pages) && HasRenderedPage(view, group.Pages[0]) });
                    await Task.Delay(400);
                }
                await Task.Delay(1000);
                process.Refresh();
                var delays = tickDelays.Order().ToArray();
                var result = new
                {
                    Label = label, Files = paths.Length, Pages = reader.Session.Documents.Sum(g => g.Pages.Count),
                    InitialPrivateMB = Math.Round(initial / 1048576d, 1),
                    OpenPrivateMB = Math.Round(openedPrivate / 1048576d, 1),
                    FinalPrivateMB = Math.Round(process.PrivateMemorySize64 / 1048576d, 1),
                    PeakPrivateMB = Math.Round(peakPrivate / 1048576d, 1),
                    OpenMs = Math.Round(openMs, 1), CpuSeconds = Math.Round((process.TotalProcessorTime - cpu).TotalSeconds, 2),
                    UiDelayP95Ms = delays.Length == 0 ? 0 : Math.Round(delays[(int)((delays.Length - 1) * .95)], 1),
                    UiDelayMaxMs = delays.Length == 0 ? 0 : Math.Round(delays[^1], 1),
                    WarmThumbnails = reader.Session.WarmThumbnailCompleted,
                    NativeDocuments = PdfThumbnailService.CachedDocumentCount, Switches = switches
                };
                string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(Output, "multi-tab-" + label + ".json"), json);
                Console.WriteLine(json);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                timer.Stop();
                reader.Session.Documents.Clear();
                reader.ShutdownReader();
                reader.Close();
                PdfThumbnailService.PrepareForShutdown(TimeSpan.FromSeconds(5));
                frame.Continue = false;
            }
        }));
        Dispatcher.PushFrame(frame);
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    static Application CreateReaderTestApplication()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var path in new[]
        {
            "XTStyle;component/Themes/Generic.xaml", "XTStyle;component/Themes/Light.xaml",
            "XTPdfMergeApp;component/Resources/UiTokens.Light.xaml", "XTPdfMergeApp;component/Resources/UiIcons.xaml",
            "XTPdfMergeApp;component/Resources/UiStyles.xaml"
        })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/" + path) });
        return app;
    }

    static bool HasRenderedPage(ContinuousPdfView view, XTPdfMergeApp.Domain.PagePlacement row)
    {
        var states = (System.Collections.IDictionary)typeof(ContinuousPdfView).GetField("_states", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)!;
        var state = states[row];
        return state != null && state.GetType().GetField("Bitmap")!.GetValue(state) is System.Windows.Media.Imaging.BitmapSource;
    }
}
