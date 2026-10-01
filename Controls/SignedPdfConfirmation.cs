using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp.Controls;

internal static class SignedPdfConfirmation
{
    /// <summary>Ask before persistent writes; no session-wide consent and no "don't ask again".</summary>
    public static async Task<bool> ConfirmAsync(Window? owner, IEnumerable<string> sources,
        string action, bool changesOriginal, IEnumerable<string>? destinations = null)
    {
        var sourcePaths = sources.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var destinationPaths = destinations?.Where(File.Exists).ToList() ?? new List<string>();
        var info = await PdfSignatureService.ReadManyAsync(sourcePaths.Concat(destinationPaths));
        var failed = info.FirstOrDefault(x => x.Error != null);
        if (failed != null)
        {
            AppDialog.Show(owner, $"Could not check digital signatures in \"{Path.GetFileName(failed.Path)}\".\n\n{failed.Error}\n\nThe operation was stopped before writing any PDF.",
                "Could not check signatures", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        var signed = info.Where(x => x.HasSignatures).ToList();
        if (signed.Count == 0) return true;
        string files = string.Join("\n", signed.Take(5).Select(x => "• " + Path.GetFileName(x.Path)));
        if (signed.Count > 5) files += $"\n… and {signed.Count - 5} more";
        bool overwritesSigned = changesOriginal || signed.Any(x => destinationPaths.Contains(x.Path, StringComparer.OrdinalIgnoreCase));
        string consequence = overwritesSigned
            ? "Saving will modify an original or existing PDF and may invalidate its signatures or conflict with certification restrictions. Save a separate copy to preserve the signed original."
            : "This creates a modified copy. Signatures may not remain valid in that copy. The source files stay unchanged.";
        return AppDialog.ConfirmSignedPdf(owner,
            $"{action}\n{files}\n\n{consequence}\n\nSignatures detected; their validity and certificate trust have not been verified.\n\nContinue anyway?");
    }
}
