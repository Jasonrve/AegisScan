package database

import (
	"context"
	"fmt"
	"sync"

	"github.com/aegisscan/aegisscan/internal/app"
	"github.com/aegisscan/aegisscan/internal/domain"
)

type CheckpointStore struct {
	mu     sync.RWMutex
	values map[string]domain.ScanCheckpoint
}

var _ app.CheckpointStore = (*CheckpointStore)(nil)

func NewCheckpointStore() *CheckpointStore {
	return &CheckpointStore{values: map[string]domain.ScanCheckpoint{}}
}
func checkpointKey(repo domain.RepositoryID, scanner domain.Scanner) string {
	return fmt.Sprintf("%s/%s", repo, scanner)
}
func (s *CheckpointStore) GetCheckpoint(_ context.Context, repo domain.RepositoryID, scanner domain.Scanner) (domain.ScanCheckpoint, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	value, ok := s.values[checkpointKey(repo, scanner)]
	if !ok {
		return domain.ScanCheckpoint{}, domain.ErrCheckpointNotFound
	}
	return value, nil
}
func (s *CheckpointStore) SaveCheckpoint(_ context.Context, checkpoint domain.ScanCheckpoint) error {
	if checkpoint.RepositoryID == "" || checkpoint.Scanner == "" || checkpoint.CommitSHA == "" {
		return fmt.Errorf("complete checkpoint is required")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.values[checkpointKey(checkpoint.RepositoryID, checkpoint.Scanner)] = checkpoint
	return nil
}
