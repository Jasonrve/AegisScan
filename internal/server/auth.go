package server

import (
	"context"
	"crypto/rsa"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"math/big"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/aegisscan/aegisscan/internal/domain"
	"github.com/golang-jwt/jwt/v5"
)

type OIDCConfig struct{ IssuerURL, Audience, RoleClaim string }

func (c OIDCConfig) Validate() error {
	if strings.TrimSpace(c.IssuerURL) == "" || strings.TrimSpace(c.Audience) == "" {
		return fmt.Errorf("OIDC issuer and audience are required")
	}
	if c.RoleClaim == "" {
		return fmt.Errorf("OIDC role claim is required")
	}
	return nil
}

type Principal struct {
	Subject string
	Role    domain.Role
}
type TokenVerifier interface {
	Verify(context.Context, string) (Principal, error)
}
type JWTVerifier struct {
	Issuer, Audience, RoleClaim string
	KeyFunc                     jwt.Keyfunc
	RoleMappings                map[string]domain.Role
}

// RemoteOIDCVerifier obtains signing keys from a configured OIDC JWKS
// endpoint. Keys are refreshed on each verification in this small MVP adapter;
// production deployments can add a bounded cache without changing the token
// or authorization contract.
type RemoteOIDCVerifier struct {
	Issuer, Audience, RoleClaim string
	JWKSURL                     string
	Client                      *http.Client
	RoleMappings                map[string]domain.Role
	mu                          sync.RWMutex
	keys                        map[string]*rsa.PublicKey
	fetchedAt                   time.Time
	TTL                         time.Duration
}

func (v *RemoteOIDCVerifier) Verify(ctx context.Context, tokenString string) (Principal, error) {
	if v.Issuer == "" || v.Audience == "" || v.RoleClaim == "" || v.JWKSURL == "" {
		return Principal{}, fmt.Errorf("remote OIDC verifier is not configured")
	}
	keys, err := v.cachedKeys(ctx)
	if err != nil {
		return Principal{}, err
	}
	verifier := JWTVerifier{Issuer: v.Issuer, Audience: v.Audience, RoleClaim: v.RoleClaim, RoleMappings: v.RoleMappings, KeyFunc: func(token *jwt.Token) (any, error) {
		kid, _ := token.Header["kid"].(string)
		key, ok := keys[kid]
		if !ok {
			refreshed, refreshErr := v.refreshKeys(ctx)
			if refreshErr != nil {
				return nil, refreshErr
			}
			keys = refreshed
			key, ok = keys[kid]
			if !ok {
				return nil, fmt.Errorf("OIDC signing key %q not found", kid)
			}
		}
		return key, nil
	}}
	return verifier.Verify(ctx, tokenString)
}
func (v *RemoteOIDCVerifier) refreshKeys(ctx context.Context) (map[string]*rsa.PublicKey, error) {
	loaded, err := v.fetchKeys(ctx)
	if err != nil {
		return nil, err
	}
	v.mu.Lock()
	v.keys, v.fetchedAt = loaded, time.Now()
	v.mu.Unlock()
	return loaded, nil
}
func (v *RemoteOIDCVerifier) cachedKeys(ctx context.Context) (map[string]*rsa.PublicKey, error) {
	v.mu.RLock()
	keys, fetched := v.keys, v.fetchedAt
	v.mu.RUnlock()
	ttl := v.TTL
	if ttl <= 0 {
		ttl = 15 * time.Minute
	}
	if keys != nil && time.Since(fetched) < ttl {
		return keys, nil
	}
	v.mu.Lock()
	defer v.mu.Unlock()
	if v.keys != nil && time.Since(v.fetchedAt) < ttl {
		return v.keys, nil
	}
	loaded, err := v.fetchKeys(ctx)
	if err != nil {
		return nil, err
	}
	v.keys, v.fetchedAt = loaded, time.Now()
	return loaded, nil
}

type jwksDocument struct {
	Keys []jwk `json:"keys"`
}
type jwk struct {
	Kid string `json:"kid"`
	Kty string `json:"kty"`
	N   string `json:"n"`
	E   string `json:"e"`
}

func (v RemoteOIDCVerifier) fetchKeys(ctx context.Context) (map[string]*rsa.PublicKey, error) {
	client := v.Client
	if client == nil {
		client = &http.Client{Timeout: 10 * time.Second}
	}
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, v.JWKSURL, nil)
	if err != nil {
		return nil, fmt.Errorf("create JWKS request: %w", err)
	}
	response, err := client.Do(request)
	if err != nil {
		return nil, fmt.Errorf("fetch JWKS: %w", err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("fetch JWKS: status %d", response.StatusCode)
	}
	body, err := io.ReadAll(response.Body)
	if err != nil {
		return nil, fmt.Errorf("read JWKS: %w", err)
	}
	var document jwksDocument
	if err := json.Unmarshal(body, &document); err != nil {
		return nil, fmt.Errorf("decode JWKS: %w", err)
	}
	keys := make(map[string]*rsa.PublicKey, len(document.Keys))
	for _, item := range document.Keys {
		if item.Kty != "RSA" || item.Kid == "" {
			continue
		}
		key, err := rsaKey(item.N, item.E)
		if err != nil {
			return nil, fmt.Errorf("decode JWKS key %s: %w", item.Kid, err)
		}
		keys[item.Kid] = key
	}
	if len(keys) == 0 {
		return nil, fmt.Errorf("JWKS contains no usable RSA keys")
	}
	return keys, nil
}

