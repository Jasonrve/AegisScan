using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AegisScan.KubeOps.Data;
using Microsoft.EntityFrameworkCore;

namespace AegisScan.KubeOps.Scanning;

public sealed record SbomComponent(string Ref, string Name, string Version, string Purl, string Type, string License, string Group, string Ecosystem);
public sealed record SbomVulnerability(string Id, string Severity, string Fix, string Url, string Summary);

/// <summary>Reads the parts of a CycloneDX 1.4+ document Aegis shows: components grouped by the lock file / OS they belong to, and the vulnerabilities affecting each.</summary>
public sealed class CycloneDx
{
    private const string TrivyType = "aquasecurity:trivy:Type";
    private const string TrivyPkgType = "aquasecurity:trivy:PkgType";
    public List<SbomComponent> Components { get; } = [];
    public Dictionary<string, List<SbomVulnerability>> Vulnerabilities { get; } = [];

    public static CycloneDx Read(JsonElement root)
    {
        var all = new List<JsonElement>();
        void Collect(JsonElement list) { foreach (var c in list.EnumerateArray()) { all.Add(c); if (c.TryGetProperty("components", out var nested) && nested.ValueKind == JsonValueKind.Array) Collect(nested); } }
        if (root.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Array) Collect(components);

        // A lock file / OS component "depends on" its packages, which depend on further packages: a package belongs to the first such root that reaches it.
        var edges = new Dictionary<string, List<string>>();
        if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array)
            foreach (var d in deps.EnumerateArray())
                if (Str(d, "ref") is { Length: > 0 } r && d.TryGetProperty("dependsOn", out var on) && on.ValueKind == JsonValueKind.Array) edges[r] = on.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        var groupOf = new Dictionary<string, (string Name, string Ecosystem)>();
        foreach (var c in all.Where(c => Str(c, "type") is "application" or "operating-system" && Str(c, "bom-ref").Length > 0))
        {
            var label = Str(c, "type") == "application" ? GenericResultParser.CleanPath(Str(c, "name")) : $"{Str(c, "name")} {Str(c, "version")}".Trim() + " (OS packages)";
            var queue = new Queue<string>([Str(c, "bom-ref")]);
            while (queue.TryDequeue(out var current))
                foreach (var next in edges.GetValueOrDefault(current) ?? [])
                    if (groupOf.TryAdd(next, (label, Prop(c, TrivyType)))) queue.Enqueue(next);
        }

        var result = new CycloneDx();
        foreach (var c in all.Where(c => Str(c, "type") is not ("application" or "operating-system" or "container")))
        {
            var bomRef = Str(c, "bom-ref");
            var (group, ecosystem) = groupOf.GetValueOrDefault(bomRef, ("Other packages", Prop(c, TrivyPkgType)));
            var name = Str(c, "group") is { Length: > 0 } g ? $"{g}/{Str(c, "name")}" : Str(c, "name");
            result.Components.Add(new SbomComponent(bomRef, name, Str(c, "version"), Str(c, "purl"), Str(c, "type"), License(c), group, ecosystem));
        }

        if (root.TryGetProperty("vulnerabilities", out var vulns) && vulns.ValueKind == JsonValueKind.Array)
            foreach (var v in vulns.EnumerateArray())
            {
                var url = GenericResultParser.SafeUrl(v.TryGetProperty("advisories", out var adv) && adv.ValueKind == JsonValueKind.Array && adv.GetArrayLength() > 0 ? Str(adv[0], "url") : "");
                var vuln = new SbomVulnerability(GenericResultParser.Cap(Str(v, "id"), 200), Severity(v), GenericResultParser.Cap(Str(v, "recommendation"), 200), url, GenericResultParser.Cap(Str(v, "description"), 300));
                if (vuln.Id.Length == 0 || !v.TryGetProperty("affects", out var affects) || affects.ValueKind != JsonValueKind.Array) continue;
                foreach (var a in affects.EnumerateArray())
                    if (Str(a, "ref") is { Length: > 0 } target)
                    {
                        if (!result.Vulnerabilities.TryGetValue(target, out var list)) result.Vulnerabilities[target] = list = [];
                        list.Add(vuln);
                    }
            }
        return result;
    }

    /// <summary>The highest rating any source gives (Trivy lists NVD, the distro and others, which often disagree).</summary>
    private static string Severity(JsonElement vuln)
    {
        var best = "low";
        if (vuln.TryGetProperty("ratings", out var ratings) && ratings.ValueKind == JsonValueKind.Array)
            foreach (var r in ratings.EnumerateArray())
                if (Str(r, "severity").ToLowerInvariant() is var s && Rank(s) > Rank(best)) best = s;
        return best;
    }

    private static int Rank(string severity) => severity switch { "critical" => 3, "high" => 2, "medium" => 1, _ => 0 };

    private static string License(JsonElement component)
    {
        if (!component.TryGetProperty("licenses", out var licenses) || licenses.ValueKind != JsonValueKind.Array) return "";
        return string.Join(", ", licenses.EnumerateArray().Select(l => Str(l, "expression") is { Length: > 0 } e ? e : Str(l, "license", "id") is { Length: > 0 } id ? id : Str(l, "license", "name")).Where(l => l.Length > 0));
    }

    private static string Prop(JsonElement component, string name) =>
        component.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Array
            ? props.EnumerateArray().Where(p => Str(p, "name") == name).Select(p => Str(p, "value")).FirstOrDefault() ?? "" : "";

    private static string Str(JsonElement element, params string[] path)
    {
        foreach (var key in path)
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out element)) return "";
        return element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : "";
    }
}

