using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;
using DocumentGroup = XTPdfMergeApp.Domain.WorkspaceDocument;
using PageRow = XTPdfMergeApp.Domain.PagePlacement;

namespace XTPdfMergeApp;

public partial class ReaderWindow
{
    /// <summary>
    /// Chuột phải tab → "Replace sheets from revision…": chọn PDF in lại (XT_PRINT) rồi thay đúng các bản vẽ cùng số hiệu
    /// (không có số hiệu thì cùng DWG + layout) vào file đang xem. Các trang khác giữ nguyên; chưa ghi file, Ctrl+Z hoàn tác cả lô.
    /// </summary>
    private async void ReaderTabReplaceSheets_Click(object sender, RoutedEventArgs e)
    {
        if (_readerGroup == null || EditHost == null || _readerGroup.Pages.Count == 0) return;
        var target = _readerGroup;

        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = "Choose the revised PDF", Filter = "PDF (*.pdf)|*.pdf",
            InitialDirectory = System.IO.Path.GetDirectoryName(target.SourcePath) is { } dir && System.IO.Directory.Exists(dir) ? dir : ""
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        string revisionPath = System.IO.Path.GetFullPath(dialog.FileName);
        if (target.Pages.Any(p => string.Equals(p.SourcePath, revisionPath, StringComparison.OrdinalIgnoreCase)))
        {
            AppDialog.Show(this, "Choose a different file: the revision cannot be the file being edited.", "Replace sheets", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Thông tin sheet của file đang xem (theo từng file nguồn của các trang) và của bản revision.
        var pages = target.Pages.ToList();
        var (targetInfo, revisionInfo) = await Task.Run(() =>
        {
            var bySource = pages.Select(p => p.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(path => path, XTSheetIndex.Read, StringComparer.OrdinalIgnoreCase);
            var list = pages.Select(p => bySource[p.SourcePath].TryGetValue(p.PageNumber, out var info) ? info : null).ToList();
            return (list, XTSheetIndex.Read(revisionPath));
        });

        if (revisionInfo.Count == 0 || targetInfo.All(i => i == null))
        {
            AppDialog.Show(this, "These PDFs have no XT sheet information, so the sheets cannot be matched.\nPlot both with the current XT_PRINT / XT_SHEETS first.",
                "Replace sheets", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var (pairs, unmatched) = XTSheetIndex.Match(targetInfo, revisionInfo);
        if (pairs.Count == 0)
        {
            AppDialog.Show(this, "No sheet of the revision matches a sheet in this file (matched by sheet number, or DWG + layout).", "Replace sheets",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string names = string.Join(", ", pairs.Take(6).Select(p => targetInfo[p.TargetIndex]!.No is { Length: > 0 } no ? no : "p." + (p.TargetIndex + 1)));
        if (pairs.Count > 6) names += $", … (+{pairs.Count - 6})";
        string extra = unmatched.Count > 0 ? $"\n{unmatched.Count} sheet(s) in the revision have no match here and will be ignored." : "";
        if (AppDialog.Show(this, $"Replace {pairs.Count} sheet(s) in \"{target.FileName}\" with the revised pages from \"{System.IO.Path.GetFileName(revisionPath)}\"?\n{names}{extra}\n\nNothing is saved until you save the file; Ctrl+Z undoes it.",
                "Replace sheets", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        // Mở bản revision (tab riêng) để lấy trang của nó; xong thì đóng tab đó.
        int groupsBefore = _groups.Count;
        await Session.OpenFilesInReaderAsync(new[] { revisionPath });
        var revision = _groups.FirstOrDefault(g => g.Pages.Count > 0 && g.Pages.All(p => string.Equals(p.SourcePath, revisionPath, StringComparison.OrdinalIgnoreCase)));
        if (revision == null || !_groups.Contains(target))
        {
            AppDialog.Show(this, "Could not open the revised PDF.", "Replace sheets", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var work = new List<(PageRow Old, DocumentGroup Source, PageRow New)>();
        foreach (var (targetIndex, revisionPage) in pairs)
        {
            var newRow = revision.Pages.FirstOrDefault(p => p.PageNumber == revisionPage);
            if (newRow != null) work.Add((pages[targetIndex], revision, newRow));
        }
        int replaced = EditHost.ReplaceSheets(target, work);
        if (_groups.Contains(revision) && !revision.IsDirty) EditHost.CloseDocument(revision);
        if (_groups.Contains(target)) SelectDocumentTab(target);
        if (replaced > 0)
            XTPdfMergeApp.Services.Growl.Success($"Replaced {replaced} sheet(s). Not saved yet: Ctrl+S to save, Ctrl+Z to undo.", this);
    }
}
