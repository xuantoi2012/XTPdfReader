using XTPdfMergeApp.Domain;

namespace XTPdfMergeApp.Workspace;

internal sealed class ReorderDocumentsCommand : IWorkspaceCommand
{
    private readonly PdfWorkspace _workspace;
    private readonly WorkspaceDocument _document;
    private readonly int _newIndex;
    private int _oldIndex;

    public ReorderDocumentsCommand(PdfWorkspace workspace, WorkspaceDocument document, int newIndex)
    {
        _workspace = workspace;
        _document = document;
        _newIndex = newIndex;
    }

    public string Description => "Sắp xếp file";
    public void Execute()
    {
        _oldIndex = _workspace.Documents.IndexOf(_document);
        if (_oldIndex >= 0) _workspace.Documents.Move(_oldIndex, System.Math.Clamp(_newIndex, 0, _workspace.Documents.Count - 1));
    }
    public void Undo()
    {
        int current = _workspace.Documents.IndexOf(_document);
        if (current >= 0) _workspace.Documents.Move(current, System.Math.Clamp(_oldIndex, 0, _workspace.Documents.Count - 1));
    }
}
