using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AegisScan.KubeOps.Data;
using Microsoft.EntityFrameworkCore;

namespace AegisScan.KubeOps.Scanning;

/// <summary>Posts JSON events to the configured webhook ("webhookUrl"), signed with HMAC-SHA256 in <c>X-Aegis-Signature</c> when a "webhookSecret" is set.</summary>
public static class WebhookNotifier
{
    public static async Task<(bool Ok, string Detail)> SendAsync(AegisDbContext db, IHttpClientFactory httpClientFactory, object payload, CancellationToken ct)
    {
        var url = await SettingStringAsync(db, "webhookUrl", ct);
        if (string.IsNullOrWhiteSpace(url)) return (false, "No webhook URL is configured.");
        return await PostAsync(httpClientFactory, url, await SettingStringAsync(db, "webhookSecret", ct), payload, ct);
    }

    /// <summary>Sends "sarif.updated" (the SARIF of the repository's findings for the scanner that just ran) to every enabled webhook matching the repository and scanner.</summary>
    public static async Task SendSarifAsync(AegisDbContext db, IHttpClientFactory httpClientFactory, ILogger logger, ExecutionEntity execution, CancellationToken ct)
    {
        var hooks = await db.Webhooks.AsNoTracking()
            .Where(w => w.Enabled && (w.RepositoryId == "" || w.RepositoryId == execution.RepositoryId) && (w.Scanner == "" || w.Scanner == execution.Scanner)).ToListAsync(ct);
        if (hooks.Count == 0) return;
        await db.SaveChangesAsync(ct); // the scan's findings are still pending in this tick's transaction
        var sarif = SarifExporter.Build(execution.RepositoryId, await db.Findings.AsNoTracking().Where(f => f.RepositoryId == execution.RepositoryId && f.Scanner == execution.Scanner).ToListAsync(ct));
        var payload = new { @event = "sarif.updated", executionId = execution.Id, repositoryId = execution.RepositoryId, scanner = execution.Scanner, commit = execution.CommitSHA, sarif };
        foreach (var hook in hooks)
        {
            var (ok, detail) = await PostAsync(httpClientFactory, hook.Url, hook.Secret, payload, ct);
            if (!ok) logger.LogWarning("SARIF webhook {Webhook} for execution {ExecutionId} failed: {Detail}.", hook.Id, execution.Id, detail);
        }
    }

    private static async Task<(bool Ok, string Detail)> PostAsync(IHttpClientFactory httpClientFactory, string url, string? secret, object payload, CancellationToken ct)
    {
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
