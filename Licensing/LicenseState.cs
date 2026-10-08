using System;

namespace XTPdfMergeApp.Licensing;

internal enum LicenseStatus
{
    /// <summary>Licensing is off (no server configured, development build).</summary>
    Disabled,
    /// <summary>No usable token: sign in.</summary>
    SignedOut,
    /// <summary>Trial or paid, and inside its dates.</summary>
    Active,
    /// <summary>The token was not renewed for too long (or the clock went backwards): the app must reach the server.</summary>
    NeedsOnline,
    /// <summary>The last day has passed.</summary>
    Expired
}

internal sealed record LicenseState(LicenseStatus Status, LicensePayload? Payload, int DaysLeft)
{
    internal bool AllowsUse => Status is LicenseStatus.Disabled or LicenseStatus.Active;
    internal bool IsTrial => Payload?.Kind == "trial";

    internal string Describe() => Status switch
    {
        LicenseStatus.Disabled => "Licensing is off in this build",
        LicenseStatus.SignedOut => "Not signed in",
        LicenseStatus.NeedsOnline => "Connect to the internet to renew the license check",
        LicenseStatus.Expired => IsTrial ? "The 15-day trial has ended" : $"The license ended on {Payload!.Until:dd/MM/yyyy}",
        _ => IsTrial
            ? $"Trial: {Left()} (until {Payload!.Until:dd/MM/yyyy})"
            : $"Licensed until {Payload!.Until:dd/MM/yyyy} ({Left()})"
    };

    private string Left() => DaysLeft switch { 0 => "last day", 1 => "1 day left", _ => $"{DaysLeft} days left" };

    /// <summary>Decides what a token allows right now. <paramref name="lastSeenUtc"/> is the latest clock reading the app ever stored: a clock
    /// that is behind it by more than a few minutes is treated as tampered with.</summary>
    internal static LicenseState Evaluate(LicensePayload? payload, string machineId, string product, DateTimeOffset now, DateTimeOffset lastSeenUtc)
    {
        if (payload is null || payload.Product != product || !string.Equals(payload.MachineId, machineId, StringComparison.Ordinal))
            return new LicenseState(LicenseStatus.SignedOut, null, 0);
        if (now < lastSeenUtc - TimeSpan.FromMinutes(10) || now < payload.IssuedAt - TimeSpan.FromMinutes(10) || now > payload.ValidUntil)
            return new LicenseState(LicenseStatus.NeedsOnline, payload, 0);
        int daysLeft = payload.Until.DayNumber - DateOnly.FromDateTime(now.UtcDateTime).DayNumber;
        return daysLeft < 0
            ? new LicenseState(LicenseStatus.Expired, payload, 0)
            : new LicenseState(LicenseStatus.Active, payload, daysLeft);
    }
}
