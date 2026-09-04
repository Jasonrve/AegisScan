package plugins

import (
	"context"
	"io"
	"strings"
	"testing"

	"github.com/aegisscan/aegisscan/internal/app"
)

type ReaderRunner struct{ Reader io.Reader }

func (r ReaderRunner) Run(context.Context, string, ...string) ([]byte, error) {
	return io.ReadAll(r.Reader)
}

func TestRenovateCapturesSummaryWithoutSecretOutput(t *testing.T) {
	output := "{\"type\":\"update\"}\n{\"type\":\"error\"}\npassword=do-not-store"
	result, err := (Renovate{Runner: ReaderRunner{Reader: strings.NewReader(output)}}).Run(context.Background(), app.Workspace{Path: t.TempDir()}, "")
	if err != nil {
		t.Fatal(err)
	}
	if result.Summary["updates"] != 1 || result.Summary["errors"] != 1 {
		t.Fatalf("summary = %#v", result.Summary)
	}
	if strings.Contains(Sanitize(output), "do-not-store") {
		t.Fatal("secret was not sanitized")
	}
}
