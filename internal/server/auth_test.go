package server

import (
	"context"
	"crypto/rand"
	"crypto/rsa"
	"encoding/base64"
	"encoding/json"
	"github.com/aegisscan/aegisscan/internal/domain"
	"github.com/golang-jwt/jwt/v5"
	"net/http"
	"net/http/httptest"
	"testing"
)

type verifierStub struct{}

func (verifierStub) Verify(context.Context, string) (Principal, error) {
	return Principal{Subject: "user-1", Role: domain.RoleOperator}, nil
}
func TestAuthenticatorAddsPrincipal(t *testing.T) {
	var got Principal
	next := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		got, _ = PrincipalFromContext(r.Context())
		w.WriteHeader(http.StatusNoContent)
	})
	request := httptest.NewRequest("GET", "/", nil)
	request.Header.Set("Authorization", "Bearer token")
	response := httptest.NewRecorder()
	(Authenticator{Verifier: verifierStub{}}).Middleware(next).ServeHTTP(response, request)
	if response.Code != http.StatusNoContent || got.Role != domain.RoleOperator {
		t.Fatalf("status=%d principal=%#v", response.Code, got)
	}
}

func TestJWTVerifierValidatesIssuerAudienceAndRole(t *testing.T) {
	secret := []byte("test-secret")
	signed := jwt.NewWithClaims(jwt.SigningMethodHS256, jwt.MapClaims{"sub": "user-1", "iss": "https://issuer.example", "aud": "aegisscan", "role": "operator"})
	token, err := signed.SignedString(secret)
	if err != nil {
		t.Fatal(err)
	}
	verifier := JWTVerifier{Issuer: "https://issuer.example", Audience: "aegisscan", RoleClaim: "role", KeyFunc: func(*jwt.Token) (any, error) { return secret, nil }}
	principal, err := verifier.Verify(context.Background(), token)
	if err != nil || principal.Role != domain.RoleOperator {
		t.Fatalf("principal=%#v err=%v", principal, err)
	}
}

func TestJWTVerifierMapsOIDCGroups(t *testing.T) {
	secret := []byte("test-secret")
	signed := jwt.NewWithClaims(jwt.SigningMethodHS256, jwt.MapClaims{"sub": "user-2", "iss": "https://issuer.example", "aud": "aegisscan", "groups": []string{"security-operators"}})
	token, err := signed.SignedString(secret)
	if err != nil {
		t.Fatal(err)
	}
	verifier := JWTVerifier{Issuer: "https://issuer.example", Audience: "aegisscan", RoleClaim: "groups", RoleMappings: map[string]domain.Role{"security-operators": domain.RoleOperator}, KeyFunc: func(*jwt.Token) (any, error) { return secret, nil }}
	principal, err := verifier.Verify(context.Background(), token)
	if err != nil || principal.Role != domain.RoleOperator {
		t.Fatalf("principal=%#v err=%v", principal, err)
	}
}

func TestRemoteOIDCVerifierFetchesJWKS(t *testing.T) {
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		_ = json.NewEncoder(w).Encode(map[string]any{"keys": []map[string]string{{"kid": "key-1", "kty": "RSA", "n": base64.RawURLEncoding.EncodeToString(key.N.Bytes()), "e": base64.RawURLEncoding.EncodeToString([]byte{1, 0, 1})}}})
	}))
	defer server.Close()
	signed := jwt.NewWithClaims(jwt.SigningMethodRS256, jwt.MapClaims{"sub": "user-3", "iss": "https://issuer.example", "aud": "aegisscan", "role": "viewer"})
	signed.Header["kid"] = "key-1"
	token, err := signed.SignedString(key)
	if err != nil {
		t.Fatal(err)
	}
	verifier := &RemoteOIDCVerifier{Issuer: "https://issuer.example", Audience: "aegisscan", RoleClaim: "role", JWKSURL: server.URL}
	principal, err := verifier.Verify(context.Background(), token)
	if err != nil || principal.Role != domain.RoleViewer {
		t.Fatalf("principal=%#v err=%v", principal, err)
	}
}
