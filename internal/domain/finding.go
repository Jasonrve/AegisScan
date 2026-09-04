package domain

import "time"

type FindingState string

const (
	FindingOpen       FindingState = "open"
	FindingResolved   FindingState = "resolved"
	FindingSuppressed FindingState = "suppressed"
)

type Finding struct {
	Fingerprint  string       `json:"fingerprint"`
	RepositoryID RepositoryID `json:"repositoryId"`
	Detector     string       `json:"detector"`
	Verified     bool         `json:"verified"`
	SafePath     string       `json:"safePath,omitempty"`
	CommitSHA    string       `json:"commitSha,omitempty"`
	FirstSeen    time.Time    `json:"firstSeen"`
	LastSeen     time.Time    `json:"lastSeen"`
	State        FindingState `json:"state"`
}
