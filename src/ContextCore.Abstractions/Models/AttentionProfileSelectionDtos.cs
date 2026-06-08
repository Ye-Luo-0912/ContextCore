namespace ContextCore.Abstractions.Models;

/// <summary>Attention profile selection report generated from baseline and extended eval reports.</summary>
public sealed class AttentionProfileSelectionReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string BaselineReportPath { get; init; } = string.Empty;

    public string ExtendedReportPath { get; init; } = string.Empty;

    public string RecommendedProfile { get; init; } = string.Empty;

    public string RecommendedMode { get; init; } = "shadow-only";

    public string RiskLevel { get; init; } = "high";

    public IReadOnlyList<string> BlockingIssues { get; init; } = Array.Empty<string>();

    public string NextAction { get; init; } = string.Empty;

    public AttentionProfileSafetyGateResult SafetyGate { get; init; } = new();

    public IReadOnlyList<AttentionProfileSelectionProfileReport> Profiles { get; init; } = Array.Empty<AttentionProfileSelectionProfileReport>();
}

/// <summary>Selection summary for one attention profile across baseline and extended eval reports.</summary>
public sealed class AttentionProfileSelectionProfileReport
{
    public string ProfileId { get; init; } = string.Empty;

    public string PolicyVersion { get; init; } = string.Empty;

    public double SelectionScore { get; init; }

    public AttentionProfileSelectionMetrics Baseline { get; init; } = new();

    public AttentionProfileSelectionMetrics Extended { get; init; } = new();

    public IReadOnlyList<AttentionProfileSelectionSampleDelta> TopImprovedSamples { get; init; } = Array.Empty<AttentionProfileSelectionSampleDelta>();

    public IReadOnlyList<AttentionProfileSelectionSampleDelta> TopRegressedSamples { get; init; } = Array.Empty<AttentionProfileSelectionSampleDelta>();

    public AttentionProfileSafetyGateResult SafetyGate { get; init; } = new();
}

/// <summary>Metrics used by profile selection for one report scope.</summary>
public sealed class AttentionProfileSelectionMetrics
{
    public string Scope { get; init; } = string.Empty;

    public int TotalSamples { get; init; }

    public double PassRate { get; init; }

    public double CurrentRecall5 { get; init; }

    public double CurrentNoiseRatio { get; init; }

    public double AttentionMrr { get; init; }

    public double AttentionRecall3 { get; init; }

    public double AttentionRecall5 { get; init; }

    public int ImprovedSamples { get; init; }

    public int RegressedSamples { get; init; }

    public int CurrentMrrOneRegressionCount { get; init; }

    public int MustNotHitPromotedCount { get; init; }

    public double SelectedSetChangeRatio { get; init; }

    public IReadOnlyList<ContextEvalAttentionProfileCategorySummary> CategoryBreakdown { get; init; } = Array.Empty<ContextEvalAttentionProfileCategorySummary>();
}

/// <summary>Per-sample profile delta used for top improved/regressed diagnostics.</summary>
public sealed class AttentionProfileSelectionSampleDelta
{
    public string Scope { get; init; } = string.Empty;

    public string ProfileId { get; init; } = string.Empty;

    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public double CurrentMrr { get; init; }

    public double AttentionMrr { get; init; }

    public double MrrDelta { get; init; }

    public double AttentionRecall3 { get; init; }

    public double AttentionRecall5 { get; init; }

    public bool WouldChangeSelectedSet { get; init; }

    public int MustHitDemotedCount { get; init; }

    public int MustNotHitPromotedCount { get; init; }

    public double SelectedSetChangeRatio { get; init; }

    public IReadOnlyList<ContextEvalAttentionCandidateDiagnostic> CandidateBreakdown { get; init; } = Array.Empty<ContextEvalAttentionCandidateDiagnostic>();
}

/// <summary>Guarded rerank safety gate result for a profile.</summary>
public sealed class AttentionProfileSafetyGateResult
{
    public bool Passed { get; init; }

    public IReadOnlyList<AttentionProfileSafetyGateCheck> Checks { get; init; } = Array.Empty<AttentionProfileSafetyGateCheck>();

    public IReadOnlyList<string> BlockingIssues { get; init; } = Array.Empty<string>();
}

/// <summary>Single safety gate check result.</summary>
public sealed class AttentionProfileSafetyGateCheck
{
    public string Code { get; init; } = string.Empty;

    public bool Passed { get; init; }

    public string Message { get; init; } = string.Empty;

    public double Actual { get; init; }

    public double Threshold { get; init; }
}
