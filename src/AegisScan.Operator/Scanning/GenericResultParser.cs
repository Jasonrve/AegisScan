using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AegisScan.KubeOps.Data;

namespace AegisScan.KubeOps.Scanning;

/// <summary>
/// Interprets the output of custom scanners: SARIF 2.1.0 (Trivy, Semgrep, Gitleaks, Checkov, ...) or the small "Aegis JSON" schema for wrapper scripts.
/// The JSON document is located inside the merged stdout+stderr log, raw snippets are never stored (only a masked preview), and output that cannot be
/// interpreted yields an <see cref="ScanParseResult.Error"/> so the run fails loudly instead of showing "0 findings".
/// </summary>
public static class GenericResultParser
{
    public const string FormatSarif = "sarif";
    public const string FormatAegisJson = "aegis-json";
    public const string FormatNone = "none";
    public static readonly string[] Formats = [FormatSarif, FormatAegisJson, FormatNone];
    public static readonly string[] Categories = ["secrets", "dependencies", "vulnerabilities", "code", "iac", "other"];

    internal const int MaxFindings = 5000;
    public const int MaxLogBytes = 20_000_000;
    private const string FenceBegin = "AEGIS-RESULT-BEGIN";
    private const string FenceEnd = "AEGIS-RESULT-END";
    private const string WorkspacePrefix = ScanJobFactory.WorkspacePath + "/";

    public static ScanParseResult Parse(string format, string log, string repositoryId, ScannerEntity scanner)
    {
        if (format == FormatNone)
            return new ScanParseResult([], false, $"Scanner '{scanner.Id}' finished; its output ({log.Split('\n').Length} lines) is not interpreted (output format 'none') and not stored because it may contain secrets.") { NotInterpreted = true };
        if (log.Length > MaxLogBytes) return OutputTooLarge();

        using var doc = ExtractDocument(log, format == FormatSarif ? "runs" : "aegisVersion");
        if (doc is null) return Fail($"no {(format == FormatSarif ? "SARIF" : "Aegis JSON")} document found in the scanner output (the scanner must print it to stdout; see docs/operations/custom-scanners.md)");
        try
        {
            return format == FormatSarif ? ParseSarif(doc.RootElement, repositoryId, scanner) : ParseAegisJson(doc.RootElement, repositoryId, scanner);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return Fail("the document does not follow the expected schema");
        }
    }

    /// <summary>The log reader stops at <see cref="MaxLogBytes"/>; a SARIF document cut off there cannot be trusted.</summary>
    public static ScanParseResult OutputTooLarge() => Fail("the scanner output exceeds 20 MB");

    private static ScanParseResult Fail(string reason) =>
        new([], false, "error: " + reason + "\n") { Error = "error: could not interpret scanner output: " + reason };

    /// <summary>Finds the JSON document: the whole log, else text between AEGIS-RESULT fences, else the first line-start `{` that parses and carries the marker property.</summary>
    internal static JsonDocument? ExtractDocument(string log, string marker)
    {
        if (TryParseWhole(log.Trim(), marker) is { } whole) return whole;
        var begin = log.IndexOf(FenceBegin, StringComparison.Ordinal);
        var end = log.IndexOf(FenceEnd, StringComparison.Ordinal);
        if (begin >= 0 && end > begin && TryParseWhole(log[(begin + FenceBegin.Length)..end].Trim(), marker) is { } fenced) return fenced;
        var tries = 0;
        var bytes = Encoding.UTF8.GetBytes(log);
        for (var i = 0; i < bytes.Length && tries < 200; i++)
        {
            if (bytes[i] != (byte)'{' || (i > 0 && bytes[i - 1] != (byte)'\n')) continue;
            tries++;
            try
            {
                var reader = new Utf8JsonReader(bytes.AsSpan(i));
                if (JsonDocument.TryParseValue(ref reader, out var candidate) && candidate is not null)
                {
                    if (candidate.RootElement.ValueKind == JsonValueKind.Object && candidate.RootElement.TryGetProperty(marker, out _)) return candidate;
                    candidate.Dispose();
                }
            }
            catch (JsonException) { /* not a JSON document start: keep looking */ }
        }
        return null;
    }

