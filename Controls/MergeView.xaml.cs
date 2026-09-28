using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;
using DocumentGroup = XTPdfMergeApp.Domain.WorkspaceDocument;

namespace XTPdfMergeApp.Controls
{
    /// <summary>
    /// Màn hình Merge (docs/UI_REDESIGN.md, P6): mỗi file đang mở là 1 cửa sổ con trong vùng làm việc. Layout 1 · 2 · 3 cột · 2×2 · Free;
    /// tối đa 4 cửa sổ hiện cùng lúc (cửa sổ dùng lâu nhất tự thu xuống dock); nhấp đúp header = phóng to/trả lại; kéo tới mép = snap;
    /// cửa sổ ≥ 320×240 và luôn nằm trong vùng làm việc. Kéo thả dùng cùng lệnh Move/Copy của panel Pages (có Undo).
    /// </summary>
    public partial class MergeView : UserControl
    {
        private enum MergeLayout { One, Two, Three, Grid, Free }

        private sealed class Entry
        {
            public required MergeMiniWindow Window;
            public bool Minimized;
            public bool Maximized;
            public long LastActive;
            public Rect FreeRect = Rect.Empty;
        }

        private const double MinWidth_ = 320, MinHeight_ = 240, Gap = 10;

        private readonly List<Entry> _entries = new();
        private ObservableCollection<DocumentGroup>? _groups;
        private long _tick;
        private MergeLayout _layout = MergeLayout.Two;
        private bool _layoutChosen; // false = tự chọn layout theo số cửa sổ
        private bool _updatingLayoutButtons;

        public MergeView()
        {
            InitializeComponent();
            UpdateLayoutButtons();
        }

        /// <summary>Máy chủ chỉnh sửa (Move/Delete/Save/Undo…) — do ReaderWindow gắn.</summary>
        internal Func<IReaderPageEditHost?>? HostProvider { get; set; }
        private IReaderPageEditHost? Host => HostProvider?.Invoke();

        internal event Action? DoneRequested;
        internal event Action? MergeAllRequested;
        internal event Action? OpenFileRequested;

        /// <summary>Gắn danh sách file đang mở (cùng danh sách với các tab của cửa sổ đọc).</summary>
        internal void Bind(ObservableCollection<DocumentGroup> groups)
        {
            _groups = groups;
            groups.CollectionChanged += (_, _) => Sync();
            Sync();
        }

        /// <summary>Đưa file này lên (mở lại nếu đang ở dock).</summary>
        internal void ShowGroup(DocumentGroup group)
        {
            var entry = _entries.FirstOrDefault(e => ReferenceEquals(e.Window.Group, group));
            if (entry == null) return;
            entry.Minimized = false;
            entry.LastActive = ++_tick;
            Relayout();
        }

        // ── Đồng bộ với danh sách file ────────────────────────────────

        private void Sync()
        {
            if (_groups == null) return;
            foreach (var entry in _entries.Where(e => !_groups.Contains(e.Window.Group)).ToList())
            {
                Workspace.Children.Remove(entry.Window);
                _entries.Remove(entry);
            }
            foreach (var group in _groups)
            {
                if (_entries.Any(e => ReferenceEquals(e.Window.Group, group))) continue;
                var window = new MergeMiniWindow(group);
                Hook(window);
                Workspace.Children.Add(window);
                _entries.Add(new Entry { Window = window, LastActive = ++_tick });
            }
            _entries.Sort((a, b) => _groups.IndexOf(a.Window.Group).CompareTo(_groups.IndexOf(b.Window.Group)));
            Relayout();
        }

        private void Hook(MergeMiniWindow window)
        {
            window.Activated += OnActivated;
            window.MoveDelta += OnMoveDelta;
            window.MoveFinished += OnMoveFinished;
            window.ResizeDelta += OnResizeDelta;
            window.ToggleMaximizeRequested += w => { var e = EntryOf(w); e.Maximized = !e.Maximized; Relayout(); };
            window.MinimizeRequested += w => { var e = EntryOf(w); e.Minimized = true; e.Maximized = false; Relayout(); };
            window.CloseRequested += async w => await CloseAsync(w.Group);
            window.SaveRequested += async w => { if (Host != null) await Host.SaveGroupAsync(w.Group, saveAs: false); };
            window.PagesDropped += OnPagesDropped;
            window.DeleteRequested += (w, pages) => Host?.DeletePages(w.Group, pages);
        }

