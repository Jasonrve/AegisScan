package database

import (
	"context"
	"fmt"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"

	"github.com/aegisscan/aegisscan/internal/app"
	"github.com/aegisscan/aegisscan/internal/domain"
)

type GitCache struct {
	Root  string
	mu    sync.Mutex
	locks map[domain.RepositoryID]*sync.Mutex
}

func NewGitCache(root string) *GitCache {
	return &GitCache{Root: root, locks: make(map[domain.RepositoryID]*sync.Mutex)}
}

func (c *GitCache) repoLock(id domain.RepositoryID) *sync.Mutex {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.locks[id] == nil {
		c.locks[id] = &sync.Mutex{}
	}
	return c.locks[id]
}

func (c *GitCache) Prepare(ctx context.Context, repo domain.Repository, _ domain.ScanMode) (app.Workspace, error) {
	if repo.ID == "" || strings.TrimSpace(repo.CloneURL) == "" {
		return app.Workspace{}, fmt.Errorf("repository identity and clone URL are required")
	}
	if err := validCloneURL(repo.CloneURL); err != nil {
		return app.Workspace{}, err
	}
	lock := c.repoLock(repo.ID)
	lock.Lock()
	defer lock.Unlock()
	canonical := filepath.Join(c.Root, string(repo.ID)+".git")
	if _, err := os.Stat(filepath.Join(canonical, "HEAD")); os.IsNotExist(err) {
		if err := os.MkdirAll(c.Root, 0o750); err != nil {
			return app.Workspace{}, fmt.Errorf("create cache root: %w", err)
		}
		if _, statErr := os.Stat(canonical); statErr == nil {
			if err := os.RemoveAll(canonical); err != nil {
				return app.Workspace{}, fmt.Errorf("recover corrupt cache: %w", err)
			}
		}
		if err := run(ctx, c.Root, "git", "clone", "--mirror", "--", repo.CloneURL, canonical); err != nil {
			return app.Workspace{}, fmt.Errorf("clone repository: %w", err)
		}
	} else if err != nil {
		return app.Workspace{}, fmt.Errorf("inspect canonical cache: %w", err)
	} else if err := run(ctx, canonical, "git", "remote", "update", "--prune"); err != nil {
		return app.Workspace{}, fmt.Errorf("fetch repository: %w", err)
	}
	if err := os.MkdirAll(c.Root, 0o750); err != nil {
		return app.Workspace{}, fmt.Errorf("create cache root: %w", err)
	}
	workspace, err := os.MkdirTemp(c.Root, "aegisscan-workspace-")
	if err != nil {
		return app.Workspace{}, fmt.Errorf("create isolated workspace: %w", err)
	}
	branch := repo.DefaultBranch
	if branch == "" {
		branch = "main"
	}
	if err := run(ctx, workspace, "git", "clone", "--shared", "--branch", branch, "--", canonical, filepath.Join(workspace, "repo")); err != nil {
		_ = os.RemoveAll(workspace)
		return app.Workspace{}, fmt.Errorf("materialize workspace: %w", err)
	}
	return app.Workspace{Path: filepath.Join(workspace, "repo"), Root: workspace, Branch: branch, RepositoryID: repo.ID}, nil
}

func (c *GitCache) Release(_ context.Context, workspace app.Workspace) error {
	if workspace.Path == "" {
		return nil
	}
	root := workspace.Root
	if root == "" {
		root = filepath.Dir(workspace.Path)
	}
	cleanRoot, cleanBase := filepath.Clean(root), filepath.Clean(c.Root)
	if filepath.Dir(cleanRoot) != cleanBase || !strings.HasPrefix(filepath.Base(cleanRoot), "aegisscan-workspace-") {
		return fmt.Errorf("refusing to remove unmanaged workspace %q", root)
	}
	return os.RemoveAll(cleanRoot)
}

func validCloneURL(raw string) error {
	u, err := url.Parse(raw)
	if err != nil {
		return fmt.Errorf("parse clone URL: %w", err)
	}
	if u.Scheme == "" && filepath.IsAbs(raw) || len(raw) > 2 && raw[1] == ':' {
		return nil
	}
	if u.Scheme == "file" && u.Host == "" {
		return nil
	}
	if (u.Scheme != "https" && u.Scheme != "ssh") || u.Host == "" {
		return fmt.Errorf("unsupported or invalid clone URL")
	}
	return nil
}

func run(ctx context.Context, dir, name string, args ...string) error {
	cmd := exec.CommandContext(ctx, name, args...)
	cmd.Dir = dir
	cmd.Env = append(os.Environ(), "GIT_TERMINAL_PROMPT=0", "GIT_CONFIG_COUNT=1", "GIT_CONFIG_KEY_0=protocol.ext.allow", "GIT_CONFIG_VALUE_0=never")
	if output, err := cmd.CombinedOutput(); err != nil {
		return fmt.Errorf("%s: %w: %s", name, err, strings.TrimSpace(string(output)))
	}
	return nil
}

var _ app.GitCache = (*GitCache)(nil)
