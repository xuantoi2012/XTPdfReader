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
        private IReadOnlyList<(string SourcePath, int PageNumber)> _pages;
        private readonly int _currentIndex;
        private int _previewPosition;
        private int _previewVersion;
        private bool _ready;
        private PrintBySizePanel? _routing;
        private string _routingKey = "";
        private IReadOnlyList<PageSizeGroup> _routingGroups = Array.Empty<PageSizeGroup>();
        private readonly List<(CheckBox Box, PageSizeGroup Group)> _sizeChecks = new();
        private readonly Dictionary<string, Dictionary<int, double>> _colorShare = new(StringComparer.OrdinalIgnoreCase); // per file: page -> percent of the page that is colored
        private HashSet<int> _colorDefault = new();   // 0-based positions of the pages that carry color pictures
        private HashSet<int> _colorSet = new();       // the pages that print in color (the default, as the user edited it)
        private bool _advanced;
        private bool _colorRequested;
        private byte[]? _topDevMode;   // the driver settings chosen with the Properties button of the top row
        private readonly Dictionary<string, (double W, double H)> _sizes = new();
        private List<(double Width, double Height)> _pagePoints = new();   // khổ mọi trang của file (point), theo thứ tự trang
        private List<PaperOption> _papers = new();                          // khổ giấy máy in đang chọn
        private IReadOnlyList<PageSizeGroup> _groups = Array.Empty<PageSizeGroup>();

        /// <summary>
        /// <paramref name="prepared"/>: the same pages read from a working copy that carries the unsaved edits, still being made. The window opens at once (the copy of a big file
        /// takes a while); printing and the preview wait for it.
        /// </summary>
        internal PrintWindow(IReadOnlyList<(string SourcePath, int PageNumber)> pages, int currentIndex, Task<List<(string SourcePath, int PageNumber)>>? prepared = null)
        {
            _pages = pages;
            _currentIndex = Math.Clamp(currentIndex, 0, Math.Max(0, pages.Count - 1));
            InitializeComponent();

            PagesAll.Content = Loc.T($"All pages ({pages.Count})");
            PagesCurrent.Content = Loc.T($"Current page ({_currentIndex + 1})");
            RangeTotal.Text = "/ " + pages.Count;
            RangeBox.Text = pages.Count > 1 ? "1-" + pages.Count : "1";
            PagesAll.IsChecked = true;

            foreach (string printer in PrinterSettings.InstalledPrinters) PrinterBox.Items.Add(printer);
            string def = new PrinterSettings().PrinterName;
            PrinterBox.SelectedItem = PrinterBox.Items.Contains(def) ? def : PrinterBox.Items.Count > 0 ? PrinterBox.Items[0] : null;
            RefreshTopCollate(true);
            if (PrinterBox.Items.Count == 0)
            {
                PrintButton.IsEnabled = false;
                SummaryText.Text = Loc.T("No printer is installed.");
            }
            LayerNote.Text = pages.Select(p => p.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Any(p => PdfLayerStateStore.GetToken(p).Length > 0)
                ? "Hidden layers are not printed (the current layer view is used)." : "";
            Routing();
            ProfileHost.Child = Routing().ProfileBar;
            Routing().ProfileBar.Margin = new Thickness(0);
            _ready = true;
            bool waiting = prepared is { IsCompleted: false };
            string layerNote = LayerNote.Text;
            if (waiting)
            {
                PrintButton.IsEnabled = false;
                LayerNote.Text = Loc.T("Preparing a copy with your unsaved edits… (progress in the status bar)");
            }
            Loaded += async (_, _) =>
            {
                UpdateSummary();
                if (prepared != null)
                {
                    try { _pages = await prepared; }
                    catch (Exception ex)
                    {
                        AppDialog.Show(this, "Could not prepare the PDF for printing:\n" + ex.Message, "Print", MessageBoxButton.OK, MessageBoxImage.Error);
                        Close();
                        return;
                    }
                    LayerNote.Text = layerNote;
                    if (PrinterBox.Items.Count > 0) PrintButton.IsEnabled = true;
                    UpdateSummary();
                }
                _ = RefreshPreviewAsync();
                _ = LoadPageSizesAsync();
            };
        }

        private List<int>? SelectedIndices()
        {
            List<int>? list;
            if (PagesCurrent.IsChecked == true) list = new List<int> { _currentIndex };
            else if (PagesBySize.IsChecked == true) list = _sizeChecks.Where(c => c.Box.IsChecked == true).SelectMany(c => c.Group.PageIndexes).Distinct().OrderBy(i => i).ToList();
            else if (PagesColor.IsChecked == true) list = _colorSet.Where(i => i < _pages.Count).OrderBy(i => i).ToList();
            else if (PagesRange.IsChecked == true) list = PdfPrintService.ParseRange(RangeBox.Text, _pages.Count)?.Select(n => n - 1).ToList();
            else list = Enumerable.Range(0, _pages.Count).ToList();
            if (list == null) return null;
            // Subset is by page NUMBER in the document (1-based).
            if (SubsetBox.SelectedIndex == 1) list = list.Where(i => (i + 1) % 2 == 1).ToList();
            else if (SubsetBox.SelectedIndex == 2) list = list.Where(i => (i + 1) % 2 == 0).ToList();
            if (ReverseBox.IsChecked == true) list.Reverse();
            return list;
        }

        private PrintScale Scale => ScaleActual.IsChecked == true ? PrintScale.ActualSize : ScaleCustom.IsChecked == true ? PrintScale.Custom
            : ScaleReduce.IsChecked == true ? PrintScale.ReduceToPaper : PrintScale.FitToPaper;
        private PrintColor Color => LinesBox.IsChecked == true ? PrintColor.BlackLines : GrayBox.IsChecked == true ? PrintColor.Grayscale : PrintColor.Color;
        private PrintQuality Quality => HighQualityBox.IsChecked == true ? PrintQuality.CadHigh : ImageBox.IsChecked == true ? PrintQuality.Fast : PrintQuality.Standard;
        private PrintOrientation Orientation => OrientBox.SelectedIndex switch { 1 => PrintOrientation.Portrait, 2 => PrintOrientation.Landscape, _ => PrintOrientation.Auto };
        private int Percent => int.TryParse(PercentBox.Text, out int p) ? Math.Clamp(p, 5, 1000) : 100;

        // ── Cài đặt ───────────────────────────────────────────────────

        private void Printer_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (PrinterBox.SelectedItem is not string name) return;
            _topDevMode = null;
            RefreshTopCollate(true);
            var settings = new PrinterSettings { PrinterName = name };
            if (!settings.IsValid) return;
            _papers = settings.PaperSizes.Cast<PaperSize>()
                .Select(p => new PaperOption(p.PaperName, p.Width / 100.0 * 25.4, p.Height / 100.0 * 25.4)).ToList();
            RefreshSizes();
            ApplySimple();
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
            var built = PrintSizePlan.Build(_pagePoints, _papers);
            bool changed = _groups.Count != built.Count || _groups.Zip(built).Any(z => z.First.Name != z.Second.Name || !z.First.PageIndexes.SequenceEqual(z.Second.PageIndexes));
            _groups = built;
            SizesNote.Text = string.Join("  ·  ", _groups.Select(g => PrintSizePlan.Label(g)));
            SizeButton.IsEnabled = true;
            if (changed) FillSizeChecks();
            PagesBySize.IsEnabled = true;
            RefreshRouting();
        }

        /// <summary>One tick box for each paper size of the file, under "Pages by paper size".</summary>
        private void FillSizeChecks()
        {
            var ticked = _sizeChecks.Where(c => c.Box.IsChecked != true).Select(c => c.Group.Name).ToHashSet();
            _sizeChecks.Clear();
            SizeChecks.Children.Clear();
            foreach (var group in _groups)
            {
                var box = new CheckBox { Content = $"{group.Name}  ({group.Dims})  ×  {group.Count}", IsChecked = !ticked.Contains(group.Name), Margin = new Thickness(0, 0, 0, 6) };
                box.Checked += SizeCheck_Changed;
                box.Unchecked += SizeCheck_Changed;
                _sizeChecks.Add((box, group));
                SizeChecks.Children.Add(box);
            }
            UpdateSizeButton();
        }

        private void UpdateSizeButton()
        {
            var names = _sizeChecks.Where(c => c.Box.IsChecked == true).Select(c => c.Group.Name).ToList();
            SizeButton.Text = names.Count == 0 ? Loc.T("None") : names.Count == _sizeChecks.Count ? Loc.T("All sizes") : string.Join(", ", names);
        }

        private void SizeButton_Click(object sender, RoutedEventArgs e) => SizePopup.IsOpen = !SizePopup.IsOpen;

        private void SizeCheck_Changed(object sender, RoutedEventArgs e)
        {
            UpdateSizeButton();
            if (!_ready) return;
            if (PagesBySize.IsChecked != true) PagesBySize.IsChecked = true; else Setting_Changed(sender, e);
        }

        // ── Color pages ───────────────────────────────────────────────

        /// <summary>The pages that carry color pictures are the quick pick of the pages that print in color (read in the background; the box can be edited).</summary>
        private async Task LoadColorPagesAsync()
        {
            if (_colorRequested) return;
            _colorRequested = true;
            var paths = _pages.Select(p => p.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            ColorStat.Text = Loc.T("Looking for color pages…");
            for (int k = 0; k < paths.Count; k++)
            {
                var progress = new Progress<(int Done, int Total)>(p => ColorStat.Text = Loc.T($"Looking for color pages… {p.Done} of {p.Total}"));
                _colorShare[paths[k]] = await PdfColorPages.ShareAsync(paths[k], progress);
            }
            int tenths = AppSettings.ColorPageTenths;
            _colorDefault = Enumerable.Range(0, _pages.Count).Where(i => _colorShare.TryGetValue(_pages[i].SourcePath, out var share) && share.TryGetValue(_pages[i].PageNumber, out double f) && f * 10 >= tenths).ToHashSet();
            ColorPagesBox.Text = PrintSizePlan.PageList(_colorDefault.ToList()); // fires ColorPages_TextChanged
            UpdateColorStat();
            RefreshRouting();
        }

        /// <summary>Looking for color pages takes a few seconds on a big set: it starts when the user asks for the color pages or opens Advanced.</summary>
        private void PagesColor_Checked(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            _ = LoadColorPagesAsync();
            Setting_Changed(sender, e);
        }

        private void ColorPages_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready) return;
            if (PagesColor.IsChecked != true && ColorPagesBox.IsKeyboardFocusWithin) PagesColor.IsChecked = true;
            UpdateColorStat();
            RefreshRouting();
            UpdateSummary();
            _ = RefreshPreviewAsync();
        }

        private void ColorReset_Click(object sender, RoutedEventArgs e) => ColorPagesBox.Text = PrintSizePlan.PageList(_colorDefault.ToList());

        private void UpdateColorStat()
        {
            var parsed = PdfPrintService.ParseRange(ColorPagesBox.Text, _pages.Count);
            if (parsed == null) { ColorStat.Text = Loc.T($"The pages must be like 1-3, 7 (this file has {_pages.Count})"); return; }
            _colorSet = parsed.Select(n => n - 1).ToHashSet();
            ColorStat.Text = _colorSet.Count == 0 ? Loc.T("No page prints in color.") : Loc.T($"{_colorSet.Count} of {_pages.Count} pages print in color");
        }

        /// <summary>The groups the cards show: the sizes of the pages that print, each split into its black-and-white pages and its color pages.</summary>
        private List<PageSizeGroup> BuildRoutingGroups()
        {
            var set = new HashSet<int>(SelectedIndices() ?? new List<int>());
            var result = new List<PageSizeGroup>();
            foreach (var group in _groups)
            {
                var pages = group.PageIndexes.Where(set.Contains).ToList();
                var color = pages.Where(_colorSet.Contains).ToList();
                var mono = pages.Where(i => !_colorSet.Contains(i)).ToList();
                if (mono.Count > 0) result.Add(group with { PageIndexes = mono, Color = false });
                if (color.Count > 0) result.Add(group with { PageIndexes = color, Color = true });
            }
            return result;
        }

        /// <summary>In from every card: each kind of sheet on its own printer and paper (see <see cref="PrintBySizePanel"/>); each card has its own grayscale; Black lines only applies to every card.</summary>
        private async Task PrintByRoutingAsync()
        {
            var selected = SelectedIndices();
            if (selected is not { Count: > 0 } || _groups.Count == 0) return;
            ApplySimple();
            var jobsToPrint = Routing().TryBuildJobs(this);
            if (jobsToPrint == null) return;

            PrintButton.IsEnabled = false;
            int done = 0, failed = 0;
            foreach (var job in jobsToPrint)
            {
                SummaryText.Text = Loc.T($"Printing {PrintSizePlan.Label(job.Group)} on {job.Printer}…");
                // this card's own driver settings (tray, quality…) with its collate, copies and paper written in
                bool collate = job.Collate == true && PrinterDriver.SupportsCollate(job.Printer) && PrinterDriver.HonorsCollate(job.Printer);
                var devMode = PrinterDriver.WithSettings(job.Printer, job.DevMode, job.Collate is null ? null : collate, job.Copies, job.Paper) ?? job.DevMode;
                var request = new PrintRequest(job.Group.PageIndexes.Select(i => _pages[i]).ToList(), job.Printer, job.Paper, job.Copies, Scale, Percent, LinesBox.IsChecked == true ? PrintColor.BlackLines : job.Gray ? PrintColor.Grayscale : PrintColor.Color, Quality, Orientation,
                    AutoCenterBox.IsChecked == true, collate, devMode);
                try { if (await PdfPrintService.PrintAsync(request, new Progress<int>(_ => { }))) done++; else failed++; }
                catch { failed++; }
            }
            if (failed > 0)
            {
                AppDialog.Show(this, $"{done} size group(s) were sent to the printers; {failed} could not be printed (printer not available or an error).", "Print by size", MessageBoxButton.OK, MessageBoxImage.Warning);
                PrintButton.IsEnabled = true;
                UpdateSummary();
                return;
            }
            DialogResult = true;
        }

        private PrintBySizePanel Routing()
        {
            if (_routing != null) return _routing;
            _routing = new PrintBySizePanel(PrinterBox.SelectedItem as string ?? "");
            _routing.Changed += () =>
            {
                if (!_ready) return;
                // the Copies / Collate boxes read the printer of the first card
                if (_advanced && _routing!.FirstPrinter() is { } first && !Equals(PrinterBox.SelectedItem, first) && PrinterBox.Items.Contains(first)) PrinterBox.SelectedItem = first;
                LayoutPaper(); // the paper chosen for a card is the paper of the preview of its pages
                _ = RefreshPreviewAsync();
            };
            BySizeHost.Child = _routing;
            return _routing;
        }

        /// <summary>The cards follow the print range and the color pages: one for each size (and color) of the pages that will print.</summary>
        private void RefreshRouting()
        {
            if (_routing == null || _groups.Count == 0) return;
            var groups = BuildRoutingGroups();
            string key = string.Join("|", groups.Select(g => PrintSizePlan.Key(g) + ":" + string.Join(",", g.PageIndexes)));
            if (key == _routingKey) return;
            _routingKey = key;
            _routingGroups = groups;
            _routing.SetGroups(groups);
            ApplySimple();
        }

        /// <summary>The paper the page will print on: the one chosen on its card; until a printer is chosen, a sheet as big as the page.</summary>
        private PaperSize? PaperForPage(int pageIndex)
        {
            if (_routing != null && _routingGroups.FirstOrDefault(g => g.PageIndexes.Contains(pageIndex)) is { } group && _routing.PaperOf(PrintSizePlan.Key(group)) is { } routed) return routed;
            var (w, h) = pageIndex < _pagePoints.Count ? _pagePoints[pageIndex] : (595.0, 842.0);
            double a = Math.Min(w, h) / 72 * 100, b = Math.Max(w, h) / 72 * 100;
            return new PaperSize("page", (int)Math.Round(a), (int)Math.Round(b));
        }

        private void Setting_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            RangeBox.IsEnabled = PagesRange.IsChecked == true;
            SizeChecks.Visibility = PagesBySize.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PercentBox.IsEnabled = ScaleCustom.IsChecked == true;
            _previewPosition = 0;
            UpdateSummary();
            _ = RefreshPreviewAsync();
        }

        private void Subset_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) Setting_Changed(sender, e); }

        /// <summary>The page prints in gray: the box on top (simple), or the grayscale box of its card (Advanced).</summary>
        private bool GrayOfPage(int pageIndex)
        {
            if (!_advanced) return GrayBox.IsChecked == true;
            return _routing != null && _routingGroups.FirstOrDefault(g => g.PageIndexes.Contains(pageIndex)) is { } group && _routing.GrayOf(PrintSizePlan.Key(group));
        }

        private void ColorBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            if (ReferenceEquals(sender, LinesBox) && LinesBox.IsChecked == true) GrayBox.IsChecked = false;
            else if (ReferenceEquals(sender, GrayBox) && GrayBox.IsChecked == true) LinesBox.IsChecked = false;
            LinesBox2.IsChecked = LinesBox.IsChecked;
            ApplySimple();
            _ = RefreshPreviewAsync();
        }

        private void ImageBox_Changed(object sender, RoutedEventArgs e)
        {
            if (ImageBox.IsChecked == true) HighQualityBox.IsChecked = false;
            ImageBox2.IsChecked = ImageBox.IsChecked;
        }

        private void HighQuality_Changed(object sender, RoutedEventArgs e)
        {
            if (HighQualityBox.IsChecked == true) { ImageBox.IsChecked = false; ImageBox2.IsChecked = false; }
            Setting_Changed(sender, e);
        }

        /// <summary>Black lines only and Print as image are in the top row and in Print Handling (the top row is hidden in Advanced): the two copies stay the same.</summary>
        private void Mirror_Changed(object sender, RoutedEventArgs e)
        {
            LinesBox.IsChecked = LinesBox2.IsChecked;
            ImageBox.IsChecked = ImageBox2.IsChecked;
            ColorBox_Changed(LinesBox, e);
            ImageBox_Changed(ImageBox, e);
        }

        // ── Top row (simple dialog) and Advanced ──────────────────────

        private int TopCopies => int.TryParse(CopiesBox.Text, out int c) ? Math.Clamp(c, 1, 99) : 1;

        /// <summary>Collate follows the printer's driver: the box is off when the driver cannot take it from us.</summary>
        private void RefreshTopCollate(bool fromPrinterDefault)
        {
            if (PrinterBox.SelectedItem is not string printer) { CollateBox.IsEnabled = false; return; }
            bool can = PrinterDriver.SupportsCollate(printer);
            CollateBox.IsEnabled = can && PrinterDriver.HonorsCollate(printer);
            CollateBox.ToolTip = !can ? Loc.T("This printer's driver cannot collate copies") : CollateBox.IsEnabled ? Loc.T("Same as the Collate setting of the printer's Properties")
                : Loc.T("This driver keeps its own Collate setting: change it in Properties… (the box shows what the driver has)");
            if (!fromPrinterDefault) return;
            try { CollateBox.IsChecked = can && new PrinterSettings { PrinterName = printer }.Collate; }
            catch { CollateBox.IsChecked = false; }
        }

        private void TopSetting_Changed(object sender, RoutedEventArgs e)
        {
            CopiesBox.Text = TopCopies.ToString();
            ApplySimple();
        }

        private void PrinterProps_Click(object sender, RoutedEventArgs e)
        {
            if (PrinterBox.SelectedItem is not string printer) return;
            var start = PrinterDriver.WithSettings(printer, _topDevMode, CollateBox.IsEnabled ? CollateBox.IsChecked == true : null, TopCopies, null) ?? _topDevMode;
            var devMode = PrinterDriver.ShowDialog(new System.Windows.Interop.WindowInteropHelper(this).Handle, printer, start);
            if (devMode == null) return;
            _topDevMode = devMode;
            var (_, copies, collate) = PrinterDriver.Read(printer, devMode);
            CopiesBox.Text = Math.Clamp((int)copies, 1, 99).ToString();
            if (PrinterDriver.SupportsCollate(printer)) CollateBox.IsChecked = collate;
            ApplySimple();
        }

        /// <summary>The simple dialog prints every size on the printer of the top row with its copies, collate and grayscale; Advanced is where each size has its own.</summary>
        private void ApplySimple()
        {
            if (_advanced || !_ready || _routing == null || PrinterBox.SelectedItem is not string printer) return;
            _routing.ApplyToAll(printer, _topDevMode, GrayBox.IsChecked == true, TopCopies, CollateBox.IsEnabled ? CollateBox.IsChecked == true : null);
        }

        private void Advanced_Click(object sender, RoutedEventArgs e) => SetAdvanced(!_advanced);

        private void SetAdvanced(bool on)
        {
            if (on == _advanced) return;
            if (!on) { _advanced = false; ApplySimple(); }
            _advanced = on;
            AdvancedButton.Tag = on ? "Active" : null;
            TopBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            ProfileHost.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            Col3Host.Visibility = Splitter3.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            SplitCol.Width = new GridLength(on ? 8 : 0);
            Col3.MinWidth = on ? 340 : 0;
            Col3.Width = on ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            if (WindowState == WindowState.Normal)
            {
                double delta = on ? 400 : -400;
                MinWidth = on ? 1000 : 700;
                Width = Math.Max(MinWidth, Width + delta);
            }
            if (on) { _ = LoadColorPagesAsync(); RefreshRouting(); }
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
            SummaryText.Text = indices == null ? Loc.T("Check the page range (for example 1-12, 40, 55-60).")
                : indices.Count == 0 ? Loc.T("No pages selected.") : "";
            PrintButton.IsEnabled = indices is { Count: > 0 } && PrinterBox.Items.Count > 0;
            if (_groups.Count > 0) RefreshRouting(); // the pages to print changed: the cards follow
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
            PreviewImage.Source = bitmap == null ? null : !(LinesBox.IsChecked == true || GrayOfPage(pageIndex)) ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0);
            PreviewCaption.Text = Loc.T($"Page {_previewPosition + 1} of {indices.Count}");
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
            if (indices == null || indices.Count == 0) return;
            int pageIndex = indices[Math.Clamp(_previewPosition, 0, indices.Count - 1)];
            var paper = PaperForPage(pageIndex);
            if (paper == null) return;
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

            ZoomText.Text = Loc.T($"{scale * 100:0.##}%");
            DocText.Text = Loc.T($"{pageW / 100:0.0} x {pageH / 100:0.0} inch");
            PaperText.Text = Loc.T($"{pw / 100:0.0} x {ph / 100:0.0} inch");
        }

        // ── Nút ───────────────────────────────────────────────────────

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private async void Print_Click(object sender, RoutedEventArgs e) => await PrintByRoutingAsync();
    }
}
