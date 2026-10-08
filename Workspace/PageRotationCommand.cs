using System.Collections.Generic;
using System.Linq;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp.Workspace;

/// <summary>Pages turned in memory (<see cref="PageRotationPendingStore"/>): Undo turns them back, Redo turns them again. The file gets the new /Rotate on Save.</summary>
internal sealed class PageRotationCommand(string description, IReadOnlyList<(string Path, IReadOnlyList<int> Pages)> targets, int degrees) : IWorkspaceCommand
{
    private List<(string Path, IReadOnlyDictionary<int, int> Before)> _before = new();

    public string Description { get; } = description;
    public IEnumerable<string> AffectedSources => targets.Select(t => t.Path);

    public void Execute() => _before = targets.Select(t => (t.Path, PageRotationPendingStore.Rotate(t.Path, t.Pages, degrees))).ToList();

    public void Undo()
    {
        foreach (var (path, before) in _before) PageRotationPendingStore.Restore(path, before);
    }
}
