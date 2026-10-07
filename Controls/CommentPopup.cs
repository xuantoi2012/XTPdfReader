using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XTPdfMergeApp.Services;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls
{
    /// <summary>
    /// Popup nổi ngay tại vị trí bấm — làm theo đúng thẻ comment của Word (đã mở Word thử):
    /// - Tạo ghi chú mới: thẻ "soạn" (avatar + tên, ô nhập nhiều dòng, nút Post / Cancel, Ctrl+Enter = Post).
    /// - Bấm ghi chú có sẵn: tác giả, ngày, nội dung + toàn bộ reply; nút bút chì = sửa NGAY TRONG thẻ (không mở
    ///   hộp thoại), "…" = Edit / Delete / Resolve thread; ô "Reply" ở cuối, gõ vào thì hiện Post / Cancel,
    ///   Post xong reply hiện luôn trong thẻ và thẻ vẫn mở (như Word), không đóng popup.
    /// Tự vẽ bằng code (không dùng chung DataTemplate của CommentsPanel vì panel đó là ItemsControl trong sidebar,
    /// còn đây là 1 cửa sổ nổi độc lập theo dõi vị trí bấm trên trang).
    /// </summary>
    internal sealed class CommentPopup : Window
    {
        /// <summary>Thẻ soạn: người dùng bấm Post (hoặc bấm ra ngoài khi đã gõ chữ) — tạo ghi chú với nội dung này.</summary>
        internal event Action<string>? Posted;
        internal event Action<string>? ReplySubmitted;
        /// <summary>Sửa nội dung 1 mục trong luồng (gốc hoặc reply).</summary>
        internal event Action<CommentInfo, string>? EditRequested;
        /// <summary>Xoá 1 mục trong luồng; xoá gốc = xoá cả luồng (popup tự đóng).</summary>
        internal event Action<CommentInfo>? DeleteRequested;
        internal event Action<bool>? ResolvedToggled;

        private readonly StackPanel _thread = new();
        private TextBlock _chipText = null!;
        private Border _chip = null!;
        private bool _resolved;
        private bool _closed;
        private readonly TextBox? _composeBox;

        private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

        private CommentPopup()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = true;
            Topmost = true;
            SizeToContent = SizeToContent.Height;
            Width = 320;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Background = Res("Ui.Surface");
            // Viền màu nhấn như thẻ đang được chọn của Word.
            BorderBrush = Res("Ui.Accent");
            BorderThickness = new Thickness(1.5);
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; CloseOnce(); } };
            // Bấm ra ngoài = đóng. Thẻ soạn đã có chữ thì Post luôn (không mất chữ vừa gõ, giống ô nhập cũ khi mất focus).
            Deactivated += (_, _) =>
            {
                if (_composeBox != null && _composeBox.Text.Trim().Length > 0) { Posted?.Invoke(_composeBox.Text.Trim()); _composeBox.Text = ""; }
                CloseOnce();
            };
        }

        /// <summary>Thẻ soạn ghi chú mới.</summary>
        private CommentPopup(string author) : this()
        {
            var root = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
            root.Children.Add(BuildPersonHeader(author, null, 28));
            _composeBox = NewInput("Start a note…", 56);
            _composeBox.Margin = new Thickness(0, 8, 0, 0);
            root.Children.Add(_composeBox);
            root.Children.Add(new TextBlock { Text = "Tip: Press Ctrl+Enter to post.", FontSize = 11, Margin = new Thickness(0, 4, 0, 0), Foreground = Res("Ui.Muted") });
            var box = _composeBox;
            root.Children.Add(BuildPostCancel(() => Submit(box), CloseOnce, out _));
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; Submit(box); }
            };
            Content = root;
            Loaded += (_, _) => box.Focus();

            void Submit(TextBox b)
            {
                string text = b.Text.Trim();
                if (text.Length == 0) return;
                b.Text = "";
                Posted?.Invoke(text);
                CloseOnce();
            }
        }

        /// <summary>Thẻ xem luồng có sẵn.</summary>
        private CommentPopup(CommentInfo root, IReadOnlyList<CommentInfo> replies) : this()
        {
            _resolved = root.Resolved;
            var panel = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
            _thread.Children.Add(BuildItem(root, isRoot: true));
            foreach (var reply in replies) _thread.Children.Add(BuildItem(reply, isRoot: false));
            panel.Children.Add(_thread);

            var replyBox = NewInput("Reply", 0);
            replyBox.Margin = new Thickness(0, 10, 0, 0);
            panel.Children.Add(replyBox);
            var buttons = BuildPostCancel(Submit, () => { replyBox.Text = ""; Keyboard.Focus(this); }, out var post);
            buttons.Visibility = Visibility.Collapsed;
            panel.Children.Add(buttons);
            // Như Word: nút Post / Cancel chỉ hiện khi đang gõ reply.
            replyBox.TextChanged += (_, _) =>
            {
                buttons.Visibility = replyBox.Text.Length > 0 || replyBox.IsKeyboardFocused ? Visibility.Visible : Visibility.Collapsed;
                post.IsEnabled = replyBox.Text.Trim().Length > 0;
            };
            replyBox.GotKeyboardFocus += (_, _) => { buttons.Visibility = Visibility.Visible; post.IsEnabled = replyBox.Text.Trim().Length > 0; };
            replyBox.LostKeyboardFocus += (_, _) => { if (replyBox.Text.Length == 0) buttons.Visibility = Visibility.Collapsed; };
            replyBox.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; Submit(); }
            };
            Content = panel;
            _replyBox = replyBox;

            void Submit()
            {
                string text = replyBox.Text.Trim();
                if (text.Length == 0) return;
                replyBox.Text = "";
                ReplySubmitted?.Invoke(text);
                // Hiện ngay trong thẻ (bản ghi thật vào AnnotationStore đi song song, lần mở sau đọc từ đó).
                _thread.Children.Add(BuildItem(new CommentInfo(root.Path, root.Page, "", QuickAnnotationKind.Reply, Environment.UserName, DateTime.Now, text, false, root.Name), isRoot: false, editable: false));
                replyBox.Focus();
            }
        }

        private TextBox? _replyBox;

        private void CloseOnce()
        {
            if (_closed) return;
            _closed = true;
            Close();
        }

        // ── Dựng thẻ ──────────────────────────────────────────────────

        /// <summary>Ô nhập nhiều dòng có chữ mờ gợi ý (Enter = xuống dòng, Ctrl+Enter = Post như Word).</summary>
        private static TextBox NewInput(string placeholder, double minHeight)
        {
            var box = new TextBox
            {
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = Math.Max(28, minHeight), MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(4, 4, 4, 4),
                VerticalContentAlignment = VerticalAlignment.Top, Foreground = Res("Ui.Text"), Background = Res("Ui.Surface")
            };
            var hint = new VisualBrush(new TextBlock { Text = placeholder, Foreground = Res("Ui.Muted"), FontSize = 12, Margin = new Thickness(6, 4, 0, 0) })
            {
                AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, Stretch = Stretch.None
            };
            void Update() => box.Background = box.Text.Length == 0 ? hint : Res("Ui.Surface");
            box.TextChanged += (_, _) => Update();
            Update();
            return box;
        }

        private static FrameworkElement BuildPostCancel(Action post, Action cancel, out XTButton postButton)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var ok = new XTButton { Style = (Style)Application.Current.FindResource("UiPrimaryButton"), Text = "Post", Height = 28, Padding = new Thickness(14, 0, 14, 0), ToolTip = "Post (Ctrl+Enter)" };
            ok.Click += (_, _) => post();
            var no = new XTButton { Style = (Style)Application.Current.FindResource("UiGhostButton"), Text = "Cancel", Height = 28, Margin = new Thickness(6, 0, 0, 0) };
            no.Click += (_, _) => cancel();
            row.Children.Add(ok);
            row.Children.Add(no);
            postButton = ok;
            return row;
        }

        private static FrameworkElement BuildPersonHeader(string author, DateTime? date, double size)
        {
            string name = author.Length > 0 ? author : "Unknown";
            var (initials, avatar) = CommentsPanel.PersonStyle(name);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var circle = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = avatar, VerticalAlignment = VerticalAlignment.Top };
            circle.Child = new TextBlock { Text = initials, Foreground = Brushes.White, FontSize = size > 24 ? 11 : 9, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(circle);
            var namePanel = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            namePanel.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Foreground = Res("Ui.Text"), TextTrimming = TextTrimming.CharacterEllipsis });
            if (date is { } d)
                namePanel.Children.Add(new TextBlock { Text = d.ToString("MMMM d, yyyy 'at' h:mm tt"), FontSize = 10.5, Foreground = Res("Ui.Muted") });
            Grid.SetColumn(namePanel, 1);
            grid.Children.Add(namePanel);
            return grid;
        }

        /// <summary>1 mục của luồng (gốc hoặc reply): đầu thẻ + nội dung; bút chì / "…" để sửa, xoá (gốc có thêm Resolve).</summary>
        private FrameworkElement BuildItem(CommentInfo item, bool isRoot, bool editable = true)
        {
            var container = new StackPanel { Margin = new Thickness(isRoot ? 0 : 20, isRoot ? 0 : 10, 0, 0) };
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(BuildPersonHeader(item.Author, item.Date, isRoot ? 28 : 22));
            var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(tools, 1);
            head.Children.Add(tools);
            container.Children.Add(head);

            var body = new TextBlock { Text = item.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(isRoot ? 36 : 30, 4, 0, 0), Foreground = Res("Ui.Text") };
            container.Children.Add(body);
            if (!editable) return container;

            if (isRoot)
            {
                _chip = new Border
                {
                    CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 1), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 2, 0), ToolTip = "Click to change the status"
                };
                _chip.Child = _chipText = new TextBlock { FontSize = 11 };
                _chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; ToggleResolved(); };
                UpdateChip();
                tools.Children.Add(_chip);
            }

            XTButton IconButton(string icon, string tip)
            {
                var b = new XTButton { Style = (Style)Application.Current.FindResource("UiIconButton"), Icon = (Geometry)Application.Current.FindResource(icon), Width = 24, Height = 24, ToolTip = tip };
                tools.Children.Add(b);
                return b;
            }

            var edit = IconButton("Ui.Icon.pencil", "Edit");
            edit.Click += (_, _) => BeginEdit(container, body, item);
            var more = IconButton("Ui.Icon.more", "More thread actions");
            more.Click += (_, _) =>
            {
                var cm = new ContextMenu();
                var editItem = new MenuItem { Header = isRoot ? "Edit comment" : "Edit reply" };
                editItem.Click += (_, _) => BeginEdit(container, body, item);
                cm.Items.Add(editItem);
                var delete = new MenuItem { Header = isRoot ? "Delete thread" : "Delete reply" };
                delete.Click += (_, _) =>
                {
                    DeleteRequested?.Invoke(item);
                    if (isRoot) CloseOnce(); else _thread.Children.Remove(container);
                };
                cm.Items.Add(delete);
                if (isRoot)
                {
                    var resolve = new MenuItem { Header = _resolved ? "Reopen thread" : "Resolve thread" };
                    resolve.Click += (_, _) => ToggleResolved();
                    cm.Items.Add(resolve);
                }
                cm.PlacementTarget = more;
                cm.IsOpen = true;
            };
            return container;
        }

        /// <summary>Sửa tại chỗ như Word: nội dung biến thành ô nhập (sẵn chữ cũ) + Save / Cancel.</summary>
        private void BeginEdit(StackPanel container, TextBlock body, CommentInfo item)
        {
            if (container.Tag is "editing") return;
            container.Tag = "editing";
            int index = container.Children.IndexOf(body);
            body.Visibility = Visibility.Collapsed;
            var box = NewInput("", 28);
            box.Text = body.Text;
            box.Margin = new Thickness(body.Margin.Left, 6, 0, 0);
            var editor = new StackPanel();
            editor.Children.Add(box);
            var buttons = BuildPostCancel(Save, Done, out var save);
            save.Text = "Save";
            editor.Children.Add(buttons);
            container.Children.Insert(index + 1, editor);
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; Save(); }
                else if (e.Key == Key.Escape) { e.Handled = true; Done(); }
            };
            box.Focus();
            box.CaretIndex = box.Text.Length;

            void Save()
            {
                string text = box.Text.Trim();
                if (text.Length > 0 && text != item.Text)
                {
                    EditRequested?.Invoke(item, text);
                    body.Text = text;
                }
                Done();
            }

            void Done()
            {
                container.Children.Remove(editor);
                body.Visibility = Visibility.Visible;
                container.Tag = null;
            }
        }

        private void ToggleResolved()
        {
            _resolved = !_resolved;
            UpdateChip();
            ResolvedToggled?.Invoke(_resolved);
        }

        private void UpdateChip()
        {
            _chip.Background = Res(_resolved ? "Ui.Chip" : "Ui.AccentSoft");
            _chipText.Text = _resolved ? "Resolved" : "Open";
            _chipText.Foreground = Res(_resolved ? "Ui.Muted" : "Ui.Accent");
        }

        // ── Mở ────────────────────────────────────────────────────────

        /// <summary>
        /// Opens the card beside <paramref name="devicePoint"/> = the clicked point in screen DEVICE pixels, on the monitor that point is on.
        /// (It used to be placed in DIPs and clamped to the primary monitor's work area, so with the window dragged to the other screen the card
        /// jumped back to the primary one.)
        /// </summary>
        private static T Place<T>(T popup, Window owner, Point devicePoint) where T : Window
        {
            popup.Owner = owner;
            popup.WindowStartupLocation = WindowStartupLocation.Manual;
            popup.SourceInitialized += (_, _) => MoveNear(popup, devicePoint);
            popup.ContentRendered += (_, _) => MoveNear(popup, devicePoint);
            popup.SizeChanged += (_, _) => MoveNear(popup, devicePoint);
            popup.Show();
            return popup;
        }

        private static void MoveNear(Window popup, Point devicePoint)
        {
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(popup).Handle;
            if (hwnd == IntPtr.Zero || !NativeWindow.GetWindowRect(hwnd, out var rect)) return;
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            double k = VisualTreeHelper.GetDpi(popup).DpiScaleX;
            int x = (int)Math.Round(devicePoint.X + 12 * k), y = (int)Math.Round(devicePoint.Y + 12 * k);
            var monitor = NativeWindow.MonitorFromPoint(new NativeWindow.POINT { X = (int)devicePoint.X, Y = (int)devicePoint.Y }, 2 /* nearest */);
            var info = new NativeWindow.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeWindow.MONITORINFO>() };
            if (monitor != IntPtr.Zero && NativeWindow.GetMonitorInfo(monitor, ref info))
            {
                x = Math.Max(info.rcWork.Left, Math.Min(x, info.rcWork.Right - width));
                y = Math.Max(info.rcWork.Top, Math.Min(y, info.rcWork.Bottom - height));
            }
            if (x != rect.Left || y != rect.Top) NativeWindow.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010); // no size, no z-order, no activate
        }

        private static class NativeWindow
        {
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] internal struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
            [System.Runtime.InteropServices.DllImport("user32.dll")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
            [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(POINT point, uint flags);
            [System.Runtime.InteropServices.DllImport("user32.dll")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
            [System.Runtime.InteropServices.DllImport("user32.dll")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        }

        /// <summary>Mở luồng có sẵn cạnh điểm bấm (toạ độ màn hình), kẹp trong biên màn hình.</summary>
        public static CommentPopup Show(Window owner, Point devicePoint, CommentInfo root, IReadOnlyList<CommentInfo> replies, bool focusReply = false)
        {
            var popup = Place(new CommentPopup(root, replies), owner, devicePoint);
            if (focusReply)
                popup.Dispatcher.BeginInvoke(() => popup._replyBox?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
            return popup;
        }

        /// <summary>Mở thẻ soạn ghi chú mới cạnh điểm bấm.</summary>
        public static CommentPopup Compose(Window owner, Point devicePoint, string author)
            => Place(new CommentPopup(author), owner, devicePoint);
    }
}
