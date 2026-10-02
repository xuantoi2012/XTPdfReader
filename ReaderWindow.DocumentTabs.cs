using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DocumentGroup = XTPdfMergeApp.Domain.WorkspaceDocument;

namespace XTPdfMergeApp;

public partial class ReaderWindow
{
    private const double MinimumDocumentTabWidth = 150;
    private readonly ObservableCollection<DocumentGroup> _visibleDocumentTabs = new();
    private int _tabWindowStart;

    private void InitializeDocumentTabs()
    {
        ReaderDocumentTabs.ItemsSource = _visibleDocumentTabs;
        Loaded += (_, _) => UpdateDocumentTabs(_readerGroup);
    }

    private void ReaderDocumentTabsStrip_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_groups != null) UpdateDocumentTabs(_readerGroup);
    }

    private void UpdateDocumentTabs(DocumentGroup? active = null)
    {
        double available = Math.Max(0, ReaderDocumentTabsStrip.ActualWidth - ReaderStartButton.Width - ReaderAddTabButton.Width);
        bool overflow = _groups.Count * MinimumDocumentTabWidth > available;
        int capacity = Math.Max(1, (int)Math.Floor((available - (overflow ? ReaderTabsOverflowButton.Width : 0)) / MinimumDocumentTabWidth));
        int count = Math.Min(_groups.Count, capacity);
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
            ReaderTabsOverflowButton.Visibility = _groups.Count > count ? Visibility.Visible : Visibility.Collapsed;
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
}
