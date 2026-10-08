using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace XTCapture
{
    internal enum MarkupTool { Select, Rectangle, Ellipse, Arrow, Line, Pen, Text, Marker, Mosaic }

    /// <summary>
    /// The drawing logic, with no screen in it: tools, the objects, selection, moving and resizing, undo / redo. A host (the overlay over the frozen screen, or the
    /// window that reopens a stored capture) feeds it mouse positions in picture pixels and draws what it holds.
    /// </summary>
    internal sealed class MarkupController
    {
        private enum Gesture { None, Draw, Move, Resize }

        private List<MarkupItem> _items = new();
        private readonly List<List<MarkupItem>> _undo = new();
        private readonly List<List<MarkupItem>> _redo = new();
        private Gesture _gesture;
        private Point _start, _last;
        private MarkupItem? _origin;           // the object as it was when a move / resize began
        private CaptureHandleHit _handle;
        private bool _changedDuringGesture;
        private MarkupItem? _draftText;

        public IReadOnlyList<MarkupItem> Items => _items;
        public string? SelectedId { get; private set; }
        public MarkupItem? Selected => SelectedId == null ? null : _items.FirstOrDefault(i => i.Id == SelectedId);
        public MarkupTool Tool { get; private set; } = MarkupTool.Select;

        /// <summary>What the next object will look like; while an object is selected, what it looks like (the property bar edits this).</summary>
        public MarkupStyle Style { get; private set; } = new();

        /// <summary>The object being drawn right now (not in <see cref="Items"/> until the mouse is released).</summary>
        public MarkupItem? Draft { get; private set; }

        /// <summary>The picture's size: new points are kept inside it.</summary>
        public Size Limit { get; set; } = new Size(double.MaxValue, double.MaxValue);

        /// <summary>How close (in picture pixels) the mouse must be to pick an object or a handle.</summary>
        public double Tolerance { get; set; } = 6;

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public bool IsEmpty => _items.Count == 0;

        /// <summary>True between a mouse press that started drawing, moving or resizing and its release.</summary>
        public bool GestureActive => _gesture != Gesture.None;
        public int NextMarkerNumber => _items.Where(i => i.Kind == MarkupKind.Marker).Select(i => i.Number).DefaultIfEmpty(0).Max() + 1;

        /// <summary>The text object being typed (its box is shown by the host instead of the drawing).</summary>
        public string? EditingTextId { get; private set; }

        /// <summary>Anything about the objects, the selection, the tool or the style changed: the host redraws and refreshes its bar.</summary>
        public event Action? Changed;

        /// <summary>A text object was started or opened for editing: the host shows a box at its place. The second argument is true for a new one.</summary>
        public event Action<MarkupItem, bool>? TextEditRequested;

        private void Raise() => Changed?.Invoke();

        // ── Tool, style, selection ──────────────────────────────────────

        public void SetTool(MarkupTool tool)
        {
            if (tool == Tool) return;
            CancelGesture();
            Tool = tool;
            if (tool != MarkupTool.Select && Selected is { } selected && !Fits(selected, tool)) SelectedId = null;
            Raise();
        }

        private static bool Fits(MarkupItem item, MarkupTool tool) => item.Kind.ToString() == tool.ToString();

        public void Select(string? id)
        {
            if (SelectedId == id) return;
            SelectedId = id;
            if (Selected is { } item) Style = item.Style;
            Raise();
        }

        /// <summary>Changes the style the next object gets and, when one is selected, that object (one undo step).</summary>
        public void ApplyStyle(Func<MarkupStyle, MarkupStyle> change)
        {
            Style = change(Style);
            if (Selected is { } item && item.Style != Style)
            {
                Push();
                Replace(item with { Style = Style });
            }
            Raise();
        }

        // ── Objects ─────────────────────────────────────────────────────

        public void Load(IEnumerable<MarkupItem> items)
        {
            CancelGesture();
            _items = items.ToList();
            _undo.Clear();
            _redo.Clear();
            SelectedId = null;
            Draft = null;
            Raise();
        }

        private void Push()
        {
            _undo.Add(new List<MarkupItem>(_items));
            if (_undo.Count > 200) _undo.RemoveAt(0);
            _redo.Clear();
        }

        private void Replace(MarkupItem item)
        {
            int index = _items.FindIndex(i => i.Id == item.Id);
            if (index >= 0) _items[index] = item;
        }

        public void Undo()
        {
            if (!CanUndo) return;
            CancelGesture();
            _redo.Add(new List<MarkupItem>(_items));
            _items = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            if (SelectedId != null && Selected == null) SelectedId = null;
            Raise();
        }

        public void Redo()
        {
            if (!CanRedo) return;
            CancelGesture();
            _undo.Add(new List<MarkupItem>(_items));
            _items = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            if (SelectedId != null && Selected == null) SelectedId = null;
            Raise();
        }

        public void DeleteSelected()
        {
            if (Selected is not { } item) return;
            Push();
            _items.Remove(item);
            SelectedId = null;
            Raise();
        }

        public void Clear()
        {
            if (_items.Count == 0) return;
            Push();
            _items.Clear();
            SelectedId = null;
            Raise();
        }

        /// <summary>Moves the selected object by (dx, dy) (the arrow keys); a series of nudges is one step per call.</summary>
        public void Nudge(double dx, double dy)
        {
            if (Selected is not { } item) return;
            Push();
            Replace(item.Translate(dx, dy));
            Raise();
        }

        /// <summary>Brings the selected object to the front.</summary>
        public void BringToFront()
        {
            if (Selected is not { } item || _items[^1].Id == item.Id) return;
            Push();
            _items.Remove(item);
            _items.Add(item);
            Raise();
        }

        // ── Text ────────────────────────────────────────────────────────

        /// <summary>The user finished typing: a new text is added (nothing when empty), an old one changed (removed when emptied).</summary>
        public void CommitText(string id, string text)
        {
            text = text.TrimEnd('\r', '\n');
            EditingTextId = null;
            if (_draftText != null && _draftText.Id == id)
            {
                var draft = _draftText;
                _draftText = null;
                if (text.Trim().Length == 0) { Raise(); return; }
                Push();
                _items.Add(draft with { Text = text });
                SelectedId = draft.Id;
                Style = draft.Style;
            }
            else if (_items.FirstOrDefault(i => i.Id == id) is { } existing)
            {
                if (text.Trim().Length == 0) { Push(); _items.Remove(existing); if (SelectedId == id) SelectedId = null; }
                else if (text != existing.Text) { Push(); Replace(existing with { Text = text }); SelectedId = id; }
            }
            Raise();
        }

        public void CancelText()
        {
            _draftText = null;
            EditingTextId = null;
            Raise();
        }

        /// <summary>The text objects' box is drawn by the host while editing; this is the item as it is now (the draft or the stored one).</summary>
        public MarkupItem? TextBeingEdited => EditingTextId == null ? null : (_draftText?.Id == EditingTextId ? _draftText : _items.FirstOrDefault(i => i.Id == EditingTextId));

        public void BeginEditText(MarkupItem item)
        {
            EditingTextId = item.Id;
            SelectedId = item.Id;
            Style = item.Style;
            Raise();
            TextEditRequested?.Invoke(item, false);
        }

        // ── Pointer ─────────────────────────────────────────────────────

        private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, Limit.Width), Math.Clamp(p.Y, 0, Limit.Height));

        private static bool IsDrawing(MarkupTool tool) => tool != MarkupTool.Select;

        public void PointerDown(Point p, int clickCount = 1)
        {
            if (EditingTextId != null) return; // the typing box owns the mouse until it is closed
            CancelGesture();
            _start = _last = p;
            _changedDuringGesture = false;

            // a handle of the selected object works with every tool
            if (Selected is { } selected && MarkupGeometry.HitHandle(selected, p, Tolerance) is { Kind: not CaptureHandle.None } handle)
            {
                _handle = handle;
                _origin = selected;
                _gesture = Gesture.Resize;
                return;
            }

            if (Tool == MarkupTool.Select)
            {
                var hit = MarkupGeometry.Pick(_items, p, Tolerance);
                if (hit == null) { Select(null); return; }
                Select(hit.Id);
                if (hit.Kind == MarkupKind.Text && clickCount >= 2) { BeginEditText(hit); return; }
                _origin = hit;
                _gesture = Gesture.Move;
                return;
            }

            var at = Clamp(p);
            switch (Tool)
            {
                case MarkupTool.Text:
                {
                    var existing = MarkupGeometry.Pick(_items, p, Tolerance);
                    if (existing is { Kind: MarkupKind.Text }) { BeginEditText(existing); return; }
                    _draftText = new MarkupItem(MarkupItem.NewId(), MarkupKind.Text, at.X, at.Y, at.X, at.Y, Style);
                    EditingTextId = _draftText.Id;
                    Raise();
                    TextEditRequested?.Invoke(_draftText, true);
                    return;
                }
                case MarkupTool.Marker:
                {
                    Push();
                    var marker = new MarkupItem(MarkupItem.NewId(), MarkupKind.Marker, at.X, at.Y, at.X, at.Y, Style, Number: NextMarkerNumber);
                    _items.Add(marker);
                    SelectedId = marker.Id;
                    _origin = marker;
                    _gesture = Gesture.Move; // dragging right after placing it adjusts the place
                    _changedDuringGesture = true;
                    Raise();
                    return;
                }
                case MarkupTool.Pen:
                    Draft = new MarkupItem(MarkupItem.NewId(), MarkupKind.Pen, at.X, at.Y, at.X, at.Y, Style, Points: new List<double> { at.X, at.Y });
                    break;
                default:
                {
                    var kind = Enum.Parse<MarkupKind>(Tool.ToString());
                    var style = Tool == MarkupTool.Mosaic ? Style with { Width = Style.Width >= 6 && Selected?.Kind == MarkupKind.Mosaic ? Style.Width : MarkupStyle.DefaultBlock, Opacity = 100 } : Style;
                    Draft = new MarkupItem(MarkupItem.NewId(), kind, at.X, at.Y, at.X, at.Y, style);
                    break;
                }
            }
            _gesture = Gesture.Draw;
            Raise();
        }

        public void PointerMove(Point p)
        {
            switch (_gesture)
            {
                case Gesture.Draw when Draft is { } draft:
                {
                    var at = Clamp(p);
                    Draft = draft.Kind == MarkupKind.Pen
                        ? AppendPoint(draft, at)
                        : draft with { X2 = at.X, Y2 = at.Y };
                    Raise();
                    break;
                }
                case Gesture.Move when _origin is { } origin:
                {
                    double dx = p.X - _start.X, dy = p.Y - _start.Y;
                    if (!_changedDuringGesture && Math.Abs(dx) + Math.Abs(dy) < 2) return;
                    if (!_changedDuringGesture) { Push(); _changedDuringGesture = true; }
                    Replace(origin.Translate(dx, dy));
                    Raise();
                    break;
                }
                case Gesture.Resize when _origin is { } origin:
                {
                    if (!_changedDuringGesture) { Push(); _changedDuringGesture = true; }
                    Replace(MarkupGeometry.Resize(origin, _handle, Clamp(p), Limit));
                    Raise();
                    break;
                }
            }
            _last = p;
        }

        private static MarkupItem AppendPoint(MarkupItem pen, Point at)
        {
            var points = pen.Points!;
            double dx = at.X - points[^2], dy = at.Y - points[^1];
            if (dx * dx + dy * dy < 2.25) return pen; // under 1.5 px: the same point
            var more = new List<double>(points) { at.X, at.Y };
            return pen with { Points = more, X2 = at.X, Y2 = at.Y };
        }

        public void PointerUp(Point p)
        {
            var gesture = _gesture;
            _gesture = Gesture.None;
            if (gesture == Gesture.Draw && Draft is { } draft)
            {
                Draft = null;
                if (Usable(draft))
                {
                    Push();
                    _items.Add(draft);
                    SelectedId = draft.Id;
                    Style = draft.Style;
                }
                Raise();
                return;
            }
            if (gesture is Gesture.Move or Gesture.Resize)
            {
                _origin = null;
                if (Selected is { } item) Style = item.Style;
                Raise();
            }
        }

        private static bool Usable(MarkupItem item)
        {
            switch (item.Kind)
            {
                case MarkupKind.Rectangle:
                case MarkupKind.Ellipse:
                case MarkupKind.Mosaic:
                    return Math.Abs(item.X2 - item.X1) >= 3 && Math.Abs(item.Y2 - item.Y1) >= 3;
                case MarkupKind.Arrow:
                case MarkupKind.Line:
                    return Math.Abs(item.X2 - item.X1) + Math.Abs(item.Y2 - item.Y1) >= 4;
                default:
                    return true;
            }
        }

        private void CancelGesture()
        {
            _gesture = Gesture.None;
            Draft = null;
            _origin = null;
        }

        /// <summary>What a click at <paramref name="p"/> would grab: a handle of the selected object, an object (to move), or nothing.</summary>
        public CaptureHandleHit HitForCursor(Point p)
        {
            if (Selected is { } selected && MarkupGeometry.HitHandle(selected, p, Tolerance) is { Kind: not CaptureHandle.None } handle) return handle;
            if (Tool == MarkupTool.Select && MarkupGeometry.Pick(_items, p, Tolerance) != null) return new CaptureHandleHit(CaptureHandle.Move);
            return default;
        }
    }
}