func rsaKey(encodedN, encodedE string) (*rsa.PublicKey, error) {
	modulus, err := base64.RawURLEncoding.DecodeString(encodedN)
	if err != nil {
		return nil, fmt.Errorf("decode modulus: %w", err)
	}
	exponent, err := base64.RawURLEncoding.DecodeString(encodedE)
	if err != nil || len(exponent) == 0 || len(exponent) > 4 {
		return nil, fmt.Errorf("decode exponent")
	}
	value := 0
	for _, byteValue := range exponent {
		value = value<<8 | int(byteValue)
	}
	if value < 2 {
		return nil, fmt.Errorf("invalid exponent")
	}
	return &rsa.PublicKey{N: new(big.Int).SetBytes(modulus), E: value}, nil
}

func (v JWTVerifier) Verify(ctx context.Context, tokenString string) (Principal, error) {
	if v.KeyFunc == nil || v.Issuer == "" || v.Audience == "" || v.RoleClaim == "" {
		return Principal{}, fmt.Errorf("JWT verifier is not configured")
	}
	token, err := jwt.ParseWithClaims(tokenString, jwt.MapClaims{}, v.KeyFunc, jwt.WithIssuer(v.Issuer), jwt.WithAudience(v.Audience))
	if err != nil || !token.Valid {
		return Principal{}, fmt.Errorf("invalid OIDC token: %w", err)
	}
	claims, ok := token.Claims.(jwt.MapClaims)
	if !ok {
		return Principal{}, fmt.Errorf("invalid OIDC claims")
	}
	subject, ok := claims["sub"].(string)
	if !ok || subject == "" {
		return Principal{}, fmt.Errorf("OIDC subject is required")
	}
	roleNames := claimValues(claims[v.RoleClaim])
	if len(roleNames) == 0 {
		return Principal{}, fmt.Errorf("OIDC role claim is required")
	}
	role, ok := v.mapRole(roleNames)
	if !ok {
		return Principal{}, fmt.Errorf("unsupported OIDC role")
	}
	select {
	case <-ctx.Done():
		return Principal{}, ctx.Err()
	default:
	}
	return Principal{Subject: subject, Role: role}, nil
}

func claimValues(value any) []string {
	switch claim := value.(type) {
	case string:
		return []string{claim}
	case []any:
		values := make([]string, 0, len(claim))
		for _, item := range claim {
			if value, ok := item.(string); ok {
				values = append(values, value)
			}
		}
		return values
	default:
		return nil
	}
}

func (v JWTVerifier) mapRole(values []string) (domain.Role, bool) {
	for _, value := range values {
		if mapped, ok := v.RoleMappings[value]; ok && validRole(mapped) {
			return mapped, true
		}
		role := domain.Role(strings.ToLower(value))
		if validRole(role) {
			return role, true
		}
	}
	return "", false
}

func validRole(role domain.Role) bool {
	return role == domain.RoleAdmin || role == domain.RoleOperator || role == domain.RoleViewer
}

type Authenticator struct{ Verifier TokenVerifier }
type principalKey struct{}

func WithPrincipal(ctx context.Context, principal Principal) context.Context {
	return context.WithValue(ctx, principalKey{}, principal)
}
func PrincipalFromContext(ctx context.Context) (Principal, bool) {
	principal, ok := ctx.Value(principalKey{}).(Principal)
	return principal, ok
}
func (a Authenticator) Middleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if a.Verifier == nil {
			next.ServeHTTP(w, r)
			return
		}
		header := strings.TrimSpace(r.Header.Get("Authorization"))
		if !strings.HasPrefix(header, "Bearer ") {
			writeJSON(w, http.StatusUnauthorized, map[string]string{"code": "unauthenticated"})
			return
		}
		principal, err := a.Verifier.Verify(r.Context(), strings.TrimSpace(strings.TrimPrefix(header, "Bearer ")))
		if err != nil || principal.Subject == "" {
			writeJSON(w, http.StatusUnauthorized, map[string]string{"code": "unauthenticated"})
			return
		}
		next.ServeHTTP(w, r.WithContext(WithPrincipal(r.Context(), principal)))
	})
}
