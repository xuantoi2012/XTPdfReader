using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XTStyle.Controls;

namespace XTPdfMergeApp.Controls;

/// <summary>Shared, themed modal messages. Closing a confirmation never accepts the operation.</summary>
internal static class AppDialog
{
    public static MessageBoxResult Show(Window? owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var dispatcher = owner?.Dispatcher ?? Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            return dispatcher.Invoke(() => Show(owner, message, title, buttons, image, defaultResult));
        var dialog = new AppDialogWindow(message, title, buttons, image, defaultResult);
        if (owner?.IsVisible == true) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog.Result;
    }

    public static bool ConfirmSignedPdf(Window? owner, string message)
    {
        var dispatcher = owner?.Dispatcher ?? Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            return dispatcher.Invoke(() => ConfirmSignedPdf(owner, message));
        var dialog = new AppDialogWindow(message, "Digitally signed PDF", MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (owner?.IsVisible == true) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog.Result == MessageBoxResult.Yes;
    }
}

internal sealed class AppDialogWindow : XTWindow
{
    internal MessageBoxResult Result { get; private set; }
    internal IReadOnlyList<XTButton> ActionButtons => _buttons;
    private readonly List<XTButton> _buttons = new();

    internal AppDialogWindow(string message, string title, MessageBoxButton buttons, MessageBoxImage image,
        MessageBoxResult defaultResult = MessageBoxResult.None, string? acceptLabel = null)
    {
        message = XTPdfMergeApp.Services.Loc.T(message);
        title = XTPdfMergeApp.Services.Loc.T(title);
        Title = "PDF Reader Pro";
        TitleBarMode = TitleBarMode.Dialog;
        TitleIcon = TryFindResource("App.Icon.Logo");
        TitleIconBrush = new SolidColorBrush(Color.FromRgb(255, 112, 24));
        Width = Math.Min(480, SystemParameters.WorkArea.Width - 32);
        MaxHeight = Math.Max(180, SystemParameters.WorkArea.Height - 48);
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12.5;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        SetResourceReference(BackgroundProperty, "Ui.Surface");
        SetResourceReference(ForegroundProperty, "Ui.Text");

        Result = buttons switch
        {
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.OK => MessageBoxResult.OK,
            _ => MessageBoxResult.Cancel
        };
        if (defaultResult == MessageBoxResult.None) defaultResult = Result;

        var root = new DockPanel();
        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 12, 18, 12) };
        footer.SetResourceReference(Border.BackgroundProperty, "Ui.Bg");
        footer.SetResourceReference(Border.BorderBrushProperty, "Ui.Border");
        DockPanel.SetDock(footer, Dock.Bottom);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Child = actions;
        root.Children.Add(footer);

        void AddButton(string label, MessageBoxResult result, bool primary, bool cancel = false)
        {
            var button = new XTButton
            {
                Text = label, MinWidth = 80, Height = 30, Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(_buttons.Count == 0 ? 0 : 8, 0, 0, 0), IsDefault = result == defaultResult, IsCancel = cancel
            };
            if (TryFindResource(primary ? "UiPrimaryButton" : "UiGhostButton") is Style style) button.Style = style;
            button.Click += (_, _) => { Result = result; DialogResult = result == MessageBoxResult.OK || result == MessageBoxResult.Yes; };
            _buttons.Add(button);
            actions.Children.Add(button);
        }
        switch (buttons)
        {
            case MessageBoxButton.OK: AddButton(acceptLabel ?? "OK", MessageBoxResult.OK, true); break;
            case MessageBoxButton.OKCancel:
                AddButton(acceptLabel ?? "OK", MessageBoxResult.OK, true);
                AddButton("Cancel", MessageBoxResult.Cancel, false, true); break;
            case MessageBoxButton.YesNo:
                AddButton("Yes", MessageBoxResult.Yes, true);
                AddButton("No", MessageBoxResult.No, false); break;
            case MessageBoxButton.YesNoCancel:
                AddButton("Yes", MessageBoxResult.Yes, true);
                AddButton("No", MessageBoxResult.No, false);
                AddButton("Cancel", MessageBoxResult.Cancel, false, true); break;
            default: throw new ArgumentOutOfRangeException(nameof(buttons));
        }

        var body = new Grid { Margin = new Thickness(18) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        var badge = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        badge.SetResourceReference(Border.BackgroundProperty, image == MessageBoxImage.Warning ? "Ui.WarnSoft" : "Ui.AccentSoft");
        var icon = new System.Windows.Shapes.Path { Data = CreateIcon(image), Width = 20, Height = 20, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        icon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            image == MessageBoxImage.Error ? "Ui.Danger" : image == MessageBoxImage.Warning ? "Ui.Warn" : "Ui.Accent");
        badge.Child = icon;
        body.Children.Add(badge);
        var text = new StackPanel();
        Grid.SetColumn(text, 1);
        var heading = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Text");
        text.Children.Add(heading);
        var detail = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Muted");
        text.Children.Add(new ScrollViewer
        {
            Content = detail, Margin = new Thickness(0, 8, 0, 0),
            MaxHeight = Math.Max(60, Math.Min(280, SystemParameters.WorkArea.Height - 210)),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        body.Children.Add(text);
        root.Children.Add(body);
        Content = root;
        Loaded += (_, _) =>
        {
            if (Owner == null)
            {
                Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - ActualWidth) / 2;
                Top = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - ActualHeight) / 2;
            }
            foreach (var button in _buttons) if (button.IsDefault) { button.Focus(); break; }
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    private static Geometry CreateIcon(MessageBoxImage image) => Geometry.Parse(image switch
    {
        MessageBoxImage.Warning => "M12,2 L23,22 H1 Z M11,8 H13 V15 H11 Z M11,17 H13 V19 H11 Z",
        MessageBoxImage.Error => "F0 M12,1 A11,11 0 1 1 12,23 A11,11 0 1 1 12,1 M7,6 L6,7 L11,12 L6,17 L7,18 L12,13 L17,18 L18,17 L13,12 L18,7 L17,6 L12,11 Z",
        _ => "F0 M12,1 A11,11 0 1 1 12,23 A11,11 0 1 1 12,1 M11,10 H13 V18 H11 Z M11,6 H13 V8 H11 Z"
    });
}