/// <summary>Interprets the output of the built-in "sbom" scanner (protocol documented on <see cref="SbomScan.Script"/>): SBOMs plus one finding per vulnerable component.</summary>
public static class SbomResultParser
{
    private const int MaxDocumentBytes = 64 * 1024 * 1024;

    public static ScanParseResult Parse(string log, string repositoryId, ScannerEntity scanner)
    {
        var findings = new Dictionary<string, FindingEntity>();
        var occurrences = new Dictionary<string, int>();
        var sboms = new List<SbomEntity>();
        var output = new StringBuilder();
        string[]? header = null;
        var body = new StringBuilder();

        void Failed(string kind, string origin, string reference, string reason)
        {
            if (!Known(kind, reference)) return;
            reason = ScanOutputSanitizer.Sanitize(GenericResultParser.Cap(reason.Trim().Length > 0 ? reason.Trim() : "no reason reported", 300));
            sboms.Add(new SbomEntity { Id = Id(repositoryId, kind, reference), RepositoryId = repositoryId, Kind = kind, Ref = reference, Origin = origin == "-" ? "" : origin, Error = reason });
            output.AppendLine($"{kind} {reference}: FAILED - {reason}");
        }

        foreach (var raw in log.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("AEGIS-SBOM-BEGIN ", StringComparison.Ordinal))
            {
                if (header is not null) Failed(header[1], header[2], header[4], "output truncated (no end marker)");
                header = line.Split(' ', 5);
                body.Clear();
            }
            else if (line == "AEGIS-SBOM-END" && header is { Length: 5 })
            {
                var json = Decode(body.ToString());
                if (json is null || !TryIngest(json, header, repositoryId, scanner, findings, occurrences, sboms, output))
                    Failed(header[1], header[2], header[4], "the SBOM could not be decoded or is not a CycloneDX document");
                header = null;
            }
            else if (line.StartsWith("AEGIS-SBOM-FAILED ", StringComparison.Ordinal) && line.Split(' ', 5) is { Length: >= 4 } f)
                Failed(f[1], f[2], f[3], f.Length > 4 ? f[4] : "");
            else if (header is not null) body.Append(line);
        }
        if (header is not null) Failed(header[1], header[2], header[4], "output truncated (no end marker)");

