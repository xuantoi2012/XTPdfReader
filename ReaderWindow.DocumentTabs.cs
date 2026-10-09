using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DocumentGroup = XTPdfMergeApp.Domain.WorkspaceDocument;

namespace XTPdfMergeApp;

/// <summary>Khung chứa thanh tab: đo cho cha là rộng 0 (chiều cao theo con) nhưng xếp con đúng bề ngang được cấp. Nhờ vậy tab (bề ngang tự tính)
/// không làm phình hàng chứa nó, và ActualWidth của host luôn là chỗ trống thật của cửa sổ.</summary>
public sealed class ReaderTabsHost : System.Windows.Controls.Decorator
{
    protected override Size MeasureOverride(Size constraint)
    {
        if (Child == null) return new Size(0, 0);
        Child.Measure(constraint);
        return new Size(0, Child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(0, 0, arrangeSize.Width, arrangeSize.Height));
        return arrangeSize;
    }
}

public partial class ReaderWindow
{
    /// <summary>Tab rộng nhất / hẹp nhất. Ít file thì mỗi tab rộng tối đa; nhiều file thì chia đều đúng bề ngang còn lại, hết chỗ thì bớt số tab hiện (phần còn lại vào nút danh sách).</summary>
    private const double MaximumDocumentTabWidth = 200;
    private const double MinimumDocumentTabWidth = 130;
    private readonly ObservableCollection<DocumentGroup> _visibleDocumentTabs = new();
    private int _tabWindowStart;

    private void InitializeDocumentTabs()
    {
        ReaderDocumentTabs.ItemsSource = _visibleDocumentTabs;
        ReaderDocumentTabs.PreviewMouseMove += ReaderTabs_PreviewMouseMove;
        ReaderDocumentTabs.PreviewMouseLeftButtonUp += ReaderTabs_PreviewMouseLeftButtonUp;
        Loaded += (_, _) => UpdateDocumentTabs(_readerGroup);
    }

    private void ReaderTabsHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_groups != null) UpdateDocumentTabs(_readerGroup);
    }

    private void UpdateDocumentTabs(DocumentGroup? active = null)
    {
        // Nút + nằm sát tab cuối. Nút danh sách (mép phải) chỉ hiện khi tab không đủ chỗ.
        // Chỗ cho tab + nút + = bề ngang host, cộng lại phần nút danh sách nếu nó đang chiếm.
        double listWidth = ReaderTabsOverflowButton.Width;
        double total = ReaderTabsHost.ActualWidth + (ReaderTabsOverflowButton.Visibility == Visibility.Visible ? listWidth : 0);
        double spaceWithoutList = Math.Max(0, total - ReaderAddTabButton.Width);
        bool overflow = _groups.Count * MinimumDocumentTabWidth > spaceWithoutList;
        double available = Math.Max(0, spaceWithoutList - (overflow ? listWidth : 0));
        int capacity = Math.Max(1, (int)Math.Floor(available / MinimumDocumentTabWidth));
        int count = Math.Min(_groups.Count, capacity);
        // Chiều rộng ListBox = đúng phần còn lại (tối đa MaximumDocumentTabWidth mỗi tab); UniformGrid chia đều → không có khoảng thừa.
        ReaderDocumentTabs.Width = _groups.Count == 0 ? 0 : Math.Max(MinimumDocumentTabWidth, Math.Min(available, count * MaximumDocumentTabWidth));
        ReaderTabsOverflowButton.Visibility = _groups.Count > count ? Visibility.Visible : Visibility.Collapsed;
        _tabWindowStart = Math.Clamp(_tabWindowStart, 0, Math.Max(0, _groups.Count - count));
        int index = active == null ? -1 : _groups.IndexOf(active);
        if (index >= 0)
        {
            if (index < _tabWindowStart) _tabWindowStart = index;
            else if (index >= _tabWindowStart + count) _tabWindowStart = index - count + 1;
        }

        bool syncing = _syncingDocumentTabs;
        _syncingDocumentTabs = true;
        try
        {
            var visible = _groups.Skip(_tabWindowStart).Take(count).ToArray();
            if (!_visibleDocumentTabs.SequenceEqual(visible))
            {
                _visibleDocumentTabs.Clear();
                foreach (var group in visible) _visibleDocumentTabs.Add(group);
            }
                        ReaderDocumentTabs.SelectedItem = StartPage.Visibility == Visibility.Visible ? null : active ?? _readerGroup;
        }
        finally { _syncingDocumentTabs = syncing; }
    }

    private void SelectDocumentTab(DocumentGroup group)
    {
        if (!_groups.Contains(group)) return;
        ShowStart(false);
        UpdateDocumentTabs(group);
        if (!ReferenceEquals(_readerGroup, group)) ShowGroup(group);
        ReaderDocumentTabs.Focus();
    }

    private void ReaderTabsOverflow_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ReaderTabsOverflowButton, Placement = PlacementMode.Bottom, MaxHeight = 480 };
        foreach (var group in _groups)
        {
            var title = new TextBlock { MaxWidth = 360, TextTrimming = TextTrimming.CharacterEllipsis };
            title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(DocumentGroup.FileName)) { Source = group });
            var item = new MenuItem
            {
                Header = title, ToolTip = group.SourcePath,
                IsCheckable = true, IsChecked = ReferenceEquals(group, _readerGroup) && StartPage.Visibility != Visibility.Visible
            };
            item.Click += (_, _) => SelectDocumentTab(group);
            menu.Items.Add(item);
        }
        ReaderTabsOverflowButton.ContextMenu = menu;
        menu.Closed += (_, _) => ReaderTabsOverflowButton.ContextMenu = null;
        menu.IsOpen = true;
    }

    private bool HandleDocumentTabKey(KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return false;
        if (e.Key == Key.Tab && _groups.Count > 0)
        {
            int current = _readerGroup == null ? -1 : _groups.IndexOf(_readerGroup);
            int direction = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1;
            int next = (current + direction + _groups.Count) % _groups.Count;
            SelectDocumentTab(_groups[next]);
            return true;
        }
        if (e.Key == Key.W && _readerGroup != null && StartPage.Visibility != Visibility.Visible)
        {
            ReaderCloseDocument_Click(new FrameworkElement { DataContext = _readerGroup }, new RoutedEventArgs());
            return true;
        }
        return false;
    }

    // ── Menu chuột phải của tab (tab được chọn trước khi menu mở, xem ReaderTab_PreviewMouseRightButtonDown) ──

    private string? CurrentTabPath => _readerGroup?.SourcePath is { Length: > 0 } path && System.IO.File.Exists(path) ? path : null;

    private void ReaderTabOpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentTabPath is not { } path) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { }
    }

    private void ReaderTabCopyFile_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentTabPath is not { } path) return;
        try
        {
            var files = new System.Collections.Specialized.StringCollection { path };
            Clipboard.SetFileDropList(files);
        }
        catch { }
    }

    private void ReaderTabCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentTabPath is not { } path) return;
        try { Clipboard.SetText($"\"{path}\""); } // như "Copy as path" của Explorer: có dấu nháy
        catch { }
    }

    private async void ReaderTabCloseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var group in _groups.ToList())
            if (!await CloseGroupAsync(group)) return; // huỷ ở 1 file thì dừng, các file đã đóng vẫn đóng
    }
}
