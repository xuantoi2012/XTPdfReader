using System;
using System.Collections.Generic;
using System.Linq;
using XTPdfMergeApp.Domain;

namespace XTPdfMergeApp.Workspace;

internal sealed class MergeDocumentsCommand : IWorkspaceCommand
{
    private readonly PdfWorkspace _workspace;
    private readonly List<WorkspaceDocument> _documents;
    private readonly string? _newName;
    private readonly int? _insertIndex;
    private List<(WorkspaceDocument Document, int Index, List<PagePlacement> Pages)>? _snapshot;
    private string? _oldTargetName;

    /// <param name="insertIndex">null = nối trang vào cuối file đích (ghép thường). Có giá trị = chèn
    /// trang của các file còn lại vào đúng vị trí đó trong file đích (nút Insert của Viewer).</param>
    public MergeDocumentsCommand(PdfWorkspace workspace, IEnumerable<WorkspaceDocument> documents, string? newName,
        int? insertIndex = null)
    {
        _workspace = workspace;
        _documents = documents.ToList();
        _newName = newName;
        _insertIndex = insertIndex;
    }

    public string Description => _insertIndex.HasValue ? "Insert pages from file"
        : !string.IsNullOrWhiteSpace(_newName) ? "Merge all files" : "Merge selected files";

    public void Execute()
    {
        if (_documents.Count < 2) return;
        WorkspaceDocument target = _documents[0];
        _oldTargetName = target.CaptureDisplayName();
        _snapshot = _documents.Select(d => (d, _workspace.Documents.IndexOf(d), d.Pages.ToList())).ToList();

        int insertAt = Math.Clamp(_insertIndex ?? target.Pages.Count, 0, target.Pages.Count);
        foreach (var source in _documents.Skip(1))
        {
            foreach (var page in source.Pages.ToList())
            {
                source.Pages.Remove(page);
                target.Pages.Insert(insertAt++, page);
            }
            _workspace.Documents.Remove(source);
        }
        if (!string.IsNullOrWhiteSpace(_newName)) target.SetDisplayName(_newName);
    }

    public void Undo()
    {
        if (_snapshot == null || _snapshot.Count < 2) return;
        WorkspaceDocument target = _snapshot[0].Document;
        foreach (var sourceState in _snapshot.Skip(1).OrderBy(x => x.Index))
        {
            foreach (var page in sourceState.Pages) target.Pages.Remove(page);
            sourceState.Document.Pages.Clear();
            foreach (var page in sourceState.Pages) sourceState.Document.Pages.Add(page);
            if (!_workspace.Documents.Contains(sourceState.Document))
                _workspace.Documents.Insert(Math.Clamp(sourceState.Index, 0, _workspace.Documents.Count), sourceState.Document);
        }
        target.RestoreDisplayName(_oldTargetName);
    }
}
