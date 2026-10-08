namespace XTPdfMergeApp.Licensing;

/// <summary>
/// Where the license server is and which public key checks its tokens. Fill <see cref="SupabaseUrl"/> and <see cref="AnonKey"/> after the
/// Supabase project for the Reader exists (see Licensing/Server/README.md). While they are empty, licensing is OFF (development builds);
/// Packaging/Release/Build-Release.ps1 refuses to pack a release in that state.
/// </summary>
internal static class LicenseConfig
{
    internal const string SupabaseUrl = "";
    internal const string AnonKey = "";

    /// <summary>ECDSA P-256 public key (SPKI, base64). The matching private key lives only in the Edge Function secret LICENSE_PRIVATE_KEY.</summary>
    internal const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEmkQONobCKCdCfQPIxOKKDnj6YMpWwuETuV2fBPPcnXDw4ZtDfPWHDvgJYM3NdswFTrcyTS5idLqGmPLIts5qhw==";

    internal const string Product = "reader";
    internal const int MaxDevices = 2;
    internal const int TrialDays = 15;
    /// <summary>How long a signed token works without talking to the server again.</summary>
    internal const int OfflineGraceDays = 7;
    /// <summary>The app asks the server for a fresh token (renewals, removed devices) when the one it holds is older than this.</summary>
    internal const int RefreshAfterHours = 24;

    internal static bool IsConfigured => SupabaseUrl.Length > 0 && AnonKey.Length > 0;
}
