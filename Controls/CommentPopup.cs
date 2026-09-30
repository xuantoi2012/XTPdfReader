using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XTPdfMergeApp.Services;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls
{
    /// <summary>
    /// Popup nổi ngay tại vị trí bấm — kiểu Word: bấm vào icon Note/Typewriter là thấy ngay tác giả, ngày,
    /// nội dung, trả lời — không cần mở panel Comments. Cùng hình thức thẻ với panel Comments (avatar tròn,
    /// reply gọn không viền riêng) nhưng tự vẽ bằng code (không dùng chung DataTemplate của CommentsPanel vì
    /// panel đó là ItemsControl trong sidebar, còn đây là 1 cửa sổ nổi độc lập theo dõi vị trí bấm trên trang).
    /// </summary>
    internal sealed class CommentPopup : Window
    {
        internal event Action<string>? ReplySubmitted;
        internal event Action<string>? EditRequested;
        internal event Action? DeleteRequested;
        internal event Action<bool>? ResolvedToggled;

        private TextBlock _chipText = null!;
        private Border _chip = null!;
        private bool _resolved;
        private bool _closed;
        private bool _modalOpen;

        private CommentPopup(CommentInfo root, IReadOnlyList<CommentInfo> replies)
        {
            _resolved = root.Resolved;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = true;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            Width = 300;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Background = (Brush)Application.Current.FindResource("Ui.Surface");
            BorderBrush = (Brush)Application.Current.FindResource("Ui.Border");
            BorderThickness = new Thickness(1.5);
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.25 };

            var root_ = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
            root_.Children.Add(BuildHeader(root));
            root_.Children.Add(new TextBlock
            {
                Text = root.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(36, 6, 0, 0),
                Foreground = (Brush)Application.Current.FindResource("Ui.Text")
            });
            foreach (var reply in replies) root_.Children.Add(BuildReplyRow(reply));

            var replyBox = new TextBox
            {
                Margin = new Thickness(0, 8, 0, 0), Height = 28, VerticalContentAlignment = VerticalAlignment.Center,
                Text = "Reply", Foreground = (Brush)Application.Current.FindResource("Ui.Muted")
            };
            replyBox.GotKeyboardFocus += (_, _) =>
            {
                if (replyBox.Text != "Reply") return;
                replyBox.Text = ""; replyBox.Foreground = (Brush)Application.Current.FindResource("Ui.Text");
            };
            replyBox.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                string text = replyBox.Text.Trim();
                if (text.Length > 0 && text != "Reply") { ReplySubmitted?.Invoke(text); CloseOnce(); }
                e.Handled = true;
            };
            root_.Children.Add(replyBox);
            Content = root_;

            Loaded += (_, _) => replyBox.Focus();
            // Mở hộp thoại "Edit" (modal, chủ là chính popup này) cũng làm popup mất active — không phải chỉ khi
            // bấm ra ngoài — nên phải chặn đóng lặp (CloseOnce) chứ Close() thẳng gọi lại chính Deactivated này
            // ngay giữa lúc WPF đang đóng cửa sổ, ném lỗi không ai bắt được và sập cả app.
            Deactivated += (_, _) => { if (!_modalOpen) CloseOnce(); };
        }

        private void CloseOnce()
        {
            if (_closed) return;
            _closed = true;
            Close();
        }

        private FrameworkElement BuildHeader(CommentInfo root)
        {
            var (initials, avatar) = CommentsPanel.PersonStyle(root.Author.Length > 0 ? root.Author : "Unknown");
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var circle = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = avatar, VerticalAlignment = VerticalAlignment.Top };
            circle.Child = new TextBlock { Text = initials, Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(circle);

            var namePanel = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            namePanel.Children.Add(new TextBlock { Text = root.Author.Length > 0 ? root.Author : "Unknown", FontWeight = FontWeights.SemiBold, Foreground = (Brush)Application.Current.FindResource("Ui.Text") });
            namePanel.Children.Add(new TextBlock { Text = root.Date?.ToString("d MMM") ?? "", FontSize = 11, Foreground = (Brush)Application.Current.FindResource("Ui.Muted") });
            Grid.SetColumn(namePanel, 1);
            grid.Children.Add(namePanel);

            _chip = new Border
            {
                CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 1), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Top,
                Background = (Brush)Application.Current.FindResource(_resolved ? "Ui.Chip" : "Ui.AccentSoft"),
                ToolTip = "Click to change the status"
            };
            _chip.Child = _chipText = new TextBlock
            {
                Text = _resolved ? "Resolved" : "Open", FontSize = 11,
                Foreground = (Brush)Application.Current.FindResource(_resolved ? "Ui.Muted" : "Ui.Accent")
            };
            _chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; _resolved = !_resolved; UpdateChip(); ResolvedToggled?.Invoke(_resolved); };
            Grid.SetColumn(_chip, 2);
            grid.Children.Add(_chip);

            var menu = new XTButton { Style = (Style)Application.Current.FindResource("UiIconButton"), Icon = (Geometry)Application.Current.FindResource("Ui.Icon.more"), Width = 26, Height = 26, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            menu.Click += (_, _) =>
            {
                var cm = new ContextMenu();
                var edit = new MenuItem { Header = "Edit" };
                edit.Click += (_, _) =>
                {
                    _modalOpen = true;
                    string? text = TextPromptWindow.Ask(this, "Edit comment", "Comment text:", "");
                    _modalOpen = false;
                    if (!string.IsNullOrWhiteSpace(text)) { EditRequested?.Invoke(text); CloseOnce(); }
                };
                var delete = new MenuItem { Header = "Delete" };
                delete.Click += (_, _) => { DeleteRequested?.Invoke(); CloseOnce(); };
                cm.Items.Add(edit); cm.Items.Add(delete);
                cm.PlacementTarget = menu; cm.IsOpen = true;
            };
            Grid.SetColumn(menu, 3);
            grid.Children.Add(menu);
            return grid;
        }

        private void UpdateChip()
        {
            _chip.Background = (Brush)Application.Current.FindResource(_resolved ? "Ui.Chip" : "Ui.AccentSoft");
            _chipText.Text = _resolved ? "Resolved" : "Open";
            _chipText.Foreground = (Brush)Application.Current.FindResource(_resolved ? "Ui.Muted" : "Ui.Accent");
        }

        private static FrameworkElement BuildReplyRow(CommentInfo reply)
        {
            var (initials, avatar) = CommentsPanel.PersonStyle(reply.Author.Length > 0 ? reply.Author : "Unknown");
            var border = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = (Brush)Application.Current.FindResource("Ui.Border"),
                Padding = new Thickness(0, 8, 0, 0), Margin = new Thickness(0, 8, 0, 0)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var circle = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = avatar, VerticalAlignment = VerticalAlignment.Top };
            circle.Child = new TextBlock { Text = initials, Foreground = Brushes.White, FontSize = 9, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(circle);
            var col = new StackPanel { Margin = new Thickness(7, 0, 0, 0) };
            var head = new Grid();
            head.Children.Add(new TextBlock { Text = reply.Author.Length > 0 ? reply.Author : "Unknown", FontWeight = FontWeights.SemiBold, FontSize = 11.5, Foreground = (Brush)Application.Current.FindResource("Ui.Text") });
            head.Children.Add(new TextBlock { Text = reply.Date?.ToString("d MMM") ?? "", FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Right, Foreground = (Brush)Application.Current.FindResource("Ui.Muted") });
            col.Children.Add(head);
            col.Children.Add(new TextBlock { Text = reply.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), FontSize = 11.5, Foreground = (Brush)Application.Current.FindResource("Ui.Text") });
            Grid.SetColumn(col, 1);
            grid.Children.Add(col);
            border.Child = grid;
            return border;
        }

        /// <summary>Mở popup ngay cạnh điểm bấm (toạ độ màn hình), kẹp trong biên màn hình.</summary>
        public static CommentPopup Show(Window owner, Point screenPoint, CommentInfo root, IReadOnlyList<CommentInfo> replies)
        {
            var popup = new CommentPopup(root, replies) { Owner = owner };
            popup.Left = screenPoint.X + 12;
            popup.Top = screenPoint.Y + 12;
            popup.ContentRendered += (_, _) =>
            {
                var area = SystemParameters.WorkArea;
                if (popup.Left + popup.ActualWidth > area.Right) popup.Left = Math.Max(area.Left, area.Right - popup.ActualWidth);
                if (popup.Top + popup.ActualHeight > area.Bottom) popup.Top = Math.Max(area.Top, area.Bottom - popup.ActualHeight);
            };
            popup.Show();
            return popup;
        }
    }
}
