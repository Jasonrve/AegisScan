using System.Text.Json;
using AegisScan.KubeOps.Data;
using k8s.Models;

namespace AegisScan.KubeOps.Scanning;

/// <summary>Builds the Kubernetes Job that clones a repository and runs a scanner against it.</summary>
public static class ScanJobFactory
{
    public const string WorkspaceVolumeName = "workspace";
    public const string WorkspacePath = "/workspace/repo";
    public const string ScanContainerName = "scan";

    public static V1Job Build(ExecutionEntity execution, RepositoryEntity repository, ScannerEntity scanner, string @namespace)
    {
        // Attempt-qualified so a retry never collides with the previous attempt's Job object
        // (which may not be TTL-cleaned up yet).
        var jobName = $"aegisscan-{execution.Id}-{execution.Attempt}";
        return new V1Job
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
                    ["aegisscan.io/execution-id"] = execution.Id
                }
            },
            Spec = new V1JobSpec
            {
                BackoffLimit = 0,
                TtlSecondsAfterFinished = 300,
                Template = new V1PodTemplateSpec
                {
                    Spec = new V1PodSpec
                    {
                        RestartPolicy = "Never",
                        InitContainers =
                        [
                            new V1Container
                            {
                                Name = "clone",
                                Image = "alpine/git:2.47.1",
                                Command = ["sh", "-c", "git clone --depth 50 --single-branch --branch \"$BRANCH\" \"$CLONE_URL\" \"$TARGET\""],
                                Env =
                                [
                                    new V1EnvVar { Name = "CLONE_URL", Value = repository.CloneUrl },
                                    new V1EnvVar { Name = "BRANCH", Value = repository.DefaultBranch },
                                    new V1EnvVar { Name = "TARGET", Value = WorkspacePath }
                                ],
                                VolumeMounts = [new V1VolumeMount { Name = WorkspaceVolumeName, MountPath = "/workspace" }]
                            }
                        ],
                        Containers = [BuildScanContainer(scanner)],
                        Volumes = [new V1Volume { Name = WorkspaceVolumeName, EmptyDir = new V1EmptyDirVolumeSource() }]
                    }
                }
            }
        };
    }

    private static V1Container BuildScanContainer(ScannerEntity scanner)
    {
        var command = SubstituteWorkspace(DeserializeArgs(scanner.CommandJson));
        var args = SubstituteWorkspace(DeserializeArgs(scanner.ArgsJson));
        return new V1Container
        {
            Name = ScanContainerName,
            Image = scanner.Image,
            Command = command.Count > 0 ? command : null,
            Args = args.Count > 0 ? args : null,
            // Scanners that operate on the current working directory rather than an explicit path
            // argument (e.g. Renovate's --platform=local) need this to actually see the cloned repo.
            WorkingDir = WorkspacePath,
            // Renovate has no CLI flag for JSON logging (only this env var) and RenovateResultParser
            // depends on it; harmless no-op for scanners (like TruffleHog) that ignore it.
            Env = [new V1EnvVar { Name = "LOG_FORMAT", Value = "json" }],
            VolumeMounts = [new V1VolumeMount { Name = WorkspaceVolumeName, MountPath = "/workspace" }]
        };
    }

    private static List<string> DeserializeArgs(string json) =>
        JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private static List<string> SubstituteWorkspace(List<string> values) =>
        values.Select(v => v.Replace("{{workspace}}", WorkspacePath, StringComparison.Ordinal)).ToList();
}
