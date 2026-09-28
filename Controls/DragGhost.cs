using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace XTPdfMergeApp.Controls;

/// <summary>
/// Drag preview for page drags (mockup: stacked thumbnail + "Move N pages · hold Ctrl to copy"). A small transparent top-most window follows the cursor
/// while <see cref="DragDrop.DoDragDrop"/> runs; it is offset from the cursor so it never sits under the pointer.
/// </summary>
internal sealed class DragGhost : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);

    private readonly Window _window;
    private readonly TextBlock _hint;
    private readonly int _count;

    private DragGhost(ImageSource? thumbnail, int count)
    {
        _count = count;
        var stack = new Grid { Width = 92, Height = 72, Margin = new Thickness(0, 0, 0, 6) };
        for (int i = Math.Min(count, 3) - 1; i >= 0; i--)
        {
            var card = new Border
            {
                Width = 78, Height = 58, Background = Brushes.White, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(i * 6, i * 5, 0, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.3 }
            };
            card.SetResourceReference(Border.BorderBrushProperty, "Ui.Border");
            if (i == 0 && thumbnail != null) card.Child = new Image { Source = thumbnail, Stretch = Stretch.Uniform, Margin = new Thickness(2) };
            stack.Children.Add(card);
        }
        _hint = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontFamily = new FontFamily("Segoe UI") };
        var tip = new Border { Background = new SolidColorBrush(Color.FromRgb(26, 32, 44)), CornerRadius = new CornerRadius(5), Padding = new Thickness(9, 4, 9, 5), Child = _hint, HorizontalAlignment = HorizontalAlignment.Left };
        var panel = new StackPanel { Opacity = 0.95 };
        panel.Children.Add(stack);
        panel.Children.Add(tip);
        _window = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false,
            Topmost = true, SizeToContent = SizeToContent.WidthAndHeight, IsHitTestVisible = false, Content = panel, Focusable = false
        };
        Update(false);
        _window.Show();
    }

    private void Update(bool copy)
    {
        _hint.Text = $"{(copy ? "Copy" : "Move")} {_count} page{(_count == 1 ? "" : "s")} · hold Ctrl to copy";
        if (!GetCursorPos(out var point)) return;
        var source = PresentationSource.FromVisual(Application.Current.MainWindow!);
        var scale = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var position = scale.Transform(new Point(point.X, point.Y));
        _window.Left = position.X + 16;
        _window.Top = position.Y + 16;
    }

    public void Dispose() => _window.Close();

    /// <summary>Runs a page drag with the ghost preview; returns what DoDragDrop returns.</summary>
    public static DragDropEffects Run(DependencyObject source, DataObject data, ImageSource? thumbnail, int count)
    {
        using var ghost = new DragGhost(thumbnail, Math.Max(1, count));
        void OnFeedback(object? s, GiveFeedbackEventArgs e)
        {
            ghost.Update((e.Effects & DragDropEffects.Copy) != 0 && (Keyboard.Modifiers & ModifierKeys.Control) != 0);
            e.UseDefaultCursors = true;
            e.Handled = true;
        }
        var element = (IInputElement)source;
        element.AddHandler(DragDrop.GiveFeedbackEvent, new GiveFeedbackEventHandler(OnFeedback));
        try { return DragDrop.DoDragDrop(source, data, DragDropEffects.Move | DragDropEffects.Copy); }
        finally { element.RemoveHandler(DragDrop.GiveFeedbackEvent, new GiveFeedbackEventHandler(OnFeedback)); }
    }
}