    private static JsonDocument? TryParseWhole(string text, string marker)
    {
        if (!text.StartsWith('{')) return null;
        try
        {
            var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(marker, out _)) return doc;
            doc.Dispose();
        }
        catch (JsonException) { /* fall through to the other strategies */ }
        return null;
    }

    private static ScanParseResult ParseSarif(JsonElement root, string repositoryId, ScannerEntity scanner)
    {
        var findings = new Dictionary<string, FindingEntity>();
        var occurrences = new Dictionary<string, int>();
        var authoritative = true;
        var skipped = 0;
        foreach (var run in root.GetProperty("runs").EnumerateArray())
        {
            if (run.TryGetProperty("invocations", out var invocations) && invocations.ValueKind == JsonValueKind.Array
                && invocations.EnumerateArray().Any(i => i.TryGetProperty("executionSuccessful", out var ok) && ok.ValueKind == JsonValueKind.False))
                authoritative = false;
            var rules = new List<JsonElement>();
            if (run.TryGetProperty("tool", out var tool) && tool.TryGetProperty("driver", out var driver) && driver.TryGetProperty("rules", out var ruleArray) && ruleArray.ValueKind == JsonValueKind.Array)
                rules.AddRange(ruleArray.EnumerateArray());
            if (!run.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) continue;

            foreach (var result in results.EnumerateArray())
            {
                if (findings.Count >= MaxFindings) { skipped++; continue; }
                var ruleId = Str(result, "ruleId");
                JsonElement? rule = null;
                if (result.TryGetProperty("ruleIndex", out var index) && index.TryGetInt32(out var i) && i >= 0 && i < rules.Count) rule = rules[i];
                else if (ruleId.Length > 0) rule = rules.Where(r => Str(r, "id") == ruleId).Cast<JsonElement?>().FirstOrDefault();
                if (ruleId.Length == 0 && rule is { } r0) ruleId = Str(r0, "id");
                if (ruleId.Length == 0) ruleId = "unknown-rule";

                var (path, line, snippet) = Location(result);
                var message = Cap(Str(result, "message", "text").Replace(WorkspacePrefix, ""), 400);
                var title = rule is { } r1 ? FirstNonEmpty(Str(r1, "shortDescription", "text"), Str(r1, "name")) : "";
                var severity = SarifSeverity(result, rule);
                // A leaked credential is never "medium": SARIF from secret scanners (gitleaks) carries no severity at all.
                if (scanner.Category == "secrets" && severity is "medium" or "low") severity = "high";
                var helpUrl = rule is { } r2 ? SafeUrl(Str(r2, "helpUri")) : "";
                var partial = result.TryGetProperty("partialFingerprints", out var pf) && pf.ValueKind == JsonValueKind.Object
                    ? string.Join('|', pf.EnumerateObject().Select(p => $"{p.Name}={p.Value.GetString() ?? ""}").Order(StringComparer.Ordinal)) : "";
                var detail = scanner.Category == "secrets" ? title // some secret scanners interpolate the matched value into the message
                    : title.Length > 0 && !message.StartsWith(title, StringComparison.Ordinal) ? Cap($"{title} — {message}", 400) : message;
                Add(findings, occurrences, scanner, repositoryId, ruleId, severity, path, line, detail, helpUrl, false, snippet, partial);
            }
        }
        return Done(findings, authoritative, skipped, "SARIF");
    }

    private static ScanParseResult ParseAegisJson(JsonElement root, string repositoryId, ScannerEntity scanner)
    {
        if (!root.TryGetProperty("aegisVersion", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1)
            return Fail("unsupported aegisVersion (expected 1)");
        var findings = new Dictionary<string, FindingEntity>();
        var occurrences = new Dictionary<string, int>();
        var skipped = 0;
        foreach (var item in root.GetProperty("findings").EnumerateArray())
        {
            if (findings.Count >= MaxFindings) { skipped++; continue; }
            var id = FirstNonEmpty(Str(item, "id"), Str(item, "title"));
            if (id.Length == 0) { skipped++; continue; }
            var line = item.TryGetProperty("line", out var l) && l.TryGetInt32(out var n) && n > 0 ? n : (int?)null;
            var detail = FirstNonEmpty(Str(item, "detail"), Str(item, "title"));
            var severity = NormalizeSeverity(Str(item, "severity"));
            var verified = item.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True;
            Add(findings, occurrences, scanner, repositoryId, id, severity, CleanPath(Str(item, "path")), line, Cap(detail, 400), SafeUrl(Str(item, "url")), verified, Str(item, "preview"), "");
        }
        var authoritative = root.TryGetProperty("authoritative", out var a) && a.ValueKind == JsonValueKind.True;
        return Done(findings, authoritative, skipped, "Aegis JSON");
    }

    internal static void Add(Dictionary<string, FindingEntity> findings, Dictionary<string, int> occurrences, ScannerEntity scanner, string repositoryId, string ruleId, string severity,
        string path, int? line, string detail, string helpUrl, bool verified, string snippet, string partialFingerprint)
    {
        // Identity = rule + path + (the tool's own fingerprints, else a hash of the matched snippet) + occurrence index. Neither the message
        // (advisory text changes over time) nor the line (code moves) is part of it, so triage survives rescans.
        var evidence = partialFingerprint.Length > 0 ? partialFingerprint : snippet.Length > 0 ? Hash(snippet) : "";
        var identity = $"{repositoryId}:{scanner.Id}:{ruleId}:{path}:{evidence}";
        var occurrence = occurrences.GetValueOrDefault(identity);
        occurrences[identity] = occurrence + 1;
        var fingerprint = Hash($"{identity}:{occurrence}");
        findings[fingerprint] = new FindingEntity
        {
            Fingerprint = fingerprint,
            RepositoryId = repositoryId,
            Scanner = scanner.Id,
            Category = scanner.Category,
            Detector = Cap(ruleId, 200),
            Severity = severity,
            Verified = verified,
            SafePath = path,
            SourceLine = line,
            Detail = detail,
            HelpUrl = helpUrl,
            // Raw snippets may be secrets (a code scanner flags hardcoded passwords): only a masked one-line preview is ever kept.
            Preview = TruffleHogResultParser.MaskPreview(snippet),
            State = "open"
        };
    }

    private static ScanParseResult Done(Dictionary<string, FindingEntity> findings, bool authoritative, int skipped, string format)
    {
        var output = new StringBuilder();
        foreach (var f in findings.Values.Take(200))
            output.AppendLine($"{f.Detector} ({f.Severity}) in {(f.SafePath.Length > 0 ? f.SafePath : "(no file)")}{(f.SourceLine is { } l ? ":" + l : "")}");
        output.AppendLine($"{findings.Count} finding(s) from {format}{(skipped > 0 ? $"; {skipped} result(s) skipped (limit or missing id)" : "")}.");
        if (!authoritative) output.AppendLine("note: the scanner did not report a complete run, so findings it no longer reports are not auto-resolved.");
        return new ScanParseResult(findings.Values.ToList(), authoritative, ScanOutputSanitizer.Sanitize(output.ToString()));
    }

    private static string SarifSeverity(JsonElement result, JsonElement? rule)
    {
        foreach (var source in new[] { (JsonElement?)result, rule })
            if (source is { } s && s.TryGetProperty("properties", out var props) && props.TryGetProperty("security-severity", out var ss)
                && double.TryParse(ss.ValueKind == JsonValueKind.Number ? ss.GetRawText() : ss.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var score))
                return score >= 9 ? "critical" : score >= 7 ? "high" : score >= 4 ? "medium" : "low";
        var level = FirstNonEmpty(Str(result, "level"), rule is { } r ? Str(r, "defaultConfiguration", "level") : "", "warning");
        return level switch { "error" => "high", "warning" => "medium", _ => "low" };
    }

    private static string NormalizeSeverity(string value) => value.ToLowerInvariant() switch
    {
        "critical" => "critical", "high" => "high", "medium" or "moderate" => "medium", _ => "low"
    };

    private static (string Path, int? Line, string Snippet) Location(JsonElement result)
    {
        if (!result.TryGetProperty("locations", out var locations) || locations.ValueKind != JsonValueKind.Array || locations.GetArrayLength() == 0) return ("", null, "");
        if (!locations[0].TryGetProperty("physicalLocation", out var physical)) return ("", null, "");
        var path = CleanPath(Str(physical, "artifactLocation", "uri"));
        int? line = null; var snippet = "";
        if (physical.TryGetProperty("region", out var region))
        {
            if (region.TryGetProperty("startLine", out var sl) && sl.TryGetInt32(out var n) && n > 0) line = n;
            snippet = Str(region, "snippet", "text");
        }
        return (path, line, snippet);
    }

    /// <summary>Repository-relative display path: no scheme, workspace prefix, leading slashes or traversal segments.</summary>
    public static string CleanPath(string uri)
    {
        var path = Uri.UnescapeDataString(uri);
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) path = path[7..];
        if (path.StartsWith(WorkspacePrefix, StringComparison.Ordinal)) path = path[WorkspacePrefix.Length..];
        path = path.Replace('\\', '/').TrimStart('/');
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        return Cap(string.Join('/', path.Split('/').Where(s => s != ".." && s != ".")), 500);
    }

    internal static string SafeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && url.Length <= 500 ? url : "";

    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string Cap(string value, int max) => value.Length <= max ? value : value[..max] + "…";
    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => v.Length > 0) ?? "";

    /// <summary>Reads a nested string property ("" when any step is missing or not a string).</summary>
    private static string Str(JsonElement element, params string[] path)
    {
        foreach (var key in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out element)) return "";
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : "";
    }
}
