package operator

import (
	"fmt"
	"github.com/aegisscan/aegisscan/internal/domain"
	"regexp"
	"strings"
)

type JobSpec struct {
	Name, Namespace, Image  string
	Repository              domain.RepositoryID
	CloneURL                string
	Scanner                 domain.Scanner
	Mode                    domain.ScanMode
	Attempt, TimeoutSeconds int
}
type JobManifest struct {
	APIVersion string   `json:"apiVersion"`
	Kind       string   `json:"kind"`
	Metadata   Metadata `json:"metadata"`
	Spec       JobBody  `json:"spec"`
}
type Metadata struct {
	Name      string            `json:"name"`
	Namespace string            `json:"namespace"`
	Labels    map[string]string `json:"labels"`
}
type JobBody struct {
	BackoffLimit            int         `json:"backoffLimit"`
	TTLSecondsAfterFinished int         `json:"ttlSecondsAfterFinished"`
	Template                PodTemplate `json:"template"`
}
type PodTemplate struct {
	Metadata Metadata `json:"metadata"`
	Spec     PodSpec  `json:"spec"`
}
type PodSpec struct {
	RestartPolicy string      `json:"restartPolicy"`
	Containers    []Container `json:"containers"`
}
type Container struct {
	Name    string   `json:"name"`
	Image   string   `json:"image"`
	Command []string `json:"command,omitempty"`
	Args    []string `json:"args,omitempty"`
}

var safeName = regexp.MustCompile(`[^a-z0-9-]+`)

func BuildJob(spec JobSpec) (JobManifest, error) {
	if spec.Repository == "" || spec.Scanner == "" || spec.Image == "" {
		return JobManifest{}, fmt.Errorf("repository, scanner, and image are required")
	}
	if spec.Attempt < 1 {
		spec.Attempt = 1
	}
	if spec.TimeoutSeconds < 1 {
		spec.TimeoutSeconds = 1800
	}
	name := spec.Name
	if name == "" {
		name = fmt.Sprintf("scan-%s-%s-%d", spec.Repository, spec.Scanner, spec.Attempt)
	}
	name = safeName.ReplaceAllString(name, "-")
	labels := map[string]string{"app.kubernetes.io/name": "aegisscan-scan", "aegisscan.io/repository": safeLabel(string(spec.Repository)), "aegisscan.io/scanner": safeLabel(string(spec.Scanner)), "aegisscan.io/mode": safeLabel(string(spec.Mode))}
	args := []string{"--repository-id=" + string(spec.Repository), "--mode=" + string(spec.Mode)}
	if spec.Scanner == domain.ScannerTruffleHog {
		args = []string{"git", spec.CloneURL, "--json"}
	}
	return JobManifest{
		APIVersion: "batch/v1", Kind: "Job",
		Metadata: Metadata{Name: name, Namespace: spec.Namespace, Labels: labels},
		Spec: JobBody{BackoffLimit: 0, TTLSecondsAfterFinished: 86400, Template: PodTemplate{
			Metadata: Metadata{Labels: labels},
			Spec:     PodSpec{RestartPolicy: "Never", Containers: []Container{{Name: "scanner", Image: spec.Image, Args: args}}},
		}},
	}, nil
}

func safeLabel(value string) string {
	value = safeName.ReplaceAllString(value, "-")
	if len(value) > 63 {
		value = value[:63]
	}
	return strings.Trim(value, "-")
}
