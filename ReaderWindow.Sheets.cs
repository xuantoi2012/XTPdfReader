using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;

namespace XTPdfMergeApp;

public partial class ReaderWindow
{
    /// <summary>Tab Sheets → "Link numbers": số hiệu bản vẽ trong chữ của các trang khác (mục lục, "xem KT-05") thành liên kết nhảy tới bản vẽ.</summary>
    private async void OnLinkSheetNumbers()
    {
        if (_readerGroup == null) return;
        var sources = _readerGroup.Pages.Select(p => p.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sources.Count != 1) return;
        string path = sources[0];
        try
        {
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            List<SheetLink> links;
            try { links = await Task.Run(() => XTSheetLinks.Find(path, XTSheetIndex.Read(path))); }
            finally { System.Windows.Input.Mouse.OverrideCursor = null; }

            if (links.Count == 0)
            {
                AppDialog.Show(this, "No sheet number was found in the text of other pages.\n\nThis needs sheet info (use “Read info…” if the file has none) and pages with a real text layer.",
                    "Link sheet numbers", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            int pages = links.Select(l => l.Page).Distinct().Count(), targets = links.Select(l => l.TargetPage).Distinct().Count();
            if (AppDialog.Show(this, $"Found {links.Count} sheet number(s) on {pages} page(s), pointing to {targets} sheet(s).\nMake them clickable?\n\nThe links are added to the file; the text itself is not changed.",
                    "Link sheet numbers", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            if (!await PdfPermissionDialog.RequireAsync(this, sources, PdfPermissionOperation.Modify)) return;
            if (!await SignedPdfConfirmation.ConfirmAsync(this, sources, "Add sheet links", true)) return;
            AnnotationStore.ReleaseReader(path);
            int added;
            using (await PdfThumbnailService.SuspendDocumentAsync(path, TimeSpan.FromSeconds(3)))
                added = await Task.Run(() => XTSheetLinks.WriteInPlace(path, links));
            Session.RefreshDiskStamp(path);
            XTPdfMergeApp.Services.Growl.Success(added == 0 ? "Those numbers already have links." : $"{added} sheet link(s) added. Click a number to jump to the sheet.", this);
        }
        catch (Exception ex) { AppDialog.Show(this, "Could not add the links:\n" + ex.Message, "Link sheet numbers", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    /// <summary>Tab Sheets → "Page labels": ghi số hiệu bản vẽ làm nhãn trang của file đang xem.</summary>
    private async void OnSheetPageLabels()
    {
        if (_readerGroup == null) return;
        var sources = _readerGroup.Pages.Select(p => p.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sources.Count != 1) return;
        string path = sources[0];
        try
        {
            if (!await PdfPermissionDialog.RequireAsync(this, sources, PdfPermissionOperation.Modify)) return;
            if (!await SignedPdfConfirmation.ConfirmAsync(this, sources, "Set page labels", true)) return;
            AnnotationStore.ReleaseReader(path);
            int sheets;
            using (await PdfThumbnailService.SuspendDocumentAsync(path, TimeSpan.FromSeconds(3)))
                sheets = await Task.Run(() => XTPageLabels.WriteInPlace(path));
            if (sheets == 0) { AppDialog.Show(this, "This file has no sheet numbers yet. Use “Read info…” first.", "Page labels", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            Session.RefreshDiskStamp(path);
            XTPdfMergeApp.Services.Growl.Success($"Page labels set from {sheets} sheet numbers.", this);
        }
        catch (Exception ex) { AppDialog.Show(this, "Could not set the page labels:\n" + ex.Message, "Page labels", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    /// <summary>Tab Sheets → "Read info…": đọc số hiệu / tên / tỷ lệ từ chữ trong khung tên rồi ghi vào /XTSheet của file đang xem.</summary>
    private void OnReadSheetInfo()
    {
        if (_readerGroup == null || _readerGroup.Pages.Count == 0) return;
        var sources = _readerGroup.Pages.Select(p => p.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sources.Count != 1)
        {
            AppDialog.Show(this, "Reading sheet info works on one file at a time. Open the file in its own tab.", "Read sheet info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string path = sources[0];
        var pages = _readerGroup.Pages.Select(p => p.PageNumber).Distinct().OrderBy(n => n).ToList();
        int sample = _readerPage != null && string.Equals(_readerPage.SourcePath, path, StringComparison.OrdinalIgnoreCase) ? _readerPage.PageNumber : pages[0];

        OnReadSheetInfo(path, pages, sample, autoRead: false);
    }

    /// <summary>The window for these pages of one file; <paramref name="autoRead"/> reads them at once with the areas saved for their paper size.</summary>
    private async void OnReadSheetInfo(string path, IReadOnlyList<int> pages, int sample, bool autoRead)
    {
        var sources = new List<string> { path };
        var dialog = new ReadSheetInfoWindow(path, pages, sample) { Owner = this, AutoReadOnOpen = autoRead };
        if (dialog.ShowDialog() != true || dialog.Result.Count == 0) return;
        try
        {
            if (!await PdfPermissionDialog.RequireAsync(this, sources, PdfPermissionOperation.Modify)) return;
            if (!await SignedPdfConfirmation.ConfirmAsync(this, sources, "Write sheet info", true)) return;
            AnnotationStore.ReleaseReader(path);
            using (await PdfThumbnailService.SuspendDocumentAsync(path, TimeSpan.FromSeconds(3)))
                await Task.Run(() => XTSheetInfoWriter.WriteInPlace(path, dialog.Result));
            Session.RefreshDiskStamp(path);
            await ReaderSidePanel.RefreshSheetsAsync();
            XTPdfMergeApp.Services.Growl.Success($"Sheet info written for {dialog.Result.Count} pages.", this);
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, "Could not write the sheet info:\n" + ex.Message, "Read sheet info", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Tab Sheets → "Split by…": mỗi phần (Hạng mục / Subset / DWG) thành 1 file PDF trong thư mục người dùng chọn.</summary>
    private async void OnSplitSheets(IReadOnlyList<(string Label, IReadOnlyList<PageRow> Pages)> parts)
    {
        if (_readerGroup == null || parts.Count == 0) return;
        var allPages = parts.SelectMany(p => p.Pages).ToList();
        if (!await PdfPermissionDialog.RequireAsync(this, allPages.Select(p => p.SourcePath), PdfPermissionOperation.Copy)) return;

        string dir = Path.GetDirectoryName(_readerGroup.SourcePath) ?? "";
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = $"Save {parts.Count} files to", SelectedPath = Directory.Exists(dir) ? dir : ""
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        string folder = dialog.SelectedPath;

        string baseName = Path.GetFileNameWithoutExtension(_readerGroup.FileName);
        var planned = new List<ExportPart>();
        int index = 0;
        foreach (var (label, pages) in parts)
        {
            index++;
            var mapped = await AnnotationWorkingCopy.MapAsync(pages.Select(p => (p.SourcePath, p.PageNumber)));
            planned.Add(new ExportPart
            {
                Label = label, Pages = mapped,
                FileName = PdfExportService.Sanitize($"{baseName} - {index:D2} {label}") + ".pdf"
            });
        }

        var existing = planned.Where(p => File.Exists(Path.Combine(folder, p.FileName))).ToList();
        if (existing.Count > 0 && AppDialog.Show(this, $"{existing.Count} file(s) already exist in that folder and will be replaced. Continue?", "Split",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        (int Written, string Error) result;
        try { result = await PdfExportService.ExportAsync(planned, folder, flatten: false, new HashSet<string>(), optimize: false); }
        finally { System.Windows.Input.Mouse.OverrideCursor = null; }

        if (result.Error.Length > 0) AppDialog.Show(this, "Could not split the file:\n" + result.Error, "Split", MessageBoxButton.OK, MessageBoxImage.Error);
        else XTPdfMergeApp.Services.Growl.Success($"Created {result.Written} files", this);
    }
}
