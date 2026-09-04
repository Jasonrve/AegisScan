package discovery

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"path"
	"strings"

	"github.com/aegisscan/aegisscan/internal/domain"
)

type Source struct {
	Provider        string
	Organization    string
	Include         []string
	Exclude         []string
	IncludeArchived bool
}

type Provider interface {
	Discover(ctx context.Context, source Source) ([]domain.Repository, error)
}

type GitHubProvider struct {
	Client  *http.Client
	Token   string
	BaseURL string
}

func (p GitHubProvider) Discover(ctx context.Context, source Source) ([]domain.Repository, error) {
	if strings.TrimSpace(source.Organization) == "" {
		return nil, fmt.Errorf("github organization is required")
	}
	client := p.Client
	if client == nil {
		client = http.DefaultClient
	}
	base := p.BaseURL
	if base == "" {
		base = "https://api.github.com"
	}
	result := make([]domain.Repository, 0, 100)
	next := strings.TrimRight(base, "/") + "/orgs/" + source.Organization + "/repos?per_page=100"
	for next != "" {
		req, err := http.NewRequestWithContext(ctx, http.MethodGet, next, nil)
		if err != nil {
			return nil, err
		}
		req.Header.Set("Accept", "application/vnd.github+json")
		if p.Token != "" {
			req.Header.Set("Authorization", "Bearer "+p.Token)
		}
		resp, err := client.Do(req)
		if err != nil {
			return nil, fmt.Errorf("github repository request: %w", err)
		}
		if resp.StatusCode != http.StatusOK {
			resp.Body.Close()
			return nil, fmt.Errorf("github repository request returned %s", resp.Status)
		}
		var items []struct {
			Name          string `json:"name"`
			FullName      string `json:"full_name"`
			CloneURL      string `json:"clone_url"`
			DefaultBranch string `json:"default_branch"`
			Archived      bool   `json:"archived"`
		}
		if err := json.NewDecoder(resp.Body).Decode(&items); err != nil {
			resp.Body.Close()
			return nil, fmt.Errorf("decode github repositories: %w", err)
		}
		link := resp.Header.Get("Link")
		resp.Body.Close()
		for _, item := range items {
			if item.Archived && !source.IncludeArchived {
				continue
			}
			if matches(source, item.Name) {
				name := item.FullName
				if name == "" {
					name = item.Name
				}
				result = append(result, domain.Repository{ID: domain.RepositoryID(name), Name: name, Provider: "github", CloneURL: item.CloneURL, DefaultBranch: item.DefaultBranch, Status: domain.RepositoryReady})
			}
		}
		next = nextLink(link)
	}
	return result, nil
}

func nextLink(header string) string {
	for _, part := range strings.Split(header, ",") {
		pieces := strings.Split(part, ";")
		if len(pieces) < 2 || !strings.Contains(pieces[1], `rel="next"`) {
			continue
		}
		value := strings.TrimSpace(pieces[0])
		if len(value) > 2 && value[0] == '<' && value[len(value)-1] == '>' {
			if parsed, err := url.Parse(value[1 : len(value)-1]); err == nil {
				return parsed.String()
			}
		}
	}
	return ""
}

func matches(source Source, name string) bool {
	for _, pattern := range source.Exclude {
		if ok, _ := path.Match(pattern, name); ok {
			return false
		}
	}
	if len(source.Include) == 0 {
		return true
	}
	for _, pattern := range source.Include {
		if ok, _ := path.Match(pattern, name); ok {
			return true
		}
	}
	return false
}
