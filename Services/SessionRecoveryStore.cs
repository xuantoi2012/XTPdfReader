using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using XTPdfMergeApp.Domain;
using XTPdfMergeApp.Workspace;

namespace XTPdfMergeApp.Services;

public sealed record SavedPage(string Path, int Number, int Rotation = 0, string? Bookmark = null);
public sealed record SavedDocument(string Path, string? Name, bool Untitled, List<SavedPage> Pages, List<SavedPage> Baseline, bool Temporary = false);
public sealed record SavedSource(string Path, long Length, DateTime Stamp, List<QuickAnnotationChange> Changes, byte[]? Embedded = null);
public sealed record SavedSession(int Version, List<SavedDocument> Documents, List<SavedDocument> Draft, List<SavedSource> Sources, int ActiveDocument, int ActivePage);

/// <summary>Windows-user-encrypted checkpoints. Passwords and native/visual state are never serialized.</summary>
internal static class SessionRecoveryStore
{
    private static readonly object Gate = new();
    internal static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XTPdfReader", "recovery");
    internal static string Checkpoint => Path.Combine(Folder, "session.dat");

    internal static List<SavedDocument> CaptureDocuments(IEnumerable<WorkspaceDocument> documents, Func<WorkspaceDocument, bool>? temporary = null)
        => documents.Where(d => !d.IsOpening && !d.HasLoadError).Select(d => new SavedDocument(d.SourcePath, d.CaptureDisplayName(), d.IsUntitled,
            d.Pages.Select(p => new SavedPage(p.SourcePath, p.PageNumber, p.Rotation, p.Bookmark)).ToList(),
            d.CaptureBaseline().Select(p => new SavedPage(p.Path, p.Page)).ToList(), temporary?.Invoke(d) == true)).ToList();

    internal static SavedSession Capture(IEnumerable<WorkspaceDocument> documents, List<SavedDocument> draft, int active, int page,
        Func<string, (long Length, DateTime Stamp)?>? baselineStamp = null)
    {
        var docs = CaptureDocuments(documents);
        var paths = docs.Concat(draft).SelectMany(d => d.Pages.Concat(d.Baseline)).Select(p => p.Path)
            .Concat(docs.Where(d => File.Exists(d.Path)).Select(d => d.Path)).Distinct(StringComparer.OrdinalIgnoreCase);
        var sources = paths.Select(path =>
        {
            var info = new FileInfo(path);
            var stamp = baselineStamp?.Invoke(path);
            // Untitled/blank/recovered temporary PDFs must survive temp cleanup.
            bool embedded = BlankPageService.IsBlankFile(path) || path.StartsWith(Path.Combine(Folder, "sources") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            return new SavedSource(path, stamp?.Length ?? (info.Exists ? info.Length : 0), stamp?.Stamp ?? (info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue),
                AnnotationStore.Pending(path).ToList(), embedded && info.Exists ? File.ReadAllBytes(path) : null);
        }).ToList();
        return new SavedSession(1, docs, draft, sources, active, page);
    }

    internal static byte[] Encode(SavedSession session) => ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(session), null, DataProtectionScope.CurrentUser);
    internal static SavedSession Decode(byte[] bytes)
    {
        var result = JsonSerializer.Deserialize<SavedSession>(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser))
            ?? throw new InvalidDataException("The session checkpoint is empty.");
        if (result.Version != 1) throw new InvalidDataException("This session checkpoint has an unsupported version.");
        return result;
    }
    internal static void Save(SavedSession session, string? path = null)
    {
        lock (Gate)
        {
            path ??= Checkpoint;
            Directory.CreateDirectory(Folder);
            byte[] data = Encode(session);
            PdfFileTransaction.Run(new[] { path }, (_, stage) => File.WriteAllBytes(stage, data));
        }
    }
    internal static SavedSession? Load(string? path = null)
    {
        lock (Gate) { path ??= Checkpoint; return File.Exists(path) ? Decode(File.ReadAllBytes(path)) : null; }
    }
    internal static void Clear() { lock (Gate) { if (File.Exists(Checkpoint)) File.Delete(Checkpoint); } }
    internal static bool SourceUnchanged(SavedSource source)
    {
        if (source.Embedded != null) return true;
        var info = new FileInfo(source.Path);
        return info.Exists && info.Length == source.Length && info.LastWriteTimeUtc == source.Stamp;
    }
    internal static WorkspaceDocument RestoreDocument(SavedDocument saved, PdfWorkspace workspace, IReadOnlyDictionary<string, string> paths,
        IReadOnlyDictionary<string, int> counts)
    {
        string Map(string path) => paths.GetValueOrDefault(path, path);
        var doc = new WorkspaceDocument { SourcePath = Map(saved.Path) };
        doc.RestoreDisplayName(saved.Name);
        foreach (var page in saved.Pages)
            if (counts.TryGetValue(page.Path, out int count) && page.Number >= 1 && page.Number <= count)
            {
                var placement = workspace.CreatePlacement(Map(page.Path), page.Number);
                placement.Rotation = page.Rotation;
                placement.Bookmark = page.Bookmark;
                doc.Pages.Add(placement);
            }
        doc.RestoreBaseline(saved.Baseline.Select(p => (Map(p.Path), p.Number)));
        if (saved.Untitled) doc.MarkUntitled(saved.Name ?? "Untitled.pdf");
        doc.SetAnnotationsDirty(doc.Pages.Any(p => AnnotationStore.HasPending(p.SourcePath)));
        return doc;
    }
}
