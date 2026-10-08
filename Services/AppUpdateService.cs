using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using XTPdfMergeApp.Controls;

namespace XTPdfMergeApp.Services
{
    /// <summary>A newer release on GitHub: nothing is downloaded until the user asks for it.</summary>
    internal sealed record UpdateInfo(string Version, string DownloadUrl, long Size, string Notes);

    /// <summary>
    /// Looks for a newer release on GitHub (a PUBLIC repo that holds only the installers, not the code) when the app starts. When there is one,
    /// the title bar shows an "Update" button and a small window offers it once per start. Only "Cập nhật ngay" downloads
    /// <c>PDFReaderPro-Setup.exe</c> and starts it (it asks for administrator rights, because the app lives in Program Files); the app then
    /// exits and the setup reopens it. "Để sau" does nothing: the next start offers it again. Silent when running from a development
    /// folder, offline, or when no release exists.
    /// </summary>
    public static class AppUpdateService
    {
        private const string DefaultReleaseUrl = "https://api.github.com/repos/xuantoi2012/PDFReaderPro-Releases/releases/latest";

        /// <summary>Development only: XTPDF_UPDATE_FEED points the check at another URL (a local test server); the setup is then started without the admin prompt and with XTPDF_UPDATE_SETUPARGS appended.</summary>
        private static string? TestFeed => Environment.GetEnvironmentVariable("XTPDF_UPDATE_FEED");
        private static string LatestReleaseUrl => TestFeed ?? DefaultReleaseUrl;
        private const string AssetName = "PDFReaderPro-Setup.exe";

        /// <summary>The newer release found at start-up, or null.</summary>
        internal static UpdateInfo? Available { get; private set; }

        /// <summary>Raised on the UI thread when <see cref="Available"/> changes.</summary>
        internal static event Action? AvailableChanged;

        /// <summary>Only an installed copy (under Program Files) updates itself; a build run from the source folder never does.</summary>
        internal static bool IsInstalled
        {
            get
            {
                string baseDir = Path.GetFullPath(AppContext.BaseDirectory);
                return baseDir.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(baseDir, "Uninstall.exe"));
            }
        }

        /// <summary>Asks GitHub for the latest release. Sets <see cref="Available"/>; true when a newer version exists.</summary>
        public static async Task<bool> CheckAsync()
        {
            try
            {
                if (!IsInstalled && TestFeed is null) return false;
                using var http = NewClient(TimeSpan.FromSeconds(20));
                using var release = JsonDocument.Parse(await http.GetStringAsync(LatestReleaseUrl));
                var root = release.RootElement;
                string tag = root.GetProperty("tag_name").GetString() ?? "";
                if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || !Version.TryParse(AppInfo.Version, out var current) || latest <= current)
                    return Publish(null);

                string? url = null; long size = 0;
                foreach (var asset in root.GetProperty("assets").EnumerateArray())
                    if (asset.GetProperty("name").GetString() == AssetName)
                    {
                        url = asset.GetProperty("browser_download_url").GetString();
                        size = asset.GetProperty("size").GetInt64();
                    }
                if (url is null || !(url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || (TestFeed is not null && url.StartsWith("http://127.0.0.1", StringComparison.Ordinal)))) return Publish(null);
                string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
                return Publish(new UpdateInfo(latest.ToString(3), url, size, notes));
            }
            catch
            {
                return false; // offline, rate limited, no release yet: try again at the next start
            }
        }

        private static bool Publish(UpdateInfo? info)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                Available = info;
                AvailableChanged?.Invoke();
            });
            return info is not null;
        }

        /// <summary>Checks, and on a newer version offers it once (the title bar button stays for the rest of the session).</summary>
        public static async Task CheckInBackgroundAsync()
        {
            if (!await CheckAsync()) return;
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (Application.Current.MainWindow is { IsVisible: true } owner && Available is { } info)
                    UpdateReadyWindow.ShowFor(owner, info);
            });
        }

        /// <summary>Downloads the setup of <paramref name="info"/> to a temp folder (progress 0..1). A finished earlier download of the same size is reused.</summary>
        internal static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double> progress, CancellationToken token)
        {
            string folder = Path.Combine(Path.GetTempPath(), "PDFReaderPro-Update");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, $"PDFReaderPro-Setup-{info.Version}.exe");
            if (File.Exists(file) && new FileInfo(file).Length == info.Size) { progress.Report(1); return file; }

            string partial = file + ".part";
            using var http = NewClient(TimeSpan.FromMinutes(10));
            using var response = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? info.Size;
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var target = File.Create(partial))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), token);
                    done += read;
                    if (total > 0) progress.Report(Math.Min(1.0, (double)done / total));
                }
            }
            if (info.Size > 0 && new FileInfo(partial).Length != info.Size) { File.Delete(partial); throw new IOException("Tập tin tải về không đầy đủ."); }
            File.Move(partial, file, overwrite: true);
            return file;
        }

        /// <summary>Starts the downloaded setup with administrator rights and reopening the app afterwards. False when the user declined the prompt.</summary>
        internal static bool StartSetup(string setupPath)
        {
            try
            {
                // XT Capture (the tray program beside the Reader) would keep its files locked while the setup replaces them.
                CaptureLauncher.RequestExit();
                var start = new ProcessStartInfo(setupPath, "--launch") { UseShellExecute = true, Verb = "runas" };
                if (TestFeed is not null)
                {
                    start.Arguments += " " + Environment.GetEnvironmentVariable("XTPDF_UPDATE_SETUPARGS");
                    start.Verb = "";
                }
                Process.Start(start);
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false; // UAC prompt declined
            }
        }

        private static HttpClient NewClient(TimeSpan timeout)
        {
            var http = new HttpClient { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PDFReaderPro-Updater");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return http;
        }
    }
}
