using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using XTPdfMergeApp.Services;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls
{
    /// <summary>Hộp thoại Export / Split (docs/UI_REDESIGN.md, mockup 15): giữ layer hay flatten theo View hiện tại, tối ưu dung lượng, tách nhiều file, chọn file cần tạo.</summary>
    public partial class ExportWindow : XTWindow
    {
        private readonly IReadOnlyList<(string Path, int Page)> _pages;
        private readonly IReadOnlyList<string> _sourcePaths;
        private readonly string _baseName;
        private readonly HashSet<string> _hiddenNames;
        private List<ExportPart> _parts = new();
        private int _planVersion;
        private bool _ready;

        internal ExportWindow(IReadOnlyList<(string Path, int Page)> pages, string baseName, string defaultFolder, bool preferFlatten,
            IReadOnlyList<string>? sourcePaths = null)
        {
            _pages = pages;
            _sourcePaths = sourcePaths ?? pages.Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _baseName = baseName;
            InitializeComponent();
            _hiddenNames = PdfExportService.CurrentHiddenNames(pages.Select(p => p.Path));
            FolderBox.Text = defaultFolder;

            KeepHint.Text = _hiddenNames.Count == 0 ? "The result stays editable with the same layer list."
                : $"The result keeps the layer list; the current view ({_hiddenNames.Count} layer{(_hiddenNames.Count == 1 ? "" : "s")} hidden) becomes its default.";
            FlattenHint.Text = _hiddenNames.Count == 0 ? "No layer is hidden right now: the layer list is removed, everything stays visible. Layers cannot be toggled later."
                : $"View: {_hiddenNames.Count} layer{(_hiddenNames.Count == 1 ? "" : "s")} hidden · smaller file, layers cannot be toggled later.";
            (preferFlatten ? FlattenLayers : KeepLayers).IsChecked = true;
            _ready = true;
            Loaded += (_, _) => _ = ReplanAsync();
        }

        private SplitMode Mode => (SplitBox.SelectedItem as ComboBoxItem)?.Tag is string tag ? Enum.Parse<SplitMode>(tag) : SplitMode.None;

        private void Setting_Changed(object sender, RoutedEventArgs e) { }

        private void Setting_Changed_Combo(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            EveryNRow.Visibility = Mode == SplitMode.EveryN ? Visibility.Visible : Visibility.Collapsed;
            RangesRow.Visibility = Mode == SplitMode.Ranges ? Visibility.Visible : Visibility.Collapsed;
            _ = ReplanAsync();
        }

        private void Setting_Changed_Text(object sender, TextChangedEventArgs e) { if (_ready) _ = ReplanAsync(); }

        private async Task ReplanAsync()
        {
            int version = ++_planVersion;
            int n = int.TryParse(EveryNBox.Text, out int v) ? v : 20;
            var parts = await PdfExportService.PlanAsync(_pages, _baseName, Mode, n, RangesBox.Text);
            if (version != _planVersion) return;
            _parts = parts.ToList();
            PartsList.ItemsSource = _parts;
            PartsNote.Text = Mode switch
            {
                SplitMode.PageSize => "Page sizes found in this document. Unchecked sizes are skipped.",
                SplitMode.Bookmark => "One file per top-level bookmark. Pages before the first bookmark go to “Front”.",
                SplitMode.None => "",
                _ => "Unchecked files are skipped."
            };
            UpdateSummary();
        }

        private void Part_Toggled(object sender, RoutedEventArgs e) => UpdateSummary();

        private void UpdateSummary()
        {
            var enabled = _parts.Where(p => p.Enabled).ToList();
            int pages = enabled.Sum(p => p.Pages.Count);
            SummaryText.Text = enabled.Count == 0 ? "Nothing to export." : $"{enabled.Count} file{(enabled.Count == 1 ? "" : "s")} · {pages} pages";
            ExportButton.IsEnabled = enabled.Count > 0;
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Export to", SelectedPath = FolderBox.Text };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) FolderBox.Text = dlg.SelectedPath;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            string folder = FolderBox.Text.Trim();
            if (!Directory.Exists(folder))
            {
                AppDialog.Show(this, "The folder does not exist.", "Export PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var enabled = _parts.Where(p => p.Enabled).ToList();
            var clash = enabled.Where(p => _sourcePaths.Any(path => string.Equals(path, Path.Combine(folder, p.FileName), StringComparison.OrdinalIgnoreCase))).ToList();
            if (clash.Count > 0)
            {
                AppDialog.Show(this, $"\"{clash[0].FileName}\" is one of the source files. Choose another folder.", "Export PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var existing = enabled.Where(p => File.Exists(Path.Combine(folder, p.FileName))).ToList();
            if (existing.Count > 0 &&
                AppDialog.Show(this, existing.Count == 1 ? $"\"{existing[0].FileName}\" already exists. Replace it?" : $"{existing.Count} files already exist. Replace them?",
                    "Export PDF", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            if (!await SignedPdfConfirmation.ConfirmAsync(this,
                _sourcePaths, "Export PDF files", false,
                enabled.Select(part => Path.Combine(folder, part.FileName)))) return;
            ExportButton.IsEnabled = false;
            var progress = new Progress<(int Done, int Total)>(p => SummaryText.Text = $"Exporting… {p.Done} of {p.Total}");
            var (written, error) = await PdfExportService.ExportAsync(_parts, folder, FlattenLayers.IsChecked == true, _hiddenNames, SizeOptimized.IsChecked == true, progress);
            if (error.Length > 0)
            {
                AppDialog.Show(this, $"Exported {written} of {enabled.Count} files.\n\n{error}", "Export PDF", MessageBoxButton.OK, MessageBoxImage.Error);
                ExportButton.IsEnabled = true;
                return;
            }
            Written = written;
            OutputFolder = folder;
            DialogResult = true;
        }

        public int Written { get; private set; }
        public string OutputFolder { get; private set; } = "";
    }
}
