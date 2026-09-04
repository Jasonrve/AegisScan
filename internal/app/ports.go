package app

import (
	"context"

	"github.com/aegisscan/aegisscan/internal/domain"
)

type RepositoryStore interface {
	Get(ctx context.Context, id domain.RepositoryID) (domain.Repository, error)
	List(ctx context.Context) ([]domain.Repository, error)
}

type RepositoryUpdater interface {
	Update(context.Context, domain.Repository) error
}

type ExecutionStore interface {
	Create(ctx context.Context, execution domain.Execution) error
	Update(ctx context.Context, execution domain.Execution) error
}

type CheckpointStore interface {
	GetCheckpoint(context.Context, domain.RepositoryID, domain.Scanner) (domain.ScanCheckpoint, error)
	SaveCheckpoint(context.Context, domain.ScanCheckpoint) error
}
type FindingStore interface {
	SaveFinding(context.Context, domain.Finding) error
}

// FindingReconciler is optional so existing persistence adapters can adopt
// full-scan lifecycle semantics without changing the basic save contract.
type FindingReconciler interface {
	ReconcileFullScan(context.Context, domain.RepositoryID, []string) error
}

type Workspace struct {
	RepositoryID domain.RepositoryID
	Path         string
	Root         string
	Branch       string
	Commit       string
	CommitFrom   string
}

type GitCache interface {
	Prepare(ctx context.Context, repo domain.Repository, mode domain.ScanMode) (Workspace, error)
	Release(ctx context.Context, workspace Workspace) error
}

type ScannerPlugin interface {
	Name() domain.Scanner
	Run(ctx context.Context, workspace Workspace, mode domain.ScanMode) (ScanResult, error)
}

type ScanResult struct {
	Findings []domain.Finding
	Summary  map[string]int
	Log      string
}
