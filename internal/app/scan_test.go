package app

import (
	"context"
	"github.com/aegisscan/aegisscan/internal/domain"
	"testing"
	"time"
)

type repoStoreStub struct{ repo domain.Repository }

func (s repoStoreStub) Get(context.Context, domain.RepositoryID) (domain.Repository, error) {
	return s.repo, nil
}
func (s repoStoreStub) List(context.Context) ([]domain.Repository, error) {
	return []domain.Repository{s.repo}, nil
}

type executionStoreStub struct{}

func (executionStoreStub) Create(context.Context, domain.Execution) error { return nil }
func (executionStoreStub) Update(context.Context, domain.Execution) error { return nil }

type cacheStub struct{}

func (cacheStub) Prepare(context.Context, domain.Repository, domain.ScanMode) (Workspace, error) {
	return Workspace{Path: "workspace"}, nil
}
func (cacheStub) Release(context.Context, Workspace) error { return nil }

type pluginStub struct{}

func (pluginStub) Name() domain.Scanner { return domain.ScannerTruffleHog }
func (pluginStub) Run(context.Context, Workspace, domain.ScanMode) (ScanResult, error) {
	return ScanResult{}, nil
}

type checkpointStub struct {
	checkpoint domain.ScanCheckpoint
	saved      domain.ScanCheckpoint
}

func (s *checkpointStub) GetCheckpoint(context.Context, domain.RepositoryID, domain.Scanner) (domain.ScanCheckpoint, error) {
	return s.checkpoint, nil
}
func (s *checkpointStub) SaveCheckpoint(_ context.Context, c domain.ScanCheckpoint) error {
	s.saved = c
	return nil
}

func TestScanServiceUsesAndAdvancesCheckpoint(t *testing.T) {
	checkpoints := &checkpointStub{checkpoint: domain.ScanCheckpoint{RepositoryID: "repo", Scanner: domain.ScannerTruffleHog, CommitSHA: "old"}}
	service := ScanService{Repositories: repoStoreStub{repo: domain.Repository{ID: "repo", LatestCommitSHA: "new"}}, Executions: executionStoreStub{}, Cache: cacheStub{}, Plugins: map[domain.Scanner]ScannerPlugin{domain.ScannerTruffleHog: pluginStub{}}, Checkpoints: checkpoints}
	execution, err := service.Run(context.Background(), "repo", domain.ScannerTruffleHog, domain.ScanIncremental)
	if err != nil {
		t.Fatal(err)
	}
	if execution.CommitFrom != "old" || execution.CommitTo != "new" || checkpoints.saved.CommitSHA != "new" {
		t.Fatalf("execution=%#v checkpoint=%#v", execution, checkpoints.saved)
	}
}

type updatingRepoStore struct {
	repository domain.Repository
	updated    domain.Repository
}

func (s *updatingRepoStore) Get(context.Context, domain.RepositoryID) (domain.Repository, error) {
	return s.repository, nil
}
func (s *updatingRepoStore) List(context.Context) ([]domain.Repository, error) {
	return []domain.Repository{s.repository}, nil
}
func (s *updatingRepoStore) Update(_ context.Context, repository domain.Repository) error {
	s.updated = repository
	return nil
}

func TestScanServiceSelectsAndRecordsPeriodicFullScan(t *testing.T) {
	now := time.Unix(2000, 0)
	store := &updatingRepoStore{repository: domain.Repository{ID: "repo", LatestCommitSHA: "new"}}
	checkpoints := &checkpointStub{}
	service := ScanService{Repositories: store, Executions: executionStoreStub{}, Cache: cacheStub{}, Plugins: map[domain.Scanner]ScannerPlugin{domain.ScannerTruffleHog: pluginStub{}}, Checkpoints: checkpoints, FullScanInterval: time.Hour, Now: func() time.Time { return now }}
	execution, err := service.Run(context.Background(), "repo", domain.ScannerTruffleHog, "")
	if err != nil {
		t.Fatal(err)
	}
	if execution.Mode != domain.ScanFull || store.updated.LastFullScan == nil || !store.updated.LastFullScan.Equal(now) {
		t.Fatalf("execution=%#v updated=%#v", execution, store.updated)
	}
}
