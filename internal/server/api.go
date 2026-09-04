package server

import (
	"context"
	"encoding/json"
	"net/http"
	"strconv"
	"strings"
	"time"

	"github.com/aegisscan/aegisscan/internal/domain"
)

type RepositoryReader interface{ ListRepositories() []domain.Repository }
type RepositoryWriter interface {
	CreateRepository(domain.Repository) (domain.Repository, error)
}
type RepositoryDeleter interface { DeleteRepository(domain.RepositoryID) bool }
type RunStarter interface {
	StartRepositoryRun(domain.RepositoryID, domain.Scanner, domain.ScanMode) (domain.Execution, error)
}
type RunCanceler interface{ CancelRun(string) bool }
type JobStarter interface {
	StartJob(context.Context, domain.Execution, domain.Repository) (string, error)
}
type ExecutionRefresher interface{ RefreshExecution(context.Context, string) }
type RunLogReader interface {
	ExecutionLogs(context.Context, string) (string, error)
}
type ExecutionReader interface{ ListExecutions() []domain.Execution }
type FindingReader interface{ ListFindings() []domain.Finding }
type ExecutionDetailReader interface {
	GetExecution(string) (domain.Execution, bool)
}
type FindingDetailReader interface {
	GetFinding(string) (domain.Finding, bool)
}
type SettingsReader interface {
	Get(context.Context) domain.RuntimeSettings
}
type SettingsWriter interface {
	Update(context.Context, domain.RuntimeSettings) error
}
type AuditReader interface{ ListAuditEvents() []domain.AuditEvent }
type AuditWriter interface{ AppendAudit(domain.AuditEvent) }

type API struct {
	Repositories  RepositoryReader
	Runs          RunStarter
	Cancel        RunCanceler
	Executions    ExecutionReader
	Findings      FindingReader
	Settings      SettingsReader
	SettingsWrite SettingsWriter
	Audit         AuditReader
	AuditSink     AuditWriter
	// AllowRoleHeader is for local development only. Production should provide
	// an Authenticator so roles come from a verified OIDC principal.
	AllowRoleHeader bool
	Jobs            JobStarter
}

func (a API) Handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("/api/v1/repositories", a.repositories)
	mux.HandleFunc("/api/v1/runs", a.runs)
	mux.HandleFunc("/api/v1/runs/", a.runDetail)
	mux.HandleFunc("/api/v1/findings", a.findings)
	mux.HandleFunc("/api/v1/findings/", a.findingDetail)
	mux.HandleFunc("/api/v1/settings", a.settings)
	mux.HandleFunc("/api/v1/audit", a.audit)
	if a.AllowRoleHeader {
		return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			ctx := context.WithValue(r.Context(), roleHeaderKey{}, true)
			mux.ServeHTTP(w, r.WithContext(ctx))
		})
	}
	return mux
}

type roleHeaderKey struct{}

func role(r *http.Request) domain.Role {
	if principal, ok := PrincipalFromContext(r.Context()); ok {
		return principal.Role
	}
	if r.Context().Value(roleHeaderKey{}) != true {
		return ""
	}
	switch strings.ToLower(r.Header.Get("X-Aegis-Role")) {
	case "admin":
		return domain.RoleAdmin
	case "operator":
		return domain.RoleOperator
	default:
		return domain.RoleViewer
	}
}
func writeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(value)
}

func page(r *http.Request) (int, int) {
	current, _ := strconv.Atoi(r.URL.Query().Get("page"))
	size, _ := strconv.Atoi(r.URL.Query().Get("pageSize"))
	if current < 1 {
		current = 1
	}
	if size < 1 {
		size = 50
	}
	if size > 100 {
		size = 100
	}
	return current, size
}
func paginate[T any](items []T, r *http.Request) []T {
	current, size := page(r)
	start := (current - 1) * size
	if start >= len(items) {
		return []T{}
	}
	end := start + size
	if end > len(items) {
		end = len(items)
	}
	return items[start:end]
}
func pagePayload[T any](items []T, r *http.Request) map[string]any {
	current, size := page(r)
	return map[string]any{"items": paginate(items, r), "page": current, "pageSize": size, "total": len(items)}
}

