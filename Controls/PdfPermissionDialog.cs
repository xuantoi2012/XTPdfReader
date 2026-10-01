using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using XTPdfMergeApp.Services;

namespace XTPdfMergeApp.Controls;

internal static class PdfPermissionDialog
{
    public static bool Require(Window? owner, IEnumerable<string> paths, PdfPermissionOperation operation)
    {
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            if (!Check(owner, path, PdfSecurityService.ReadCached(path), operation)) return false;
        return true;
    }

    public static async Task<bool> RequireAsync(Window? owner, IEnumerable<string> paths, PdfPermissionOperation operation)
    {
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            if (!Check(owner, path, await PdfSecurityService.ReadAsync(path), operation)) return false;
        return true;
    }

    private static bool Check(Window? owner, string path, PdfSecurityInfo info, PdfPermissionOperation operation)
    {
        if (PdfPermissionPolicy.Allows(info, operation)) return true;
        AppDialog.Show(owner, info.Error != null
            ? $"Could not read permissions for \"{Path.GetFileName(path)}\":\n\n{info.Error}"
            : $"\"{Path.GetFileName(path)}\" does not permit {operation.ToString().ToLowerInvariant()} with the current password.\n\nOpen it with an owner password to use this operation.",
            "PDF permissions", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }
}
