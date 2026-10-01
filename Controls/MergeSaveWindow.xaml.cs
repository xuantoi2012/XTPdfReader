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

    public sealed record OutlineItem(string Title, int Page, IReadOnlyList<OutlineItem> Children);

    /// <summary>Hộp thoại "Save merged file" (docs/UI_REDESIGN.md, mockup 17): tên file, thư mục, các file nguồn, tuỳ chọn và bản xem trước bookmark của kết quả.</summary>
    public partial class MergeSaveWindow : XTWindow
    {
        private readonly IReadOnlyList<(string SourcePath, int PageNumber)> _pages;
        private int _previewVersion;
        private bool _ready;

        /// <summary>Đường dẫn file kết quả (sau khi bấm Save).</summary>
        public string OutputPath { get; private set; } = "";
        public MergeOptions Options { get; private set; } = MergeOptions.Default;

        internal MergeSaveWindow(IReadOnlyList<(string SourcePath, int PageNumber)> pages, string defaultFolder)
        {
            _pages = pages;
            InitializeComponent();

            NameBox.Text = "Merged.pdf";
            FolderBox.Text = defaultFolder;
            var saved = AppSettings.MergeOptionsSaved;
            FileBookmarksBox.IsChecked = saved.FileBookmarks;
            KeepBookmarksBox.IsChecked = saved.KeepBookmarks;
            MergeLayersBox.IsChecked = saved.MergeLayers;
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
            => new(FileBookmarksBox.IsChecked == true, KeepBookmarksBox.IsChecked == true, MergeLayersBox.IsChecked == true,
                   PageNumbersBox.IsChecked == true, OptimizeBox.IsChecked == true);

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

        private async Task AnalyzeAsync()
        {
            SummaryText.Text = $"{_pages.Count} pages";
            var pages = _pages;
            var (layersAll, layersDistinct, bytes) = await Task.Run(() =>
            {
                long size = 0;
                int all = 0;
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var group in pages.GroupBy(p => p.SourcePath, StringComparer.OrdinalIgnoreCase))
                {
                    string path = group.Key;
                    try
                    {
                        var info = PdfLayerService.ReadLayers(path);
                        all += info.Names.Count;
                        foreach (var name in info.Names.Values) names.Add(name);
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
                return (all, names.Count, size);
            });
            if (!IsLoaded) return;
            LayersHint.Text = layersAll == 0 ? "The source files have no layers."
                : layersAll == layersDistinct ? $"{layersAll} layers, no duplicate names."
                : $"{layersDistinct} layers instead of {layersAll}.";
            SummaryText.Text = $"{_pages.Count} pages · about {Math.Max(1, bytes >> 20)} MB";
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
