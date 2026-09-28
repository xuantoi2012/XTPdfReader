using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp.Controls
{
    public sealed record CommentGroupHeader(string Title) { public bool IsHeader => true; }

    public sealed record CommentCard(CommentInfo Info, Geometry Icon, string Author, string DateText, string Text, bool Resolved) { public bool IsHeader => false; }

    /// <summary>Tab Comments của panel trái (docs/UI_REDESIGN.md, mockup 11): chú thích của window đang xem (Typewriter, Note, Highlight), lọc theo trạng thái / loại / tác giả,
    /// bấm để tới trang, bấm chip để đổi Open ↔ Resolved, xuất bảng tóm tắt (CSV).</summary>
    public partial class CommentsPanel : UserControl
    {
        private List<CommentInfo> _all = new();
        private IReadOnlyList<string> _paths = Array.Empty<string>();
        private bool _selecting, _loadingAuthors;
        private int _version;

        public CommentsPanel() => InitializeComponent();

        internal event Action<CommentInfo>? CommentActivated;
        internal event Action<CommentInfo, bool>? ResolvedToggled;
        /// <summary>Số chú thích (null = chưa có).</summary>
        internal event Action<int?>? CountChanged;

        /// <summary>Chú thích của các file như đang hiển thị (AnnotationStore: file + thay đổi chưa lưu).</summary>
        internal async Task SetFilesAsync(IReadOnlyList<string> paths)
        {
            _paths = paths;
            int version = ++_version;
            if (_all.Count == 0)
            {
                EmptyText.Text = "Loading comments…";
                EmptyText.Visibility = Visibility.Visible;
            }
            var all = new List<CommentInfo>();
            foreach (string path in paths)
            {
                if (version != _version) return;
                foreach (var a in await AnnotationStore.GetAllAsync(path))
                    if (a.Selectable)
                        all.Add(new CommentInfo(path, a.PageNumber, a.Name, a.Kind, a.Author, a.Date, a.Text, a.Resolved));
            }
            if (version != _version) return;
            _all = all;
            LoadAuthors();
            Rebuild();
            CountChanged?.Invoke(_all.Count == 0 ? null : _all.Count);
        }

        private void LoadAuthors()
        {
            _loadingAuthors = true;
            string? previous = (AuthorBox.SelectedItem as ComboBoxItem)?.Tag as string;
            AuthorBox.Items.Clear();
            AuthorBox.Items.Add(new ComboBoxItem { Content = "All authors", Tag = "" });
            foreach (string author in _all.Select(c => c.Author).Where(a => a.Length > 0).Distinct().OrderBy(a => a))
                AuthorBox.Items.Add(new ComboBoxItem { Content = author, Tag = author });
            AuthorBox.SelectedItem = AuthorBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == previous) ?? AuthorBox.Items[0];
            _loadingAuthors = false;
        }

        private IEnumerable<CommentInfo> Filtered()
        {
            string type = (TypeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            string author = (AuthorBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            return _all.Where(c => (type.Length == 0 || c.Kind.ToString() == type) && (author.Length == 0 || c.Author == author));
        }

        private void Filter_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) Rebuild(); }
        private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded && !_loadingAuthors) Rebuild(); }

        private void Rebuild()
        {
            var scoped = Filtered().ToList();
            StatusAll.Content = $"All {scoped.Count}";
            StatusOpen.Content = $"Open {scoped.Count(c => !c.Resolved)}";
            StatusResolved.Content = $"Resolved {scoped.Count(c => c.Resolved)}";
            var shown = scoped.Where(c => StatusOpen.IsChecked == true ? !c.Resolved : StatusResolved.IsChecked != true || c.Resolved).ToList();

            bool multiFile = _paths.Count > 1;
            var rows = new List<object>();
            foreach (var group in shown.GroupBy(c => (c.Path, c.Page)))
            {
                rows.Add(new CommentGroupHeader((multiFile ? Path.GetFileName(group.Key.Path) + " · " : "") + "Page " + group.Key.Page));
                foreach (var c in group) rows.Add(ToCard(c));
            }
            _selecting = true;
            Cards.ItemsSource = rows;
            _selecting = false;
            if (_all.Count == 0) { EmptyText.Text = "This document has no comments yet. Use Typewriter, Note or Highlight in the toolbar."; EmptyText.Visibility = Visibility.Visible; }
            else if (shown.Count == 0) { EmptyText.Text = "No comments match the filters."; EmptyText.Visibility = Visibility.Visible; }
            else EmptyText.Visibility = Visibility.Collapsed;
        }

        private static CommentCard ToCard(CommentInfo c)
        {
            string iconName = c.Kind switch
            {
                QuickAnnotationKind.Typewriter => "type", QuickAnnotationKind.Highlight => "hl", QuickAnnotationKind.Stamp => "stamp",
                QuickAnnotationKind.Shape => "shapes", _ => "comment"
            };
            string text = c.Text.Trim();
            if (c.Kind == QuickAnnotationKind.Stamp) text = StampDefinition.Decode(c.Text).Definition is { IsImage: false } stamp ? stamp.Text : "Image stamp";
            if (text.Length == 0) text = c.Kind switch { QuickAnnotationKind.Highlight => "Highlight", QuickAnnotationKind.Shape => "Shape", _ => "(empty)" };
            return new CommentCard(c, (Geometry)Application.Current.FindResource("Ui.Icon." + iconName),
                c.Author.Length > 0 ? c.Author : "Unknown", c.Date?.ToString("d MMM") ?? "", text, c.Resolved);
        }

        private void Cards_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_selecting || Cards.SelectedItem is not CommentCard card) return;
            CommentActivated?.Invoke(card.Info);
        }

        private void Status_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.DataContext is CommentCard card) ResolvedToggled?.Invoke(card.Info, !card.Resolved);
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_all.Count == 0) return;
            using var dlg = new System.Windows.Forms.SaveFileDialog
            {
                Title = "Export comment summary",
                Filter = "CSV (*.csv)|*.csv",
                DefaultExt = "csv",
                FileName = "Comments.csv"
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            string Q(string s) => "\"" + s.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
            var sb = new StringBuilder("File,Page,Type,Author,Date,Status,Text\r\n");
            foreach (var c in _all)
                sb.Append(string.Join(",", Q(Path.GetFileName(c.Path)), c.Page, c.Kind, Q(c.Author), c.Date?.ToString("yyyy-MM-dd") ?? "", c.Resolved ? "Resolved" : "Open", Q(c.Text))).Append("\r\n");
            try { File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true)); }
            catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), "Could not save the file:\n" + ex.Message, "Export summary", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }
}