func (a API) repositories(w http.ResponseWriter, r *http.Request) {
	if r.Method == http.MethodDelete {
		if !role(r).Allows(domain.PermissionConfigure) { writeJSON(w, http.StatusForbidden, map[string]string{"code":"forbidden"}); return }
		deleter, ok := a.Repositories.(RepositoryDeleter); if !ok { writeJSON(w, http.StatusServiceUnavailable, map[string]string{"code":"repository_service_unavailable"}); return }
		id := domain.RepositoryID(strings.TrimPrefix(r.URL.Path, "/api/v1/repositories/")); if id == "" || !deleter.DeleteRepository(id) { writeJSON(w, http.StatusNotFound, map[string]string{"code":"repository_not_found"}); return }; a.recordAudit(r, "delete_repository", string(id), "accepted", nil); w.WriteHeader(http.StatusNoContent); return
	}
	if r.Method == http.MethodPost {
		if !role(r).Allows(domain.PermissionConfigure) {
			writeJSON(w, http.StatusForbidden, map[string]string{"code": "forbidden"})
			return
		}
		writer, ok := a.Repositories.(RepositoryWriter)
		if !ok {
			writeJSON(w, http.StatusServiceUnavailable, map[string]string{"code": "repository_service_unavailable"})
			return
		}
		var request struct {
			Name          string `json:"name"`
			Provider      string `json:"provider"`
			CloneURL      string `json:"cloneUrl"`
			DefaultBranch string `json:"defaultBranch"`
		}
		if err := json.NewDecoder(r.Body).Decode(&request); err != nil || strings.TrimSpace(request.Name) == "" || strings.TrimSpace(request.CloneURL) == "" {
			writeJSON(w, http.StatusBadRequest, map[string]string{"code": "invalid_repository"})
			return
		}
		if request.Provider == "" {
			request.Provider = "github"
		}
		if request.DefaultBranch == "" {
			request.DefaultBranch = "main"
		}
		repository, err := writer.CreateRepository(domain.Repository{Name: strings.TrimSpace(request.Name), Provider: request.Provider, CloneURL: strings.TrimSpace(request.CloneURL), DefaultBranch: request.DefaultBranch, Status: domain.RepositoryReady, CacheState: "Pending"})
		if err != nil {
			writeJSON(w, http.StatusConflict, map[string]string{"code": "repository_exists"})
			return
		}
		a.recordAudit(r, "create_repository", string(repository.ID), "accepted", nil)
		writeJSON(w, http.StatusCreated, repository)
		return
	}
	if r.Method != http.MethodGet || !role(r).Allows(domain.PermissionView) {
		writeJSON(w, http.StatusForbidden, map[string]string{"code": "forbidden"})
		return
	}
	if a.Repositories == nil {
		writeJSON(w, http.StatusOK, []domain.Repository{})
		return
	}
	writeJSON(w, http.StatusOK, pagePayload(a.Repositories.ListRepositories(), r))
}
func (a API) runs(w http.ResponseWriter, r *http.Request) {
	if r.Method == http.MethodGet {
		if !role(r).Allows(domain.PermissionView) {
			writeJSON(w, http.StatusForbidden, map[string]string{"code": "forbidden"})
			return
		}
		if a.Executions == nil {
			writeJSON(w, http.StatusOK, []domain.Execution{})
			return
		}
		if refresher, ok := a.Jobs.(ExecutionRefresher); ok {
			for _, execution := range a.Executions.ListExecutions() {
				refresher.RefreshExecution(r.Context(), execution.ID)
			}
		}
		writeJSON(w, http.StatusOK, pagePayload(a.Executions.ListExecutions(), r))
		return
	}
	if r.Method != http.MethodPost || !role(r).Allows(domain.PermissionRun) {
		writeJSON(w, http.StatusForbidden, map[string]string{"code": "forbidden"})
		return
	}
	if a.Runs == nil {
		writeJSON(w, http.StatusServiceUnavailable, map[string]string{"code": "run_service_unavailable"})
		return
	}
	var request struct {
		RepositoryID domain.RepositoryID `json:"repositoryId"`
		Scanner      domain.Scanner      `json:"scanner"`
		Mode         domain.ScanMode     `json:"mode"`
	}
	if err := json.NewDecoder(r.Body).Decode(&request); err != nil || request.RepositoryID == "" || request.Scanner == "" {
		writeJSON(w, http.StatusBadRequest, map[string]string{"code": "invalid_request"})
		return
	}
	execution, err := a.Runs.StartRepositoryRun(request.RepositoryID, request.Scanner, request.Mode)
	if err != nil {
		writeJSON(w, http.StatusUnprocessableEntity, map[string]string{"code": "run_rejected"})
		return
	}
	if a.Jobs != nil {
		repository, found := repositoryByID(a.Repositories, request.RepositoryID)
		if !found {
			writeJSON(w, http.StatusUnprocessableEntity, map[string]string{"code": "repository_not_found"})
			return
		}
		jobName, startErr := a.Jobs.StartJob(r.Context(), execution, repository)
		if startErr != nil {
			writeJSON(w, http.StatusServiceUnavailable, map[string]string{"code": "job_start_failed"})
			return
		}
		execution.JobName = jobName
		if state, ok := a.Executions.(*MemoryState); ok {
			state.UpdateExecution(execution)
		}
	}
	a.recordAudit(r, "manual_scan", string(request.RepositoryID), "accepted", map[string]string{"scanner": string(request.Scanner), "mode": string(request.Mode)})
	writeJSON(w, http.StatusAccepted, execution)
}

