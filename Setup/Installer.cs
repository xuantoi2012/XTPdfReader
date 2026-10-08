using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;

namespace PdfReaderSetup;

/// <summary>Where things go. <see cref="Sandbox"/> redirects everything into one folder and HKCU, for testing without touching the machine.</summary>
internal sealed class Layout
{
    internal const string AppName = "PDF Reader Pro";
    internal const string ExeName = "XTPdfMergeApp.exe";
    internal const string UninstallExe = "Uninstall.exe";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PDFReaderPro";

    internal string InstallDir { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
    internal string DesktopDir { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    internal string StartMenuDir { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
    internal RegistryKey Hive { get; init; } = Registry.LocalMachine;
    internal string RegistryPath { get; init; } = UninstallKey;
    /// <summary>Where "PDFReaderPro.Document" and the .pdf OpenWithProgids entry go (HKLM\SOFTWARE\Classes).</summary>
    internal string ClassesPath { get; init; } = @"SOFTWARE\Classes";
    /// <summary>The Default apps entry: the app's capabilities and the RegisteredApplications value pointing at them.</summary>
    internal string CapabilitiesPath { get; init; } = @"SOFTWARE\XT\PDFReaderPro\Capabilities";
    internal string RegisteredAppsPath { get; init; } = @"SOFTWARE\RegisteredApplications";
    internal bool IsSandbox { get; init; }

    internal static Layout Sandbox(string root) => new()
    {
        InstallDir = Path.Combine(root, "app"),
        DesktopDir = Path.Combine(root, "Desktop"),
        StartMenuDir = Path.Combine(root, "StartMenu"),
        Hive = Registry.CurrentUser,
        RegistryPath = @"Software\PDFReaderProSetupTest\Uninstall",
        ClassesPath = @"Software\PDFReaderProSetupTest\Classes",
        CapabilitiesPath = @"Software\PDFReaderProSetupTest\XT\PDFReaderPro\Capabilities",
        RegisteredAppsPath = @"Software\PDFReaderProSetupTest\RegisteredApplications",
        IsSandbox = true
    };

    internal string ShortcutOnDesktop => Path.Combine(DesktopDir, AppName + ".lnk");
    internal string ShortcutInStartMenu => Path.Combine(StartMenuDir, AppName + ".lnk");
}

/// <summary>
/// Installs and removes the app. Every step is real work and reports the fraction of the whole job it has finished; the window may show
/// it slower than it happens (see SetupWindow), never faster.
/// </summary>
internal static class Installer
{
    private sealed record SetupInfo(string Version);

    /// <summary>Progress at which the old copy is replaced. Before it, cancelling leaves the machine untouched; after it, the install must finish.</summary>
    internal const double CommitFraction = 0.83;

    /// <summary>
    /// The new files are written to a staging folder next to the install folder first and swapped in at <see cref="CommitFraction"/>,
    /// so a cancelled or failed install never leaves a half-written app behind.
    /// </summary>
    internal static void Install(Layout layout, string installerPath, Action<double, string> report, CancellationToken cancel = default)
    {
        string staging = layout.InstallDir.TrimEnd(Path.DirectorySeparatorChar) + ".staging";
        try
        {
            report(0.0, "Đang chuẩn bị…");
            var payload = Payload.Read(installerPath) ?? throw new InvalidOperationException("Gói cài đặt không còn dữ liệu ứng dụng. Hãy tải lại bộ cài.");
            using var zipStream = new MemoryStream(payload.Zip, writable: false);
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
            string version = ReadVersion(zip);
            var files = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name) && e.FullName != "__setup.json").ToList();
            long totalBytes = Math.Max(1, files.Sum(e => e.Length));

            report(0.04, "Đang chuẩn bị thư mục cài đặt…");
            ClearDirectory(staging);
            Directory.CreateDirectory(staging);
            string root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            long done = 0;
            foreach (var entry in files)
            {
                cancel.ThrowIfCancellationRequested();
                string target = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // never write outside the install folder
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                done += entry.Length;
                report(0.08 + 0.72 * done / totalBytes, "Đang sao chép tập tin…");
            }
            if (!File.Exists(Path.Combine(staging, Layout.ExeName))) throw new FileNotFoundException("Gói cài đặt không có " + Layout.ExeName);
            Payload.CopyStub(installerPath, Path.Combine(staging, Layout.UninstallExe), payload.ExeLength);

            cancel.ThrowIfCancellationRequested();
            report(CommitFraction, "Đang đóng ứng dụng đang chạy…");
            StopRunningApp(layout.InstallDir);
            report(0.86, "Đang thay thế bản cũ…");
            ClearDirectory(layout.InstallDir);
            if (Directory.Exists(layout.InstallDir)) Directory.Delete(layout.InstallDir, recursive: true); // throws if a file is still locked: nothing half-done to hide
            Directory.Move(staging, layout.InstallDir);

            report(0.90, "Đang ghi thông tin hệ thống…");
            WriteRegistry(layout, version);
            WriteAssociations(layout, Path.Combine(layout.InstallDir, Layout.ExeName));

            report(0.95, "Đang tạo lối tắt…");
            string exe = Path.Combine(layout.InstallDir, Layout.ExeName);
            CreateShortcut(layout.ShortcutOnDesktop, exe);
            CreateShortcut(layout.ShortcutInStartMenu, exe);
            report(1.0, "Hoàn tất");
        }
        finally
        {
            ClearDirectory(staging); // nothing left behind after a cancel, an error, or (already moved) a success
        }
    }

    internal static void Uninstall(Layout layout, Action<double, string> report)
    {
        report(0.0, "Đang chuẩn bị…");
        report(0.10, "Đang đóng ứng dụng đang chạy…");
        StopRunningApp(layout.InstallDir);
        report(0.30, "Đang xóa lối tắt…");
        TryDelete(layout.ShortcutOnDesktop);
        TryDelete(layout.ShortcutInStartMenu);
        report(0.50, "Đang xóa thông tin hệ thống…");
        try { layout.Hive.DeleteSubKeyTree(layout.RegistryPath, throwOnMissingSubKey: false); } catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        RemoveAssociations(layout);
        report(0.65, "Đang xóa tập tin…");
        ClearDirectory(layout.InstallDir);
        try { Directory.Delete(layout.InstallDir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        report(1.0, "Hoàn tất");
    }

    /// <summary>The version shown in the window: the one inside the package, or (uninstall) the one registered.</summary>
    internal static string? PeekVersion(Layout layout, string installerPath, bool uninstall)
    {
        try
        {
            if (uninstall) { using var key = layout.Hive.OpenSubKey(layout.RegistryPath); return key?.GetValue("DisplayVersion") as string; }
            if (Payload.Read(installerPath) is not { } payload) return null;
            using var zip = new ZipArchive(new MemoryStream(payload.Zip, writable: false), ZipArchiveMode.Read);
            return ReadVersion(zip);
        }
        catch { return null; }
    }

    private static string ReadVersion(ZipArchive zip)
    {
        var entry = zip.GetEntry("__setup.json");
        if (entry is null) return "1.0.0";
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<SetupInfo>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.Version ?? "1.0.0";
    }

    /// <summary>Processes started from the install folder: the Reader, XT Capture, a worker. The caller disposes them.</summary>
    private static List<Process> FindRunning(string installDir)
    {
        string dir = Path.GetFullPath(installDir);
        var running = new List<Process>();
        foreach (var process in Process.GetProcesses())
        {
            string? path = null;
            try { path = process.MainModule?.FileName; } catch { /* system process, not ours */ }
            bool ours = path is not null && Path.GetFullPath(path).StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && process.Id != Environment.ProcessId;
            if (ours) running.Add(process); else process.Dispose();
        }
        return running;
    }

    /// <summary>True while something from the install folder is running (the installer asks before closing it).</summary>
    internal static bool IsRunning(string installDir)
    {
        var running = FindRunning(installDir);
        foreach (var process in running) process.Dispose();
        return running.Count > 0;
    }

    /// <summary>Gives a program that is closing itself (the Reader that started an update) time to finish.</summary>
    internal static void WaitForExit(string installDir, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && IsRunning(installDir)) Thread.Sleep(250);
    }

    /// <summary>Closes the Reader, XT Capture and any worker started from the install folder; the files must be free to replace.</summary>
    private static void StopRunningApp(string installDir)
    {
        var running = FindRunning(installDir);
        foreach (var process in running) { try { process.CloseMainWindow(); } catch { } }
        var deadline = DateTime.UtcNow.AddSeconds(5);
        foreach (var process in running)
        {
            try
            {
                if (!process.WaitForExit(Math.Max(0, (int)(deadline - DateTime.UtcNow).TotalMilliseconds))) process.Kill(entireProcessTree: true);
            }
            catch { /* already gone */ }
            process.Dispose();
        }
    }

    private static void ClearDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { Directory.Delete(dir, recursive: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(400); }
        }
        // A file is still locked (an antivirus scan, say): fall back to removing what can be removed; extraction overwrites the rest.
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) TryDelete(file);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private const string ProgId = "PDFReaderPro.Document";

    /// <summary>
    /// Makes the app a candidate for .pdf in Windows "Default apps" (ProgId, OpenWithProgids, Capabilities, RegisteredApplications).
    /// It does not take the default itself: Windows only lets the user do that; the app's settings page opens that Windows page.
    /// </summary>
    private static void WriteAssociations(Layout layout, string exe)
    {
        using (var progId = layout.Hive.CreateSubKey($@"{layout.ClassesPath}\{ProgId}"))
        {
            progId.SetValue("", "PDF document");
            progId.SetValue("FriendlyTypeName", Layout.AppName);
            using var icon = progId.CreateSubKey("DefaultIcon");
            icon.SetValue("", exe + ",0");
            using var command = progId.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{exe}\" \"%1\"");
        }
        using (var openWith = layout.Hive.CreateSubKey($@"{layout.ClassesPath}\.pdf\OpenWithProgids"))
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        using (var capabilities = layout.Hive.CreateSubKey(layout.CapabilitiesPath))
        {
            capabilities.SetValue("ApplicationName", Layout.AppName);
            capabilities.SetValue("ApplicationDescription", "Read, review and edit PDF drawing sets");
            using var associations = capabilities.CreateSubKey("FileAssociations");
            associations.SetValue(".pdf", ProgId);
        }
        using (var registered = layout.Hive.CreateSubKey(layout.RegisteredAppsPath))
            registered.SetValue(Layout.AppName, layout.CapabilitiesPath);
        NotifyAssociationsChanged(layout);
    }

