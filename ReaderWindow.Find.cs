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
                FindBarQuery.Text = find.Query;
                FindBarCount.Text = hits.Count == 0 ? "0" : $"{Math.Max(1, index)} / {hits.Count}";
                FindBar.Visibility = hits.Count > 0 || find.Query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                ScheduleFindRefresh();
            };
            _groups.CollectionChanged += (_, _) => find.UpdateScopeLabel(_groups.Count);
            find.UpdateScopeLabel(_groups.Count);
        }

        /// <summary>Ctrl+F: mở tab Find, chọn ô tìm (lấy chữ đang chọn nếu có).</summary>
        private void OpenFind()
        {
            ShowStart(false);
            ShowMerge(false);
            ShowSettings(false);
            ReaderSidePanel.ShowPanel("Find");
            ReaderSidePanel.Find.FocusQuery();
        }

        private void FindNext_Click(object sender, RoutedEventArgs e) => ReaderSidePanel.Find.Step(1);
        private void FindPrev_Click(object sender, RoutedEventArgs e) => ReaderSidePanel.Find.Step(-1);

        private void FindClose_Click(object sender, RoutedEventArgs e)
        {
            ReaderSidePanel.Find.Clear();
            FindBar.Visibility = Visibility.Collapsed;
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
