using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Windows;

namespace XTPdfMergeApp.Services
{
    /// <summary>
    /// The Reader does not capture the screen itself: XT Capture (XTCapture.exe, installed beside the Reader) does, and keeps running in the tray.
    /// This starts it with a command ("capture", "store", "settings"); a copy that is already running takes the command from the new start.
    /// </summary>
    internal static class CaptureLauncher
    {
        /// <summary>The pipe XT Capture listens on (one per Windows user; keep in step with XTCapture/InstanceChannel.cs).</summary>
        internal static string PipeName { get; set; } = "XTCapture_Command_" + new string(Environment.UserName.Where(char.IsLetterOrDigit).ToArray());

        /// <summary>XTCapture.exe next to the Reader; while developing, the one the sibling project last built.</summary>
        internal static string? FindExecutable()
        {
            string beside = Path.Combine(AppContext.BaseDirectory, "XTCapture.exe");
            if (File.Exists(beside)) return beside;
            try
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (int up = 0; up < 8 && dir != null; up++, dir = dir.Parent)
                {
                    string project = Path.Combine(dir.FullName, "XTCapture", "bin");
                    if (!Directory.Exists(project)) continue;
                    return Directory.EnumerateFiles(project, "XTCapture.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                }
            }
            catch { /* no development tree */ }
            return null;
        }

        /// <summary>Starts XT Capture with <paramref name="command"/>; tells the user when it is not installed.</summary>
        internal static bool Run(string command, Window? owner = null)
        {
            string? exe = FindExecutable();
            if (exe == null)
            {
                if (owner != null) Controls.AppDialog.Show(owner, "XT Capture was not found next to the Reader. Reinstall PDF Reader Pro to get it.", "XT Capture", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            try
            {
                Process.Start(new ProcessStartInfo(exe, "--" + command) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
                return true;
            }
            catch (Exception ex)
            {
                if (owner != null) Controls.AppDialog.Show(owner, "XT Capture could not start: " + ex.Message, "XT Capture", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        /// <summary>Asks a running XT Capture to quit (before an update replaces its files). False when none is running.</summary>
        internal static bool RequestExit(int timeoutMs = 800)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(timeoutMs);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine("exit");
                return true;
            }
            catch { return false; }
        }
    }
}
