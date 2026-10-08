using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XTCapture
{
    /// <summary>
    /// Shows the markup of a <see cref="MarkupController"/> in picture pixels, with the selected object outlined and its handles. It takes no input: the host (the
    /// overlay or the capture editor) gives the controller the mouse positions. Put it in a canvas whose units are picture pixels.
    /// </summary>
    internal sealed class MarkupCanvas : FrameworkElement
    {
        private readonly MarkupRenderer _renderer = new();
        private MarkupController? _controller;

        /// <summary>The picture under the markup (a mosaic is made from it).</summary>
        public BitmapSource? Original { get; set; }

        /// <summary>Picture pixels per screen pixel's worth of handle: a handle is drawn 9 units wide times this (the host sets 1 / zoom, or the screen scale).</summary>
        public double HandleScale { get; set; } = 1;

        public MarkupController? Controller
        {
            get => _controller;
            set
            {
                if (_controller != null) _controller.Changed -= OnChanged;
                _controller = value;
                if (_controller != null) _controller.Changed += OnChanged;
                InvalidateVisual();
            }
        }

        public MarkupCanvas() => IsHitTestVisible = false;

        private void OnChanged() => InvalidateVisual();

        protected override void OnRender(DrawingContext dc)
        {
            if (_controller == null) return;
            var items = _controller.Draft == null ? _controller.Items : _controller.Items.Append(_controller.Draft);
            _renderer.Draw(dc, items, Original, _controller.EditingTextId);
            DrawSelection(dc);
        }

        private void DrawSelection(DrawingContext dc)
        {
            if (_controller?.Selected is not { } item || _controller.EditingTextId != null) return;
            var box = MarkupRenderer.Bounds(item);
            double pad = item.Kind is MarkupKind.Rectangle or MarkupKind.Ellipse or MarkupKind.Arrow or MarkupKind.Line or MarkupKind.Pen ? item.Style.Width / 2 + 2 * HandleScale : 2 * HandleScale;
            box.Inflate(pad, pad);
            var accent = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
            var dashed = new Pen(accent, Math.Max(1, HandleScale)) { DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0) };
            dc.DrawRectangle(null, new Pen(Brushes.White, Math.Max(1, HandleScale) * 3), box);
            dc.DrawRectangle(null, dashed, box);
            double size = 9 * HandleScale;
            var handlePen = new Pen(accent, 1.5 * HandleScale);
            foreach (var (_, at) in MarkupGeometry.Handles(item))
                dc.DrawRectangle(Brushes.White, handlePen, new Rect(at.X - size / 2, at.Y - size / 2, size, size));
        }
    }
}
