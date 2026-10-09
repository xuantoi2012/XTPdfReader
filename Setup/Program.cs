using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace PdfReaderSetup;

internal sealed class Options
{
    internal Layout Layout { get; set; } = new();
    internal string InstallerPath { get; set; } = Environment.ProcessPath ?? "";
    internal bool Uninstall { get; set; }
    internal bool Silent { get; set; }
    internal bool Launch { get; set; }
    /// <summary>An update started by the Reader: wait for that process to be closed by the user before installing (never close it for them).</summary>
    internal int WaitPid { get; set; }
    /// <summary>The window never finishes sooner than this (see SetupWindow).</summary>
    internal double Seconds { get; set; } = 15;
}

internal static class Program
{
    /// <summary>
    /// <c>PDFReaderPro-Setup.exe</c>                install (window, ~15 s)
    /// <c>--silent</c> no window · <c>--launch</c> start the Reader afterwards · <c>--seconds N</c> minimum duration
    /// <c>--uninstall</c> remove (<c>Uninstall.exe</c> in the install folder is this same program) · <c>--sandbox DIR</c> test run in DIR
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        var options = new Options();
        string? uninstallRun = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--silent": options.Silent = true; break;
                case "--launch": options.Launch = true; break;
                case "--uninstall": options.Uninstall = true; options.Seconds = 8; break;
                case "--wait-pid" when i + 1 < args.Length && int.TryParse(args[i + 1], out var pid): options.WaitPid = pid; i++; break;
                case "--seconds" when i + 1 < args.Length && double.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out var s):
                    options.Seconds = Math.Max(0.1, s); i++; break;
                case "--sandbox" when i + 1 < args.Length: options.Layout = Layout.Sandbox(Path.GetFullPath(args[++i])); break;
                case "--uninstall-run" when i + 1 < args.Length: uninstallRun = args[++i]; options.Uninstall = true; options.Seconds = 8; break;
            }
        }

        // An uninstaller cannot delete the folder it runs from: start a copy from %TEMP% and let that one do the work.
        string self = Environment.ProcessPath ?? "";
        if (options.Uninstall && uninstallRun is null && IsInside(self, options.Layout.InstallDir))
        {
            string copy = Path.Combine(Path.GetTempPath(), "PDFReaderPro-Uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(self, copy, overwrite: true);
            var start = new ProcessStartInfo(copy) { UseShellExecute = false };
            start.ArgumentList.Add("--uninstall-run");
            start.ArgumentList.Add(options.Layout.InstallDir);
            foreach (var a in args.Where(a => a != "--uninstall")) start.ArgumentList.Add(a);
            Process.Start(start);
            return 0;
        }

        if (options.WaitPid > 0)
        {
            try { using var reader = Process.GetProcessById(options.WaitPid); reader.WaitForExit(); }
            catch (ArgumentException) { /* already gone */ }
            Installer.WaitForExit(options.Layout.InstallDir, TimeSpan.FromSeconds(8)); // XT Capture and workers follow the Reader out
        }

        int code = options.Silent ? RunSilent(options) : RunWindow(options);
        if (uninstallRun is not null) ScheduleSelfDelete(self);
        return code;
    }

    private static int RunSilent(Options options)
    {
        try
        {
            if (options.Uninstall) Installer.Uninstall(options.Layout, (_, _) => { });
            else
            {
                Installer.Install(options.Layout, options.InstallerPath, (_, _) => { });
                if (options.Launch)
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.Combine(options.Layout.InstallDir, Layout.ExeName)}\"") { UseShellExecute = true });
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunWindow(Options options)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PDFReaderPro-Setup;component/Styles.xaml") });
        app.Resources["Green"] = AccentBrush(); // the name is old: the colour is the accent chosen in the Reader's settings
        return app.Run(new SetupWindow(options));
    }

    /// <summary>The accent of the Reader's settings (same registry value), as a soft left-to-right gradient; the orange of the brand when nothing is set.</summary>
    private static System.Windows.Media.Brush AccentBrush()
    {
        string name = "Orange";
        try { using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\XTStyle\XTPdfMergeApp"); name = key?.GetValue("Accent") as string ?? name; } catch { }
        string hex = name switch { "Blue" => "#2563EB", "Green" => "#0F8B6D", "Purple" => "#7C4DFF", "Red" => "#D9463B", _ => "#E8590C" };
        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        System.Windows.Media.Color Mix(System.Windows.Media.Color a, double t) => System.Windows.Media.Color.FromRgb((byte)(a.R + (255 - a.R) * t), (byte)(a.G + (255 - a.G) * t), (byte)(a.B + (255 - a.B) * t));
        var brush = new System.Windows.Media.LinearGradientBrush(Mix(c, 0.28), c, 0);
        brush.Freeze();
        return brush;
    }

    private static bool IsInside(string path, string folder)
        => path.Length > 0 && Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void ScheduleSelfDelete(string self)
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 4 >nul & del /f /q \"{self}\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch { /* a leftover temp file */ }
    }
}