        private Entry EntryOf(MergeMiniWindow window) => _entries.First(e => ReferenceEquals(e.Window, window));

        private async System.Threading.Tasks.Task CloseAsync(DocumentGroup group)
        {
            var host = Host;
            if (host == null) return;
            if (group.IsDirty)
            {
                var answer = MessageBox.Show(Window.GetWindow(this), $"Save changes to \"{group.FileName}\" before closing?", "Unsaved changes",
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Cancel) return;
                if (answer == MessageBoxResult.Yes && !await host.SaveGroupAsync(group, saveAs: false)) return;
            }
            host.CloseDocument(group);
        }

        // ── Layout ────────────────────────────────────────────────────

        private static int Capacity(MergeLayout layout) => layout switch { MergeLayout.One => 1, MergeLayout.Two => 2, MergeLayout.Three => 3, _ => 4 };

        private MergeLayout EffectiveLayout()
        {
            if (_layoutChosen) return _layout;
            int open = _entries.Count(e => !e.Minimized);
            return open <= 1 ? MergeLayout.One : open == 2 ? MergeLayout.Two : open == 3 ? MergeLayout.Three : MergeLayout.Grid;
        }

        private void Layout_Checked(object sender, RoutedEventArgs e)
        {
            if (_updatingLayoutButtons || sender is not RadioButton { Tag: string tag }) return;
            var chosen = Enum.Parse<MergeLayout>(tag);
            if (chosen == MergeLayout.Free) EnterFree(); // giữ nguyên vị trí hiện tại làm điểm xuất phát
            _layout = chosen;
            _layoutChosen = true;
            foreach (var entry in _entries) entry.Maximized = false;
            Relayout();
        }

        private void UpdateLayoutButtons()
        {
            _updatingLayoutButtons = true;
            try
            {
                var layout = EffectiveLayout();
                (layout switch { MergeLayout.One => LayoutOne, MergeLayout.Two => LayoutTwo, MergeLayout.Three => LayoutThree, MergeLayout.Grid => LayoutGrid, _ => LayoutFree }).IsChecked = true;
            }
            finally { _updatingLayoutButtons = false; }
        }

        private Rect Area => new(Gap, Gap, Math.Max(MinWidth_, Workspace.ActualWidth - 2 * Gap), Math.Max(MinHeight_, Workspace.ActualHeight - 2 * Gap));

        /// <summary>Cửa sổ đang hiện: tối đa theo sức chứa của layout, ưu tiên cửa sổ dùng gần nhất.</summary>
        private List<Entry> VisibleEntries(MergeLayout layout)
            => _entries.Where(e => !e.Minimized).OrderByDescending(e => e.LastActive).Take(Capacity(layout)).ToList();

