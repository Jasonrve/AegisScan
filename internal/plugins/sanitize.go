package plugins

import "regexp"

var secretPatterns = []*regexp.Regexp{
	regexp.MustCompile(`(?i)(authorization|api[-_]?key|token|password|secret)\s*[:=]\s*(?:Bearer\s+)?[^\s,}]+`),
	regexp.MustCompile(`(?i)bearer\s+[A-Za-z0-9._-]+`),
	regexp.MustCompile(`gh[pousr]_[A-Za-z0-9]{20,}`),
	regexp.MustCompile(`github_pat_[A-Za-z0-9_]{20,}`),
	regexp.MustCompile(`://[^/\s:]+:[^/\s@]+@`),
	regexp.MustCompile(`-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----`),
}

const maxSanitizedLogBytes = 256 * 1024

func Sanitize(value string) string {
	for _, pattern := range secretPatterns {
		value = pattern.ReplaceAllString(value, "[REDACTED]")
	}
	if len(value) > maxSanitizedLogBytes {
		value = value[:maxSanitizedLogBytes] + "\n[log truncated]"
	}
	return value
}
