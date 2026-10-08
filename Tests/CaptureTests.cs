using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using iText.Kernel.Pdf;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;
using XTPdfMergeApp.Services.Capture;

internal static partial class Program
{
    /// <summary>A picture whose pixel (x, y) is (x % 256, y % 256, 128): every pixel tells where it came from.</summary>
    static BitmapSource CaptureTestPicture(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                pixels[i] = 128; pixels[i + 1] = (byte)(y % 256); pixels[i + 2] = (byte)(x % 256); pixels[i + 3] = 255;
            }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    static byte[] CapturePixel(BitmapSource image, int x, int y)
    {
        var pixel = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel;
    }

    /// <summary>Region maths and the cut-out: rectangle, ellipse and polygon, white or transparent outside.</summary>
    static void TestCaptureRegion()
    {
        var rect = CaptureRegion.Rectangle(new Point(80, 70), new Point(20, 10));
        Check(rect.Bounds == new Int32Rect(20, 10, 60, 60), "A rectangle picked from the bottom right corner has the same box");
        Check(rect.Contains(20, 10) && rect.Contains(79.5, 69.5) && !rect.Contains(80, 40) && !rect.Contains(10, 40), "Rectangle containment");
        Check(!CaptureRegion.Rectangle(new Point(5, 5), new Point(6, 6)).IsUsable, "A 1 px region is not usable");

        var oval = CaptureRegion.Ellipse(new Point(0, 0), new Point(100, 60));
        Check(oval.Contains(50, 30) && !oval.Contains(2, 2) && !oval.Contains(98, 58) && oval.Contains(50, 1), "Ellipse containment");

        var triangle = CaptureRegion.Polygon(new[] { new Point(0, 0), new Point(100, 0), new Point(50, 80) });
        Check(triangle.IsUsable && triangle.Bounds == new Int32Rect(0, 0, 100, 80), "Polygon box");
        Check(triangle.Contains(50, 20) && !triangle.Contains(5, 60) && !triangle.Contains(95, 60), "Polygon containment");
        var bowtie = CaptureRegion.Polygon(new[] { new Point(0, 0), new Point(100, 100), new Point(100, 0), new Point(0, 100) });
        Check(bowtie.Contains(10, 50) && bowtie.Contains(90, 50) && !bowtie.Contains(50, 10), "A self-crossing outline: the side lobes are inside, the crossing leaves the top and bottom out");
        Check(!CaptureRegion.Polygon(new[] { new Point(0, 0), new Point(50, 0) }).IsUsable, "Two corners are not a polygon");

        var picture = CaptureTestPicture(200, 150);
        var cut = CaptureImaging.Crop(picture, rect, transparentOutside: false);
        Check(cut.PixelWidth == 60 && cut.PixelHeight == 60 && CapturePixel(cut, 0, 0) is [128, 10, 20, 255] && CapturePixel(cut, 59, 59) is [128, 69, 79, 255], "Rectangle cut-out keeps the source pixels");

        var white = CaptureImaging.Crop(picture, oval, transparentOutside: false);
        Check(white.PixelWidth == 100 && white.PixelHeight == 60, "Ellipse cut-out is its box");
        Check(CapturePixel(white, 0, 0) is [255, 255, 255, 255] && CapturePixel(white, 50, 30) is [128, 30, 50, 255], "Outside the ellipse white, inside the picture");
        var clear = CaptureImaging.Crop(picture, oval, transparentOutside: true);
        Check(CapturePixel(clear, 0, 0)[3] == 0 && CapturePixel(clear, 50, 30)[3] == 255, "Outside the ellipse transparent when asked");
        var tri = CaptureImaging.Crop(picture, triangle, transparentOutside: true);
        Check(CapturePixel(tri, 2, 70)[3] == 0 && CapturePixel(tri, 50, 20)[3] == 255, "Polygon cut-out");

        var edge = CaptureImaging.Crop(picture, CaptureRegion.Rectangle(new Point(150, 100), new Point(260, 220)), false);
        Check(edge.PixelWidth == 50 && edge.PixelHeight == 50, "A region past the screen edge is cut to the picture");
        Check(CaptureImaging.EncodePng(cut).Length > 100, "PNG encoding");
    }

