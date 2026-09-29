using System;
using XTPdfMergeApp.Domain;

namespace XTPdfMergeApp.Workspace;

/// <summary>Thêm một document vào workspace để việc tạo nhóm tạm cũng Undo/Redo được.</summary>
internal sealed class AddDocumentCommand : IWorkspaceCommand
{
    private readonly PdfWorkspace _workspace;
    private readonly WorkspaceDocument _document;
    private readonly int _requestedIndex;
    private int _index;

    public AddDocumentCommand(PdfWorkspace workspace, WorkspaceDocument document, int index = int.MaxValue)
    {
        _workspace = workspace;
        _document = document;
        _requestedIndex = index;
    }

    public string Description => "Create group";

    public void Execute()
    {
        if (_workspace.Documents.Contains(_document)) return;
        _index = Math.Clamp(_requestedIndex, 0, _workspace.Documents.Count);
        _workspace.Documents.Insert(_index, _document);
    }

    public void Undo()
    {
        if (_workspace.Documents.Contains(_document)) _workspace.Documents.Remove(_document);
    }
}
