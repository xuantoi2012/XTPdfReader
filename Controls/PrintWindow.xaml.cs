using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XTPdfMergeApp.Services;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls
{
    /// <summary>Print dialog laid out like Foxit's (printer + driver Properties, range/subset, handling, paper/orientation, preview). Rare options (booklet, tiling, bleed marks) are left out.</summary>
    public partial class PrintWindow : XTWindow
    {
        private readonly IReadOnlyList<(string SourcePath, int PageNumber)> _pages;
        private readonly int _currentIndex;
        private int _previewPosition;
        private int _previewVersion;
        private bool _ready;
        private byte[]? _devMode;
        private readonly Dictionary<string, (double W, double H)> _sizes = new();
        private List<(double Width, double Height)> _pagePoints = new();   // khổ mọi trang của file (point), theo thứ tự trang
        private List<PaperOption> _papers = new();                          // khổ giấy máy in đang chọn
        private IReadOnlyList<PageSizeGroup> _groups = Array.Empty<PageSizeGroup>();

        internal PrintWindow(IReadOnlyList<(string SourcePath, int PageNumber)> pages, int currentIndex)
        {
            _pages = pages;
            _currentIndex = Math.Clamp(currentIndex, 0, Math.Max(0, pages.Count - 1));
            InitializeComponent();

            PagesAll.Content = $"All pages ({pages.Count})";
            PagesCurrent.Content = $"Current page ({_currentIndex + 1})";
            RangeTotal.Text = "/ " + pages.Count;
            PagesAll.IsChecked = true;

            foreach (string printer in PrinterSettings.InstalledPrinters) PrinterBox.Items.Add(printer);
            string def = new PrinterSettings().PrinterName;
            PrinterBox.SelectedItem = PrinterBox.Items.Contains(def) ? def : PrinterBox.Items.Count > 0 ? PrinterBox.Items[0] : null;
            if (PrinterBox.Items.Count == 0)
            {
                PrintButton.IsEnabled = false;
                PropertiesButton.IsEnabled = false;
                SummaryText.Text = "No printer is installed.";
            }
            LayerNote.Text = pages.Select(p => p.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Any(p => PdfLayerStateStore.GetToken(p).Length > 0)
                ? "Hidden layers are not printed (the current layer view is used)." : "";
            _ready = true;
            Loaded += (_, _) => { UpdateSummary(); _ = RefreshPreviewAsync(); _ = LoadPageSizesAsync(); };
        }

        private List<int>? SelectedIndices()
        {
            List<int>? list;
            if (PagesCurrent.IsChecked == true) list = new List<int> { _currentIndex };
            else if (PagesRange.IsChecked == true) list = PdfPrintService.ParseRange(RangeBox.Text, _pages.Count)?.Select(n => n - 1).ToList();
            else list = Enumerable.Range(0, _pages.Count).ToList();
            if (list == null) return null;
            // Subset is by page NUMBER in the document (1-based).
            if (SubsetBox.SelectedIndex == 1) list = list.Where(i => (i + 1) % 2 == 1).ToList();
            else if (SubsetBox.SelectedIndex == 2) list = list.Where(i => (i + 1) % 2 == 0).ToList();
            if (ReverseBox.IsChecked == true) list.Reverse();
            return list;
        }

        private PaperSize? SelectedPaper => (PaperBox.SelectedItem as ComboBoxItem)?.Tag as PaperSize;
        private PrintScale Scale => ScaleActual.IsChecked == true ? PrintScale.ActualSize : ScaleCustom.IsChecked == true ? PrintScale.Custom
            : ScaleReduce.IsChecked == true ? PrintScale.ReduceToPaper : PrintScale.FitToPaper;
        private PrintColor Color => LinesBox.IsChecked == true ? PrintColor.BlackLines : GrayBox.IsChecked == true ? PrintColor.Grayscale : PrintColor.Color;
        private PrintQuality Quality => HighQualityBox.IsChecked == true ? PrintQuality.CadHigh : PrintQuality.Standard;
        private PrintOrientation Orientation => OrientBox.SelectedIndex switch { 1 => PrintOrientation.Portrait, 2 => PrintOrientation.Landscape, _ => PrintOrientation.Auto };
        private int Percent => int.TryParse(PercentBox.Text, out int p) ? Math.Clamp(p, 5, 1000) : 100;

        // ── Cài đặt ───────────────────────────────────────────────────

        private void Printer_Changed(object sender, SelectionChangedEventArgs e)
        {
            PaperBox.Items.Clear();
            _devMode = null; // driver settings belong to one printer
            if (PrinterBox.SelectedItem is not string name) return;
            var settings = new PrinterSettings { PrinterName = name };
            PrinterStatus.Text = settings.IsValid ? (settings.IsDefaultPrinter ? "Default printer" : "") : "Printer not available";
            if (!settings.IsValid) return;
            ComboBoxItem? selected = null;
            string defaultPaper = settings.DefaultPageSettings.PaperSize.PaperName;
            foreach (PaperSize paper in settings.PaperSizes)
            {
                double w = paper.Width / 100.0 * 25.4, h = paper.Height / 100.0 * 25.4;
                var item = new ComboBoxItem { Content = $"{paper.PaperName} ({w:0} × {h:0} mm)", Tag = paper };
                PaperBox.Items.Add(item);
                if (paper.PaperName == defaultPaper) selected = item;
            }
            PaperBox.SelectedItem = selected ?? (PaperBox.Items.Count > 0 ? PaperBox.Items[0] : null);
            _papers = settings.PaperSizes.Cast<PaperSize>()
                .Select(p => new PaperOption(p.PaperName, p.Width / 100.0 * 25.4, p.Height / 100.0 * 25.4)).ToList();
            RefreshSizes();
        }

        // ── Khổ giấy của các trang so với máy in ──────────────────────

        /// <summary>Đọc khổ mọi trang của file (nền), rồi đối chiếu với khổ giấy máy in.</summary>
        private async Task LoadPageSizesAsync()
        {
            var bySource = new Dictionary<string, (double Width, double Height)[]?>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in _pages.Select(p => p.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try { bySource[path] = await PdfThumbnailService.GetPageSizesAsync(path); }
                catch { bySource[path] = null; }
            }
            _pagePoints = _pages.Select(p =>
            {
                var sizes = bySource.GetValueOrDefault(p.SourcePath);
                return sizes != null && p.PageNumber - 1 < sizes.Length && sizes[p.PageNumber - 1].Width > 0 ? sizes[p.PageNumber - 1] : (595.0, 842.0);
            }).ToList();
            RefreshSizes();
        }

        private void RefreshSizes()
        {
            if (_pagePoints.Count == 0) return;
            _groups = PrintSizePlan.Build(_pagePoints, _papers);
            SizesNote.Text = string.Join("  ·  ", _groups.Select(g => PrintSizePlan.Label(g)));
            SizesButton.IsEnabled = true;
            var warn = Mismatches();
            SizesWarning.Visibility = warn.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SizesWarning.Text = warn.Count == 0 ? "" : "⚠ " + string.Join("\n⚠ ", warn.Select(g =>
                PrintSizePlan.Label(g) + ": " + PrintSizePlan.Status(g)));
        }

        /// <summary>Nhóm khổ có trang đang được chọn in mà máy in không có đúng khổ giấy đó.</summary>
        private List<PageSizeGroup> Mismatches()
        {
            var selected = SelectedIndices();
            if (selected == null || _groups.Count == 0) return new List<PageSizeGroup>();
            var set = new HashSet<int>(selected);
            return _groups.Where(g => g.Fit != SizeFit.Exact && g.PageIndexes.Any(set.Contains)).ToList();
        }

        /// <summary>In từng khổ giấy trên máy in riêng, mỗi nhóm tự lấy đúng khổ giấy của máy in đó (xem <see cref="PrintRoutingWindow"/>).</summary>
        private async Task PrintByRoutingAsync()
        {
            var selected = SelectedIndices();
            if (selected is not { Count: > 0 } || _groups.Count == 0) return;
            var set = new HashSet<int>(selected);
            var groups = _groups.Select(g => g with { PageIndexes = g.PageIndexes.Where(set.Contains).ToList() }).Where(g => g.PageIndexes.Count > 0).ToList();
            var dialog = new PrintRoutingWindow(groups, PrinterBox.SelectedItem as string ?? "") { Owner = this };
            if (dialog.ShowDialog() != true) return;

            int copies = int.TryParse(CopiesBox.Text, out int c) ? Math.Clamp(c, 1, 99) : 1;
            PrintButton.IsEnabled = false;
            SizesButton.IsEnabled = false;
            int done = 0, failed = 0;
            foreach (var job in dialog.Jobs)
            {
                SummaryText.Text = $"Printing {PrintSizePlan.Label(job.Group)} on {job.Printer}…";
                var request = new PrintRequest(job.Group.PageIndexes.Select(i => _pages[i]).ToList(), job.Printer, job.Paper, copies, Scale, Percent, Color, Quality, Orientation,
                    AutoCenterBox.IsChecked == true, CollateBox.IsChecked == true, null);
                try { if (await PdfPrintService.PrintAsync(request, new Progress<int>(_ => { }))) done++; else failed++; }
                catch { failed++; }
            }
            if (failed > 0)
            {
                AppDialog.Show(this, $"{done} size group(s) were sent to the printers; {failed} could not be printed (printer not available or an error).", "Print by size", MessageBoxButton.OK, MessageBoxImage.Warning);
                PrintButton.IsEnabled = true;
                SizesButton.IsEnabled = true;
                UpdateSummary();
                return;
            }
            DialogResult = true;
        }

        private void Sizes_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = SizesButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var group in _groups)
            {
                var item = new MenuItem
                {
                    Header = $"{(group.Fit == SizeFit.Exact ? "✓" : "⚠")} {PrintSizePlan.Label(group)} — {PrintSizePlan.Status(group)}",
                    ToolTip = "Print only these pages: " + PrintSizePlan.PageList(group.PageIndexes)
                };
                var pages = group.PageIndexes;
                item.Click += (_, _) =>
                {
                    PagesRange.IsChecked = true;
                    RangeBox.Text = PrintSizePlan.PageList(pages);
                };
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            var route = new MenuItem { Header = "Print each size on its own printer / paper…" };
            route.Click += async (_, _) => await PrintByRoutingAsync();
            menu.Items.Add(route);
            var copy = new MenuItem { Header = "Copy size report" };
            copy.Click += (_, _) => { try { Clipboard.SetText(PrintSizePlan.Report(_groups, PrinterBox.SelectedItem as string ?? "")); } catch { } };
            menu.Items.Add(copy);
            menu.IsOpen = true;
        }

        private void Properties_Click(object sender, RoutedEventArgs e)
        {
            if (PrinterBox.SelectedItem is not string name) return;
            var devMode = PrinterDriver.ShowDialog(new System.Windows.Interop.WindowInteropHelper(this).Handle, name, _devMode);
            if (devMode == null) return;
            _devMode = devMode;
            try
            {
                var (paper, copies) = PrinterDriver.Read(name, devMode);
                if (paper != null)
                    foreach (ComboBoxItem item in PaperBox.Items)
                        if ((item.Tag as PaperSize)?.PaperName == paper.PaperName) { PaperBox.SelectedItem = item; break; }
                if (copies > 1) CopiesBox.Text = copies.ToString();
            }
            catch { /* the driver settings are still applied when printing */ }
        }

        private void Paper_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) LayoutPaper(); }

        private void Setting_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            RangeBox.IsEnabled = PagesRange.IsChecked == true;
            PercentBox.IsEnabled = ScaleCustom.IsChecked == true;
            _previewPosition = 0;
            UpdateSummary();
            _ = RefreshPreviewAsync();
        }

        private void Subset_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) Setting_Changed(sender, e); }

        private void ColorBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            _ready = false; // Black lines and grayscale exclude each other.
            if (ReferenceEquals(sender, LinesBox) && LinesBox.IsChecked == true) GrayBox.IsChecked = false;
            else if (ReferenceEquals(sender, GrayBox) && GrayBox.IsChecked == true) LinesBox.IsChecked = false;
            _ready = true;
            _ = RefreshPreviewAsync();
        }

        private void Range_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready) return;
            _previewPosition = 0;
            UpdateSummary();
            _ = RefreshPreviewAsync();
        }

        private void Percent_TextChanged(object sender, TextChangedEventArgs e) { if (_ready) LayoutPaper(); }

        private void UpdateSummary()
        {
            var indices = SelectedIndices();
            string quality = Quality == PrintQuality.CadHigh ? " · CAD high quality (600 DPI)" : " · Standard (300 DPI)";
            SummaryText.Text = indices == null ? "Check the page range (for example 1-12, 40, 55-60)."
                : indices.Count == 0 ? "No pages selected." : $"{indices.Count} page{(indices.Count == 1 ? "" : "s")} will be printed{quality}";
            PrintButton.IsEnabled = indices is { Count: > 0 } && PrinterBox.Items.Count > 0;
            if (_groups.Count > 0) RefreshSizes(); // chọn trang đổi → cảnh báo khổ giấy theo các trang sẽ in
        }

        // ── Xem trước ─────────────────────────────────────────────────

        private void PrevPreview_Click(object sender, RoutedEventArgs e) => StepPreview(-1);
        private void NextPreview_Click(object sender, RoutedEventArgs e) => StepPreview(1);

        private void StepPreview(int delta)
        {
            var indices = SelectedIndices();
            if (indices == null || indices.Count == 0) return;
            _previewPosition = (_previewPosition + delta + indices.Count) % indices.Count;
            _ = RefreshPreviewAsync();
        }

        private void PreviewHost_SizeChanged(object sender, SizeChangedEventArgs e) { if (_ready) LayoutPaper(); }

        private async Task RefreshPreviewAsync()
        {
            int version = ++_previewVersion;
            var indices = SelectedIndices();
            if (indices == null || indices.Count == 0) { PreviewImage.Source = null; PreviewCaption.Text = ""; return; }
            _previewPosition = Math.Clamp(_previewPosition, 0, indices.Count - 1);
            int pageIndex = indices[_previewPosition];
            var (path, number) = _pages[pageIndex];
            var bitmap = await PdfThumbnailService.RenderPageAsync(path, number - 1, 900, layerToken: PdfLayerStateStore.GetToken(path), withAnnotations: true);
            var sizes = await PdfThumbnailService.GetPageSizesAsync(path);
            if (version != _previewVersion) return;
            _sizes[path + "|" + number] = sizes != null && number - 1 < sizes.Length && sizes[number - 1].Width > 0 ? sizes[number - 1] : (595, 842);
            PreviewImage.Source = bitmap == null ? null : Color == PrintColor.Color ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0);
            PreviewCaption.Text = $"Page {_previewPosition + 1} of {indices.Count}";
            LayoutPaper();
        }

        private bool IsLandscape(int pageIndex)
        {
            if (Orientation != PrintOrientation.Auto) return Orientation == PrintOrientation.Landscape;
            var (path, number) = _pages[pageIndex];
            return _sizes.TryGetValue(path + "|" + number, out var s) && s.W > s.H;
        }

        /// <summary>Vẽ tờ giấy đúng tỉ lệ khổ giấy đã chọn và đặt ảnh trang theo tỉ lệ in.</summary>
        private void LayoutPaper()
        {
            var indices = SelectedIndices();
            var paper = SelectedPaper;
            if (paper == null || indices == null || indices.Count == 0) return;
            int pageIndex = indices[Math.Clamp(_previewPosition, 0, indices.Count - 1)];
            bool landscape = IsLandscape(pageIndex);
            double pw = landscape ? paper.Height : paper.Width, ph = landscape ? paper.Width : paper.Height;
            double maxW = Math.Max(50, PreviewHost.ActualWidth - 20), maxH = Math.Max(50, PreviewHost.ActualHeight - 20);
            double k = Math.Min(maxW / pw, maxH / ph);
            Paper.Width = pw * k;
            Paper.Height = ph * k;
            Paper.ClipToBounds = true;

            var (path, number) = _pages[pageIndex];
            if (!_sizes.TryGetValue(path + "|" + number, out var size)) size = (595, 842);
            double pageW = size.W / 72 * 100, pageH = size.H / 72 * 100; // trăm inch
            double fit = Math.Min(pw / pageW, ph / pageH);
            double scale = Scale switch
            {
                PrintScale.ActualSize => 1.0,
                PrintScale.Custom => Percent / 100.0,
                PrintScale.ReduceToPaper => Math.Min(1.0, fit),
                _ => fit
            };
            PreviewImage.Width = pageW * scale * k;
            PreviewImage.Height = pageH * scale * k;
            PreviewImage.HorizontalAlignment = AutoCenterBox.IsChecked == true ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            PreviewImage.VerticalAlignment = AutoCenterBox.IsChecked == true ? VerticalAlignment.Center : VerticalAlignment.Top;

            ZoomText.Text = $"{scale * 100:0.##}%";
            DocText.Text = $"{pageW / 100:0.0} x {pageH / 100:0.0} inch";
            PaperText.Text = $"{pw / 100:0.0} x {ph / 100:0.0} inch";
        }

        // ── Nút ───────────────────────────────────────────────────────

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private async void Print_Click(object sender, RoutedEventArgs e)
        {
            var indices = SelectedIndices();
            var paper = SelectedPaper;
            if (indices is not { Count: > 0 } || paper == null || PrinterBox.SelectedItem is not string printer) return;
            var mismatches = Mismatches();
            if (mismatches.Count > 0 && AppDialog.Show(this,
                    "This printer does not have the exact paper for some of the pages:\n\n" +
                    string.Join("\n", mismatches.Select(g => $"• {PrintSizePlan.Label(g)}: {PrintSizePlan.Status(g)} (pages {PrintSizePlan.PageList(g.PageIndexes)})")) +
                    "\n\nPrint anyway?", "Print", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            int copies = int.TryParse(CopiesBox.Text, out int c) ? Math.Clamp(c, 1, 99) : 1;
            var request = new PrintRequest(indices.Select(i => _pages[i]).ToList(), printer, paper, copies, Scale, Percent, Color, Quality, Orientation,
                AutoCenterBox.IsChecked == true, CollateBox.IsChecked == true, _devMode);

            PrintButton.IsEnabled = false;
            bool ok;
            try
            {
                var progress = new Progress<int>(n => SummaryText.Text = $"Printing page {n} of {indices.Count}…");
                ok = await PdfPrintService.PrintAsync(request, progress);
            }
            catch (Exception ex)
            {
                AppDialog.Show(this, "Printing failed:\n" + ex.Message, "Print", MessageBoxButton.OK, MessageBoxImage.Error);
                PrintButton.IsEnabled = true;
                return;
            }
            if (!ok)
            {
                AppDialog.Show(this, "The selected printer is not available.", "Print", MessageBoxButton.OK, MessageBoxImage.Warning);
                PrintButton.IsEnabled = true;
                return;
            }
            DialogResult = true;
        }
    }
}
