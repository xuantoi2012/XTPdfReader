using System.Collections.Generic;
using System.Linq;
using XTPdfMergeApp.Domain;

namespace XTPdfMergeApp.Workspace;

internal static class CommandSources
{
    public static IEnumerable<string> Of(IEnumerable<WorkspaceDocument> documents, IEnumerable<PagePlacement>? pages = null)
        => documents.Select(d => d.SourcePath).Concat(documents.SelectMany(d => d.Pages).Select(p => p.SourcePath))
            .Concat(pages?.Select(p => p.SourcePath) ?? Enumerable.Empty<string>());
}
