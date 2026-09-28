using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;

namespace XTPdfMergeApp
{
    /// <summary>
    /// Selecting placed annotations (Typewriter text, Note, Highlight, Stamp) with the Hand tool: click = select, drag = move,
    /// double-click = edit text, Delete / right-click menu = delete. Also the Typewriter format bar (font, size, colour, bold, italic).
    /// </summary>
    public partial class ReaderWindow
    {
        private QuickAnnotationSpec? _selAnn;
        private PageRow? _selRow;

        private sealed class AnnotationMove
        {
            public required PageRow Row { get; init; }
            public required QuickAnnotationSpec Spec { get; init; }
            public required double StartU { get; init; }
            public required double StartV { get; init; }
            public double DeltaU, DeltaV;
            public bool Moved;
        }

        private AnnotationMove? _annMove;

        private static bool IsInside(DependencyObject? node, DependencyObject ancestor)
        {
            while (node != null)
            {
                if (ReferenceEquals(node, ancestor)) return true;
                node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
            }
            return false;
        }

        /// <summary>Bars floating over the page (Find, text format, shape style, highlight mode): clicks on them are not clicks on the page.</summary>
        private bool IsOverlayBar(DependencyObject? source)
            => IsInside(source, FindBar) || IsInside(source, TextFormatBar) || IsInside(source, ShapeBar) || IsInside(source, HighlightBar);

        private void SelectAnnotation(PageRow? row, QuickAnnotationSpec? spec)
        {
            _selRow = spec == null ? null : row;
            _selAnn = spec;
            if (spec is { Kind: QuickAnnotationKind.Typewriter })
            {
                _textFormat = TextFormat.Decode(spec.Format);
                LoadFormatBar();
            }
            else if (spec is { Kind: QuickAnnotationKind.Shape })
            {
                var style = ShapeStyle.Decode(spec.Format);
                _shapeStyle = _shapeStyle with { Color = style.Color, Width = style.Width };
                LoadShapeBar();
            }
            UpdateFormatBarVisibility();
            UpdateSelectionVisual();
        }

        /// <summary>Keeps the selection box on the annotation while scrolling / zooming, and follows it after an edit or undo.</summary>
        private void UpdateSelectionVisual()
        {
            if (_selAnn is not { } spec || _selRow is not { } row || _annotationEditor != null)
            {
                AnnotationSelectionBox.Visibility = Visibility.Collapsed;
                return;
            }
            double du = _annMove?.DeltaU ?? 0, dv = _annMove?.DeltaV ?? 0;
            if (!TryPageToLayer(row, spec.U1 + du, spec.V1 + dv, out Point a) || !TryPageToLayer(row, spec.U2 + du, spec.V2 + dv, out Point b))
            {
                AnnotationSelectionBox.Visibility = Visibility.Collapsed;
                return;
            }
            double left = Math.Min(a.X, b.X) - 3, top = Math.Min(a.Y, b.Y) - 3;
            AnnotationSelectionBox.Width = Math.Abs(a.X - b.X) + 6;
            AnnotationSelectionBox.Height = Math.Abs(a.Y - b.Y) + 6;
            Canvas.SetLeft(AnnotationSelectionBox, left);
            Canvas.SetTop(AnnotationSelectionBox, top);
            AnnotationSelectionBox.Visibility = Visibility.Visible;
        }

        /// <summary>After an edit the cache is re-read: point the selection at the new version (same /NM), or drop it if the annotation is gone (undo).</summary>
        private void RefreshSelectionFromCache()
        {
            if (_selAnn is not { } spec || _selRow is not { } row || _annMove != null || _annotationEditor != null) return;
            var page = GetCachedPageAnnotations(row);
            if (page == null) return;
            var fresh = page.Annotations.FirstOrDefault(a => a.Name == spec.Name);
            if (fresh == null) { SelectAnnotation(null, null); return; }
            if (!fresh.Equals(spec)) _selAnn = fresh;
        }

        // ── Move by dragging ──────────────────────────────────────────

        private void BeginAnnotationMove(PageHit hit, QuickAnnotationSpec spec)
        {
            _annMove = new AnnotationMove { Row = hit.Row, Spec = spec, StartU = hit.U, StartV = hit.V };
            ReaderCommentHoverPopup.Visibility = Visibility.Collapsed;
            ReaderContentHost.CaptureMouse();
        }

