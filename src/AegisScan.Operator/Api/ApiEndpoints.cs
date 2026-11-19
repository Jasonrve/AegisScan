using System.Security.Claims;
using System.Text.Json;
using AegisScan.KubeOps.Data;
using AegisScan.KubeOps.Resources;
using KubeOps.KubernetesClient;
using AegisScan.KubeOps.Scanning;
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
            var ids = repositories.Select(r => r.Id).ToList();
            var openCounts = await db.Findings.Where(f => ids.Contains(f.RepositoryId) && f.State == "open")
                .GroupBy(f => f.RepositoryId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
            var links = await LinkContext.CreateAsync(db, ids);
            var lastStatus = await db.Executions.Where(e => ids.Contains(e.RepositoryId) && (e.Status == "succeeded" || e.Status == "failed"))
                .GroupBy(e => e.RepositoryId).Select(g => new { g.Key, Status = g.OrderByDescending(e => e.CreatedAt).Select(e => e.Status).FirstOrDefault() })
                .ToDictionaryAsync(x => x.Key, x => x.Status);
            return Results.Ok(new { items = repositories.Select(r => ToView(r, openCounts.GetValueOrDefault(r.Id), links) with { Health = HealthOf(r, lastStatus.GetValueOrDefault(r.Id)) }), total, page, pageSize });
        }).RequireAuthorization(AegisRoles.Viewer);

        api.MapPost("/repositories", async (RepositoryInput input, AegisDbContext db, ClaimsPrincipal user) =>
        {
            var id = string.IsNullOrWhiteSpace(input.Id) ? input.Name : input.Id;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(input.CloneUrl))
                return Results.BadRequest(new { error = "name or id and cloneUrl are required" });
            if (!IsAllowedCloneUrl(input.CloneUrl))
                return Results.BadRequest(new { error = "cloneUrl must be an absolute https or ssh URL with a host" });

            var entity = await db.Repositories.FindAsync(id);
            var isNew = entity is null;
            if (entity is null)
            {
                entity = new RepositoryEntity { Id = id };
                db.Repositories.Add(entity);
            }
            entity.Name = input.Name ?? id;
            entity.Provider = input.Provider ?? "generic";
            entity.CloneUrl = input.CloneUrl;
            entity.DefaultBranch = input.DefaultBranch ?? "main";
            // Only a new repository starts Ready: re-posting an existing one must not clear Blocked (unblocking is an admin action).
            if (isNew) { entity.Status = "Ready"; entity.CacheState = "Unknown"; }
            await RecordAuditAsync(db, user, "repository.upsert", id, "success", new { entity.Provider, entity.CloneUrl });
            await db.SaveChangesAsync();
            return Results.Ok(ToView(entity, await CountOpenFindingsAsync(db, id), await LinkContext.CreateAsync(db, [id])));
        }).RequireAuthorization(AegisRoles.Operator);

        api.MapDelete("/repositories/{id}", async (string id, AegisDbContext db, ClaimsPrincipal user) =>
        {
            id = DecodeId(id);
            var entity = await db.Repositories.FindAsync(id);
            if (entity is null) return Results.NotFound();
            if (entity.Status == "Blocked" && !user.IsInRole("admin"))
                return Results.Json(new { error = "a blocked repository can only be removed by an admin (removing it would discard its findings and clear the block)" }, statusCode: StatusCodes.Status403Forbidden);
            db.RemoveRepositoryWithData(entity);
            await RecordAuditAsync(db, user, "repository.delete", id, "success");
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(AegisRoles.Operator);

        api.MapPost("/repositories/{id}/unblock", async (string id, AegisDbContext db, ClaimsPrincipal user) =>
        {
            id = DecodeId(id);
            var entity = await db.Repositories.FindAsync(id);
            if (entity is null) return Results.NotFound();
            entity.Status = "Ready";
            db.ClearAlertAck($"repo:{id}");
            await RecordAuditAsync(db, user, "repository.unblock", id, "success");
            await db.SaveChangesAsync();
            return Results.Ok(ToView(entity, await CountOpenFindingsAsync(db, id), await LinkContext.CreateAsync(db, [id])));
        }).RequireAuthorization(AegisRoles.Admin);

        api.MapGet("/repositories/{id}/gate", async (string id, AegisDbContext db) =>
        {
            id = DecodeId(id);
            var entity = await db.Repositories.FindAsync(id);
            if (entity is null) return Results.NotFound();
            var blocked = entity.Status == "Blocked";
            return Results.Ok(new { blocked, reason = blocked ? "Verified finding detected; blocked pending review." : null });
        }).RequireAuthorization(AegisRoles.Viewer);

        api.MapGet("/repositories/{id}/languages", async (string id, AegisDbContext db, IHttpClientFactory httpClientFactory) =>
        {
            id = DecodeId(id);
            var entity = await db.Repositories.FindAsync(id);
            if (entity is null) return Results.NotFound();
            if (entity.LanguagesJson is null)
            {
                entity.LanguagesJson = await FetchLanguagesJsonAsync(entity, httpClientFactory);
                await db.SaveChangesAsync();
            }
            return Results.Ok(new { items = JsonSerializer.Deserialize<Dictionary<string, long>>(entity.LanguagesJson) });
        }).RequireAuthorization(AegisRoles.Viewer);

        api.MapGet("/runs", async (AegisDbContext db, int page = 1, int pageSize = 50, string? repositoryId = null, string? status = null, string? scanner = null) =>
        {
            (page, pageSize) = NormalizePaging(page, pageSize);
            var query = db.Executions.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(repositoryId)) query = query.Where(e => e.RepositoryId == repositoryId);
            if (!string.IsNullOrWhiteSpace(scanner)) query = query.Where(e => e.Scanner == scanner);
            if (!string.IsNullOrWhiteSpace(status)) query = query.Where(e => e.Status == status);
            var total = await query.CountAsync();
            var executions = await query.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
            var links = await LinkContext.CreateAsync(db, executions.Select(e => e.RepositoryId));
            return Results.Ok(new { items = executions.Select(e => ToView(e, links)), total, page, pageSize });
        }).RequireAuthorization(AegisRoles.Viewer);

        api.MapPost("/runs", async (RunInput input, AegisDbContext db, ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(input.RepositoryId) || await db.Repositories.FindAsync(input.RepositoryId) is null)
                return Results.NotFound(new { error = $"Repository '{input.RepositoryId}' does not exist" });
            var scannerId = string.IsNullOrWhiteSpace(input.Scanner) ? "trufflehog" : input.Scanner;
            if (await db.Scanners.FindAsync(scannerId) is not { Enabled: true })
                return Results.BadRequest(new { error = $"Scanner '{scannerId}' does not exist or is disabled" });
            if (input.Mode is not (null or "incremental" or "full"))
                return Results.BadRequest(new { error = "mode must be 'incremental' or 'full'" });
            // Unspecified mode falls back to the "defaultScanMode" setting (see Settings view), then incremental.
            string? defaultMode = null;
            try { defaultMode = await db.Settings.FindAsync("defaultScanMode") is { } modeSetting ? JsonSerializer.Deserialize<string>(modeSetting.Value) : null; }
            catch (JsonException) { /* malformed legacy value: fall back to incremental */ }
            var execution = new ExecutionEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                RepositoryId = input.RepositoryId,
                Scanner = scannerId,
                Mode = input.Mode ?? (defaultMode == "full" ? "full" : "incremental"),
                Status = "queued",
                Attempt = 1,
                SanitizedLog = "Scan queued by AegisScan operator."
            };
            db.Executions.Add(execution);
            await RecordAuditAsync(db, user, "run.queue", execution.Id, "success", new { execution.RepositoryId, execution.Scanner, execution.Mode });
            await db.SaveChangesAsync();
            return Results.Ok(ToView(execution, await LinkContext.CreateAsync(db, [execution.RepositoryId])));
        }).RequireAuthorization(AegisRoles.Operator);

        api.MapDelete("/runs/{id}", async (string id, AegisDbContext db, ClaimsPrincipal user, IKubernetes kubernetes) =>
        {
            var execution = await db.Executions.FindAsync(id);
            if (execution is null) return Results.NotFound();
            if (execution.Status is not ("queued" or "running" or "preparing"))
                return Results.Conflict(new { error = $"Run is already {execution.Status} and cannot be canceled" });
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
            await db.Executions.FindAsync(id) is { } run
                ? Results.Ok(new { logs = string.IsNullOrEmpty(run.OutputLog) ? run.SanitizedLog : $"{run.SanitizedLog}\n\n{run.OutputLog}" })
                : Results.NotFound())
            .RequireAuthorization(AegisRoles.Viewer);

        api.MapGet("/findings", async (AegisDbContext db, int page = 1, int pageSize = 50, string? repositoryId = null, string? state = null, bool? verified = null, string? scanner = null, string? detector = null) =>
        {
            (page, pageSize) = NormalizePaging(page, pageSize);
            var query = db.Findings.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(repositoryId)) query = query.Where(f => f.RepositoryId == repositoryId);
            if (!string.IsNullOrWhiteSpace(state)) query = query.Where(f => f.State == state);
            if (verified.HasValue) query = query.Where(f => f.Verified == verified.Value);
            if (!string.IsNullOrWhiteSpace(scanner)) query = query.Where(f => f.Scanner == scanner);
            if (!string.IsNullOrWhiteSpace(detector)) query = query.Where(f => f.Detector == detector);
            var total = await query.CountAsync();
            var findings = await query.OrderByDescending(f => f.LastSeenAt).ThenBy(f => f.Fingerprint).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
            var links = await LinkContext.CreateAsync(db, findings.Select(f => f.RepositoryId));
            return Results.Ok(new { items = findings.Select(f => ToView(f, links)), total, page, pageSize });
        }).RequireAuthorization(AegisRoles.Viewer);

        api.MapPatch("/findings/{fingerprint}", async (string fingerprint, FindingStateInput input, AegisDbContext db, ClaimsPrincipal user) =>
        {
            if (!FindingStates.Contains(input.State, StringComparer.Ordinal))
                return Results.BadRequest(new { error = $"state must be one of: {string.Join(", ", FindingStates)}" });
            // Ignoring or dismissing a finding is an audit-relevant decision: it must say why.
            if (input.State is "ignored" or "false_positive" && string.IsNullOrWhiteSpace(input.Reason))
                return Results.BadRequest(new { error = $"a reason is required to mark a finding {input.State.Replace('_', ' ')}" });
            var finding = await db.Findings.FindAsync(fingerprint);
            if (finding is null) return Results.NotFound();
            var previous = finding.State;
            finding.State = input.State;
            finding.ResolvedAt = input.State == "resolved" ? DateTimeOffset.UtcNow : null;
            db.ClearAlertAck($"finding:{fingerprint}");
            await RecordAuditAsync(db, user, "finding.triage", fingerprint, "success", new { finding.RepositoryId, from = previous, to = input.State, reason = input.Reason?.Trim() });
            await db.SaveChangesAsync();
            return Results.Ok(ToView(finding, await LinkContext.CreateAsync(db, [finding.RepositoryId])));
        }).RequireAuthorization(AegisRoles.Operator);

        api.MapGet("/findings/{fingerprint}/history", async (string fingerprint, AegisDbContext db) =>
        {
            var events = await db.AuditEvents.AsNoTracking().Where(a => a.Action == "finding.triage" && a.Target == fingerprint).OrderByDescending(a => a.Timestamp).Take(50).ToListAsync();
            return Results.Ok(new { items = events.Select(ToHistory) });
        }).RequireAuthorization(AegisRoles.Viewer);

        api.MapPut("/findings/{fingerprint}/assignee", async (string fingerprint, AssigneeInput input, AegisDbContext db, ClaimsPrincipal user) =>
        {
            var assignee = (input.Assignee ?? "").Trim();
            if (assignee.Length > 100) return Results.BadRequest(new { error = "assignee must be at most 100 characters" });
            var finding = await db.Findings.FindAsync(fingerprint);
            if (finding is null) return Results.NotFound();
            var previous = finding.Assignee;
            finding.Assignee = assignee;
            await RecordAuditAsync(db, user, "finding.assign", fingerprint, "success", new { finding.RepositoryId, from = previous, to = assignee });
            await db.SaveChangesAsync();
            return Results.Ok(ToView(finding, await LinkContext.CreateAsync(db, [finding.RepositoryId])));
        }).RequireAuthorization(AegisRoles.Operator);

        api.MapGet("/findings/stats", async (AegisDbContext db) =>
        {
            var links = await LinkContext.CreateAsync(db, []);
            var open = await db.Findings.AsNoTracking().Where(f => f.State == "open").ToListAsync();
            var since = DateTimeOffset.UtcNow.AddDays(-90);
            var resolved = await db.Findings.AsNoTracking().Where(f => f.State == "resolved" && f.ResolvedAt != null && f.ResolvedAt > since)
                .Select(f => new { f.FirstSeenAt, ResolvedAt = f.ResolvedAt!.Value }).ToListAsync();
            double? mttr = resolved.Count == 0 ? null : Math.Round(resolved.Average(r => (r.ResolvedAt - r.FirstSeenAt).TotalDays), 1);
            return Results.Ok(new { open = open.Count, overdue = open.Count(links.IsOverdue), mttrDays = mttr, resolvedLast90Days = resolved.Count });
        }).RequireAuthorization(AegisRoles.Viewer);

        // ScanPolicy resources are the declarative source of truth (GitOps); this is the read-only governance view of them.
        api.MapGet("/policies", async (AegisDbContext db, IKubernetesClient client) =>
        {
            var policies = await client.ListAsync<V1ScanPolicy>();
            var counts = await db.Repositories.Where(r => r.PolicyRef != null).GroupBy(r => r.PolicyRef!).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
            return Results.Ok(new
            {
                items = policies.OrderBy(p => p.Metadata.Name).Select(p => new
                {
                    name = p.Metadata.Name,
                    scanners = p.Spec.Scanners,
                    schedule = p.Spec.Schedule,
                    fullScanSchedule = p.Spec.FullScanSchedule,
                    incrementalByDefault = p.Spec.IncrementalByDefault,
                    timeoutSeconds = p.Spec.TimeoutSeconds,
                    retries = p.Spec.Retries,
                    maxConcurrency = p.Spec.MaxConcurrency,
                    phase = p.Status.Phase,
                    message = p.Status.Message,
                    repositories = counts.GetValueOrDefault(p.Metadata.Name),
                    createdAt = p.Metadata.CreationTimestamp
                })
            });
        }).RequireAuthorization(AegisRoles.Viewer);

        api.MapGet("/alerts/acks", async (AegisDbContext db) =>
        {
            var now = DateTimeOffset.UtcNow;
            var acks = await db.AlertAcks.AsNoTracking().Where(a => a.SnoozeUntil == null || a.SnoozeUntil > now).ToListAsync();
            return Results.Ok(new { items = acks.Select(a => new { a.Key, a.Actor, a.AcknowledgedAt, a.SnoozeUntil }) });
        }).RequireAuthorization(AegisRoles.Viewer);

        api.MapPut("/alerts/acks", async (AlertAckInput input, AegisDbContext db, ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(input.Key) || input.Key.Length > 300) return Results.BadRequest(new { error = "key is required (max 300 characters)" });
            if (input.SnoozeHours is { } hours && hours is < 1 or > 720) return Results.BadRequest(new { error = "snoozeHours must be between 1 and 720" });
            var ack = await db.AlertAcks.FindAsync(input.Key);
            if (ack is null) { ack = new AlertAckEntity { Key = input.Key }; db.AlertAcks.Add(ack); }
            ack.Actor = user.Identity?.Name ?? "unknown";
            ack.AcknowledgedAt = DateTimeOffset.UtcNow;
            ack.SnoozeUntil = input.SnoozeHours is { } h ? ack.AcknowledgedAt.AddHours(h) : null;
            await RecordAuditAsync(db, user, input.SnoozeHours is null ? "alert.acknowledge" : "alert.snooze", input.Key, "success", new { input.SnoozeHours });
            await db.SaveChangesAsync();
            return Results.Ok(new { ack.Key, ack.Actor, ack.AcknowledgedAt, ack.SnoozeUntil });
        }).RequireAuthorization(AegisRoles.Operator);

        api.MapDelete("/alerts/acks", async (string key, AegisDbContext db, ClaimsPrincipal user) =>
        {
            var ack = await db.AlertAcks.FindAsync(key);
            if (ack is null) return Results.NotFound();
            db.AlertAcks.Remove(ack);
            await RecordAuditAsync(db, user, "alert.unacknowledge", key, "success");
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(AegisRoles.Operator);

        api.MapPost("/webhook/test", async (AegisDbContext db, IHttpClientFactory httpClientFactory, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var (ok, detail) = await WebhookNotifier.SendAsync(db, httpClientFactory, new { @event = "webhook.test", message = "AegisScan test notification", timestamp = DateTimeOffset.UtcNow }, ct);
            await RecordAuditAsync(db, user, "webhook.test", "webhook", ok ? "success" : "failure", new { detail });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { ok, detail });
        }).RequireAuthorization(AegisRoles.Admin);

        api.MapGet("/system", async (AegisDbContext db, IKubernetes kubernetes) =>
        {
            string? kubernetesVersion = null;
            try { kubernetesVersion = (await kubernetes.Version.GetCodeAsync()).GitVersion; }
            catch (Exception ex) when (ex is HttpRequestException or HttpOperationException or TaskCanceledException) { /* reported as unreachable */ }
            return Results.Ok(new
            {
                version = typeof(ApiEndpoints).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                kubernetes = new { reachable = kubernetesVersion is not null, version = kubernetesVersion, @namespace = Environment.GetEnvironmentVariable("AEGIS_KUBE_NAMESPACE") ?? "default" },
                queue = new { queued = await db.Executions.CountAsync(e => e.Status == "queued"), running = await db.Executions.CountAsync(e => e.Status == "running") }
            });
        }).RequireAuthorization(AegisRoles.Viewer);

        api.MapGet("/settings", async (AegisDbContext db, ClaimsPrincipal user) => Results.Ok(await LoadSettingsAsync(db, user.IsInRole("admin"))))
            .RequireAuthorization(AegisRoles.Viewer);

        api.MapPut("/settings", async (Dictionary<string, JsonElement> settings, AegisDbContext db, ClaimsPrincipal user) =>
        {
            if (ValidateSettings(settings) is { } settingsError) return Results.BadRequest(new { error = settingsError });
            var changes = new Dictionary<string, object?>();
            foreach (var (key, value) in settings)
            {
                if (key == "webhookSecret" && value.ValueKind == JsonValueKind.String && value.GetString() == "********") continue; // unchanged
                var entity = await db.Settings.FindAsync(key);
                var before = entity?.Value;
                if (entity is null)
                {
                    entity = new SettingEntity { Key = key };
                    db.Settings.Add(entity);
                }
                entity.Value = value.GetRawText();
                if (before != entity.Value)
                    changes[key] = key == "webhookSecret" ? "changed" : new { from = before is null ? (object?)null : JsonElement.Parse(before), to = value };
            }
            await RecordAuditAsync(db, user, "settings.update", "settings", "success", changes);
            await db.SaveChangesAsync();
            return Results.Ok(await LoadSettingsAsync(db, true));
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
            if (!IsValidScannerId(input.Id))
                return Results.BadRequest(new { error = "id must be lowercase letters, digits or '-' (max 63 chars, starting and ending alphanumeric): it becomes a Kubernetes label value" });
            if (await db.Scanners.FindAsync(input.Id) is not null)
                return Results.Conflict(new { error = $"Scanner '{input.Id}' already exists" });

            if (ValidateOutputContract(input) is { } contractError) return Results.BadRequest(new { error = contractError });
            if (input.OutputFormat == "native") return Results.BadRequest(new { error = "outputFormat 'native' is reserved for built-in scanners" });
            var entity = new ScannerEntity
            {
                Id = input.Id,
                Name = input.Name,
                Image = input.Image,
                CommandJson = JsonSerializer.Serialize(input.Command ?? []),
                ArgsJson = JsonSerializer.Serialize(input.Args ?? []),
                BuiltIn = false,
                Enabled = input.Enabled ?? true,
                OutputFormat = input.OutputFormat ?? GenericResultParser.FormatNone,
                Category = input.Category ?? "other",
                SuccessExitCodesJson = JsonSerializer.Serialize(input.SuccessExitCodes ?? [0])
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

            if (ValidateOutputContract(input) is { } contractError) return Results.BadRequest(new { error = contractError });
            if (!entity.BuiltIn && input.OutputFormat == "native") return Results.BadRequest(new { error = "outputFormat 'native' is reserved for built-in scanners" });

            entity.Name = input.Name;
            entity.Image = input.Image;
            entity.CommandJson = JsonSerializer.Serialize(input.Command ?? []);
            entity.ArgsJson = JsonSerializer.Serialize(input.Args ?? []);
            if (input.Enabled is { } enabled) entity.Enabled = enabled;
            if (input.OutputFormat is not null && !entity.BuiltIn) entity.OutputFormat = input.OutputFormat; // built-ins keep their native parser
            if (input.Category is not null && !entity.BuiltIn) entity.Category = input.Category;
            if (input.SuccessExitCodes is not null) entity.SuccessExitCodesJson = JsonSerializer.Serialize(input.SuccessExitCodes);
            await RecordAuditAsync(db, user, "scanner.update", id, "success", new { entity.Image, entity.Enabled });
            await db.SaveChangesAsync();
            return Results.Ok(ToView(entity));
        }).RequireAuthorization(AegisRoles.Admin);

        // "Will it display correctly?" before running: parses pasted scanner output with the same code a real run uses; nothing is stored.
        api.MapPost("/scanners/preview-parse", (ParsePreviewInput input) =>
        {
            if (!GenericResultParser.Formats.Contains(input.Format) || input.Format == GenericResultParser.FormatNone)
                return Results.BadRequest(new { error = "format must be 'sarif' or 'aegis-json'" });
            if (input.Output is null || input.Output.Length > 2_000_000) return Results.BadRequest(new { error = "output is required and limited to 2 MB" });
            var scanner = new ScannerEntity { Id = "preview", Category = input.Category is not null && GenericResultParser.Categories.Contains(input.Category) ? input.Category : "other" };
            var result = GenericResultParser.Parse(input.Format, input.Output!, "preview", scanner);
            return Results.Ok(new
            {
                error = result.Error,
                authoritative = result.Authoritative,
                output = result.Output,
                findings = result.Findings.Take(100).Select(f => new { detector = f.Detector, severity = f.Severity, path = f.SafePath, line = f.SourceLine, detail = f.Detail, helpUrl = f.HelpUrl, preview = f.Preview, verified = f.Verified })
            });
        }).RequireAuthorization(AegisRoles.Operator);

        api.MapDelete("/scanners/{id}",async (string id, AegisDbContext db, ClaimsPrincipal user) =>
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
            csv.AppendLine("fingerprint,repositoryId,scanner,detector,severity,verified,safePath,detail,commitSHA,state,firstSeen,lastSeen");
            foreach (var f in findings)
                csv.AppendLine(string.Join(',', CsvField(f.Fingerprint), CsvField(f.RepositoryId), CsvField(f.Scanner), CsvField(f.Detector),
                    FindingSeverity.For(f), f.Verified, CsvField(f.SafePath), CsvField(f.Detail), CsvField(f.CommitSHA), CsvField(f.State),
                    f.FirstSeenAt.ToString("O"), f.LastSeenAt.ToString("O")));
            return Results.Text(csv.ToString(), "text/csv");
        }).RequireAuthorization(AegisRoles.Viewer);

        api.MapGet("/reports/runs.csv", async (AegisDbContext db) =>
        {
            var runs = await db.Executions.AsNoTracking().OrderByDescending(e => e.CreatedAt).Take(5000).ToListAsync();
            var csv = new System.Text.StringBuilder();
            csv.AppendLine("id,repositoryId,scanner,mode,status,attempt,findingCount,commit,createdAt,startedAt,finishedAt");
            foreach (var e in runs)
                csv.AppendLine(string.Join(',', CsvField(e.Id), CsvField(e.RepositoryId), CsvField(e.Scanner), CsvField(e.Mode), CsvField(e.Status), e.Attempt, e.FindingCount,
                    CsvField(e.CommitSHA), e.CreatedAt.ToString("O"), e.StartedAt?.ToString("O"), e.FinishedAt?.ToString("O")));
            return Results.Text(csv.ToString(), "text/csv");
        }).RequireAuthorization(AegisRoles.Viewer);

        api.MapGet("/reports/audit.csv", async (AegisDbContext db) =>
        {
            var events = await db.AuditEvents.AsNoTracking().OrderByDescending(a => a.Timestamp).Take(20000).ToListAsync();
            var csv = new System.Text.StringBuilder();
            csv.AppendLine("timestamp,actor,action,target,outcome,metadata");
            foreach (var a in events)
                csv.AppendLine(string.Join(',', a.Timestamp.ToString("O"), CsvField(a.Actor), CsvField(a.Action), CsvField(a.Target), CsvField(a.Outcome), CsvField(a.Metadata)));
            return Results.Text(csv.ToString(), "text/csv");
        }).RequireAuthorization(AegisRoles.Admin);

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

        api.MapSbomApi();
        api.MapActivityApi();
        api.MapDashboardApi();
        api.MapFixesApi();
    }

    /// <summary>Shape checks for settings the backend reads: a malformed value must be rejected at write time, not break every later read.</summary>
    public static string? ValidateSettings(IReadOnlyDictionary<string, JsonElement> settings)
    {
        static bool IsHttpUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        foreach (var (key, value) in settings)
        {
            switch (key)
            {
                case "maxConcurrency" when !(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var c) && c is >= 1 and <= 100):
                    return "maxConcurrency must be an integer between 1 and 100";
                case "retentionDays" when !(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var d) && d is >= 1 and <= 3650):
                    return "retentionDays must be an integer between 1 and 3650";
                case "defaultScanMode" when !(value.ValueKind == JsonValueKind.String && value.GetString() is "incremental" or "full"):
                    return "defaultScanMode must be 'incremental' or 'full'";
                case "blockVerifiedFindings" or "renovateCreatePullRequests" when value.ValueKind is not (JsonValueKind.True or JsonValueKind.False):
                    return $"{key} must be true or false";
                case "webhookUrl" when value.ValueKind != JsonValueKind.Null && !(value.ValueKind == JsonValueKind.String && (string.IsNullOrWhiteSpace(value.GetString()) || IsHttpUrl(value.GetString()))):
                    return "webhookUrl must be an absolute http(s) URL";
                case "vulnerabilityLookup" when value.ValueKind is not (JsonValueKind.True or JsonValueKind.False):
                    return "vulnerabilityLookup must be true or false";
                case "webhookSecret" when value.ValueKind != JsonValueKind.String:
                    return "webhookSecret must be a string";
                case "slaDays":
                    if (value.ValueKind != JsonValueKind.Object) return "slaDays must be an object of severity -> days";
                    foreach (var entry in value.EnumerateObject())
                        if (entry.Name is not ("critical" or "high" or "medium" or "low") || !(entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetInt32(out var sla) && sla is >= 1 and <= 3650))
                            return "slaDays entries must be critical/high/medium/low -> an integer between 1 and 3650";
                    break;
                case "forgeUrlOverrides":
                    if (value.ValueKind != JsonValueKind.Object) return "forgeUrlOverrides must be an object of internal URL -> public URL";
                    foreach (var entry in value.EnumerateObject())
                        if (entry.Value.ValueKind != JsonValueKind.String || !IsHttpUrl(entry.Name) || !IsHttpUrl(entry.Value.GetString()))
                            return "forgeUrlOverrides entries must map an absolute http(s) URL to an absolute http(s) URL";
                    break;
            }
        }
        return null;
    }

    /// <summary>Repository ids can contain '/' (discovery creates owner/name), which cannot travel in a path segment; clients send '~' instead.</summary>
    public static string DecodeId(string id) => id.Replace('~', '/');

    public static string HealthOf(RepositoryEntity repository, string? lastFinishedStatus) =>
        repository.Status == "Blocked" ? "blocked" : lastFinishedStatus is null ? "never-scanned" : lastFinishedStatus == "failed" ? "failing" : "healthy";

    private static readonly string[] FindingStates = ["open", "resolved", "ignored", "false_positive"];

    private static Task<int> CountOpenFindingsAsync(AegisDbContext db, string repositoryId) =>
        db.Findings.CountAsync(f => f.RepositoryId == repositoryId && f.State == "open");

    /// <summary>Quotes CSV fields and neutralizes spreadsheet formulas: finding fields come from repository content, so a value starting with = + - @ would otherwise execute when the export is opened in Excel.</summary>
    public static string CsvField(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    private static (int Page, int PageSize) NormalizePaging(int page, int pageSize) =>
        (Math.Max(page, 1), Math.Clamp(pageSize, 1, 200));

    /// <summary>Scanner ids are stamped on Jobs as the `aegisscan.io/scanner` label, so they must be valid label values.</summary>
    public static bool IsValidScannerId(string id) => ScannerIdPattern.IsMatch(id);

    /// <summary>Validates the optional output-contract fields of a scanner definition; returns an error message or null.</summary>
    public static string? ValidateOutputContract(ScannerInput input)
    {
        if (input.OutputFormat is not null && !GenericResultParser.Formats.Contains(input.OutputFormat) && input.OutputFormat != "native")
            return "outputFormat must be one of: sarif, aegis-json, none";
        if (input.Category is not null && !GenericResultParser.Categories.Contains(input.Category))
            return "category must be one of: " + string.Join(", ", GenericResultParser.Categories);
        if (input.SuccessExitCodes is not null && (input.SuccessExitCodes.Count is 0 or > 16 || input.SuccessExitCodes.Any(c => c is < 0 or > 255)))
            return "successExitCodes must list 1-16 exit codes between 0 and 255";
        return null;
    }
    private static readonly System.Text.RegularExpressions.Regex ScannerIdPattern = new("^[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>https/ssh only; plain http is accepted solely when AEGIS_ALLOW_INSECURE_CLONE_URLS=true (local dev against e.g. the compose Gitea).</summary>
    public static bool IsAllowedCloneUrl(string cloneUrl) =>
        Uri.TryCreate(cloneUrl, UriKind.Absolute, out var uri)
        && uri.Host.Length > 0
        && (AllowedCloneUrlSchemes.Contains(uri.Scheme, StringComparer.Ordinal)
            || (uri.Scheme == "http" && Environment.GetEnvironmentVariable("AEGIS_ALLOW_INSECURE_CLONE_URLS") == "true"));

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

    private static async Task<Dictionary<string, JsonElement>> LoadSettingsAsync(AegisDbContext db, bool isAdmin)
    {
        var rows = await db.Settings.AsNoTracking().ToListAsync();
        // The webhook signing secret is write-only: it is never returned, only whether one is set.
        // Webhook URLs (Slack/Teams style) are credentials in themselves: non-admins only learn that one is configured.
        return rows.ToDictionary(row => row.Key, row => (row.Key == "webhookSecret" && row.Value != "\"\"") ? JsonElement.Parse("\"********\"")
            : (row.Key == "webhookUrl" && !isAdmin && row.Value != "\"\"" && row.Value != "null") ? JsonDocument.Parse("\"(configured)\"").RootElement
            : JsonElement.Parse(row.Value));
    }

    internal static Task RecordAuditAsync(AegisDbContext db, ClaimsPrincipal user, string action, string target, string outcome, object? metadata = null)
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

    private static RepositoryView ToView(RepositoryEntity entity, int activeFindingCount, LinkContext links) =>
        new(entity.Id, entity.Name, entity.Provider, entity.CloneUrl, entity.DefaultBranch, entity.Status, entity.CacheState)
        {
            WebUrl = links.RepoUrl(entity.Id),
            Health = entity.Status == "Blocked" ? "blocked" : "healthy",
            ActiveFindingCount = activeFindingCount,
            LatestCommitSHA = entity.LatestCommitSHA,
            Languages = entity.LanguagesJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, long>>(entity.LanguagesJson)
        };

    private static ExecutionView ToView(ExecutionEntity entity, LinkContext links) =>
        new(entity.Id, entity.RepositoryId, entity.Scanner, entity.Mode, entity.Status, entity.Attempt, entity.SanitizedLog)
        {
            CreatedAt = entity.CreatedAt,
            StartedAt = entity.StartedAt,
            FinishedAt = entity.FinishedAt,
            FindingCount = entity.FindingCount,
            OutputInterpreted = entity.OutputInterpreted,
            CommitSHA = entity.CommitSHA,
            CommitUrl = links.CommitUrl(entity.RepositoryId, entity.CommitSHA),
            RepositoryUrl = links.RepoUrl(entity.RepositoryId),
            FailureReason = entity.Status == "failed" ? FailureReasonOf(entity.OutputLog) : null,
            Links = links.RunLinks(entity)
        };

    private static FindingView ToView(FindingEntity entity, LinkContext links) =>
        new(entity.Fingerprint, entity.RepositoryId, entity.Detector, entity.Verified, entity.SafePath, entity.CommitSHA, entity.State)
        {
            Scanner = entity.Scanner,
            Category = entity.Category,
            HelpUrl = entity.HelpUrl,
            Severity = FindingSeverity.For(entity),
            Preview = entity.Preview,
            FileUrl = links.FileUrl(entity),
            CommitUrl = links.CommitUrl(entity.RepositoryId, entity.CommitSHA),
            PullRequestNumber = entity.PullRequestNumber,
            ChangelogUrl = ChangelogUrl(entity),
            Advisories = ParseAdvisories(entity.Advisories),
            SourceLine = entity.SourceLine,
            IntroducedCommit = entity.IntroducedCommit,
            IntroducedCommitUrl = links.CommitUrl(entity.RepositoryId, entity.IntroducedCommit),
            Assignee = entity.Assignee,
            DueAt = links.DueAt(entity),
            Overdue = entity.State == "open" && links.DueAt(entity) < DateTimeOffset.UtcNow,
            ResolvedAt = entity.ResolvedAt,
            PullRequestUrl = entity.PullRequestNumber is { } pr ? links.PullRequestUrl(entity.RepositoryId, pr) : null,
            Detail = entity.Detail,
            FirstSeen = entity.FirstSeenAt,
            LastSeen = entity.LastSeenAt,
            ExecutionId = entity.ExecutionId
        };

    /// <summary>Where to read what changed: GitHub sources have a releases page; other sources link to the project itself.</summary>
    private static string? ChangelogUrl(FindingEntity f) =>
        f.SourceUrl.Length == 0 || !Uri.TryCreate(f.SourceUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ? null
        : uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ? f.SourceUrl.TrimEnd('/') + "/releases" : f.SourceUrl;

    /// <summary>The first line of the stored (already sanitized) output that reports an error.</summary>
    public static string? FailureReasonOf(string? output) =>
        output?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("error", StringComparison.OrdinalIgnoreCase) || l.Contains("could not", StringComparison.OrdinalIgnoreCase));

    private static List<AdvisoryView> ParseAdvisories(string json)
    {
        if (json.Length == 0) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateArray().Select(a => new AdvisoryView(a.GetProperty("id").GetString() ?? "", a.GetProperty("summary").GetString() ?? "", a.GetProperty("url").GetString() ?? "")).ToList();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return []; }
    }

    private static FindingHistoryView ToHistory(AuditEntity entity)
    {
        string From = "", To = "", Reason = "";
        try
        {
            using var doc = JsonDocument.Parse(entity.Metadata);
            var root = doc.RootElement;
            From = root.TryGetProperty("from", out var f) ? f.GetString() ?? "" : "";
            To = root.TryGetProperty("to", out var t) ? t.GetString() ?? "" : "";
            Reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
        }
        catch (JsonException) { /* legacy row without structured metadata */ }
        return new FindingHistoryView(entity.Timestamp, entity.Actor, From, To, Reason);
    }

    private static AuditView ToView(AuditEntity entity) =>
        new(entity.Id, entity.Timestamp, entity.Actor, entity.Action, entity.Target, entity.Outcome, entity.Metadata);

    private static RoleMappingView ToView(RoleMappingEntity entity) =>
        new(entity.IdpGroup, entity.AegisRole);

    private static ScannerView ToView(ScannerEntity entity) =>
        new(entity.Id, entity.Name, entity.Image,
            JsonSerializer.Deserialize<List<string>>(entity.CommandJson) ?? [],
            JsonSerializer.Deserialize<List<string>>(entity.ArgsJson) ?? [],
            entity.BuiltIn, entity.Enabled, entity.OutputFormat, entity.Category,
            JsonSerializer.Deserialize<List<int>>(entity.SuccessExitCodesJson) ?? [0]);

    public sealed record RepositoryInput(string? Id, string? Name, string? Provider, string CloneUrl, string? DefaultBranch);
    /// <summary>Resolves repository web URLs (with the "forgeUrlOverrides" setting applied) for one response.</summary>
    private sealed class LinkContext(Dictionary<string, RepositoryEntity> repositories, Dictionary<string, string> overrides, Dictionary<string, int> sla)
    {
        public static async Task<LinkContext> CreateAsync(AegisDbContext db, IEnumerable<string> repositoryIds)
        {
            var ids = repositoryIds.Distinct().ToList();
            var repositories = await db.Repositories.AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id);
            var overrides = new Dictionary<string, string>();
            try
            {
                if (await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "forgeUrlOverrides") is { } setting
                    && JsonSerializer.Deserialize<Dictionary<string, string>>(setting.Value) is { } parsed)
                    overrides = parsed;
            }
            catch (JsonException) { /* a malformed legacy value must not break every list endpoint; links just lose the mapping */ }
            var sla = new Dictionary<string, int> { ["critical"] = 7, ["high"] = 30, ["medium"] = 90, ["low"] = 180 };
            try
            {
                if (await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "slaDays") is { } slaSetting
                    && JsonSerializer.Deserialize<Dictionary<string, int>>(slaSetting.Value) is { } configured)
                    foreach (var (severity, days) in configured) sla[severity] = days;
            }
            catch (JsonException) { /* malformed legacy value: keep the defaults */ }
            return new LinkContext(repositories, overrides, sla);
        }

        /// <summary>Remediation deadline: first seen + the SLA days of the finding's severity.</summary>
        public DateTimeOffset DueAt(FindingEntity f) => f.FirstSeenAt.AddDays(sla.GetValueOrDefault(FindingSeverity.For(f), 90));
        public bool IsOverdue(FindingEntity f) => f.State == "open" && DueAt(f) < DateTimeOffset.UtcNow;

        public string? RepoUrl(string id) => repositories.TryGetValue(id, out var r) ? ForgeLinks.RepoUrl(r.CloneUrl, overrides) : null;
        private string Provider(string id) => repositories.TryGetValue(id, out var r) ? r.Provider : "";
        public string? CommitUrl(string id, string sha) => ForgeLinks.CommitUrl(Provider(id), RepoUrl(id), sha);
        public string? PullRequestUrl(string id, int number) => ForgeLinks.PullRequestUrl(Provider(id), RepoUrl(id), number);

        public string? FileUrl(FindingEntity f)
        {
            if (f.SafePath.StartsWith("image:", StringComparison.Ordinal)) return null; // a container image finding has no file on the forge
            if (f.Scanner != "trufflehog") return f.SafePath.Length > 0 && f.CommitSHA.Length > 0 ? ForgeLinks.FileUrl(Provider(f.RepositoryId), RepoUrl(f.RepositoryId), f.CommitSHA, f.SafePath, f.SourceLine) : null;
            long? line = f.Detail.StartsWith("line ", StringComparison.Ordinal) && long.TryParse(f.Detail.AsSpan(5), out var n) ? n : null;
            // A git-history scan knows the commit that introduced the secret, and the line number is relative to that revision.
            return ForgeLinks.FileUrl(Provider(f.RepositoryId), RepoUrl(f.RepositoryId), f.IntroducedCommit.Length > 0 ? f.IntroducedCommit : f.CommitSHA, f.SafePath, line);
        }

        public List<LinkView> RunLinks(ExecutionEntity e)
        {
            if (string.IsNullOrEmpty(e.LinksJson)) return [];
            var provider = Provider(e.RepositoryId);
            var repoUrl = RepoUrl(e.RepositoryId);
            return (JsonSerializer.Deserialize<List<RunLink>>(e.LinksJson) ?? [])
                .Select(l => (Link: l, Url: l.Kind == "issue" ? ForgeLinks.IssueUrl(provider, repoUrl, l.Number) : ForgeLinks.PullRequestUrl(provider, repoUrl, l.Number)))
                .Where(x => x.Url is not null)
                .Select(x => new LinkView(x.Link.Kind, $"#{x.Link.Number} {x.Link.Title}", x.Url!)).ToList();
        }
    }

    public sealed record AssigneeInput(string? Assignee);
    public sealed record AlertAckInput(string Key, int? SnoozeHours = null);
    public sealed record FindingStateInput(string State, string? Reason = null);
    public sealed record RunInput(string RepositoryId, string? Scanner, string? Mode);
    public sealed record ScannerInput(string? Id, string Name, string Image, List<string>? Command, List<string>? Args, bool? Enabled, string? OutputFormat = null, string? Category = null, List<int>? SuccessExitCodes = null);
    public sealed record ParsePreviewInput(string Format, string? Output, string? Category);
    public sealed record RoleMappingInput(string AegisRole);
}
