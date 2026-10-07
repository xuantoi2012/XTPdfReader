using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
            /// <summary>Plain click (no drag) on a text box that was already selected: open it for editing.</summary>
            public bool OpenEditOnClick;
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
            => IsInside(source, FindBar) || IsInside(source, TextFormatBar) || IsInside(source, ShapeBar) || IsInside(source, MarkupColorBar);

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
            UpdateSelectionVisualCore();
            UpdateTextChrome();
            PositionFloatingBars();
        }

        private void UpdateSelectionVisualCore()
        {
            UpdateShapeGrips();
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
            if (!Controls.PdfPermissionDialog.Require(this, new[] { hit.Row.SourcePath }, PdfPermissionOperation.Annotate)) return;
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
            if (!move.Moved || (Math.Abs(move.DeltaU) < 1e-6 && Math.Abs(move.DeltaV) < 1e-6))
            {
                UpdateSelectionVisual();
                if (!move.Moved && move.Spec.Kind == QuickAnnotationKind.Comment) ShowCommentPopup(move.Row, move.Spec);
                else if (!move.Moved && move.OpenEditOnClick) _ = OpenAnnotationEditorAsync(new PageHit(move.Row, move.StartU, move.StartV), move.Spec.Kind, move.Spec);
                return true;
            }

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
            QuickAnnotationKind.Reply => "reply",
            QuickAnnotationKind.Callout => "callout",
            QuickAnnotationKind.Highlight => "highlight",
            QuickAnnotationKind.Underline => "underline",
            QuickAnnotationKind.StrikeOut => "strikethrough",
            QuickAnnotationKind.Squiggly => "squiggly underline",
            QuickAnnotationKind.Ink => "pencil stroke",
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

        private void CopyAnnotationText(QuickAnnotationSpec spec)
        {
            if (_selRow is { } row && !Controls.PdfPermissionDialog.Require(this, new[] { row.SourcePath }, PdfPermissionOperation.Copy)) return;
            if (string.IsNullOrWhiteSpace(spec.Text)) return;
            try { Clipboard.SetText(spec.Text); }
            catch { XTStyle.Controls.XTGrowl.Error("Could not copy the annotation text.", this); }
        }

        private void ToggleSelectedAnnotationResolved()
        {
            if (_selAnn is not { } spec || _selRow is not { } row) return;
            var changed = spec with { Resolved = !spec.Resolved };
            _selAnn = changed;
            CommitAnnotationChange(row, new QuickAnnotationChange(spec, changed), changed.Resolved ? "Resolve " + KindLabel(spec.Kind) : "Reopen " + KindLabel(spec.Kind));
        }

        private async Task ReplyToAnnotationAsync(PageHit hit, QuickAnnotationSpec parent)
        {
            string? text = TextPromptWindow.Ask(this, "Reply to comment", "Reply text:");
            if (string.IsNullOrWhiteSpace(text)) return;
            var page = await LoadPageAnnotationsAsync(hit.Row);
            if (page == null) return;
            var reply = new QuickAnnotationSpec(NewAnnotationName(), QuickAnnotationKind.Reply, hit.Row.PageNumber,
                Math.Min(.95, parent.U2 + .01), Math.Min(.95, parent.V1 + .01), 0, 0, text) { Format = "R|" + parent.Name };
            reply = PdfQuickAnnotationService.WithMeasuredSize(reply, page.Geometry);
            CommitAnnotationChange(hit.Row, new QuickAnnotationChange(null, reply), "Reply to comment");
        }

        private void ReaderContent_AnnotationContextMenu(object sender, MouseButtonEventArgs e)
        {
            if (_annotationEditor != null || IsOverlayBar(e.OriginalSource as DependencyObject)) return;
            if (_readerTool == ReaderTool.Select && _textSelection != null)
            {
                e.Handled = true;
                var textMenu = new ContextMenu();
                var copy = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C" };
                copy.Click += (_, _) => CopySelectedText();
                textMenu.Items.Add(copy);
                textMenu.IsOpen = true;
                return;
            }
            if (!TryHitPage(e.GetPosition(ReaderContentHost), out var hit)) return;
            var picked = PickAnnotation(GetCachedPageAnnotations(hit.Row), hit);
            if (picked == null)
            {
                e.Handled = true;
                ShowPageContextMenu();
                return;
            }
            e.Handled = true;
            SelectAnnotation(hit.Row, picked);

            var menu = new ContextMenu();
            if (!string.IsNullOrWhiteSpace(picked.Text))
            {
                var copy = new MenuItem { Header = "Copy text", InputGestureText = "Ctrl+C" };
                copy.Click += (_, _) => CopyAnnotationText(picked);
                menu.Items.Add(copy);
            }
            if (picked.Kind is QuickAnnotationKind.Typewriter or QuickAnnotationKind.Comment or QuickAnnotationKind.Callout)
            {
                var edit = new MenuItem { Header = "Edit text" };
                edit.Click += (_, _) =>
                {
                    if (picked.Kind == QuickAnnotationKind.Comment) ShowCommentPopup(hit.Row, picked);
                    else _ = OpenAnnotationEditorAsync(hit, picked.Kind, picked);
                };
                menu.Items.Add(edit);
            }
            if (picked.Kind != QuickAnnotationKind.Reply)
            {
                var reply = new MenuItem { Header = "Reply…" };
                reply.Click += (_, _) =>
                {
                    // Ghi chú: trả lời ngay trong popup luồng (kiểu Word) thay vì hộp thoại rời.
                    if (picked.Kind == QuickAnnotationKind.Comment) ShowCommentPopup(hit.Row, picked, focusReply: true);
                    else _ = ReplyToAnnotationAsync(hit, picked);
                };
                menu.Items.Add(reply);
            }
            if (picked.Selectable)
            {
                var resolved = new MenuItem { Header = picked.Resolved ? "Reopen comment" : "Mark as resolved" };
                resolved.Click += (_, _) => ToggleSelectedAnnotationResolved();
                menu.Items.Add(resolved);
            }
            if (menu.Items.Count > 0)
                menu.Items.Add(new Separator());
            var delete = new MenuItem { Header = "Delete " + KindLabel(picked.Kind), InputGestureText = "Del" };
            delete.Click += (_, _) => DeleteSelectedAnnotation();
            menu.Items.Add(delete);
            menu.IsOpen = true;
        }

        /// <summary>Right-click on empty page space with no annotation under the pointer (Foxit shows this instead of nothing): view tools + navigation.</summary>
        private void ShowPageContextMenu()
        {
            var menu = new ContextMenu();
            MenuItem Add(string header, string gesture, Action action)
            {
                var item = new MenuItem { Header = header, InputGestureText = gesture };
                item.Click += (_, _) => action();
                menu.Items.Add(item);
                return item;
            }
            Add(_readerTool == ReaderTool.Hand ? "Select Tool" : "Hand Tool", "",
                () => SetReaderTool(_readerTool == ReaderTool.Hand ? ReaderTool.Select : ReaderTool.Hand));
            Add("Add note", "", () => SetReaderTool(ReaderTool.Comment));
            Add("Highlight text", "", () => SelectHighlightMode("Text"));
            menu.Items.Add(new Separator());
            Add("Zoom In", "+", () => ReaderZoomIn_Click(this, new RoutedEventArgs()));
            Add("Zoom Out", "-", () => ReaderZoomOut_Click(this, new RoutedEventArgs()));
            Add("Fit Page", "", () => ReaderFitPage_Click(this, new RoutedEventArgs()));
            Add("Fit Width", "0", () => ReaderFitWidth_Click(this, new RoutedEventArgs()));
            menu.Items.Add(new Separator());
            Add("Rotate Clockwise", "", () => ReaderRotateRight_Click(this, new RoutedEventArgs()));
            Add("Rotate Counterclockwise", "", () => ReaderRotateLeft_Click(this, new RoutedEventArgs()));
            menu.Items.Add(new Separator());
            Add("Previous Page", "PgUp", () => ReaderPreviousPage_Click(this, new RoutedEventArgs()));
            Add("Next Page", "PgDn", () => ReaderNextPage_Click(this, new RoutedEventArgs()));
            menu.Items.Add(new Separator());
            Add("Print…", "Ctrl+P", () => ReaderPrint_Click(this, new RoutedEventArgs()));
            menu.IsOpen = true;
        }

        /// <summary>Highlight/Underline/Strikethrough "on text": the words between the press and the release points, one rectangle per line.</summary>
        private async Task CommitTextMarkupAsync(HighlightDrag drag, double endU, double endV, QuickAnnotationKind kind)
        {
            var page = await LoadPageAnnotationsAsync(drag.Row);
            if (page == null) return;
            var geometry = page.Geometry;
            var (ax, ay, _, _) = geometry.DisplayRectToUser(drag.StartU, drag.StartV, drag.StartU, drag.StartV);
            var (bx, by, _, _) = geometry.DisplayRectToUser(endU, endV, endU, endV);
            var rects = await PdfThumbnailService.SelectTextAsync(drag.Row.SourcePath, drag.Row.PageNumber, ax, ay, bx, by);
            if (rects == null || rects.Count == 0)
            {
                XTStyle.Controls.XTGrowl.Info(kind == QuickAnnotationKind.Highlight
                    ? "No selectable text there. Use Area mode for drawings and scanned pages."
                    : "No selectable text there.", this);
                return;
            }
            var spec = new QuickAnnotationSpec(NewAnnotationName(), kind, drag.Row.PageNumber,
                rects.Min(r => r.U1), rects.Min(r => r.V1), rects.Max(r => r.U2), rects.Max(r => r.V2), "") { Format = PdfQuickAnnotationService.EncodeTextHighlight(rects) };
            CommitAnnotationChange(drag.Row, new QuickAnnotationChange(null, spec), "Add " + KindLabel(kind));
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
                FmtUnderline.IsChecked = _textFormat.Underline;
                FmtAlignLeft.IsChecked = _textFormat.Align == 0;
                FmtAlignCenter.IsChecked = _textFormat.Align == 1;
                FmtAlignRight.IsChecked = _textFormat.Align == 2;
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


            bool shapes = _readerTool == ReaderTool.Shape || _selAnn is { Kind: QuickAnnotationKind.Shape };
            if (shapes && ShapeBar.Visibility != Visibility.Visible) LoadShapeBar();
            ShapeBar.Visibility = shapes ? Visibility.Visible : Visibility.Collapsed;

            bool markup = _selAnn is { Kind: QuickAnnotationKind.Highlight or QuickAnnotationKind.Underline or QuickAnnotationKind.StrikeOut or QuickAnnotationKind.Squiggly };
            if (markup) LoadMarkupColorBar();
            MarkupColorBar.Visibility = markup ? Visibility.Visible : Visibility.Collapsed;
            PositionFloatingBars();
        }

        // ── Colour of a selected Highlight/Underline/Strikethrough ("properties" of the selected markup) ──────

        private static readonly string[] MarkupColorPalette = { "#FFEB00", "#7CFC00", "#FF6EC7", "#66CCFF", "#ED1C24", "#FF8A00", "#000000" };
        private bool _markupColorBuilt, _markupColorLoading;

        private void EnsureMarkupColorBar()
        {
            if (_markupColorBuilt) return;
            _markupColorBuilt = true;
            foreach (string hex in MarkupColorPalette)
            {
                var swatch = new RadioButton
                {
                    GroupName = "MarkupColor", Tag = hex, Focusable = false, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 4, 0), ToolTip = "Colour",
                    Template = SwatchTemplate((Color)ColorConverter.ConvertFromString(hex))
                };
                swatch.Checked += (_, _) => { if (!_markupColorLoading) MarkupColorChanged(hex); };
                MarkupColorSwatches.Children.Add(swatch);
            }
        }

        /// <summary>Default colour of a markup that has none set yet — matches what <see cref="PdfQuickAnnotationService"/> writes.</summary>
        private static string DefaultMarkupColor(QuickAnnotationKind kind) => kind == QuickAnnotationKind.Highlight ? "#FFEB00" : "#ED1C24";

        private void LoadMarkupColorBar()
        {
            EnsureMarkupColorBar();
            string current = _selAnn is { } spec ? (spec.Color.Length > 0 ? spec.Color : DefaultMarkupColor(spec.Kind)) : DefaultMarkupColor(QuickAnnotationKind.Highlight);
            _markupColorLoading = true;
            try
            {
                foreach (RadioButton swatch in MarkupColorSwatches.Children)
                    swatch.IsChecked = string.Equals((string)swatch.Tag, current, StringComparison.OrdinalIgnoreCase);
            }
            finally { _markupColorLoading = false; }
        }

        private void MarkupColorChanged(string hex)
        {
            if (_selAnn is not { Kind: QuickAnnotationKind.Highlight or QuickAnnotationKind.Underline or QuickAnnotationKind.StrikeOut or QuickAnnotationKind.Squiggly } spec || _selRow is not { } row) return;
            string current = spec.Color.Length > 0 ? spec.Color : DefaultMarkupColor(spec.Kind);
            if (string.Equals(current, hex, StringComparison.OrdinalIgnoreCase)) return;
            var changed = Regenerated(spec) with { Color = hex };
            _selAnn = changed;
            CommitAnnotationChange(row, new QuickAnnotationChange(spec, changed), "Change colour");
        }

        private void FmtControl_Changed(object sender, RoutedEventArgs e)
        {
            if (_formatLoading || !_formatBarBuilt) return;
            var family = FmtFont.SelectedIndex >= 0 ? TextFormat.Families[FmtFont.SelectedIndex] : _textFormat.Family;
            double size = _textFormat.Size;
            if (FmtSize.SelectedItem is ComboBoxItem { Tag: double picked }) size = picked;
            _textFormat = _textFormat with { Family = family, Size = size, Bold = FmtBold.IsChecked == true, Italic = FmtItalic.IsChecked == true, Underline = FmtUnderline.IsChecked == true };
            FormatBarChanged();
        }

        private void FmtAlign_Click(object sender, RoutedEventArgs e)
        {
            if (_formatLoading || !_formatBarBuilt || sender is not ToggleButton { Tag: string tag } || !int.TryParse(tag, out int align)) return;
            _textFormat = _textFormat with { Align = align };
            LoadFormatBar(); // radio behaviour: exactly one alignment lit
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
            AppSettings.TypewriterFormat = (_textFormat with { Width = 0 }).Encode(); // the box width belongs to one text box, never to the tool default
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
                // WithMeasuredSize re-grows from the anchor; a fixed-width box keeps its width (carried in the format).
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
            editor.TextDecorations = _textFormat.Underline ? TextDecorations.Underline : null;
            editor.TextAlignment = _textFormat.Align switch { 1 => TextAlignment.Center, 2 => TextAlignment.Right, _ => TextAlignment.Left };
        }
    }
}
