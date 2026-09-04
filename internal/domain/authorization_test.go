package domain

import "testing"

func TestRolePermissions(t *testing.T) {
	if !RoleAdmin.Allows(PermissionManageRBAC) || !RoleOperator.Allows(PermissionRun) || RoleOperator.Allows(PermissionConfigure) || !RoleViewer.Allows(PermissionView) || RoleViewer.Allows(PermissionRun) {
		t.Fatal("role permissions do not match policy")
	}
}
