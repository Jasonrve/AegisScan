package server

import (
	"net/http/httptest"
	"testing"
)

func TestHealthEndpoints(t *testing.T) {
	h := HealthHandler()
	for _, path := range []string{"/healthz", "/readyz"} {
		r := httptest.NewRecorder()
		h.ServeHTTP(r, httptest.NewRequest("GET", path, nil))
		if r.Code != 200 {
			t.Fatalf("%s status = %d", path, r.Code)
		}
		if r.Header().Get("X-Content-Type-Options") != "nosniff" {
			t.Fatal("security headers missing")
		}
	}
}

func TestMetricsEndpoint(t *testing.T) {
	r := httptest.NewRecorder()
	HealthHandler().ServeHTTP(r, httptest.NewRequest("GET", "/metrics", nil))
	if r.Code != 200 || r.Body.String() == "" {
		t.Fatalf("metrics response = %d %q", r.Code, r.Body.String())
	}
}
