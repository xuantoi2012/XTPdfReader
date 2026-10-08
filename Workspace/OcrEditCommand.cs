using System.Collections.Generic;
using XTPdfMergeApp.Services.Ocr;

namespace XTPdfMergeApp.Workspace;

/// <summary>OCR text added in memory (<see cref="OcrPendingStore"/>): Undo takes it away again (and brings back what a page had), Redo adds it. Saved with Ctrl+S.</summary>
internal sealed class OcrEditCommand(string description, string path, IReadOnlyDictionary<int, IReadOnlyList<OcrWord>?> pages) : IWorkspaceCommand
{
    private IReadOnlyDictionary<int, IReadOnlyList<OcrWord>?>? _before;

    public string Description { get; } = description;
    public IEnumerable<string> AffectedSources => new[] { path };
    public void Execute() => _before = OcrPendingStore.Set(path, pages);
    public void Undo()
    {
        if (_before != null) OcrPendingStore.Set(path, _before);
    }
}
