using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XTPdfMergeApp.Controls;

internal sealed class DragGhostAdorner : Adorner
{
    private readonly ImageSource? _image;
    private readonly string _label;
    private Point _position;
    private bool _copy;

    public DragGhostAdorner(UIElement adornedElement, ImageSource? image, string label) : base(adornedElement)
    {
        _image = image;
        _label = label;
        IsHitTestVisible = false;
    }

    public void Update(Point position, bool copy)
    {
        _position = position;
        _copy = copy;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        const double width = 150;
        const double height = 112;
        var rect = new Rect(_position.X + 18, _position.Y + 18, width, height);
        drawingContext.PushOpacity(0.86);
        drawingContext.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(245, 248, 252)),
            new Pen(new SolidColorBrush(Color.FromRgb(3, 109, 246)), 2), rect, 7, 7);
        if (_image != null)
            drawingContext.DrawImage(_image, new Rect(rect.X + 7, rect.Y + 7, width - 14, height - 34));
        drawingContext.Pop();

        var text = new FormattedText(
            _copy ? $"Sao chép · {_label}" : _label,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Semibold"), 11,
            new SolidColorBrush(Color.FromRgb(25, 42, 62)), 1.0);
        drawingContext.DrawText(text, new Point(rect.X + 8, rect.Bottom - 23));
    }
}