    /// <summary>Every capture is a one-page PDF whose page is the picture; the store lists newest first and finds old ones.</summary>
    static void TestCaptureLibrary()
    {
        string folder = Path.Combine(Output, "capture-library");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        CaptureLibrary.Folder = folder;
        try
        {
            Check(CaptureLibrary.List().Count == 0, "No folder, no captures");
            var first = CaptureLibrary.Save(CaptureTestPicture(400, 300), new DateTime(2026, 10, 1, 9, 0, 0));
            var second = CaptureLibrary.Save(CaptureTestPicture(200, 100), new DateTime(2026, 10, 1, 9, 0, 0));
            Check(first != second && File.Exists(first) && File.Exists(second), "Two captures in the same second get different names");
            using (var document = new PdfDocument(new PdfReader(first)))
            {
                var size = document.GetPage(1).GetPageSize();
                Check(document.GetNumberOfPages() == 1 && Math.Abs(size.GetWidth() - 300) < 0.01 && Math.Abs(size.GetHeight() - 225) < 0.01, "One page, 400 x 300 px = 300 x 225 pt");
            }
            Check(Math.Abs(CaptureLibrary.PixelWidth(first) - 400) < 1 && Math.Abs(CaptureLibrary.PixelWidth(second) - 200) < 1, "The pixel width comes back from the page size");
            Check(!Directory.EnumerateFiles(folder, "*.tmp").Any(), "No half-written file is left");

            File.SetCreationTime(first, DateTime.Now.AddDays(-100));
            File.SetLastWriteTime(first, DateTime.Now.AddDays(-100));
            var list = CaptureLibrary.List();
            Check(list.Count == 2 && list[0].Path == second, "Newest first");
            var old = CaptureLibrary.FindOlderThan(30, Array.Empty<string>());
            Check(old.Count == 1 && old[0].Path == first, "Only the 100 day old capture is older than 30 days");
            Check(CaptureLibrary.FindOlderThan(30, new[] { first }).Count == 0, "A capture open in a tab is never offered");
        }
        finally { CaptureLibrary.Folder = CaptureLibrary.DefaultFolder; }
    }

