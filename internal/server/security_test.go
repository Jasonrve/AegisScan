package server

import (
	"context"
	"net/http/httptest"
	"testing"

	"github.com/aegisscan/aegisscan/internal/domain"
)

func TestRoleHeaderIsIgnoredUnlessExplicitlyEnabled(t *testing.T) {
	request := httptest.NewRequest("GET", "/api/v1/repositories", nil)
	request.Header.Set("X-Aegis-Role", "admin")
	if got := role(request); got != "" {
		t.Fatalf("role = %s", got)
	}
	ctx := context.WithValue(request.Context(), roleHeaderKey{}, true)
	if got := role(request.WithContext(ctx)); got != domain.RoleAdmin {
		t.Fatalf("development role = %s", got)
	}
}
