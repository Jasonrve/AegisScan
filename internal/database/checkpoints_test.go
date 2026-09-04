package database

import (
	"context"
	"testing"

	"github.com/aegisscan/aegisscan/internal/domain"
)

func TestCheckpointStoreOnlyPersistsCompleteCheckpoints(t *testing.T) {
	store := NewCheckpointStore()
	if err := store.SaveCheckpoint(context.Background(), domain.ScanCheckpoint{RepositoryID: "repo"}); err == nil {
		t.Fatal("expected validation error")
	}
	expected := domain.ScanCheckpoint{RepositoryID: "repo", Scanner: domain.ScannerTruffleHog, CommitSHA: "abc"}
	if err := store.SaveCheckpoint(context.Background(), expected); err != nil {
		t.Fatal(err)
	}
	got, err := store.GetCheckpoint(context.Background(), "repo", domain.ScannerTruffleHog)
	if err != nil || got != expected {
		t.Fatalf("checkpoint = %#v, err=%v", got, err)
	}
}