func (a API) findings(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet || !role(r).Allows(domain.PermissionView) {
		writeJSON(w, http.StatusForbidden, map[string]string{"code": "forbidden"})
		return
	}
	if a.Findings == nil {
		writeJSON(w, http.StatusOK, []domain.Finding{})
		return
	}
	writeJSON(w, http.StatusOK, pagePayload(a.Findings.ListFindings(), r))
}

func (a API) runDetail(w http.ResponseWriter, r *http.Request) {
	if r.Method == http.MethodDelete {
		if !role(r).Allows(domain.PermissionCancel) || a.Cancel == nil {
			writeJSON(w, http.StatusForbidden, map[string]string{"code": "forbidden"})
			return
		}
		id := strings.TrimPrefix(r.URL.Path, "/api/v1/runs/")
		if !a.Cancel.CancelRun(id) {
			writeJSON(w, http.StatusNotFound, map[string]string{"code": "run_not_found"})
			return
		}
		a.recordAudit(r, "cancel_run", id, "accepted", nil)
		w.WriteHeader(http.StatusNoContent)
		return
	}
	if r.Method != http.MethodGet || !role(r).Allows(domain.PermissionView) {
		writeJSON(w, http.StatusForbidden, map[string]string{"code": "forbidden"})
		return
	}
	reader, ok := a.Executions.(ExecutionDetailReader)
	if !ok {
		writeJSON(w, http.StatusNotFound, map[string]string{"code": "run_not_found"})
		return
	}
	id := strings.TrimPrefix(r.URL.Path, "/api/v1/runs/")
	if strings.HasSuffix(id, "/logs") {
		id = strings.TrimSuffix(id, "/logs")
		if reader, ok := a.Jobs.(RunLogReader); ok {
			logText, err := reader.ExecutionLogs(r.Context(), id)
			if err == nil {
				writeJSON(w, http.StatusOK, map[string]string{"logs": logText})
				return
			}
		}
		writeJSON(w, http.StatusNotFound, map[string]string{"code": "logs_not_found"})
		return
	}
	if refresher, ok := a.Jobs.(ExecutionRefresher); ok {
		refresher.RefreshExecution(r.Context(), id)
	}
	execution, found := reader.GetExecution(id)
	if !found {
		writeJSON(w, http.StatusNotFound, map[string]string{"code": "run_not_found"})
		return
	}
	writeJSON(w, http.StatusOK, execution)
}

