package plugins

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"

	"github.com/aegisscan/aegisscan/internal/app"
	"github.com/aegisscan/aegisscan/internal/domain"
)

type CommandRunner interface {
	Run(context.Context, string, ...string) ([]byte, error)
}
type Renovate struct{ Runner CommandRunner }
type RenovateSummary struct {
	Updates int    `json:"updates"`
	Errors  int    `json:"errors"`
	Log     string `json:"log"`
}

func (Renovate) Name() domain.Scanner { return domain.ScannerRenovate }
func (r Renovate) Run(ctx context.Context, workspace app.Workspace, _ domain.ScanMode) (app.ScanResult, error) {
	if workspace.Path == "" {
		return app.ScanResult{}, fmt.Errorf("workspace is required")
	}
	if r.Runner == nil {
		return app.ScanResult{}, fmt.Errorf("renovate command runner is required")
	}
	output, err := r.Runner.Run(ctx, workspace.Path, "renovate", "--platform=local", "--dry-run", "--log-format=json")
	if err != nil {
		return app.ScanResult{}, fmt.Errorf("run renovate: %w", err)
	}
	summary := RenovateSummary{Log: Sanitize(string(output))}
	for _, line := range strings.Split(string(output), "\n") {
		var item struct {
			Type string `json:"type"`
		}
		if json.Unmarshal([]byte(line), &item) == nil {
			if item.Type == "update" {
				summary.Updates++
			}
			if item.Type == "error" {
				summary.Errors++
			}
		}
	}
	return app.ScanResult{Summary: map[string]int{"updates": summary.Updates, "errors": summary.Errors}, Log: summary.Log}, nil
}

var _ app.ScannerPlugin = Renovate{}
