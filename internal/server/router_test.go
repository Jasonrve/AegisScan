package server

import (
	"net/http/httptest"
	"testing"
)

func TestNewHandlerServesAPIAndHealth(t *testing.T) {
	handler := NewHandler(API{AllowRoleHeader: true})
	for _, path := range []string{"/healthz", "/readyz", "/metrics", "/api/v1/repositories"} {
		response := httptest.NewRecorder()
		request := httptest.NewRequest("GET", path, nil)
		request.Header.Set("X-Aegis-Role", "viewer")
		handler.ServeHTTP(response, request)
		if response.Code != 200 {
			t.Fatalf("%s status = %d", path, response.Code)
		}
	}
}
