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

    /// <summary>1 người trong 1 thread — dùng cho reply (avatar nhỏ, không viền/box riêng, không có ô Reply riêng).</summary>
    public sealed record CommentPerson(CommentInfo Info, string Initials, Brush AvatarBrush, string Author, string DateText, string Text);

    /// <summary>1 thread bình luận: mục gốc + toàn bộ reply (mọi cấp, dàn phẳng theo thời gian) hiện gộp trong 1 khung —
    /// kiểu Word, thay vì mỗi reply là 1 card viền riêng như trước.</summary>
    public sealed record CommentCard(CommentInfo Info, Geometry Icon, string Initials, Brush AvatarBrush, string Author, string DateText, string Text,
        bool Resolved, IReadOnlyList<CommentPerson> Replies) { public bool IsHeader => false; }

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
        internal event Action<CommentInfo, string>? ReplySubmitted;
        internal event Action<CommentInfo, string>? EditRequested;
        internal event Action<CommentInfo>? DeleteRequested;
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
                        all.Add(new CommentInfo(path, a.PageNumber, a.Name, a.Kind, a.Author, a.Date, a.Text, a.Resolved,
                            a.Kind == QuickAnnotationKind.Reply && a.Format.StartsWith("R|", StringComparison.Ordinal) ? a.Format[2..] : ""));
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

        /// <summary>What the list shows now: type, author and status filters applied.</summary>
        private List<CommentInfo> Shown() => Filtered().Where(c => StatusOpen.IsChecked == true ? !c.Resolved : StatusResolved.IsChecked != true || c.Resolved).ToList();

        private void Filter_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) Rebuild(); }
        private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded && !_loadingAuthors) Rebuild(); }

        private void Rebuild()
        {
            var scoped = Filtered().ToList();
            StatusAll.Content = $"All {scoped.Count}";
            StatusOpen.Content = $"Open {scoped.Count(c => !c.Resolved)}";
            StatusResolved.Content = $"Resolved {scoped.Count(c => c.Resolved)}";
            var shown = Shown();

            bool multiFile = _paths.Count > 1;
            var rows = new List<object>();
            foreach (var group in shown.GroupBy(c => (c.Path, c.Page)))
            {
                rows.Add(new CommentGroupHeader((multiFile ? Path.GetFileName(group.Key.Path) + " · " : "") + "Page " + group.Key.Page));
                var byParent = group.ToLookup(c => c.ParentName);
                // Mọi reply (kể cả reply-của-reply) dàn phẳng theo thời gian dưới đúng 1 thread gốc — giống Word,
                // Word cũng không lồng reply nhiều cấp, chỉ có 1 luồng hội thoại phẳng dưới mỗi bình luận gốc.
                List<CommentPerson> CollectReplies(string parentName) => byParent[parentName].OrderBy(r => r.Date)
                    .SelectMany(r => new[] { ToPerson(r) }.Concat(CollectReplies(r.Name))).ToList();
                foreach (var root in group.Where(c => c.ParentName.Length == 0 || !group.Any(p => p.Name == c.ParentName)).OrderBy(c => c.Date))
                    rows.Add(ToCard(root, CollectReplies(root.Name)));
            }
            _selecting = true;
            Cards.ItemsSource = rows;
            _selecting = false;
            if (_all.Count == 0) { EmptyText.Text = "This document has no comments yet. Use Typewriter, Note or Highlight in the toolbar."; EmptyText.Visibility = Visibility.Visible; }
            else if (shown.Count == 0) { EmptyText.Text = "No comments match the filters."; EmptyText.Visibility = Visibility.Visible; }
            else EmptyText.Visibility = Visibility.Collapsed;
        }

        private static CommentCard ToCard(CommentInfo c, IReadOnlyList<CommentPerson> replies)
        {
            string iconName = c.Kind switch
            {
                QuickAnnotationKind.Typewriter => "type", QuickAnnotationKind.Reply => "comment", QuickAnnotationKind.Callout => "callout", QuickAnnotationKind.Highlight => "hl", QuickAnnotationKind.Stamp => "stamp",
                QuickAnnotationKind.Shape => "shapes", QuickAnnotationKind.Underline => "underline", QuickAnnotationKind.StrikeOut => "strike", QuickAnnotationKind.Squiggly => "squiggly", QuickAnnotationKind.Ink => "pencil", _ => "comment"
            };
            string text = c.Text.Trim();
            if (c.Kind == QuickAnnotationKind.Stamp) text = StampDefinition.Decode(c.Text).Definition is { IsImage: false } stamp ? stamp.Text : "Image stamp";
            if (text.Length == 0) text = c.Kind switch
            {
                QuickAnnotationKind.Callout => "Callout", QuickAnnotationKind.Highlight => "Highlight", QuickAnnotationKind.Shape => "Shape",
                QuickAnnotationKind.Underline => "Underline", QuickAnnotationKind.StrikeOut => "Strikethrough", QuickAnnotationKind.Squiggly => "Squiggly underline", QuickAnnotationKind.Ink => "Pencil stroke", _ => "(empty)"
            };
            string author = c.Author.Length > 0 ? c.Author : "Unknown";
            var (initials, avatar) = PersonStyle(author);
            return new CommentCard(c, (Geometry)Application.Current.FindResource("Ui.Icon." + iconName),
                initials, avatar, author, c.Date?.ToString("d MMM") ?? "", text, c.Resolved, replies);
        }

        /// <summary>1 reply, hiển thị gọn trong thread của mục gốc — không icon loại chú thích, không ô Reply riêng.</summary>
        private static CommentPerson ToPerson(CommentInfo c)
        {
            string author = c.Author.Length > 0 ? c.Author : "Unknown";
            var (initials, avatar) = PersonStyle(author);
            string text = c.Text.Trim();
            return new CommentPerson(c, initials, avatar, author, c.Date?.ToString("d MMM") ?? "", text.Length > 0 ? text : "(empty)");
        }

        // Màu avatar theo tên tác giả (băm đơn giản) — cùng 1 người luôn ra cùng 1 màu trong suốt phiên làm việc.
        private static readonly Brush[] AvatarPalette = BuildAvatarPalette();
        private static Brush[] BuildAvatarPalette()
        {
            var hex = new[] { "#D9424F", "#2E7D32", "#1565C0", "#8E24AA", "#EF6C00", "#00838F", "#5D4037", "#6D4C41" };
            var brushes = new Brush[hex.Length];
            for (int i = 0; i < hex.Length; i++)
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex[i]));
                b.Freeze();
                brushes[i] = b;
            }
            return brushes;
        }

        internal static (string Initials, Brush Avatar) PersonStyle(string author)
        {
            string[] parts = author.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string initials = parts.Length switch
            {
                0 => "?",
                1 => (parts[0].Length >= 2 ? parts[0][..2] : parts[0]).ToUpperInvariant(),
                _ => (parts[0][0].ToString() + parts[^1][0]).ToUpperInvariant()
            };
            int hash = 0;
            foreach (char ch in author) hash = hash * 31 + ch;
            return (initials, AvatarPalette[(hash & int.MaxValue) % AvatarPalette.Length]);
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

        private void ReplyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox { Text: "Reply" } box) { box.Text = ""; box.Foreground = (Brush)Application.Current.FindResource("Ui.Text"); }
        }

        private void ReplyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || sender is not TextBox { Tag: CommentInfo info } box) return;
            string text = box.Text.Trim();
            if (text.Length > 0 && text != "Reply") ReplySubmitted?.Invoke(info, text);
            box.Text = "Reply"; box.Foreground = (Brush)Application.Current.FindResource("Ui.Muted"); e.Handled = true;
        }

        private void MessageMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: CommentInfo info } anchor) return;
            var menu = new ContextMenu();
            var edit = new MenuItem { Header = "Edit" };
            edit.Click += (_, _) =>
            {
                string? text = TextPromptWindow.Ask(Window.GetWindow(this), "Edit comment", "Comment text:", info.Text);
                if (!string.IsNullOrWhiteSpace(text) && text != info.Text) EditRequested?.Invoke(info, text);
            };
            var delete = new MenuItem { Header = "Delete" };
            delete.Click += (_, _) => DeleteRequested?.Invoke(info);
            menu.Items.Add(edit); menu.Items.Add(delete);
            menu.PlacementTarget = anchor; menu.IsOpen = true;
        }

        /// <summary>Exports the comments the list shows (the filters apply: only open ones, one author, one type...), with the sheet number and title of each page.</summary>
        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            var shown = Shown();
            if (shown.Count == 0) return;
            var sheets = new Dictionary<string, IReadOnlyDictionary<int, XTSheetPageInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in shown.Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase))
                sheets[path] = await Task.Run(() => XTSheetIndex.Read(path));
            var rows = CommentExport.Build(shown, (path, page) => sheets.TryGetValue(path, out var map) && map.TryGetValue(page, out var info) ? info : null);
            string first = shown[0].Path;
            using var dlg = new System.Windows.Forms.SaveFileDialog
            {
                Title = "Export the comment list",
                Filter = "CSV (*.csv)|*.csv",
                DefaultExt = "csv",
                FileName = Path.GetFileNameWithoutExtension(first) + " - comments.csv",
                InitialDirectory = Path.GetDirectoryName(first) is { } dir && Directory.Exists(dir) ? dir : ""
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            try { File.WriteAllText(dlg.FileName, CommentExport.ToCsv(rows), new UTF8Encoding(false)); }
            catch (Exception ex) { AppDialog.Show(Window.GetWindow(this), "Could not save the file:\n" + ex.Message, "Export comments", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }
}