        private bool UpdateAnnotationMove(Point pointInHost)
        {
            if (_annMove is not { } move) return false;
            if (!TryGetPagePoint(move.Row, pointInHost, clamp: true, out var current)) return true;
            var s = move.Spec;
            double du = Math.Clamp(current.U - move.StartU, -s.U1, 1 - s.U2);
            double dv = Math.Clamp(current.V - move.StartV, -s.V1, 1 - s.V2);
            if (!move.Moved && TryPageToLayer(move.Row, s.U1, s.V1, out var p0) && TryPageToLayer(move.Row, s.U1 + du, s.V1 + dv, out var p1) && (p1 - p0).Length > 4)
                move.Moved = true;
            if (move.Moved)
            {
                move.DeltaU = du;
                move.DeltaV = dv;
                // Chú thích đi theo chuột ngay (lớp chú thích vẽ nó lệch du, dv) — chưa đổi gì cho tới khi nhả chuột.
                AnnotationLayer.Edit.MoveName = s.Name;
                AnnotationLayer.Edit.MoveDU = du;
                AnnotationLayer.Edit.MoveDV = dv;
                ReaderContinuousView.Redraw();
                UpdateSelectionVisual();
            }
            return true;
        }

        private bool FinishAnnotationMove()
        {
            if (_annMove is not { } move) return false;
            _annMove = null;
            if (ReaderContentHost.IsMouseCaptured) ReaderContentHost.ReleaseMouseCapture();
            if (AnnotationLayer.Edit.MoveName != null)
            {
                AnnotationLayer.Edit.MoveName = null;
                ReaderContinuousView.Redraw();
            }
            if (!move.Moved || (Math.Abs(move.DeltaU) < 1e-6 && Math.Abs(move.DeltaV) < 1e-6)) { UpdateSelectionVisual(); return true; }

            // Dời giữ nguyên appearance (và số đối tượng của chú thích lấy từ file: Save chỉ dịch toạ độ).
            var s = move.Spec;
            var moved = s.Translate(move.DeltaU, move.DeltaV);
            _selAnn = moved;
            CommitAnnotationChange(move.Row, new QuickAnnotationChange(s, moved), "Move " + KindLabel(s.Kind));
            UpdateSelectionVisual();
            return true;
        }

        private static string KindLabel(QuickAnnotationKind kind) => kind switch
        {
            QuickAnnotationKind.Typewriter => "text",
            QuickAnnotationKind.Comment => "note",
            QuickAnnotationKind.Highlight => "highlight",
            QuickAnnotationKind.Stamp => "stamp",
            QuickAnnotationKind.Shape => "shape",
            _ => "annotation"
        };

        // ── Delete / menu ─────────────────────────────────────────────

        private void DeleteSelectedAnnotation()
        {
            if (_selAnn is not { } spec || _selRow is not { } row) return;
            SelectAnnotation(null, null);
            CommitAnnotationChange(row, new QuickAnnotationChange(spec, null), "Delete " + KindLabel(spec.Kind));
        }

        private void ReaderContent_AnnotationContextMenu(object sender, MouseButtonEventArgs e)
        {
            if (_annotationEditor != null || IsOverlayBar(e.OriginalSource as DependencyObject)) return;
            if (!TryHitPage(e.GetPosition(ReaderContentHost), out var hit)) return;
            var picked = PickAnnotation(GetCachedPageAnnotations(hit.Row), hit);
            if (picked == null) return;
            e.Handled = true;
            SelectAnnotation(hit.Row, picked);

            var menu = new ContextMenu();
            if (picked.Kind is QuickAnnotationKind.Typewriter or QuickAnnotationKind.Comment)
            {
                var edit = new MenuItem { Header = "Edit text" };
                edit.Click += (_, _) => _ = OpenAnnotationEditorAsync(hit, picked.Kind, picked);
                menu.Items.Add(edit);
            }
            var delete = new MenuItem { Header = "Delete " + KindLabel(picked.Kind), InputGestureText = "Del" };
            delete.Click += (_, _) => DeleteSelectedAnnotation();
            menu.Items.Add(delete);
            menu.IsOpen = true;
        }

        private bool _highlightModeLoading;

