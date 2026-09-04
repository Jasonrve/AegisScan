package plugins

import (
	"bufio"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"path/filepath"
	"strings"
	"time"

	"github.com/aegisscan/aegisscan/internal/app"
	"github.com/aegisscan/aegisscan/internal/domain"
)

type TruffleHog struct {
	Output io.Reader
	Runner CommandRunner
	Now    func() time.Time
}

type truffleHogResult struct {
	DetectorName   string `json:"DetectorName"`
	Verified       bool   `json:"Verified"`
	Raw            string `json:"Raw"`
	SourceMetadata struct {
		Data struct {
			Git struct {
				Commit     string `json:"commit"`
				File       string `json:"file"`
				Line       int    `json:"line"`
				Repository string `json:"repository"`
			} `json:"Git"`
			Files struct {
				File string `json:"file"`
				Line int    `json:"line"`
			} `json:"Files"`
		} `json:"Data"`
	} `json:"SourceMetadata"`
	SourceName string `json:"SourceName"`
}

func (TruffleHog) Name() domain.Scanner { return domain.ScannerTruffleHog }

func (t TruffleHog) Run(ctx context.Context, workspace app.Workspace, mode domain.ScanMode) (app.ScanResult, error) {
	if workspace.Path == "" {
		return app.ScanResult{}, fmt.Errorf("workspace is required")
	}
	if t.Output == nil && t.Runner != nil {
		args := []string{"git", "file://" + workspace.Path, "--json"}
		if mode == domain.ScanIncremental {
			if workspace.CommitFrom == "" || workspace.Commit == "" {
				return app.ScanResult{}, fmt.Errorf("incremental TruffleHog scan requires commit range")
			}
			args = append(args, "--since-commit", workspace.CommitFrom)
			if workspace.Branch != "" {
				args = append(args, "--branch", workspace.Branch)
			}
		}
		command := append([]string{"trufflehog"}, args...)
		output, err := t.Runner.Run(ctx, workspace.Path, command...)
		if err != nil {
			return app.ScanResult{}, fmt.Errorf("run trufflehog: %w", err)
		}
		t.Output = strings.NewReader(string(output))
	}
	if t.Output == nil {
		return app.ScanResult{Summary: map[string]int{}}, nil
	}
	repositoryID := workspace.RepositoryID
	if repositoryID == "" {
		repositoryID = domain.RepositoryID(filepath.Base(workspace.Path))
	}
	findings, err := ParseTruffleHog(ctx, t.Output, repositoryID, t.Now)
	if err != nil {
		return app.ScanResult{}, err
	}
	return app.ScanResult{Findings: findings, Summary: map[string]int{"findings": len(findings)}, Log: Sanitize("TruffleHog completed with " + fmt.Sprint(len(findings)) + " findings")}, nil
}

func ParseTruffleHog(ctx context.Context, input io.Reader, repo domain.RepositoryID, now func() time.Time) ([]domain.Finding, error) {
	if now == nil {
		now = time.Now
	}
	scanner := bufio.NewScanner(input)
	scanner.Buffer(make([]byte, 1024), 2*1024*1024)
	seen := map[string]bool{}
	findings := make([]domain.Finding, 0)
	for scanner.Scan() {
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		default:
		}
		var raw truffleHogResult
		if err := json.Unmarshal(scanner.Bytes(), &raw); err != nil {
			continue
		}
		if strings.TrimSpace(raw.DetectorName) == "" {
			continue
		}
		path := raw.SourceMetadata.Data.Git.File
		if path == "" {
			path = raw.SourceMetadata.Data.Files.File
		}
		path = strings.ReplaceAll(path, "\\", "/")
		path = strings.TrimPrefix(filepath.ToSlash(filepath.Clean("/"+path)), "/")
		// Fingerprints are based on stable metadata plus a one-way digest of the
		// secret, while the raw secret itself is intentionally discarded.
		digest := sha256.Sum256([]byte(raw.DetectorName + "\x00" + path + "\x00" + raw.Raw))
		fingerprint := hex.EncodeToString(digest[:])
		if seen[fingerprint] {
			continue
		}
		seen[fingerprint] = true
		stamp := now()
		commit := raw.SourceMetadata.Data.Git.Commit
		findings = append(findings, domain.Finding{Fingerprint: fingerprint, RepositoryID: repo, Detector: raw.DetectorName, Verified: raw.Verified, SafePath: path, CommitSHA: commit, FirstSeen: stamp, LastSeen: stamp, State: domain.FindingOpen})
	}
	if err := scanner.Err(); err != nil {
		return nil, fmt.Errorf("read trufflehog output: %w", err)
	}
	return findings, nil
}

var _ app.ScannerPlugin = TruffleHog{}
