using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using XTPdfMergeApp.Domain;

namespace XTPdfMergeApp.Workspace;

internal sealed class PdfWorkspace
{
    private readonly Dictionary<string, SourceDocument> _sourceDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(Guid DocumentId, int PageIndex), SourcePage> _sourcePages = new();

    public ObservableCollection<WorkspaceDocument> Documents { get; } = new();
    public UndoRedoManager History { get; } = new();

    public SourceDocument GetOrAddSourceDocument(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (_sourceDocuments.TryGetValue(fullPath, out var existing)) return existing;
        var info = new FileInfo(fullPath);
        var document = new SourceDocument(Guid.NewGuid(), fullPath,
            info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue,
            info.Exists ? info.Length : 0);
        _sourceDocuments.Add(fullPath, document);
        return document;
    }

    public PagePlacement CreatePlacement(string path, int pageNumber)
    {
        SourceDocument document = GetOrAddSourceDocument(path);
        var key = (document.SourceDocumentId, pageNumber);
        if (!_sourcePages.TryGetValue(key, out var sourcePage))
        {
            sourcePage = new SourcePage(Guid.NewGuid(), document.SourceDocumentId, document.SourcePath, pageNumber);
            _sourcePages.Add(key, sourcePage);
        }

        return new PagePlacement
        {
            SourcePageId = sourcePage.SourcePageId,
            SourcePath = sourcePage.SourcePath,
            PageNumber = sourcePage.SourcePageIndex
        };
    }

    public void Execute(IWorkspaceCommand command) => History.Execute(command);
}

