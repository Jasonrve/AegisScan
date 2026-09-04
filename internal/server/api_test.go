package server

import (
	"github.com/aegisscan/aegisscan/internal/app"
	"github.com/aegisscan/aegisscan/internal/domain"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestAPIViewerCanReadButCannotStartRun(t *testing.T) {
	api := API{AllowRoleHeader: true}
	read := httptest.NewRecorder()
	api.Handler().ServeHTTP(read, httptest.NewRequest("GET", "/api/v1/repositories", nil))
	if read.Code != 200 {
		t.Fatalf("read status = %d", read.Code)
	}
	request := httptest.NewRequest("POST", "/api/v1/runs", nil)
	request.Header.Set("X-Aegis-Role", "viewer")
	write := httptest.NewRecorder()
	api.Handler().ServeHTTP(write, request)
	if write.Code != 403 {
		t.Fatalf("run status = %d", write.Code)
	}
}

func TestAPIViewerCanReadRunsAndFindings(t *testing.T) {
	api := API{AllowRoleHeader: true}
	for _, path := range []string{"/api/v1/runs", "/api/v1/findings"} {
		r := httptest.NewRecorder()
		request := httptest.NewRequest("GET", path, nil)
		request.Header.Set("X-Aegis-Role", "viewer")
		api.Handler().ServeHTTP(r, request)
		if r.Code != 200 {
			t.Fatalf("%s status = %d", path, r.Code)
		}
	}
}

func TestSettingsReadAndAdminUpdate(t *testing.T) {
	store := app.NewSettingsStore()
	api := API{Settings: store, SettingsWrite: store, AllowRoleHeader: true}
	read := httptest.NewRecorder()
	api.Handler().ServeHTTP(read, httptest.NewRequest("GET", "/api/v1/settings", nil))
	if read.Code != 200 {
		t.Fatalf("settings read status = %d", read.Code)
	}
	update := httptest.NewRequest("PUT", "/api/v1/settings", strings.NewReader(`{"maxConcurrency":8,"defaultScanMode":"incremental","retentionDays":30,"cacheRetentionGB":50}`))
	update.Header.Set("X-Aegis-Role", "admin")
	result := httptest.NewRecorder()
	api.Handler().ServeHTTP(result, update)
	if result.Code != 200 {
		t.Fatalf("settings update status = %d", result.Code)
	}
}

type repositoryReaderStub struct{}

func (repositoryReaderStub) ListRepositories() []domain.Repository {
	return []domain.Repository{{Name: "one"}, {Name: "two"}, {Name: "three"}}
}
func TestAPIRepositoryPagination(t *testing.T) {
	request := httptest.NewRequest("GET", "/api/v1/repositories?page=2&pageSize=2", nil)
	request.Header.Set("X-Aegis-Role", "viewer")
	response := httptest.NewRecorder()
	(API{Repositories: repositoryReaderStub{}, AllowRoleHeader: true}).Handler().ServeHTTP(response, request)
	if response.Code != 200 || !strings.Contains(response.Body.String(), `"name":"three"`) || strings.Contains(response.Body.String(), `"name":"one"`) {
		t.Fatalf("pagination response = %s", response.Body.String())
	}
}

func TestAdminCanCreateRepository(t *testing.T) {
	state := NewMemoryState(nil)
	api := API{Repositories: state, AllowRoleHeader: true, AuditSink: state}
	request := httptest.NewRequest("POST", "/api/v1/repositories", strings.NewReader(`{"name":"team/service","cloneUrl":"https://github.com/team/service.git"}`))
	request.Header.Set("X-Aegis-Role", "admin")
	response := httptest.NewRecorder()
	api.Handler().ServeHTTP(response, request)
	if response.Code != 201 || !strings.Contains(response.Body.String(), "team/service") {
		t.Fatalf("create status=%d body=%s", response.Code, response.Body.String())
	}
}