func repositoryByID(reader RepositoryReader, id domain.RepositoryID) (domain.Repository, bool) {
	if reader == nil {
		return domain.Repository{}, false
	}
	for _, item := range reader.ListRepositories() {
		if item.ID == id {
			return item, true
		}
	}
	return domain.Repository{}, false
}

func (a API) findingDetail(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet || !role(r).Allows(domain.PermissionView) {
		writeJSON(w, http.StatusForbidden, map[string]string{"code": "forbidden"})
		return
	}
	reader, ok := a.Findings.(FindingDetailReader)
	if !ok {
		writeJSON(w, http.StatusNotFound, map[string]string{"code": "finding_not_found"})
		return
	}
	fingerprint := strings.TrimPrefix(r.URL.Path, "/api/v1/findings/")
	finding, found := reader.GetFinding(fingerprint)
	if !found {
		writeJSON(w, http.StatusNotFound, map[string]string{"code": "finding_not_found"})
		return
	}
	writeJSON(w, http.StatusOK, finding)
}

func (a API) settings(w http.ResponseWriter, r *http.Request) {
	switch r.Method {
	case http.MethodGet:
		if !role(r).Allows(domain.PermissionView) {
			writeJSON(w, http.StatusForbidden, map[string]string{"code": "forbidden"})
			return
		}
		if a.Settings == nil {
			writeJSON(w, http.StatusOK, domain.DefaultRuntimeSettings())
			return
		}
		writeJSON(w, http.StatusOK, a.Settings.Get(r.Context()))
	case http.MethodPut:
		if !role(r).Allows(domain.PermissionConfigure) || a.SettingsWrite == nil {
			writeJSON(w, http.StatusForbidden, map[string]string{"code": "forbidden"})
			return
		}
		settings := domain.DefaultRuntimeSettings()
		if a.Settings != nil {
			settings = a.Settings.Get(r.Context())
		}
		decoder := json.NewDecoder(r.Body)
		decoder.DisallowUnknownFields()
		if err := decoder.Decode(&settings); err != nil {
			writeJSON(w, http.StatusBadRequest, map[string]string{"code": "invalid_request"})
			return
		}
		if err := a.SettingsWrite.Update(r.Context(), settings); err != nil {
			writeJSON(w, http.StatusUnprocessableEntity, map[string]string{"code": "invalid_settings"})
			return
		}
		a.recordAudit(r, "update_settings", "runtime", "accepted", map[string]string{"maxConcurrency": strconv.Itoa(settings.MaxConcurrency), "defaultScanMode": string(settings.DefaultScanMode)})
		writeJSON(w, http.StatusOK, settings)
	default:
		writeJSON(w, http.StatusMethodNotAllowed, map[string]string{"code": "method_not_allowed"})
	}
}

func (a API) recordAudit(r *http.Request, action, target, outcome string, metadata map[string]string) {
	if a.AuditSink == nil {
		return
	}
	actor := "local"
	if principal, ok := PrincipalFromContext(r.Context()); ok && principal.Subject != "" {
		actor = principal.Subject
	}
	a.AuditSink.AppendAudit(domain.AuditEvent{Timestamp: time.Now().UTC(), Actor: actor, Role: role(r), Action: action, Target: target, Outcome: outcome, Metadata: metadata})
}

func (a API) audit(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet || !role(r).Allows(domain.PermissionManageRBAC) || a.Audit == nil {
		writeJSON(w, http.StatusForbidden, map[string]string{"code": "forbidden"})
		return
	}
	writeJSON(w, http.StatusOK, pagePayload(a.Audit.ListAuditEvents(), r))
}
