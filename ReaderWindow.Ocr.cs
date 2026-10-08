using System;
using System.IO;
using System.Linq;
using System.Windows;
using iText.Kernel.Pdf;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;
using XTStyle.Controls;

namespace XTPdfMergeApp
{
    /// <summary>OCR of scanned pages: the ribbon button, Ctrl+K and the hint in Find open <see cref="OcrWindow"/> for the file on screen; the searchable copy opens in a new tab.</summary>
    public partial class ReaderWindow
    {
        private void ReaderOcr_Click(object sender, RoutedEventArgs e) => StartOcr();

        internal void StartOcr()
        {
            if (_readerGroup == null || _readerPage == null)
            {
                XTGrowl.Info("Open a scanned PDF first.", this);
                return;
            }
            string path = _readerPage.SourcePath;
            if (!Controls.PdfPermissionDialog.Require(this, new[] { path }, PdfPermissionOperation.Modify)) return;
            if (_readerGroup.IsDirty &&
                AppDialog.Show(this, "This file has changes that are not saved. OCR reads the saved file on disk, so those changes will not be in the copy. Continue?", "OCR",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
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
            var window = new OcrWindow(path, _readerPage.PageNumber, pageCount) { Owner = this };
            if (window.ShowDialog() != true || window.OutputPath is not { } output || !File.Exists(output)) return;
            if (EditHost != null) _ = EditHost.OpenPathsAsync(new[] { output });
            XTGrowl.Success("Searchable copy opened: " + Path.GetFileName(output), this);
        }
    }
}