        private void Relayout()
        {
            var layout = EffectiveLayout();
            var visible = VisibleEntries(layout);
            var area = Area;
            // Vị trí theo thứ tự file (ổn định), riêng layout "1" chỉ có cửa sổ dùng gần nhất.
            var ordered = visible.OrderBy(e => _entries.IndexOf(e)).ToList();

            for (int i = 0; i < ordered.Count; i++)
            {
                var entry = ordered[i];
                Rect rect = entry.Maximized ? area : layout switch
                {
                    MergeLayout.One => area,
                    MergeLayout.Two => Column(area, i, Math.Max(2, ordered.Count)),
                    MergeLayout.Three => Column(area, i, Math.Max(3, ordered.Count)),
                    MergeLayout.Grid => GridCell(area, i, ordered.Count),
                    _ => FreeRectOf(entry, i, area)
                };
                Place(entry, rect);
            }

            var rank = visible.OrderBy(e => e.LastActive).ToList();
            long top = visible.Count == 0 ? 0 : visible.Max(e => e.LastActive);
            foreach (var entry in _entries)
            {
                bool shown = visible.Contains(entry);
                entry.Window.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
                entry.Window.SetActive(shown && entry.LastActive == top);
                entry.Window.SetResizable(layout == MergeLayout.Free && !entry.Maximized);
                Panel.SetZIndex(entry.Window, entry.Maximized ? 1000 : rank.IndexOf(entry) + 1);
            }

            UpdateLayoutButtons();
            RebuildDock(visible);
            EmptyHint.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static Rect Column(Rect area, int index, int count)
        {
            double width = (area.Width - Gap * (count - 1)) / count;
            return new Rect(area.X + index * (width + Gap), area.Y, width, area.Height);
        }

        private static Rect GridCell(Rect area, int index, int count)
        {
            double w = (area.Width - Gap) / 2, h = (area.Height - Gap) / 2;
            int row = index / 2, col = index % 2;
            // 3 cửa sổ: ô cuối trải hết chiều ngang.
            if (count == 3 && index == 2) return new Rect(area.X, area.Y + h + Gap, area.Width, h);
            if (count <= 2) return Column(area, index, Math.Max(1, count));
            return new Rect(area.X + col * (w + Gap), area.Y + row * (h + Gap), w, h);
        }

        private Rect FreeRectOf(Entry entry, int index, Rect area)
        {
            if (entry.FreeRect.IsEmpty)
            {
                double w = Math.Min(area.Width, Math.Max(MinWidth_, area.Width * 0.45)), h = Math.Min(area.Height, Math.Max(MinHeight_, area.Height * 0.7));
                entry.FreeRect = new Rect(area.X + 30 * index, area.Y + 30 * index, w, h);
            }
            entry.FreeRect = Clamp(entry.FreeRect, area);
            return entry.FreeRect;
        }

        /// <summary>Giữ cửa sổ trong vùng làm việc và không nhỏ hơn 320×240.</summary>
        private static Rect Clamp(Rect rect, Rect area)
        {
            double w = Math.Clamp(rect.Width, Math.Min(MinWidth_, area.Width), area.Width);
            double h = Math.Clamp(rect.Height, Math.Min(MinHeight_, area.Height), area.Height);
            double x = Math.Clamp(rect.X, area.Left, Math.Max(area.Left, area.Right - w));
            double y = Math.Clamp(rect.Y, area.Top, Math.Max(area.Top, area.Bottom - h));
            return new Rect(x, y, w, h);
        }

        private static void Place(Entry entry, Rect rect)
        {
            var window = entry.Window;
            Canvas.SetLeft(window, rect.X);
            Canvas.SetTop(window, rect.Y);
            window.Width = Math.Max(1, rect.Width);
            window.Height = Math.Max(1, rect.Height);
        }

        /// <summary>Chuyển sang Free nhưng giữ cửa sổ ở đúng chỗ đang đứng.</summary>
        private void EnterFree()
        {
            if (_layout == MergeLayout.Free && _layoutChosen) return;
            var current = EffectiveLayout();
            var visible = VisibleEntries(current).OrderBy(e => _entries.IndexOf(e)).ToList();
            foreach (var entry in visible)
                if (entry.Window.Width > 0 && !double.IsNaN(entry.Window.Width))
                    entry.FreeRect = new Rect(Canvas.GetLeft(entry.Window), Canvas.GetTop(entry.Window), entry.Window.Width, entry.Window.Height);
            _layout = MergeLayout.Free;
            _layoutChosen = true;
        }

        private void Workspace_SizeChanged(object sender, SizeChangedEventArgs e) => Relayout();

        // ── Di chuyển / đổi cỡ / snap ─────────────────────────────────

        private void OnActivated(MergeMiniWindow window)
        {
            var entry = EntryOf(window);
            if (entry.LastActive == _tick) return; // đã là cửa sổ đang dùng
            entry.LastActive = ++_tick;
            Relayout();
        }

        private void OnMoveDelta(MergeMiniWindow window, Vector delta)
        {
            var entry = EntryOf(window);
            if (entry.Maximized) return;
            if (!(_layout == MergeLayout.Free && _layoutChosen))
            {
                EnterFree();
                Relayout();
            }
            var rect = entry.FreeRect;
            rect.Offset(delta);
            entry.FreeRect = Clamp(rect, Area);
            Place(entry, entry.FreeRect);
        }

        private void OnMoveFinished(MergeMiniWindow window, Point pointer)
        {
            var entry = EntryOf(window);
            if (entry.Maximized || !(_layout == MergeLayout.Free && _layoutChosen)) return;
            var area = Area;
            const double edge = 8;
            if (pointer.Y < edge) { entry.Maximized = true; }
            else if (pointer.X < edge) entry.FreeRect = new Rect(area.X, area.Y, (area.Width - Gap) / 2, area.Height);
            else if (pointer.X > Workspace.ActualWidth - edge) entry.FreeRect = new Rect(area.X + (area.Width + Gap) / 2, area.Y, (area.Width - Gap) / 2, area.Height);
            Relayout();
        }

        private void OnResizeDelta(MergeMiniWindow window, Vector delta, string edge)
        {
            var entry = EntryOf(window);
            if (!(_layout == MergeLayout.Free && _layoutChosen) || entry.Maximized) return;
            var rect = entry.FreeRect;
            if (edge.Contains('R')) rect.Width += delta.X;
            if (edge.Contains('B')) rect.Height += delta.Y;
            entry.FreeRect = Clamp(rect, Area);
            Place(entry, entry.FreeRect);
        }

        // ── Kéo thả trang / file ──────────────────────────────────────

        private void OnPagesDropped(MergeMiniWindow target, ReaderSidePanel.PageDragData data, int index, bool copy)
        {
            var host = Host;
            if (host == null) return;
            var result = host.MovePages(data.Source, target.Group, data.Pages, index, copy);
            if (result.Count > 0 && _entries.Any(e => ReferenceEquals(e.Window, target))) target.SelectPages(result);
        }

        // ── Dock ──────────────────────────────────────────────────────

        private void RebuildDock(List<Entry> visible)
        {
            DockChips.Children.Clear();
            var docked = _entries.Where(e => !visible.Contains(e)).ToList();
            foreach (var entry in docked)
            {
                var group = entry.Window.Group;
                var text = new TextBlock { Text = $"{group.FileName}  ·  {group.Pages.Count}", MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                text.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Text");
                var chip = new Border
                {
                    Height = 28,
                    Padding = new Thickness(12, 0, 12, 0),
                    Margin = new Thickness(0, 0, 8, 0),
                    CornerRadius = new CornerRadius(14),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    AllowDrop = true,
                    ToolTip = group.SourcePath + "\nClick to open · drop pages here to append them",
                    Child = text
                };
                chip.SetResourceReference(Border.BackgroundProperty, "Ui.Surface");
                chip.SetResourceReference(Border.BorderBrushProperty, "Ui.Border");
                chip.MouseLeftButtonUp += (_, _) => ShowGroup(group);
                chip.DragOver += (_, e) =>
                {
                    if (!e.Data.GetDataPresent(typeof(ReaderSidePanel.PageDragData))) return;
                    e.Effects = (e.KeyStates & DragDropKeyStates.ControlKey) != 0 ? DragDropEffects.Copy : DragDropEffects.Move;
                    chip.SetResourceReference(Border.BorderBrushProperty, "Ui.Accent");
                    e.Handled = true;
                };
                chip.DragLeave += (_, _) => chip.SetResourceReference(Border.BorderBrushProperty, "Ui.Border");
                chip.Drop += (_, e) =>
                {
                    chip.SetResourceReference(Border.BorderBrushProperty, "Ui.Border");
                    if (e.Data.GetData(typeof(ReaderSidePanel.PageDragData)) is not ReaderSidePanel.PageDragData data) return;
                    Host?.MovePages(data.Source, group, data.Pages, group.Pages.Count, (e.KeyStates & DragDropKeyStates.ControlKey) != 0);
                    e.Handled = true;
                };
                DockChips.Children.Add(chip);
            }
            if (docked.Count == 0)
            {
                var hint = new TextBlock { Text = "Minimized windows appear here.", FontSize = 11.5 };
                hint.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Muted");
                DockChips.Children.Add(hint);
            }
        }

        // ── Thanh công cụ ─────────────────────────────────────────────

        private void NewTemp_Click(object sender, RoutedEventArgs e)
        {
            var group = Host?.CreateTempWindow();
            if (group != null) ShowGroup(group);
        }

        private void Undo_Click(object sender, RoutedEventArgs e) => Host?.Undo();
        private void Redo_Click(object sender, RoutedEventArgs e) => Host?.Redo();
        private void Done_Click(object sender, RoutedEventArgs e) => DoneRequested?.Invoke();
        private void MergeAll_Click(object sender, RoutedEventArgs e) => MergeAllRequested?.Invoke();
        private void OpenFile_Click(object sender, RoutedEventArgs e) => OpenFileRequested?.Invoke();
    }
}