    private static void RemoveAssociations(Layout layout)
    {
        try
        {
            layout.Hive.DeleteSubKeyTree($@"{layout.ClassesPath}\{ProgId}", throwOnMissingSubKey: false);
            using (var openWith = layout.Hive.OpenSubKey($@"{layout.ClassesPath}\.pdf\OpenWithProgids", writable: true)) openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
            layout.Hive.DeleteSubKeyTree(layout.CapabilitiesPath, throwOnMissingSubKey: false);
            using (var registered = layout.Hive.OpenSubKey(layout.RegisteredAppsPath, writable: true)) registered?.DeleteValue(Layout.AppName, throwOnMissingValue: false);
            // The parent key "XT\PDFReaderPro" stays only if something else lives there.
            using (var parent = layout.Hive.OpenSubKey(layout.CapabilitiesPath.Replace(@"\Capabilities", ""), writable: false))
                if (parent is { SubKeyCount: 0, ValueCount: 0 }) layout.Hive.DeleteSubKey(layout.CapabilitiesPath.Replace(@"\Capabilities", ""), throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        NotifyAssociationsChanged(layout);
    }

    private static void NotifyAssociationsChanged(Layout layout)
    {
        if (!layout.IsSandbox) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void WriteRegistry(Layout layout, string version)
    {
        string exe = Path.Combine(layout.InstallDir, Layout.ExeName);
        string uninstall = Path.Combine(layout.InstallDir, Layout.UninstallExe);
        using var key = layout.Hive.CreateSubKey(layout.RegistryPath);
        key.SetValue("DisplayName", Layout.AppName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "XT");
        key.SetValue("InstallLocation", layout.InstallDir);
        key.SetValue("DisplayIcon", exe);
        key.SetValue("UninstallString", $"\"{uninstall}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{uninstall}\" --uninstall --silent");
        key.SetValue("EstimatedSize", (int)(DirectorySize(layout.InstallDir) / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
    }

    private static long DirectorySize(string dir)
        => Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;

    private static void CreateShortcut(string linkPath, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Không tạo được lối tắt (WScript.Shell).");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(linkPath);
        link.TargetPath = target;
        link.WorkingDirectory = Path.GetDirectoryName(target);
        link.IconLocation = target + ",0";
        link.Description = Layout.AppName;
        link.Save();
    }
}
