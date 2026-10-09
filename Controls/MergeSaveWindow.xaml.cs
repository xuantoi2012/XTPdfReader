using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using XTPdfMergeApp.Services;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls
{
    public sealed record SourceRow(string Name, string Pages, string Range);

    /// <summary>1 dòng của danh sách "giữ riêng": tên layer rút gọn, số file nguồn có layer này, có giữ riêng không.</summary>
    public sealed class KeepLayerItem : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _keep;
        public KeepLayerItem(string name, int files, bool keep) { Name = name; Files = files; _keep = keep; }
        public string Name { get; }
        public int Files { get; }
        public string FilesText => Files == 1 ? "1 file" : Files + " files";
        public bool Keep
        {
            get => _keep;
            set { if (_keep == value) return; _keep = value; PropertyChanged?.Invoke(this, new(nameof(Keep))); }
        }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed record OutlineItem(string Title, int Page, IReadOnlyList<OutlineItem> Children);

    /// <summary>Hộp thoại "Save merged file" (docs/UI_REDESIGN.md, mockup 17): tên file, thư mục, các file nguồn, tuỳ chọn và bản xem trước bookmark của kết quả.</summary>
    public partial class MergeSaveWindow : XTWindow
    {
        private readonly IReadOnlyList<(string SourcePath, int PageNumber)> _pages;
        private int _previewVersion;
        private bool _ready;
        private List<KeepLayerItem> _keepItems = new();
        private HashSet<string> _savedKeep = new(StringComparer.Ordinal);
        private int _layersAll, _layersDistinct;
        private List<string> _rawLayerNames = new();
        private Dictionary<string, string> _layerRenames = new(StringComparer.Ordinal);

        /// <summary>Đường dẫn file kết quả (sau khi bấm Save).</summary>
        public string OutputPath { get; private set; } = "";
        public MergeOptions Options { get; private set; } = MergeOptions.Default;

        internal MergeSaveWindow(IReadOnlyList<(string SourcePath, int PageNumber)> pages, string defaultFolder)
        {
            _pages = pages;
            InitializeComponent();

            NameBox.Text = Loc.T("Merged.pdf");
            FolderBox.Text = defaultFolder;
            var saved = AppSettings.MergeOptionsSaved;
            FileBookmarksBox.IsChecked = saved.FileBookmarks;
            KeepBookmarksBox.IsChecked = saved.KeepBookmarks;
            _savedKeep = new HashSet<string>(saved.KeepLayers ?? Array.Empty<string>(), StringComparer.Ordinal);
            CollapseNameBox.Text = saved.CollapseLayerName;
            (saved.LayerMode switch
            {
                MergeLayerMode.Separate => LayersSeparateRadio,
                MergeLayerMode.KeepSome => LayersKeepSomeRadio,
                _ => LayersByNameRadio
            }).IsChecked = true;
            KeepSomePanel.Visibility = saved.LayerMode == MergeLayerMode.KeepSome ? Visibility.Visible : Visibility.Collapsed;
            PageNumbersBox.IsChecked = saved.PageNumbers;
            OptimizeBox.IsChecked = saved.Optimize;

            SourceList.ItemsSource = BuildSourceRows();
            _ready = true;
            Loaded += (_, _) =>
            {
                NameBox.Focus();
                NameBox.Select(0, Path.GetFileNameWithoutExtension(NameBox.Text).Length);
                _ = AnalyzeAsync();
                RefreshPreview();
            };
        }

        private MergeOptions CurrentOptions()
        {
            bool keepSome = LayersKeepSomeRadio.IsChecked == true;
            string collapse = CollapseNameBox.Text.Trim();
            return new(FileBookmarksBox.IsChecked == true, KeepBookmarksBox.IsChecked == true, LayersSeparateRadio.IsChecked != true,
                       PageNumbersBox.IsChecked == true, OptimizeBox.IsChecked == true,
                       keepSome ? _keepItems.Where(i => i.Keep).Select(i => i.Name).ToList() : null,
                       collapse.Length > 0 ? collapse : MergeOptions.DefaultCollapseName,
                       "", _layerRenames.Count > 0 && !LayersSeparateRadio.IsChecked.GetValueOrDefault() ? new Dictionary<string, string>(_layerRenames) : null);
        }

        private List<SourceRow> BuildSourceRows()
        {
            var rows = new List<SourceRow>();
            int i = 0;
            while (i < _pages.Count)
            {
                string path = _pages[i].SourcePath;
                int start = i;
                while (i < _pages.Count && string.Equals(_pages[i].SourcePath, path, StringComparison.OrdinalIgnoreCase)) i++;
                int count = i - start;
                rows.Add(new SourceRow(Path.GetFileName(path), count == 1 ? "1 page" : count + " pages", count == 1 ? (start + 1).ToString() : $"{start + 1}–{i}"));
            }
            return rows;
        }

        // ── Bản xem trước bookmark ────────────────────────────────────

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (_ready) RefreshPreview();
        }

        private async void RefreshPreview()
        {
            int version = ++_previewVersion;
            var options = CurrentOptions();
            var plan = await Task.Run(() => MergeOutlinePlanner.Build(_pages, options));
            if (version != _previewVersion) return;
            OutlineTree.ItemsSource = plan.Select(ToItem).ToList();
            NoOutlineText.Visibility = plan.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static OutlineItem ToItem(MergeOutlineNode node) => new(node.Title, node.OutPage, node.Children.Select(ToItem).ToList());

        // ── Số liệu: layer sau khi gộp, dung lượng ước tính ───────────

        /// <summary>Tên các layer sẽ có trong file kết quả theo cách gộp đang chọn (để đổi tên). Chế độ giữ riêng theo file không đổi tên được.</summary>
        private List<string> ResultLayerNames()
        {
            if (LayersKeepSomeRadio.IsChecked == true)
            {
                var names = _keepItems.Where(i => i.Keep).Select(i => i.Name).ToList();
                string collapse = CollapseNameBoxText();
                if (_keepItems.Any(i => !i.Keep) && !names.Contains(collapse)) names.Add(collapse);
                return names;
            }
            return _rawLayerNames.ToList();
        }

        private string CollapseNameBoxText() => CollapseNameBox.Text.Trim() is { Length: > 0 } text ? text : MergeOptions.DefaultCollapseName;

        private void RenameLayers_Click(object sender, RoutedEventArgs e)
        {
            var names = ResultLayerNames();
            if (names.Count == 0) return;
            var window = new RenameResultLayersWindow(names, _layerRenames) { Owner = this };
            if (window.ShowDialog() != true) return;
            _layerRenames = new Dictionary<string, string>(window.Renames, StringComparer.Ordinal);
            UpdateLayersHint();
        }

        private void LayerMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            RenameLayersButton.IsEnabled = LayersSeparateRadio.IsChecked != true;
            KeepSomePanel.Visibility = LayersKeepSomeRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            UpdateLayersHint();
        }

        private void KeepFilter_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyKeepFilter();

        private void ApplyKeepFilter()
        {
            string filter = KeepFilterBox.Text.Trim();
            KeepList.ItemsSource = filter.Length == 0 ? _keepItems
                : _keepItems.Where(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private void KeepAll_Click(object sender, RoutedEventArgs e) => SetKeepVisible(true);
        private void KeepNone_Click(object sender, RoutedEventArgs e) => SetKeepVisible(false);

        private void SetKeepVisible(bool keep)
        {
            if (KeepList.ItemsSource is IEnumerable<KeepLayerItem> shown)
                foreach (var item in shown) item.Keep = keep;
            UpdateLayersHint();
        }

        private void UpdateLayersHint()
        {
            if (_layerRenames.Count > 0 && LayersSeparateRadio.IsChecked != true) { UpdateLayersHintCore(); LayersHint.Text += $" {_layerRenames.Count} renamed."; return; }
            UpdateLayersHintCore();
        }

        private void UpdateLayersHintCore()
        {
            if (_layersAll == 0) { LayersHint.Text = "The source files have no layers."; return; }
            if (LayersSeparateRadio.IsChecked == true) LayersHint.Text = $"{_layersAll} layers stay separate, grouped by file.";
            else if (LayersKeepSomeRadio.IsChecked == true)
            {
                int kept = _keepItems.Count(i => i.Keep);
                LayersHint.Text = kept == 0 ? "Every layer will be merged into one." : $"{kept} layer(s) kept, the rest merged into one.";
            }
            else LayersHint.Text = _layersAll == _layersDistinct ? $"{_layersAll} layers, no duplicate names." : $"{_layersDistinct} layers instead of {_layersAll}.";
        }

        private async Task AnalyzeAsync()
        {
            SummaryText.Text = Loc.T($"{_pages.Count} pages");
            var pages = _pages;
            var (layersAll, layersDistinct, bytes, perName, rawNames) = await Task.Run(() =>
            {
                long size = 0;
                int all = 0;
                var names = new HashSet<string>(StringComparer.Ordinal);
                var filesPerShort = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var group in pages.GroupBy(p => p.SourcePath, StringComparer.OrdinalIgnoreCase))
                {
                    string path = group.Key;
                    try
                    {
                        var info = PdfLayerService.ReadLayers(path);
                        all += info.Names.Count;
                        foreach (var name in info.Names.Values) names.Add(name);
                        foreach (var shortName in info.Names.Values.Select(LayerMergePolicy.ShortName).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal))
                            filesPerShort[shortName] = filesPerShort.GetValueOrDefault(shortName) + 1;
                    }
                    catch { /* không đọc được layer: bỏ qua */ }
                    try
                    {
                        using var doc = new iText.Kernel.Pdf.PdfDocument(new iText.Kernel.Pdf.PdfReader(path));
                        int total = Math.Max(1, doc.GetNumberOfPages());
                        size += (long)(new FileInfo(path).Length * (double)group.Count() / total);
                    }
                    catch { }
                }
                return (all, names.Count, size, filesPerShort, names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList());
            });
            if (!IsLoaded) return;
            _layersAll = layersAll;
            _layersDistinct = layersDistinct;
            _rawLayerNames = rawNames;
            _keepItems = perName.OrderBy(kv => kv.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(kv => new KeepLayerItem(kv.Key, kv.Value, _savedKeep.Contains(kv.Key))).ToList();
            foreach (var item in _keepItems) item.PropertyChanged += (_, _) => UpdateLayersHint();
            ApplyKeepFilter();
            UpdateLayersHint();
            SummaryText.Text = Loc.T($"{_pages.Count} pages · about {Math.Max(1, bytes >> 20)} MB");
        }

        // ── Nút ───────────────────────────────────────────────────────

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Save the merged file to", SelectedPath = FolderBox.Text };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) FolderBox.Text = dlg.SelectedPath;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            string name = NameBox.Text.Trim();
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                AppDialog.Show(this, "Enter a valid file name.", "Save merged file", MessageBoxButton.OK, MessageBoxImage.Warning);
                NameBox.Focus();
                return;
            }
            if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";
            string folder = FolderBox.Text.Trim();
            if (!Directory.Exists(folder))
            {
                AppDialog.Show(this, "The folder does not exist.", "Save merged file", MessageBoxButton.OK, MessageBoxImage.Warning);
                FolderBox.Focus();
                return;
            }
            string output = Path.GetFullPath(Path.Combine(folder, name));
            if (_pages.Any(p => string.Equals(p.SourcePath, output, StringComparison.OrdinalIgnoreCase)))
            {
                AppDialog.Show(this, "That file is one of the merged files. Choose a different name.", "Save merged file", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (File.Exists(output) &&
                AppDialog.Show(this, $"\"{name}\" already exists. Replace it?", "Save merged file", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            Options = CurrentOptions();
            AppSettings.MergeOptionsSaved = Options;
            AppSettings.LastMergeFolder = folder;
            OutputPath = output;
            DialogResult = true;
        }
    }
}
