using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AegisScan.KubeOps.Data;

namespace AegisScan.KubeOps.Scanning;

/// <summary>Posts JSON events to the configured webhook ("webhookUrl"), signed with HMAC-SHA256 in <c>X-Aegis-Signature</c> when a "webhookSecret" is set.</summary>
public static class WebhookNotifier
{
    public static async Task<(bool Ok, string Detail)> SendAsync(AegisDbContext db, IHttpClientFactory httpClientFactory, object payload, CancellationToken ct)
    {
        var url = await SettingStringAsync(db, "webhookUrl", ct);
        if (string.IsNullOrWhiteSpace(url)) return (false, "No webhook URL is configured.");
        var secret = await SettingStringAsync(db, "webhookSecret", ct);

        var body = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (!string.IsNullOrEmpty(secret))
            request.Headers.Add("X-Aegis-Signature", "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))));
        try
        {
            using var client = httpClientFactory.CreateClient("resilient");
            using var response = await client.SendAsync(request, ct);
            return (response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, ex.Message);
        }
    }

    private static async Task<string?> SettingStringAsync(AegisDbContext db, string key, CancellationToken ct)
    {
        var setting = await db.Settings.FindAsync([key], ct);
        try { return setting is null ? null : JsonSerializer.Deserialize<string>(setting.Value); }
        catch (JsonException) { return null; }
    }
}
