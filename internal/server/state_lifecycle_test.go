package server

import (
	"context"
	"testing"

	"github.com/aegisscan/aegisscan/internal/domain"
)

func TestMemoryStateReconcilesFullScan(t *testing.T) {
	state := NewMemoryState(nil)
	for _, fingerprint := range []string{"kept", "gone"} {
		if err := state.Save(context.Background(), domain.Finding{Fingerprint: fingerprint, RepositoryID: "repo", State: domain.FindingOpen}); err != nil {
			t.Fatal(err)
		}
	}
	if err := state.ReconcileFullScan(context.Background(), "repo", []string{"kept"}); err != nil {
		t.Fatal(err)
	}
	for _, finding := range state.ListFindings() {
		if finding.Fingerprint == "gone" && finding.State != domain.FindingResolved {
			t.Fatalf("gone finding state = %s", finding.State)
		}
	}
}
