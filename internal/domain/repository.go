package domain

import "time"

type RepositoryID string

type RepositoryStatus string

const (
	RepositoryReady    RepositoryStatus = "Ready"
	RepositoryError    RepositoryStatus = "Error"
	RepositoryDisabled RepositoryStatus = "Disabled"
)

type Repository struct {
	ID                 RepositoryID     `json:"id"`
	Name               string           `json:"name"`
	Provider           string           `json:"provider"`
	CloneURL           string           `json:"cloneUrl"`
	DefaultBranch      string           `json:"defaultBranch"`
	LatestCommitSHA    string           `json:"latestCommitSha"`
	Status             RepositoryStatus `json:"status"`
	CacheState         string           `json:"cacheState"`
	LastSuccessfulScan *time.Time       `json:"lastSuccessfulScan,omitempty"`
	LastFullScan       *time.Time       `json:"lastFullScan,omitempty"`
	ActiveFindingCount int              `json:"activeFindingCount"`
}

type Scanner string

const (
	ScannerTruffleHog Scanner = "trufflehog"
	ScannerRenovate   Scanner = "renovate"
)

type ScanMode string

const (
	ScanIncremental ScanMode = "incremental"
	ScanFull        ScanMode = "full"
)

type ExecutionStatus string

const (
	ExecutionQueued    ExecutionStatus = "queued"
	ExecutionPreparing ExecutionStatus = "preparing"
	ExecutionRunning   ExecutionStatus = "running"
	ExecutionSucceeded ExecutionStatus = "succeeded"
	ExecutionFailed    ExecutionStatus = "failed"
	ExecutionCanceled  ExecutionStatus = "canceled"
	ExecutionTimedOut  ExecutionStatus = "timed_out"
	ExecutionSkipped   ExecutionStatus = "skipped"
)

type Execution struct {
	ID           string          `json:"id"`
	RepositoryID RepositoryID    `json:"repositoryId"`
	Scanner      Scanner         `json:"scanner"`
	Mode         ScanMode        `json:"mode"`
	Status       ExecutionStatus `json:"status"`
	Attempt      int             `json:"attempt"`
	CommitFrom   string          `json:"commitFrom,omitempty"`
	CommitTo     string          `json:"commitTo,omitempty"`
	StartedAt    *time.Time      `json:"startedAt,omitempty"`
	FinishedAt   *time.Time      `json:"finishedAt,omitempty"`
	ErrorCode    string          `json:"errorCode,omitempty"`
	Summary      map[string]int  `json:"summary,omitempty"`
	SanitizedLog string          `json:"sanitizedLog,omitempty"`
	JobName      string          `json:"jobName,omitempty"`
}
