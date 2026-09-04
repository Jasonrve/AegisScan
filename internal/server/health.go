package server

import "net/http"

func HealthHandler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte(`{"status":"ok"}`))
	})
	mux.HandleFunc("/readyz", func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte(`{"status":"ready"}`))
	})
	mux.HandleFunc("/metrics", func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "text/plain; version=0.0.4")
		_, _ = w.Write([]byte("# HELP aegisscan_up Whether the AegisScan process is serving requests.\n# TYPE aegisscan_up gauge\naegisscan_up 1\n"))
	})
	return secureHeaders(mux)
}

func NewHandler(api API) http.Handler {
	return newHandler(api, nil)
}

// NewHandlerWithAuth composes the API with the supplied OIDC authenticator.
// Health endpoints remain probeable without a bearer token; API routes are
// authenticated before reaching role-aware handlers.
func NewHandlerWithAuth(api API, authenticator Authenticator) http.Handler {
	return newHandler(api, &authenticator)
}

func newHandler(api API, authenticator *Authenticator) http.Handler {
	mux := http.NewServeMux()
	health := HealthHandler()
	mux.Handle("/healthz", health)
	mux.Handle("/readyz", health)
	mux.Handle("/metrics", health)
	apiHandler := api.Handler()
	if authenticator != nil {
		apiHandler = authenticator.Middleware(apiHandler)
	}
	mux.Handle("/api/", secureHeaders(apiHandler))
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Body != nil {
			r.Body = http.MaxBytesReader(w, r.Body, 1<<20)
		}
		mux.ServeHTTP(w, r)
	})
}

func secureHeaders(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("X-Content-Type-Options", "nosniff")
		w.Header().Set("X-Frame-Options", "DENY")
		w.Header().Set("Referrer-Policy", "no-referrer")
		next.ServeHTTP(w, r)
	})
}
