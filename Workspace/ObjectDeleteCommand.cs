using System.Collections.Generic;
using XTPdfMergeApp.Services.TextEdit;

namespace XTPdfMergeApp.Workspace;

/// <summary>Objects marked for removal in memory (<see cref="ObjectDeletePendingStore"/>): Undo brings them back on the page, Redo marks them again. Written into the file on Save.</summary>
internal sealed class ObjectDeleteCommand(string description, string path, IReadOnlyList<PdfObjectRef> objects) : IWorkspaceCommand
{
    private IReadOnlyList<PdfObjectRef> _added = objects;

    public string Description { get; } = description;
    public IEnumerable<string> AffectedSources => new[] { path };
    public void Execute() => _added = ObjectDeletePendingStore.Add(path, objects);
    public void Undo() => ObjectDeletePendingStore.Remove(path, _added);
}
