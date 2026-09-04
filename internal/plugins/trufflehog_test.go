package plugins

import (
	"context"
	"strings"
	"testing"
	"time"
)

func TestParseTruffleHogDeduplicatesAndDropsRawSecret(t *testing.T) {
	input := `{"DetectorName":"AWS","Verified":true,"Raw":"SUPER-SECRET","SourceMetadata":{"Data":{"Files":{"file":"./config/app.env","line":3}}}}` + "\n" + `{"DetectorName":"AWS","Verified":true,"Raw":"SUPER-SECRET","SourceMetadata":{"Data":{"Files":{"file":"./config/app.env","line":3}}}}`
	findings, err := ParseTruffleHog(context.Background(), strings.NewReader(input), "repo", func() time.Time { return time.Unix(10, 0) })
	if err != nil || len(findings) != 1 {
		t.Fatalf("findings = %#v, err = %v", findings, err)
	}
	if findings[0].SafePath != "config/app.env" || strings.Contains(findings[0].Fingerprint, "SUPER") {
		t.Fatalf("unsafe finding data: %#v", findings[0])
	}
}
