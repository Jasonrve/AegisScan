package server

import (
	"context"
	"fmt"
	"log"
	"os"
	"sync"

	"github.com/aegisscan/aegisscan/internal/domain"
	"github.com/aegisscan/aegisscan/internal/operator"
)

type KubernetesRuns struct {
	Jobs      *operator.KubernetesJobClient
	State     *MemoryState
	Image     string
	Namespace string
	mu        sync.RWMutex
	names     map[string]string
}

func (r *KubernetesRuns) StartJob(ctx context.Context, execution domain.Execution, repository domain.Repository) (string, error) {
	if r.Jobs == nil {
		return "", fmt.Errorf("kubernetes job client is required")
	}
	image := r.Image
	if image == "" {
		if execution.Scanner == domain.ScannerTruffleHog {
			image = os.Getenv("AEGIS_TRUFFLEHOG_IMAGE")
		} else {
			image = os.Getenv("AEGIS_RENOVATE_IMAGE")
		}
	}
	if image == "" {
		return "", fmt.Errorf("scanner image is not configured")
	}
	namespace := r.Namespace
	if namespace == "" {
		namespace = r.Jobs.Namespace
	}
	manifest, err := operator.BuildJob(operator.JobSpec{Name: "scan-" + execution.ID, Namespace: namespace, Image: image, Repository: repository.ID, CloneURL: repository.CloneURL, Scanner: execution.Scanner, Mode: execution.Mode, Attempt: execution.Attempt})
	if err != nil {
		return "", err
	}
	if err := r.Jobs.Create(ctx, manifest); err != nil {
		log.Printf("kubernetes job create failed: %v", err)
		return "", err
	}
	r.mu.Lock()
	if r.names == nil {
		r.names = map[string]string{}
	}
	r.names[execution.ID] = manifest.Metadata.Name
	r.mu.Unlock()
	return manifest.Metadata.Name, nil
}
func (r *KubernetesRuns) RefreshExecution(ctx context.Context, id string) {
	r.mu.RLock()
	name := r.names[id]
	r.mu.RUnlock()
	if name == "" || r.State == nil {
		return
	}
	status, err := r.Jobs.Status(ctx, name)
	if err != nil {
		return
	}
	logs, _ := r.Jobs.Logs(ctx, name)
	execution, ok := r.State.GetExecution(id)
	if !ok {
		return
	}
	execution.Status = domain.ExecutionStatus(status)
	execution.SanitizedLog = logs
	r.State.UpdateExecution(execution)
}
func (r *KubernetesRuns) ExecutionLogs(ctx context.Context, id string) (string, error) {
	r.RefreshExecution(ctx, id)
	r.mu.RLock()
	name := r.names[id]
	r.mu.RUnlock()
	if name == "" && r.State != nil {
		if execution, ok := r.State.GetExecution(id); ok {
			name = execution.JobName
		}
	}
	if name == "" {
		return "", fmt.Errorf("job not found")
	}
	return r.Jobs.Logs(ctx, name)
}
