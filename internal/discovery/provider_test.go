package discovery

import (
	"context"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestGitHubProviderRequiresOrganization(t *testing.T) {
	_, err := (GitHubProvider{}).Discover(nil, Source{})
	if err == nil {
		t.Fatal("expected organization validation error")
	}
}

func TestGitHubProviderFiltersArchivedAndPatterns(t *testing.T) {
	ts := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/orgs/acme/repos" {
			t.Fatalf("path = %s", r.URL.Path)
		}
		_, _ = w.Write([]byte(`[{"name":"app","clone_url":"https://example/app.git","default_branch":"main","archived":false},{"name":"old","clone_url":"x","default_branch":"main","archived":true}]`))
	}))
	defer ts.Close()
	got, err := (GitHubProvider{BaseURL: ts.URL}).Discover(context.Background(), Source{Organization: "acme", Include: []string{"app"}})
	if err != nil || len(got) != 1 || got[0].Name != "app" || got[0].ID != "app" || got[0].CloneURL != "https://example/app.git" || got[0].DefaultBranch != "main" {
		t.Fatalf("repositories = %#v, err = %v", got, err)
	}
}

func TestNextLink(t *testing.T) {
	got := nextLink(`<https://api.github.com/orgs/acme/repos?page=2>; rel="next", <https://api.github.com/orgs/acme/repos?page=4>; rel="last"`)
	if got == "" || got[len(got)-6:] != "page=2" {
		t.Fatalf("next link = %q", got)
	}
}
