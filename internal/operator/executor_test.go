package operator

import (
	"context"
	"testing"
	"time"
)

type jobClientStub struct {
	created, deleted []string
	jobs             []JobRecord
}

func (c *jobClientStub) Create(_ context.Context, job JobManifest) error {
	c.created = append(c.created, job.Metadata.Name)
	return nil
}
func (c *jobClientStub) Delete(_ context.Context, name string) error {
	c.deleted = append(c.deleted, name)
	return nil
}
func (c *jobClientStub) List(context.Context, map[string]string) ([]JobRecord, error) {
	return c.jobs, nil
}
func TestExecutorLifecycle(t *testing.T) {
	client := &jobClientStub{jobs: []JobRecord{{Name: "old", CreatedAt: time.Unix(0, 0), Active: true}, {Name: "new", CreatedAt: time.Now(), Active: true}}}
	executor := Executor{Client: client, StaleAfter: time.Hour}
	if err := executor.Start(context.Background(), JobSpec{Image: "scanner", Repository: "repo", Scanner: "trufflehog", Mode: "full"}); err != nil {
		t.Fatal(err)
	}
	if err := executor.Cancel(context.Background(), "scan"); err != nil {
		t.Fatal(err)
	}
	deleted, err := executor.CleanupStale(context.Background(), time.Unix(3601, 0), nil)
	if err != nil || len(deleted) != 1 || client.deleted[1] != "old" {
		t.Fatalf("deleted=%v err=%v", deleted, err)
	}
}
