using System.Collections.Generic;
using System.Linq;
using XTPdfMergeApp.Services.TextEdit;

namespace XTPdfMergeApp.Workspace;

/// <summary>Many text runs replaced at once (batch find and replace): one Undo takes all of them back.</summary>
internal sealed class TextEditBatchCommand(string description, string path, IReadOnlyList<(int Page, TextRun Place, TextEdit? Edit)> items) : IWorkspaceCommand
{
    private List<TextEdit?>? _before;

    public string Description { get; } = description;
    public IEnumerable<string> AffectedSources => new[] { path };

    public void Execute() => _before = items.Select(i => TextEditPendingStore.Set(path, i.Page, i.Place, i.Edit)).ToList();

    public void Undo()
    {
        if (_before == null) return;
        for (int k = items.Count - 1; k >= 0; k--) TextEditPendingStore.Set(path, items[k].Page, items[k].Place, _before[k]);
    }
}
