using System.Text.Json;
using AegisScan.KubeOps.Api;
using AegisScan.KubeOps.Data;
using k8s.Models;

namespace AegisScan.KubeOps.Scanning;

/// <summary>Builds the Kubernetes Job that clones a repository and runs a scanner against it.</summary>
public static class ScanJobFactory
{
    public const string WorkspaceVolumeName = "workspace";
    public const string WorkspacePath = "/workspace/repo";
    public const string ScanContainerName = "scan";
    public const string CloneContainerName = "clone";
    /// <summary>Optional Secret (key "github-token") exposed to scanners as GITHUB_COM_TOKEN so Renovate can look up GitHub-hosted dependencies.</summary>
    public const string CredentialsSecretName = "aegisscan-scanner-credentials";
    /// <summary>Secret key holding the forge token Renovate uses to open pull requests and issues.</summary>
    public const string ForgeTokenKey = "renovate-token";

    /// <summary>Renovate can open PRs/issues on these providers; other providers keep the read-only lookup mode.</summary>
    public static bool SupportsPullRequests(RepositoryEntity repository) =>
        repository.Provider.ToLowerInvariant() is "gitea" or "github" or "gitlab" && ForgeLinks.Slug(repository.CloneUrl) is not null;

    private const long NonRootId = 65534;
    private const int DeadlineSeconds = 1800;

