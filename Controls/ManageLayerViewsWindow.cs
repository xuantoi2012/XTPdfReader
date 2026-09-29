using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using XTPdfMergeApp.Services;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>Quản lý các View layer đã lưu: đổi tên, xoá.</summary>
internal sealed class ManageLayerViewsWindow : XTWindow
{
    private readonly ListBox _list;
    private readonly XTButton _rename, _delete;

    public ManageLayerViewsWindow()
    {
        Title = "Manage layer views";
        TitleBarMode = TitleBarMode.Dialog;
        Width = 380;
        Height = 360;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 13;

        _list = new ListBox { Height = 210, Margin = new Thickness(0, 8, 0, 0) };
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _rename = new XTButton { Text = "Rename…", Width = 96, Height = 32, Margin = new Thickness(0, 0, 8, 0) };
        _rename.Click += (_, _) => Rename();
        _delete = new XTButton { Text = "Delete", Width = 84, Height = 32, Margin = new Thickness(0, 0, 8, 0) };
        _delete.Click += (_, _) => Delete();
        var close = new XTButton { Text = "Close", Width = 84, Height = 32, IsCancel = true, IsDefault = true };
        close.Click += (_, _) => DialogResult = true;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(_rename);
        buttons.Children.Add(_delete);
        buttons.Children.Add(close);

        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
        panel.Children.Add(new TextBlock { Text = "Saved views (shared by all files, matched by layer name):", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_list);
        panel.Children.Add(buttons);
        Content = panel;
        Reload();
    }

    private string? Selected => _list.SelectedItem as string;

    private void Reload(string? select = null)
    {
        _list.ItemsSource = LayerViewStore.Views.Select(v => v.Name).ToList();
        if (select != null) _list.SelectedItem = select;
        UpdateButtons();
    }

    private void UpdateButtons() => _rename.IsEnabled = _delete.IsEnabled = Selected != null;

    private void Rename()
    {
        if (Selected is not { } old) return;
        string? name = TextPromptWindow.Ask(this, "Rename view", "New name:", old, text =>
            !string.Equals(text, old, StringComparison.OrdinalIgnoreCase) && LayerViewStore.Exists(text) ? "A view with this name already exists." : null);
        if (name == null) return;
        LayerViewStore.Rename(old, name);
        Reload(name);
    }

    private void Delete()
    {
        if (Selected is not { } name) return;
        if (MessageBox.Show(this, $"Delete the view \"{name}\"?", "Delete view", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        LayerViewStore.Remove(name);
        Reload();
    }
}
