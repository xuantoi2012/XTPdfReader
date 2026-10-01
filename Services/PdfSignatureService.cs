using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using iText.Forms;
using iText.Kernel.Pdf;

namespace XTPdfMergeApp.Services;

/// <summary>Presence detection only. A filled signature field is NOT proof of a valid/trusted signature.</summary>
internal sealed record PdfSignatureInfo(string Path, int SignatureCount, bool IsCertified, string? Error = null)
{
    public bool HasSignatures => SignatureCount > 0 || IsCertified;
}

internal static class PdfSignatureService
{
    public static Task<PdfSignatureInfo> ReadAsync(string path) => Task.Run(() => Read(path));

    private static PdfSignatureInfo Read(string path)
    {
        try
        {
            var properties = new ReaderProperties();
            if (PdfThumbnailService.TryGetDocumentPassword(path) is { Length: > 0 } password)
                properties.SetPassword(Encoding.UTF8.GetBytes(password));
            using var document = new PdfDocument(new PdfReader(path, properties));
            var fields = PdfAcroForm.GetAcroForm(document, false)?.GetAllFormFields();
            int count = fields?.Values.Count(field => PdfName.Sig.Equals(field.GetFormType()) &&
                field.GetPdfObject().GetAsDictionary(PdfName.V) != null) ?? 0;
            bool certified = document.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.Perms)?.Get(PdfName.DocMDP) != null;
            return new PdfSignatureInfo(path, count, certified);
        }
        catch (Exception ex) { return new PdfSignatureInfo(path, 0, false, ex.Message); }
    }

    public static async Task<IReadOnlyList<PdfSignatureInfo>> ReadManyAsync(IEnumerable<string> paths)
    {
        // Sequential background reads avoid opening every source of a large merge at once.
        var results = new List<PdfSignatureInfo>();
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            results.Add(await ReadAsync(path));
        return results;
    }
}
