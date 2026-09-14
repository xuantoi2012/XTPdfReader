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
    private List<(WorkspaceDocument Document, int Index, List<PagePlacement> Pages)>? _snapshot;
    private string? _oldTargetName;

    public MergeDocumentsCommand(PdfWorkspace workspace, IEnumerable<WorkspaceDocument> documents, string? newName)
    {
        _workspace = workspace;
        _documents = documents.ToList();
        _newName = newName;
    }

    public string Description => !string.IsNullOrWhiteSpace(_newName) ? "Ghép tất cả file" : "Ghép file đã chọn";

    public void Execute()
    {
        if (_documents.Count < 2) return;
        WorkspaceDocument target = _documents[0];
        _oldTargetName = target.CaptureDisplayName();
        _snapshot = _documents.Select(d => (d, _workspace.Documents.IndexOf(d), d.Pages.ToList())).ToList();

        foreach (var source in _documents.Skip(1))
        {
            foreach (var page in source.Pages.ToList())
            {
                source.Pages.Remove(page);
                target.Pages.Add(page);
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
