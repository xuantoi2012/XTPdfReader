using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;
using XTStyle.Controls;

namespace XTPdfMergeApp
{
    /// <summary>The two batch tools of the Edit group: find and replace in a drawn area over many pages, and signatures / stamps put at one place on many pages.</summary>
    public partial class ReaderWindow
    {
        private void ReaderBatchFind_Click(object sender, RoutedEventArgs e) => OpenBatchFind();

        private void ReaderStampPages_Click(object sender, RoutedEventArgs e) => OpenStampPages();

        internal void OpenBatchFind()
        {
            if (_readerPage == null || EditHost == null) { XTGrowl.Info("Open a PDF first.", this); return; }
            string path = _readerPage.SourcePath;
            if (!PdfPermissionDialog.Require(this, new[] { path }, PdfPermissionOperation.Modify)) return;
            CommitTextEdit();
            int count = PageCountOf(path);
            if (count < 1) { AppDialog.Show(this, "The file could not be read.", "Find and replace in an area", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            var window = new BatchFindWindow(path, _readerPage.PageNumber, count) { Owner = this };
            if (window.ShowDialog() != true) return;
            if (window.ObjectsToDelete.Count > 0)
            {
                _ = DeleteObjectsFromWindowAsync(path, window);
                return;
            }
            if (window.Edits.Count == 0) return;
            _ = EditHost.ApplyTextEditsAsync(path, window.Edits, window.Description);
            XTGrowl.Success(window.Description + ". Ctrl+S saves it into the file; Undo takes it back.", this);
        }

        private async Task DeleteObjectsFromWindowAsync(string path, BatchFindWindow window)
        {
            if (EditHost == null) return;
            bool ok = await EditHost.DeleteObjectsAsync(path, window.ObjectsToDelete, window.Description);
            if (ok) XTGrowl.Success(window.Description + ". Undo (Ctrl+Z) brings them all back.", this);
        }

        internal void OpenStampPages()
        {
            if (_readerPage == null || EditHost == null) { XTGrowl.Info("Open a PDF first.", this); return; }
            string path = _readerPage.SourcePath;
            if (!PdfPermissionDialog.Require(this, new[] { path }, PdfPermissionOperation.Annotate)) return;
            int count = PageCountOf(path);
            if (count < 1) { AppDialog.Show(this, "The file could not be read.", "Stamp pages", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            var window = new StampPagesWindow(path, _readerPage.PageNumber, count) { Owner = this };
            if (window.ShowDialog() != true || window.Definition == null) return;
            _ = StampPagesAsync(path, window);
        }

        private async Task StampPagesAsync(string path, StampPagesWindow window)
        {
            var definition = window.Definition!;
            string sub = window.AddNameAndDate ? $"{Environment.UserName} · {DateTime.Now:dd/MM/yyyy}" : definition.Sub;
            string encoded = definition.Encode(window.Opacity, sub);
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
            XTGrowl.Success($"Stamped {changes.Count} page{(changes.Count == 1 ? "" : "s")}. Ctrl+S saves them; Undo takes them all back; drag a corner to resize one.", this);
        }
    }
}
