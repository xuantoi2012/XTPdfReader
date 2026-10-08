using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using iText.Kernel.Pdf;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;
using XTPdfMergeApp.Services.Ocr;
using XTStyle.Controls;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;

namespace XTPdfMergeApp
{
    /// <summary>
    /// OCR of scanned pages: the ribbon button, Ctrl+K, the hint in Find and the page menus open <see cref="OcrWindow"/>. The text is kept in the open file as an
    /// unsaved change (Ctrl+S writes it, Undo takes it away); the dialog can also write a searchable copy.
    /// </summary>
    public partial class ReaderWindow
    {
        private void ReaderOcr_Click(object sender, RoutedEventArgs e) => StartOcr();

        /// <summary>OCR for the file on screen (the dialog chooses the pages).</summary>
        internal void StartOcr()
        {
            if (_readerGroup == null || _readerPage == null)
            {
                XTGrowl.Info("Open a scanned PDF first.", this);
                return;
            }
            RunOcr(_readerPage.SourcePath, new[] { _readerPage.PageNumber }, quick: false);
        }

        /// <summary>OCR of these pages (source page numbers, one file): reads at once, keeps the text until Ctrl+S.</summary>
        internal void OcrPages(IReadOnlyList<PageRow> rows)
        {
            if (!SinglePdf(rows, "OCR", out string path, out var pages)) return;
            RunOcr(path, pages, quick: true);
        }

        private void RunOcr(string path, IReadOnlyList<int> pages, bool quick)
        {
            if (!Controls.PdfPermissionDialog.Require(this, new[] { path }, PdfPermissionOperation.Modify)) return;
            int pageCount;
            try
            {
                using var reader = new PdfReader(path);
                using var document = new PdfDocument(reader);
                pageCount = document.GetNumberOfPages();
            }
            catch (Exception ex)
            {
                AppDialog.Show(this, "The file could not be read: " + ex.Message, "OCR", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var window = new OcrWindow(path, pages.Count > 0 ? pages[0] : 1, pageCount) { Owner = this, AutoStart = quick && OcrService.IsAvailable() };
            if (quick) window.SelectPages(pages);
            if (window.ShowDialog() != true) return;
            if (window.PendingWords is { Count: > 0 } words && EditHost != null)
            {
                int total = words.Values.Sum(w => w.Count);
                _ = EditHost.ApplyOcrAsync(path, words.ToDictionary(p => p.Key, p => (IReadOnlyList<OcrWord>?)p.Value), $"OCR {words.Count} page{(words.Count == 1 ? "" : "s")}");
                XTGrowl.Success($"OCR text added to {words.Count} page{(words.Count == 1 ? "" : "s")} ({total} words). Ctrl+S saves it into the file.", this);
            }
            else if (window.OutputPath is { } output && File.Exists(output))
            {
                if (EditHost != null) _ = EditHost.OpenPathsAsync(new[] { output });
                XTGrowl.Success("Searchable copy opened: " + Path.GetFileName(output), this);
            }
        }

        /// <summary>"Read sheet info" for these pages only: the saved areas of their paper size are read at once (OCR where there is no text), the user checks and writes.</summary>
        internal void ReadSheetInfoForPages(IReadOnlyList<PageRow> rows)
        {
            if (!SinglePdf(rows, "Read sheet info", out string path, out var pages)) return;
            OnReadSheetInfo(path, pages, pages[0], autoRead: true);
        }

        /// <summary>The pages must all come from one file (a window can mix files). Page numbers are those of the file.</summary>
        private bool SinglePdf(IReadOnlyList<PageRow> rows, string title, out string path, out List<int> pages)
        {
            path = "";
            pages = new List<int>();
            if (rows.Count == 0) return false;
            var sources = rows.Select(r => r.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (sources.Count != 1)
            {
                AppDialog.Show(this, "The chosen pages come from different files. Choose pages of one file.", title, MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            path = sources[0];
            pages = rows.Select(r => r.PageNumber).Distinct().OrderBy(n => n).ToList();
            return true;
        }
    }
}