        var good = sboms.Count(s => s.Document.Length > 0);
        if (sboms.Count == 0) return Fail("no SBOM found in the scanner output (the scan script printed nothing usable)");
        if (good == 0) return Fail("no SBOM could be produced: " + string.Join("; ", sboms.Take(3).Select(s => $"{s.Ref}: {s.Error}")));
        output.AppendLine($"{good} SBOM(s), {findings.Count} vulnerable component(s).");
        // A failed artifact is unknown, not clean: never auto-resolve earlier findings while any artifact could not be scanned.
        var complete = good == sboms.Count;
        if (!complete) output.AppendLine("note: some artifacts failed, so vulnerabilities they no longer report are not auto-resolved.");
        return new ScanParseResult(findings.Values.ToList(), complete, ScanOutputSanitizer.Sanitize(output.ToString())) { Sboms = sboms };
    }

    private static ScanParseResult Fail(string reason) =>
        new([], false, "error: " + reason + "\n") { Error = "error: could not interpret scanner output: " + reason };

    private static bool Known(string kind, string reference) => kind == "repository" ? reference == "repository" : kind == "image" && SbomScan.IsValidImageRef(reference);

    public static string Id(string repositoryId, string kind, string reference) => GenericResultParser.Hash($"{repositoryId}|{kind}|{reference}")[..32];

    private static bool TryIngest(string json, string[] header, string repositoryId, ScannerEntity scanner, Dictionary<string, FindingEntity> findings,
        Dictionary<string, int> occurrences, List<SbomEntity> sboms, StringBuilder output)
    {
        string kind = header[1], origin = header[2], source = header[3], reference = header[4];
        if (!Known(kind, reference) || sboms.Any(s => s.Id == Id(repositoryId, kind, reference))) return true; // unknown or duplicate artifact: ignore, not a failure
        CycloneDx bom;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("bomFormat", out _)) return false;
            bom = CycloneDx.Read(doc.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { return false; }

        var entity = new SbomEntity
        {
            Id = Id(repositoryId, kind, reference), RepositoryId = repositoryId, Kind = kind, Ref = reference, Origin = origin == "-" ? "" : origin,
            Source = source == "registry" ? "registry" : "generated", ComponentCount = bom.Components.Count, Document = json
        };
        foreach (var component in bom.Components)
        {
            if (!bom.Vulnerabilities.TryGetValue(component.Ref, out var vulns) || findings.Count >= GenericResultParser.MaxFindings) continue;
            // Image findings are keyed by the configured reference (not the digest), so a rebuilt image keeps its triage history.
            var path = kind == "image" ? "image:" + reference : component.Group;
            foreach (var vuln in vulns)
            {
                var detail = $"{component.Name} {component.Version}".Trim() + (vuln.Fix.Length > 0 ? " — " + vuln.Fix : "");
                GenericResultParser.Add(findings, occurrences, scanner, repositoryId, vuln.Id, vuln.Severity, path, null, detail, vuln.Url, false, "",
                    component.Purl.Length > 0 ? component.Purl : $"{component.Name}@{component.Version}");
                switch (vuln.Severity) { case "critical": entity.Critical++; break; case "high": entity.High++; break; case "medium": entity.Medium++; break; default: entity.Low++; break; }
            }
        }
        sboms.Add(entity);
        output.AppendLine($"{kind} {reference}: {entity.ComponentCount} components ({entity.Source}), {entity.Critical} critical / {entity.High} high / {entity.Medium} medium / {entity.Low} low.");
        return true;
    }

    private static string? Decode(string base64)
    {
        try
        {
            using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(base64)), CompressionMode.Decompress);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = gzip.Read(chunk, 0, chunk.Length)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxDocumentBytes) return null;
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException) { return null; }
    }
}

public static class SbomStore
{
    /// <summary>Replaces the repository's SBOMs with this run's. A failed artifact keeps its previous document (with the error); artifacts no longer linked or detected are removed. Caller saves.</summary>
    public static async Task UpsertAsync(AegisDbContext db, ExecutionEntity execution, string? commit, IReadOnlyList<SbomEntity> sboms, CancellationToken ct)
    {
        var existing = await db.Sboms.Where(s => s.RepositoryId == execution.RepositoryId).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var sbom in sboms)
        {
            var row = existing.FirstOrDefault(e => e.Id == sbom.Id);
            if (row is null) { sbom.GeneratedAt = now; sbom.CommitSHA = commit ?? ""; sbom.ExecutionId = execution.Id; db.Sboms.Add(sbom); }
            else if (sbom.Document.Length == 0) { row.Error = sbom.Error; row.Origin = sbom.Origin; }
            else
            {
                (row.Origin, row.Source, row.ComponentCount, row.Critical, row.High, row.Medium, row.Low, row.Document, row.Error) =
                    (sbom.Origin, sbom.Source, sbom.ComponentCount, sbom.Critical, sbom.High, sbom.Medium, sbom.Low, sbom.Document, "");
                (row.GeneratedAt, row.CommitSHA, row.ExecutionId) = (now, commit ?? "", execution.Id);
            }
        }
        db.Sboms.RemoveRange(existing.Where(e => sboms.All(s => s.Id != e.Id)));
    }
}
