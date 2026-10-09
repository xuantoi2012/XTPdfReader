using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;
using XTPdfMergeApp.Services;

internal static partial class Program
{
    static SigningCertificate MakeTestCertificate(string cn, string org)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}, O={org}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        using var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
        string pfx = Path.Combine(Output, "test-sign.pfx");
        File.WriteAllBytes(pfx, cert.Export(X509ContentType.Pfx, "pw"));
        return PdfDigitalSignService.LoadPfx(pfx, "pw");
    }

    static async Task TestDigitalSignAsync()
    {
        Directory.CreateDirectory(Output);
        string src = Path.Combine(Output, "sign-src.pdf");
        using (var doc = new PdfDocument(new PdfWriter(src)))
        using (var layout = new Document(doc))
        {
            layout.Add(new Paragraph("Van ban thu nghiem ky so"));
            doc.AddNewPage().SetRotation(90);
        }
        var cert = MakeTestCertificate("Nguyen Van A", "Cong ty Thu Nghiem");
        Check(cert.Subject == "Nguyen Van A" && cert.Organization == "Cong ty Thu Nghiem", "Certificate name and organisation are read from the subject");

        string seal = Path.Combine(Output, "seal.png");
        File.WriteAllBytes(seal, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        string out1 = Path.Combine(Output, "sign-out1.pdf");
        await PdfDigitalSignService.SignAsync(new DigitalSignRequest(src, out1, cert, 1, 0.5, 0.75, 0.95, 0.95,
            "Công ty Thử nghiệm", seal, "Phê duyệt", "Hà Nội", null, false));
        var checks = await PdfDigitalSignService.CheckAsync(out1);
        Check(checks.Count == 1 && checks[0].IntegrityOk && checks[0].CoversWholeFile, "A signed PDF verifies: integrity ok and covers the whole file");
        Check((await PdfSignatureService.ReadAsync(out1)).SignatureCount == 1, "Presence check sees the new signature");

        await PdfSignatureFieldService.LoadAsync(out1);
        var fields = PdfSignatureFieldService.All(out1);
        Check(fields.Count == 1 && fields[0].Signed && fields[0].Page == 1 && Math.Abs(fields[0].U1 - 0.5) < 0.02 && Math.Abs(fields[0].V1 - 0.75) < 0.02,
            "The signature field is found on its page, signed, where it was drawn");
        Check(PdfSignatureFieldService.Of(out1, 2).Count == 0, "Other pages have no signature field");
        Check(checks[0].Signer == "Nguyen Van A" && checks[0].Organization == "Cong ty Thu Nghiem" && checks[0].Reason == "Phê duyệt" && checks[0].Location == "Hà Nội",
            "The details of a signature carry signer, organisation, reason and place");

        // A second signature on the rotated page keeps the first one valid (incremental update)
        string out2 = Path.Combine(Output, "sign-out2.pdf");
        await PdfDigitalSignService.SignAsync(new DigitalSignRequest(out1, out2, cert, 2, 0.1, 0.1, 0.5, 0.3,
            "Công ty Thử nghiệm", null, "", "", null, false));
        var two = await PdfDigitalSignService.CheckAsync(out2);
        Check(two.Count == 2 && two.All(c => c.IntegrityOk) && !two[0].CoversWholeFile && two[1].CoversWholeFile, "A second signature keeps the first one valid");

        // tampering is detected
        var bytes = File.ReadAllBytes(out1);
        int at = System.Text.Encoding.ASCII.GetString(bytes).IndexOf("%%EOF", StringComparison.Ordinal);
        string text = System.Text.Encoding.Latin1.GetString(bytes).Replace("Van ban", "Van bam");
        string bad = Path.Combine(Output, "sign-bad.pdf");
        File.WriteAllBytes(bad, System.Text.Encoding.Latin1.GetBytes(text));
        var tampered = await PdfDigitalSignService.CheckAsync(bad);
        Check(tampered.Count == 1 && !tampered[0].IntegrityOk || text == System.Text.Encoding.Latin1.GetString(bytes), "A changed byte is reported as a broken signature");

        // rotated page rectangle lands inside the page
        using var d = new PdfDocument(new PdfReader(src));
        var rect = PdfDigitalSignService.ToPageRect(d, new DigitalSignRequest(src, "", cert, 2, 0, 0, 0.5, 0.5, "", null, "", "", null, false));
        var page = d.GetPage(2).GetPageSize();
        Check(rect.GetLeft() >= page.GetLeft() - 0.01 && rect.GetRight() <= page.GetRight() + 0.01 && rect.GetWidth() > 0 && rect.GetHeight() > 0, "Signature rectangle on a rotated page stays on the page");
        Console.WriteLine($"signed files in {Output}");
    }
}
