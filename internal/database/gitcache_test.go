package database

import (
	"context"
	"os"
	"os/exec"
	"path/filepath"
	"sync"
	"testing"

	"github.com/aegisscan/aegisscan/internal/domain"
)

func TestGitCachePrepareAndReleaseIsolatesWorkspace(t *testing.T) {
	remote := t.TempDir()
	runTestGit(t, remote, "init", "--bare")
	source := t.TempDir()
	runTestGit(t, source, "init")
	runTestGit(t, source, "config", "user.email", "test@example.com")
	runTestGit(t, source, "config", "user.name", "Aegis Test")
	if err := os.WriteFile(filepath.Join(source, "README.md"), []byte("safe fixture\n"), 0o640); err != nil {
		t.Fatal(err)
	}
	runTestGit(t, source, "add", ".")
	runTestGit(t, source, "commit", "-m", "initial")
	runTestGit(t, source, "push", remote, "HEAD:main")
	cache := NewGitCache(filepath.Join(t.TempDir(), "cache"))
	repo := domain.Repository{ID: "repo-1", CloneURL: remote, DefaultBranch: "main"}
	workspace, err := cache.Prepare(context.Background(), repo, domain.ScanFull)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(filepath.Join(workspace.Path, "README.md")); err != nil {
		t.Fatalf("workspace missing file: %v", err)
	}
	if err := os.WriteFile(filepath.Join(workspace.Path, "scanner-output.txt"), []byte("isolated"), 0o640); err != nil {
		t.Fatal(err)
	}
	if err := cache.Release(context.Background(), workspace); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(workspace.Path); !os.IsNotExist(err) {
		t.Fatalf("workspace was not cleaned: %v", err)
	}
	if err := os.WriteFile(filepath.Join(source, "CHANGELOG.md"), []byte("fetched\n"), 0o640); err != nil {
		t.Fatal(err)
	}
	runTestGit(t, source, "add", ".")
	runTestGit(t, source, "commit", "-m", "second")
	runTestGit(t, source, "push", remote, "HEAD:main")
	workspace, err = cache.Prepare(context.Background(), repo, domain.ScanIncremental)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(filepath.Join(workspace.Path, "CHANGELOG.md")); err != nil {
		t.Fatalf("incremental fetch did not update workspace: %v", err)
	}
	if err := cache.Release(context.Background(), workspace); err != nil {
		t.Fatal(err)
	}
}

func TestGitCacheRecoversCorruptCanonicalAndSerializesPreparation(t *testing.T) {
	remote := t.TempDir()
	runTestGit(t, remote, "init", "--bare")
	source := t.TempDir()
	runTestGit(t, source, "init")
	runTestGit(t, source, "config", "user.email", "test@example.com")
	runTestGit(t, source, "config", "user.name", "Aegis Test")
	if err := os.WriteFile(filepath.Join(source, "README.md"), []byte("safe\n"), 0o640); err != nil {
		t.Fatal(err)
	}
	runTestGit(t, source, "add", ".")
	runTestGit(t, source, "commit", "-m", "initial")
	runTestGit(t, source, "push", remote, "HEAD:main")
	root := filepath.Join(t.TempDir(), "cache")
	cache := NewGitCache(root)
	repo := domain.Repository{ID: "repo-concurrent", CloneURL: remote, DefaultBranch: "main"}
	if err := os.MkdirAll(filepath.Join(root, "repo-concurrent.git"), 0o750); err != nil {
		t.Fatal(err)
	}
	var wg sync.WaitGroup
	results := make(chan error, 2)
	for range 2 {
		wg.Add(1)
		go func() {
			defer wg.Done()
			workspace, err := cache.Prepare(context.Background(), repo, domain.ScanIncremental)
			if err == nil {
				err = cache.Release(context.Background(), workspace)
			}
			results <- err
		}()
	}
	wg.Wait()
	close(results)
	for err := range results {
		if err != nil {
			t.Fatal(err)
		}
	}
	if _, err := os.Stat(filepath.Join(root, "repo-concurrent.git", "HEAD")); err != nil {
		t.Fatalf("canonical cache not recovered: %v", err)
	}
}

func runTestGit(t *testing.T, dir string, args ...string) {
	t.Helper()
	cmd := exec.Command("git", args...)
	cmd.Dir = dir
	if out, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("git %v: %v: %s", args, err, out)
	}
}