        private void HighlightMode_Checked(object sender, RoutedEventArgs e)
        {
            if (_highlightModeLoading) return;
            AppSettings.HighlightMode = ReferenceEquals(sender, HlModeArea) ? "Area" : "Text";
        }

        /// <summary>"Highlight text": the words between the press and the release points, one rectangle per line.</summary>
        private async Task CommitTextHighlightAsync(HighlightDrag drag, double endU, double endV)
        {
            var page = await LoadPageAnnotationsAsync(drag.Row);
            if (page == null) return;
            var geometry = page.Geometry;
            var (ax, ay, _, _) = geometry.DisplayRectToUser(drag.StartU, drag.StartV, drag.StartU, drag.StartV);
            var (bx, by, _, _) = geometry.DisplayRectToUser(endU, endV, endU, endV);
            var rects = await PdfThumbnailService.SelectTextAsync(drag.Row.SourcePath, drag.Row.PageNumber, ax, ay, bx, by);
            if (rects == null || rects.Count == 0)
            {
                XTStyle.Controls.XTGrowl.Info("No selectable text there. Use Area mode for drawings and scanned pages.", this);
                return;
            }
            var spec = new QuickAnnotationSpec(NewAnnotationName(), QuickAnnotationKind.Highlight, drag.Row.PageNumber,
                rects.Min(r => r.U1), rects.Min(r => r.V1), rects.Max(r => r.U2), rects.Max(r => r.V2), "") { Format = PdfQuickAnnotationService.EncodeTextHighlight(rects) };
            CommitAnnotationChange(drag.Row, new QuickAnnotationChange(null, spec), "Highlight text");
        }

        // ── Typewriter format bar ─────────────────────────────────────

        private TextFormat _textFormat = TextFormat.Decode(AppSettings.TypewriterFormat);
        private bool _formatLoading;
        private bool _formatBarBuilt;

        private void EnsureFormatBar()
        {
            if (_formatBarBuilt) return;
            _formatBarBuilt = true;
            foreach (string family in TextFormat.Families)
                FmtFont.Items.Add(new ComboBoxItem { Content = family, FontFamily = new FontFamily(family), Focusable = false });
            foreach (double size in TextFormat.Sizes)
                FmtSize.Items.Add(new ComboBoxItem { Content = size.ToString("0"), Tag = size, Focusable = false });
            foreach (string hex in TextFormat.Colors)
            {
                var swatch = new RadioButton
                {
                    GroupName = "FmtColor", Tag = hex, Focusable = false, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 4, 0), ToolTip = "Text colour",
                    Template = SwatchTemplate((Color)ColorConverter.ConvertFromString(hex))
                };
                swatch.Checked += (_, _) => { if (!_formatLoading) { _textFormat = _textFormat with { Color = hex }; FormatBarChanged(); } };
                FmtColors.Children.Add(swatch);
            }
        }

