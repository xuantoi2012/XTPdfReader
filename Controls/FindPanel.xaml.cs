using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp.Controls
{
    public sealed record FindGroupHeader(string Title, int Count) { public bool IsHeader => true; }

    public sealed record FindHitRow(SearchHit Hit, string Before, string Match, string After) { public bool IsHeader => false; }

    /// <summary>
    /// Tab Find của panel trái (docs/UI_REDESIGN.md, mockup 8): ô tìm, Match case / Whole word, phạm vi (file này / mọi file đang mở), kết quả nhóm theo trang
    /// với đoạn trích, ghi chú số trang không có chữ tìm được. Việc tìm chạy nền (PdfThumbnailService.SearchAsync), kết quả hiện dần.
    /// </summary>
    public partial class FindPanel : UserControl
    {
        private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(350) };
        private CancellationTokenSource? _cts;
        private readonly List<SearchHit> _hits = new();
        private int _current = -1;
        private bool _rebuildQueued, _selecting, _searching;

        public FindPanel()
        {
            InitializeComponent();
            _debounce.Tick += (_, _) => { _debounce.Stop(); _ = SearchAsync(); };
        }

        /// <summary>Các file của window đang xem, và mọi file đang mở (đường dẫn).</summary>
        internal Func<(IReadOnlyList<string> ThisFile, IReadOnlyList<string> AllOpen)>? FilesProvider { get; set; }

        /// <summary>Bấm 1 kết quả / Next / Previous → tới trang và tô kết quả đó.</summary>
        internal event Action<SearchHit>? HitActivated;
        /// <summary>Kết quả đổi (đang tìm, tìm xong, xoá): toàn bộ kết quả, kết quả hiện tại (hoặc null) và chỉ số 1-based.</summary>
        internal event Action<IReadOnlyList<SearchHit>, SearchHit?, int>? ResultsChanged;

        internal string Query => QueryBox.Text;

        internal bool MatchCase { get => MatchCaseBox.IsChecked == true; set => MatchCaseBox.IsChecked = value; }
        internal bool WholeWord { get => WholeWordBox.IsChecked == true; set => WholeWordBox.IsChecked = value; }
        internal bool AllOpenFiles { get => ScopeAll.IsChecked == true; set { if (value) ScopeAll.IsChecked = true; else ScopeThis.IsChecked = true; } }

        /// <summary>Enter trong ô tìm của thanh Find nổi: chạy tìm nếu đang chờ, không thì sang kết quả kế / trước.</summary>
        internal void Submit(int delta)
        {
            if (_debounce.IsEnabled) { _debounce.Stop(); _ = SearchAsync(); }
            else Step(delta);
        }

        internal void FocusQuery()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                QueryBox.Focus();
                QueryBox.SelectAll();
            }), DispatcherPriority.Input);
        }

        internal void SetQuery(string text)
        {
            QueryBox.Text = text;
            QueryBox.CaretIndex = text.Length;
        }

        /// <summary>Số file đang mở (nhãn của phạm vi "All open files").</summary>
        private string? _folder;

        /// <summary>Mọi PDF trong thư mục đã chọn (kể cả thư mục con); không quá <see cref="MaxFolderFiles"/> file.</summary>
        private IReadOnlyList<string> FolderFiles()
        {
            if (_folder == null || !System.IO.Directory.Exists(_folder)) return Array.Empty<string>();
            try
            {
                return System.IO.Directory.EnumerateFiles(_folder, "*.pdf", new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                    .Where(f => !System.IO.Path.GetFileName(f).StartsWith('.')).Take(MaxFolderFiles).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            catch { return Array.Empty<string>(); }
        }

        private const int MaxFolderFiles = 500;

        private void ScopeFolder_Checked(object sender, RoutedEventArgs e)
        {
            if (_folder == null || !System.IO.Directory.Exists(_folder))
            {
                if (!ChooseFolder()) { ScopeThis.IsChecked = true; return; }
            }
            Option_Changed(sender, e);
        }

        /// <summary>Chuột phải "Folder" = chọn thư mục khác.</summary>
        private void ScopeFolder_RightClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (ChooseFolder() && ScopeFolder.IsChecked == true) Option_Changed(sender, e);
        }

        private bool ChooseFolder()
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Search the PDF files in this folder (and its sub-folders)", SelectedPath = _folder ?? "" };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return false;
            _folder = dialog.SelectedPath;
            ScopeFolder.Content = "Folder: " + System.IO.Path.GetFileName(_folder.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            ScopeFolder.ToolTip = _folder + " — right-click to choose another folder";
            return true;
        }

        internal void UpdateScopeLabel(int openFiles) => ScopeAll.Content = openFiles > 1 ? $"All open files ({openFiles})" : "All open files";

        internal void Clear()
        {
            _cts?.Cancel();
            QueryBox.Text = "";
            ClearResults();
        }

        private void ClearResults()
        {
            _hits.Clear();
            _current = -1;
            Results.ItemsSource = null;
            StatusText.Text = "";
            NoTextNote.Visibility = Visibility.Collapsed;
            RaiseChanged();
        }

        private void Query_TextChanged(object sender, TextChangedEventArgs e)
        {
            ClearButton.Visibility = QueryBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _debounce.Stop();
            if (QueryBox.Text.Trim().Length == 0)
            {
                _cts?.Cancel();
                ClearResults();
                return;
            }
            _debounce.Start();
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || QueryBox.Text.Trim().Length == 0) return;
            _debounce.Stop();
            _ = SearchAsync();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            Clear();
            QueryBox.Focus();
        }

        private void Query_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (_debounce.IsEnabled) { _debounce.Stop(); _ = SearchAsync(); }
                else Step((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape) { Clear(); e.Handled = true; }
        }

        // ── Tìm ───────────────────────────────────────────────────────

        private async Task SearchAsync()
        {
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            string query = QueryBox.Text.Trim();
            if (query.Length == 0) { ClearResults(); return; }

            var (thisFile, allOpen) = FilesProvider?.Invoke() ?? (Array.Empty<string>(), Array.Empty<string>());
            var files = (ScopeFolder.IsChecked == true ? FolderFiles() : ScopeAll.IsChecked == true ? allOpen : thisFile).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            bool matchCase = MatchCaseBox.IsChecked == true, wholeWord = WholeWordBox.IsChecked == true;

            _hits.Clear();
            _current = -1;
            _searching = true;
            Results.ItemsSource = null;
            NoTextNote.Visibility = Visibility.Collapsed;
            StatusText.Text = "Searching…";
            RaiseChanged();

            int totalPages = 0, withoutText = 0;
            foreach (string path in files)
            {
                if (cts.IsCancellationRequested) return;
                string file = path;
                var summary = await PdfThumbnailService.SearchAsync(file, query, matchCase, wholeWord, (batch, done) =>
                {
                    if (batch.Count == 0) return;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (cts.IsCancellationRequested) return;
                        _hits.AddRange(batch);
                        QueueRebuild(query, files.Count > 1);
                    }));
                }, cts.Token);
                if (cts.IsCancellationRequested) return;
                if (summary != null) { totalPages += summary.TotalPages; withoutText += summary.PagesWithoutText; }
            }
            if (cts.IsCancellationRequested) return;
            _searching = false;
            Rebuild(query, files.Count > 1);
            int pagesWithHits = _hits.Select(h => (h.Path, h.PageNumber)).Distinct().Count();
            StatusText.Text = _hits.Count == 0 ? "No results" : $"{_hits.Count} result{(_hits.Count == 1 ? "" : "s")} on {pagesWithHits} page{(pagesWithHits == 1 ? "" : "s")}";
            if (withoutText > 0 && totalPages > 0)
            {
                NoTextText.Text = $"{withoutText} of {totalPages} pages have no searchable text (their text is drawn as lines or scanned). Results cover the other {totalPages - withoutText} pages.";
                NoTextNote.Visibility = Visibility.Visible;
            }
            if (_hits.Count > 0 && _current < 0) Step(1, activate: false);
            RaiseChanged();
        }

        private void QueueRebuild(string query, bool multiFile)
        {
            if (_rebuildQueued) return;
            _rebuildQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _rebuildQueued = false;
                if (!_searching) return; // đã xong: kết quả cuối đã dựng
                Rebuild(query, multiFile);
                StatusText.Text = $"Searching… {_hits.Count} so far";
            }), DispatcherPriority.Background);
        }

        private void Rebuild(string query, bool multiFile)
        {
            var rows = new List<object>();
            foreach (var group in _hits.GroupBy(h => (h.Path, h.PageNumber)))
            {
                string title = (multiFile ? Path.GetFileName(group.Key.Path) + " · " : "") + "Page " + group.Key.PageNumber;
                rows.Add(new FindGroupHeader(title, group.Count()));
                foreach (var hit in group) rows.Add(ToRow(hit, query));
            }
            _selecting = true;
            Results.ItemsSource = rows;
            if (_current >= 0 && _current < _hits.Count)
                Results.SelectedItem = rows.OfType<FindHitRow>().FirstOrDefault(r => ReferenceEquals(r.Hit, _hits[_current]));
            _selecting = false;
        }

        private static FindHitRow ToRow(SearchHit hit, string query)
        {
            string s = hit.Snippet;
            int at = s.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            return at < 0 ? new FindHitRow(hit, s, "", "") : new FindHitRow(hit, s[..at], s.Substring(at, query.Length), s[(at + query.Length)..]);
        }

        // ── Chọn / duyệt kết quả ──────────────────────────────────────

        private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_selecting || Results.SelectedItem is not FindHitRow row) return;
            _current = _hits.IndexOf(row.Hit);
            RaiseChanged();
            HitActivated?.Invoke(row.Hit);
        }

        /// <summary>Kết quả kế / trước (vòng tròn). <paramref name="activate"/> false: chỉ chọn, không nhảy trang.</summary>
        internal void Step(int delta, bool activate = true)
        {
            if (_hits.Count == 0) return;
            _current = _current < 0 ? (delta > 0 ? 0 : _hits.Count - 1) : (_current + delta + _hits.Count) % _hits.Count;
            var hit = _hits[_current];
            _selecting = true;
            Results.SelectedItem = (Results.ItemsSource as List<object>)?.OfType<FindHitRow>().FirstOrDefault(r => ReferenceEquals(r.Hit, hit));
            if (Results.SelectedItem != null) Results.ScrollIntoView(Results.SelectedItem);
            _selecting = false;
            RaiseChanged();
            if (activate) HitActivated?.Invoke(hit);
        }

        private void RaiseChanged()
            => ResultsChanged?.Invoke(_hits, _current >= 0 && _current < _hits.Count ? _hits[_current] : null, _current + 1);
    }
}
