using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp
{
    /// <summary>Ký số văn bản (một người ký, chứng thư trên USB token hoặc file .pfx) và kiểm tra chữ ký của file đang mở.</summary>
    public partial class ReaderWindow
    {
        private void ReaderSign_Click(object sender, RoutedEventArgs e) => OpenSignPanel();

        /// <summary>Which saved single-file document the Sign / Marks panels work on; null (after telling the user) when there is none.</summary>
        private async Task<string?> RequireSavedPdfAsync(string title)
        {
            var group = _readerGroup;
            string? path = group?.SourcePath;
            if (group == null || EditHost == null || string.IsNullOrWhiteSpace(path) || _readerPage == null) { Growl.Info("Open a PDF first.", this); return null; }
            if (!File.Exists(path) || group.Pages.Select(p => p.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            {
                AppDialog.Show(this, "Hãy lưu văn bản thành một tệp PDF trước.", title, MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            if (group.IsDirty)
            {
                var answer = AppDialog.Show(this, $"\"{group.FileName}\" có thay đổi chưa lưu. Lưu trước? (Bản mới được tạo từ nội dung đã lưu.)", title,
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes || !await EditHost.SaveGroupAsync(group, saveAs: false)) return null;
            }
            return path;
        }

        internal async void OpenSignPanel()
        {
            if (_toolPanelKind == "sign") { CloseToolPanel(); return; }
            string? path = await RequireSavedPdfAsync("Ký số");
            if (path == null || _readerPage == null) return;
            int count = PageCountOf(path);
            if (count < 1) { AppDialog.Show(this, "The file could not be read.", "Ký số", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            var surface = EnsureAreaSurface();
            surface.PlaceMode = false;
            surface.BoundPath = path;
            surface.Activate();
            var panel = new SignPanel(surface, path, _readerPage.PageNumber, count);
            panel.Applied += request => { CloseToolPanel(); _ = RunSignAsync(request); };
            ShowToolPanel("sign", "Ký số", panel, panel.Detach, 400);
            _ = panel.StartAsync();
        }

        private async Task RunSignAsync(DigitalSignRequest request)
        {
            var previous = Mouse.OverrideCursor;
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                await PdfDigitalSignService.SignAsync(request);
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = previous;
                AppDialog.Show(this, "Không ký được:\n" + ex.Message + "\n\nNếu cửa sổ nhập PIN của token đã bị đóng hoặc nhập sai, hãy thử ký lại.",
                    "Ký số", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally { Mouse.OverrideCursor = previous; }

            Growl.Success("Đã ký số", this);
            await Session.OpenFilesInReaderAsync(new[] { request.OutputPath });
        }

        private async void ReaderVerifySignatures_Click(object sender, RoutedEventArgs e)
        {
            string? path = _readerGroup?.SourcePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            try
            {
                var checks = await PdfDigitalSignService.CheckAsync(path);
                if (checks.Count == 0)
                {
                    AppDialog.Show(this, "Văn bản này chưa có chữ ký số.", "Kiểm tra chữ ký", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var sb = new StringBuilder();
                foreach (var c in checks)
                {
                    sb.AppendLine($"• {c.Signer}{(c.Organization.Length > 0 ? " — " + c.Organization : "")}");
                    sb.AppendLine($"   Thời gian ký: {(c.SignedAt?.ToString("dd/MM/yyyy HH:mm:ss") ?? "không rõ")}");
                    sb.AppendLine($"   Nội dung: {(c.IntegrityOk ? (c.CoversWholeFile ? "nguyên vẹn, chưa bị sửa" : "chữ ký đúng, nhưng sau đó văn bản có thêm nội dung (ví dụ chữ ký khác)") : "ĐÃ BỊ SỬA hoặc chữ ký không hợp lệ")}");
                    sb.AppendLine($"   Chứng thư: {(c.ChainTrusted ? "tin cậy trên máy này" : "chưa tin cậy trên máy này")}");
                    if (c.Note.Length > 0) sb.AppendLine("   " + c.Note);
                    sb.AppendLine();
                }
                AppDialog.Show(this, sb.ToString().TrimEnd(), "Kiểm tra chữ ký số", MessageBoxButton.OK,
                    checks.All(c => c.IntegrityOk) ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                AppDialog.Show(this, "Không kiểm tra được chữ ký:\n" + ex.Message, "Kiểm tra chữ ký", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ReaderPageMarks_Click(object sender, RoutedEventArgs e) => OpenMarksPanel();

        internal async void OpenMarksPanel()
        {
            if (_toolPanelKind == "marks") { CloseToolPanel(); return; }
            string? path = await RequireSavedPdfAsync("Dấu chữ, đầu/chân trang");
            if (path == null || _readerPage == null) return;
            int count = PageCountOf(path);
            if (count < 1) { AppDialog.Show(this, "The file could not be read.", "Dấu chữ, đầu/chân trang", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            var surface = EnsureAreaSurface();
            surface.PlaceMode = false;
            surface.BoundPath = path;
            surface.Activate();
            var panel = new PageMarksPanel(surface, path, _readerPage.PageNumber, count);
            panel.Applied += job => { CloseToolPanel(); _ = RunMarksAsync(job); };
            ShowToolPanel("marks", "Dấu chữ, đầu/chân trang, che nội dung", panel, panel.Detach, 420);
            _ = panel.StartAsync();
        }

        private async Task RunMarksAsync(PageMarkJob job)
        {
            var previous = Mouse.OverrideCursor;
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                await PdfPageMarkService.ApplyAsync(job);
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = previous;
                AppDialog.Show(this, "Không xử lý được:\n" + ex.Message, "Dấu chữ, đầu/chân trang", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally { Mouse.OverrideCursor = previous; }
            Growl.Success("Đã tạo bản mới", this);
            await Session.OpenFilesInReaderAsync(new[] { job.OutputPath });
        }
    }
}
