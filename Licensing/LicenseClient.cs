using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XTPdfMergeApp.Licensing;

internal sealed record LicenseSession(string AccessToken, string RefreshToken, string Email);
internal sealed record LicenseDevice(string Id, string Name, DateTimeOffset LastSeen, bool IsThisPc);

/// <summary>A failure the window can show as is; <see cref="Code"/> is the server code (for example DEVICE_LIMIT) or "NETWORK".</summary>
internal sealed class LicenseException(string code, string message, IReadOnlyList<LicenseDevice>? devices = null) : Exception(message)
{
    internal string Code { get; } = code;
    internal IReadOnlyList<LicenseDevice> Devices { get; } = devices ?? [];
}

/// <summary>Talks to the Supabase project: sign-up and sign-in (Auth REST) and the <c>license</c> Edge Function that signs tokens and manages devices.</summary>
internal sealed class LicenseClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string _url = LicenseConfig.SupabaseUrl.TrimEnd('/');

    internal async Task<string> SignUpAsync(string email, string password, string displayName, CancellationToken token)
    {
        var root = await SendAsync(HttpMethod.Post, "/auth/v1/signup", null, new { email, password, data = new { display_name = displayName } }, token);
        // With "Confirm email" on, no session comes back: the user must open the mail first.
        return root.TryGetProperty("access_token", out _)
            ? "Account created. You can sign in now."
            : "Account created. Open the confirmation e-mail we sent, then sign in.";
    }

    internal async Task<LicenseSession> SignInAsync(string email, string password, CancellationToken token)
        => ReadSession(await SendAsync(HttpMethod.Post, "/auth/v1/token?grant_type=password", null, new { email, password }, token), email);

    internal async Task<LicenseSession> RefreshAsync(string refreshToken, string email, CancellationToken token)
        => ReadSession(await SendAsync(HttpMethod.Post, "/auth/v1/token?grant_type=refresh_token", null, new { refresh_token = refreshToken }, token), email);

    /// <summary>Registers this PC (or confirms it) and returns the signed token. Throws DEVICE_LIMIT, with the PCs in use, when both slots are taken.</summary>
    internal async Task<string> ClaimAsync(LicenseSession session, CancellationToken token)
    {
        var root = await FunctionAsync(session, new { action = "claim", machine_id = MachineId.Get(), device_name = MachineId.DeviceName(), product = LicenseConfig.Product }, token);
        return root.GetProperty("token").GetString() ?? throw new LicenseException("SERVER", "The server sent no license.");
    }

    internal async Task<IReadOnlyList<LicenseDevice>> DevicesAsync(LicenseSession session, CancellationToken token)
        => ReadDevices(await FunctionAsync(session, new { action = "devices", machine_id = MachineId.Get(), product = LicenseConfig.Product }, token));

    internal async Task RemoveDeviceAsync(LicenseSession session, string deviceId, CancellationToken token)
        => await FunctionAsync(session, new { action = "remove", device_id = deviceId, product = LicenseConfig.Product }, token);

    private Task<JsonElement> FunctionAsync(LicenseSession session, object body, CancellationToken token)
        => SendAsync(HttpMethod.Post, "/functions/v1/license", session.AccessToken, body, token);

    private static LicenseSession ReadSession(JsonElement root, string email)
        => new(root.GetProperty("access_token").GetString()!, root.GetProperty("refresh_token").GetString()!, email);

    private static List<LicenseDevice> ReadDevices(JsonElement root)
    {
        var list = new List<LicenseDevice>();
        if (root.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Array)
            foreach (var d in devices.EnumerateArray())
                list.Add(new LicenseDevice(d.GetProperty("id").GetString()!, d.GetProperty("name").GetString() ?? "PC",
                    d.TryGetProperty("last_seen", out var seen) && seen.TryGetDateTimeOffset(out var when) ? when : DateTimeOffset.MinValue,
                    d.TryGetProperty("this_pc", out var me) && me.ValueKind == JsonValueKind.True));
        return list;
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, string? bearer, object body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, _url + path) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        request.Headers.Add("apikey", LicenseConfig.AnonKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer ?? LicenseConfig.AnonKey);
        try
        {
            using var response = await Http.SendAsync(request, token);
            string text = await response.Content.ReadAsStringAsync(token);
            JsonElement root = default;
            try { root = JsonDocument.Parse(text.Length == 0 ? "{}" : text).RootElement.Clone(); } catch (JsonException) { }
            if (response.IsSuccessStatusCode) return root;
            throw Failure(root, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
        {
            throw new LicenseException("NETWORK", "Cannot reach the license server. Check the internet connection.");
        }
    }

    private static LicenseException Failure(JsonElement root, int status)
    {
        string code = Pick(root, "error_code", "code", "error") ?? "SERVER";
        string message = Pick(root, "msg", "message", "error_description") ?? $"The server answered {status}.";
        IReadOnlyList<LicenseDevice>? devices = root.ValueKind == JsonValueKind.Object ? ReadDevices(root) : null;
        return code switch
        {
            "invalid_credentials" => new LicenseException(code, "Wrong e-mail or password."),
            "email_not_confirmed" => new LicenseException(code, "Confirm your e-mail first (open the mail we sent), then sign in."),
            "user_already_exists" => new LicenseException(code, "This e-mail already has an account. Sign in instead."),
            "weak_password" => new LicenseException(code, "Choose a stronger password (at least 8 characters)."),
            "DEVICE_LIMIT" => new LicenseException(code, $"This account is already on {LicenseConfig.MaxDevices} PCs. Remove one to use this PC.", devices),
            "BLOCKED" => new LicenseException(code, "This account is suspended. Contact the seller."),
            _ => new LicenseException(code, message)
        };
    }

    private static string? Pick(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number) return v.ToString();
        return null;
    }
}
