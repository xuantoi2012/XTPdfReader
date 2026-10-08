using System.Collections.Generic;
using XTPdfMergeApp.Services.TextEdit;

namespace XTPdfMergeApp.Workspace;

/// <summary>One text run replaced in memory (<see cref="TextEditPendingStore"/>): Undo puts back what the place had (the earlier edit, or the file's own text), Redo replaces it again. Saved with Ctrl+S.</summary>
internal sealed class TextEditCommand(string description, string path, int pageNumber, TextRun place, TextEdit? edit) : IWorkspaceCommand
{
    private TextEdit? _before;

    public string Description { get; } = description;
    public IEnumerable<string> AffectedSources => new[] { path };
    public void Execute() => _before = TextEditPendingStore.Set(path, pageNumber, place, edit);
    public void Undo() => TextEditPendingStore.Set(path, pageNumber, place, _before);
}
