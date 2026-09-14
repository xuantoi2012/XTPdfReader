using System;
using System.Collections.Generic;
using System.Linq;
using XTPdfMergeApp.Domain;

namespace XTPdfMergeApp.Workspace;

internal sealed class RemovePagesCommand : IWorkspaceCommand
{
    private readonly PdfWorkspace _workspace;
    private readonly WorkspaceDocument _document;
    private readonly List<PagePlacement> _pages;
    private List<(PagePlacement Page, int Index)>? _snapshot;
    private int _documentIndex;

    public RemovePagesCommand(PdfWorkspace workspace, WorkspaceDocument document, IEnumerable<PagePlacement> pages)
    {
        _workspace = workspace;
        _document = document;
        _pages = pages.ToList();
    }

    public string Description => "Xóa trang";
    public void Execute()
    {
        _documentIndex = _workspace.Documents.IndexOf(_document);
        _snapshot = _pages.Select(p => (p, _document.Pages.IndexOf(p))).Where(x => x.Item2 >= 0).ToList();
        foreach (var page in _pages) _document.Pages.Remove(page);
        if (_document.Pages.Count == 0) _workspace.Documents.Remove(_document);
    }
    public void Undo()
    {
        if (_snapshot == null) return;
        if (!_workspace.Documents.Contains(_document))
            _workspace.Documents.Insert(Math.Clamp(_documentIndex, 0, _workspace.Documents.Count), _document);
        foreach (var item in _snapshot.OrderBy(x => x.Index))
            _document.Pages.Insert(Math.Clamp(item.Index, 0, _document.Pages.Count), item.Page);
    }
}