    /// <summary>The real screen: the frozen picture has the virtual screen's size, windows come top first, and a window can be asked to paint itself.</summary>
    static void TestScreenGrabber()
    {
        if (System.Windows.Application.Current == null) CreateReaderTestApplication();
        var host = new Window { Width = 320, Height = 200, Left = -32000, Top = -32000, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Background = Brushes.Red, Title = "capture-grab-test" };
        host.Show();
        Pump(TimeSpan.FromMilliseconds(200));
        try
        {
            var screen = ScreenGrabber.VirtualScreen;
            var snapshot = ScreenGrabber.Snapshot();
            Check(snapshot.Image.PixelWidth == screen.Width && snapshot.Image.PixelHeight == screen.Height && screen.Width > 100, "The snapshot is the virtual screen");
            Check(snapshot.Windows.Select(w => w.ZOrder).SequenceEqual(Enumerable.Range(0, snapshot.Windows.Count)), "Windows are numbered from the top");
            var mine = snapshot.Windows.FirstOrDefault(w => w.Title == "capture-grab-test");
            Check(mine != null && mine.Bounds.Width >= 300 && mine.Bounds.Height >= 180, "The test window is listed with its frame");
            if (mine != null)
            {
                var grabbed = ScreenGrabber.GrabWindow(mine);
                Check(grabbed != null && grabbed.PixelWidth >= 300 && CapturePixel(grabbed, grabbed.PixelWidth / 2, grabbed.PixelHeight / 2) is [_, _, > 200, _], "A window that is not on screen paints itself red");
            }
            var other = ScreenGrabber.Snapshot(new HashSet<uint> { (uint)Environment.ProcessId });
            Check(!other.Windows.Any(w => w.Title == "capture-grab-test"), "Windows of an excluded process are left out");
            Check(ScreenGrabber.IsBlank(BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, new byte[8 * 8 * 4], 8 * 4)), "A black picture counts as blank");
            Check(!ScreenGrabber.IsBlank(CaptureTestPicture(40, 40)), "A real picture does not");
        }
        finally { host.Close(); }
    }

    static CaptureOverlayWindow NewOverlay(Func<CaptureWindow, BitmapSource?>? grab = null, double scale = 1)
    {
        var windows = new List<CaptureWindow>
        {
            new(new IntPtr(1), "Front", new Int32Rect(100, 100, 300, 200), 0),
            new(new IntPtr(2), "Back", new Int32Rect(50, 50, 500, 400), 1),
        };
        var snapshot = new ScreenSnapshot(CaptureTestPicture(800, 600), new Point(0, 0), windows);
        var overlay = new CaptureOverlayWindow(snapshot, scale, atScreen: false, grab);
        overlay.Show();
        Pump(TimeSpan.FromMilliseconds(200));
        return overlay;
    }

    /// <summary>The overlay driven like a mouse: window hover and cycling, a covered window, rectangle by drag and by two clicks, ellipse, polygon.</summary>
    static void TestCaptureOverlay()
    {
        if (System.Windows.Application.Current == null) CreateReaderTestApplication();
        bool priorTransparent = XTPdfMergeApp.Services.AppSettings.CaptureTransparentOutside;
        XTPdfMergeApp.Services.AppSettings.CaptureTransparentOutside = true;
        try
        {
            // Window mode
            int grabbed = 0;
            var overlay = NewOverlay(w => { grabbed++; return null; });
            Check(overlay.Mode == CapturePick.Window && overlay.Region == null && !overlay.ActionBarVisible, "Starts in Window mode with nothing picked");
            overlay.OnPointerMove(new Point(150, 150));
            Check(overlay.HoveredWindow?.Title == "Front", "The window on top under the cursor is hovered");
            overlay.OnPointerMove(new Point(450, 90));
            Check(overlay.HoveredWindow?.Title == "Back", "Where only the back window is, that one is hovered");
            overlay.OnPointerMove(new Point(700, 500));
            Check(overlay.HoveredWindow == null, "Over no window nothing is highlighted");
            overlay.OnPointerMove(new Point(150, 150));
            overlay.OnKey(Key.Tab);
            Check(overlay.HoveredWindow?.Title == "Back", "Tab moves to the window behind the one on top");
            overlay.OnKey(Key.Tab);
            Check(overlay.HoveredWindow?.Title == "Front", "Tab wraps around");
            overlay.OnKey(Key.Tab);
            overlay.OnPointerDown(new Point(150, 150));
            Check(overlay.Region?.Bounds == new Int32Rect(50, 50, 500, 400) && overlay.ActionBarVisible, "Clicking picks the hovered window and shows Copy / Edit");
            overlay.Finish(CaptureAction.Copy);
            Check(overlay.Outcome is { Action: CaptureAction.Copy } o && o.Image.PixelWidth == 500 && o.Image.PixelHeight == 400, "A covered window is asked to paint itself, and when it cannot the frozen pixels are cut (500 x 400)");
            Check(grabbed == 1, "The covered window was asked once");

            overlay = NewOverlay(w => { grabbed++; return null; });
            overlay.OnPointerMove(new Point(150, 150));
            overlay.OnPointerDown(new Point(150, 150));
            overlay.Finish(CaptureAction.Edit);
            Check(overlay.Outcome is { Action: CaptureAction.Edit } e && e.Image.PixelWidth == 300 && CapturePixel(e.Image, 0, 0) is [128, 100, 100, 255], "The front window is cut from the frozen picture (not asked again)");
            Check(grabbed == 1, "An uncovered window is not asked to paint itself");

            // Rectangle: drag
            overlay = NewOverlay();
            overlay.OnKey(Key.R);
            Check(overlay.Mode == CapturePick.Rectangle, "R switches to Rectangle");
            overlay.OnPointerDown(new Point(10, 10));
            overlay.OnPointerMove(new Point(60, 40));
            overlay.OnPointerMove(new Point(110, 90));
            overlay.OnPointerUp(new Point(110, 90));
            Check(overlay.Region?.Bounds == new Int32Rect(10, 10, 100, 80) && overlay.ActionBarVisible, "Dragging picks a rectangle");
            overlay.OnKey(Key.Enter);
            Check(overlay.Outcome is { Action: CaptureAction.Copy } r && r.Image.PixelWidth == 100 && r.Image.PixelHeight == 80, "Enter copies the rectangle");

            // Rectangle: two clicks
            overlay = NewOverlay();
            overlay.OnKey(Key.R);
            overlay.OnPointerDown(new Point(20, 20));
            overlay.OnPointerUp(new Point(20, 20));
            Check(overlay.Region == null && !overlay.ActionBarVisible, "A click alone only sets the first corner");
            overlay.OnPointerMove(new Point(80, 70));
            Check(overlay.Region?.Bounds == new Int32Rect(20, 20, 60, 50), "The rectangle follows the cursor until the second click");
            overlay.OnPointerDown(new Point(120, 100));
            overlay.OnPointerUp(new Point(120, 100));
            Check(overlay.Region?.Bounds == new Int32Rect(20, 20, 100, 80) && overlay.ActionBarVisible, "The second click finishes the rectangle");
            overlay.OnKey(Key.Escape);
            Check(overlay.Region == null && !overlay.ActionBarVisible, "Esc clears the pick first");
            overlay.OnKey(Key.Escape);
            Check(!overlay.IsVisible, "A second Esc leaves the capture");

            // Ellipse
            overlay = NewOverlay();
            overlay.OnKey(Key.E);
            overlay.OnPointerDown(new Point(100, 100));
            overlay.OnPointerUp(new Point(300, 220));
            overlay.Finish(CaptureAction.Copy);
            Check(overlay.Outcome!.Image.PixelWidth == 200 && CapturePixel(overlay.Outcome.Image, 0, 0)[3] == 0 && CapturePixel(overlay.Outcome.Image, 100, 60)[3] == 255, "A circle keeps its middle and clears its corners");

            // Polygon
            overlay = NewOverlay();
            overlay.OnKey(Key.P);
            overlay.OnPointerDown(new Point(10, 10));
            overlay.OnPointerDown(new Point(110, 10));
            overlay.OnPointerDown(new Point(60, 90));
            overlay.OnPointerMove(new Point(30, 30));
            overlay.OnKey(Key.Back);
            overlay.OnPointerDown(new Point(60, 90));
            Check(overlay.Region == null, "Backspace removed the last corner, and the polygon is still open");
            overlay.OnPointerDown(new Point(13, 12));
            Check(overlay.Region is { Kind: CaptureShapeKind.Polygon } && overlay.Region.Bounds == new Int32Rect(10, 10, 100, 80) && overlay.ActionBarVisible, "Clicking next to the first corner closes the polygon");
            overlay.Finish(CaptureAction.Copy);
            var shape = overlay.Outcome!.Image;
            Check(CapturePixel(shape, 50, 5)[3] == 255 && CapturePixel(shape, 2, 70)[3] == 0, "The polygon keeps its inside and clears the rest");
            overlay.Close();

            overlay = NewOverlay();
            overlay.OnKey(Key.P);
            overlay.OnPointerDown(new Point(10, 10));
            overlay.OnPointerDown(new Point(110, 10));
            overlay.OnPointerDown(new Point(60, 90));
            overlay.OnKey(Key.Enter);
            Check(overlay.Region is { Kind: CaptureShapeKind.Polygon }, "Enter closes the polygon");
            SavePng(overlay, "capture-overlay-polygon");
            overlay.Close();
        }
        finally { XTPdfMergeApp.Services.AppSettings.CaptureTransparentOutside = priorTransparent; }
    }

    /// <summary>The Reader side: Copy keeps the picture in the store and on the clipboard; Edit opens it in a tab; the store window lists it.</summary>
    static void TestCaptureReaderFlow()
    {
        string folder = Path.Combine(Output, "capture-reader");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        CaptureLibrary.Folder = folder;
        try
        {
            RunReaderFlow("capture-reader-flow", async f =>
            {
                var picture = CaptureTestPicture(640, 480);
                string? copied = await f.Window.HandleCaptureAsync(new CaptureOutcome(CaptureAction.Copy, picture));
                Check(copied != null && File.Exists(copied) && Path.GetDirectoryName(copied) == folder, "Copy keeps the picture in the store");
                try { Check(System.Windows.Clipboard.ContainsImage(), "Copy puts the picture on the clipboard"); }
                catch (System.Runtime.InteropServices.ExternalException) { /* the clipboard was busy: not what is tested here */ }

                string? edited = await f.Window.HandleCaptureAsync(new CaptureOutcome(CaptureAction.Edit, CaptureTestPicture(320, 200)));
                await Task.Delay(2500);
                Check(edited != null && f.Window.Session.Documents.Any(g => string.Equals(g.SourcePath, edited, StringComparison.OrdinalIgnoreCase)), "Edit opens the capture in a tab");

                // add a shape to the capture like any PDF
                var spec = RectSpec("xt-cap", 0.2, 0.2, 0.6, 0.6);
                await ((XTPdfMergeApp.IReaderPageEditHost)f.Window.Session).ApplyAnnotationChangesAsync(edited!, new[] { new QuickAnnotationChange(null, spec) }, "add");
                await Task.Delay(800);
                var page = await AnnotationStore.GetPageAsync(edited!, 1);
                Check(page != null && page.Annotations.Count == 1, "The capture takes annotations like any page");

                var window = CaptureLibraryWindow.ShowFor(f.Window, _ => { }, () => { }, () => f.Window.Session.Documents.Select(g => g.SourcePath).ToList());
                window.Left = -32000; window.Top = -32000;
                Pump(TimeSpan.FromMilliseconds(200));
                await Task.Delay(2500);
                Check(window.CardCount == 2, "The Captures window lists both (got " + window.CardCount + ")");
                var rendered = await CaptureLibraryWindow.RenderAsync(copied!);
                Check(rendered != null && rendered.PixelWidth == 640, "A capture renders back at the size it was taken (got " + rendered?.PixelWidth + ")");
                SavePng(window, "capture-library");
                window.Close();
            });
        }
        finally { CaptureLibrary.Folder = CaptureLibrary.DefaultFolder; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    /// <summary>Run by hand (it dims the real screen for a second, so it is not in the smoke chain): the overlay covers the virtual screen pixel for pixel and takes the keyboard focus.</summary>
    static void TestCaptureOverlayOnScreen()
    {
        if (System.Windows.Application.Current == null) CreateReaderTestApplication();
        var snapshot = ScreenGrabber.Snapshot();
        double scale = 1;
        var probe = new Window { Width = 10, Height = 10, Left = -32000, Top = -32000, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        probe.Show();
        scale = System.Windows.Media.VisualTreeHelper.GetDpi(probe).DpiScaleX;
        probe.Close();
        var overlay = new CaptureOverlayWindow(snapshot, scale);
        overlay.Show();
        Pump(TimeSpan.FromMilliseconds(600));
        var handle = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
        GetWindowRect(handle, out NativeRect rect);
        var screen = ScreenGrabber.VirtualScreen;
        Console.WriteLine($"overlay {rect.L},{rect.T} {rect.R - rect.L}x{rect.B - rect.T} screen {screen.X},{screen.Y} {screen.Width}x{screen.Height} scale {scale} windows {snapshot.Windows.Count}");
        Check(rect.L == screen.X && rect.T == screen.Y && rect.R - rect.L == screen.Width && rect.B - rect.T == screen.Height, "The overlay covers the virtual screen pixel for pixel");
        Check(GetForegroundWindow() == handle || overlay.IsActive, "The overlay has the focus (so Esc and Enter reach it)");
        overlay.OnKey(Key.Escape);
        Check(!overlay.IsVisible, "Esc closes it");
    }
}
