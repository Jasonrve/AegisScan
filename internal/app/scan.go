package app

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/aegisscan/aegisscan/internal/domain"
)

type ScanService struct {
	Repositories     RepositoryStore
	Executions       ExecutionStore
	Cache            GitCache
	Plugins          map[domain.Scanner]ScannerPlugin
	Checkpoints      CheckpointStore
	Findings         FindingStore
	FullScanInterval time.Duration
	Now              func() time.Time
}

func (s ScanService) Run(ctx context.Context, repoID domain.RepositoryID, scanner domain.Scanner, mode domain.ScanMode) (domain.Execution, error) {
	repo, err := s.Repositories.Get(ctx, repoID)
	if err != nil {
		return domain.Execution{}, fmt.Errorf("load repository %s: %w", repoID, err)
	}
	plugin, ok := s.Plugins[scanner]
	if !ok {
		return domain.Execution{}, fmt.Errorf("scanner %q is not configured", scanner)
	}
	now := time.Now
	if s.Now != nil {
		now = s.Now
	}
	mode = SelectScanMode(mode, repo.LastFullScan, now(), s.FullScanInterval)
	started := now()
	execution := domain.Execution{ID: fmt.Sprintf("%s-%s-%d", repoID, scanner, started.UnixNano()), RepositoryID: repoID, Scanner: scanner, Mode: mode, Status: domain.ExecutionPreparing, Attempt: 1, StartedAt: &started}
	if repo.LatestCommitSHA == "" {
		return execution, fmt.Errorf("repository %s has no observed commit", repoID)
	}
	if s.Checkpoints != nil {
		checkpoint, checkpointErr := s.Checkpoints.GetCheckpoint(ctx, repoID, scanner)
		if errors.Is(checkpointErr, domain.ErrCheckpointNotFound) && mode == domain.ScanIncremental {
			return execution, fmt.Errorf("load scan checkpoint: %w", checkpointErr)
		}
		if checkpointErr != nil && !errors.Is(checkpointErr, domain.ErrCheckpointNotFound) {
			return execution, fmt.Errorf("load scan checkpoint: %w", checkpointErr)
		}
		from, to, rangeErr := domain.CommitRange(checkpoint, repo.LatestCommitSHA, mode)
		if rangeErr != nil {
			return execution, rangeErr
		}
		execution.CommitFrom, execution.CommitTo = from, to
	} else if mode == domain.ScanIncremental {
		return execution, fmt.Errorf("incremental scans require checkpoint storage")
	}
	if err := s.Executions.Create(ctx, execution); err != nil {
		return domain.Execution{}, fmt.Errorf("create execution: %w", err)
	}
	workspace, err := s.Cache.Prepare(ctx, repo, mode)
	if err != nil {
		execution.Status = domain.ExecutionFailed
		execution.ErrorCode = "repository_prepare_failed"
		_ = s.Executions.Update(ctx, execution)
		return execution, fmt.Errorf("prepare workspace: %w", err)
	}
	defer s.Cache.Release(ctx, workspace)
	workspace.RepositoryID = repoID
	workspace.Branch = repo.DefaultBranch
	workspace.Commit = execution.CommitTo
	workspace.CommitFrom = execution.CommitFrom
	execution.Status = domain.ExecutionRunning
	if err := s.Executions.Update(ctx, execution); err != nil {
		return execution, fmt.Errorf("mark execution running: %w", err)
	}
	result, runErr := plugin.Run(ctx, workspace, mode)
	if runErr != nil {
		execution.Status = domain.ExecutionFailed
		execution.ErrorCode = "scanner_failed"
	} else {
		execution.Status = domain.ExecutionSucceeded
		execution.Summary = result.Summary
		execution.SanitizedLog = result.Log
		if s.Findings != nil {
			seen := make([]string, 0, len(result.Findings))
			for _, finding := range result.Findings {
				if err := s.Findings.SaveFinding(ctx, finding); err != nil {
					return execution, fmt.Errorf("persist finding: %w", err)
				}
				seen = append(seen, finding.Fingerprint)
			}
			if mode == domain.ScanFull {
				if reconciler, ok := s.Findings.(FindingReconciler); ok {
					if err := reconciler.ReconcileFullScan(ctx, repoID, seen); err != nil {
						return execution, fmt.Errorf("reconcile findings: %w", err)
					}
				}
			}
		}
		if s.Checkpoints != nil {
			if err := s.Checkpoints.SaveCheckpoint(ctx, domain.ScanCheckpoint{RepositoryID: repoID, Scanner: scanner, CommitSHA: repo.LatestCommitSHA}); err != nil {
				return execution, fmt.Errorf("persist scan checkpoint: %w", err)
			}
		}
		if mode == domain.ScanFull {
			repo.LastFullScan = ptrTime(now())
			if updater, ok := s.Repositories.(RepositoryUpdater); ok {
				if err := updater.Update(ctx, repo); err != nil {
					return execution, fmt.Errorf("persist full-scan timestamp: %w", err)
				}
			}
		}
	}
	finished := now()
	execution.FinishedAt = &finished
	if err := s.Executions.Update(ctx, execution); err != nil {
		return execution, fmt.Errorf("persist execution result: %w", err)
	}
	return execution, nil
}

func ptrTime(value time.Time) *time.Time { return &value }
