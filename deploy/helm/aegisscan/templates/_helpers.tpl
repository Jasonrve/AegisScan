{{- define "aegisscan.name" -}}aegisscan{{- end -}}
{{- define "aegisscan.fullname" -}}{{ .Release.Name }}-aegisscan{{- end -}}
{{- define "aegisscan.authority" -}}
{{- if .Values.authentication.authority -}}{{ .Values.authentication.authority }}
{{- else if .Values.keycloak.enabled -}}http://{{ .Release.Name }}-keycloak-http.{{ .Release.Namespace }}.svc/realms/{{ .Values.keycloak.realm }}
{{- else -}}{{ required "authentication.authority is required (OIDC issuer URL) unless keycloak.enabled=true" "" }}
{{- end -}}
{{- end -}}
{{- define "aegisscan.webAuthority" -}}
{{- if .Values.web.oidc.authority -}}{{ .Values.web.oidc.authority }}
{{- else if .Values.keycloak.enabled -}}{{ required "keycloak.publicUrl is required when keycloak.enabled=true" .Values.keycloak.publicUrl | trimSuffix "/" }}/realms/{{ .Values.keycloak.realm }}
{{- else -}}{{ include "aegisscan.authority" . }}
{{- end -}}
{{- end -}}
