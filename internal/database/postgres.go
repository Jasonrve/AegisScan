package database

import (
	"context"
	"database/sql"
	"encoding/json"
	"fmt"
	"github.com/aegisscan/aegisscan/internal/app"
	"github.com/aegisscan/aegisscan/internal/domain"
)

type Postgres struct{ DB *sql.DB }

func (p Postgres) CreateRepository(ctx context.Context, repository domain.Repository) error {
	if p.DB == nil {
		return fmt.Errorf("postgres database is required")
	}
	_, err := p.DB.ExecContext(ctx, `INSERT INTO repositories (id, name, provider, clone_url, default_branch, latest_commit_sha, status, cache_state, last_successful_scan, last_full_scan, active_finding_count) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11) ON CONFLICT (id) DO UPDATE SET name=$2, provider=$3, clone_url=$4, default_branch=$5, latest_commit_sha=$6, status=$7, cache_state=$8, last_successful_scan=$9, last_full_scan=$10, active_finding_count=$11, updated_at=now()`, repository.ID, repository.Name, repository.Provider, repository.CloneURL, repository.DefaultBranch, repository.LatestCommitSHA, repository.Status, repository.CacheState, repository.LastSuccessfulScan, repository.LastFullScan, repository.ActiveFindingCount)
	return err
}

