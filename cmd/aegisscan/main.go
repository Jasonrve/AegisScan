package main

import (
	"context"
	"log"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/aegisscan/aegisscan/internal/domain"
	"github.com/aegisscan/aegisscan/internal/operator"
	"github.com/aegisscan/aegisscan/internal/server"
)

func main() {
	address := os.Getenv("AEGIS_HTTP_ADDR")
	if address == "" {
		address = ":8080"
	}
	var repositories []domain.Repository
	if os.Getenv("AEGIS_ALLOW_DEMO_DATA") == "true" {
		repositories = []domain.Repository{
			{ID: "platform/api", Name: "platform/api", Provider: "github", CloneURL: "https://github.com/example/platform-api.git", DefaultBranch: "main", Status: domain.RepositoryReady, CacheState: "Ready"},
			{ID: "web/app", Name: "web/app", Provider: "github", CloneURL: "https://github.com/example/web-app.git", DefaultBranch: "main", Status: domain.RepositoryReady, CacheState: "Ready"},
			{ID: "infra/terraform", Name: "infra/terraform", Provider: "github", CloneURL: "https://github.com/example/infra-terraform.git", DefaultBranch: "main", Status: domain.RepositoryReady, CacheState: "Ready"},
		}
	}
	state := server.NewMemoryState(repositories)
	var jobs *server.KubernetesRuns
	if kubeServer := os.Getenv("AEGIS_KUBE_SERVER"); kubeServer != "" {
		client, err := operator.NewRemoteKubernetesJobClient(kubeServer, os.Getenv("AEGIS_KUBE_TOKEN"), os.Getenv("AEGIS_KUBE_NAMESPACE"))
		if err != nil {
			log.Fatal(err)
		}
		jobs = &server.KubernetesRuns{Jobs: client, State: state, Image: os.Getenv("AEGIS_SCAN_IMAGE"), Namespace: os.Getenv("AEGIS_KUBE_NAMESPACE")}
	}
	api := server.API{Repositories: state, Runs: state, Cancel: state, Executions: state, Findings: state, Settings: state, SettingsWrite: state, Audit: state, AuditSink: state, AllowRoleHeader: os.Getenv("AEGIS_ALLOW_ROLE_HEADER") == "true", Jobs: jobs}
	oidcIssuer, oidcAudience, oidcRoleClaim, oidcJWKS := os.Getenv("AEGIS_OIDC_ISSUER"), os.Getenv("AEGIS_OIDC_AUDIENCE"), os.Getenv("AEGIS_OIDC_ROLE_CLAIM"), os.Getenv("AEGIS_OIDC_JWKS_URL")
	var handler http.Handler
	if oidcIssuer != "" || oidcAudience != "" || oidcRoleClaim != "" || oidcJWKS != "" {
		if oidcIssuer == "" || oidcAudience == "" || oidcRoleClaim == "" || oidcJWKS == "" {
			log.Fatal("AEGIS_OIDC_ISSUER, AEGIS_OIDC_AUDIENCE, AEGIS_OIDC_ROLE_CLAIM, and AEGIS_OIDC_JWKS_URL must be configured together")
		}
		handler = server.NewHandlerWithAuth(api, server.Authenticator{Verifier: &server.RemoteOIDCVerifier{Issuer: oidcIssuer, Audience: oidcAudience, RoleClaim: oidcRoleClaim, JWKSURL: oidcJWKS}})
	} else if os.Getenv("AEGIS_ALLOW_ANONYMOUS") == "true" {
		log.Print("WARNING: anonymous access enabled; do not use in production")
		handler = server.NewHandler(api)
	} else {
		log.Fatal("configure AEGIS_OIDC_* or set AEGIS_ALLOW_ANONYMOUS=true for local development")
	}
	log.Printf("aegisscan api listening on %s", address)
	srv := &http.Server{Addr: address, Handler: handler, ReadHeaderTimeout: 10 * time.Second, ReadTimeout: 30 * time.Second, WriteTimeout: 60 * time.Second, IdleTimeout: 120 * time.Second}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	go func() {
		<-ctx.Done()
		shutdown, cancel := context.WithTimeout(context.Background(), 20*time.Second)
		defer cancel()
		_ = srv.Shutdown(shutdown)
	}()
	if err := srv.ListenAndServe(); err != nil && err != http.ErrServerClosed {
		log.Fatal(err)
	}
}
