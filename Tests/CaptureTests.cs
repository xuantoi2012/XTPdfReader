using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XTCapture;
using XTPdfMergeApp.Services;

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

    /// <summary>XT Capture's settings live under their own registry key in the tests, so a real install is never touched.</summary>
    static void WithCaptureTestSettings(Action body)
    {
        string key = @"Software\XTCaptureTest";
        string priorKey = CaptureSettings.RegKey;
        CaptureSettings.RegKey = key;
        try { body(); }
        finally
        {
            CaptureSettings.RegKey = priorKey;
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false); } catch { }
        }
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

    /// <summary>The handles of a picked area: eight on a rectangle / circle, one per corner on a polygon; resize, turn over, minimum size, move, clamp.</summary>
    static void TestCaptureHandles()
    {
        var limit = new Size(400, 300);
        var rect = CaptureRegion.Rectangle(new Point(100, 100), new Point(200, 160));
        Check(CaptureHandles.Positions(rect).Count == 8, "A rectangle has eight handles");
        Check(CaptureHandles.HitTest(rect, new Point(102, 99), 6).Kind == CaptureHandle.TopLeft, "The top left corner is hit near it");
        Check(CaptureHandles.HitTest(rect, new Point(150, 101), 6).Kind == CaptureHandle.Top, "The top middle is the Top handle");
        Check(CaptureHandles.HitTest(rect, new Point(199, 130), 6).Kind == CaptureHandle.Right, "The right middle is the Right handle");
        Check(CaptureHandles.HitTest(rect, new Point(150, 130), 6).Kind == CaptureHandle.Move, "Inside, away from the handles, moves");
        Check(CaptureHandles.HitTest(rect, new Point(300, 250), 6).Kind == CaptureHandle.None, "Outside is nothing (a click there starts a new area)");

        var grown = CaptureHandles.Resize(rect, new CaptureHandleHit(CaptureHandle.BottomRight), new Point(260, 220), limit);
        Check(grown.Bounds == new Int32Rect(100, 100, 160, 120), "The bottom right handle drags the corner");
        var narrowed = CaptureHandles.Resize(rect, new CaptureHandleHit(CaptureHandle.Left), new Point(150, 400), limit);
        Check(narrowed.Bounds == new Int32Rect(150, 100, 50, 60), "A side handle changes one side only");
        var turned = CaptureHandles.Resize(rect, new CaptureHandleHit(CaptureHandle.Right), new Point(40, 130), limit);
        Check(turned.Bounds == new Int32Rect(40, 100, 60, 60), "Dragged past the other side, the box turns over");
        var tiny = CaptureHandles.Resize(rect, new CaptureHandleHit(CaptureHandle.Right), new Point(101, 130), limit);
        Check(tiny.IsUsable && CaptureHandles.Box(tiny).Width >= CaptureHandles.MinimumSize, "It never collapses");
        var clamped = CaptureHandles.Resize(rect, new CaptureHandleHit(CaptureHandle.BottomRight), new Point(900, 900), limit);
        Check(clamped.Bounds == new Int32Rect(100, 100, 300, 200), "It stays inside the screen");

        var moved = CaptureHandles.Move(rect, 30, -20, limit);
        Check(moved.Bounds == new Int32Rect(130, 80, 100, 60), "Move shifts the area");
        var pinned = CaptureHandles.Move(rect, 900, 900, limit);
        Check(pinned.Bounds == new Int32Rect(300, 240, 100, 60), "Move stops at the screen edge");
        var oval = CaptureRegion.Ellipse(new Point(100, 100), new Point(200, 160));
        Check(CaptureHandles.Resize(oval, new CaptureHandleHit(CaptureHandle.Bottom), new Point(150, 200), limit).Kind == CaptureShapeKind.Ellipse, "A circle stays a circle");

        var triangle = CaptureRegion.Polygon(new[] { new Point(10, 10), new Point(110, 10), new Point(60, 90) });
        Check(CaptureHandles.Positions(triangle).Count == 3 && CaptureHandles.HitTest(triangle, new Point(111, 11), 6) is { Kind: CaptureHandle.Vertex, Index: 1 }, "A polygon has one handle per corner");
        var dragged = CaptureHandles.Resize(triangle, new CaptureHandleHit(CaptureHandle.Vertex, 2), new Point(60, 150), limit);
        Check(dragged.Points[2] == new Point(60, 150) && dragged.Points[0] == triangle.Points[0], "A polygon corner moves alone");
        Check(CaptureHandles.Move(triangle, 5, 5, limit).Points[1] == new Point(115, 15), "A polygon moves as a whole");
    }

    /// <summary>The Store: a folder per capture (picture as taken, small picture, meta); newest first; old ones found; half-written ones ignored.</summary>
    static void TestCaptureStore()
    {
        string folder = Path.Combine(Output, "capture-store");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        CaptureStore.Folder = folder;
        try
        {
            Check(CaptureStore.List().Count == 0, "No folder, no captures");
            var first = CaptureStore.Save(CaptureTestPicture(800, 400), new DateTime(2026, 10, 1, 9, 0, 0));
            var second = CaptureStore.Save(CaptureTestPicture(200, 100), new DateTime(2026, 10, 1, 9, 0, 0));
            Check(first.Id != second.Id && File.Exists(first.OriginalPath) && File.Exists(second.ThumbPath) && File.Exists(Path.Combine(first.Folder, CaptureStore.MetaFile)), "Two captures in the same second get their own folders with the three files");
            Check(first.Width == 800 && first.Height == 400, "The size comes from meta.json");
            var original = CaptureStore.LoadOriginal(first);
            Check(original.PixelWidth == 800 && CapturePixel(original, 5, 7) is [128, 7, 5, 255], "The original is kept pixel for pixel");
            var thumb = CaptureStore.LoadThumbnail(first);
            Check(thumb.PixelWidth == 360 && thumb.PixelHeight == 180, "The list picture is fitted to 360 px");
            Check(CaptureStore.LoadThumbnail(second).PixelWidth == 200, "A small capture is not enlarged");
            Check(!Directory.EnumerateDirectories(folder, "*.tmp").Any(), "No half-written folder is left");

            Directory.CreateDirectory(Path.Combine(folder, "Capture broken.tmp"));
            File.WriteAllText(Path.Combine(folder, "Capture broken.tmp", CaptureStore.OriginalFile), "x");
            Directory.CreateDirectory(Path.Combine(folder, "not a capture"));
            Check(CaptureStore.List().Count == 2, "A .tmp folder and a folder without a picture are ignored");

            var list = CaptureStore.List();
            Check(list[0].Id == second.Id || list[0].Created >= list[1].Created, "Newest first");
            var old = CaptureStore.FindOlderThan(30, new DateTime(2026, 12, 1));
            Check(old.Count == 2, "Both are older than 30 days on 1 December");
            Check(CaptureStore.FindOlderThan(30, new DateTime(2026, 10, 10)).Count == 0, "None is older than 30 days on 10 October");
            Check(CaptureStore.FormatSize(500) == "1 KB" && CaptureStore.FormatSize(3 * 1024 * 1024) == "3 MB", "Sizes read well");
        }
        finally { CaptureStore.Folder = CaptureStore.DefaultFolder; }
    }

    /// <summary>Shortcut text, the Windows registration and the fallback when another program owns the shortcut.</summary>
    static void TestCaptureHotkey()
    {
        if (System.Windows.Application.Current == null) CreateReaderTestApplication();
        Check(CaptureHotkey.Parse("Win+C") is { Modifiers: CaptureHotkey.Win, VirtualKey: 'C' }, "Win+C parses");
        Check(CaptureHotkey.Parse("ctrl + alt + f9") is { Modifiers: CaptureHotkey.Ctrl | CaptureHotkey.Alt, VirtualKey: 0x78 }, "Case, spaces and function keys parse");
        Check(CaptureHotkey.Parse("PrintScreen") is { Modifiers: 0, VirtualKey: 0x2C }, "PrintScreen may stand alone");
        Check(CaptureHotkey.Parse("C") == null && CaptureHotkey.Parse("Ctrl+") == null && CaptureHotkey.Parse("Ctrl+A+B") == null && CaptureHotkey.Parse("") == null, "A bare letter, a missing key and two keys are refused");
        Check(CaptureHotkey.Presets.All(p => CaptureHotkey.Parse(p)?.ToString() == p), "Every preset round-trips as text");

        using var first = new HotkeyService();
        using var second = new HotkeyService();
        var status = first.Apply("Ctrl+Alt+F11");
        Check(status == HotkeyStatus.Preferred && first.Active?.ToString() == "Ctrl+Alt+F11", "A free shortcut is taken as asked");
        var other = second.Apply("Ctrl+Alt+F11");
        Check(other == HotkeyStatus.Fallback && second.Active != null && second.Active?.ToString() != "Ctrl+Alt+F11" && second.Message.Contains("used by another program"), "A shortcut that is taken falls back to the next free one and says so");
        int pressed = 0;
        first.Pressed += () => pressed++;
        PostMessage(first.Handle, 0x0312, new IntPtr(0x5854), IntPtr.Zero);
        Pump(TimeSpan.FromMilliseconds(200));
        Check(pressed == 1, "The Windows hotkey message raises Pressed");
        first.Apply("Ctrl+Alt+F10");
        Check(first.Active?.ToString() == "Ctrl+Alt+F10", "Changing the shortcut releases the old one");
        Check(second.Apply("Ctrl+Alt+F11") == HotkeyStatus.Preferred, "The released shortcut is free again");
        var win = new HotkeyService();
        Console.WriteLine("Win+C on this PC: " + win.Apply("Win+C") + " -> " + win.Active);
        win.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Settings: defaults, saved values, the Reader's theme choice is read from the Reader's key.</summary>
    static void TestCaptureSettings() => WithCaptureTestSettings(() =>
    {
        Check(CaptureSettings.ShowFloatingButton && CaptureSettings.Hotkey == "Win+C" && !CaptureSettings.TransparentOutside && CaptureSettings.FirstRun && CaptureSettings.CleanupDays == 90, "Defaults: corner button on, Win+C, white outside, first run, 90 days");
        CaptureSettings.ShowFloatingButton = false;
        CaptureSettings.Hotkey = "Win+Alt+C";
        CaptureSettings.CleanupDays = 30;
        CaptureSettings.FloatingButtonPosition = (120, 340);
        Check(!CaptureSettings.ShowFloatingButton && CaptureSettings.Hotkey == "Win+Alt+C" && CaptureSettings.CleanupDays == 30 && CaptureSettings.FloatingButtonPosition == (120, 340), "Saved values come back");
        string? changed = null;
        Action<string> handler = name => changed = name;
        CaptureSettings.Changed += handler;
        CaptureSettings.TransparentOutside = true;
        CaptureSettings.Changed -= handler;
        Check(changed == "TransparentOutside", "A change is announced by name");
        Check(CaptureSettings.ReaderTheme is "System" or "Light" or "Dark", "The Reader's theme choice is readable");
    });

    /// <summary>Start with Windows: the Run value (a test name, never the real one) is written and removed.</summary>
    static void TestCaptureAutostart()
    {
        string priorName = Autostart.ValueName;
        Autostart.ValueName = "XTCaptureTest";
        try
        {
            Autostart.Set(false);
            Check(!Autostart.IsEnabled, "Off to start with");
            Check(Autostart.Set(true, @"C:\Apps\XTCapture.exe") && Autostart.IsEnabled, "Switched on");
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                Check(key?.GetValue("XTCaptureTest") as string == "\"C:\\Apps\\XTCapture.exe\" --background", "The program starts in the background");
            Autostart.Set(false);
            Check(!Autostart.IsEnabled, "Switched off again");
        }
        finally { Autostart.Set(false); Autostart.ValueName = priorName; }
    }

    /// <summary>The command pipe: a second start passes its command to the running one; nobody listening = false.</summary>
    static void TestCaptureInstanceChannel()
    {
        string prior = InstanceChannel.PipeName;
        InstanceChannel.PipeName = "XTCaptureTest_" + Guid.NewGuid().ToString("N");
        try
        {
            Check(!InstanceChannel.Send("capture", 200), "Nobody listening: not delivered");
            var received = new List<string>();
            var listener = InstanceChannel.Listen(command => { lock (received) received.Add(command); });
            try
            {
                Check(InstanceChannel.Send("Capture") && InstanceChannel.Send("store"), "Commands are delivered");
                for (int i = 0; i < 40; i++) { lock (received) if (received.Count >= 2) break; Thread.Sleep(50); }
                lock (received) Check(received.SequenceEqual(new[] { "capture", "store" }), "They arrive in order, lower case (" + string.Join(",", received) + ")");
                // the Reader's launcher talks to the same pipe
                CaptureLauncher.PipeName = InstanceChannel.PipeName;
                Check(CaptureLauncher.RequestExit(), "The Reader can ask a running XT Capture to quit");
                for (int i = 0; i < 40; i++) { lock (received) if (received.Count >= 3) break; Thread.Sleep(50); }
                lock (received) Check(received.Count >= 3 && received[2] == "exit", "The quit request arrives as 'exit'");
            }
            finally { listener.Cancel(); }
            Check(App.CommandFrom(new[] { "--capture" }) == "capture" && App.CommandFrom(new[] { "/background" }) == "background" && App.CommandFrom(Array.Empty<string>()) == "store", "Start arguments name the command");
        }
        finally { InstanceChannel.PipeName = prior; CaptureLauncher.PipeName = "XTCapture_Command_" + new string(Environment.UserName.Where(char.IsLetterOrDigit).ToArray()); }
    }

    /// <summary>The Reader finds XTCapture.exe (built beside it or in the sibling project).</summary>
    static void TestCaptureLauncherFindsExe()
        => Check(CaptureLauncher.FindExecutable() is { } exe && File.Exists(exe), "XTCapture.exe is found next to the Reader or in the development tree");

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
            Check(ScreenGrabber.SystemScale >= 1, "The system scale is known");
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

    /// <summary>The overlay driven like a mouse: window hover and cycling, a covered window, rectangle by drag and by two clicks, circle, polygon, handles, crosshair.</summary>
    static void TestCaptureOverlay() => WithCaptureTestSettings(() =>
    {
        if (System.Windows.Application.Current == null) CreateReaderTestApplication();
        CaptureSettings.TransparentOutside = true;

        // Window mode
        int grabbed = 0;
        var overlay = NewOverlay(w => { grabbed++; return null; });
        Check(overlay.Mode == CapturePick.Window && overlay.Region == null && !overlay.ActionBarVisible, "Starts in Window mode with nothing picked");
        overlay.OnPointerMove(new Point(150, 150));
        Check(overlay.CrosshairVisible, "The crosshair follows the mouse");
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
        Check(overlay.Region?.Bounds == new Int32Rect(50, 50, 500, 400) && overlay.ActionBarVisible && overlay.HandleCount == 8, "Clicking picks the hovered window, shows Copy / Store and its handles");
        overlay.Finish(CaptureAction.Copy);
        Check(overlay.Outcome is { Action: CaptureAction.Copy } o && o.Image.PixelWidth == 500 && o.Image.PixelHeight == 400, "A covered window is asked to paint itself, and when it cannot the frozen pixels are cut (500 x 400)");
        Check(grabbed == 1, "The covered window was asked once");

        overlay = NewOverlay(w => { grabbed++; return null; });
        overlay.OnPointerMove(new Point(150, 150));
        overlay.OnPointerDown(new Point(150, 150));
        overlay.Finish(CaptureAction.Store);
        Check(overlay.Outcome is { Action: CaptureAction.Store } e && e.Image.PixelWidth == 300 && CapturePixel(e.Image, 0, 0) is [128, 100, 100, 255], "The front window is cut from the frozen picture (not asked again)");
        Check(grabbed == 1, "An uncovered window is not asked to paint itself");

        // A picked window can be adjusted with its handles; the pixels then come from the frozen screen
        overlay = NewOverlay(w => { grabbed++; return null; });
        overlay.OnPointerMove(new Point(450, 90));
        overlay.OnPointerDown(new Point(450, 90));
        overlay.OnPointerMove(new Point(550, 450));
        Check(overlay.CurrentCursor == Cursors.SizeNWSE, "On the bottom right corner the pointer is a resize arrow");
        overlay.OnPointerDown(new Point(550, 450));
        overlay.OnPointerMove(new Point(500, 400));
        overlay.OnPointerUp(new Point(500, 400));
        Check(overlay.Region?.Bounds == new Int32Rect(50, 50, 450, 350), "Dragging the corner handle resizes the picked window");
        overlay.Finish(CaptureAction.Copy);
        Check(overlay.Outcome!.Image.PixelWidth == 450 && grabbed == 1, "The adjusted area is cut from the frozen screen, the window is not asked again");

        // Rectangle: drag, then handles and move
        overlay = NewOverlay();
        overlay.OnKey(Key.R);
        Check(overlay.Mode == CapturePick.Rectangle, "R switches to Rectangle");
        overlay.OnPointerDown(new Point(10, 10));
        overlay.OnPointerMove(new Point(60, 40));
        overlay.OnPointerMove(new Point(110, 90));
        overlay.OnPointerUp(new Point(110, 90));
        Check(overlay.Region?.Bounds == new Int32Rect(10, 10, 100, 80) && overlay.ActionBarVisible && overlay.HandleCount == 8, "Dragging picks a rectangle with eight handles");
        overlay.OnPointerMove(new Point(60, 50));
        Check(overlay.CurrentCursor == Cursors.SizeAll && !overlay.CrosshairVisible, "Inside the area the pointer says 'move'");
        overlay.OnPointerDown(new Point(60, 50));
        overlay.OnPointerMove(new Point(160, 150));
        overlay.OnPointerUp(new Point(160, 150));
        Check(overlay.Region?.Bounds == new Int32Rect(110, 110, 100, 80), "Dragging inside moves the area");
        overlay.OnPointerDown(new Point(110, 110));
        overlay.OnPointerMove(new Point(90, 100));
        overlay.OnPointerUp(new Point(90, 100));
        Check(overlay.Region?.Bounds == new Int32Rect(90, 100, 120, 90), "Dragging the top left handle resizes the area");
        overlay.OnPointerDown(new Point(600, 500));
        Check(overlay.Region == null && !overlay.ActionBarVisible && overlay.HandleCount == 0, "A click outside the area starts over");
        overlay.OnPointerUp(new Point(600, 500));
        overlay.OnKey(Key.Escape);
        overlay.OnPointerDown(new Point(10, 10));
        overlay.OnPointerMove(new Point(110, 90));
        overlay.OnPointerUp(new Point(110, 90));
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

        // Circle
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
        Check(overlay.Region is { Kind: CaptureShapeKind.Polygon } && overlay.Region.Bounds == new Int32Rect(10, 10, 100, 80) && overlay.ActionBarVisible && overlay.HandleCount == 3, "Clicking next to the first corner closes the polygon, with a handle per corner");
        overlay.OnPointerDown(new Point(60, 90));
        overlay.OnPointerMove(new Point(60, 140));
        overlay.OnPointerUp(new Point(60, 140));
        Check(overlay.Region?.Bounds == new Int32Rect(10, 10, 100, 130), "Dragging a corner reshapes the polygon");
        overlay.OnPointerDown(new Point(60, 140));
        overlay.OnPointerMove(new Point(60, 90));
        overlay.OnPointerUp(new Point(60, 90));
        overlay.Finish(CaptureAction.Copy);
        var shape = overlay.Outcome!.Image;
        Check(CapturePixel(shape, 50, 5)[3] == 255 && CapturePixel(shape, 2, 70)[3] == 0, "The polygon keeps its inside and clears the rest");

        overlay = NewOverlay();
        overlay.OnKey(Key.P);
        overlay.OnPointerDown(new Point(10, 10));
        overlay.OnPointerDown(new Point(110, 10));
        overlay.OnPointerDown(new Point(60, 90));
        overlay.OnKey(Key.Enter);
        Check(overlay.Region is { Kind: CaptureShapeKind.Polygon }, "Enter closes the polygon");
        overlay.OnPointerMove(new Point(300, 300));
        SavePng(overlay, "capture-overlay-polygon");
        overlay.Close();

        overlay = NewOverlay();
        overlay.OnKey(Key.R);
        overlay.OnPointerDown(new Point(200, 150));
        overlay.OnPointerMove(new Point(400, 330));
        overlay.OnPointerUp(new Point(400, 330));
        overlay.OnPointerMove(new Point(600, 500));
        SavePng(overlay, "capture-overlay-rectangle");
        overlay.Close();
    });

    /// <summary>The corner button: half see-through, click raises Clicked, a drag moves it and is remembered, a place off the screens falls back to the corner.</summary>
    static void TestFloatingButton() => WithCaptureTestSettings(() =>
    {
        if (System.Windows.Application.Current == null) CreateReaderTestApplication();
        var work = SystemParameters.WorkArea;
        var corner = FloatingCaptureButton.DefaultPosition();
        Check(Math.Abs(corner.X - (work.Right - FloatingCaptureButton.Size - 16)) < 0.5 && Math.Abs(corner.Y - (work.Bottom - FloatingCaptureButton.Size - 16)) < 0.5, "The default place is the bottom right corner of the work area");
        Check(FloatingCaptureButton.ResolvePosition((-1, -1)) == corner, "Nothing saved: the corner");
        Check(FloatingCaptureButton.ResolvePosition((-5000, 40)) == corner, "A saved place that is no longer on a screen: the corner");
        Check(FloatingCaptureButton.ResolvePosition((100, 120)) == new Point(100, 120), "A saved place on a screen is kept");

        var button = new FloatingCaptureButton { Left = -32000, Top = -32000 };
        button.Show();
        Pump(TimeSpan.FromMilliseconds(200));
        Check(Math.Abs(button.Opacity - 0.5) < 0.01, "It rests at about 50 % opacity");
        Check(button.Topmost && !button.ShowInTaskbar && button.Width == 48, "Always on top, not in the taskbar, 48 px");
        int clicks = 0;
        button.Clicked += () => clicks++;
        button.OnPress(new Point(100, 100));
        button.OnDrag(new Point(102, 101));
        button.OnRelease();
        Check(clicks == 1 && !button.WasDragged, "A press and release without moving is a click");
        double left = button.Left, top = button.Top;
        button.OnPress(new Point(100, 100));
        button.OnDrag(new Point(160, 140));
        button.OnRelease();
        Check(clicks == 1 && button.WasDragged && button.Left > left && button.Top > top, "A drag moves it and is not a click");
        Check(CaptureSettings.FloatingButtonPosition == ((int)Math.Round(button.Left), (int)Math.Round(button.Top)), "The new place is remembered");
        button.Close();
    });

    /// <summary>The tray program's capture flow: the picture is kept in the Store, Copy also copies it, the corner button is hidden meanwhile.</summary>
    static void TestCaptureTrayFlow() => WithCaptureTestSettings(() =>
    {
        if (System.Windows.Application.Current == null) CreateReaderTestApplication();
        string folder = Path.Combine(Output, "capture-tray");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        CaptureStore.Folder = folder;
        ToastWindow.Suppress = true;
        StoreWindow.Offscreen = true;
        CaptureSettings.ShowFloatingButton = false;
        var host = new TrayHost(showTray: false);
        var frame = new System.Windows.Threading.DispatcherFrame();
        Exception? failure = null;
        try
        {
            host.OverlayFactory = (snapshot, scale) =>
            {
                // the "user": pick a rectangle on the frozen screen and press Enter
                var overlay = new CaptureOverlayWindow(snapshot, scale, atScreen: false);
                overlay.Loaded += (_, _) => overlay.Dispatcher.BeginInvoke(new Action(() =>
                {
                    overlay.OnKey(Key.R);
                    overlay.OnPointerDown(new Point(10, 10));
                    overlay.OnPointerMove(new Point(110, 70));
                    overlay.OnPointerUp(new Point(110, 70));
                    overlay.OnKey(Key.Enter);
                }));
                return overlay;
            };
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await host.CaptureAsync();
                    var entries = CaptureStore.List();
                    Check(entries.Count == 1 && entries[0].Width == 100 && entries[0].Height == 60, "Copy keeps a 100 x 60 picture in the Store");
                    Check(ToastWindow.LastText == "Copied. Kept in the Store.", "The user is told (" + ToastWindow.LastText + ")");
                    try { Check(Clipboard.ContainsImage() && Clipboard.GetImage()?.PixelWidth == 100, "The picture is on the clipboard"); }
                    catch (System.Runtime.InteropServices.ExternalException) { /* the clipboard was busy: not what is tested here */ }
                    Check(!host.IsCapturing, "The capture is over");

                    var entry = await host.HandleAsync(new CaptureOutcome(CaptureAction.Store, CaptureTestPicture(300, 200)));
                    Check(entry != null && CaptureStore.List().Count == 2 && StoreWindow.IsOpen, "Store keeps it and opens the Store window");
                    Pump(TimeSpan.FromMilliseconds(300));
                    var window = (StoreWindow)System.Windows.Application.Current.Windows.OfType<Window>().First(w => w is StoreWindow);
                    Check(window.CardCount == 2, "The Store window shows both captures (" + window.CardCount + ")");
                    SavePng(window, "capture-store");
                    window.Close();
                }
                catch (Exception ex) { failure = ex; }
                finally { frame.Continue = false; }
            }));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            if (failure != null) throw new Exception("tray flow: " + failure.Message, failure);
        }
        finally
        {
            host.Dispose();
            ToastWindow.Suppress = false;
            StoreWindow.Offscreen = false;
            CaptureStore.Folder = CaptureStore.DefaultFolder;
        }
    });
}
