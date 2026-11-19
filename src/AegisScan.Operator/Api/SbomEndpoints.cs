using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AegisScan.KubeOps.Data;
using AegisScan.KubeOps.Scanning;
using Microsoft.EntityFrameworkCore;

namespace AegisScan.KubeOps.Api;

/// <summary>SBOM views of a repository: its checkout and the container images linked to it (CycloneDX documents stored by the "sbom" scanner).</summary>
public static class SbomEndpoints
{
    private static readonly Dictionary<string, int> Rank = new() { ["critical"] = 3, ["high"] = 2, ["medium"] = 1, ["low"] = 0 };

    public static void MapSbomApi(this RouteGroupBuilder api)
    {
        api.MapGet("/repositories/{id}/sboms", async (string id, AegisDbContext db) =>
        {
            id = ApiEndpoints.DecodeId(id);
            if (!await db.Repositories.AsNoTracking().AnyAsync(r => r.Id == id)) return Results.NotFound();
            // Projected, so the (multi-MB) documents are not loaded for a list.
            var artifacts = await db.Sboms.AsNoTracking().Where(s => s.RepositoryId == id)
                .Select(s => new { s.Id, s.Kind, s.Ref, s.Origin, s.Source, s.CommitSHA, s.GeneratedAt, s.ComponentCount, s.Critical, s.High, s.Medium, s.Low, s.Error, HasDocument = s.Document != "" })
                .ToListAsync();
            var images = await db.RepositoryImages.AsNoTracking().Where(i => i.RepositoryId == id).Select(i => i.Ref).OrderBy(r => r).ToListAsync();
            return Results.Ok(new { images, artifacts = artifacts.OrderBy(a => a.Kind == "repository" ? 0 : 1).ThenBy(a => a.Ref, StringComparer.Ordinal) });
        }).RequireAuthorization(AegisRoles.Viewer);

        api.MapGet("/repositories/{id}/sboms/{sbomId}", async (string id, string sbomId, AegisDbContext db) =>
        {
            id = ApiEndpoints.DecodeId(id);
            var sbom = await db.Sboms.AsNoTracking().FirstOrDefaultAsync(s => s.RepositoryId == id && s.Id == sbomId);
            if (sbom is null) return Results.NotFound();
            var groups = new List<object>();
            if (sbom.Document.Length > 0)
            {
                using var doc = JsonDocument.Parse(sbom.Document);
                var bom = CycloneDx.Read(doc.RootElement);
                int Worst(IEnumerable<SbomVulnerability> v) => v.Select(x => Rank.GetValueOrDefault(x.Severity, 0) + 1).DefaultIfEmpty(0).Max();
                groups = bom.Components.GroupBy(c => c.Group).Select(g => new
                {
                    name = g.Key,
                    ecosystem = g.First().Ecosystem,
                    components = g.Select(c => new { c.Name, c.Version, c.Purl, c.License, Vulnerabilities = (bom.Vulnerabilities.GetValueOrDefault(c.Ref) ?? []).DistinctBy(v => v.Id).OrderByDescending(v => Rank.GetValueOrDefault(v.Severity, 0)).ToList() })
                        .OrderByDescending(c => Worst(c.Vulnerabilities)).ThenBy(c => c.Name, StringComparer.Ordinal).ToList()
                }).OrderByDescending(g => g.components.Sum(c => c.Vulnerabilities.Count)).ThenBy(g => g.name, StringComparer.Ordinal).Cast<object>().ToList();
            }
            return Results.Ok(new { sbom.Id, sbom.Kind, sbom.Ref, sbom.Origin, sbom.Source, sbom.CommitSHA, sbom.GeneratedAt, sbom.ComponentCount, sbom.Critical, sbom.High, sbom.Medium, sbom.Low, sbom.Error, HasDocument = sbom.Document.Length > 0, groups });
        }).RequireAuthorization(AegisRoles.Viewer);

        api.MapGet("/repositories/{id}/sboms/{sbomId}/cyclonedx", async (string id, string sbomId, AegisDbContext db) =>
        {
            id = ApiEndpoints.DecodeId(id);
            var sbom = await db.Sboms.AsNoTracking().FirstOrDefaultAsync(s => s.RepositoryId == id && s.Id == sbomId);
            if (sbom is null || sbom.Document.Length == 0) return Results.NotFound();
            var name = System.Text.RegularExpressions.Regex.Replace($"{(sbom.Kind == "image" ? sbom.Ref : id)}", "[^A-Za-z0-9._-]", "_");
            return Results.File(Encoding.UTF8.GetBytes(sbom.Document), "application/vnd.cyclonedx+json", $"sbom-{name}.cdx.json");
        }).RequireAuthorization(AegisRoles.Viewer);

        api.MapPut("/repositories/{id}/images", async (string id, ImagesInput input, AegisDbContext db, ClaimsPrincipal user) =>
        {
            id = ApiEndpoints.DecodeId(id);
            if (!await db.Repositories.AnyAsync(r => r.Id == id)) return Results.NotFound();
            var images = (input.Images ?? []).Select(i => i.Trim()).Where(i => i.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            if (images.Count > SbomScan.MaxImages) return Results.BadRequest(new { error = $"at most {SbomScan.MaxImages} images per repository" });
            if (images.FirstOrDefault(i => !SbomScan.IsValidImageRef(i)) is { } bad)
                return Results.BadRequest(new { error = $"'{bad}' is not a valid image reference (e.g. registry.example.com/team/app:1.2.3 or app@sha256:...)" });
            db.RepositoryImages.RemoveRange(db.RepositoryImages.Where(i => i.RepositoryId == id));
            db.RepositoryImages.AddRange(images.Select(i => new RepositoryImageEntity { RepositoryId = id, Ref = i }));
            await ApiEndpoints.RecordAuditAsync(db, user, "repository.images", id, "success", new { images });
            await db.SaveChangesAsync();
            return Results.Ok(new { images });
        }).RequireAuthorization(AegisRoles.Operator);
    }

    public sealed record ImagesInput(List<string>? Images);
}
