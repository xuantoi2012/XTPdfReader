using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XTPdfMergeApp.Licensing;

/// <summary>
/// The single place the app asks "may I run?". Reads the stored token, checks its signature, machine and dates, renews it in the background
/// and signs in/out. Does nothing (state Disabled) while <see cref="LicenseConfig.IsConfigured"/> is false.
/// </summary>
internal static class LicenseManager
{
    private static readonly LicenseClient Client = new();
    private static StoredLicense? _stored;
    private static LicenseSession? _session;

    internal static LicenseState Current { get; private set; } = new(LicenseConfig.IsConfigured ? LicenseStatus.SignedOut : LicenseStatus.Disabled, null, 0);
    internal static string Email => _stored?.Email ?? "";
    internal static event Action? Changed;

    /// <summary>Test seam: the clock and the public key the token is checked against.</summary>
    internal static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    internal static string PublicKey { get; set; } = LicenseConfig.PublicKey;

    /// <summary>Reads the stored token and decides the state without any network.</summary>
    internal static LicenseState Reload()
    {
        if (!LicenseConfig.IsConfigured) return Set(new LicenseState(LicenseStatus.Disabled, null, 0));
        _stored = LicenseStore.Load();
        var payload = _stored is null ? null : LicenseToken.Verify(_stored.Token, PublicKey);
        var now = Clock();
        var state = LicenseState.Evaluate(payload, MachineId.Get(), LicenseConfig.Product, now, _stored?.LastSeenUtc ?? DateTimeOffset.MinValue);
        if (_stored is not null && now > _stored.LastSeenUtc && state.Status != LicenseStatus.NeedsOnline)
            LicenseStore.Save(_stored = _stored with { LastSeenUtc = now });
        return Set(state);
    }

    /// <summary>True when the stored token is old enough that the server should be asked for a fresh one.</summary>
    internal static bool ShouldRefresh => _stored is { RefreshToken.Length: > 0 } s && Clock() - s.LastRefreshUtc > TimeSpan.FromHours(LicenseConfig.RefreshAfterHours);

    /// <summary>Asks the server for a fresh token with the saved sign-in. A failure keeps the old token (it works until its grace ends);
    /// a refusal by the server (suspended, device removed) clears it.</summary>
    internal static async Task<LicenseState> RefreshAsync(CancellationToken token = default)
    {
        if (!LicenseConfig.IsConfigured || _stored is not { RefreshToken.Length: > 0 } stored) return Current;
        try
        {
            _session = await Client.RefreshAsync(stored.RefreshToken, stored.Email, token);
            await ClaimAsync(_session, token);
        }
        catch (LicenseException ex) when (ex.Code is "BLOCKED" or "DEVICE_LIMIT" or "invalid_grant" or "refresh_token_not_found")
        {
            LicenseStore.Clear();
            _stored = null;
            Set(new LicenseState(LicenseStatus.SignedOut, null, 0));
        }
        catch (LicenseException) { /* offline or server down: keep the token we have */ }
        return Current;
    }

    internal static async Task<LicenseState> SignInAsync(string email, string password, CancellationToken token = default)
    {
        _session = await Client.SignInAsync(email.Trim(), password, token);
        await ClaimAsync(_session, token);
        return Current;
    }

    internal static Task<string> SignUpAsync(string email, string password, string displayName, CancellationToken token = default)
        => Client.SignUpAsync(email.Trim(), password, displayName.Trim(), token);

    /// <summary>Retries the claim of this PC after a device was removed (needs the session from the sign-in).</summary>
    internal static async Task<LicenseState> ClaimAgainAsync(CancellationToken token = default)
    {
        if (_session is null) throw new LicenseException("SIGNED_OUT", "Sign in again.");
        await ClaimAsync(_session, token);
        return Current;
    }

    internal static Task<IReadOnlyList<LicenseDevice>> DevicesAsync(CancellationToken token = default)
        => Client.DevicesAsync(_session ?? throw new LicenseException("SIGNED_OUT", "Sign in again."), token);

    internal static Task RemoveDeviceAsync(string deviceId, CancellationToken token = default)
        => Client.RemoveDeviceAsync(_session ?? throw new LicenseException("SIGNED_OUT", "Sign in again."), deviceId, token);

    internal static void SignOut()
    {
        LicenseStore.Clear();
        _stored = null;
        _session = null;
        Set(new LicenseState(LicenseConfig.IsConfigured ? LicenseStatus.SignedOut : LicenseStatus.Disabled, null, 0));
    }

    private static async Task ClaimAsync(LicenseSession session, CancellationToken token)
    {
        string signed = await Client.ClaimAsync(session, token);
        var payload = LicenseToken.Verify(signed, PublicKey) ?? throw new LicenseException("SERVER", "The license from the server could not be verified.");
        var now = Clock();
        LicenseStore.Save(_stored = new StoredLicense(signed, session.RefreshToken, session.Email, now, now));
        Set(LicenseState.Evaluate(payload, MachineId.Get(), LicenseConfig.Product, now, DateTimeOffset.MinValue));
    }

    private static LicenseState Set(LicenseState state)
    {
        bool changed = Current.Status != state.Status || Current.DaysLeft != state.DaysLeft || Current.Payload != state.Payload;
        Current = state;
        if (changed) Changed?.Invoke();
        return state;
    }
}
