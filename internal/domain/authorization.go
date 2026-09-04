package domain

import "time"

type Role string

const (
	RoleAdmin    Role = "admin"
	RoleOperator Role = "operator"
	RoleViewer   Role = "viewer"
)

type Permission string

const (
	PermissionView       Permission = "view"
	PermissionConfigure  Permission = "configure"
	PermissionRun        Permission = "run"
	PermissionCancel     Permission = "cancel"
	PermissionManageRBAC Permission = "manage_rbac"
)

func (r Role) Allows(permission Permission) bool {
	switch r {
	case RoleAdmin:
		return true
	case RoleOperator:
		return permission == PermissionView || permission == PermissionRun || permission == PermissionCancel
	case RoleViewer:
		return permission == PermissionView
	default:
		return false
	}
}

type AuditEvent struct {
	Timestamp time.Time         `json:"timestamp"`
	Actor     string            `json:"actor"`
	Role      Role              `json:"role"`
	Action    string            `json:"action"`
	Target    string            `json:"target"`
	Outcome   string            `json:"outcome"`
	Metadata  map[string]string `json:"metadata,omitempty"`
}
