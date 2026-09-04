package plugins

import (
	"strings"
	"testing"
)

func TestSanitizeRedactsCredentialPatternsAndCapsLogs(t *testing.T) {
	value := `Authorization: Bearer abc123 token=ghp_abcdefghijklmnopqrstuvwxyz123456 https://user:pass@example.test -----BEGIN PRIVATE KEY-----secret-----END PRIVATE KEY-----`
	clean := Sanitize(value)
	for _, secret := range []string{"abc123", "ghp_abcdefghijklmnopqrstuvwxyz123456", "user:pass", "PRIVATE KEY-----secret"} {
		if strings.Contains(clean, secret) {
			t.Fatalf("secret %q leaked in %q", secret, clean)
		}
	}
	if len(Sanitize(strings.Repeat("x", maxSanitizedLogBytes+10))) <= maxSanitizedLogBytes {
		t.Fatal("long log was not capped")
	}
}
