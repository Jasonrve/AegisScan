package domain

import (
	"testing"
	"time"
)

func TestShouldRunFullScan(t *testing.T) {
	now := time.Unix(1000, 0)
	if !ShouldRunFullScan(nil, now, time.Hour) {
		t.Fatal("first full scan should run")
	}
	last := now.Add(-time.Hour)
	if !ShouldRunFullScan(&last, now, time.Hour) {
		t.Fatal("scheduled full scan should run")
	}
	last = now.Add(-time.Minute)
	if ShouldRunFullScan(&last, now, time.Hour) {
		t.Fatal("full scan should not run early")
	}
}
