namespace ContextCore.Abstractions.Models;


/// <summary>V4 retrieval shadow readiness gate 报告；只读冻结闸门。</summary>
public sealed class VectorRetrievalShadowReadinessGateReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public bool Passed { get; init; }

    public double A3RecallAfterPolicy { get; init; }

    public int A3RiskAfterPolicy { get; init; }

    public double A3MustNotHitRiskAfterPolicy { get; init; }

    public double A3LifecycleRiskAfterPolicy { get; init; }

    public int A3FormalOutputChanged { get; init; }

    public double ExtendedRecallAfterPolicy { get; init; }

    public int ExtendedRiskAfterPolicy { get; init; }

    public double ExtendedMustNotHitRiskAfterPolicy { get; init; }

    public double ExtendedLifecycleRiskAfterPolicy { get; init; }

    public int ExtendedFormalOutputChanged { get; init; }

    public double A3FusionRecallAfterPolicy { get; init; }

    public int A3FusionRiskAfterPolicy { get; init; }

    public double A3FusionLifecycleRiskAfterPolicy { get; init; }

    public int A3FusionNewlyRiskySamples { get; init; }

    public double ExtendedFusionRecallAfterPolicy { get; init; }

    public int ExtendedFusionRiskAfterPolicy { get; init; }

    public double ExtendedFusionLifecycleRiskAfterPolicy { get; init; }

    public int ExtendedFusionNewlyRiskySamples { get; init; }

    public double A3ExpandedRecallAfterPolicy { get; init; }

    public int A3ExpandedRiskAfterPolicy { get; init; }

    public double A3ExpandedMustNotHitRiskAfterPolicy { get; init; }

    public double A3ExpandedLifecycleRiskAfterPolicy { get; init; }

    public double ExtendedExpandedRecallAfterPolicy { get; init; }

    public int ExtendedExpandedRiskAfterPolicy { get; init; }

    public double ExtendedExpandedMustNotHitRiskAfterPolicy { get; init; }

    public double ExtendedExpandedLifecycleRiskAfterPolicy { get; init; }

    public IReadOnlyDictionary<string, bool> Conditions { get; init; } =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> FailReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}


/// <summary>Qwen3 embedding provider readiness gate；不改变正式检索开关。</summary>
public sealed class VectorQwen3ReadinessGateReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public bool Passed { get; init; }

    public string ProviderId { get; init; } = "qwen3-embedding-0.6b-onnx";

    public string ProviderType { get; init; } = EmbeddingProviderTypes.OnnxLocal;

    public string ModelId { get; init; } = "qwen3-embedding-0.6b";

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }

    public int Dimension { get; init; }

    public bool UseForRuntime { get; init; }

    public bool ProviderCompatibilityPassed { get; init; }

    public double A3RecallAfterPolicy { get; init; }

    public double ExtendedRecallAfterPolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public double MustNotHitRiskAfterPolicy { get; init; }

    public double LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public int ProjectionMismatchCount { get; init; }

    public bool PgVectorFileSystemParityPassed { get; init; }

    public bool P15GatePassed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;
}


/// <summary>V3.10.F embedding provider comparison freeze；不启用 formal retrieval，不切换 preview provider。</summary>
public sealed class EmbeddingProviderComparisonFreezeReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool Passed { get; init; }

    public string ProviderId { get; init; } = "qwen3-embedding-0.6b-onnx";

    public string ModelId { get; init; } = "qwen3-embedding-0.6b";

    public string ProviderComparison { get; init; } = "Inconclusive";

    public bool ProviderConfigurationSanityPassed { get; init; }

    public string ProviderConfigurationSanityAuditPath { get; init; } = string.Empty;

    public bool ReadinessGatePassed { get; init; }

    public double A3RecallAfterPolicy { get; init; }

    public double ExtendedRecallAfterPolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public string PromotionStatus { get; init; } = EmbeddingProviderPromotionStatuses.DoNotPromote;

    public bool VectorV4RecheckAllowed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public string VectorRetrievalStatus { get; init; } = "PreviewOnly";

    public bool P15GatePassed { get; init; }

    public IReadOnlyList<string> Allowed { get; init; } = ["preview", "shadow", "eval"];

    public IReadOnlyList<string> Forbidden { get; init; } =
        ["FormalRetrievalSwitch", "PgVectorFormalRetrievalSwitch", "FormalIVectorIndexStoreBinding", "PackingPolicyIntegration", "PackageOutputIntegration"];

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}


/// <summary>hybrid retrieval preview freeze gate 报告；只冻结 preview 结论，不启用正式检索。</summary>
public sealed class HybridRetrievalPreviewFreezeReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool FreezePassed { get; init; }

    public string HybridRetrievalStatus { get; init; } = HybridRetrievalReadinessRecommendations.KeepPreviewOnly;

    public string Recommendation { get; init; } = HybridRetrievalReadinessRecommendations.KeepPreviewOnly;

    public double LegacyDenseRecallA3 { get; init; }

    public double HybridDenseOnlyRecallA3 { get; init; }

    public double HybridBestRecallA3 { get; init; }

    public double LegacyDenseRecallExtended { get; init; }

    public double HybridDenseOnlyRecallExtended { get; init; }

    public double HybridBestRecallExtended { get; init; }

    public int DenseCandidateDroppedCount { get; init; }

    public int EligibilityMismatchCount { get; init; }

    public int DedupOverwriteCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public bool V4RecheckAllowed { get; init; }

    public IReadOnlyList<string> RequiredBeforeV4 { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>单个数据集的 vector lifecycle metadata repair preview 报告。</summary>
public sealed class VectorLifecycleMetadataRepairPlanReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetName { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public int Dimension { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public int CandidateCount { get; init; }

    public int AutoRepairableCount { get; init; }

    public int HumanReviewRequiredCount { get; init; }

    public int ForbiddenRepairCount { get; init; }

    public int CorrectlyBlockedSkippedCount { get; init; }

    public double EstimatedRecallRecovery { get; init; }

    public int RiskAfterRepairEstimate { get; init; }

    public string Recommendation { get; init; } = VectorLifecycleMetadataRepairPlanRecommendations.KeepPreviewOnly;

    public IReadOnlyList<VectorLifecycleMetadataRepairCandidate> Candidates { get; init; } =
        Array.Empty<VectorLifecycleMetadataRepairCandidate>();
}


/// <summary>A3 / Extended vector lifecycle metadata repair preview 汇总。</summary>
public sealed class VectorLifecycleMetadataRepairPlanSummaryReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<VectorLifecycleMetadataRepairPlanReport> Reports { get; init; } =
        Array.Empty<VectorLifecycleMetadataRepairPlanReport>();

    public int CandidateCount { get; init; }

    public int AutoRepairableCount { get; init; }

    public int HumanReviewRequiredCount { get; init; }

    public int ForbiddenRepairCount { get; init; }

    public int CorrectlyBlockedSkippedCount { get; init; }

    public double EstimatedRecallRecovery { get; init; }

    public int RiskAfterRepairEstimate { get; init; }

    public string Recommendation { get; init; } = VectorLifecycleMetadataRepairPlanRecommendations.KeepPreviewOnly;

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }
}


