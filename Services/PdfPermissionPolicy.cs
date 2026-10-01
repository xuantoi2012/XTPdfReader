using System;

namespace XTPdfMergeApp.Services;

internal enum PdfPermissionOperation { Print, Copy, Modify, Annotate }

internal static class PdfPermissionPolicy
{
    public static bool Allows(PdfSecurityInfo info, PdfPermissionOperation operation)
        => info.Error == null && (operation switch
        {
            PdfPermissionOperation.Print => info.CanPrint,
            PdfPermissionOperation.Copy => info.CanCopy,
            PdfPermissionOperation.Modify => info.CanModify,
            PdfPermissionOperation.Annotate => info.CanAnnotate,
            _ => false
        });

    public static void EnsureAllowed(string path, PdfPermissionOperation operation)
    {
        var info = PdfSecurityService.ReadCached(path);
        if (info.Error != null) throw new InvalidOperationException("Could not read PDF permissions: " + info.Error);
        if (!Allows(info, operation)) throw new UnauthorizedAccessException("This PDF does not permit " + operation.ToString().ToLowerInvariant() + " with the current password.");
    }
}
