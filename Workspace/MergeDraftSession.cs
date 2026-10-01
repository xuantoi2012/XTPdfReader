using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using XTPdfMergeApp.Domain;

namespace XTPdfMergeApp.Workspace;

/// <summary>
/// Bàn nháp riêng cho cửa sổ Merge. Không bao giờ dùng trực tiếp các document đang
/// mở ở ReaderWindow: đóng Merge là có thể bỏ toàn bộ thay đổi mà tab chính không bị ảnh hưởng.
/// </summary>
internal sealed class MergeDraftSession
{
    private PdfWorkspace _workspace = new();
    private readonly Dictionary<Guid, WorkspaceDocument> _draftBySourceId = new();
    private readonly HashSet<Guid> _temporaryDocumentIds = [];
    private int _temporaryDocumentNumber;

    internal ObservableCollection<WorkspaceDocument> Documents => _workspace.Documents;
    /// <summary>Những nhóm đang là window thật và được phép xuất bằng Merge all.</summary>
    internal IEnumerable<WorkspaceDocument> WindowDocuments => _workspace.Documents.Where(document => !IsTemporary(document));
    /// <summary>Bãi giữ trang; không bao giờ được đưa vào Merge all cho tới khi người dùng mở thành window.</summary>
    internal IEnumerable<WorkspaceDocument> TemporaryDocuments => _workspace.Documents.Where(IsTemporary);
    internal UndoRedoManager History => _workspace.History;
    internal void Restore(IEnumerable<(WorkspaceDocument Document, bool Temporary)> documents)
    {
        _workspace = new PdfWorkspace();
        _draftBySourceId.Clear();
        _temporaryDocumentIds.Clear();
        foreach (var (document, temporary) in documents)
        {
            _workspace.Documents.Add(document);
            if (temporary) _temporaryDocumentIds.Add(document.DocumentId);
        }
    }

    internal void Begin(IEnumerable<WorkspaceDocument> sourceDocuments)
    {
        _workspace = new PdfWorkspace();
        _draftBySourceId.Clear();
        _temporaryDocumentIds.Clear();
        _temporaryDocumentNumber = 0;

        foreach (var source in sourceDocuments)
        {
            var draft = CloneDocument(source);
            _workspace.Documents.Add(draft);
            _draftBySourceId[source.DocumentId] = draft;
        }
    }

    internal WorkspaceDocument? GetDraftFor(WorkspaceDocument source)
        => _workspace.Documents.Contains(source)
            ? source
            : _draftBySourceId.GetValueOrDefault(source.DocumentId);

    internal IReadOnlyList<PagePlacement> MovePages(WorkspaceDocument source, WorkspaceDocument target,
        IReadOnlyList<PagePlacement> pages, int insertIndex, bool copy)
    {
        source = GetDraftFor(source) ?? source;
        target = GetDraftFor(target) ?? target;
        if (!_workspace.Documents.Contains(source) || !_workspace.Documents.Contains(target)) return [];

        var draftPages = ResolvePages(source, pages);
        if (draftPages.Count == 0) return [];
        var command = new MovePagesCommand(_workspace, source, target, draftPages, insertIndex, copy);
        _workspace.Execute(command);
        return command.InsertedPages;
    }

    internal void DeletePages(WorkspaceDocument document, IReadOnlyList<PagePlacement> pages)
    {
        document = GetDraftFor(document) ?? document;
        if (!_workspace.Documents.Contains(document)) return;
        var draftPages = ResolvePages(document, pages);
        if (draftPages.Count > 0) _workspace.Execute(new RemovePagesCommand(_workspace, document, draftPages));
    }

    internal void RemoveDocument(WorkspaceDocument document)
    {
        if (_workspace.Documents.Contains(document)) _workspace.Execute(new RemoveDocumentCommand(_workspace, document));
    }

    internal WorkspaceDocument CreateTemporaryDocument()
        => CreateDocument(temporary: true);

    internal WorkspaceDocument CreateWindowDocument()
        => CreateDocument(temporary: false);

    internal void PromoteTemporaryDocument(WorkspaceDocument document)
        => _temporaryDocumentIds.Remove(document.DocumentId);

    /// <summary>Trả các trang của 1 nhóm tạm về đúng file nguồn của chúng (nối vào cuối file đó), nếu file đó
    /// vẫn đang mở thành 1 window trong bản nháp này. Trang có nguồn không còn mở thì giữ nguyên tại chỗ.
    /// Trả về true nếu có ít nhất 1 trang được chuyển.</summary>
    internal bool ReturnPagesToSource(WorkspaceDocument group)
    {
        group = GetDraftFor(group) ?? group;
        if (!_workspace.Documents.Contains(group)) return false;

        var byWindow = group.Pages
            .GroupBy(page => page.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Pages: (IReadOnlyList<PagePlacement>)g.ToList(),
                           Target: WindowDocuments.FirstOrDefault(d =>
                               d != group && string.Equals(d.SourcePath, g.Key, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Target != null)
            .ToList();

        bool moved = false;
        foreach (var (pages, target) in byWindow)
        {
            if (target == null) continue;
            var draftPages = ResolvePages(group, pages);
            if (draftPages.Count == 0) continue;
            _workspace.Execute(new MovePagesCommand(_workspace, group, target, draftPages, target.Pages.Count, copy: false));
            moved = true;
        }
        return moved;
    }

    internal void MoveToTemporaryShelf(WorkspaceDocument document)
    {
        if (_workspace.Documents.Contains(document)) _temporaryDocumentIds.Add(document.DocumentId);
    }

    internal bool IsTemporary(WorkspaceDocument document)
        => _temporaryDocumentIds.Contains(document.DocumentId);

    private WorkspaceDocument CreateDocument(bool temporary)
    {
        string name = $"New merge document {++_temporaryDocumentNumber}";
        var document = new WorkspaceDocument
        {
            // Chỉ là identity của vùng nháp; trang được thả vào vẫn tham chiếu file nguồn thật.
            SourcePath = Path.Combine(Path.GetTempPath(), $"XTPdfReader-MergeDraft-{Guid.NewGuid():N}.pdf")
        };
        document.SetDisplayName(name);
        document.SetBaseline();
        _workspace.Execute(new AddDocumentCommand(_workspace, document));
        if (temporary) _temporaryDocumentIds.Add(document.DocumentId);
        return document;
    }

    internal void Undo() => History.Undo();
    internal void Redo() => History.Redo();

    private static WorkspaceDocument CloneDocument(WorkspaceDocument source)
    {
        var clone = new WorkspaceDocument { SourcePath = source.SourcePath };
        if (source.CaptureDisplayName() is { } displayName) clone.SetDisplayName(displayName);
        foreach (var page in source.Pages) clone.Pages.Add(page.Copy());
        clone.SetBaseline();
        return clone;
    }

    private static List<PagePlacement> ResolvePages(WorkspaceDocument source, IEnumerable<PagePlacement> pages)
    {
        // PlacementId thay đổi khi clone, nhưng SourcePageId vẫn ổn định. Dùng queue để vẫn xử lý
        // đúng khi cùng một trang nguồn xuất hiện nhiều lần trong một file của bản nháp.
        var candidates = source.Pages.GroupBy(p => p.SourcePageId)
            .ToDictionary(g => g.Key, g => new Queue<PagePlacement>(g));
        var result = new List<PagePlacement>();
        foreach (var page in pages)
        {
            if (source.Pages.Contains(page)) { result.Add(page); continue; }
            if (candidates.TryGetValue(page.SourcePageId, out var matches) && matches.Count > 0)
                result.Add(matches.Dequeue());
        }
        return result;
    }
}
