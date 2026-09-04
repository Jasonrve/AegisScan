package server

import (
	"context"
	"fmt"
	"sync"
	"time"

	"github.com/aegisscan/aegisscan/internal/domain"
)

// MemoryState is the default local runtime state. It gives the API a real,
// concurrency-safe state plane while durable adapters are configured.
type MemoryState struct {
	mu           sync.RWMutex
	repositories map[domain.RepositoryID]domain.Repository
	executions   []domain.Execution
	findings     map[string]domain.Finding
	audit        []domain.AuditEvent
	settings     domain.RuntimeSettings
}

func (s *MemoryState) CreateRepository(repository domain.Repository) (domain.Repository, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	repository.Name = fmt.Sprint(repository.Name)
	if repository.ID == "" {
		repository.ID = domain.RepositoryID(repository.Name)
	}
	if _, exists := s.repositories[repository.ID]; exists {
		return domain.Repository{}, fmt.Errorf("repository %q already exists", repository.ID)
	}
	s.repositories[repository.ID] = repository
	return repository, nil
}
func (s *MemoryState) DeleteRepository(id domain.RepositoryID) bool { s.mu.Lock(); defer s.mu.Unlock(); if _, ok := s.repositories[id]; !ok { return false }; delete(s.repositories, id); return true }

func NewMemoryState(repositories []domain.Repository) *MemoryState {
	state := &MemoryState{repositories: make(map[domain.RepositoryID]domain.Repository), findings: make(map[string]domain.Finding), settings: domain.DefaultRuntimeSettings()}
	for _, repository := range repositories {
		state.repositories[repository.ID] = repository
	}
	return state
}

func (s *MemoryState) ListRepositories() []domain.Repository {
	s.mu.RLock()
	defer s.mu.RUnlock()
	result := make([]domain.Repository, 0, len(s.repositories))
	for _, repository := range s.repositories {
		result = append(result, repository)
	}
	return result
}

func (s *MemoryState) StartRepositoryRun(id domain.RepositoryID, scanner domain.Scanner, mode domain.ScanMode) (domain.Execution, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, ok := s.repositories[id]; !ok {
		return domain.Execution{}, fmt.Errorf("repository %q not found", id)
	}
	if scanner == "" {
		return domain.Execution{}, fmt.Errorf("scanner is required")
	}
	if mode == "" {
		mode = s.settings.DefaultScanMode
	}
	now := time.Now()
	execution := domain.Execution{ID: fmt.Sprintf("%s-%s-%d", id, scanner, now.UnixNano()), RepositoryID: id, Scanner: scanner, Mode: mode, Status: domain.ExecutionQueued, Attempt: 1, StartedAt: &now}
	s.executions = append([]domain.Execution{execution}, s.executions...)
	return execution, nil
}

func (s *MemoryState) ListExecutions() []domain.Execution {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return append([]domain.Execution(nil), s.executions...)
}

func (s *MemoryState) GetExecution(id string) (domain.Execution, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	for _, execution := range s.executions {
		if execution.ID == id {
			return execution, true
		}
	}
	return domain.Execution{}, false
}

func (s *MemoryState) UpdateExecution(execution domain.Execution) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for i := range s.executions {
		if s.executions[i].ID == execution.ID {
			s.executions[i] = execution
			return
		}
	}
}

func (s *MemoryState) CancelRun(id string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	for index, execution := range s.executions {
		if execution.ID == id && (execution.Status == domain.ExecutionQueued || execution.Status == domain.ExecutionRunning) {
			execution.Status = domain.ExecutionCanceled
			s.executions[index] = execution
			return true
		}
	}
	return false
}

func (s *MemoryState) ListFindings() []domain.Finding {
	s.mu.RLock()
	defer s.mu.RUnlock()
	result := make([]domain.Finding, 0, len(s.findings))
	for _, finding := range s.findings {
		result = append(result, finding)
	}
	return result
}

func (s *MemoryState) GetFinding(fingerprint string) (domain.Finding, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	finding, ok := s.findings[fingerprint]
	return finding, ok
}

func (s *MemoryState) AppendAudit(event domain.AuditEvent) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.audit = append(s.audit, event)
}

func (s *MemoryState) ListAuditEvents() []domain.AuditEvent {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return append([]domain.AuditEvent(nil), s.audit...)
}

func (s *MemoryState) Save(_ context.Context, finding domain.Finding) error {
	if finding.Fingerprint == "" || finding.RepositoryID == "" {
		return fmt.Errorf("finding fingerprint and repository are required")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if previous, ok := s.findings[finding.Fingerprint]; ok {
		finding.FirstSeen = previous.FirstSeen
	}
	if finding.State == "" {
		finding.State = domain.FindingOpen
	}
	s.findings[finding.Fingerprint] = finding
	return nil
}
func (s *MemoryState) SaveFinding(ctx context.Context, finding domain.Finding) error {
	return s.Save(ctx, finding)
}

func (s *MemoryState) ReconcileFullScan(_ context.Context, repositoryID domain.RepositoryID, seen []string) error {
	seenSet := make(map[string]struct{}, len(seen))
	for _, fingerprint := range seen {
		seenSet[fingerprint] = struct{}{}
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	for fingerprint, finding := range s.findings {
		if finding.RepositoryID == repositoryID {
			if _, ok := seenSet[fingerprint]; !ok && finding.State == domain.FindingOpen {
				finding.State = domain.FindingResolved
				s.findings[fingerprint] = finding
			}
		}
	}
	return nil
}

func (s *MemoryState) Get(context.Context) domain.RuntimeSettings {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.settings
}

func (s *MemoryState) Update(_ context.Context, settings domain.RuntimeSettings) error {
	if err := settings.Validate(); err != nil {
		return err
	}
	settings.Source = domain.SettingRuntime
	s.mu.Lock()
	s.settings = settings
	s.mu.Unlock()
	return nil
}
