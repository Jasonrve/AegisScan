package server

import (
	"context"
	"testing"

	"github.com/aegisscan/aegisscan/internal/domain"
)

func TestMemoryStatePersistsRunAndSettings(t *testing.T) {
	state := NewMemoryState([]domain.Repository{{ID: "repo-1", Name: "platform/api"}})
	execution, err := state.StartRepositoryRun("repo-1", domain.ScannerTruffleHog, "")
	if err != nil || execution.Status != domain.ExecutionQueued || execution.Mode != domain.ScanIncremental {
		t.Fatalf("execution = %#v, err = %v", execution, err)
	}
	if len(state.ListExecutions()) != 1 {
		t.Fatal("queued execution was not retained")
	}
	if got, ok := state.GetExecution(execution.ID); !ok || got.ID != execution.ID {
		t.Fatal("execution detail was not retained")
	}
	if !state.CancelRun(execution.ID) {
		t.Fatal("queued execution was not cancellable")
	}
	if got, _ := state.GetExecution(execution.ID); got.Status != domain.ExecutionCanceled {
		t.Fatalf("execution status = %s", got.Status)
	}
	settings := domain.DefaultRuntimeSettings()
	settings.MaxConcurrency = 8
	if err := state.Update(context.Background(), settings); err != nil {
		t.Fatal(err)
	}
	if got := state.Get(context.Background()).MaxConcurrency; got != 8 {
		t.Fatalf("max concurrency = %d", got)
	}
}
