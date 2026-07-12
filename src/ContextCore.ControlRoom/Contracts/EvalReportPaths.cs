namespace ContextCore.ControlRoom;

/// <summary>
/// Eval report output paths and file names shared between ControlRoom and Evaluation.
/// Extracted from Evaluation Runner classes to break ControlRoom→Evaluation dependency.
/// </summary>
public static class EvalReportPaths
{
    // Router output directory (shared by all router runners)
    public const string RouterOutputDirectory = "learning/router";
    public const string RouterIntentBaselineReportFileName = "router-intent-baseline-report.json";
    public const string RouterShadowTraceQualityReportFileName = "router-shadow-trace-quality-report.json";
    public const string RouterDisagreementTriageA3ReportFileName = "router-disagreement-triage-a3.json";
    public const string RouterDisagreementTriageExtendedReportFileName = "router-disagreement-triage-extended.json";
    public const string RouterHardNegativesFileName = "router-hard-negatives.jsonl";
    public const string RouterGuardedOptInReadinessGateReportFileName = "router-guarded-optin-readiness-gate.json";

    // Ranker output directory (shared by all candidate reranker runners)
    public const string RankerOutputDirectory = "learning/ranker";
    public const string RankerShadowEvalA3ReportFileName = "candidate-reranker-shadow-eval-a3.json";
    public const string RankerShadowEvalExtendedReportFileName = "candidate-reranker-shadow-eval-extended.json";
    public const string RankerFeatureCompletenessA3ReportFileName = "candidate-reranker-feature-completeness-a3.json";
    public const string RankerFeatureCompletenessExtendedReportFileName = "candidate-reranker-feature-completeness-extended.json";
    public const string RankerShadowFailureAuditA3ReportFileName = "candidate-reranker-shadow-failure-audit-a3.json";
    public const string RankerShadowFailureAuditExtendedReportFileName = "candidate-reranker-shadow-failure-audit-extended.json";
    public const string RankerScoreDistributionA3ReportFileName = "candidate-reranker-score-distribution-a3.json";
    public const string RankerScoreDistributionExtendedReportFileName = "candidate-reranker-score-distribution-extended.json";
    public const string RankerListwiseCalibrationA3ReportFileName = "candidate-reranker-listwise-calibration-a3.json";
    public const string RankerListwiseCalibrationExtendedReportFileName = "candidate-reranker-listwise-calibration-extended.json";
    public const string RankerFormalPriorityAlignmentA3ReportFileName = "candidate-reranker-formal-priority-alignment-a3.json";
    public const string RankerFormalPriorityAlignmentExtendedReportFileName = "candidate-reranker-formal-priority-alignment-extended.json";
    public const string RankerShadowTraceQualityReportFileName = "candidate-reranker-shadow-trace-quality-report.json";

    // Readiness output directory
    public const string ReadinessOutputDirectory = "learning/readiness";
    public const string LearningReadinessFreezeReportFileName = "learning-readiness-freeze-report.json";
    public const string LearningRuntimeChangeReadinessGateFileName = "learning-runtime-change-readiness-gate.json";

    // Foundation output directory
    public const string FoundationOutputDirectory = "foundation";
}

