using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using XTPdfMergeApp.Services;
using XTPdfMergeApp.Services.TextEdit;
using XTStyle.Controls;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;

namespace XTPdfMergeApp
{
    /// <summary>
    /// Edit Object: for drawings (letters are strokes) and scans, where there is no text to edit. Click a line, a shape or an image to select it (Shift adds more), or drag an area
    /// to take everything inside it; then Delete removes it from the file (Undo brings it back), Ctrl+C copies the picture of it. A frame is hit on its edge, so what it frames stays.
    /// </summary>
    public partial class ReaderWindow
    {
        private sealed record ObjectSelection(PageRow Row, List<PdfObjectRef> Objects);
        private sealed record ObjectDrag(PageHit Start);

        private ObjectSelection? _objectSelection;
        private ObjectDrag? _objectDrag;
        private bool _objectAreaMode;
        private bool _objectBusy;
        private readonly List<UIElement> _objectVisuals = new();

        private void ReaderEditObject_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = ReaderEditObjectButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            void Add(string header, string gesture, Action click, bool enabled = true)
            {
                var item = new MenuItem { Header = header, InputGestureText = gesture, IsEnabled = enabled };
                item.Click += (_, _) => click();
                menu.Items.Add(item);
            }
            Add("Select object (click a line, shape or image)", "", () => StartObjectTool(areaMode: false));
            Add("Select area (drag; takes everything inside)", "", () => StartObjectTool(areaMode: true));
            menu.Items.Add(new Separator());
            bool any = _objectSelection is { Objects.Count: > 0 };
            Add("Delete selected", "Del", () => _ = DeleteSelectedObjectsAsync(), any);
            Add("Copy selected as picture", "Ctrl+C", () => _ = CopySelectedObjectsAsync(), any);
            menu.IsOpen = true;
        }

        internal void StartObjectTool(bool areaMode)
        {
            _objectAreaMode = areaMode;
            if (_readerTool == ReaderTool.EditObject) { ClearObjectSelection(); ApplyToolCursor(); return; }
            SetReaderTool(ReaderTool.EditObject);
        }

        private void ClearObjectSelection()
        {
            foreach (var visual in _objectVisuals) ReaderInteractionLayer.Children.Remove(visual);
            _objectVisuals.Clear();
            _objectSelection = null;
        }

        private async Task BeginObjectPickAsync(PageHit hit)
        {
            if (_objectBusy) return;
            _objectBusy = true;
            try
            {
                var page = await LoadPageAnnotationsAsync(hit.Row);
                if (page == null || _readerTool != ReaderTool.EditObject) return;
                double x = hit.U * page.Geometry.DisplayWidth, y = hit.V * page.Geometry.DisplayHeight;
                IReadOnlyList<PdfObjectRef> found;
                try { found = await ObjectEditService.PickAsync(hit.Row.SourcePath, hit.Row.PageNumber, x, y); }
                catch (Exception ex) { XTGrowl.Info(ex.Message, this); return; }
                if (found.Count == 0)
                {
                    XTGrowl.Info("Nothing drawn there. Click right on a line, a shape's edge or an image.", this);
                    return;
                }
                bool add = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _objectSelection != null && _objectSelection.Row.PageNumber == hit.Row.PageNumber
                           && string.Equals(_objectSelection.Row.SourcePath, hit.Row.SourcePath, StringComparison.OrdinalIgnoreCase);
                var chosen = found[0];
                var objects = add ? _objectSelection!.Objects : new List<PdfObjectRef>();
                int existing = objects.FindIndex(o => o.Kind == chosen.Kind && o.Index == chosen.Index);
                if (existing >= 0) objects.RemoveAt(existing); else objects.Add(chosen);
                ShowObjectSelection(hit.Row, objects);
            }
            finally { _objectBusy = false; }
        }