/// <summary>Retrieval Dataset V2 readiness gate 报告。</summary>
public sealed class RetrievalDatasetV2ReadinessGateReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetId { get; init; } = string.Empty;

    public bool GatePassed { get; init; }

    public double RecallThreshold { get; init; }

    public double BestRecallAfterPolicy { get; init; }

    public double BestMrrAfterPolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PgVectorParityPassed { get; init; }

    public bool MaterializationGatePassed { get; init; }

    public int ValidationIssueCount { get; init; }

    public int MissingEvidenceCount { get; init; }

    public int MissingProvenanceCount { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2ShadowEvalRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>Dataset V2 stress freeze 的输出状态。</summary>
public static class RetrievalDatasetV2StressFreezeStatuses
{
    public const string ReadyForV4RecheckInput = nameof(ReadyForV4RecheckInput);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>Dataset V2 stress freeze recommendation。</summary>
public static class RetrievalDatasetV2StressFreezeRecommendations
{
    public const string ReadyForV4RecheckInput = nameof(ReadyForV4RecheckInput);
    public const string BlockedByMissingReport = nameof(BlockedByMissingReport);
    public const string BlockedByLeakage = nameof(BlockedByLeakage);
    public const string BlockedByAnchorDominance = nameof(BlockedByAnchorDominance);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByHybridScoringRisk = nameof(BlockedByHybridScoringRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByRuntimeUse = nameof(BlockedByRuntimeUse);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>Dataset V2 stress freeze gate；仅允许作为 V4 复核输入，不允许直接进入正式检索。</summary>
public sealed class RetrievalDatasetV2StressFreezeReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetId { get; init; } = string.Empty;

    public bool FreezePassed { get; init; }

    public string DatasetV2Stress { get; init; } = RetrievalDatasetV2StressFreezeStatuses.KeepPreviewOnly;

    public string BestPreviewProfile { get; init; } = string.Empty;

    public double StressRecall { get; init; }

    public double HoldoutRecall { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public int LeakageIssueCount { get; init; }

    public double AnchorDominanceScore { get; init; }

    public bool MaterializationGatePassed { get; init; }

    public bool SmallSetReadinessGatePassed { get; init; }

    public string StressReadinessRecommendation { get; init; } = string.Empty;

    public bool StressFailureTriageCompleted { get; init; }

    public bool HybridScoringRepairGatePassed { get; init; }

    public int HybridScoringRiskCandidateCount { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool V4RecheckAllowed { get; init; }

    public bool ReadyForFormalRetrieval { get; init; }

    public string Recommendation { get; init; } = RetrievalDatasetV2StressFreezeRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}


/// <summary>Formal preview freeze 状态。</summary>
public static class VectorFormalPreviewFreezeStatuses
{
    public const string ReadyForScopedOptInPreview = nameof(ReadyForScopedOptInPreview);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>Formal preview freeze recommendation。</summary>
public static class VectorFormalPreviewFreezeRecommendations
{
    public const string ReadyForScopedOptInPreview = nameof(ReadyForScopedOptInPreview);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingGate = nameof(BlockedByMissingGate);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByFormalPackageWrite = nameof(BlockedByFormalPackageWrite);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string BlockedByRuntimeChangeGate = nameof(BlockedByRuntimeChangeGate);
}


/// <summary>Formal preview freeze gate report；只冻结 preview-only 许可，不启用 runtime。</summary>
public sealed class VectorFormalPreviewFreezeReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool FreezePassed { get; init; }

    public string VectorFormalPreview { get; init; } = VectorFormalPreviewFreezeStatuses.KeepPreviewOnly;

    public string AllowedMode { get; init; } = "ScopedPreviewOnly";

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool V4ReadinessRecheckPassed { get; init; }

    public bool GuardedFormalPreviewGatePassed { get; init; }

    public bool ShadowPackageComparisonGatePassed { get; init; }

    public bool ScopedFormalPreviewOptInGatePassed { get; init; }

    public bool LimitedFormalPreviewObservationGatePassed { get; init; }

    public bool RuntimeChangeReadinessGatePassed { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool RuntimeMutated { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public IReadOnlyList<string> ForbiddenChanges { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = VectorFormalPreviewFreezeRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}


/// <summary>Explicit scoped runtime experiment planning report；只描述计划和 dry-run 边界。</summary>
public sealed class ExplicitScopedRuntimeExperimentPlanReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PlanPassed { get; init; }

    public string Recommendation { get; init; } = ExplicitScopedRuntimeExperimentRecommendations.KeepPreviewOnly;

    public string Mode { get; init; } = ExplicitScopedRuntimeExperimentModes.PlanOnly;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public int ScopeCount { get; init; }

    public int AllowlistedScopeCount { get; init; }

    public bool NonAllowlistedScopeChecked { get; init; }

    public IReadOnlyDictionary<string, string> RequiredGateSummary { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();

    public string RollbackPlan { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> ObservationMetrics { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool DryRunSupported { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}


/// <summary>Scoped runtime experiment design freeze status。</summary>
public static class ScopedRuntimeExperimentDesignFreezeStatuses
{
    public const string Frozen = nameof(Frozen);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>Scoped runtime experiment design freeze recommendation。</summary>
public static class ScopedRuntimeExperimentDesignFreezeRecommendations
{
    public const string ReadyForRuntimeExperimentProposal = nameof(ReadyForRuntimeExperimentProposal);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingDryRunObservation = nameof(BlockedByMissingDryRunObservation);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByVectorStoreBindingMutation = nameof(BlockedByVectorStoreBindingMutation);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string BlockedByMissingRollbackPlan = nameof(BlockedByMissingRollbackPlan);
}


/// <summary>Scoped runtime experiment design freeze report；冻结设计边界，不启用 runtime。</summary>
public sealed class ScopedRuntimeExperimentDesignFreezeReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool FreezePassed { get; init; }

    public string Recommendation { get; init; } =
        ScopedRuntimeExperimentDesignFreezeRecommendations.KeepPreviewOnly;

    public string DesignStatus { get; init; } = ScopedRuntimeExperimentDesignFreezeStatuses.KeepPreviewOnly;

    public string AllowedMode { get; init; } = "ExplicitScopedRuntimeExperimentOnly";

    public int AllowlistedScopeCount { get; init; }

    public int ObservationRunCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public bool RollbackPlanAvailable { get; init; }

    public bool ReadyForRuntimeExperimentProposal { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalPackageWriteAllowed { get; init; }

    public bool PackingPolicyIntegrationAllowed { get; init; }

    public bool GlobalDefaultOnAllowed { get; init; }

    public bool FoundationReleaseCandidateGatePassed { get; init; }

    public bool ServiceFoundationFreezeGatePassed { get; init; }

    public bool VectorFormalPreviewFreezeGatePassed { get; init; }

    public bool ScopedRuntimeExperimentGatePassed { get; init; }

    public bool DryRunObservationGatePassed { get; init; }

    public bool RuntimeChangeReadinessGatePassed { get; init; }

    public bool P15GatePassed { get; init; }

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}


/// <summary>V4.12 scoped runtime experiment approval gate；不授权 runtime switch。</summary>
public sealed class ScopedRuntimeExperimentApprovalGateReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; } = ScopedRuntimeExperimentApprovalRecommendations.KeepPreviewOnly;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public string ApprovalMode { get; init; } = string.Empty;

    public string ApprovedBy { get; init; } = string.Empty;

    public bool ApprovalExists { get; init; }

    public bool ApprovalExpired { get; init; }

    public bool ApprovalRevoked { get; init; }

    public bool RequiredAcknowledgementsPresent { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalPackageWriteAllowed { get; init; }

    public bool PackingPolicyIntegrationAllowed { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>V4.16 scoped runtime experiment observation freeze / promotion decision。</summary>
public static class ScopedRuntimeExperimentObservationFreezeDecisions
{
    public const string KeepScopedObservation = nameof(KeepScopedObservation);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string ReadyForFormalRetrievalIntegrationPlan = nameof(ReadyForFormalRetrievalIntegrationPlan);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByOutputChange = nameof(BlockedByOutputChange);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string BlockedByTraceGap = nameof(BlockedByTraceGap);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
}


/// <summary>V4.16 observation freeze report；只冻结主线决策，不允许 runtime/formal package 变更。</summary>
public sealed class ScopedRuntimeExperimentObservationFreezeReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool FreezePassed { get; init; }

    public string PromotionDecision { get; init; } = ScopedRuntimeExperimentObservationFreezeDecisions.KeepPreviewOnly;

    public string Recommendation { get; init; } = ScopedRuntimeExperimentObservationFreezeDecisions.KeepPreviewOnly;

    public string ObservationWindowId { get; init; } = string.Empty;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public bool V414GatePassed { get; init; }

    public bool V415GatePassed { get; init; }

    public bool RuntimeChangeGatePassed { get; init; }

    public bool P15GatePassed { get; init; }

    public int ObservationRunCount { get; init; }

    public int RequestCount { get; init; }

    public int ExperimentRouteHitCount { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool KillSwitchAvailable { get; init; }

    public bool KillSwitchSmokePassed { get; init; }

    public bool RollbackVerified { get; init; }

    public double TraceCompleteness { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>formal retrieval integration plan；只规划接入点和下一阶段，不执行接入。</summary>
public sealed class FormalRetrievalIntegrationPlanReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PlanPassed { get; init; }

    public string Recommendation { get; init; } = FormalRetrievalIntegrationPlanRecommendations.KeepPreviewOnly;

    public string AllowedMode { get; init; } = FormalRetrievalIntegrationPlanModes.PlanOnly;

    public string RequiredNextPhase { get; init; } = "ShadowFormalRetrievalAdapter";

    public bool V416PromotionDecisionPassed { get; init; }

    public bool RuntimeChangeGatePassed { get; init; }

    public bool P15GatePassed { get; init; }

    public string PromotionDecision { get; init; } = string.Empty;

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public IReadOnlyList<string> IntegrationPoints { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();

    public string FallbackPathPlan { get; init; } = string.Empty;

    public string ConfigSwitchPlan { get; init; } = string.Empty;

    public string TraceComparisonOutputPlan { get; init; } = string.Empty;

    public string RollbackPlan { get; init; } = string.Empty;

    public string KillSwitchPlan { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>shadow formal retrieval adapter plan；只定义影子 adapter 设计，不接入正式检索。</summary>
public sealed class ShadowFormalRetrievalAdapterPlanReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PlanPassed { get; init; }

    public string Recommendation { get; init; } = ShadowFormalRetrievalAdapterPlanRecommendations.KeepPreviewOnly;

    public string AllowedMode { get; init; } = "PlanOnly";

    public string RequiredNextPhase { get; init; } = "ShadowFormalRetrievalAdapterDesignFreeze";

    public IReadOnlyList<string> AdapterInputs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AdapterOutputs { get; init; } = Array.Empty<string>();

    public string VectorProviderSource { get; init; } = string.Empty;

    public string GraphCandidateSource { get; init; } = string.Empty;

    public IReadOnlyList<string> GateOrder { get; init; } = Array.Empty<string>();

    public string FallbackPath { get; init; } = string.Empty;

    public string RollbackPlan { get; init; } = string.Empty;

    public string TraceArtifactPlan { get; init; } = string.Empty;

    public string ComparisonArtifactPlan { get; init; } = string.Empty;

    public string LatencyBaselinePlan { get; init; } = string.Empty;

    public string AllocationBaselinePlan { get; init; } = string.Empty;

    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();

    public bool V50ProjectStateAuditPassed { get; init; }

    public bool V4FormalPreviewFreezeReadable { get; init; }

    public bool V416PromotionDecisionReadable { get; init; }

    public bool V414GuardedRuntimeExperimentReadable { get; init; }

    public bool V42ShadowPackageComparisonReadable { get; init; }

    public bool RuntimeChangeGatePassed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>运行时特征推导失败冻结推荐。</summary>
public static class RuntimeFeatureDerivationFailureFreezeRecommendations
{
    public const string ReadyForGraphHubNoiseControlPreview = nameof(ReadyForGraphHubNoiseControlPreview);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingRepairGate = nameof(BlockedByMissingRepairGate);
    public const string BlockedByRepairGateNotFrozen = nameof(BlockedByRepairGateNotFrozen);
}


/// <summary>运行时特征推导失败冻结报告。</summary>
public sealed class RuntimeFeatureDerivationFailureFreezeReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool FreezePassed { get; init; }

    public string Recommendation { get; init; }
        = RuntimeFeatureDerivationFailureFreezeRecommendations.KeepPreviewOnly;

    public string FrozenStatus { get; init; } = "BlockedByHubRelationNoise";

    public string RepairGateSourcePath { get; init; } = string.Empty;

    public string DerivationGateSourcePath { get; init; } = string.Empty;

    public bool CanonicalAnchorResolverReusable { get; init; } = true;

    public bool RuntimeRelationIntentDeriverReady { get; init; } = false;

    public bool CombinedRepairEvalUpperBoundOnly { get; init; } = true;

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public double V57DerivedRecall { get; init; }

    public double V57DerivedMrr { get; init; }

    public double V58TrainBaselineRecall { get; init; }

    public double V58TrainDerivedRecall { get; init; }

    public double V58TrainBaselineMrr { get; init; }

    public double V58TrainDerivedMrr { get; init; }

    public double V58HoldoutBaselineRecall { get; init; }

    public double V58HoldoutDerivedRecall { get; init; }

    public double V58HoldoutBaselineMrr { get; init; }

    public double V58HoldoutDerivedMrr { get; init; }

    public double CanonicalRelationCoverageRate { get; init; }

    public double CanonicalEvidenceCoverageRate { get; init; }

    public double CanonicalSourceCoverageRate { get; init; }

    public IReadOnlyList<string> FailureReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> DisabledCapabilities { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RecommendedNextPhases { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> FrozenArtifactPaths { get; init; } = Array.Empty<string>();
}


/// <summary>受控应用合并 dry-run 观察推荐。</summary>
public static class ControlledAppliedMergeDryRunDecisionRecommendations
{
    public const string ReadyForControlledAppliedMergeApproval = nameof(ReadyForControlledAppliedMergeApproval);
    public const string KeepDryRunOnly = nameof(KeepDryRunOnly);
    public const string BlockedByMissingProposalGate = nameof(BlockedByMissingProposalGate);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByOutputMutation = nameof(BlockedByOutputMutation);
    public const string BlockedByConstraintViolation = nameof(BlockedByConstraintViolation);
}


/// <summary>受控应用合并预览冻结推荐。</summary>
public static class ControlledAppliedMergePreviewFreezeRecommendations
{
    public const string ReadyForV6MainlineFreeze = nameof(ReadyForV6MainlineFreeze);
    public const string BlockedByMissingScopedPreviewGate = nameof(BlockedByMissingScopedPreviewGate);
}


/// <summary>受控应用合并预览冻结报告。</summary>
public sealed class ControlledAppliedMergePreviewFreezeReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool FreezePassed { get; init; }
    public string Recommendation { get; init; }
        = ControlledAppliedMergePreviewFreezeRecommendations.ReadyForV6MainlineFreeze;
    public string ScopedPreviewSourcePath { get; init; } = "";
    public int ProposalAddCount { get; init; }
    public int ProposalRemoveCount { get; init; }
    public int DryRunWouldApplyAdd { get; init; }
    public int DryRunWouldApplyRemove { get; init; }
    public int PreviewAddCount { get; init; }
    public int PreviewRemoveCount { get; init; }
    public bool ApprovalPresent { get; init; }
    public string ApprovedBy { get; init; } = "";
    public int V6PhaseCount { get; init; }
    public IReadOnlyList<string> FrozenArtifacts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>V7.0 controlled applied merge runtime preview plan 推荐。</summary>
public static class ControlledAppliedMergeRuntimePreviewPlanRecommendations
{
    public const string ReadyForRuntimePreviewActivation = nameof(ReadyForRuntimePreviewActivation);
    public const string BlockedByV6FreezeNotPassed = nameof(BlockedByV6FreezeNotPassed);
    public const string BlockedByOPTFreezeNotPassed = nameof(BlockedByOPTFreezeNotPassed);
    public const string BlockedByRuntimeChangeGateNotPassed = nameof(BlockedByRuntimeChangeGateNotPassed);
    public const string BlockedByP15NotPassed = nameof(BlockedByP15NotPassed);
    public const string BlockedByMissingKillSwitch = nameof(BlockedByMissingKillSwitch);
    public const string BlockedByMissingRollbackPlan = nameof(BlockedByMissingRollbackPlan);
    public const string BlockedByMissingObservationPlan = nameof(BlockedByMissingObservationPlan);
    public const string BlockedByMissingStopConditions = nameof(BlockedByMissingStopConditions);
    public const string BlockedByMissingAllowlistedScope = nameof(BlockedByMissingAllowlistedScope);
    public const string BlockedByRuntimeSwitchAttempt = nameof(BlockedByRuntimeSwitchAttempt);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V7.0 controlled applied merge runtime preview plan 模式。</summary>
public static class ControlledAppliedMergeRuntimePreviewPlanModes
{
    public const string PlanOnly = nameof(PlanOnly);
}


/// <summary>V7.0 controlled applied merge runtime preview plan 选项。</summary>
public sealed class ControlledAppliedMergeRuntimePreviewPlanOptions
{
    public bool Enabled { get; init; } = true;
    public string Mode { get; init; } = ControlledAppliedMergeRuntimePreviewPlanModes.PlanOnly;
    public string ConfigSwitch { get; init; } = "ControlledAppliedMergeRuntimePreview:Enabled";
    public string ApprovalMode { get; init; } = "ControlledAppliedMergePreview";
    public string TracePath { get; init; } = "vector/v7/runtime-preview-trace.jsonl";
    public int MaxRequestCount { get; init; } = 100;
    public int MaxDurationMinutes { get; init; } = 30;
    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = ["demo-workspace"];
    public IReadOnlyList<string> CollectionAllowlist { get; init; } = ["demo-collection"];
    public bool RequireKillSwitch { get; init; } = true;
    public bool RequireRollbackPlan { get; init; } = true;
    public bool RequireObservationPlan { get; init; } = true;
    public bool RequireStopConditions { get; init; } = true;
}


/// <summary>V7.0 controlled applied merge runtime preview plan 报告；只产出 plan，不实现 runtime preview。</summary>
public sealed class ControlledAppliedMergeRuntimePreviewPlanReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool PlanPassed { get; init; }
    public string Recommendation { get; init; }
        = ControlledAppliedMergeRuntimePreviewPlanRecommendations.KeepPreviewOnly;
    public string Mode { get; init; } = ControlledAppliedMergeRuntimePreviewPlanModes.PlanOnly;
    public string NextAllowedPhase { get; init; } = "ControlledAppliedMergeRuntimePreviewActivationContract";

    public bool V6FreezePassed { get; init; }
    public bool OPTFreezePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public bool P15GatePassed { get; init; }

    public IReadOnlyList<string> AllowlistedScopes { get; init; } = Array.Empty<string>();
    public string ConfigSwitch { get; init; } = "";
    public string ApprovalMode { get; init; } = "";
    public string KillSwitchPlan { get; init; } = "";
    public string RollbackPlan { get; init; } = "";
    public string TracePath { get; init; } = "";
    public IReadOnlyList<string> ObservationMetrics { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> StopConditions { get; init; } = Array.Empty<string>();
    public int MaxRequestCount { get; init; }
    public int MaxDurationMinutes { get; init; }

    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>V7.1 controlled applied merge runtime preview dry-run 推荐。</summary>
public static class ControlledAppliedMergeRuntimePreviewDryRunRecommendations
{
    public const string ReadyForRuntimePreviewGate = nameof(ReadyForRuntimePreviewGate);
    public const string BlockedByPlanNotPassed = nameof(BlockedByPlanNotPassed);
    public const string BlockedByV6FreezeNotPassed = nameof(BlockedByV6FreezeNotPassed);
    public const string BlockedByMissingProposal = nameof(BlockedByMissingProposal);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByOutputMutation = nameof(BlockedByOutputMutation);
    public const string BlockedByConstraintViolation = nameof(BlockedByConstraintViolation);
    public const string KeepDryRunOnly = nameof(KeepDryRunOnly);
}


/// <summary>V7.1 controlled applied merge runtime preview dry-run 选项。</summary>
public sealed class ControlledAppliedMergeRuntimePreviewDryRunOptions
{
    public bool Enabled { get; init; } = true;
    public int ObservationRuns { get; init; } = 3;
    public int MinObservationRuns { get; init; } = 1;
    public int MaxAddPerSample { get; init; } = 3;
    public int MaxRemovePerSample { get; init; } = 3;
    public int MaxTokenDeltaPerSample { get; init; } = 200;
    public int MaxTokenDeltaTotal { get; init; } = 4000;
    public int EstimatedTokensPerItem { get; init; } = 50;
    public double WouldApplyRatio { get; init; } = 0.5;
    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = ["demo-workspace"];
    public IReadOnlyList<string> CollectionAllowlist { get; init; } = ["demo-collection"];
}


/// <summary>V7.1 controlled applied merge runtime preview dry-run harness 报告。
/// 只计算 preview result，只写 trace/report，不改变 formal selected set，
/// 不写 formal package，不改变 package output，不改变 PackingPolicy。</summary>
public sealed class ControlledAppliedMergeRuntimePreviewDryRunReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool DryRunPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; }
        = ControlledAppliedMergeRuntimePreviewDryRunRecommendations.KeepDryRunOnly;
    public string NextAllowedPhase { get; init; } = "KeepDryRunOnly";

    public bool PlanPassed { get; init; }
    public bool V6FreezePassed { get; init; }
    public string PlanSourcePath { get; init; } = "";
    public string ProposalSourcePath { get; init; } = "";

    public IReadOnlyList<string> AllowlistedScopes { get; init; } = Array.Empty<string>();
    public string TracePath { get; init; } = "";

    public int ObservationRuns { get; init; }
    public int RequestCount { get; init; }
    public int WouldApplyAddCount { get; init; }
    public int WouldApplyRemoveCount { get; init; }
    public int AppliedAddCount { get; init; }
    public int AppliedRemoveCount { get; init; }
    public int BaselinePackageCount { get; init; }
    public int PreviewPackageCount { get; init; }
    public int TotalTokenDelta { get; init; }
    public int MaxTokenDeltaPerSample { get; init; }

    public int ScopeLeakCount { get; init; }
    public int ErrorCount { get; init; }
    public double LatencyP50Ms { get; init; }
    public double LatencyP95Ms { get; init; }

    public bool RollbackVerified { get; init; }
    public bool KillSwitchTested { get; init; }
    public bool StopConditionsChecked { get; init; }
    public bool TraceWritten { get; init; }

    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int FormalOutputChanged { get; init; }

    public bool FormalSelectedSetChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<string> ObservationMetrics { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> StopConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>V7.2 controlled applied merge runtime preview activation preflight 推荐。</summary>
public static class ControlledAppliedMergeRuntimePreviewActivationPreflightRecommendations
{
    public const string ReadyForRuntimePreviewActivation = nameof(ReadyForRuntimePreviewActivation);
    public const string BlockedByPlanNotPassed = nameof(BlockedByPlanNotPassed);
    public const string BlockedByDryRunNotPassed = nameof(BlockedByDryRunNotPassed);
    public const string BlockedByV6FreezeNotPassed = nameof(BlockedByV6FreezeNotPassed);
    public const string BlockedByMissingKillSwitch = nameof(BlockedByMissingKillSwitch);
    public const string BlockedByMissingRollbackPlan = nameof(BlockedByMissingRollbackPlan);
    public const string BlockedByMissingTraceSink = nameof(BlockedByMissingTraceSink);
    public const string BlockedByMissingAllowlistedScope = nameof(BlockedByMissingAllowlistedScope);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByFormalOutputMutation = nameof(BlockedByFormalOutputMutation);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V7.2 activation preflight 模式。</summary>
public static class ControlledAppliedMergeRuntimePreviewActivationPreflightModes
{
    public const string PreflightOnly = nameof(PreflightOnly);
}


/// <summary>V7.2 activation preflight 选项。</summary>
public sealed class ControlledAppliedMergeRuntimePreviewActivationPreflightOptions
{
    public bool Enabled { get; init; } = true;
    public string Mode { get; init; } = ControlledAppliedMergeRuntimePreviewActivationPreflightModes.PreflightOnly;
    public bool RequireKillSwitch { get; init; } = true;
    public bool RequireRollbackPlan { get; init; } = true;
    public bool RequireTraceSink { get; init; } = true;
    public bool TraceSinkAvailable { get; init; } = true;
    public bool RequireRuntimeChangeGate { get; init; } = true;
    public bool RequireP15Gate { get; init; } = true;
    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = ["demo-workspace"];
    public IReadOnlyList<string> CollectionAllowlist { get; init; } = ["demo-collection"];
}


/// <summary>V7.2 scoped runtime preview activation prelight 报告。
/// 安装/验证 runtime preview 入口，但仍保持 preview-only、scope-only、no formal output mutation。</summary>
public sealed class ControlledAppliedMergeRuntimePreviewActivationPreflightReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool PreflightPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; }
        = ControlledAppliedMergeRuntimePreviewActivationPreflightRecommendations.KeepPreviewOnly;
    public string Mode { get; init; } = ControlledAppliedMergeRuntimePreviewActivationPreflightModes.PreflightOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public bool PlanPassed { get; init; }
    public bool DryRunPassed { get; init; }
    public bool V6FreezePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public bool P15GatePassed { get; init; }

    public IReadOnlyList<string> AllowlistedScopes { get; init; } = Array.Empty<string>();
    public string ConfigSwitch { get; init; } = "";
    public string TracePath { get; init; } = "";

    public bool KillSwitchAvailable { get; init; }
    public bool RollbackPlanAvailable { get; init; }
    public bool TraceSinkAvailable { get; init; }
    public bool ConfigPatchPreviewed { get; init; }
    public bool ConfigPatchWritten { get; init; }
    public bool ScopeValidationPassed { get; init; }
    public int ScopeLeakCount { get; init; }
    public bool NonAllowlistedScopeChecked { get; init; }

    public bool FormalSelectedSetChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }

    public int WouldApplyAddCount { get; init; }
    public int WouldApplyRemoveCount { get; init; }
    public int TotalTokenDelta { get; init; }
    public int RiskAfterPolicy { get; init; }

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>V7.3 controlled applied merge runtime preview observation window 推荐。</summary>
public static class ControlledAppliedMergeRuntimePreviewObservationWindowRecommendations
{
    public const string ReadyForRuntimePreviewObservationFreeze = nameof(ReadyForRuntimePreviewObservationFreeze);
    public const string BlockedByPreflightNotPassed = nameof(BlockedByPreflightNotPassed);
    public const string BlockedByDryRunNotPassed = nameof(BlockedByDryRunNotPassed);
    public const string BlockedByConstraintViolation = nameof(BlockedByConstraintViolation);
    public const string BlockedByInstability = nameof(BlockedByInstability);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
    public const string BlockedByRollbackOrKillSwitch = nameof(BlockedByRollbackOrKillSwitch);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V7.3 observation window 选项。</summary>
public sealed class ControlledAppliedMergeRuntimePreviewObservationWindowOptions
{
    public bool Enabled { get; init; } = true;
    public int ObservationRunCount { get; init; } = 5;
    public int MinObservationRunCount { get; init; } = 3;
    public int MaxRequestCount { get; init; } = 100;
    public int MaxDurationMinutes { get; init; } = 30;
    public int MaxErrorCount { get; init; } = 0;
    public int MaxTokenDeltaTotal { get; init; } = 4000;
    public int MaxTokenDeltaPerSample { get; init; } = 200;
    public int EstimatedTokensPerItem { get; init; } = 50;
    public double WouldApplyRatio { get; init; } = 0.5;
    public int SimulatedDurationMinutes { get; init; } = 5;
    public int SimulatedErrorCount { get; init; } = 0;
    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = ["demo-workspace"];
    public IReadOnlyList<string> CollectionAllowlist { get; init; } = ["demo-collection"];
}


/// <summary>V7.3 observation window 单轮结果。</summary>
public sealed class ControlledAppliedMergeRuntimePreviewObservationRunResult
{
    public int RunIndex { get; init; }
    public bool DryRunPassed { get; init; }
    public string StableSignature { get; init; } = string.Empty;
    public int RequestCount { get; init; }
    public int WouldApplyAddCount { get; init; }
    public int WouldApplyRemoveCount { get; init; }
    public int AppliedAddCount { get; init; }
    public int AppliedRemoveCount { get; init; }
    public int TotalTokenDelta { get; init; }
    public int MaxTokenDeltaPerSample { get; init; }
    public int BaselinePackageCount { get; init; }
    public int PreviewPackageCount { get; init; }
    public int ScopeLeakCount { get; init; }
    public int ErrorCount { get; init; }
    public double LatencyP50Ms { get; init; }
    public double LatencyP95Ms { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int FormalOutputChanged { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool RollbackVerified { get; init; }
    public bool KillSwitchTested { get; init; }
    public bool StopConditionsChecked { get; init; }
    public bool TraceWritten { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool GlobalDefaultOn { get; init; }
}


/// <summary>V7.3 scoped runtime preview observation window 报告。
/// 可以跑 observation window，但仍是 scoped、preview-only、allowlisted only、discard result、trace/report only、no formal output mutation。</summary>
public sealed class ControlledAppliedMergeRuntimePreviewObservationWindowReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ObservationPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; }
        = ControlledAppliedMergeRuntimePreviewObservationWindowRecommendations.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public bool PreflightPassed { get; init; }
    public bool DryRunPassed { get; init; }
    public bool V6FreezePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public bool P15GatePassed { get; init; }

    public IReadOnlyList<string> AllowlistedScopes { get; init; } = Array.Empty<string>();
    public string TracePath { get; init; } = "";

    public int ObservationRunCount { get; init; }
    public int MinObservationRunCount { get; init; }
    public int FailedRunCount { get; init; }
    public int RequestCountTotal { get; init; }
    public int MaxRequestCount { get; init; }
    public int DurationMinutes { get; init; }
    public int MaxDurationMinutes { get; init; }
    public int ErrorCount { get; init; }
    public int MaxErrorCount { get; init; }
    public bool RequestDurationErrorWindowEnforced { get; init; }
    public bool ObservationWindowLimitEnforced { get; init; }

    public int DistinctStableSignatureCount { get; init; }
    public bool DeterministicDryRunStable { get; init; }
    public bool PreviewAddRemoveStable { get; init; }
    public int WouldApplyAddCountMin { get; init; }
    public int WouldApplyAddCountMax { get; init; }
    public int WouldApplyAddCountTotal { get; init; }
    public int WouldApplyRemoveCountMin { get; init; }
    public int WouldApplyRemoveCountMax { get; init; }
    public int WouldApplyRemoveCountTotal { get; init; }

    public int AppliedAddCountMax { get; init; }
    public int AppliedRemoveCountMax { get; init; }
    public bool AppliedDeltaZero { get; init; }

    public int TotalTokenDeltaMax { get; init; }
    public int MaxTokenDeltaPerSampleMax { get; init; }
    public bool TokenDeltaWithinBudget { get; init; }

    public int ScopeLeakCountTotal { get; init; }
    public int ErrorCountTotal { get; init; }
    public double LatencyP50MsAvg { get; init; }
    public double LatencyP95MsMax { get; init; }

    public int RiskAfterPolicyMax { get; init; }
    public int MustNotHitRiskAfterPolicyMax { get; init; }
    public int LifecycleRiskAfterPolicyMax { get; init; }
    public int FormalOutputChangedMax { get; init; }

    public bool RollbackVerified { get; init; }
    public bool KillSwitchTested { get; init; }
    public bool StopConditionsChecked { get; init; }
    public bool TraceWritten { get; init; }
    public bool ResultDiscarded { get; init; }

    public bool FormalSelectedSetChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<ControlledAppliedMergeRuntimePreviewObservationRunResult> Runs { get; init; } = Array.Empty<ControlledAppliedMergeRuntimePreviewObservationRunResult>();
    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>架构清理计划报告。</summary>
public sealed class ArchitectureCleanupPlanReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool PlanPassed { get; init; }
    public string Recommendation { get; init; }
        = ArchitectureCleanupPlanRecommendations.BlockedByMissingV6FFreeze;
    public int CoreRunnerCount { get; init; }
    public int DtoClassCount { get; init; }
    public int EvalCommandLines { get; init; }
    public int ControlRoomServiceLines { get; init; }
    public int RendererLines { get; init; }
    public int SubcommandCount { get; init; }
    public IReadOnlyList<ArchitectureCleanupItem> RecommendedMigrations { get; init; }
        = Array.Empty<ArchitectureCleanupItem>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>架构清理已完成项。</summary>
public sealed class ArchitectureCleanupCompletedItem
{
    public string Category { get; init; } = "";
    public string Result { get; init; } = "";
    public IReadOnlyList<string> Artifacts { get; init; } = Array.Empty<string>();
}


/// <summary>架构清理冻结报告；汇总 OPT-001~OPT-006 结果并冻结 ArchitectureCleanup=Frozen。</summary>
public sealed class ArchitectureCleanupFreezeReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool FreezePassed { get; init; }
    public string Recommendation { get; init; } = ArchitectureCleanupFreezeRecommendations.BlockedByMissingReports;
    public string ArchitectureCleanup { get; init; } = "Frozen";
    public string NextAllowedPhase { get; init; } = "None (ArchitectureCleanup frozen)";
    public IReadOnlyList<ArchitectureCleanupCompletedItem> CompletedItems { get; init; } = Array.Empty<ArchitectureCleanupCompletedItem>();
    public IReadOnlyList<string> RemainingDebt { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DeferredCleanupItems { get; init; } = Array.Empty<string>();
    public int TotalDtoCount { get; init; }
    public int CoreRuntimeDtoCount { get; init; }
    public int TotalRunnerCount { get; init; }
    public int RuntimeRunnerCount { get; init; }
    public int EvalRunnerCount { get; init; }
    public int GateRunnerCount { get; init; }
    public int DatasetRunnerCount { get; init; }
    public int LegacyRunnerCount { get; init; }
    public int EvalCommandMainLines { get; init; }
    public int EvalCommandFamilyTotalLines { get; init; }
    public int ControlRoomServiceLines { get; init; }
    public int RendererLines { get; init; }
    public int ControlRoomRegistryDescriptorCount { get; init; }
    public bool ArchitectureCleanupPlanPassed { get; init; }
    public bool DtoSplitPlanGenerated { get; init; }
    public bool PathHygieneGatePassed { get; init; }
    public bool P15BuildLockHardened { get; init; }
    public bool ControlRoomRegistryConsolidated { get; init; }
    public bool EvalCommandSplit { get; init; }
    public bool VectorRunnerDirectoryIsolated { get; init; }
    public bool FormalRetrievalNotEnabled { get; init; }
    public bool NoRuntimeSwitch { get; init; }
    public bool NoFormalPackageWrite { get; init; }
    public bool NoPackagePackingPolicyVectorBindingMutation { get; init; }
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>架构清理冻结 gate 报告；验证 freeze 报告的 completeness 和 compliance。</summary>
public sealed class ArchitectureCleanupFreezeGateReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = "";
    public bool FreezeReportPresent { get; init; }
    public bool FreezePassed { get; init; }
    public bool AllSubReportsAvailable { get; init; }
    public bool AllGateRulesCompliant { get; init; }
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>Scoped runtime experiment dry-run harness freeze recommendation。</summary>
public static class ScopedRuntimeExperimentHarnessFreezeRecommendations
{
    public const string ReadyForGuardedRuntimeExperimentPlanning = nameof(ReadyForGuardedRuntimeExperimentPlanning);
    public const string BlockedByMissingProposal = nameof(BlockedByMissingProposal);
    public const string BlockedByMissingApproval = nameof(BlockedByMissingApproval);
    public const string BlockedByExpiredApproval = nameof(BlockedByExpiredApproval);
    public const string BlockedByRevokedApproval = nameof(BlockedByRevokedApproval);
    public const string BlockedByUnsafeApprovalMode = nameof(BlockedByUnsafeApprovalMode);
    public const string BlockedByHarnessFailure = nameof(BlockedByHarnessFailure);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByMissingGate = nameof(BlockedByMissingGate);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>Scoped runtime experiment harness freeze report；冻结 no-op harness 设计边界，不授权 runtime switch。</summary>
public sealed class ScopedRuntimeExperimentHarnessFreezeReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool FreezePassed { get; init; }

    public string Recommendation { get; init; } = ScopedRuntimeExperimentHarnessFreezeRecommendations.KeepPreviewOnly;

    public string ProposalId { get; init; } = string.Empty;

    public string ApprovalId { get; init; } = string.Empty;

    public string ApprovalMode { get; init; } = string.Empty;

    public string HarnessStatus { get; init; } = string.Empty;

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public string AllowedMode { get; init; } = "NoOpHarnessOnly / ExplicitScopedExperimentPlanningOnly";

    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();

    public string NextAllowedPhase { get; init; } = "GuardedScopedRuntimeExperimentPlan";

    public bool ProposalGatePassed { get; init; }

    public bool ApprovalSummaryPassed { get; init; }

    public bool NoOpHarnessGatePassed { get; init; }

    public bool DesignFreezeGatePassed { get; init; }

    public bool ServiceFoundationFreezeGatePassed { get; init; }

    public bool FoundationReleaseCandidateGatePassed { get; init; }

    public bool RuntimeChangeReadinessGatePassed { get; init; }

    public bool P15GatePassed { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>V4.11 guarded scoped runtime experiment plan；不切 runtime、不写正式 package。</summary>
public sealed class GuardedScopedRuntimeExperimentPlanReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PlanPassed { get; init; }

    public string Recommendation { get; init; } = GuardedScopedRuntimeExperimentPlanRecommendations.KeepPreviewOnly;

    public string ProposalId { get; init; } = string.Empty;

    public string RequiredApprovalMode { get; init; } = ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment;

    public IReadOnlyList<string> SelectedScopes { get; init; } = Array.Empty<string>();

    public int MaxRequestCount { get; init; }

    public int MaxDurationMinutes { get; init; }

    public string KillSwitchPlan { get; init; } = string.Empty;

    public string RollbackPlan { get; init; } = string.Empty;

    public IReadOnlyList<string> ObservationPlan { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> StopConditions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();

    public bool RuntimeSwitchAllowed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}


/// <summary>vector lifecycle metadata review 决策类型。</summary>
public static class VectorLifecycleMetadataReviewDecisions
{
    public const string ApproveForSidecar = nameof(ApproveForSidecar);
    public const string Reject = nameof(Reject);
    public const string NeedsEvidence = nameof(NeedsEvidence);
    public const string Supersede = nameof(Supersede);
}

/// <summary>hybrid retrieval readiness gate 报告；FormalRetrievalAllowed 恒 false。</summary>
public sealed class HybridRetrievalReadinessGateReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Passed { get; init; }
    public double A3RecallAfterPolicy { get; init; }
    public double ExtendedRecallAfterPolicy { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int FormalOutputChanged { get; init; }
    public bool PolicyViolationFound { get; init; }
    public bool P15GatePassed { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public IReadOnlyList<string> Allowed { get; init; } = ["preview", "shadow", "eval"];
    public IReadOnlyList<string> Forbidden { get; init; } = ["FormalRetrievalSwitch", "FormalIVectorIndexStoreBinding", "PackingPolicyIntegration", "PackageOutputIntegration"];
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    public string Recommendation { get; init; } = HybridRetrievalReadinessRecommendations.KeepPreviewOnly;
}
