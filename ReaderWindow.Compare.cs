using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using XTPdfMergeApp.Controls;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp;

public partial class ReaderWindow
{
    /// <summary>
    /// Chuột phải tab → "Compare with another version…": chọn PDF phiên bản khác; các bản vẽ khớp nhau (số hiệu trong thông tin sheet, không có thì theo thứ tự trang
    /// khi 2 file cùng số trang) được vẽ chồng lên nhau để thấy nét nào đổi.
    /// </summary>
    private async void ReaderTabCompare_Click(object sender, RoutedEventArgs e)
    {
        if (_readerGroup == null || _readerGroup.Pages.Count == 0) return;
        var sources = _readerGroup.Pages.Select(p => p.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sources.Count != 1)
        {
            AppDialog.Show(this, "Compare works on one file at a time. Open the file in its own tab.", "Compare versions", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string oldPath = sources[0];
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = "Choose the other version of this file", Filter = "PDF (*.pdf)|*.pdf",
            InitialDirectory = System.IO.Path.GetDirectoryName(oldPath) is { } dir && System.IO.Directory.Exists(dir) ? dir : ""
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        string newPath = System.IO.Path.GetFullPath(dialog.FileName);
        if (string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase))
        {
            AppDialog.Show(this, "Choose a different file.", "Compare versions", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        List<ComparePair> pairs;
        try { pairs = await Task.Run(() => BuildComparePairs(oldPath, newPath)); }
        finally { System.Windows.Input.Mouse.OverrideCursor = null; }

        if (pairs.Count == 0)
        {
            AppDialog.Show(this, "No sheets could be matched: the files have no sheet numbers in common and a different number of pages.", "Compare versions",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new CompareWindow(oldPath, newPath, pairs) { Owner = this }.Show();
    }

    /// <summary>Cặp trang khớp giữa 2 file: theo số hiệu / DWG+layout trong thông tin sheet; không khớp được thì theo thứ tự trang nếu cùng số trang.</summary>
    internal static List<ComparePair> BuildComparePairs(string oldPath, string newPath)
    {
        var oldInfo = XTSheetIndex.Read(oldPath);
        var newInfo = XTSheetIndex.Read(newPath);
        int oldCount = PageCountOf(oldPath), newCount = PageCountOf(newPath);

        var oldPages = Enumerable.Range(1, oldCount).Select(n => oldInfo.TryGetValue(n, out var i) ? i : null).ToList();
        var (matches, _) = XTSheetIndex.Match(oldPages, newInfo);
        if (matches.Count > 0)
            return matches.Select(m =>
            {
                var info = oldInfo[m.TargetIndex + 1];
                string label = (info.No.Length > 0 ? info.No + "  " : "") + (info.Title.Length > 0 ? info.Title : "page " + (m.TargetIndex + 1));
                return new ComparePair(m.TargetIndex + 1, m.RevisionPage, label);
            }).ToList();

        if (oldCount == newCount && oldCount > 0)
            return Enumerable.Range(1, oldCount).Select(n => new ComparePair(n, n, "Page " + n)).ToList();
        return new List<ComparePair>();
    }

    private static int PageCountOf(string path)
    {
        try
        {
            using var doc = new iText.Kernel.Pdf.PdfDocument(new iText.Kernel.Pdf.PdfReader(path));
            return doc.GetNumberOfPages();
        }
        catch { return 0; }
    }
}
