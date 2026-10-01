using System;
using System.Collections.Generic;
using System.Linq;
using XTPdfMergeApp.Domain;

namespace XTPdfMergeApp.Workspace;

/// <summary>Chèn các placement MỚI (vd trang trắng) vào một window; Undo gỡ đúng các placement đó.</summary>
internal sealed class InsertPagesCommand : IWorkspaceCommand
{
    private readonly WorkspaceDocument _target;
    private readonly List<PagePlacement> _pages;
    private readonly int _index;

    public InsertPagesCommand(WorkspaceDocument target, IEnumerable<PagePlacement> pages, int index, string description)
    {
        _target = target;
        _pages = pages.ToList();
        _index = index;
        Description = description;
    }

    public string Description { get; }
    public IEnumerable<string> AffectedSources => CommandSources.Of(new[] { _target }, _pages);
    public IReadOnlyList<PagePlacement> Inserted => _pages;

    public void Execute()
    {
        int at = Math.Clamp(_index, 0, _target.Pages.Count);
        for (int i = 0; i < _pages.Count; i++) _target.Pages.Insert(at + i, _pages[i]);
    }

    public void Undo()
    {
        foreach (var page in _pages) _target.Pages.Remove(page);
    }
}
