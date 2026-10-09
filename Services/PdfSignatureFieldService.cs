using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using iText.Forms;
using iText.Kernel.Pdf;
using iText.Signatures;

namespace XTPdfMergeApp.Services;

/// <summary>A signature field on a page (signed or still empty), as fractions of the displayed page.</summary>
internal sealed record SignatureField(string Name, int Page, double U1, double V1, double U2, double V2, bool Signed)
{
    public bool Contains(double u, double v) => u >= U1 && u <= U2 && v >= V1 && v <= V2;
}

/// <summary>Where the signature fields are, so the reader can mark them like Foxit does and open their details on a click.</summary>
internal static class PdfSignatureFieldService
{
    private static readonly ConcurrentDictionary<string, (int Epoch, IReadOnlyList<SignatureField> Fields)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static event Action<string>? Changed;

    public static IReadOnlyList<SignatureField> Of(string path, int page)
        => Cache.TryGetValue(path, out var entry) ? entry.Fields.Where(f => f.Page == page).ToList() : Array.Empty<SignatureField>();

    public static IReadOnlyList<SignatureField> All(string path) => Cache.TryGetValue(path, out var entry) ? entry.Fields : Array.Empty<SignatureField>();

    public static void Forget(string path) => Cache.TryRemove(path, out _);

    /// <summary>Reads the fields (once per file change) and raises <see cref="Changed"/> when they are new.</summary>
    public static async Task LoadAsync(string path)
    {
        int epoch = PdfThumbnailService.FileChangeEpoch;
        if (Cache.TryGetValue(path, out var known) && known.Epoch == epoch) return;
        var fields = await Task.Run(() => Read(path));
        Cache[path] = (epoch, fields);
        Changed?.Invoke(path);
    }

    private static IReadOnlyList<SignatureField> Read(string path)
    {
        var list = new List<SignatureField>();
        try
        {
            var props = new ReaderProperties();
            if (PdfThumbnailService.TryGetDocumentPassword(path) is { Length: > 0 } password) props.SetPassword(Encoding.UTF8.GetBytes(password));
            using var doc = new PdfDocument(new PdfReader(path, props));
            var form = PdfAcroForm.GetAcroForm(doc, false);
            if (form == null) return list;
            var util = new SignatureUtil(doc);
            var signed = util.GetSignatureNames().ToHashSet();
            foreach (var (name, field) in form.GetAllFormFields())
            {
                if (!PdfName.Sig.Equals(field.GetFormType())) continue;
                bool isSigned = signed.Contains(name) || field.GetPdfObject().GetAsDictionary(PdfName.V) != null;
                foreach (var widget in field.GetWidgets())
                {
                    var page = widget.GetPage();
                    if (page == null) continue;
                    var rect = widget.GetRectangle()?.ToRectangle();
                    if (rect == null || rect.GetWidth() < 1 || rect.GetHeight() < 1) continue; // an invisible signature has no box
                    var geometry = PdfQuickAnnotationService.GetGeometry(page);
                    var d = geometry.UserRectToDisplay(rect.GetLeft(), rect.GetBottom(), rect.GetRight(), rect.GetTop());
                    list.Add(new SignatureField(name, doc.GetPageNumber(page), d.Item1, d.Item2, d.Item3, d.Item4, isSigned));
                }
            }
        }
        catch { /* unreadable file: no fields shown */ }
        return list;
    }
}
