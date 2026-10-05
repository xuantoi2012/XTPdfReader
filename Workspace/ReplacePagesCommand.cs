using System;
using System.Collections.Generic;
using System.Linq;
using XTPdfMergeApp.Domain;

namespace XTPdfMergeApp.Workspace;

/// <summary>
/// Thay trang bằng trang revision: với mỗi cặp, chèn bản sao trang mới ngay sau trang cũ rồi bỏ trang cũ. Cả lô là MỘT bước Undo/Redo.
/// Chỉ đổi workspace (chưa ghi file); Lưu mới ghi ra file.
/// </summary>
internal sealed class ReplacePagesCommand : IWorkspaceCommand
{
    public sealed record Pair(WorkspaceDocument Target, PagePlacement Old, WorkspaceDocument Source, PagePlacement New);

    private readonly PdfWorkspace _workspace;
    private readonly IReadOnlyList<Pair> _pairs;
    private List<IWorkspaceCommand>? _commands;

    public ReplacePagesCommand(PdfWorkspace workspace, IReadOnlyList<Pair> pairs)
    {
        _workspace = workspace;
        _pairs = pairs;
    }

    public string Description => _pairs.Count == 1 ? "Replace 1 sheet" : $"Replace {_pairs.Count} sheets";

    public IEnumerable<string> AffectedSources => CommandSources.Of(
        _pairs.SelectMany(p => new[] { p.Target, p.Source }).Distinct(), _pairs.SelectMany(p => new[] { p.Old, p.New }));

    public void Execute()
    {
        if (_commands != null)
        {
            foreach (var command in _commands) command.Execute(); // Redo: cùng trạng thái như lần đầu
            return;
        }
        _commands = new List<IWorkspaceCommand>();
        foreach (var pair in _pairs)
        {
            int at = pair.Target.Pages.IndexOf(pair.Old);
            if (at < 0) continue; // trang cũ đã không còn trong window
            var insert = new MovePagesCommand(_workspace, pair.Source, pair.Target, new[] { pair.New }, at + 1, copy: true);
            insert.Execute();
            _commands.Add(insert);
            var remove = new RemovePagesCommand(_workspace, pair.Target, new[] { pair.Old });
            remove.Execute();
            _commands.Add(remove);
        }
    }

    public void Undo()
    {
        if (_commands == null) return;
        for (int i = _commands.Count - 1; i >= 0; i--) _commands[i].Undo();
    }
}
