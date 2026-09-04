package operator

import (
	"fmt"
	"github.com/aegisscan/aegisscan/internal/domain"
)

type RepositorySpec struct {
	Provider, CloneURL, DefaultBranch string
	Disabled                          bool
}
type RepositoryStatus struct {
	Phase                                        domain.RepositoryStatus
	LatestCommitSHA, CacheState, LatestScanState string
	Message                                      string
}

func ReconcileRepository(spec RepositorySpec, observedCommit string, cacheReady bool) RepositoryStatus {
	if spec.Disabled {
		return RepositoryStatus{Phase: domain.RepositoryDisabled, Message: "repository is disabled by desired state"}
	}
	if spec.Provider == "" || spec.CloneURL == "" {
		return RepositoryStatus{Phase: domain.RepositoryError, Message: "provider and cloneURL are required"}
	}
	if !cacheReady {
		return RepositoryStatus{Phase: domain.RepositoryError, LatestCommitSHA: observedCommit, CacheState: "Error", Message: "shared Git cache is unavailable"}
	}
	if observedCommit == "" {
		return RepositoryStatus{Phase: domain.RepositoryError, CacheState: "Ready", Message: "repository has no observed commit"}
	}
	return RepositoryStatus{Phase: domain.RepositoryReady, LatestCommitSHA: observedCommit, CacheState: "Ready", LatestScanState: "Pending"}
}

func ValidateRepositorySpec(spec RepositorySpec) error {
	if spec.Provider == "" || spec.CloneURL == "" {
		return fmt.Errorf("provider and cloneURL are required")
	}
	return nil
}
