package domain

import "fmt"

type SettingSource string

const (
	SettingDefault   SettingSource = "default"
	SettingBootstrap SettingSource = "bootstrap"
	SettingRuntime   SettingSource = "runtime"
)

type RuntimeSettings struct {
	Source                SettingSource `json:"source"`
	MaxConcurrency        int           `json:"maxConcurrency"`
	DefaultScanMode       ScanMode      `json:"defaultScanMode"`
	FullScanSchedule      string        `json:"fullScanSchedule"`
	RetentionDays         int           `json:"retentionDays"`
	CacheRetentionGB      int           `json:"cacheRetentionGB"`
	TruffleHogImage       string        `json:"truffleHogImage"`
	RenovateImage         string        `json:"renovateImage"`
	BlockVerifiedFindings bool          `json:"blockVerifiedFindings"`
	MinimumSeverity       string        `json:"minimumSeverity"`
	RequireScanApproval   bool          `json:"requireScanApproval"`
}

func DefaultRuntimeSettings() RuntimeSettings {
	return RuntimeSettings{Source: SettingDefault, MaxConcurrency: 4, DefaultScanMode: ScanIncremental, RetentionDays: 90, CacheRetentionGB: 100}
}

func (s RuntimeSettings) Validate() error {
	if s.MaxConcurrency < 1 || s.MaxConcurrency > 100 {
		return fmt.Errorf("max concurrency must be between 1 and 100")
	}
	if s.RetentionDays < 1 {
		return fmt.Errorf("retention days must be positive")
	}
	if s.CacheRetentionGB < 1 {
		return fmt.Errorf("cache retention must be positive")
	}
	if s.DefaultScanMode != ScanIncremental && s.DefaultScanMode != ScanFull {
		return fmt.Errorf("unsupported default scan mode %q", s.DefaultScanMode)
	}
	if s.MinimumSeverity != "" && s.MinimumSeverity != "low" && s.MinimumSeverity != "medium" && s.MinimumSeverity != "high" && s.MinimumSeverity != "critical" {
		return fmt.Errorf("unsupported minimum severity %q", s.MinimumSeverity)
	}
	return nil
}
