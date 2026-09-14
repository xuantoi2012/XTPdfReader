using System;
using System.Collections.Generic;
using System.Linq;
using XTPdfMergeApp.Domain;

namespace XTPdfMergeApp.Workspace;

internal sealed class MovePagesCommand : IWorkspaceCommand
{
    private readonly PdfWorkspace _workspace;
    private readonly WorkspaceDocument _source;
    private readonly WorkspaceDocument _target;
    private readonly List<PagePlacement> _requested;
    private readonly int _requestedInsertIndex;
    private readonly bool _copy;
    private List<(PagePlacement Page, int Index)>? _original;
    private List<PagePlacement>? _inserted;
    private int _sourceDocumentIndex = -1;

    public MovePagesCommand(PdfWorkspace workspace, WorkspaceDocument source, WorkspaceDocument target,
        IEnumerable<PagePlacement> pages, int insertIndex, bool copy)
    {
        _workspace = workspace;
        _source = source;
        _target = target;
        _requested = pages.ToList();
        _requestedInsertIndex = insertIndex;
        _copy = copy;
    }

    public string Description => _copy ? "Sao chép trang" : "Di chuyển trang";

    public void Execute()
    {
        _sourceDocumentIndex = _workspace.Documents.IndexOf(_source);
        _original = _requested.Select(p => (p, _source.Pages.IndexOf(p))).ToList();
        int insertAt = Math.Clamp(_requestedInsertIndex, 0, _target.Pages.Count);

        if (_copy)
        {
            _inserted = _requested.Select(p => p.Copy()).ToList();
        }
        else
        {
            int removedBefore = ReferenceEquals(_source, _target)
                ? _original.Count(x => x.Index >= 0 && x.Index < insertAt) : 0;
            foreach (var page in _requested) _source.Pages.Remove(page);
            insertAt -= removedBefore;
            _inserted = _requested;
        }

        insertAt = Math.Clamp(insertAt, 0, _target.Pages.Count);
        for (int i = 0; i < _inserted.Count; i++) _target.Pages.Insert(insertAt + i, _inserted[i]);
        if (!_copy && _source.Pages.Count == 0) _workspace.Documents.Remove(_source);
    }

    public void Undo()
    {
        if (_inserted == null || _original == null) return;
        foreach (var page in _inserted) _target.Pages.Remove(page);
        if (!_copy)
        {
            if (!_workspace.Documents.Contains(_source))
                _workspace.Documents.Insert(Math.Clamp(_sourceDocumentIndex, 0, _workspace.Documents.Count), _source);
            foreach (var item in _original.OrderBy(x => x.Index))
                _source.Pages.Insert(Math.Clamp(item.Index, 0, _source.Pages.Count), item.Page);
        }
    }
}