func (p Postgres) ListRepositories(ctx context.Context) ([]domain.Repository, error) {
	if p.DB == nil {
		return nil, fmt.Errorf("postgres database is required")
	}
	rows, err := p.DB.QueryContext(ctx, `SELECT id, name, provider, clone_url, default_branch, COALESCE(latest_commit_sha,''), status, COALESCE(cache_state,''), last_successful_scan, last_full_scan, active_finding_count FROM repositories ORDER BY name`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := []domain.Repository{}
	for rows.Next() {
		var repository domain.Repository
		if err := rows.Scan(&repository.ID, &repository.Name, &repository.Provider, &repository.CloneURL, &repository.DefaultBranch, &repository.LatestCommitSHA, &repository.Status, &repository.CacheState, &repository.LastSuccessfulScan, &repository.LastFullScan, &repository.ActiveFindingCount); err != nil {
			return nil, err
		}
		result = append(result, repository)
	}
	return result, rows.Err()
}

func (p Postgres) ListExecutions(ctx context.Context) ([]domain.Execution, error) {
	if p.DB == nil {
		return nil, fmt.Errorf("postgres database is required")
	}
	rows, err := p.DB.QueryContext(ctx, `SELECT id, repository_id, scanner, scan_mode, status, attempt, COALESCE(commit_from,''), COALESCE(commit_to,''), started_at, finished_at, COALESCE(normalized_error,''), scanner_summary, COALESCE(sanitized_log,'') FROM scan_executions ORDER BY created_at DESC LIMIT 1000`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := []domain.Execution{}
	for rows.Next() {
		var execution domain.Execution
		var summary []byte
		if err := rows.Scan(&execution.ID, &execution.RepositoryID, &execution.Scanner, &execution.Mode, &execution.Status, &execution.Attempt, &execution.CommitFrom, &execution.CommitTo, &execution.StartedAt, &execution.FinishedAt, &execution.ErrorCode, &summary, &execution.SanitizedLog); err != nil {
			return nil, err
		}
		if len(summary) > 0 && string(summary) != "null" {
			if err := json.Unmarshal(summary, &execution.Summary); err != nil {
				return nil, err
			}
		}
		result = append(result, execution)
	}
	return result, rows.Err()
}

func (p Postgres) ListFindings(ctx context.Context) ([]domain.Finding, error) {
	if p.DB == nil {
		return nil, fmt.Errorf("postgres database is required")
	}
	rows, err := p.DB.QueryContext(ctx, `SELECT fingerprint, repository_id, detector, verified, COALESCE(safe_path,''), COALESCE(commit_sha,''), first_seen, last_seen, state FROM findings ORDER BY last_seen DESC LIMIT 1000`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := []domain.Finding{}
	for rows.Next() {
		var finding domain.Finding
		if err := rows.Scan(&finding.Fingerprint, &finding.RepositoryID, &finding.Detector, &finding.Verified, &finding.SafePath, &finding.CommitSHA, &finding.FirstSeen, &finding.LastSeen, &finding.State); err != nil {
			return nil, err
		}
		result = append(result, finding)
	}
	return result, rows.Err()
}

func (p Postgres) CreateExecution(ctx context.Context, execution domain.Execution) error {
	if p.DB == nil {
		return fmt.Errorf("postgres database is required")
	}
	summary, _ := json.Marshal(execution.Summary)
	_, err := p.DB.ExecContext(ctx, `INSERT INTO scan_executions (id, repository_id, scanner, scan_mode, status, attempt, commit_from, commit_to, started_at, finished_at, normalized_error, scanner_summary, sanitized_log) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)`, execution.ID, execution.RepositoryID, execution.Scanner, execution.Mode, execution.Status, execution.Attempt, execution.CommitFrom, execution.CommitTo, execution.StartedAt, execution.FinishedAt, execution.ErrorCode, summary, execution.SanitizedLog)
	return err
}
func (p Postgres) UpdateExecution(ctx context.Context, execution domain.Execution) error {
	if p.DB == nil {
		return fmt.Errorf("postgres database is required")
	}
	summary, _ := json.Marshal(execution.Summary)
	_, err := p.DB.ExecContext(ctx, `UPDATE scan_executions SET status=$2, attempt=$3, commit_from=$4, commit_to=$5, started_at=$6, finished_at=$7, normalized_error=$8, scanner_summary=$9, sanitized_log=$10 WHERE id=$1`, execution.ID, execution.Status, execution.Attempt, execution.CommitFrom, execution.CommitTo, execution.StartedAt, execution.FinishedAt, execution.ErrorCode, summary, execution.SanitizedLog)
	return err
}
func (p Postgres) SaveFinding(ctx context.Context, finding domain.Finding) error {
	if p.DB == nil {
		return fmt.Errorf("postgres database is required")
	}
	_, err := p.DB.ExecContext(ctx, `INSERT INTO findings (fingerprint, repository_id, detector, verified, safe_path, commit_sha, first_seen, last_seen, state) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9) ON CONFLICT (fingerprint) DO UPDATE SET verified=$4, last_seen=$8, state=$9`, finding.Fingerprint, finding.RepositoryID, finding.Detector, finding.Verified, finding.SafePath, finding.CommitSHA, finding.FirstSeen, finding.LastSeen, finding.State)
	return err
}
func (p Postgres) SaveCheckpoint(ctx context.Context, checkpoint domain.ScanCheckpoint) error {
	if p.DB == nil {
		return fmt.Errorf("postgres database is required")
	}
	_, err := p.DB.ExecContext(ctx, `INSERT INTO scan_checkpoints (repository_id, scanner, commit_sha) VALUES ($1,$2,$3) ON CONFLICT (repository_id, scanner) DO UPDATE SET commit_sha=$3, updated_at=now()`, checkpoint.RepositoryID, checkpoint.Scanner, checkpoint.CommitSHA)
	return err
}
func (p Postgres) GetCheckpoint(ctx context.Context, repo domain.RepositoryID, scanner domain.Scanner) (domain.ScanCheckpoint, error) {
	if p.DB == nil {
		return domain.ScanCheckpoint{}, fmt.Errorf("postgres database is required")
	}
	var result domain.ScanCheckpoint
	err := p.DB.QueryRowContext(ctx, `SELECT repository_id, scanner, commit_sha FROM scan_checkpoints WHERE repository_id=$1 AND scanner=$2`, repo, scanner).Scan(&result.RepositoryID, &result.Scanner, &result.CommitSHA)
	if err != nil {
		if err == sql.ErrNoRows {
			return result, domain.ErrCheckpointNotFound
		}
		return result, err
	}
	return result, nil
}
func (p Postgres) Create(ctx context.Context, execution domain.Execution) error {
	return p.CreateExecution(ctx, execution)
}
func (p Postgres) Update(ctx context.Context, execution domain.Execution) error {
	return p.UpdateExecution(ctx, execution)
}
func (p Postgres) AppendAudit(ctx context.Context, event domain.AuditEvent) error {
	if p.DB == nil {
		return fmt.Errorf("postgres database is required")
	}
	_, err := p.DB.ExecContext(ctx, `INSERT INTO audit_events (actor, role, action, target, outcome, metadata, created_at) VALUES ($1,$2,$3,$4,$5,$6,COALESCE($7, now()))`, event.Actor, event.Role, event.Action, event.Target, event.Outcome, event.Metadata, event.Timestamp)
	return err
}

func (p Postgres) ListAuditEvents(ctx context.Context) ([]domain.AuditEvent, error) {
	if p.DB == nil {
		return nil, fmt.Errorf("postgres database is required")
	}
	rows, err := p.DB.QueryContext(ctx, `SELECT created_at, actor, role, action, target, outcome, metadata FROM audit_events ORDER BY created_at DESC LIMIT 1000`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := []domain.AuditEvent{}
	for rows.Next() {
		var event domain.AuditEvent
		var metadata []byte
		if err := rows.Scan(&event.Timestamp, &event.Actor, &event.Role, &event.Action, &event.Target, &event.Outcome, &metadata); err != nil {
			return nil, err
		}
		if len(metadata) > 0 {
			_ = json.Unmarshal(metadata, &event.Metadata)
		}
		result = append(result, event)
	}
	return result, rows.Err()
}

var _ app.ExecutionStore = Postgres{}
var _ app.FindingStore = Postgres{}
var _ app.CheckpointStore = Postgres{}
