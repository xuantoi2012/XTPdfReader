using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>Cheat sheet phím tắt có thật trong ReaderWindow; mở bằng F1 để người dùng không phải nhớ command palette.</summary>
internal sealed class KeyboardShortcutsWindow : XTWindow
{
    private sealed record Shortcut(string Group, string Keys, string Action);

    private static readonly Shortcut[] Items =
    [
        new("Files", "Ctrl+O", "Open PDF files"),
        new("Files", "Ctrl+N", "New blank PDF"),
        new("Files", "Ctrl+S", "Save"),
        new("Files", "Ctrl+Shift+S", "Save as"),
        new("Files", "Ctrl+P", "Print"),
        new("Find & commands", "Ctrl+F", "Find in document"),
        new("Find & commands", "F3 / Shift+F3", "Next / previous search match"),
        new("Find & commands", "Ctrl+K", "Command palette"),
        new("View", "+ / −", "Zoom in / out"),
        new("View", "0", "Fit width"),
        new("View", "F11", "Full screen"),
        new("View", "PgUp / PgDn", "Previous / next page"),
        new("Editing", "Ctrl+Z / Ctrl+Y", "Undo / redo page or annotation changes"),
        new("Editing", "Del", "Delete selected annotation or page"),
        new("Editing", "Esc", "Leave the current tool / clear annotation selection"),
        new("Help", "F1", "This keyboard shortcuts window")
    ];

    public KeyboardShortcutsWindow()
    {
        Title = "Keyboard shortcuts";
        TitleBarMode = TitleBarMode.Tool;
        Width = 570;
        Height = 590;
        MinWidth = 420;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        Background = (Brush)Application.Current.FindResource("Ui.Bg");

        var list = new ListView { BorderThickness = new Thickness(0), Background = Brushes.Transparent, ItemsSource = Items, Margin = new Thickness(24, 0, 24, 0) };
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "Group", Width = 125, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Shortcut.Group)) });
        view.Columns.Add(new GridViewColumn { Header = "Shortcut", Width = 135, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Shortcut.Keys)) });
        view.Columns.Add(new GridViewColumn { Header = "Action", Width = 245, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Shortcut.Action)) });
        list.View = view;

        var close = new XTButton { Text = "Close", Width = 84, Height = 32, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 14, 24, 20) };
        buttons.Children.Add(close);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(list);
        Content = root;
    }

    internal static void ShowFor(Window owner) => new KeyboardShortcutsWindow { Owner = owner }.ShowDialog();
}
