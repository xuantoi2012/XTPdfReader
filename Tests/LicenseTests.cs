using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using XTPdfMergeApp.Licensing;

internal static partial class Program
{
    /// <summary>Signs a token the way the Edge Function does (ECDSA P-256 / SHA-256, r||s).</summary>
    static string LicenseSigned(ECDsa key, object payload)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload);
        byte[] signature = key.SignData(body, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return LicenseToken.ToBase64Url(body) + "." + LicenseToken.ToBase64Url(signature);
    }

    static void TestLicense()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var now = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        string mid = MachineId.Get();
        object Payload(string machine, string kind, string until, DateTimeOffset issued, int graceDays = 7, string product = "reader") =>
            new { v = 1, product, uid = "u1", email = "a@b.c", mid = machine, kind, until, iat = issued.ToUnixTimeSeconds(), valid = issued.AddDays(graceDays).ToUnixTimeSeconds() };

        Check(MachineId.Get() == MachineId.Get() && mid.Length == 25, "machine id is stable and 25 characters");

        string good = LicenseSigned(key, Payload(mid, "trial", "2026-10-20", now));
        var payload = LicenseToken.Verify(good, publicKey);
        Check(payload is { Kind: "trial" } && payload.Until == new DateOnly(2026, 10, 20), "a correctly signed token is read");

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Check(LicenseToken.Verify(LicenseSigned(other, Payload(mid, "trial", "2026-10-20", now)), publicKey) is null, "a token signed with another key is refused");
        Check(LicenseToken.Verify(good[..^4] + "AAAA", publicKey) is null, "a damaged signature is refused");
        string[] parts = good.Split('.');
        string edited = LicenseToken.ToBase64Url(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(LicenseToken.FromBase64Url(parts[0])).Replace("2026-10-20", "2030-01-01"))) + "." + parts[1];
        Check(LicenseToken.Verify(edited, publicKey) is null, "editing the end date breaks the signature");
        Check(LicenseToken.Verify("garbage", publicKey) is null && LicenseToken.Verify("", publicKey) is null, "malformed tokens are refused");

        LicenseState Eval(LicensePayload? p, DateTimeOffset at, DateTimeOffset seen) => LicenseState.Evaluate(p, mid, "reader", at, seen);
        var s = Eval(payload, now, now);
        Check(s.Status == LicenseStatus.Active && s.DaysLeft == 10 && s.IsTrial, "inside the dates the license is active with days left");
        var lastDay = new DateTimeOffset(2026, 10, 20, 23, 0, 0, TimeSpan.Zero);
        var refreshed = LicenseToken.Verify(LicenseSigned(key, Payload(mid, "trial", "2026-10-20", lastDay.AddHours(-5))), publicKey);
        Check(Eval(refreshed, lastDay, lastDay.AddHours(-5)) is { Status: LicenseStatus.Active, DaysLeft: 0 }, "the last day still works");
        Check(Eval(payload, now.AddDays(6), now).Status == LicenseStatus.Active, "within the offline grace the token works");
        Check(Eval(payload, now.AddDays(8), now.AddDays(8)).Status == LicenseStatus.NeedsOnline, "after the offline grace the server must be reached");
        Check(Eval(payload, now.AddDays(11), now.AddDays(7)).Status == LicenseStatus.NeedsOnline, "an expired date beyond the grace first asks for the server");

        string renewed = LicenseSigned(key, Payload(mid, "trial", "2026-10-20", now.AddDays(10), graceDays: 7));
        var expired = Eval(LicenseToken.Verify(renewed, publicKey), now.AddDays(11), now.AddDays(10));
        Check(expired.Status == LicenseStatus.Expired && !expired.AllowsUse && expired.Describe().Contains("trial has ended"), "the day after the end the trial is over and the app locks");
        Check(Eval(payload, now.AddDays(-1), now).Status == LicenseStatus.NeedsOnline, "a clock set back is not trusted");
        Check(Eval(payload, now.AddMinutes(-3), now).Status == LicenseStatus.Active, "a few minutes of clock difference are tolerated");

        var otherPc = LicenseToken.Verify(LicenseSigned(key, Payload("OTHERMACHINE0000000000000", "paid", "2027-10-31", now)), publicKey);
        Check(Eval(otherPc, now, now).Status == LicenseStatus.SignedOut, "a token issued for another PC is useless here");
        var wrongProduct = LicenseToken.Verify(LicenseSigned(key, Payload(mid, "paid", "2027-10-31", now, product: "toolbox")), publicKey);
        Check(Eval(wrongProduct, now, now).Status == LicenseStatus.SignedOut, "a token of another product is useless here");
        var paid = Eval(LicenseToken.Verify(LicenseSigned(key, Payload(mid, "paid", "2027-10-31", now)), publicKey), now, now);
        Check(paid.Status == LicenseStatus.Active && !paid.IsTrial && paid.Describe().StartsWith("Licensed until 31/10/2027"), "a paid license reads as licensed until its date");

        // Stored copy: protected, survives a round trip, a damaged file reads as nothing.
        string saved = LicenseStore.FilePath;
        LicenseStore.FilePath = Path.Combine(Path.GetTempPath(), "xt-license-test-" + Guid.NewGuid().ToString("N") + ".dat");
        try
        {
            var record = new StoredLicense(good, "refresh", "a@b.c", now, now);
            LicenseStore.Save(record);
            Check(!File.ReadAllText(LicenseStore.FilePath, Encoding.Latin1).Contains(good[..20]), "the stored file does not show the token in clear");
            Check(LicenseStore.Load() == record, "the stored license reads back unchanged");
            File.WriteAllBytes(LicenseStore.FilePath, [1, 2, 3]);
            Check(LicenseStore.Load() is null, "a damaged stored file reads as nothing");
        }
        finally { try { File.Delete(LicenseStore.FilePath); } catch { } LicenseStore.FilePath = saved; }
    }
}
