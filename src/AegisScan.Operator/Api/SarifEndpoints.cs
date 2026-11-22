using System.Security.Claims;
using System.Text.Json;
using AegisScan.KubeOps.Data;
using AegisScan.KubeOps.Scanning;
using Microsoft.EntityFrameworkCore;

namespace AegisScan.KubeOps.Api;

/// <summary>SARIF 2.1.0 export of a repository's findings, and CRUD for the webhooks that push it when a scan updates it.</summary>
public static class SarifEndpoints
{
    public static void MapSarifApi(this RouteGroupBuilder api)
    {
        // `scanner` = scanner id; `executionId` = the scan run that last reported the finding; `state` = open | resolved | ...
        api.MapGet("/repositories/{id}/sarif", async (string id, AegisDbContext db, string? scanner = null, string? executionId = null, string? state = null) =>
        {
            id = ApiEndpoints.DecodeId(id);
            if (!await db.Repositories.AsNoTracking().AnyAsync(r => r.Id == id)) return Results.NotFound();
            var query = db.Findings.AsNoTracking().Where(f => f.RepositoryId == id);
            if (!string.IsNullOrWhiteSpace(scanner)) query = query.Where(f => f.Scanner == scanner);
            if (!string.IsNullOrWhiteSpace(executionId)) query = query.Where(f => f.ExecutionId == executionId);
            if (!string.IsNullOrWhiteSpace(state)) query = query.Where(f => f.State == state);
            var sarif = SarifExporter.Build(id, await query.ToListAsync());
            var name = System.Text.RegularExpressions.Regex.Replace(id, "[^A-Za-z0-9._-]", "_");
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(sarif), "application/sarif+json", $"{name}.sarif");
        }).RequireAuthorization(AegisRoles.Viewer);

        // The signing secret is write-only, like the global webhook secret.
        api.MapGet("/webhooks", async (AegisDbContext db) =>
            Results.Ok((await db.Webhooks.AsNoTracking().OrderBy(w => w.Name).ToListAsync()).Select(View)))
            .RequireAuthorization(AegisRoles.Admin);

        api.MapPut("/webhooks/{id}", async (string id, WebhookInput input, AegisDbContext db, ClaimsPrincipal user) =>
        {
            if (!Uri.TryCreate(input.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return Results.BadRequest(new { error = "url must be an absolute http(s) URL" });
            var hook = await db.Webhooks.FindAsync(id);
            if (hook is null) db.Webhooks.Add(hook = new WebhookEntity { Id = id });
            hook.Name = string.IsNullOrWhiteSpace(input.Name) ? uri.Host : input.Name.Trim();
            hook.Url = input.Url!;
            hook.RepositoryId = input.RepositoryId?.Trim() ?? "";
            hook.Scanner = input.Scanner?.Trim() ?? "";
            hook.Enabled = input.Enabled ?? true;
            if (input.Secret is not null && input.Secret != "********") hook.Secret = input.Secret; // omitted / masked = keep the stored one
            await ApiEndpoints.RecordAuditAsync(db, user, "webhook.save", id, "success", new { hook.RepositoryId, hook.Scanner, hook.Enabled });
            await db.SaveChangesAsync();
            return Results.Ok(View(hook));
        }).RequireAuthorization(AegisRoles.Admin);

        api.MapDelete("/webhooks/{id}", async (string id, AegisDbContext db, ClaimsPrincipal user) =>
        {
            var hook = await db.Webhooks.FindAsync(id);
            if (hook is null) return Results.NotFound();
            db.Webhooks.Remove(hook);
            await ApiEndpoints.RecordAuditAsync(db, user, "webhook.delete", id, "success");
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(AegisRoles.Admin);
    }

    private static object View(WebhookEntity w) => new { w.Id, w.Name, w.Url, w.RepositoryId, w.Scanner, w.Enabled, HasSecret = w.Secret.Length > 0 };

    public sealed record WebhookInput(string? Name, string? Url, string? Secret, string? RepositoryId, string? Scanner, bool? Enabled);
}
