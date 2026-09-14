using System;

namespace XTPdfMergeApp.Domain;

internal sealed record SourceDocument(
    Guid SourceDocumentId,
    string SourcePath,
    DateTime LastWriteTimeUtc,
    long FileLength);

