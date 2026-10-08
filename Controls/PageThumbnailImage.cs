using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XTPdfMergeApp.Services;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;

namespace XTPdfMergeApp.Controls
{
    /// <summary>
    /// Page thumbnail (DataContext = the page) with its annotations drawn over it: pages are rendered without annotations, so
    /// thumbnails show unsaved edits the moment they are made, like the viewer (<see cref="AnnotationLayer"/>).
    /// </summary>
    public sealed class PageThumbnailImage : Image
    {
        private static readonly HashSet<PageThumbnailImage> _live = new();

        public PageThumbnailImage()
        {
            Loaded += (_, _) => _live.Add(this);
            Unloaded += (_, _) => _live.Remove(this);
        }

        /// <summary>Annotations of (normalized path, page) changed — redraw the thumbnails showing it (page 0 = every page of the file).</summary>
        internal static void Invalidate(string path, int page)
        {
            foreach (var image in _live)
                if (image.DataContext is PageRow row && (page == 0 || row.PageNumber == page) &&
                    string.Equals(AnnotationStore.Normalize(row.SourcePath), path, StringComparison.OrdinalIgnoreCase))
                    image.InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (DataContext is not PageRow row || Source is not BitmapSource bitmap || RenderSize.Width <= 0 || RenderSize.Height <= 0) return;
            if (BlankPageService.IsBlankFile(row.SourcePath)) return;
            var frame = new Rect(RenderSize);
            int rotation = (((row.Rotation + PageRotationPendingStore.Delta(row.SourcePath, row.PageNumber)) % 360) + 360) % 360; // turns that wait for Save count
            var bases = new[] { new AnnotationLayer.BaseImage(bitmap, new Rect(0, 0, 1, 1)) };
            if (rotation != 0)
            {
                // The bitmap is turned with the page: draw in the page's own orientation (no multiply — the pixels are turned).
                dc.PushTransform(new RotateTransform(rotation, frame.Width / 2, frame.Height / 2));
                if (rotation is 90 or 270)
                    frame = new Rect((frame.Width - frame.Height) / 2, (frame.Height - frame.Width) / 2, frame.Height, frame.Width);
                AnnotationLayer.Draw(dc, row, frame, VisualTreeHelper.GetDpi(this).PixelsPerDip, Array.Empty<AnnotationLayer.BaseImage>(), live: false);
                dc.Pop();
                return;
            }
            AnnotationLayer.Draw(dc, row, frame, VisualTreeHelper.GetDpi(this).PixelsPerDip, bases, live: false);
        }
    }
}
