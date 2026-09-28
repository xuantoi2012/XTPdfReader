using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace XTPdfMergeApp.Controls
{
    /// <summary>1 mục trong bảng lệnh: nhóm, tên, phím tắt hiển thị, icon (Geometry) và việc cần làm.</summary>
    public sealed record PaletteItem(string Group, string Title, string Shortcut, Geometry Icon, Action Run);

    /// <summary>Bảng lệnh Ctrl+K (docs/UI_REDESIGN.md, mockup 13): gõ để lọc lệnh + file đang mở + Recent, ↑↓ chọn, Enter chạy, Esc đóng.</summary>
    public partial class CommandPalette : UserControl
    {
        private IReadOnlyList<PaletteItem> _all = Array.Empty<PaletteItem>();

        public CommandPalette() => InitializeComponent();

        internal bool IsOpen => Visibility == Visibility.Visible;

        internal void Open(IReadOnlyList<PaletteItem> items)
        {
            _all = items;
            Query.Text = "";
            Visibility = Visibility.Visible;
            Refresh();
            Dispatcher.BeginInvoke(new Action(() => Query.Focus()), System.Windows.Threading.DispatcherPriority.Input);
        }

        internal void Close() => Visibility = Visibility.Collapsed;

        private void Refresh()
        {
            string[] words = Query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var found = _all.Where(i => words.All(w => i.Title.Contains(w, StringComparison.OrdinalIgnoreCase) || i.Group.StartsWith(w, StringComparison.OrdinalIgnoreCase)))
                            .Take(60).ToList();
            var view = new ListCollectionView(found);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PaletteItem.Group)));
            Rows.ItemsSource = view;
            if (found.Count > 0) Rows.SelectedIndex = 0;
        }

        private void Query_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

        private void Query_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    Close();
                    e.Handled = true;
                    break;
                case Key.Down:
                case Key.Up:
                    int n = Rows.Items.Count;
                    if (n > 0)
                    {
                        int i = Rows.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                        Rows.SelectedIndex = (i + n) % n;
                        Rows.ScrollIntoView(Rows.SelectedItem);
                    }
                    e.Handled = true;
                    break;
                case Key.Enter:
                    RunSelected();
                    e.Handled = true;
                    break;
            }
        }

        private void RunSelected()
        {
            if (Rows.SelectedItem is not PaletteItem item) return;
            Close();
            // Chạy sau khi bảng đã đóng để hộp thoại/tab mới nhận focus đúng.
            Dispatcher.BeginInvoke(item.Run, System.Windows.Threading.DispatcherPriority.Input);
        }

        private void Rows_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            while (source != null && source is not ListBoxItem) source = VisualTreeHelper.GetParent(source);
            if (source is ListBoxItem { DataContext: PaletteItem } item)
            {
                item.IsSelected = true;
                RunSelected();
            }
        }

        private void Backdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

        private void Panel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
    }
}
