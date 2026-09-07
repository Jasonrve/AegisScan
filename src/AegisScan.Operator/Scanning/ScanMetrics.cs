using Prometheus;

namespace AegisScan.KubeOps.Scanning;

/// <summary>Custom scan-pipeline metrics, exported alongside the built-in HTTP metrics on `/metrics`.</summary>
public static class ScanMetrics
{
    public static readonly Counter ExecutionsTotal = Metrics.CreateCounter(
        "aegisscan_executions_total", "Scan executions reaching a terminal status.",
        new CounterConfiguration { LabelNames = ["status"] });

    public static readonly Counter FindingsTotal = Metrics.CreateCounter(
        "aegisscan_findings_total", "Findings detected, by detector.",
        new CounterConfiguration { LabelNames = ["detector"] });

    public static readonly Histogram JobDurationSeconds = Metrics.CreateHistogram(
        "aegisscan_job_duration_seconds", "Scan Job wall-clock duration from Kubernetes Job start to completion.");

    public static readonly Gauge QueueDepth = Metrics.CreateGauge(
        "aegisscan_queue_depth", "Executions currently queued or running.",
        new GaugeConfiguration { LabelNames = ["status"] });
}
