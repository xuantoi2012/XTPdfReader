using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace XTPdfMergeApp.Workspace;

/// <summary>
/// An edit that was already written into the file as an incremental update (Edit Object: lines, shapes and images removed). Undo takes that update off the end of the file again,
/// Redo puts the same bytes back; the file work is done by the two delegates (they close the PDFium handles and redraw). Recorded after the first write succeeded, so Execute only runs on Redo.
/// </summary>
internal sealed class SourceTailEditCommand(string description, string path, Func<Task<bool>> undo, Func<Task<bool>> redo) : IAsyncWorkspaceCommand
{
    public string Description { get; } = description;
    public IEnumerable<string>? AffectedSources { get; } = new[] { path };
    public Task<bool> ExecuteAsync() => redo();
    public Task<bool> UndoAsync() => undo();
    public void Execute() => throw new InvalidOperationException("Source-file edits must be awaited.");
    public void Undo() => throw new InvalidOperationException("Source-file edits must be awaited.");
}
