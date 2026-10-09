using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using iText.Bouncycastle.X509;
using iText.Commons.Bouncycastle.Cert;
using iText.IO.Font;
using iText.IO.Image;
using iText.Kernel.Font;
using iText.Forms.Form.Element;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Signatures;
using Org.BouncyCastle.X509;

namespace XTPdfMergeApp.Services;

/// <summary>A certificate the user can sign with (from the Windows store, where the Viettel-CA token driver publishes it, or from a .pfx file).</summary>
internal sealed record SigningCertificate(X509Certificate2 Certificate, string Subject, string Organization, string Issuer, DateTime NotAfter, bool FromFile)
{
    public string Thumbprint => Certificate.Thumbprint;
    public string Display => $"{Subject}  —  {Issuer}  (đến {NotAfter:dd/MM/yyyy})";
}

internal sealed record DigitalSignRequest(
    string SourcePath, string OutputPath, SigningCertificate Certificate,
    int Page, double U1, double V1, double U2, double V2,
    string Organization, string? SealImagePath, string Reason, string Location,
    string? TsaUrl, bool EmbedRevocation);

internal sealed record SignatureCheck(string Field, string Signer, string Organization, DateTime? SignedAt, bool IntegrityOk, bool CoversWholeFile, bool ChainTrusted, string Note)
{
    public string Reason { get; init; } = "";
    public string Location { get; init; } = "";
    public string Issuer { get; init; } = "";
    public string Subject { get; init; } = "";
    public DateTime? ValidFrom { get; init; }
    public DateTime? ValidTo { get; init; }
    public bool HasTimestamp { get; init; }
    public string Algorithm { get; init; } = "";
    public int Revision { get; init; }
    public int Revisions { get; init; }
    public bool Valid => IntegrityOk && ChainTrusted;
}

/// <summary>
/// Signs a PDF (PAdES, detached CMS, incremental update so earlier signatures stay valid) with a certificate whose private key can live on a USB token: the key is never read, the hash is sent
/// to the token through Windows (CNG / CSP), which asks for the PIN. The look follows Vietnamese administrative practice: the seal picture on the left, the organisation, the signer and the time on the right.
/// </summary>
internal static class PdfDigitalSignService
{
    // ── certificates ─────────────────────────────────────────────────

