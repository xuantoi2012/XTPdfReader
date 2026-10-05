using System.Collections.Generic;
using System.Linq;
using XTPdfMergeApp.Domain;

namespace XTPdfMergeApp.Workspace;

/// <summary>
/// Tạo window nháp từ bản sao trang của nhiều window theo thứ tự (kéo tab này thả lên tab kia). Các window nguồn giữ nguyên.
/// Cả việc tạo window và chép trang là MỘT bước Undo/Redo: Undo bỏ luôn window nháp.
/// </summary>
internal sealed class CreateMergeDraftCommand : IWorkspaceCommand
{
    private readonly PdfWorkspace _workspace;
    private readonly WorkspaceDocument _draft;
    private readonly IReadOnlyList<WorkspaceDocument> _sources;
    private List<MovePagesCommand>? _copies;

    public CreateMergeDraftCommand(PdfWorkspace workspace, WorkspaceDocument draft, IReadOnlyList<WorkspaceDocument> sources)
    {
        _workspace = workspace;
        _draft = draft;
        _sources = sources;
    }

    public string Description => "Merge files into a draft";
    public IEnumerable<string> AffectedSources => CommandSources.Of(_sources.Append(_draft).ToList());

    public void Execute()
    {
        if (!_workspace.Documents.Contains(_draft)) _workspace.Documents.Add(_draft);
        if (_copies != null)
        {
            foreach (var copy in _copies) copy.Execute(); // Redo
            return;
        }
        _copies = new List<MovePagesCommand>();
        foreach (var source in _sources)
        {
            var copy = new MovePagesCommand(_workspace, source, _draft, source.Pages.ToList(), _draft.Pages.Count, copy: true);
            copy.Execute();
            _copies.Add(copy);
        }
    }

    public void Undo()
    {
        if (_copies != null)
            for (int i = _copies.Count - 1; i >= 0; i--) _copies[i].Undo();
        _workspace.Documents.Remove(_draft);
    }
}