        private static ControlTemplate SwatchTemplate(Color color)
        {
            var template = new ControlTemplate(typeof(RadioButton));
            var grid = new FrameworkElementFactory(typeof(Grid));
            grid.SetValue(WidthProperty, 22.0);
            grid.SetValue(HeightProperty, 22.0);
            grid.SetValue(BackgroundProperty, Brushes.Transparent);
            var ring = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
            ring.Name = "Ring";
            ring.SetValue(System.Windows.Shapes.Shape.StrokeProperty, Brushes.Transparent);
            ring.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 2.0);
            var dot = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
            dot.SetValue(WidthProperty, 14.0);
            dot.SetValue(HeightProperty, 14.0);
            dot.SetValue(System.Windows.Shapes.Shape.FillProperty, new SolidColorBrush(color));
            grid.AppendChild(ring);
            grid.AppendChild(dot);
            template.VisualTree = grid;
            var trigger = new Trigger { Property = RadioButton.IsCheckedProperty, Value = true };
            trigger.Setters.Add(new Setter(System.Windows.Shapes.Shape.StrokeProperty, Brushes.Gray, "Ring"));
            template.Triggers.Add(trigger);
            return template;
        }

        /// <summary>Shows the current format in the bar controls (no change events).</summary>
        private void LoadFormatBar()
        {
            EnsureFormatBar();
            _formatLoading = true;
            try
            {
                FmtFont.SelectedIndex = Math.Max(0, Array.FindIndex(TextFormat.Families, f => f == _textFormat.Family));
                FmtSize.SelectedIndex = Array.FindIndex(TextFormat.Sizes, s => Math.Abs(s - _textFormat.Size) < 0.01);
                if (FmtSize.SelectedIndex < 0) FmtSize.Text = _textFormat.Size.ToString("0.##");
                FmtBold.IsChecked = _textFormat.Bold;
                FmtItalic.IsChecked = _textFormat.Italic;
                foreach (RadioButton swatch in FmtColors.Children)
                    swatch.IsChecked = string.Equals((string)swatch.Tag, _textFormat.Color, StringComparison.OrdinalIgnoreCase);
            }
            finally { _formatLoading = false; }
        }

        private void UpdateFormatBarVisibility()
        {
            bool show = _readerTool == ReaderTool.Typewriter || _annotationEditor is { Kind: QuickAnnotationKind.Typewriter } || _selAnn is { Kind: QuickAnnotationKind.Typewriter };
            if (show && TextFormatBar.Visibility != Visibility.Visible) LoadFormatBar();
            TextFormatBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            if (_readerTool == ReaderTool.Highlight && HighlightBar.Visibility != Visibility.Visible)
            {
                _highlightModeLoading = true;
                (AppSettings.HighlightMode == "Area" ? HlModeArea : HlModeText).IsChecked = true;
                _highlightModeLoading = false;
            }
            HighlightBar.Visibility = _readerTool == ReaderTool.Highlight ? Visibility.Visible : Visibility.Collapsed;

            bool shapes = _readerTool == ReaderTool.Shape || _selAnn is { Kind: QuickAnnotationKind.Shape };
            if (shapes && ShapeBar.Visibility != Visibility.Visible) LoadShapeBar();
            ShapeBar.Visibility = shapes ? Visibility.Visible : Visibility.Collapsed;
        }

        private void FmtControl_Changed(object sender, RoutedEventArgs e)
        {
            if (_formatLoading || !_formatBarBuilt) return;
            var family = FmtFont.SelectedIndex >= 0 ? TextFormat.Families[FmtFont.SelectedIndex] : _textFormat.Family;
            double size = _textFormat.Size;
            if (FmtSize.SelectedItem is ComboBoxItem { Tag: double picked }) size = picked;
            _textFormat = _textFormat with { Family = family, Size = size, Bold = FmtBold.IsChecked == true, Italic = FmtItalic.IsChecked == true };
            FormatBarChanged();
        }

        private void FmtSize_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_formatLoading || !_formatBarBuilt || FmtSize.SelectedIndex >= 0) return;
            if (double.TryParse(FmtSize.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out double typed) && typed is >= 4 and <= 200)
            {
                _textFormat = _textFormat with { Size = typed };
                FormatBarChanged();
            }
        }

        private void FormatBarChanged()
        {
            AppSettings.TypewriterFormat = _textFormat.Encode();
            if (_annotationEditor is { Kind: QuickAnnotationKind.Typewriter })
            {
                ApplyEditorFormat();
                PositionAnnotationEditor();
                return;
            }
            if (_selAnn is { Kind: QuickAnnotationKind.Typewriter } spec && _selRow is { } row)
            {
                if (spec.Format == _textFormat.Encode() || GetCachedPageAnnotations(row) is not { } page) return;
                var changed = PdfQuickAnnotationService.WithMeasuredSize(Regenerated(spec) with { Format = _textFormat.Encode() }, page.Geometry);
                _selAnn = changed;
                CommitAnnotationChange(row, new QuickAnnotationChange(spec, changed), "Change text format");
            }
        }

        /// <summary>Gives the in-place text box the look of the annotation that will be written (same font, colour, style).</summary>
        private void ApplyEditorFormat()
        {
            var editor = ReaderAnnotationEditor;
            editor.FontFamily = new FontFamily(_textFormat.Family);
            editor.FontWeight = _textFormat.Bold ? FontWeights.Bold : FontWeights.Normal;
            editor.FontStyle = _textFormat.Italic ? FontStyles.Italic : FontStyles.Normal;
            try { editor.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_textFormat.Color)); }
            catch { editor.Foreground = Brushes.Black; }
        }
    }
}
