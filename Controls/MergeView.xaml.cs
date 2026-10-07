using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using XTPdfMergeApp.Services;
using XTPdfMergeApp.Workspace;
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
        private enum MergeLayout { One, Two, Vertical, Three, Grid, Free }

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
        private MergeDraftSession? _draft;
        private readonly Dictionary<PageRow, System.Threading.CancellationTokenSource> _tempThumbnailRequests = new();
        private long _tick;
        private MergeLayout _layout = MergeLayout.Two;
        private bool _layoutChosen; // false = tự chọn layout theo số cửa sổ
        private bool _updatingLayoutButtons;
        private bool _isExporting;
        private bool _restoredDraft;
        internal bool HasUnsavedDraft => _restoredDraft || _draft?.History.CanUndo == true || _draft?.Documents.Any(d => d.IsDirty && !_draft.IsInbox(d)) == true;

        public MergeView()
        {
            InitializeComponent();
            UpdateLayoutButtons();
            MergeLayersToggle.IsChecked = Services.AppSettings.MergeOptionsSaved.MergeLayers;
        }

        private void PageSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            foreach (var entry in _entries) entry.Window.ThumbWidth = e.NewValue;
        }

        private void MergeLayersToggle_Click(object sender, RoutedEventArgs e)
            => Services.AppSettings.MergeOptionsSaved = Services.AppSettings.MergeOptionsSaved with { MergeLayers = MergeLayersToggle.IsChecked == true };

        internal event Action? DoneRequested;
        internal event Action? OpenFileRequested;
        internal event Action<string>? ViewTemporaryRequested;
        internal event EventHandler? HistoryStateChanged;

        internal bool CanUndo => _draft?.History.CanUndo == true;
        internal bool CanRedo => _draft?.History.CanRedo == true;
        internal List<SavedDocument> CaptureDraft() => _draft == null ? new() : SessionRecoveryStore.CaptureDocuments(_draft.Documents.Where(d => !_draft.IsInbox(d)), _draft.IsTemporary); // the inbox keeps its own file (PrintInboxStore)
        internal void RestoreDraft(IEnumerable<(DocumentGroup Document, bool Temporary)> documents)
        {
            _restoredDraft = true;
            CancelTempThumbnailRequests();
            _draft = new MergeDraftSession();
            _draft.Restore(documents);
            _draft.History.StateChanged += (_, _) => HistoryStateChanged?.Invoke(this, EventArgs.Empty);
            Sync();
            HistoryStateChanged?.Invoke(this, EventArgs.Empty);
        }
        internal string? UndoDescription => _draft?.History.UndoDescription;
        internal string? RedoDescription => _draft?.History.RedoDescription;

        /// <summary>Tạo lại bản nháp từ tab đang mở. Thao tác trong Merge chỉ sửa bản này.</summary>
        internal void BeginSession(IEnumerable<DocumentGroup> groups)
        {
            _restoredDraft = false;
            CancelTempThumbnailRequests();
            _draft = new MergeDraftSession();
            _draft.Begin(groups);
            _draft.History.StateChanged += (_, _) => HistoryStateChanged?.Invoke(this, EventArgs.Empty);
            Sync();
            HistoryStateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Đưa file này lên (mở lại nếu đang ở dock).</summary>
        internal void ShowGroup(DocumentGroup group)
        {
            var draftGroup = _draft?.GetDraftFor(group);
            if (draftGroup == null) return;
            var entry = _entries.FirstOrDefault(e => ReferenceEquals(e.Window.Group, draftGroup));
            if (entry == null) return;
            entry.Minimized = false;
            entry.LastActive = ++_tick;
            Relayout();
            HistoryStateChanged?.Invoke(this, EventArgs.Empty);
        }

        internal async Task ImportIncomingPdfAsync(string path)
        {
            if (_draft == null) BeginSession(Array.Empty<DocumentGroup>());
            if (!File.Exists(path) || _draft == null) return;
            var result = await PdfThumbnailService.TryGetPageCountAsync(path).ConfigureAwait(true);
            if (result.Failure != PdfOpenFailure.None || result.PageCount <= 0) return;
            var inbox = _draft.AddIncomingPdf(path, result.PageCount);
            Sync();
            // A hidden inbox comes back when something arrives; a visible one is left where the user has it.
            if (_entries.FirstOrDefault(e => ReferenceEquals(e.Window.Group, inbox)) is { Minimized: true } entry)
            {
                entry.Minimized = false;
                entry.LastActive = ++_tick;
                Relayout();
            }
            HistoryStateChanged?.Invoke(this, EventArgs.Empty);
        }

        // ── Đồng bộ với danh sách file ────────────────────────────────

        private void Sync()
        {
            if (_draft == null) return;
            var shown = _draft.DisplayedDocuments.ToList();
            foreach (var entry in _entries.Where(e => !shown.Contains(e.Window.Group)).ToList())
            {
                Workspace.Children.Remove(entry.Window);
                _entries.Remove(entry);
            }
            foreach (var group in shown)
            {
                if (_entries.Any(e => ReferenceEquals(e.Window.Group, group))) continue;
                var window = new MergeMiniWindow(group, _draft.IsInbox(group)) { ThumbWidth = PageSizeSlider.Value };
                Hook(window);
                Workspace.Children.Add(window);
                _entries.Add(new Entry { Window = window, LastActive = ++_tick });
            }
            _entries.Sort((a, b) => _draft.Documents.IndexOf(a.Window.Group).CompareTo(_draft.Documents.IndexOf(b.Window.Group)));
            WindowListButton.Text = _entries.Count == 1 ? "1 window" : $"{_entries.Count} windows";
            int inboxPages = _draft.Inbox?.Pages.Count ?? 0;
            InboxButton.Text = inboxPages == 0 ? "Inbox" : $"Inbox ({inboxPages})";
            Relayout();
            HistoryStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Hook(MergeMiniWindow window)
        {
            window.Activated += OnActivated;
            window.MoveDelta += OnMoveDelta;
            window.MoveFinished += OnMoveFinished;
            window.ResizeDelta += OnResizeDelta;
            window.ToggleMaximizeRequested += ToggleMaximize;
            // The inbox is never a merge window: minimize/close only hide it (the "Print inbox" button brings it back).
            window.MinimizeRequested += w => { if (HideIfInbox(w)) return; _draft?.MoveToTemporaryShelf(w.Group); Sync(); };
            window.CloseRequested += w => { if (HideIfInbox(w)) return; _draft?.RemoveDocument(w.Group); Sync(); };
            window.InboxActionRequested += OnInboxAction;
            window.SaveRequested += async w => await ExportAsync(w.Group);
            window.PagesDropped += OnPagesDropped;
            window.DeleteRequested += (w, pages) => { _draft?.DeletePages(w.Group, pages); Sync(); };
        }

        private bool HideIfInbox(MergeMiniWindow window)
        {
            if (_draft == null || !_draft.IsInbox(window.Group)) return false;
            EntryOf(window).Minimized = true;
            Relayout();
            return true;
        }

        private void Inbox_Click(object sender, RoutedEventArgs e)
        {
            if (_draft?.Inbox is not { Pages.Count: > 0 } inbox)
            {
                XTStyle.Controls.XTGrowl.Info("The print inbox is empty. Printed and plotted PDFs arrive here.", Window.GetWindow(this));
                return;
            }
            ShowGroup(inbox);
        }

        private void OnInboxAction(MergeMiniWindow window, string action, IReadOnlyList<PageRow> pages)
        {
            if (_draft == null || pages.Count == 0) return;
            string path = pages[0].SourcePath;
            switch (action)
            {
                case "view": ViewTemporaryRequested?.Invoke(path); break;
                case "folder":
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
                    catch { /* explorer missing / path gone */ }
                    break;
                case "remove":
                    PrintInboxStore.Remove(pages.Select(p => new PrintInboxStore.Entry(p.SourcePath, p.PageNumber)));
                    _draft.DeletePages(window.Group, pages);
                    Sync();
                    break;
            }
        }

        private Entry EntryOf(MergeMiniWindow window) => _entries.First(e => ReferenceEquals(e.Window, window));

        // ── Layout ────────────────────────────────────────────────────

        private static int Capacity(MergeLayout layout) => layout switch { MergeLayout.One => 1, MergeLayout.Two or MergeLayout.Vertical => 2, MergeLayout.Three => 3, _ => 4 };

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
                (layout switch { MergeLayout.One => LayoutOne, MergeLayout.Two => LayoutTwo, MergeLayout.Vertical => LayoutVertical, MergeLayout.Three => LayoutThree, MergeLayout.Grid => LayoutGrid, _ => LayoutFree }).IsChecked = true;
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
                    MergeLayout.Vertical => Row(area, i, Math.Max(2, ordered.Count)),
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

        private static Rect Row(Rect area, int index, int count)
        {
            double height = (area.Height - Gap * (count - 1)) / count;
            return new Rect(area.X, area.Y + index * (height + Gap), area.Width, height);
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
            if (pointer.Y < edge) { MaximizeEntry(entry); return; }
            else if (pointer.X < edge) entry.FreeRect = new Rect(area.X, area.Y, (area.Width - Gap) / 2, area.Height);
            else if (pointer.X > Workspace.ActualWidth - edge) entry.FreeRect = new Rect(area.X + (area.Width + Gap) / 2, area.Y, (area.Width - Gap) / 2, area.Height);
            Relayout();
        }

        private void ToggleMaximize(MergeMiniWindow window)
        {
            var entry = EntryOf(window);
            if (entry.Maximized)
            {
                entry.Maximized = false;
                Relayout();
                return;
            }
            MaximizeEntry(entry);
        }

        private void MaximizeEntry(Entry entry)
        {
            foreach (var item in _entries) item.Maximized = false;
            entry.Maximized = true;
            entry.Minimized = false;
            entry.LastActive = ++_tick;
            _layout = MergeLayout.One;
            _layoutChosen = true;
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
            var result = _draft?.MovePages(data.Source, target.Group, data.Pages, index, copy) ?? [];
            Sync();
            if (result.Count > 0 && _entries.Any(e => ReferenceEquals(e.Window, target))) target.SelectPages(result);
        }

        // ── Dock ──────────────────────────────────────────────────────

        private void RebuildDock(List<Entry> visible)
        {
            DockChips.Children.Clear();
            var temporaryGroups = _draft?.TemporaryDocuments.ToList() ?? [];
            foreach (var group in temporaryGroups)
            {
                var cardContent = new Grid();
                cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(82) });
                cardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var firstPage = group.Pages.FirstOrDefault();
                var preview = new PageThumbnailImage { Stretch = Stretch.Uniform, Margin = new Thickness(4) };
                if (firstPage != null)
                {
                    preview.DataContext = firstPage;
                    preview.SetBinding(PageThumbnailImage.SourceProperty, new Binding(nameof(PageRow.Thumbnail)));
                    RequestTempThumbnail(firstPage);
                }
                var previewFrame = new Border { Background = Brushes.White, BorderThickness = new Thickness(1), Child = preview };
                previewFrame.SetResourceReference(Border.BorderBrushProperty, "Ui.Border");
                cardContent.Children.Add(previewFrame);

                // Hover: 2 nút nhỏ nổi trên preview — mở thành window riêng (giống click cả thẻ) hoặc
                // trả các trang về đúng file nguồn của chúng (nếu file đó vẫn đang mở).
                var canReturn = group.Pages.Any(p => _draft?.WindowDocuments.Any(d =>
                    d != group && string.Equals(d.SourcePath, p.SourcePath, StringComparison.OrdinalIgnoreCase)) == true);
                var hoverActions = new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 3, 3, 0), Visibility = Visibility.Collapsed
                };
                hoverActions.Children.Add(CreateChipActionButton("Ui.Icon.fullscreen", "Open as its own window", () =>
                {
                    _draft?.PromoteTemporaryDocument(group);
                    Sync();
                    ShowGroup(group);
                }));
                hoverActions.Children.Add(CreateChipActionButton("Ui.Icon.eye", "View in Reader", () =>
                    ViewTemporaryRequested?.Invoke(group.SourcePath)));
                if (canReturn)
                {
                    hoverActions.Children.Add(CreateChipActionButton("Ui.Icon.undo", "Return pages to their source file", () =>
                    {
                        if (_draft?.ReturnPagesToSource(group) == true) Sync();
                    }));
                }
                Grid.SetRow(hoverActions, 0);
                cardContent.Children.Add(hoverActions);
                var text = new TextBlock
                {
                    Text = $"{group.FileName}\n{group.Pages.Count} pages · click to open",
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11.5,
                    Margin = new Thickness(2, 6, 2, 0)
                };
                text.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Text");
                Grid.SetRow(text, 1);
                cardContent.Children.Add(text);
                var card = new Border
                {
                    Width = 142,
                    Height = 124,
                    Padding = new Thickness(7),
                    Margin = new Thickness(0, 0, 10, 0),
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    AllowDrop = true,
                    ToolTip = "Temporary group — not included in Merge all. Click to open as a window; drop pages here to append.",
                    Child = cardContent
                };
                card.SetResourceReference(Border.BackgroundProperty, "Ui.Surface");
                card.SetResourceReference(Border.BorderBrushProperty, "Ui.Border");
                card.MouseEnter += (_, _) => hoverActions.Visibility = Visibility.Visible;
                card.MouseLeave += (_, _) => hoverActions.Visibility = Visibility.Collapsed;
                card.MouseLeftButtonUp += (_, _) =>
                {
                    _draft?.PromoteTemporaryDocument(group);
                    Sync();
                    ShowGroup(group);
                };
                card.DragOver += (_, e) =>
                {
                    if (!e.Data.GetDataPresent(typeof(ReaderSidePanel.PageDragData))) return;
                    e.Effects = (e.KeyStates & DragDropKeyStates.ControlKey) != 0 ? DragDropEffects.Copy : DragDropEffects.Move;
                    card.SetResourceReference(Border.BorderBrushProperty, "Ui.Accent");
                    ShowTempDropHighlight(true);
                    e.Handled = true;
                };
                card.DragLeave += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "Ui.Border");
                card.Drop += (_, e) =>
                {
                    card.SetResourceReference(Border.BorderBrushProperty, "Ui.Border");
                    ShowTempDropHighlight(false);
                    if (e.Data.GetData(typeof(ReaderSidePanel.PageDragData)) is not ReaderSidePanel.PageDragData data) return;
                    _draft?.MovePages(data.Source, group, data.Pages, group.Pages.Count, (e.KeyStates & DragDropKeyStates.ControlKey) != 0);
                    Sync();
                    e.Handled = true;
                };
                DockChips.Children.Add(card);
            }
            DockInfo.Text = "Temporary groups are not included in Merge all · drag = move · Ctrl+drag = copy";
            if (temporaryGroups.Count == 0)
            {
                var hint = new TextBlock { Text = "Drop pages anywhere in this shelf to create a temporary group.", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                hint.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Muted");
                DockChips.Children.Add(hint);
            }
        }

        /// <summary>Nút tròn nhỏ nổi trên thẻ temp shelf, chỉ hiện khi hover thẻ đó.</summary>
        private static Border CreateChipActionButton(string iconResource, string tooltip, Action onClick)
        {
            var icon = new Viewbox { Width = 12, Height = 12 };
            var path = new System.Windows.Shapes.Path { Data = (Geometry)Application.Current.FindResource(iconResource), Fill = Brushes.White };
            icon.Child = new Canvas { Width = 24, Height = 24, Children = { path } };
            var button = new Border
            {
                Width = 22, Height = 22, Margin = new Thickness(3, 0, 0, 0), CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(Color.FromArgb(196, 26, 32, 44)), Cursor = Cursors.Hand,
                ToolTip = tooltip, Child = icon
            };
            button.MouseLeftButtonDown += (_, e) => { onClick(); e.Handled = true; };
            return button;
        }

        private void TempShelf_DragOver(object sender, DragEventArgs e)
        {
            EmptyArea_DragOver(sender, e);
            ShowTempDropHighlight(e.Effects != DragDropEffects.None);
        }

        private void TempShelf_DragLeave(object sender, DragEventArgs e) => ShowTempDropHighlight(false);

        private void ShowTempDropHighlight(bool show)
            => TempDropHighlight.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        private void RequestTempThumbnail(PageRow page)
        {
            if (page.Thumbnail != null || _tempThumbnailRequests.ContainsKey(page)) return;
            var cts = new System.Threading.CancellationTokenSource();
            _tempThumbnailRequests[page] = cts;
            _ = LoadTempThumbnailAsync(page, cts);
        }

        private async System.Threading.Tasks.Task LoadTempThumbnailAsync(PageRow page, System.Threading.CancellationTokenSource cts)
        {
            try { await ThumbnailCache.LoadPreviewAsync(page, cts.Token, PdfRenderPriority.Thumbnail); }
            finally
            {
                if (_tempThumbnailRequests.TryGetValue(page, out var current) && ReferenceEquals(current, cts))
                    _tempThumbnailRequests.Remove(page);
                cts.Dispose();
            }
        }

        private void CancelTempThumbnailRequests()
        {
            foreach (var cts in _tempThumbnailRequests.Values) cts.Cancel();
            _tempThumbnailRequests.Clear();
        }

        // ── Thanh công cụ ─────────────────────────────────────────────

        // ── Thả trang: canvas tạo window, Temp shelf chỉ tạo nhóm tạm ──

        private void EmptyArea_DragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            e.Effects = e.Data.GetDataPresent(typeof(ReaderSidePanel.PageDragData))
                ? (e.KeyStates & DragDropKeyStates.ControlKey) != 0 ? DragDropEffects.Copy : DragDropEffects.Move
                : DragDropEffects.None;
        }

        private void Workspace_Drop(object sender, DragEventArgs e) => DropIntoNewGroup(e, temporary: false);
        private void Dock_Drop(object sender, DragEventArgs e)
        {
            ShowTempDropHighlight(false);
            DropIntoNewGroup(e, temporary: true);
        }

        private void DropIntoNewGroup(DragEventArgs e, bool temporary)
        {
            if (_draft == null || e.Data.GetData(typeof(ReaderSidePanel.PageDragData)) is not ReaderSidePanel.PageDragData data) return;
            e.Handled = true;
            var target = temporary ? _draft.CreateTemporaryDocument() : _draft.CreateWindowDocument();
            _draft.MovePages(data.Source, target, data.Pages, 0, (e.KeyStates & DragDropKeyStates.ControlKey) != 0);
            Sync();
            if (temporary) return;
            var entry = _entries.FirstOrDefault(x => ReferenceEquals(x.Window.Group, target));
            if (entry != null) { entry.LastActive = ++_tick; Relayout(); }
        }

        private void NewTemp_Click(object sender, RoutedEventArgs e)
        {
            var group = _draft?.CreateWindowDocument();
            if (group != null) { Sync(); ShowGroup(group); }
        }

        private void WindowList_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = WindowListButton };
            foreach (var entry in _entries)
            {
                var captured = entry;
                var item = new MenuItem
                {
                    Header = $"{captured.Window.Group.FileName}  (#{_entries.IndexOf(captured) + 1})",
                    IsCheckable = true,
                    IsChecked = captured.Window.Visibility == Visibility.Visible
                };
                item.Click += (_, _) =>
                {
                    captured.Minimized = false;
                    captured.LastActive = ++_tick;
                    Relayout();
                };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        internal void UndoDraft() { _draft?.Undo(); Sync(); }
        internal void RedoDraft() { _draft?.Redo(); Sync(); }
        private void Undo_Click(object sender, RoutedEventArgs e) => UndoDraft();
        private void Redo_Click(object sender, RoutedEventArgs e) => RedoDraft();
        private void Done_Click(object sender, RoutedEventArgs e) => DoneRequested?.Invoke();
        private async void MergeAll_Click(object sender, RoutedEventArgs e)
        {
            var documents = _draft?.WindowDocuments.ToList() ?? [];
            if (documents.Count == 0) return;
            var orderWindow = new MergeOrderWindow(documents) { Owner = Window.GetWindow(this) };
            if (orderWindow.ShowDialog() != true) return;
            await ExportAsync(orderWindow.OrderedDocuments);
        }
        private void OpenFile_Click(object sender, RoutedEventArgs e) => OpenFileRequested?.Invoke();

        private System.Threading.Tasks.Task ExportAsync(DocumentGroup group)
            => ExportAsync([group]);

        private async System.Threading.Tasks.Task ExportAsync(IEnumerable<DocumentGroup> documents)
        {
            if (_isExporting) return;
            var pages = documents.SelectMany(g => g.Pages).Select(p => (p.SourcePath, p.PageNumber)).ToList();
            if (pages.Count == 0) return;
            string folder = AppSettings.LastMergeFolder;
            if (!System.IO.Directory.Exists(folder)) folder = System.IO.Path.GetDirectoryName(pages[0].SourcePath) ?? "";
            var dialog = new MergeSaveWindow(pages, folder) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
            if (!await SignedPdfConfirmation.ConfirmAsync(Window.GetWindow(this), pages.Select(p => p.SourcePath),
                "Merge PDFs", false, new[] { dialog.OutputPath })) return;

            var exported = pages.Select(p => new PrintInboxStore.Entry(p.SourcePath, p.PageNumber)).ToList();
            string error = "";
            Mouse.OverrideCursor = Cursors.Wait;
            _isExporting = true;
            bool ok;
            try
            {
                pages = await AnnotationWorkingCopy.MapAsync(pages);
                ok = await System.Threading.Tasks.Task.Run(() =>
                    XTPdfMerger.TryMergePages(pages, dialog.OutputPath, out error, null,
                        mergeLayersByName: dialog.Options.MergeLayers, options: dialog.Options));
            }
            catch (Exception ex) { ok = false; error = ex.Message; }
            finally { _isExporting = false; Mouse.OverrideCursor = null; }
            if (!ok)
            {
                AppDialog.Show(Window.GetWindow(this), "Could not create the PDF:\n" + error, "Merge", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            PrintInboxStore.Remove(exported); // merged pages are consumed: they leave the print inbox for good
            XTStyle.Controls.XTGrowl.Success($"Created {System.IO.Path.GetFileName(dialog.OutputPath)}", Window.GetWindow(this));
        }
    }
}
