using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XTPdfMergeApp.Licensing;

/// <summary>What is kept between runs: the signed token, the sign-in refresh token and the latest clock reading. Encrypted with DPAPI for the Windows user.</summary>
internal sealed record StoredLicense(string Token, string RefreshToken, string Email, DateTimeOffset LastSeenUtc, DateTimeOffset LastRefreshUtc);

internal static class LicenseStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PDFReaderPro.license.v1");

    internal static string FilePath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDFReaderPro", "license.dat");

    internal static StoredLicense? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            byte[] clear = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredLicense>(Encoding.UTF8.GetString(clear));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException) { return null; }
    }

    internal static void Save(StoredLicense value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            byte[] sealedData = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(value), Entropy, DataProtectionScope.CurrentUser);
            string temp = FilePath + ".tmp";
            File.WriteAllBytes(temp, sealedData);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the next run asks again */ }
    }

    internal static void Clear()
    {
        try { File.Delete(FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
