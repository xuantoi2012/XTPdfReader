using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using XTPdfMergeApp.Services;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;
using DocumentGroup = XTPdfMergeApp.Domain.WorkspaceDocument;

namespace XTPdfMergeApp
{
    /// <summary>Find (docs/UI_REDESIGN.md, mockup 8): nối panel Find với vùng xem — tô kết quả trên trang, thanh Find nổi, Ctrl+F / F3.</summary>
    public partial class ReaderWindow
    {
        private IReadOnlyList<SearchHit> _findHits = Array.Empty<SearchHit>();
        private SearchHit? _findCurrent;
        private readonly List<UIElement> _findRects = new();
        private bool _findRefreshQueued;

        private void InitializeFind()
        {
            var find = ReaderSidePanel.Find;
            find.FilesProvider = () =>
            {
                var thisFile = _readerGroup == null ? new List<string>()
                    : _readerGroup.Pages.Select(p => p.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var all = _groups.SelectMany(g => g.Pages.Select(p => p.SourcePath)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return (thisFile, all);
            };
            find.HitActivated += OnFindHitActivated;
            find.ResultsChanged += (hits, current, index) =>
            {
                _findHits = hits;
                _findCurrent = current;
                if (FindBarQuery.Text != find.Query) { _syncingFindBar = true; FindBarQuery.Text = find.Query; _syncingFindBar = false; }
                FindBarCount.Text = find.Query.Length == 0 ? "" : hits.Count == 0 ? "0" : $"{Math.Max(1, index)} / {hits.Count}";
                if (find.Query.Length > 0) FindBar.Visibility = Visibility.Visible;
                ScheduleFindRefresh();
            };
            _groups.CollectionChanged += (_, _) => find.UpdateScopeLabel(_groups.Count);
            find.UpdateScopeLabel(_groups.Count);
        }

        /// <summary>Ctrl+F: mở tab Find, chọn ô tìm (lấy chữ đang chọn nếu có).</summary>
        private void OpenFind()
        {
            ShowStart(false);
            var find = ReaderSidePanel.Find;
            FindBar.Visibility = Visibility.Visible;
            _syncingFindBar = true;
            FindBarQuery.Text = find.Query;
            FindBarCase.IsChecked = find.MatchCase;
            FindBarWord.IsChecked = find.WholeWord;
            FindBarScope.SelectedIndex = find.AllOpenFiles ? 1 : 0;
            _syncingFindBar = false;
            Dispatcher.BeginInvoke(new Action(() => { FindBarQuery.Focus(); FindBarQuery.SelectAll(); }), DispatcherPriority.Input);
        }

        private bool _syncingFindBar;

        private void FindBarQuery_TextChanged(object sender, TextChangedEventArgs e)
        {
            FindBarHint.Visibility = FindBarQuery.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_syncingFindBar) return;
            ReaderSidePanel.Find.SetQuery(FindBarQuery.Text);
        }

        private void FindBarQuery_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { ReaderSidePanel.Find.Submit((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1); e.Handled = true; }
            else if (e.Key == Key.Escape) { FindClose_Click(this, new RoutedEventArgs()); e.Handled = true; }
        }

        private void FindBarOption_Click(object sender, RoutedEventArgs e)
        {
            if (_syncingFindBar) return;
            var find = ReaderSidePanel.Find;
            find.MatchCase = FindBarCase.IsChecked == true;
            find.WholeWord = FindBarWord.IsChecked == true;
            find.AllOpenFiles = FindBarScope.SelectedIndex == 1;
        }

        private void FindBarExpand_Click(object sender, RoutedEventArgs e)
        {
            bool show = FindBarOptions.Visibility != Visibility.Visible;
            FindBarOptions.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            FindBarExpand.Icon = (Geometry)FindResource(show ? "Ui.Icon.chevd" : "Ui.Icon.chevr");
        }

        private void ReaderFind_Click(object sender, RoutedEventArgs e) => OpenFind();

        /// <summary>Tạo bản sao searchable qua Windows OCR; không ghi đè file nguồn hoặc working copy đang mở.</summary>
        private async void ReaderOcr_Click(object sender, RoutedEventArgs e)
        {
            string? path = _readerPage?.SourcePath ?? _readerGroup?.SourcePath;
            if (string.IsNullOrWhiteSpace(path)) return;
            if (_readerGroup is { IsDirty: true } && string.Equals(_readerGroup.SourcePath, path, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Save the current document changes before OCR. OCR always uses the saved PDF and creates a separate searchable copy.",
                    "Save before OCR", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var security = await PdfSecurityService.ReadAsync(path);
            if (security.Error != null)
            {
                MessageBox.Show(this, "Could not read this PDF for OCR:\n" + security.Error, "OCR", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (security.IsEncrypted)
            {
                MessageBox.Show(this, "OCR of encrypted PDFs is not available yet because the searchable copy must preserve the document protection. Remove protection first, then OCR the copy.",
                    "OCR", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var open = await PdfThumbnailService.TryGetPageCountAsync(path);
            if (open.PageCount <= 0)
            {
                MessageBox.Show(this, "Could not determine the page count for OCR.", "OCR", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            int currentPage = Math.Clamp(_readerPage?.PageNumber ?? 1, 1, open.PageCount);
            var dialog = new Controls.PdfOcrWindow(path, open.PageCount, currentPage) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.OutputPath is not { } output) return;

            XTStyle.Controls.XTGrowl.Success("Created searchable PDF copy", this);
            if (EditHost != null && MessageBox.Show(this, "Open the searchable copy now?", "OCR complete", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                await EditHost.OpenPathsAsync(new[] { output });
        }

        /// <summary>In các trang của window đang xem (hộp thoại Print).</summary>
        private async void ReaderPrint_Click(object sender, RoutedEventArgs e)
        {
            if (_readerGroup == null || _readerGroup.Pages.Count == 0) return;
            var sourcePaths = _readerGroup.Pages.Select(p => p.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var security in await System.Threading.Tasks.Task.WhenAll(sourcePaths.Select(PdfSecurityService.ReadAsync)))
            {
                if (security.Error == null && !security.CanPrint)
                {
                    MessageBox.Show(this, "This PDF does not grant permission to print with the current password.",
                        "Print restricted", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
            // Unsaved annotations are printed too: files with edits are read from their working copy.
            var pages = await AnnotationWorkingCopy.MapAsync(_readerGroup.Pages.Select(p => (p.SourcePath, p.PageNumber)));
            int current = _readerPage == null ? 0 : Math.Max(0, _readerGroup.Pages.IndexOf(_readerPage));
            var dialog = new Controls.PrintWindow(pages, current) { Owner = this };
            if (dialog.ShowDialog() == true) XTStyle.Controls.XTGrowl.Success("Sent to the printer", this);
        }

        private async void ReaderSecurity_Click(object sender, RoutedEventArgs e)
        {
            string? path = _readerPage?.SourcePath ?? _readerGroup?.SourcePath;
            if (string.IsNullOrWhiteSpace(path)) return;
            var info = await PdfSecurityService.ReadAsync(path);
            var summary = new Controls.DocumentSecurityWindow(System.IO.Path.GetFileName(path), info) { Owner = this };
            if (summary.ShowDialog() != true || !summary.ManageRequested || info.Error != null) return;

            if (info.IsEncrypted && !info.IsOwner)
            {
                MessageBox.Show(this, "This file was opened with a user password. Enter its owner password when opening the file before changing protection.",
                    "Protect PDF", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var options = Controls.PdfProtectionWindow.Ask(this, info);
            if (options == null) return;
            try
            {
                AnnotationStore.ReleaseReader(path);
                using (await PdfThumbnailService.SuspendDocumentAsync(path, TimeSpan.FromSeconds(4)))
                    await System.Threading.Tasks.Task.Run(() => PdfSecurityService.ApplyProtection(path, options));

                if (options.RemoveProtection) await PdfThumbnailService.ForgetDocumentPasswordAsync(path);
                else await PdfThumbnailService.SetDocumentPasswordAsync(path, options.OwnerPassword);
                Session.RefreshDiskStamp(path);
                ReaderSidePanel.OnSourceEdited(path);
                XTStyle.Controls.XTGrowl.Success(options.RemoveProtection ? "Removed PDF protection" : "Protected PDF with AES-256", this);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not change PDF protection:\n" + ex.Message, "Protect PDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void FindNext_Click(object sender, RoutedEventArgs e) => ReaderSidePanel.Find.Step(1);
        private void FindPrev_Click(object sender, RoutedEventArgs e) => ReaderSidePanel.Find.Step(-1);

        private void FindClose_Click(object sender, RoutedEventArgs e)
        {
            ReaderSidePanel.Find.Clear();
            FindBar.Visibility = Visibility.Collapsed;
            _findHits = Array.Empty<SearchHit>();
            _findCurrent = null;
            ScheduleFindRefresh();
        }

        private void OnFindHitActivated(SearchHit hit)
        {
            _findCurrent = hit;
            DocumentGroup? group = _readerGroup != null && _readerGroup.Pages.Any(p => Same(p.SourcePath, hit.Path)) ? _readerGroup
                : _groups.FirstOrDefault(g => g.Pages.Any(p => Same(p.SourcePath, hit.Path)));
            if (group == null) return;
            var row = FindRowForSourcePage(group.Pages, hit.Path, hit.PageNumber);
            if (row == null) return;
            if (!ReferenceEquals(group, _readerGroup) && ReaderDocumentTabs.SelectedItem is not null) ReaderDocumentTabs.SelectedItem = group;
            NavigateToRow(group, row);
            ScheduleFindRefresh();
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // ── Tô kết quả trên trang ─────────────────────────────────────

        private void ScheduleFindRefresh()
        {
            if (_findRefreshQueued) return;
            _findRefreshQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _findRefreshQueued = false;
                RefreshFindHighlights();
            }), DispatcherPriority.Background);
        }

        private void RefreshFindHighlights()
        {
            foreach (var element in _findRects) ReaderInteractionLayer.Children.Remove(element);
            _findRects.Clear();
            if (_findHits.Count == 0 || _readerGroup == null || _readerPage == null) return;

            var rows = new Dictionary<(string, int), PageRow>();
            foreach (var page in _readerGroup.Pages) rows.TryAdd((page.SourcePath.ToLowerInvariant(), page.PageNumber), page);

            double maxX = ReaderInteractionLayer.ActualWidth, maxY = ReaderInteractionLayer.ActualHeight;
            var fill = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xC4, 0x00));
            var accent = (Brush)FindResource("Ui.Accent");
            int drawn = 0;
            foreach (var hit in _findHits)
            {
                if (drawn > 600) break;
                if (!rows.TryGetValue((hit.Path.ToLowerInvariant(), hit.PageNumber), out var row)) continue;
                bool current = ReferenceEquals(hit, _findCurrent);
                foreach (var (u1, v1, u2, v2) in hit.Rects)
                {
                    if (!TryPageToLayer(row, u1, v1, out Point a) || !TryPageToLayer(row, u2, v2, out Point b)) break;
                    double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y), w = Math.Max(2, Math.Abs(b.X - a.X)), h = Math.Max(2, Math.Abs(b.Y - a.Y));
                    if (x > maxX || y > maxY || x + w < 0 || y + h < 0) continue;
                    var rect = new Rectangle
                    {
                        Width = w, Height = h, Fill = fill, RadiusX = 2, RadiusY = 2, IsHitTestVisible = false,
                        Stroke = current ? accent : null, StrokeThickness = current ? 2 : 0
                    };
                    Canvas.SetLeft(rect, x);
                    Canvas.SetTop(rect, y);
                    ReaderInteractionLayer.Children.Insert(0, rect);
                    _findRects.Add(rect);
                    drawn++;
                }
            }
        }
    }
}
