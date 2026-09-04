package operator

import (
	"github.com/aegisscan/aegisscan/internal/domain"
	"testing"
)

func TestBuildJobLabelsExecutionIdentity(t *testing.T) {
	job, err := BuildJob(JobSpec{Namespace: "aegis", Image: "scanner:local", Repository: "platform/api", Scanner: domain.ScannerTruffleHog, Mode: domain.ScanIncremental})
	if err != nil {
		t.Fatal(err)
	}
	if job.Kind != "Job" || job.Metadata.Labels["aegisscan.io/mode"] != "incremental" || job.Spec.Template.Spec.RestartPolicy != "Never" {
		t.Fatalf("manifest = %#v", job)
	}
}
