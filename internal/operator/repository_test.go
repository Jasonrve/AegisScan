package operator

import (
	"github.com/aegisscan/aegisscan/internal/domain"
	"testing"
)

func TestReconcileRepositoryStatus(t *testing.T) {
	ready := ReconcileRepository(RepositorySpec{Provider: "github", CloneURL: "https://example/repo.git"}, "abc", true)
	if ready.Phase != domain.RepositoryReady || ready.CacheState != "Ready" {
		t.Fatalf("ready = %#v", ready)
	}
	disabled := ReconcileRepository(RepositorySpec{Disabled: true}, "abc", true)
	if disabled.Phase != domain.RepositoryDisabled {
		t.Fatalf("disabled = %#v", disabled)
	}
	failed := ReconcileRepository(RepositorySpec{Provider: "github", CloneURL: "x"}, "", false)
	if failed.Phase != domain.RepositoryError {
		t.Fatalf("failed = %#v", failed)
	}
}