        private void ShowObjectSelection(PageRow row, List<PdfObjectRef> objects)
        {
            ClearObjectSelection();
            if (objects.Count == 0) return;
            _objectSelection = new ObjectSelection(row, objects);
            foreach (var o in objects)
            {
                if (!TryPageToLayer(row, o.X0 / o.PageWidth, o.Y0 / o.PageHeight, out Point a) || !TryPageToLayer(row, o.X1 / o.PageWidth, o.Y1 / o.PageHeight, out Point b)) continue;
                double x = Math.Min(a.X, b.X) - 3, y = Math.Min(a.Y, b.Y) - 3;
                var box = new Rectangle
                {
                    Width = Math.Abs(a.X - b.X) + 6, Height = Math.Abs(a.Y - b.Y) + 6, IsHitTestVisible = false,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 2 },
                    Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x25, 0x63, 0xEB))
                };
                Canvas.SetLeft(box, x);
                Canvas.SetTop(box, y);
                ReaderInteractionLayer.Children.Add(box);
                _objectVisuals.Add(box);
            }
            XTGrowl.Info($"{objects.Count} selected: Delete removes {(objects.Count == 1 ? "it" : "them")}, Ctrl+C copies the picture, right-click for the menu.", this);
        }

        // ── drag an area ─────────────────────────────────────────────────

        private void BeginObjectAreaDrag(PageHit hit)
        {
            ClearObjectSelection();
            if (!TryPageToLayer(hit.Row, hit.U, hit.V, out Point start)) return;
            _objectDrag = new ObjectDrag(hit);
            ReaderContentHost.CaptureMouse();
            Canvas.SetLeft(TextSelectDragRubber, start.X);
            Canvas.SetTop(TextSelectDragRubber, start.Y);
            TextSelectDragRubber.Width = TextSelectDragRubber.Height = 0;
            TextSelectDragRubber.Visibility = Visibility.Visible;
        }

        private bool UpdateObjectDrag(Point pointInHost)
        {
            if (_objectDrag is not { } drag) return false;
            if (!TryGetPagePoint(drag.Start.Row, pointInHost, clamp: true, out var current)) return true;
            if (!TryPageToLayer(drag.Start.Row, drag.Start.U, drag.Start.V, out Point a) || !TryPageToLayer(drag.Start.Row, current.U, current.V, out Point b)) return true;
            Canvas.SetLeft(TextSelectDragRubber, Math.Min(a.X, b.X));
            Canvas.SetTop(TextSelectDragRubber, Math.Min(a.Y, b.Y));
            TextSelectDragRubber.Width = Math.Abs(a.X - b.X);
            TextSelectDragRubber.Height = Math.Abs(a.Y - b.Y);
            return true;
        }

        private bool FinishObjectDrag(Point pointInHost)
        {
            if (_objectDrag is not { } drag) return false;
            _objectDrag = null;
            bool haveEnd = TryGetPagePoint(drag.Start.Row, pointInHost, clamp: true, out var end);
            bool big = TextSelectDragRubber.Width >= 4 && TextSelectDragRubber.Height >= 4;
            TextSelectDragRubber.Visibility = Visibility.Collapsed;
            if (ReaderContentHost.IsMouseCaptured) ReaderContentHost.ReleaseMouseCapture();
            if (haveEnd && big) _ = PickObjectAreaAsync(drag.Start, end);
            return true;
        }

        private async Task PickObjectAreaAsync(PageHit start, PageHit end)
        {
            var page = await LoadPageAnnotationsAsync(start.Row);
            if (page == null) return;
            double w = page.Geometry.DisplayWidth, h = page.Geometry.DisplayHeight;
            IReadOnlyList<PdfObjectRef> found;
            try
            {
                found = await ObjectEditService.PickAreaAsync(start.Row.SourcePath, start.Row.PageNumber,
                    Math.Min(start.U, end.U) * w, Math.Min(start.V, end.V) * h, Math.Max(start.U, end.U) * w, Math.Max(start.V, end.V) * h);
            }
            catch (Exception ex) { XTGrowl.Info(ex.Message, this); return; }
            if (found.Count == 0) { XTGrowl.Info("Nothing drawn completely inside that area.", this); return; }
            if (_readerTool == ReaderTool.EditObject) ShowObjectSelection(start.Row, found.ToList());
        }

        // ── delete / copy ────────────────────────────────────────────────

        internal async Task DeleteSelectedObjectsAsync()
        {
            if (_objectSelection is not { Objects.Count: > 0 } selection || EditHost == null) return;
            var objects = selection.Objects.ToList();
            ClearObjectSelection();
            string what = objects.Count == 1 ? $"1 {objects[0].Kind}" : $"{objects.Count} objects";
            bool ok = await EditHost.DeleteObjectsAsync(selection.Row.SourcePath, objects, $"Removed {what} (saved to file)");
            if (ok) XTGrowl.Success($"Removed {what}. Undo (Ctrl+Z) brings {(objects.Count == 1 ? "it" : "them")} back.", this);
        }

        internal async Task CopySelectedObjectsAsync()
        {
            if (_objectSelection is not { Objects.Count: > 0 } selection) return;
            var objects = selection.Objects;
            try
            {
                var bitmap = await PdfThumbnailService.RenderPageAsync(selection.Row.SourcePath, selection.Row.PageNumber - 1, 2600,
                    layerToken: PdfLayerStateStore.GetToken(selection.Row.SourcePath));
                if (bitmap == null) { XTGrowl.Info("The page could not be drawn to copy it.", this); return; }
                double w = objects[0].PageWidth, h = objects[0].PageHeight;
                double x0 = objects.Min(o => o.X0) - 2, y0 = objects.Min(o => o.Y0) - 2, x1 = objects.Max(o => o.X1) + 2, y1 = objects.Max(o => o.Y1) + 2;
                var rect = new Int32Rect((int)Math.Max(0, x0 / w * bitmap.PixelWidth), (int)Math.Max(0, y0 / h * bitmap.PixelHeight), 0, 0);
                int width = (int)Math.Min(bitmap.PixelWidth - rect.X, Math.Max(2, (x1 - x0) / w * bitmap.PixelWidth));
                int height = (int)Math.Min(bitmap.PixelHeight - rect.Y, Math.Max(2, (y1 - y0) / h * bitmap.PixelHeight));
                Clipboard.SetImage(new CroppedBitmap(bitmap, new Int32Rect(rect.X, rect.Y, Math.Max(1, width), Math.Max(1, height))));
                XTGrowl.Success("Copied the picture of the selection. Paste it into another program.", this);
            }
            catch (Exception ex) { XTGrowl.Info("Could not copy: " + ex.Message, this); }
        }

        private void ShowObjectContextMenu()
        {
            if (_objectSelection is not { Objects.Count: > 0 }) return;
            var menu = new ContextMenu();
            var delete = new MenuItem { Header = "Delete", InputGestureText = "Del" };
            delete.Click += (_, _) => _ = DeleteSelectedObjectsAsync();
            var copy = new MenuItem { Header = "Copy as picture", InputGestureText = "Ctrl+C" };
            copy.Click += (_, _) => _ = CopySelectedObjectsAsync();
            menu.Items.Add(delete);
            menu.Items.Add(copy);
            menu.IsOpen = true;
        }
    }
}
