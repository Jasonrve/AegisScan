package database

import (
	"context"
	"github.com/aegisscan/aegisscan/internal/domain"
	"sync"
)

type AuditStore struct {
	mu     sync.RWMutex
	events []domain.AuditEvent
}

func (s *AuditStore) Append(_ context.Context, event domain.AuditEvent) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.events = append(s.events, event)
	return nil
}
func (s *AuditStore) List(_ context.Context) []domain.AuditEvent {
	s.mu.RLock()
	defer s.mu.RUnlock()
	result := make([]domain.AuditEvent, len(s.events))
	copy(result, s.events)
	return result
}
