package app

import (
	"context"
	"fmt"
	"sync"
	"time"

	"github.com/aegisscan/aegisscan/internal/domain"
)

type Scheduler struct {
	Service            ScanRunner
	Concurrency        int
	Retries            int
	Backoff            time.Duration
	Timeout            time.Duration
	ScannerConcurrency map[domain.Scanner]int
	semaphore          chan struct{}
	mu                 sync.Mutex
	cancel             map[string]context.CancelFunc
	started            map[string]time.Time
	scannerSem         map[domain.Scanner]chan struct{}
	StaleAfter         time.Duration
}

type ScanRunner interface {
	Run(context.Context, domain.RepositoryID, domain.Scanner, domain.ScanMode) (domain.Execution, error)
}

func NewScheduler(service ScanRunner, concurrency, retries int) *Scheduler {
	if concurrency < 1 {
		concurrency = 1
	}
	if retries < 0 {
		retries = 0
	}
	return &Scheduler{Service: service, Concurrency: concurrency, Retries: retries, Backoff: 100 * time.Millisecond, Timeout: 30 * time.Minute, StaleAfter: 30 * time.Minute, ScannerConcurrency: map[domain.Scanner]int{}, semaphore: make(chan struct{}, concurrency), cancel: map[string]context.CancelFunc{}, started: map[string]time.Time{}, scannerSem: map[domain.Scanner]chan struct{}{}}
}

func (s *Scheduler) Submit(parent context.Context, repo domain.RepositoryID, scanner domain.Scanner, mode domain.ScanMode) (<-chan domain.Execution, string, error) {
	if s.Service == nil {
		return nil, "", fmt.Errorf("scan runner is required")
	}
	id := fmt.Sprintf("%s/%s", repo, scanner)
	ctx, cancel := context.WithCancel(parent)
	s.mu.Lock()
	if _, exists := s.cancel[id]; exists {
		cancel()
		s.mu.Unlock()
		return nil, "", fmt.Errorf("duplicate execution %s is already active", id)
	}
	s.cancel[id] = cancel
	s.started[id] = time.Now()
	s.mu.Unlock()
	result := make(chan domain.Execution, 1)
	go func() {
		defer close(result)
		defer func() { s.mu.Lock(); delete(s.cancel, id); delete(s.started, id); s.mu.Unlock() }()
		select {
		case s.semaphore <- struct{}{}:
		case <-ctx.Done():
			return
		}
		defer func() { <-s.semaphore }()
		scannerSemaphore := s.scannerSemaphore(scanner)
		select {
		case scannerSemaphore <- struct{}{}:
		case <-ctx.Done():
			return
		}
		defer func() { <-scannerSemaphore }()
		for attempt := 0; attempt <= s.Retries; attempt++ {
			runContext := ctx
			cancelRun := func() {}
			if s.Timeout > 0 {
				runContext, cancelRun = context.WithTimeout(ctx, s.Timeout)
			}
			execution, err := s.Service.Run(runContext, repo, scanner, mode)
			timedOut := runContext.Err() == context.DeadlineExceeded
			parentCanceled := ctx.Err() != nil
			cancelRun()
			if err == nil {
				result <- execution
				return
			}
			if timedOut || parentCanceled {
				if timedOut || ctx.Err() == context.DeadlineExceeded {
					execution.Status = domain.ExecutionTimedOut
				}
				result <- execution
				return
			}
			if attempt == s.Retries {
				result <- execution
				return
			}
			timer := time.NewTimer(s.Backoff * time.Duration(attempt+1))
			select {
			case <-timer.C:
			case <-ctx.Done():
				timer.Stop()
				return
			}
		}
	}()
	return result, id, nil
}

func (s *Scheduler) scannerSemaphore(scanner domain.Scanner) chan struct{} {
	s.mu.Lock()
	defer s.mu.Unlock()
	if semaphore, ok := s.scannerSem[scanner]; ok {
		return semaphore
	}
	limit := s.ScannerConcurrency[scanner]
	if limit < 1 {
		limit = s.Concurrency
	}
	semaphore := make(chan struct{}, limit)
	s.scannerSem[scanner] = semaphore
	return semaphore
}

func (s *Scheduler) Cancel(id string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	cancel, ok := s.cancel[id]
	if ok {
		cancel()
	}
	return ok
}

func (s *Scheduler) SweepStale(now time.Time) []string {
	s.mu.Lock()
	defer s.mu.Unlock()
	stale := make([]string, 0)
	for id, started := range s.started {
		if now.Sub(started) < s.StaleAfter {
			continue
		}
		if cancel, ok := s.cancel[id]; ok {
			cancel()
		}
		delete(s.started, id)
		stale = append(stale, id)
	}
	return stale
}
