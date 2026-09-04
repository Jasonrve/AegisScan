package operator

import (
	"context"
	"fmt"
	"time"
)

type JobRecord struct {
	Name      string
	CreatedAt time.Time
	Active    bool
}
type JobClient interface {
	Create(context.Context, JobManifest) error
	Delete(context.Context, string) error
	List(context.Context, map[string]string) ([]JobRecord, error)
}
type Executor struct {
	Client     JobClient
	StaleAfter time.Duration
}

func (e Executor) Start(ctx context.Context, spec JobSpec) error {
	if e.Client == nil {
		return fmt.Errorf("job client is required")
	}
	job, err := BuildJob(spec)
	if err != nil {
		return err
	}
	return e.Client.Create(ctx, job)
}
func (e Executor) Cancel(ctx context.Context, name string) error {
	if e.Client == nil {
		return fmt.Errorf("job client is required")
	}
	if name == "" {
		return fmt.Errorf("job name is required")
	}
	return e.Client.Delete(ctx, name)
}
func (e Executor) CleanupStale(ctx context.Context, now time.Time, labels map[string]string) ([]string, error) {
	if e.Client == nil {
		return nil, fmt.Errorf("job client is required")
	}
	if e.StaleAfter <= 0 {
		e.StaleAfter = 30 * time.Minute
	}
	jobs, err := e.Client.List(ctx, labels)
	if err != nil {
		return nil, err
	}
	deleted := make([]string, 0)
	for _, job := range jobs {
		if job.Active && now.Sub(job.CreatedAt) >= e.StaleAfter {
			if err := e.Client.Delete(ctx, job.Name); err != nil {
				return deleted, err
			}
			deleted = append(deleted, job.Name)
		}
	}
	return deleted, nil
}