    public static V1Job Build(ExecutionEntity execution, RepositoryEntity repository, ScannerEntity scanner, string @namespace, bool createPullRequests = false, string? forgeTokenKey = null, string? gitCachePvc = null, IReadOnlyList<string>? images = null)
    {
        // Attempt-qualified so a retry never collides with the previous attempt's Job object
        // (which may not be TTL-cleaned up yet).
        var jobName = $"aegisscan-{execution.Id}-{execution.Attempt}";
        var job = new V1Job
        {
            ApiVersion = "batch/v1",
            Kind = "Job",
            Metadata = new V1ObjectMeta
            {
                Name = jobName,
                NamespaceProperty = @namespace,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "aegisscan",
                    ["aegisscan.io/execution-id"] = execution.Id,
                    ["aegisscan.io/scanner"] = scanner.Id
                }
            },
            Spec = new V1JobSpec
            {
                BackoffLimit = 0,
                ActiveDeadlineSeconds = DeadlineSeconds,
                TtlSecondsAfterFinished = 300,
                Template = new V1PodTemplateSpec
                {
                    Spec = new V1PodSpec
                    {
                        RestartPolicy = "Never",
                        AutomountServiceAccountToken = false,
                        SecurityContext = new V1PodSecurityContext
                        {
                            RunAsNonRoot = true,
                            RunAsUser = NonRootId,
                            RunAsGroup = NonRootId,
                            FsGroup = NonRootId,
                            SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" }
                        },
                        InitContainers =
                        [
                            Harden(new V1Container
                            {
                                Name = CloneContainerName,
                                Image = "alpine/git:2.47.1",
                                // Last line of the clone log is the checked-out commit; the worker records it on findings.
                                Command = ["sh", "-c", gitCachePvc is null ? DirectCloneScript : CachedCloneScript],
                                Env =
                                [
                                    new V1EnvVar { Name = "CLONE_URL", Value = repository.CloneUrl },
                                    new V1EnvVar { Name = "BRANCH", Value = repository.DefaultBranch },
                                    new V1EnvVar { Name = "TARGET", Value = WorkspacePath },
                                    new V1EnvVar { Name = "HOME", Value = "/tmp" },
                                    new V1EnvVar { Name = "CACHE_KEY", Value = CacheKey(repository.Id) },
                                    // Incremental scans only need recent history; a full scan clones everything.
                                    new V1EnvVar { Name = "DEPTH", Value = execution.Mode == "full" ? "" : "--depth 50" }
                                ]
                            })
                        ],
                        Containers = [Harden(BuildScanContainer(execution, repository, scanner, createPullRequests && scanner.Id == "renovate" && SupportsPullRequests(repository), forgeTokenKey, images))],
                        Volumes =
                        [
                            new V1Volume { Name = WorkspaceVolumeName, EmptyDir = new V1EmptyDirVolumeSource() },
                            new V1Volume { Name = "tmp", EmptyDir = new V1EmptyDirVolumeSource() }
                        ]
                    }
                }
            }
        };
        if (gitCachePvc is not null)
        {
            job.Spec.Template.Spec.Volumes.Add(new V1Volume { Name = "gitcache", PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = gitCachePvc } });
            job.Spec.Template.Spec.InitContainers[0].VolumeMounts.Add(new V1VolumeMount { Name = "gitcache", MountPath = "/cache" });
        }
        return job;
    }

    /// <summary>Mirror directory name: readable prefix plus a hash of the id, so `a/b` and `a_b` never share a mirror.</summary>
    public static string CacheKey(string repositoryId) =>
        System.Text.RegularExpressions.Regex.Replace(repositoryId, "[^A-Za-z0-9._-]", "_") + "-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(repositoryId)))[..8];

    // Last line of the clone log is the checked-out commit; the worker records it on findings.
    private const string DirectCloneScript = "git clone $DEPTH --single-branch --branch \"$BRANCH\" \"$CLONE_URL\" \"$TARGET\" && git -C \"$TARGET\" rev-parse HEAD";

    /// <summary>
    /// Shared Git cache (ADR 0001): one bare mirror per repository on a shared PVC, refreshed under a lock so concurrent Jobs for the same repository
    /// never corrupt it, then a private shallow/full clone from the mirror. Scanners still never clone anything themselves.
    /// </summary>
    private const string CachedCloneScript =
        "set -e; M=\"/cache/$CACHE_KEY.git\"; " +
        "( flock 9; if [ -d \"$M\" ]; then git -C \"$M\" remote set-url origin \"$CLONE_URL\" && git -C \"$M\" remote update --prune; else git clone --mirror \"$CLONE_URL\" \"$M\"; fi ) 9>\"/cache/$CACHE_KEY.lock\"; " +
        "git clone $DEPTH --single-branch --branch \"$BRANCH\" \"file://$M\" \"$TARGET\" && git -C \"$TARGET\" rev-parse HEAD";

    /// <summary>Common container hardening: no privilege escalation, no capabilities, read-only root with writable workspace and /tmp, bounded resources.</summary>
    private static V1Container Harden(V1Container container)
    {
        container.SecurityContext = new V1SecurityContext
        {
            AllowPrivilegeEscalation = false,
            ReadOnlyRootFilesystem = true,
            Capabilities = new V1Capabilities { Drop = ["ALL"] }
        };
        container.Resources = new V1ResourceRequirements
        {
            Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("100m"), ["memory"] = new("256Mi") },
            Limits = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("1"), ["memory"] = new("1Gi") }
        };
        container.VolumeMounts =
        [
            new V1VolumeMount { Name = WorkspaceVolumeName, MountPath = "/workspace" },
            new V1VolumeMount { Name = "tmp", MountPath = "/tmp" }
        ];
        return container;
    }

    private static V1Container BuildScanContainer(ExecutionEntity execution, RepositoryEntity repository, ScannerEntity scanner, bool pullRequestMode, string? forgeTokenKey, IReadOnlyList<string>? images)
    {
        var command = scanner.Id == SbomScan.ScannerId ? ["sh", "-c", SbomScan.Script] : Substitute(DeserializeArgs(scanner.CommandJson), execution, repository);
        // A full TruffleHog scan inspects the whole git history (not just the checked-out files), which yields the commit that introduced each secret.
        if (execution.Mode == "full" && scanner.Id == "trufflehog" && command.Contains("filesystem"))
            command = command.Select(c => c == "filesystem" ? "git" : c == WorkspacePath ? "file://" + WorkspacePath : c).ToList();
        var args = Substitute(DeserializeArgs(scanner.ArgsJson), execution, repository);
        // PR mode talks to the forge itself (platform comes from env), so the read-only local-platform flags are dropped.
        if (pullRequestMode) args = args.Where(a => !a.StartsWith("--platform=", StringComparison.Ordinal) && !a.StartsWith("--dry-run", StringComparison.Ordinal)).ToList();
        var container = new V1Container
        {
            Name = ScanContainerName,
            Image = scanner.Image,
            Command = command.Count > 0 ? command : null,
            Args = args.Count > 0 ? args : null,
            // Scanners that operate on the current working directory rather than an explicit path
            // argument (e.g. Renovate's --platform=local) need this to actually see the cloned repo.
            WorkingDir = WorkspacePath,
            Env =
            [
                // Renovate has no CLI flag for JSON logging (only this env var) and RenovateResultParser
                // depends on it, as well as on debug level for the `packageFiles with updates` message;
                // harmless no-ops for scanners (like TruffleHog) that ignore them.
                new V1EnvVar { Name = "LOG_FORMAT", Value = "json" },
                new V1EnvVar { Name = "LOG_LEVEL", Value = "debug" },
                // The root filesystem is read-only; scanners get a writable HOME and Renovate a writable base dir.
                new V1EnvVar { Name = "HOME", Value = "/tmp" },
                new V1EnvVar { Name = "RENOVATE_BASE_DIR", Value = "/tmp/renovate" }
            ]
        };
        // The GitHub token is only for Renovate's dependency lookups; never hand it to arbitrary admin-supplied scanner images.
        if (scanner.Id == "renovate")
            container.Env.Add(new V1EnvVar
            {
                Name = "GITHUB_COM_TOKEN",
                ValueFrom = new V1EnvVarSource
                {
                    SecretKeyRef = new V1SecretKeySelector { Name = CredentialsSecretName, Key = "github-token", Optional = true }
                }
            });
        // Registry credentials only go to the built-in SBOM scanner (never to admin-supplied images); every key is optional so public images work without any.
        if (scanner.Id == SbomScan.ScannerId)
        {
            // The image list is user input read by a shell loop: passed as data in an env var (never spliced into the script) and re-validated here.
            container.Env.Add(new V1EnvVar { Name = "AEGIS_IMAGES", Value = string.Join("\n", (images ?? []).Where(SbomScan.IsValidImageRef).Take(SbomScan.MaxImages)) });
            foreach (var (key, env) in SbomScan.RegistryCredentials)
                container.Env.Add(new V1EnvVar { Name = env, ValueFrom = new V1EnvVarSource { SecretKeyRef = new V1SecretKeySelector { Name = CredentialsSecretName, Key = key, Optional = true } } });
        }
        if (pullRequestMode)
        {
            var origin = ForgeLinks.Origin(repository.CloneUrl);
            var provider = repository.Provider.ToLowerInvariant();
            container.Env.Add(new V1EnvVar { Name = "RENOVATE_PLATFORM", Value = provider });
            if (provider == "gitea") container.Env.Add(new V1EnvVar { Name = "RENOVATE_ENDPOINT", Value = $"{origin}/api/v1/" });
            if (provider == "gitlab") container.Env.Add(new V1EnvVar { Name = "RENOVATE_ENDPOINT", Value = $"{origin}/api/v4/" });
            container.Env.Add(new V1EnvVar { Name = "RENOVATE_REPOSITORIES", Value = ForgeLinks.Slug(repository.CloneUrl) });
            // Onboarding PRs are noise for repositories already under management; a missing renovate.json just means defaults.
            container.Env.Add(new V1EnvVar { Name = "RENOVATE_ONBOARDING", Value = "false" });
            container.Env.Add(new V1EnvVar { Name = "RENOVATE_REQUIRE_CONFIG", Value = "optional" });
            container.Env.Add(new V1EnvVar
            {
                Name = "RENOVATE_TOKEN",
                ValueFrom = new V1EnvVarSource { SecretKeyRef = new V1SecretKeySelector { Name = CredentialsSecretName, Key = forgeTokenKey ?? ForgeTokenKey } }
            });
        }
        return container;
    }

    private static List<string> DeserializeArgs(string json) =>
        JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private static List<string> Substitute(List<string> values, ExecutionEntity execution, RepositoryEntity repository) =>
        values.Select(v => v
            .Replace("{{workspace}}", WorkspacePath, StringComparison.Ordinal)
            .Replace("{{repository}}", repository.Id, StringComparison.Ordinal)
            .Replace("{{branch}}", repository.DefaultBranch, StringComparison.Ordinal)
            .Replace("{{mode}}", execution.Mode, StringComparison.Ordinal)).ToList();
}
