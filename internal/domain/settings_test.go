package domain

import "testing"

func TestRuntimeSettingsDefaultsAndValidation(t *testing.T) {
	settings := DefaultRuntimeSettings()
	if err := settings.Validate(); err != nil {
		t.Fatal(err)
	}
	settings.MaxConcurrency = 0
	if err := settings.Validate(); err == nil {
		t.Fatal("expected invalid concurrency")
	}
}
