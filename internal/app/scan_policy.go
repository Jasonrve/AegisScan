package app

import (
	"time"

	"github.com/aegisscan/aegisscan/internal/domain"
)

// SelectScanMode resolves an automatic policy decision. Explicit requests are
// honored; automatic runs use incremental scanning except when the periodic
// full-scan interval is due.
func SelectScanMode(requested domain.ScanMode, lastFull *time.Time, now time.Time, fullInterval time.Duration) domain.ScanMode {
	if requested == domain.ScanFull || requested == domain.ScanIncremental {
		return requested
	}
	if domain.ShouldRunFullScan(lastFull, now, fullInterval) {
		return domain.ScanFull
	}
	return domain.ScanIncremental
}
