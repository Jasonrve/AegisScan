package plugins

import (
	"context"
	"github.com/aegisscan/aegisscan/internal/app"
	"github.com/aegisscan/aegisscan/internal/domain"
	"strings"
	"testing"
)

type commandCapture struct{ args []string }

func (c *commandCapture) Run(_ context.Context, _ string, args ...string) ([]byte, error) {
	c.args = args
	return []byte(`{"DetectorName":"Generic"}`), nil
}
func TestTruffleHogIncrementalRequiresRange(t *testing.T) {
	_, err := (TruffleHog{Runner: &commandCapture{}}).Run(context.Background(), app.Workspace{Path: "workspace"}, domain.ScanIncremental)
	if err == nil {
		t.Fatal("expected missing range error")
	}
}

type identityRunner struct{}

func (identityRunner) Run(context.Context, string, ...string) ([]byte, error) {
	return []byte(`{"DetectorName":"AWS","Raw":"secret","SourceMetadata":{"Data":{"Files":{"file":"config.env"}}}}`), nil
}

func TestTruffleHogUsesManagedRepositoryIdentity(t *testing.T) {
	result, err := (TruffleHog{Runner: identityRunner{}}).Run(context.Background(), app.Workspace{RepositoryID: "managed-repo", Path: "temp-workspace"}, domain.ScanFull)
	if err != nil {
		t.Fatal(err)
	}
	if len(result.Findings) != 1 || result.Findings[0].RepositoryID != "managed-repo" {
		t.Fatalf("finding repository = %#v", result.Findings)
	}
}

func TestTruffleHogUsesGitMetadataAndBranch(t *testing.T) {
	runner := &commandCapture{}
	runnerOutput := `{"DetectorName":"AWS","Verified":true,"Raw":"secret","SourceMetadata":{"Data":{"Git":{"commit":"abc123","file":"config.env","line":4}}}}`
	runner = &commandCapture{}
	result, err := (TruffleHog{Runner: runner, Output: strings.NewReader(runnerOutput)}).Run(context.Background(), app.Workspace{RepositoryID: "repo", Path: "workspace"}, domain.ScanFull)
	if err != nil || len(result.Findings) != 1 || result.Findings[0].SafePath != "config.env" || result.Findings[0].CommitSHA != "abc123" {
		t.Fatalf("result=%#v err=%v", result, err)
	}
}
