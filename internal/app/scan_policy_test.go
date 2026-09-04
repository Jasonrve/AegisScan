package app

import (
	"testing"
	"time"

	"github.com/aegisscan/aegisscan/internal/domain"
)

func TestSelectScanMode(t *testing.T) {
	now := time.Unix(1000, 0)
	last := now.Add(-time.Minute)
	if got := SelectScanMode("", &last, now, time.Hour); got != domain.ScanIncremental {
		t.Fatalf("automatic mode = %s", got)
	}
	if got := SelectScanMode("", nil, now, time.Hour); got != domain.ScanFull {
		t.Fatalf("first automatic mode = %s", got)
	}
	if got := SelectScanMode(domain.ScanFull, &last, now, time.Hour); got != domain.ScanFull {
		t.Fatalf("explicit mode = %s", got)
	}
}
