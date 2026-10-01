using System;
using System.Threading.Tasks;
using System.Windows;
using Velopack;
using Velopack.Sources;
using XTStyle.Controls;

namespace XTPdfMergeApp.Services
{
    /// <summary>Tự kiểm tra bản mới qua GitHub Releases của 1 repo PUBLIC riêng chỉ chứa bản cài (không phải repo
    /// code — xem Packaging/README.md để biết cách publish lên đó). Im lặng hoàn toàn khi: chạy từ Visual
    /// Studio/debug (UpdateManager.IsInstalled = false, vì chưa cài qua Velopack nên không có gì để so), không có
    /// mạng, hoặc repo release chưa có bản nào — không bao giờ làm phiền hay chặn khởi động vì việc này.
    ///
    /// Không ép khởi động lại giữa chừng: tải ngầm bản mới rồi CHỈ áp dụng ở LẦN MỞ KẾ TIẾP
    /// (WaitExitThenApplyUpdatesAsync silent/restart=false) — giống Chrome/VS Code, không phải kiểu Windows Update
    /// bắt đóng app ngay.</summary>
    public static class AppUpdateService
    {
        // Repo PUBLIC riêng chỉ chứa Release + file cài (khác repo code, repo code có thể để private) — xem
        // Packaging/README.md. Đổi URL này nếu dùng tên repo khác.
        private const string ReleasesRepoUrl = "https://github.com/xuantoi2012/PDFReaderPro-Releases";

        public static async Task CheckInBackgroundAsync()
        {
            try
            {
                var mgr = new UpdateManager(new GithubSource(ReleasesRepoUrl, null, false));
                if (!mgr.IsInstalled) return; // chạy từ source/debug — không có bản cài Velopack nào để so

                var info = await mgr.CheckForUpdatesAsync();
                if (info == null) return; // đã là bản mới nhất

                await mgr.DownloadUpdatesAsync(info, null);
                await mgr.WaitExitThenApplyUpdatesAsync(info.TargetFullRelease, silent: true, restart: false, restartArgs: null);

                string version = info.TargetFullRelease.Version?.ToString() ?? "mới";
                Application.Current?.Dispatcher.BeginInvoke(() =>
                    XTGrowl.Show($"Đã tải bản {version} — sẽ dùng bản mới ở lần mở sau.",
                        XTGrowlType.Info, title: "Có cập nhật mới", durationSeconds: 6));
            }
            catch
            {
                // Mạng lỗi/chưa có release nào/v.v. — im lặng, tự thử lại ở lần khởi động sau.
            }
        }
    }
}
