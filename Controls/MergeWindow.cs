using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>The Merge screen in its own window (mockup 6). Closing hides it; opening again creates a fresh draft from the reader tabs.</summary>
internal sealed class MergeWindow : XTWindow
{
    private bool _reallyClose;
    private readonly XTButton _undoButton;
    private readonly XTButton _redoButton;

    public MergeView View { get; } = new();
    internal bool IsShowing => IsVisible;

    public MergeWindow()
    {
        Title = "PDF Reader Pro — Merge files";
        TitleBarMode = TitleBarMode.Full;
        Width = 1240;
        Height = 800;
        MinWidth = 900;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TitleIcon = Application.Current.FindResource("App.Icon.Logo");
        TitleIconBrush = (Brush)Application.Current.FindResource("Ui.Accent");
        Background = (Brush)Application.Current.FindResource("Ui.Bg");
        Content = View;

        _undoButton = CreateTitleButton("Ui.Icon.undo", "Undo");
        _redoButton = CreateTitleButton("Ui.Icon.redo", "Redo");
        _undoButton.Click += (_, _) => View.UndoDraft();
        _redoButton.Click += (_, _) => View.RedoDraft();
        TitleBarLeftContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            Children = { _undoButton, _redoButton }
        };
        View.HistoryStateChanged += (_, _) => UpdateHistoryButtons();
        UpdateHistoryButtons();
    }

    private XTButton CreateTitleButton(string iconResource, string label)
        => new()
        {
            Style = (Style)Application.Current.FindResource("UiIconButton"),
            Icon = (Geometry)Application.Current.FindResource(iconResource),
            Width = 28,
            Height = 28,
            Margin = new Thickness(1, 0, 1, 0),
            ToolTip = label
        };

    private void UpdateHistoryButtons()
    {
        _undoButton.IsEnabled = View.CanUndo;
        _redoButton.IsEnabled = View.CanRedo;
        _undoButton.ToolTip = View.UndoDescription is { } undo ? $"Undo: {undo}" : "Undo";
        _redoButton.ToolTip = View.RedoDescription is { } redo ? $"Redo: {redo}" : "Redo";
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Trong ô nhập (Go to…), Ctrl+Z/Ctrl+Y phải để WPF xử lý text như thông thường.
        if (Keyboard.FocusedElement is not TextBoxBase && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.Z && View.CanUndo) { View.UndoDraft(); e.Handled = true; return; }
            if (e.Key == Key.Y && View.CanRedo) { View.RedoDraft(); e.Handled = true; return; }
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>Really closes (application exit).</summary>
    public void CloseForReal()
    {
        _reallyClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }
}
