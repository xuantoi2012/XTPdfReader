using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;
using XTStyle.Controls;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;

namespace XTPdfMergeApp
{
    /// <summary>
    /// The two batch tools of the Edit group, as a panel on the right of the page view: find and replace in an area drawn on the page, and signatures / stamps put at one place on many pages.
    /// The rectangle is drawn on the page being read (<see cref="ReaderAreaSurface"/>), so the reader's own zoom, scroll and preview are used instead of a second picture.
    /// </summary>
    public partial class ReaderWindow
    {
        private ReaderAreaSurface? _areaSurface;
        private Action? _toolPanelClosed;
        private string _toolPanelKind = "";

        private void ReaderBatchFind_Click(object sender, RoutedEventArgs e) => OpenBatchFind();

        private void ReaderStampPages_Click(object sender, RoutedEventArgs e) => OpenStampPages();

        private void ReaderToolPanelClose_Click(object sender, RoutedEventArgs e) => CloseToolPanel();

        private ReaderAreaSurface EnsureAreaSurface() => _areaSurface ??= new ReaderAreaSurface(ReaderInteractionLayer, new ReaderAreaHost(
            CurrentRow: () => _readerPage,
            PageToLayer: (row, u, v) => TryPageToLayer(row, u, v, out Point p) ? p : null,
            HostToPage: (row, point) => TryHostToPage(row, point, out double u, out double v) ? (u, v) : null,
            HitPage: point => TryHitPage(point, out var hit) ? (hit.Row, hit.U, hit.V) : null,
            Navigate: NavigateAreaAsync,
            SetCursor: cursor =>
            {
                if (cursor == null) { ApplyToolCursor(); return; }
                ReaderContentHost.Cursor = cursor;
                ReaderContentHost.ForceCursor = true;
            }));

        /// <summary>The tool panel asks to show a page of a file: the reader goes there.</summary>
        private Task NavigateAreaAsync(string path, IReadOnlyList<int> pages, int? start)
        {
            if (_readerGroup == null || pages.Count == 0) return Task.CompletedTask;
            int wanted = start ?? pages[0];
            var row = _readerGroup.Pages.FirstOrDefault(p => string.Equals(p.SourcePath, path, StringComparison.OrdinalIgnoreCase) && p.PageNumber == wanted)
                      ?? _readerGroup.Pages.FirstOrDefault(p => string.Equals(p.SourcePath, path, StringComparison.OrdinalIgnoreCase) && pages.Contains(p.PageNumber));
            if (row != null) NavigateToRow(_readerGroup, row);
            return Task.CompletedTask;
        }

        /// <summary>Shows <paramref name="body"/> in the right-hand panel (one at a time); <paramref name="closed"/> runs when it goes away.</summary>
        private void ShowToolPanel(string kind, string title, UIElement body, Action closed, double width = 380)
        {
            CloseToolPanel();
            _toolPanelKind = kind;
            ReaderBatchFindButton.Tag = kind == "find" ? "Active" : null;
            ReaderStampPagesButton.Tag = kind == "stamp" ? "Active" : null;
            ReaderToolPanelTitle.Text = Loc.T(title);
            ReaderToolPanelBody.Content = body;
            Loc.ApplyTree(ReaderToolPanel);
            ReaderToolPanel.Visibility = ReaderToolSplitter.Visibility = Visibility.Visible;
            ReaderToolSplitterColumn.Width = new GridLength(4);
            ReaderToolPanelColumn.MinWidth = 320;
            ReaderToolPanelColumn.Width = new GridLength(width);
            _toolPanelClosed = closed;
        }

        internal void CloseToolPanel()
        {
            if (ReaderToolPanel.Visibility != Visibility.Visible) return;
            var closed = _toolPanelClosed;
            _toolPanelClosed = null;
            _toolPanelKind = "";
            ReaderBatchFindButton.Tag = ReaderStampPagesButton.Tag = null;
            ReaderToolPanel.Visibility = ReaderToolSplitter.Visibility = Visibility.Collapsed;
            ReaderToolPanelBody.Content = null;
            ReaderToolPanelColumn.MinWidth = 0;
            ReaderToolPanelColumn.Width = new GridLength(0);
            ReaderToolSplitterColumn.Width = new GridLength(0);
            _areaSurface?.Deactivate();
            closed?.Invoke();
        }

