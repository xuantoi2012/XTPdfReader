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
        private enum MergeLayout { Columns, Rows }

        private sealed class Entry
        {
            public required MergeMiniWindow Window;
            public bool Minimized;
            public bool Maximized;
            public long LastActive;
            public Rect Slot;
        }

        private const double MinColumnWidth = 320, Gap = 10;

        private readonly List<Entry> _entries = new();
        private MergeDraftSession? _draft;
        private readonly Dictionary<PageRow, System.Threading.CancellationTokenSource> _tempThumbnailRequests = new();
        private long _tick;
        private MergeLayout _layout = Enum.TryParse<MergeLayout>(Services.AppSettings.MergeLayoutMode, out var savedLayout) ? savedLayout : MergeLayout.Columns;
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
            ScrollToEntry(entry);
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

        /// <summary>"Clean up": printed PDFs in the fixed Printed folder that no inbox page, merge window or open tab uses go to the Recycle Bin.</summary>
        private void CleanUpPrinted_Click(object sender, RoutedEventArgs e)
        {
            var inUse = new List<string>();
            if (_draft != null) inUse.AddRange(_draft.Documents.SelectMany(d => d.Pages).Select(p => p.SourcePath));
            if (ReaderWindow.Instance?.Session is { } session) inUse.AddRange(session.Documents.SelectMany(d => d.Pages).Select(p => p.SourcePath));
            inUse.AddRange(PrintInboxStore.Load().Select(en => en.Path)); // committed inbox pages that a draft moved out stay protected
            var unused = PrintedFilesService.FindUnused(inUse);
            var owner = Window.GetWindow(this);
            if (unused.Count == 0)
            {
                XTStyle.Controls.XTGrowl.Info("Nothing to clean up: every printed PDF is still in the inbox or open.", owner);
                return;
            }
            long bytes = unused.Sum(f => f.Length);
            var answer = AppDialog.Show(owner,
                $"{unused.Count} printed PDF(s), {PrintedFilesService.FormatSize(bytes)}, are no longer in the inbox or open anywhere.\n\nMove them to the Recycle Bin?\n\nFolder: {PrintedFilesService.Folder}",
                "Clean up printed files", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            int removed = PrintedFilesService.Delete(unused);
            XTStyle.Controls.XTGrowl.Success(removed == unused.Count ? $"Moved {removed} file(s) to the Recycle Bin" : $"Moved {removed} of {unused.Count} file(s); the rest are in use", owner);
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
        // Two modes, no free placement (the user chose this: windows may be re-ordered, never left anywhere).
        //  Columns: at most 3 windows side by side, each with a fixed width taken from the Merge window's size; more windows scroll sideways.
        //  Rows:    one window per row (a single row of thumbnails), at most 3 rows share the height; more windows scroll down.
        // With 1-2 windows (3 in rows) they are stretched to fill the area evenly.

        private const int MaxVisibleSlots = 3;

        private void Layout_Checked(object sender, RoutedEventArgs e)
        {
            if (_updatingLayoutButtons || sender is not RadioButton { Tag: string tag }) return;
            _layout = Enum.Parse<MergeLayout>(tag);
            Services.AppSettings.MergeLayoutMode = tag;
            foreach (var entry in _entries) entry.Maximized = false;
            Relayout();
        }

        private void UpdateLayoutButtons()
        {
            _updatingLayoutButtons = true;
            try { (_layout == MergeLayout.Rows ? LayoutRows : LayoutColumns).IsChecked = true; }
            finally { _updatingLayoutButtons = false; }
        }

        /// <summary>Smallest height of a window in Rows mode: header + one row of thumbnails (+ its sideways scroll bar).</summary>
        private double MinRowHeight => 34 + Math.Round(PageSizeSlider.Value * 0.74) + 38 + 34;

        private void Relayout()
        {
            var shown = _entries.Where(e => !e.Minimized).ToList();
            var maximized = shown.FirstOrDefault(e => e.Maximized);
            if (maximized != null) shown = [maximized];
            int n = shown.Count;
            double viewW = Math.Max(MinColumnWidth, WorkspaceScroll.ActualWidth), viewH = Math.Max(MinRowHeight + 2 * Gap, WorkspaceScroll.ActualHeight);
            double canvasW = viewW, canvasH = viewH;
            bool rows = _layout == MergeLayout.Rows && maximized == null;

            if (maximized != null)
                maximized.Slot = new Rect(Gap, Gap, viewW - 2 * Gap, viewH - 2 * Gap);
            else if (n > 0 && !rows)
            {
                int cols = Math.Clamp((int)Math.Floor((viewW - Gap) / (MinColumnWidth + Gap)), 1, MaxVisibleSlots);
                bool scrolls = n > cols;
                int slots = Math.Min(cols, n);
                double w = (viewW - Gap * (slots + 1)) / slots, h = viewH - 2 * Gap - (scrolls ? SystemParameters.HorizontalScrollBarHeight : 0);
                for (int i = 0; i < n; i++) shown[i].Slot = new Rect(Gap + i * (w + Gap), Gap, w, h);
                canvasW = Gap + n * (w + Gap);
            }
            else if (n > 0)
            {
                int rowSlots = Math.Clamp((int)Math.Floor((viewH - Gap) / (MinRowHeight + Gap)), 1, MaxVisibleSlots);
                bool scrolls = n > rowSlots;
                int slots = Math.Min(rowSlots, n);
                double w = viewW - 2 * Gap - (scrolls ? SystemParameters.VerticalScrollBarWidth : 0), h = Math.Max(MinRowHeight, (viewH - Gap * (slots + 1)) / slots);
                for (int i = 0; i < n; i++) shown[i].Slot = new Rect(Gap, Gap + i * (h + Gap), w, h);
                canvasH = Gap + n * (h + Gap);
            }
            Workspace.Width = canvasW;
            Workspace.Height = canvasH;

            foreach (var entry in _entries)
            {
                bool visible = shown.Contains(entry);
                entry.Window.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                entry.Window.SetRowMode(rows);
                entry.Window.SetResizable(false);
                if (visible) Place(entry, entry.Slot);
                Panel.SetZIndex(entry.Window, entry.Maximized ? 1000 : 1);
            }
            HighlightActive();
            WindowListButton.Text = _entries.Count == 0 ? "0 windows" : $"{_entries.Count(e => !e.Minimized)} of {_entries.Count} windows";
            UpdateLayoutButtons();
            RebuildDock(shown);
            EmptyHint.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>The windows that Merge into one file takes: ticked ones, in window order; the print inbox is never part of it.</summary>
        internal IEnumerable<DocumentGroup> TickedDocuments()
            => _entries.Where(e => !e.Minimized && _draft?.IsInbox(e.Window.Group) != true).Select(e => e.Window.Group);

        internal int WindowCount => _entries.Count;
        internal int ShownWindowCount => _entries.Count(e => !e.Minimized);
        internal IReadOnlyList<Rect> ShownSlots => _entries.Where(e => !e.Minimized).Select(e => e.Slot).ToList();
        internal Size CanvasSize => new(Workspace.Width, Workspace.Height);
        internal Size ViewSize => new(WorkspaceScroll.ActualWidth, WorkspaceScroll.ActualHeight);
        internal void SetAllWindowsShown(bool shown)
        {
            foreach (var entry in _entries) { entry.Minimized = !shown; entry.Maximized &= shown; }
            Relayout();
        }
        internal void ChooseLayout(string name)
        {
            _layout = Enum.Parse<MergeLayout>(name);
            Relayout();
        }

        private void HighlightActive()
        {
            long top = _entries.Where(e => !e.Minimized).Select(e => e.LastActive).DefaultIfEmpty(0).Max();
            foreach (var entry in _entries) entry.Window.SetActive(!entry.Minimized && entry.LastActive == top);
        }

        private static void Place(Entry entry, Rect rect)
        {
            var window = entry.Window;
            Canvas.SetLeft(window, rect.X);
            Canvas.SetTop(window, rect.Y);
            window.Width = Math.Max(1, rect.Width);
            window.Height = Math.Max(1, rect.Height);
        }

        /// <summary>Scroll the canvas so the window's slot is on screen (after the user ticks it or something is added to it).</summary>
        private void ScrollToEntry(Entry entry)
        {
            WorkspaceScroll.UpdateLayout();
            var slot = entry.Slot;
            if (slot.Left < WorkspaceScroll.HorizontalOffset) WorkspaceScroll.ScrollToHorizontalOffset(slot.Left - Gap);
            else if (slot.Right > WorkspaceScroll.HorizontalOffset + WorkspaceScroll.ViewportWidth) WorkspaceScroll.ScrollToHorizontalOffset(slot.Right - WorkspaceScroll.ViewportWidth + Gap);
            if (slot.Top < WorkspaceScroll.VerticalOffset) WorkspaceScroll.ScrollToVerticalOffset(slot.Top - Gap);
            else if (slot.Bottom > WorkspaceScroll.VerticalOffset + WorkspaceScroll.ViewportHeight) WorkspaceScroll.ScrollToVerticalOffset(slot.Bottom - WorkspaceScroll.ViewportHeight + Gap);
        }

        private void Workspace_SizeChanged(object sender, SizeChangedEventArgs e) => Relayout();

        // ── Đổi chỗ window bằng thanh tiêu đề (thứ tự window = thứ tự ghép) ──

        private void OnActivated(MergeMiniWindow window)
        {
            var entry = EntryOf(window);
            if (entry.LastActive == _tick) return; // đã là cửa sổ đang dùng
            entry.LastActive = ++_tick;
            HighlightActive();
        }

        private void OnMoveDelta(MergeMiniWindow window, Vector delta)
        {
            var entry = EntryOf(window);
            if (entry.Maximized) return;
            // The window follows the pointer while dragged; it snaps back (or into the new slot) when released.
            Canvas.SetLeft(window, Canvas.GetLeft(window) + delta.X);
            Canvas.SetTop(window, Canvas.GetTop(window) + delta.Y);
            Panel.SetZIndex(window, 1000);
            var pointer = Mouse.GetPosition(WorkspaceScroll);
            const double edge = 36, step = 24;
            if (pointer.X < edge) WorkspaceScroll.ScrollToHorizontalOffset(WorkspaceScroll.HorizontalOffset - step);
            else if (pointer.X > WorkspaceScroll.ActualWidth - edge) WorkspaceScroll.ScrollToHorizontalOffset(WorkspaceScroll.HorizontalOffset + step);
            if (pointer.Y < edge) WorkspaceScroll.ScrollToVerticalOffset(WorkspaceScroll.VerticalOffset - step);
            else if (pointer.Y > WorkspaceScroll.ActualHeight - edge) WorkspaceScroll.ScrollToVerticalOffset(WorkspaceScroll.VerticalOffset + step);
        }

        private void OnMoveFinished(MergeMiniWindow window, Point pointer)
        {
            var entry = EntryOf(window);
            var target = _entries.FirstOrDefault(e => !ReferenceEquals(e, entry) && !e.Minimized && e.Slot.Contains(pointer));
            if (entry.Maximized || target == null || _draft == null) { Relayout(); return; }
            _draft.MoveDocument(entry.Window.Group, _draft.Documents.IndexOf(target.Window.Group));
            Sync();
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
            Relayout();
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
            DockBar.Height = temporaryGroups.Count == 0 ? 64 : 172; // an empty shelf is just a drop target: give the windows the room
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

        /// <summary>Popup with a tick per window: only ticked windows are shown on the canvas and merged. Select all / Deselect all on top.</summary>
        private void WindowList_Click(object sender, RoutedEventArgs e)
        {
            if (_entries.Count == 0) return;
            var boxes = new List<(CheckBox Box, Entry Entry)>();
            var list = new StackPanel();
            foreach (var entry in _entries)
            {
                var captured = entry;
                var label = new TextBlock
                {
                    Text = $"{captured.Window.Group.FileName}  ·  {captured.Window.Group.Pages.Count}p",
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = captured.Window.Group.SourcePath
                };
                var box = new CheckBox { Content = label, IsChecked = !captured.Minimized, Margin = new Thickness(2, 3, 2, 3) };
                box.Click += (_, _) =>
                {
                    captured.Minimized = box.IsChecked != true;
                    if (!captured.Minimized) { captured.LastActive = ++_tick; }
                    Relayout();
                    if (!captured.Minimized) ScrollToEntry(captured);
                };
                boxes.Add((box, captured));
                list.Children.Add(box);
            }

            void SetAll(bool shown)
            {
                foreach (var (box, entry) in boxes) { entry.Minimized = !shown; box.IsChecked = shown; entry.Maximized &= shown; }
                Relayout();
            }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var selectAll = new XTStyle.Controls.XTButton { Style = (Style)FindResource("UiGhostButton"), Text = "Select all" };
            var deselectAll = new XTStyle.Controls.XTButton { Style = (Style)FindResource("UiGhostButton"), Text = "Deselect all", Margin = new Thickness(8, 0, 0, 0) };
            selectAll.Click += (_, _) => SetAll(true);
            deselectAll.Click += (_, _) => SetAll(false);
            buttons.Children.Add(selectAll);
            buttons.Children.Add(deselectAll);

            var hint = new TextBlock { Text = "Only ticked windows are shown and merged.", FontSize = 11.5, Margin = new Thickness(0, 0, 0, 6) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Muted");
            var content = new DockPanel { Width = 340 };
            DockPanel.SetDock(buttons, Dock.Top);
            DockPanel.SetDock(hint, Dock.Top);
            content.Children.Add(buttons);
            content.Children.Add(hint);
            content.Children.Add(new ScrollViewer { MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list });
            var frame = new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 4, 0, 0), Child = content };
            frame.SetResourceReference(Border.BackgroundProperty, "Ui.Surface");
            frame.SetResourceReference(Border.BorderBrushProperty, "Ui.Border");
            new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = WindowListButton,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true,
                Child = frame,
                IsOpen = true
            };
        }

        internal void UndoDraft() { _draft?.Undo(); Sync(); }
        internal void RedoDraft() { _draft?.Redo(); Sync(); }
        private void Undo_Click(object sender, RoutedEventArgs e) => UndoDraft();
        private void Redo_Click(object sender, RoutedEventArgs e) => RedoDraft();
        private async void MergeAll_Click(object sender, RoutedEventArgs e)
        {
            var documents = TickedDocuments().ToList();
            if (documents.Count == 0)
            {
                XTStyle.Controls.XTGrowl.Info("No window is ticked. Use the windows button to choose which files to merge.", Window.GetWindow(this));
                return;
            }
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
