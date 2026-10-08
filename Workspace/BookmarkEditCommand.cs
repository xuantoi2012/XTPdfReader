using System.Collections.Generic;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp.Workspace;

/// <summary>A bookmark change that waits for Save (<see cref="BookmarkPendingStore"/>): Undo takes it back, Redo puts it again.</summary>
internal sealed class BookmarkEditCommand(string path, BookmarkEdit edit) : IWorkspaceCommand
{
    public string Description { get; } = edit.Description;
    public IEnumerable<string> AffectedSources => new[] { path };
    public void Execute() => BookmarkPendingStore.Add(path, edit);
    public void Undo() => BookmarkPendingStore.Remove(path, edit);
}
