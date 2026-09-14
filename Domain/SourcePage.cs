using System;

namespace XTPdfMergeApp.Domain;

/// <summary>Danh tính bất biến của một trang trong file PDF nguồn.</summary>
internal sealed record SourcePage(
    Guid SourcePageId,
    Guid SourceDocumentId,
    string SourcePath,
    int SourcePageIndex);

