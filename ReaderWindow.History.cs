using System;
using System.Collections.Generic;
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
        Dispatcher.InvokeAsync(() => XTPdfMergeApp.Services.Growl.Success(
            System.IO.Path.GetFileName(path) + ": someone else saved at the same time; your change was added on top of theirs.", this));
    }

    /// <summary>Ta vừa lưu thay đổi lên chú thích mà người khác cũng đã sửa nội dung: báo, bản của họ còn trong lịch sử.</summary>
    private void OnAnnotationConflict(string path, IReadOnlyList<string> names)
    {
        Dispatcher.InvokeAsync(() => XTPdfMergeApp.Services.Growl.Warning(
            $"{System.IO.Path.GetFileName(path)}: {names.Count} comment(s) were edited by someone else at the same time. Your text replaced theirs; their version is in the file's History.", this));
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
