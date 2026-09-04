package domain

import "time"

func ShouldRunFullScan(lastFull *time.Time, now time.Time, interval time.Duration) bool {
	if interval <= 0 {
		return false
	}
	if lastFull == nil {
		return true
	}
	return !now.Before(lastFull.Add(interval))
}
