package domain

import (
	"errors"
	"fmt"
)

var ErrCheckpointNotFound = errors.New("scan checkpoint not found")

type ScanCheckpoint struct {
	RepositoryID RepositoryID
	Scanner      Scanner
	CommitSHA    string
}

// CommitRange returns the exclusive previous checkpoint and inclusive target.
// An empty previous checkpoint means the caller must perform a full scan.
func CommitRange(checkpoint ScanCheckpoint, target string, mode ScanMode) (from, to string, err error) {
	if target == "" {
		return "", "", fmt.Errorf("target commit is required")
	}
	if mode == ScanFull {
		return "", target, nil
	}
	if mode != ScanIncremental {
		return "", "", fmt.Errorf("unsupported scan mode %q", mode)
	}
	if checkpoint.CommitSHA == "" {
		return "", "", fmt.Errorf("incremental scan requires a successful checkpoint")
	}
	if checkpoint.CommitSHA == target {
		return target, target, nil
	}
	return checkpoint.CommitSHA, target, nil
}
