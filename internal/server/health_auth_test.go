package server

import (
	"net/http/httptest"
	"testing"
)

func TestNewHandlerWithAuthProtectsAPIButNotProbes(t *testing.T) {
	handler := NewHandlerWithAuth(API{}, Authenticator{Verifier: verifierStub{}})
	probe := httptest.NewRecorder()
	handler.ServeHTTP(probe, httptest.NewRequest("GET", "/healthz", nil))
	if probe.Code != 200 {
		t.Fatalf("health status = %d", probe.Code)
	}
	api := httptest.NewRecorder()
	handler.ServeHTTP(api, httptest.NewRequest("GET", "/api/v1/repositories", nil))
	if api.Code != 401 {
		t.Fatalf("unauthenticated API status = %d", api.Code)
	}
}