    public static IReadOnlyList<SigningCertificate> ListStoreCertificates()
    {
        var list = new List<SigningCertificate>();
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                foreach (var cert in store.Certificates)
                {
                    if (!cert.HasPrivateKey || DateTime.Now > cert.NotAfter || DateTime.Now < cert.NotBefore) continue;
                    if (!CanSign(cert) || list.Any(x => x.Thumbprint == cert.Thumbprint)) continue;
                    list.Add(Describe(cert, false));
                }
            }
            catch { /* store not readable: show what was found */ }
        }
        return list.OrderBy(x => x.Subject, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static SigningCertificate LoadPfx(string path, string password)
        => Describe(new X509Certificate2(path, password, X509KeyStorageFlags.EphemeralKeySet), true);

    private static bool CanSign(X509Certificate2 cert)
    {
        var usage = cert.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        return usage == null || (usage.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) != 0;
    }

    private static SigningCertificate Describe(X509Certificate2 cert, bool fromFile)
    {
        string cn = Rdn(cert, "CN") ?? cert.GetNameInfo(X509NameType.SimpleName, false);
        string org = Rdn(cert, "O") ?? "";
        string issuer = cert.GetNameInfo(X509NameType.SimpleName, true);
        return new SigningCertificate(cert, cn, org, issuer, cert.NotAfter, fromFile);
    }

    private static string? Rdn(X509Certificate2 cert, string key)
    {
        foreach (var part in cert.SubjectName.Name.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Trim().Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return kv[1].Trim().Trim('"');
        }
        return null;
    }

    // ── signing ──────────────────────────────────────────────────────

    public static Task SignAsync(DigitalSignRequest request) => Task.Run(() => Sign(request));

    /// <summary>The text beside the seal. Kept in one place so the dialog preview and the file say the same.</summary>
    public static string AppearanceText(string organization, string signer, DateTime when)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(organization)) sb.AppendLine(organization.Trim().ToUpper(System.Globalization.CultureInfo.CurrentCulture));
        sb.AppendLine("Ký bởi: " + signer);
        sb.Append("Ký ngày: " + when.ToString("dd/MM/yyyy HH:mm:ss"));
        return sb.ToString();
    }

    private static void Sign(DigitalSignRequest r)
    {
        var cert = r.Certificate.Certificate;
        var chain = BuildChain(cert);
        var external = new WindowsKeySignature(cert);
        var when = DateTime.Now;

        var readerProps = new ReaderProperties();
        if (PdfThumbnailService.TryGetDocumentPassword(r.SourcePath) is { Length: > 0 } password)
            readerProps.SetPassword(Encoding.UTF8.GetBytes(password));

        string temp = r.OutputPath + ".signing.tmp";
        try
        {
            using (var reader = new PdfReader(r.SourcePath, readerProps))
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                var signer = new PdfSigner(reader, output, new StampingProperties().UseAppendMode());
                string fieldName = "XTSign_" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
                var rect = ToPageRect(signer.GetDocument(), r);
                var look = new SignatureFieldAppearance(fieldName);
                look.SetFont(PdfFontFactory.CreateFont(FontPath(), PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED));
                look.SetFontSize(Math.Clamp(rect.GetHeight() / 6f, 6f, 11f));
                string text = AppearanceText(r.Organization, r.Certificate.Subject, when);
                if (!string.IsNullOrWhiteSpace(r.SealImagePath) && File.Exists(r.SealImagePath))
                    look.SetContent(text, ImageDataFactory.Create(r.SealImagePath));
                else look.SetContent(text);
                signer.SetSignerProperties(new SignerProperties()
                    .SetFieldName(fieldName).SetPageNumber(r.Page).SetPageRect(rect)
                    .SetReason(r.Reason).SetLocation(r.Location).SetContact(r.Organization)
                    .SetSignatureCreator("PDF Reader Pro").SetClaimedSignDate(when)
                    .SetSignatureAppearance(look));

                ITSAClient? tsa = string.IsNullOrWhiteSpace(r.TsaUrl) ? null : new TSAClientBouncyCastle(r.TsaUrl.Trim());
                ICollection<ICrlClient>? crl = null;
                IOcspClient? ocsp = null;
                if (r.EmbedRevocation && !r.Certificate.FromFile)
                {
                    crl = new List<ICrlClient> { new CrlClientOnline() };
                    ocsp = new OcspClientBouncyCastle();
                }
                signer.SignDetached(new BouncyCastleDigest(), external, chain, crl, ocsp, tsa, tsa != null ? 20000 : 12000, PdfSigner.CryptoStandard.CADES);
            }
            if (File.Exists(r.OutputPath)) File.Delete(r.OutputPath);
            File.Move(temp, r.OutputPath);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    private static string FontPath()
    {
        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (var name in new[] { "arial.ttf", "segoeui.ttf", "tahoma.ttf" })
        {
            string p = System.IO.Path.Combine(fonts, name);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("Không tìm thấy phông chữ Arial trong Windows để vẽ chữ ký.");
    }

    /// <summary>Fractions of the displayed page (top-left origin) to the page's own coordinates, honouring /Rotate.</summary>
    internal static Rectangle ToPageRect(PdfDocument doc, DigitalSignRequest r)
    {
        var page = doc.GetPage(r.Page);
        var box = page.GetPageSize();
        int rotation = ((page.GetRotation() % 360) + 360) % 360;
        double w0 = box.GetWidth(), h0 = box.GetHeight();
        double dispW = rotation % 180 == 0 ? w0 : h0, dispH = rotation % 180 == 0 ? h0 : w0;
        (double x, double y) Map(double u, double v)
        {
            double X = u * dispW, Yup = (1 - v) * dispH;
            return rotation switch
            {
                90 => (dispH - Yup, X),
                180 => (dispW - X, dispH - Yup),
                270 => (Yup, dispW - X),
                _ => (X, Yup),
            };
        }
        var a = Map(r.U1, r.V1);
        var b = Map(r.U2, r.V2);
        float left = (float)(box.GetLeft() + Math.Min(a.x, b.x)), bottom = (float)(box.GetBottom() + Math.Min(a.y, b.y));
        return new Rectangle(left, bottom, (float)Math.Abs(a.x - b.x), (float)Math.Abs(a.y - b.y));
    }

    private static IX509Certificate[] BuildChain(X509Certificate2 cert)
    {
        var parser = new X509CertificateParser();
        var result = new List<IX509Certificate>();
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
        chain.Build(cert);
        foreach (var element in chain.ChainElements)
            result.Add(new X509CertificateBC(parser.ReadCertificate(element.Certificate.RawData)));
        if (result.Count == 0) result.Add(new X509CertificateBC(parser.ReadCertificate(cert.RawData)));
        return result.ToArray();
    }

    /// <summary>Sends the data to be signed to the key through Windows; the token driver shows its own PIN prompt.</summary>
    private sealed class WindowsKeySignature : IExternalSignature
    {
        private readonly RSA? _rsa;
        private readonly ECDsa? _ec;

        public WindowsKeySignature(X509Certificate2 cert)
        {
            _rsa = cert.GetRSAPrivateKey();
            if (_rsa == null) _ec = cert.GetECDsaPrivateKey();
            if (_rsa == null && _ec == null) throw new CryptographicException("Không truy cập được khóa riêng của chứng thư số (token đã cắm và đã cài trình điều khiển chưa?).");
        }

        public string GetDigestAlgorithmName() => "SHA-256";
        public string GetSignatureAlgorithmName() => _rsa != null ? "RSA" : "ECDSA";
        public ISignatureMechanismParams? GetSignatureMechanismParameters() => null;

        public byte[] Sign(byte[] message)
            => _rsa != null
                ? _rsa.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                : _ec!.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    // ── checking ─────────────────────────────────────────────────────

    public static Task<IReadOnlyList<SignatureCheck>> CheckAsync(string path) => Task.Run(() => Check(path));

    private static IReadOnlyList<SignatureCheck> Check(string path)
    {
        var result = new List<SignatureCheck>();
        var props = new ReaderProperties();
        if (PdfThumbnailService.TryGetDocumentPassword(path) is { Length: > 0 } password)
            props.SetPassword(Encoding.UTF8.GetBytes(password));
        using var doc = new PdfDocument(new PdfReader(path, props));
        var util = new SignatureUtil(doc);
        foreach (string name in util.GetSignatureNames())
        {
            try
            {
                var pkcs7 = util.ReadSignatureData(name);
                bool integrity = pkcs7.VerifySignatureIntegrityAndAuthenticity();
                var cert = pkcs7.GetSigningCertificate();
                var net = new X509Certificate2(cert.GetEncoded());
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                bool trusted = chain.Build(net);
                bool stamped = false;
                try { stamped = pkcs7.GetTimeStampDate() != DateTime.MinValue && pkcs7.GetTimeStampDate().Year > 1970; } catch { }
                result.Add(new SignatureCheck(name, Rdn(net, "CN") ?? net.Subject, Rdn(net, "O") ?? "",
                    pkcs7.GetSignDate() is { } d ? d : null, integrity, util.SignatureCoversWholeDocument(name), trusted,
                    trusted ? "" : "Chưa tin cậy chuỗi chứng thư (cài chứng thư gốc của nhà cung cấp, ví dụ Viettel-CA, vào Windows).")
                {
                    Reason = pkcs7.GetReason() ?? "", Location = pkcs7.GetLocation() ?? "",
                    Issuer = net.GetNameInfo(X509NameType.SimpleName, true), Subject = net.Subject,
                    ValidFrom = net.NotBefore, ValidTo = net.NotAfter, HasTimestamp = stamped,
                    Algorithm = (pkcs7.GetDigestAlgorithmName() ?? "") + " / " + (pkcs7.GetSignatureMechanismName() ?? ""),
                    Revision = util.GetRevision(name), Revisions = util.GetTotalRevisions()
                });
            }
            catch (Exception ex) { result.Add(new SignatureCheck(name, "?", "", null, false, false, false, ex.Message)); }
        }
        return result;
    }
}
