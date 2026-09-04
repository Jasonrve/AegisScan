package app

import (
	"context"
	"fmt"
	"github.com/aegisscan/aegisscan/internal/domain"
	"sync"
)

type SettingsStore struct {
	mu       sync.RWMutex
	settings domain.RuntimeSettings
}

func NewSettingsStore() *SettingsStore {
	return &SettingsStore{settings: domain.DefaultRuntimeSettings()}
}
func (s *SettingsStore) Get(context.Context) domain.RuntimeSettings {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.settings
}
func (s *SettingsStore) Update(_ context.Context, settings domain.RuntimeSettings) error {
	if err := settings.Validate(); err != nil {
		return fmt.Errorf("validate runtime settings: %w", err)
	}
	settings.Source = domain.SettingRuntime
	s.mu.Lock()
	defer s.mu.Unlock()
	s.settings = settings
	return nil
}
