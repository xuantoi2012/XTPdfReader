using System;
using System.Threading.Tasks;

namespace XTPdfMergeApp.Workspace;

/// <summary>
/// Lệnh sửa THẲNG file nguồn (ghi annotation…) — việc ghi file bất đồng bộ do 2 delegate lo. Được
/// Record vào lịch sử SAU khi lần ghi đầu thành công, nên Execute chỉ chạy lúc Redo.
/// </summary>
internal sealed class SourceFileEditCommand : IWorkspaceCommand
{
    private readonly Func<Task> _redo;
    private readonly Func<Task> _undo;

    public SourceFileEditCommand(string description, Func<Task> redo, Func<Task> undo)
    {
        Description = description;
        _redo = redo;
        _undo = undo;
    }

    public string Description { get; }
    public void Execute() => _ = _redo();
    public void Undo() => _ = _undo();
}
