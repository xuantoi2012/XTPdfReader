using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using XTPdfMergeApp.Services;
using DocumentGroup = XTPdfMergeApp.Domain.WorkspaceDocument;

namespace XTPdfMergeApp;

public partial class ReaderWindow
{
    /// <summary>Lần lưu của ta bị người khác lưu chen vào nên đã đọc lại bản mới và áp lại (xem PdfPageEditService.EditInPlace).</summary>
    private void OnSaveConflictRetried(string path)
    {
        Dispatcher.InvokeAsync(() => XTStyle.Controls.XTGrowl.Success(
            System.IO.Path.GetFileName(path) + ": someone else saved at the same time; your change was added on top of theirs.", this));
    }

    /// <summary>Thanh "file đổi trên đĩa": thêm "last saved by X" nếu lịch sử trong file cho biết ai lưu gần nhất.</summary>
    private async Task AddLastSaverToBannerAsync(DocumentGroup group)
    {
        var paths = group.Pages.Select(p => p.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count != 1) return;
        var last = await Task.Run(() => XTHistory.Read(paths[0]).LastOrDefault());
        if (last == null || !ReferenceEquals(group, _readerGroup) || DiskChangedBanner.Visibility != Visibility.Visible) return;
        DiskChangedText.Text += $" Last saved by {last.User} ({last.TimeText}).";
    }
}
