using System.Security.Claims;
using System.Text.Json;
using AegisScan.KubeOps.Data;
using k8s;
using k8s.Autorest;
using Microsoft.EntityFrameworkCore;

namespace AegisScan.KubeOps.Api;

public static class ApiEndpoints
{
    private static readonly string[] AllowedCloneUrlSchemes = ["https", "ssh"];

    public static void MapAegisApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").RequireRateLimiting("api");

        api.MapGet("/repositories", async (AegisDbContext db, int page = 1, int pageSize = 50, string? status = null, string? search = null) =>
        {
            (page, pageSize) = NormalizePaging(page, pageSize);
            var query = db.Repositories.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == status);
            if (!string.IsNullOrWhiteSpace(search)) query = query.Where(r => r.Name.Contains(search));
            var total = await query.CountAsync();
            var repositories = await query.OrderBy(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
            return Results.Ok(new { items = repositories.Select(ToView), total, page, pageSize });
        }).RequireAuthorization(AegisRoles.Viewer);

        api.MapPost("/repositories", async (RepositoryInput input, AegisDbContext db, ClaimsPrincipal user) =>
        {
            var id = string.IsNullOrWhiteSpace(input.Id) ? input.Name : input.Id;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(input.CloneUrl))
                return Results.BadRequest(new { error = "name or id and cloneUrl are required" });
            if (!IsAllowedCloneUrl(input.CloneUrl))
                return Results.BadRequest(new { error = "cloneUrl must be an absolute https or ssh URL with a host" });

            var entity = await db.Repositories.FindAsync(id);
            if (entity is null)
            {
                entity = new RepositoryEntity { Id = id };
                db.Repositories.Add(entity);
            }
            entity.Name = input.Name ?? id;
            entity.Provider = input.Provider ?? "generic";
            entity.CloneUrl = input.CloneUrl;
            entity.DefaultBranch = input.DefaultBranch ?? "main";
            entity.Status = "Ready";
            entity.CacheState = "Unknown";
            await RecordAuditAsync(db, user, "repository.upsert", id, "success", new { entity.Provider, entity.CloneUrl });
            await db.SaveChangesAsync();
            return Results.Ok(ToView(entity));
        }).RequireAuthorization(AegisRoles.Operator);

        api.MapDelete("/repositories/{id}", async (string id, AegisDbContext db, ClaimsPrincipal user) =>
        {
            var entity = await db.Repositories.FindAsync(id);
            if (entity is null) return Results.NotFound();
            db.Repositories.Remove(entity);
            await RecordAuditAsync(db, user, "repository.delete", id, "success");
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(AegisRoles.Operator);

        api.MapPost("/repositories/{id}/unblock", async (string id, AegisDbContext db, ClaimsPrincipal user) =>
        {
            var entity = await db.Repositories.FindAsync(id);
            if (entity is null) return Results.NotFound();
            entity.Status = "Ready";
            await RecordAuditAsync(db, user, "repository.unblock", id, "success");
            await db.SaveChangesAsync();
            return Results.Ok(ToView(entity));
        }).RequireAuthorization(AegisRoles.Admin);

        api.MapGet("/repositories/{id}/gate", async (string id, AegisDbContext db) =>
        {
            var entity = await db.Repositories.FindAsync(id);
            if (entity is null) return Results.NotFound();
            var blocked = entity.Status == "Blocked";
            return Results.Ok(new { blocked, reason = blocked ? "Verified finding detected; blocked pending review." : null });
        }).RequireAuthorization(AegisRoles.Viewer);

        api.MapGet("/repositories/{id}/languages", async (string id, AegisDbContext db, IHttpClientFactory httpClientFactory) =>
        {
            var entity = await db.Repositories.FindAsync(id);
            if (entity is null) return Results.NotFound();
            if (entity.LanguagesJson is null)
            {
                entity.LanguagesJson = await FetchLanguagesJsonAsync(entity, httpClientFactory);
                await db.SaveChangesAsync();
            }
            return Results.Ok(new { items = JsonSerializer.Deserialize<Dictionary<string, long>>(entity.LanguagesJson) });
        }).RequireAuthorization(AegisRoles.Viewer);

        api.MapGet("/runs", async (AegisDbContext db, int page = 1, int pageSize = 50, string? repositoryId = null, string? status = null) =>
        {
            (page, pageSize) = NormalizePaging(page, pageSize);
            var query = db.Executions.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(repositoryId)) query = query.Where(e => e.RepositoryId == repositoryId);
            if (!string.IsNullOrWhiteSpace(status)) query = query.Where(e => e.Status == status);
            var total = await query.CountAsync();
            var executions = await query.OrderByDescending(e => e.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
            return Results.Ok(new { items = executions.Select(ToView), total, page, pageSize });
        }).RequireAuthorization(AegisRoles.Viewer);

        api.MapPost("/runs", async (RunInput input, AegisDbContext db, ClaimsPrincipal user) =>
        {
            var execution = new ExecutionEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                RepositoryId = input.RepositoryId,
                Scanner = input.Scanner ?? "trufflehog",
                Mode = input.Mode ?? "incremental",
                Status = "queued",
                Attempt = 1,
                SanitizedLog = "Scan queued by AegisScan operator."
            };
            db.Executions.Add(execution);
            await RecordAuditAsync(db, user, "run.queue", execution.Id, "success", new { execution.RepositoryId, execution.Scanner, execution.Mode });
            await db.SaveChangesAsync();
            return Results.Ok(ToView(execution));
        }).RequireAuthorization(AegisRoles.Operator);

        api.MapDelete("/runs/{id}", async (string id, AegisDbContext db, ClaimsPrincipal user, IKubernetes kubernetes) =>
        {
            var execution = await db.Executions.FindAsync(id);
            if (execution is null) return Results.NotFound();
            if (execution.JobName is not null)
            {
                var @namespace = Environment.GetEnvironmentVariable("AEGIS_KUBE_NAMESPACE") ?? "default";
                try { await kubernetes.BatchV1.DeleteNamespacedJobAsync(execution.JobName, @namespace, propagationPolicy: "Background"); }
                catch (HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound) { /* already gone */ }
            }
            execution.Status = "canceled";
            execution.FinishedAt = DateTimeOffset.UtcNow;
            await RecordAuditAsync(db, user, "run.cancel", id, "success");
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(AegisRoles.Operator);

        api.MapGet("/runs/{id}/logs", async (string id, AegisDbContext db) =>
            await db.Executions.FindAsync(id) is { } run ? Results.Ok(new { logs = run.SanitizedLog }) : Results.NotFound())
            .RequireAuthorization(AegisRoles.Viewer);

        api.MapGet("/findings", async (AegisDbContext db, int page = 1, int pageSize = 50, string? repositoryId = null, string? state = null, bool? verified = null) =>
        {
            (page, pageSize) = NormalizePaging(page, pageSize);
            var query = db.Findings.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(repositoryId)) query = query.Where(f => f.RepositoryId == repositoryId);
            if (!string.IsNullOrWhiteSpace(state)) query = query.Where(f => f.State == state);
            if (verified.HasValue) query = query.Where(f => f.Verified == verified.Value);
            var total = await query.CountAsync();
            var findings = await query.OrderBy(f => f.Fingerprint).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
            return Results.Ok(new { items = findings.Select(ToView), total, page, pageSize });
        }).RequireAuthorization(AegisRoles.Viewer);

        api.MapGet("/settings", async (AegisDbContext db) => Results.Ok(await LoadSettingsAsync(db)))
            .RequireAuthorization(AegisRoles.Viewer);

        api.MapPut("/settings", async (Dictionary<string, JsonElement> settings, AegisDbContext db, ClaimsPrincipal user) =>
        {
            foreach (var (key, value) in settings)
            {
                var entity = await db.Settings.FindAsync(key);
                if (entity is null)
                {
                    entity = new SettingEntity { Key = key };
                    db.Settings.Add(entity);
                }
                entity.Value = value.GetRawText();
            }
            await RecordAuditAsync(db, user, "settings.update", "settings", "success", settings.Keys);
            await db.SaveChangesAsync();
            return Results.Ok(await LoadSettingsAsync(db));
        }).RequireAuthorization(AegisRoles.Admin);

        api.MapGet("/audit", async (AegisDbContext db, int page = 1, int pageSize = 50) =>
        {
            (page, pageSize) = NormalizePaging(page, pageSize);
            var total = await db.AuditEvents.CountAsync();
            var events = await db.AuditEvents.AsNoTracking()
                .OrderByDescending(a => a.Timestamp)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();
            return Results.Ok(new { items = events.Select(ToView), total, page, pageSize });
        }).RequireAuthorization(AegisRoles.Admin);

        api.MapGet("/scanners", async (AegisDbContext db) =>
        {
            var scanners = await db.Scanners.AsNoTracking().ToListAsync();
            return Results.Ok(new { items = scanners.Select(ToView) });
        }).RequireAuthorization(AegisRoles.Viewer);

        api.MapPost("/scanners", async (ScannerInput input, AegisDbContext db, ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(input.Id) || string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.Image))
                return Results.BadRequest(new { error = "id, name, and image are required" });
            if (await db.Scanners.FindAsync(input.Id) is not null)
                return Results.Conflict(new { error = $"Scanner '{input.Id}' already exists" });

            var entity = new ScannerEntity
            {
                Id = input.Id,
                Name = input.Name,
                Image = input.Image,
                CommandJson = JsonSerializer.Serialize(input.Command ?? []),
                ArgsJson = JsonSerializer.Serialize(input.Args ?? []),
                BuiltIn = false,
                Enabled = input.Enabled ?? true
            };
            db.Scanners.Add(entity);
            await RecordAuditAsync(db, user, "scanner.create", entity.Id, "success", new { entity.Image });
            await db.SaveChangesAsync();
            return Results.Ok(ToView(entity));
        }).RequireAuthorization(AegisRoles.Admin);

        api.MapPut("/scanners/{id}", async (string id, ScannerInput input, AegisDbContext db, ClaimsPrincipal user) =>
        {
            var entity = await db.Scanners.FindAsync(id);
            if (entity is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.Image))
                return Results.BadRequest(new { error = "name and image are required" });

            entity.Name = input.Name;
            entity.Image = input.Image;
            entity.CommandJson = JsonSerializer.Serialize(input.Command ?? []);
            entity.ArgsJson = JsonSerializer.Serialize(input.Args ?? []);
            if (input.Enabled is { } enabled) entity.Enabled = enabled;
            await RecordAuditAsync(db, user, "scanner.update", id, "success", new { entity.Image, entity.Enabled });
            await db.SaveChangesAsync();
            return Results.Ok(ToView(entity));
        }).RequireAuthorization(AegisRoles.Admin);

        api.MapDelete("/scanners/{id}", async (string id, AegisDbContext db, ClaimsPrincipal user) =>
        {
            var entity = await db.Scanners.FindAsync(id);
            if (entity is null) return Results.NotFound();
            if (entity.BuiltIn) return Results.BadRequest(new { error = "built-in scanners cannot be deleted; disable instead" });
            db.Scanners.Remove(entity);
            await RecordAuditAsync(db, user, "scanner.delete", id, "success");
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(AegisRoles.Admin);

        api.MapGet("/reports/findings.csv", async (AegisDbContext db) =>
        {
            var findings = await db.Findings.AsNoTracking().OrderBy(f => f.RepositoryId).ToListAsync();
            var csv = new System.Text.StringBuilder();
            csv.AppendLine("fingerprint,repositoryId,detector,verified,safePath,commitSHA,state");
            foreach (var f in findings)
                csv.AppendLine(string.Join(',', CsvField(f.Fingerprint), CsvField(f.RepositoryId), CsvField(f.Detector),
                    f.Verified, CsvField(f.SafePath), CsvField(f.CommitSHA), CsvField(f.State)));
            return Results.Text(csv.ToString(), "text/csv");
        }).RequireAuthorization(AegisRoles.Viewer);

        api.MapGet("/role-mappings", async (AegisDbContext db) =>
        {
            var mappings = await db.RoleMappings.AsNoTracking().OrderBy(m => m.IdpGroup).ToListAsync();
            return Results.Ok(new { items = mappings.Select(ToView) });
        }).RequireAuthorization(AegisRoles.Admin);

        api.MapPut("/role-mappings/{idpGroup}", async (string idpGroup, RoleMappingInput input, AegisDbContext db, ClaimsPrincipal user) =>
        {
            if (!AegisRoles.KnownRoles.Contains(input.AegisRole, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"aegisRole must be one of: {string.Join(", ", AegisRoles.KnownRoles)}" });

            var entity = await db.RoleMappings.FindAsync(idpGroup);
            if (entity is null)
            {
                entity = new RoleMappingEntity { IdpGroup = idpGroup };
                db.RoleMappings.Add(entity);
            }
            entity.AegisRole = input.AegisRole.ToLowerInvariant();
            await RecordAuditAsync(db, user, "roleMapping.upsert", idpGroup, "success", new { entity.AegisRole });
            await db.SaveChangesAsync();
            return Results.Ok(ToView(entity));
        }).RequireAuthorization(AegisRoles.Admin);

        api.MapDelete("/role-mappings/{idpGroup}", async (string idpGroup, AegisDbContext db, ClaimsPrincipal user) =>
        {
            var entity = await db.RoleMappings.FindAsync(idpGroup);
            if (entity is null) return Results.NotFound();
            db.RoleMappings.Remove(entity);
            await RecordAuditAsync(db, user, "roleMapping.delete", idpGroup, "success");
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(AegisRoles.Admin);
    }

    private static string CsvField(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private static (int Page, int PageSize) NormalizePaging(int page, int pageSize) =>
        (Math.Max(page, 1), Math.Clamp(pageSize, 1, 200));

    private static bool IsAllowedCloneUrl(string cloneUrl) =>
        Uri.TryCreate(cloneUrl, UriKind.Absolute, out var uri)
        && uri.Host.Length > 0
        && AllowedCloneUrlSchemes.Contains(uri.Scheme, StringComparer.Ordinal);

    /// <summary>Fetches bytes-per-language from GitHub's /repos/{owner}/{repo}/languages. Only "github" repos are supported today (mirrors RepositorySourceController); anything else, or any failure, yields an empty map rather than blocking the response.</summary>
    private static async Task<string> FetchLanguagesJsonAsync(RepositoryEntity repository, IHttpClientFactory httpClientFactory)
    {
        var ownerRepo = ParseGitHubOwnerRepo(repository.Provider, repository.CloneUrl);
        if (ownerRepo is null) return "{}";
        try
        {
            var httpClient = httpClientFactory.CreateClient("resilient");
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AegisScan/1.0");
            using var response = await httpClient.GetAsync($"https://api.github.com/repos/{ownerRepo}/languages");
            if (!response.IsSuccessStatusCode) return "{}";
            return await response.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException)
        {
            return "{}";
        }
    }

    private static string? ParseGitHubOwnerRepo(string provider, string cloneUrl)
    {
        if (!string.Equals(provider, "github", StringComparison.OrdinalIgnoreCase)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(cloneUrl, @"github\.com[:/]([^/]+/[^/]+?)(\.git)?/?$");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static async Task<Dictionary<string, JsonElement>> LoadSettingsAsync(AegisDbContext db)
    {
        var rows = await db.Settings.AsNoTracking().ToListAsync();
        return rows.ToDictionary(row => row.Key, row => JsonDocument.Parse(row.Value).RootElement);
    }

    private static Task RecordAuditAsync(AegisDbContext db, ClaimsPrincipal user, string action, string target, string outcome, object? metadata = null)
    {
        var actor = user.Identity?.Name ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
        db.AuditEvents.Add(new AuditEntity
        {
            Timestamp = DateTimeOffset.UtcNow,
            Actor = actor,
            Action = action,
            Target = target,
            Outcome = outcome,
            Metadata = JsonSerializer.Serialize(metadata ?? new { })
        });
        return Task.CompletedTask;
    }

    private static RepositoryView ToView(RepositoryEntity entity) =>
        new(entity.Id, entity.Name, entity.Provider, entity.CloneUrl, entity.DefaultBranch, entity.Status, entity.CacheState)
        {
            ActiveFindingCount = entity.ActiveFindingCount,
            LatestCommitSHA = entity.LatestCommitSHA,
            Languages = entity.LanguagesJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, long>>(entity.LanguagesJson)
        };

    private static ExecutionView ToView(ExecutionEntity entity) =>
        new(entity.Id, entity.RepositoryId, entity.Scanner, entity.Mode, entity.Status, entity.Attempt, entity.SanitizedLog)
        {
            StartedAt = entity.StartedAt,
            FinishedAt = entity.FinishedAt
        };

    private static FindingView ToView(FindingEntity entity) =>
        new(entity.Fingerprint, entity.RepositoryId, entity.Detector, entity.Verified, entity.SafePath, entity.CommitSHA, entity.State);

    private static AuditView ToView(AuditEntity entity) =>
        new(entity.Id, entity.Timestamp, entity.Actor, entity.Action, entity.Target, entity.Outcome, entity.Metadata);

    private static RoleMappingView ToView(RoleMappingEntity entity) =>
        new(entity.IdpGroup, entity.AegisRole);

    private static ScannerView ToView(ScannerEntity entity) =>
        new(entity.Id, entity.Name, entity.Image,
            JsonSerializer.Deserialize<List<string>>(entity.CommandJson) ?? [],
            JsonSerializer.Deserialize<List<string>>(entity.ArgsJson) ?? [],
            entity.BuiltIn, entity.Enabled);

    public sealed record RepositoryInput(string? Id, string? Name, string? Provider, string CloneUrl, string? DefaultBranch);
    public sealed record RunInput(string RepositoryId, string? Scanner, string? Mode);
    public sealed record ScannerInput(string? Id, string Name, string Image, List<string>? Command, List<string>? Args, bool? Enabled);
    public sealed record RoleMappingInput(string AegisRole);
}
