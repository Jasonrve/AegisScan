package domain

import "testing"

func TestCommitRange(t *testing.T) {
	from, to, err := CommitRange(ScanCheckpoint{CommitSHA: "abc"}, "def", ScanIncremental)
	if err != nil || from != "abc" || to != "def" {
		t.Fatalf("range = %q..%q, err=%v", from, to, err)
	}
	if _, _, err := CommitRange(ScanCheckpoint{}, "def", ScanIncremental); err == nil {
		t.Fatal("expected missing checkpoint error")
	}
	from, to, err = CommitRange(ScanCheckpoint{CommitSHA: "abc"}, "def", ScanFull)
	if err != nil || from != "" || to != "def" {
		t.Fatalf("full range = %q..%q, err=%v", from, to, err)
	}
}
