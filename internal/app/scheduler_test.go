package app

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/aegisscan/aegisscan/internal/domain"
)

type runnerStub struct {
	calls int
	fail  bool
}

type blockingRunner struct{}

func (blockingRunner) Run(ctx context.Context, _ domain.RepositoryID, _ domain.Scanner, _ domain.ScanMode) (domain.Execution, error) {
	<-ctx.Done()
	return domain.Execution{Status: domain.ExecutionCanceled}, ctx.Err()
}

func (r *runnerStub) Run(_ context.Context, _ domain.RepositoryID, _ domain.Scanner, _ domain.ScanMode) (domain.Execution, error) {
	r.calls++
	if r.fail {
		return domain.Execution{Status: domain.ExecutionFailed}, errors.New("failed")
	}
	return domain.Execution{Status: domain.ExecutionSucceeded}, nil
}

func TestSchedulerRetriesAndLimits(t *testing.T) {
	stub := &runnerStub{fail: true}
	scheduler := NewScheduler(stub, 1, 2)
	scheduler.Backoff = time.Millisecond
	ch, _, err := scheduler.Submit(context.Background(), "repo", domain.ScannerTruffleHog, domain.ScanFull)
	if err != nil {
		t.Fatal(err)
	}
	execution := <-ch
	if execution.Status != domain.ExecutionFailed || stub.calls != 3 {
		t.Fatalf("execution=%#v calls=%d", execution, stub.calls)
	}
}

func TestSchedulerRejectsDuplicateAndSweepsStale(t *testing.T) {
	stub := blockingRunner{}
	scheduler := NewScheduler(stub, 1, 0)
	scheduler.StaleAfter = time.Millisecond
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	first, id, err := scheduler.Submit(ctx, "repo", domain.ScannerTruffleHog, domain.ScanFull)
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err := scheduler.Submit(ctx, "repo", domain.ScannerTruffleHog, domain.ScanFull); err == nil {
		t.Fatal("expected duplicate rejection")
	}
	time.Sleep(2 * time.Millisecond)
	if len(scheduler.SweepStale(time.Now())) != 1 {
		t.Fatal("expected stale execution")
	}
	<-first
	_ = id
}

type timeoutRunner struct{}

func (timeoutRunner) Run(ctx context.Context, _ domain.RepositoryID, _ domain.Scanner, _ domain.ScanMode) (domain.Execution, error) {
	<-ctx.Done()
	return domain.Execution{Status: domain.ExecutionRunning}, ctx.Err()
}

func TestSchedulerMarksTimeout(t *testing.T) {
	scheduler := NewScheduler(timeoutRunner{}, 1, 0)
	scheduler.Timeout = 5 * time.Millisecond
	result, _, err := scheduler.Submit(context.Background(), "repo", domain.ScannerTruffleHog, domain.ScanFull)
	if err != nil {
		t.Fatal(err)
	}
	select {
	case execution := <-result:
		if execution.Status != domain.ExecutionTimedOut {
			t.Fatalf("status = %s", execution.Status)
		}
	case <-time.After(time.Second):
		t.Fatal("scheduler did not return timeout")
	}
}
