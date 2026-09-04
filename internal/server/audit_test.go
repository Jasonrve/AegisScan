package server

import (
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/aegisscan/aegisscan/internal/domain"
)

func TestAdminAuditEndpointAndSettingsMutation(t *testing.T) {
	state := NewMemoryState(nil)
	api := API{Settings: state, SettingsWrite: state, Audit: state, AuditSink: state, AllowRoleHeader: true}
	request := httptest.NewRequest("PUT", "/api/v1/settings", strings.NewReader(`{"maxConcurrency":8,"defaultScanMode":"incremental","retentionDays":30,"cacheRetentionGB":50}`))
	request.Header.Set("X-Aegis-Role", "admin")
	response := httptest.NewRecorder()
	api.Handler().ServeHTTP(response, request)
	if response.Code != 200 || len(state.ListAuditEvents()) != 1 || state.ListAuditEvents()[0].Action != "update_settings" {
		t.Fatalf("status=%d audit=%#v", response.Code, state.ListAuditEvents())
	}
	read := httptest.NewRequest("GET", "/api/v1/audit", nil)
	read.Header.Set("X-Aegis-Role", "admin")
	result := httptest.NewRecorder()
	api.Handler().ServeHTTP(result, read)
	if result.Code != 200 || !strings.Contains(result.Body.String(), string(domain.RoleAdmin)) {
		t.Fatalf("audit response status=%d body=%s", result.Code, result.Body.String())
	}
}
