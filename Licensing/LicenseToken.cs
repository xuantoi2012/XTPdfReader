using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XTPdfMergeApp.Licensing;

/// <summary>What the server vouches for. <c>Kind</c> is "trial" or "paid"; <c>Until</c> is the last day the license works.</summary>
internal sealed record LicensePayload(string Product, string UserId, string Email, string MachineId, string Kind, DateOnly Until, DateTimeOffset IssuedAt, DateTimeOffset ValidUntil);

/// <summary>
/// A license token is <c>base64url(payload json) + "." + base64url(signature)</c>, signed by the server with ECDSA P-256 / SHA-256
/// (IEEE P1363 r||s). The app only holds the public key, so a token cannot be made or edited on the user's machine.
/// </summary>
internal static class LicenseToken
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Checks the signature and reads the payload; null when the token is malformed or the signature does not match.</summary>
    internal static LicensePayload? Verify(string token, string publicKeyBase64)
    {
        try
        {
            int dot = token.IndexOf('.');
            if (dot <= 0 || dot == token.Length - 1) return null;
            byte[] body = FromBase64Url(token[..dot]);
            byte[] signature = FromBase64Url(token[(dot + 1)..]);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            if (!key.VerifyData(body, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) return null;

            var raw = JsonSerializer.Deserialize<Raw>(Encoding.UTF8.GetString(body), Json);
            if (raw is null || raw.V != 1 || string.IsNullOrEmpty(raw.Mid) || string.IsNullOrEmpty(raw.Kind)) return null;
            if (!DateOnly.TryParseExact(raw.Until, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var until)) return null;
            return new LicensePayload(raw.Product ?? "", raw.Uid ?? "", raw.Email ?? "", raw.Mid, raw.Kind, until,
                DateTimeOffset.FromUnixTimeSeconds(raw.Iat), DateTimeOffset.FromUnixTimeSeconds(raw.Valid));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException or ArgumentException) { return null; }
    }

    internal static byte[] FromBase64Url(string text)
    {
        string s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    internal static string ToBase64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record Raw(int V, string? Product, string? Uid, string? Email, string? Mid, string? Kind, string? Until, long Iat, long Valid);
}
