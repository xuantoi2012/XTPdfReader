using System;
using System.Threading.Tasks;

namespace XTPdfMergeApp.Workspace;

/// <summary>
/// Xoay trang THẬT trong file nguồn (khác nút xoay khung nhìn của Viewer). Việc ghi file do
/// <paramref name="applyRotation"/> lo (bất đồng bộ, đóng handle PDFium, render lại) — lệnh này chỉ
/// giữ chiều xoay để Undo = xoay ngược lại đúng góc đó. Được Record vào lịch sử SAU khi lần ghi đầu
/// thành công, nên Execute chỉ chạy lúc Redo.
/// </summary>
internal sealed class RotateSourcePagesCommand : IWorkspaceCommand
{
    private readonly Func<int, Task> _applyRotation;
    private readonly int _deltaDegrees;

    public RotateSourcePagesCommand(int deltaDegrees, int pageCount, Func<int, Task> applyRotation)
    {
        _deltaDegrees = deltaDegrees;
        _applyRotation = applyRotation;
        Description = $"Rotate {pageCount} page{(pageCount == 1 ? "" : "s")} {(deltaDegrees > 0 ? "right" : "left")} (saved to file)";
    }

    public string Description { get; }
    public void Execute() => _ = _applyRotation(_deltaDegrees);
    public void Undo() => _ = _applyRotation(-_deltaDegrees);
}