        internal bool ToolPanelOpen => ReaderToolPanel.Visibility == Visibility.Visible;

        internal void OpenBatchFind()
        {
            if (_toolPanelKind == "find") { CloseToolPanel(); return; } // a second click closes it
            if (_readerPage == null || EditHost == null) { Growl.Info("Open a PDF first.", this); return; }
            string path = _readerPage.SourcePath;
            if (!PdfPermissionDialog.Require(this, new[] { path }, PdfPermissionOperation.Modify)) return;
            CommitTextEdit();
            int count = PageCountOf(path);
            if (count < 1) { AppDialog.Show(this, "The file could not be read.", "Find and replace in an area", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            var surface = EnsureAreaSurface();
            surface.PlaceMode = false;
            surface.BoundPath = path;
            surface.Activate();
            var panel = new BatchFindPanel(surface, path, _readerPage.PageNumber, count);
            panel.Applied += p => ApplyBatchFind(path, p);
            ShowToolPanel("find", "Find in area", panel, panel.Detach);
            _ = panel.StartAsync();
        }

        private void ApplyBatchFind(string path, BatchFindPanel panel)
        {
            if (EditHost == null) return;
            if (panel.ObjectsToDelete.Count > 0)
            {
                CloseToolPanel();
                _ = DeleteObjectsFromPanelAsync(path, panel);
                return;
            }
            if (panel.Edits.Count == 0) return;
            CloseToolPanel();
            _ = EditHost.ApplyTextEditsAsync(path, panel.Edits, panel.Description);
            Growl.Success(panel.Description + ". Ctrl+S saves it into the file; Undo takes it back.", this);
        }

        private async Task DeleteObjectsFromPanelAsync(string path, BatchFindPanel panel)
        {
            if (EditHost == null) return;
            await EditHost.ApplyObjectDeleteAsync(path, panel.ObjectsToDelete, panel.Description);
            Growl.Success(panel.Description + ". They are painted out now; Ctrl+S removes them from the file, Undo brings them back.", this);
        }

        internal void OpenStampPages()
        {
            if (_toolPanelKind == "stamp") { CloseToolPanel(); return; }
            if (_readerPage == null || EditHost == null) { Growl.Info("Open a PDF first.", this); return; }
            string path = _readerPage.SourcePath;
            if (!PdfPermissionDialog.Require(this, new[] { path }, PdfPermissionOperation.Annotate)) return;
            int count = PageCountOf(path);
            if (count < 1) { AppDialog.Show(this, "The file could not be read.", "Stamp pages", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            var surface = EnsureAreaSurface();
            surface.PlaceMode = true;
            surface.BoundPath = path;
            surface.Activate();
            var panel = new StampPagesPanel(surface, path, _readerPage.PageNumber, count);
            panel.Applied += p => { if (p.Definition == null) return; CloseToolPanel(); _ = StampPagesAsync(path, p); };
            ShowToolPanel("stamp", "Stamp pages", panel, panel.Detach);
            _ = panel.StartAsync();
        }

        private async Task StampPagesAsync(string path, StampPagesPanel window)
        {
            var definition = window.Definition!;
            string sub = window.AddNameAndDate ? $"{Environment.UserName} · {DateTime.Now:dd/MM/yyyy}" : definition.Sub;
            string encoded = definition.Encode(window.StampOpacity, sub);
            string batch = "batch-" + Guid.NewGuid().ToString("N")[..12]; // everything this run makes shares it ("select similar" later)
            var changes = new List<QuickAnnotationChange>();
            foreach (var (page, u1, v1, u2, v2) in window.Placements)
            {
                var spec = new QuickAnnotationSpec(NewAnnotationName(), QuickAnnotationKind.Stamp, page, u1, v1, u2, v2, encoded)
                {
                    Format = encoded, Template = "stamp:" + definition.Id, Batch = batch
                };
                changes.Add(new QuickAnnotationChange(null, spec));
            }
            if (changes.Count == 0 || EditHost == null) return;
            await EditHost.ApplyAnnotationChangesAsync(path, changes, $"Stamp on {changes.Count} page{(changes.Count == 1 ? "" : "s")}");
            Growl.Success($"Stamped {changes.Count} page{(changes.Count == 1 ? "" : "s")}. Ctrl+S saves them; Undo takes them all back; drag a corner to resize one.", this);
        }
    }
}
