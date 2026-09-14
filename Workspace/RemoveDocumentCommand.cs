using System;
using XTPdfMergeApp.Domain;

namespace XTPdfMergeApp.Workspace;

internal sealed class RemoveDocumentCommand : IWorkspaceCommand
{
    private readonly PdfWorkspace _workspace;
    private readonly WorkspaceDocument _document;
    private int _index;
    public RemoveDocumentCommand(PdfWorkspace workspace, WorkspaceDocument document)
    { _workspace = workspace; _document = document; }
    public string Description => "Đóng file";
    public void Execute()
    {
        _index = _workspace.Documents.IndexOf(_document);
        if (_index >= 0) _workspace.Documents.RemoveAt(_index);
    }
    public void Undo()
    {
        if (!_workspace.Documents.Contains(_document))
            _workspace.Documents.Insert(Math.Clamp(_index, 0, _workspace.Documents.Count), _document);
    }
}
