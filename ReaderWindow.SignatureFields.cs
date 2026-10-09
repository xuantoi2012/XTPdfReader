using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp
{
    /// <summary>Digital signature fields on the page (tinted boxes, click for the details) and the status bar at the top, like Foxit.</summary>
    public partial class ReaderWindow
    {
        private IReadOnlyList<SignatureCheck> _signatureChecks = Array.Empty<SignatureCheck>();
        private string _signatureBannerPath = "";
        private readonly HashSet<string> _signatureBannerHidden = new(StringComparer.OrdinalIgnoreCase);
        private bool _signatureHooked;

        private static readonly Brush ToneGreen = Brushes.SeaGreen;
        private static readonly Brush ToneAmber = Brushes.DarkOrange;
        private static readonly Brush ToneRed = Brushes.IndianRed;

        /// <summary>Called when a document becomes the one in view: reads its signature fields, marks them and shows the status bar.</summary>
        private async void RefreshSignatureFields(string path)
        {
            if (!_signatureHooked)
            {
                _signatureHooked = true;
                PdfSignatureFieldService.Changed += _ => Dispatcher.BeginInvoke(() => ReaderContinuousView.Redraw());
            }
            SignatureBanner.Visibility = Visibility.Collapsed;
            _signatureChecks = Array.Empty<SignatureCheck>();
            _signatureBannerPath = path;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            try
            {
                await PdfSignatureFieldService.LoadAsync(path);
                var fields = PdfSignatureFieldService.All(path);
                if (!string.Equals(_signatureBannerPath, path, StringComparison.OrdinalIgnoreCase)) return; // another file came into view
                if (fields.Count == 0) return;
                if (!fields.Any(f => f.Signed))
                {
                    ShowSignatureBanner(path, "Văn bản có ô ký số chưa ký", ToneAmber);
                    return;
                }
                var checks = await PdfDigitalSignService.CheckAsync(path);
                if (!string.Equals(_signatureBannerPath, path, StringComparison.OrdinalIgnoreCase)) return;
                _signatureChecks = checks;
                if (checks.Count == 0) return;
                bool allIntact = checks.All(c => c.IntegrityOk);
                bool whole = checks.Last().CoversWholeFile;
                bool trusted = checks.All(c => c.ChainTrusted);
                if (!allIntact) ShowSignatureBanner(path, "Chữ ký số KHÔNG hợp lệ: văn bản đã bị sửa sau khi ký", ToneRed);
                else if (!whole) ShowSignatureBanner(path, $"Có {checks.Count} chữ ký số; văn bản có thêm nội dung sau lần ký cuối", ToneAmber);
                else if (!trusted) ShowSignatureBanner(path, $"Đã ký số ({checks.Count}); văn bản nguyên vẹn nhưng chứng thư chưa được tin cậy trên máy này", ToneAmber);
                else ShowSignatureBanner(path, checks.Count == 1 ? "Đã ký số, chữ ký hợp lệ" : $"Đã ký số, {checks.Count} chữ ký đều hợp lệ", ToneGreen);
            }
            catch { /* the bar is a convenience: never block opening a file */ }
        }

        private void ShowSignatureBanner(string path, string text, Brush tone)
        {
            if (_signatureBannerHidden.Contains(path)) return;
            SignatureBannerDot.Fill = tone;
            SignatureBannerText.Text = text;
            SignatureBanner.Visibility = Visibility.Visible;
        }

        private void SignatureBannerClose_Click(object sender, RoutedEventArgs e)
        {
            SignatureBanner.Visibility = Visibility.Collapsed;
            if (_signatureBannerPath.Length > 0) _signatureBannerHidden.Add(_signatureBannerPath);
        }

        private void SignatureBannerDetails_Click(object sender, RoutedEventArgs e) => ReaderVerifySignatures_Click(sender, e);

        /// <summary>The signature field under the pointer, if any.</summary>
        private SignatureField? SignatureFieldAt(PageHit hit)
            => PdfSignatureFieldService.Of(hit.Row.SourcePath, hit.Row.PageNumber).FirstOrDefault(f => f.Contains(hit.U, hit.V));

        /// <summary>A click on a signature field: a signed one opens its details, an empty one tells how to sign.</summary>
        private bool OpenSignatureField(PageHit hit)
        {
            if (SignatureFieldAt(hit) is not { } field) return false;
            if (!field.Signed)
            {
                Growl.Info("Ô ký số chưa có chữ ký. Dùng nút Ký số để ký văn bản.", this);
                return true;
            }
            _ = ShowSignatureDetailsAsync(hit.Row.SourcePath, field);
            return true;
        }

        private async Task ShowSignatureDetailsAsync(string path, SignatureField field)
        {
            var checks = _signatureChecks.Count > 0 && string.Equals(_signatureBannerPath, path, StringComparison.OrdinalIgnoreCase)
                ? _signatureChecks : await PdfDigitalSignService.CheckAsync(path);
            var check = checks.FirstOrDefault(c => c.Field == field.Name);
            if (check == null)
            {
                Growl.Info("Không đọc được thông tin chữ ký này.", this);
                return;
            }
            SignatureInfoWindow.ShowFor(this, check, checks.Count);
        }
    }
}
