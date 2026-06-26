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


/// <summary>V7.3R observation hardening 推荐。</summary>
public static class ControlledAppliedMergeRuntimePreviewObservationHardeningRecommendations
{
    public const string ReadyForRuntimePreviewObservationFreeze = nameof(ReadyForRuntimePreviewObservationFreeze);
    public const string BlockedByObservationWindowNotPassed = nameof(BlockedByObservationWindowNotPassed);
    public const string BlockedByInsufficientRuns = nameof(BlockedByInsufficientRuns);
    public const string BlockedByInsufficientRequests = nameof(BlockedByInsufficientRequests);
    public const string BlockedByDurationExceeded = nameof(BlockedByDurationExceeded);
    public const string BlockedByErrorCount = nameof(BlockedByErrorCount);
    public const string BlockedByRouteHitViolation = nameof(BlockedByRouteHitViolation);
    public const string BlockedByTraceIntegrity = nameof(BlockedByTraceIntegrity);
    public const string BlockedByStabilitySignature = nameof(BlockedByStabilitySignature);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V7.3R observation hardening 选项。</summary>
public sealed class ControlledAppliedMergeRuntimePreviewObservationHardeningOptions
{
    public bool Enabled { get; init; } = true;
    public int MinObservationRunCount { get; init; } = 10;
    public int MinRequestCountTotal { get; init; } = 120;
    public int MaxDurationMinutes { get; init; } = 30;
    public int MaxErrorCount { get; init; } = 0;
    public int RequestsPerRun { get; init; } = 12;
    public int EstimatedTokensPerItem { get; init; } = 50;
    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = ["demo-workspace"];
    public IReadOnlyList<string> CollectionAllowlist { get; init; } = ["demo-collection"];
    public IReadOnlyList<string> RequestKinds { get; init; } = ["chat", "project", "coding", "novel", "automation"];
}


/// <summary>V7.3R observation hardening 单轮结果。</summary>
public sealed class ControlledAppliedMergeRuntimePreviewObservationHardeningRunResult
{
    public int RunIndex { get; init; }
    public bool DryRunPassed { get; init; }
    public string StableSignature { get; init; } = string.Empty;
    public string Scope { get; init; } = "";
    public string RequestKind { get; init; } = "";
    public int RequestCount { get; init; }
    public int WouldApplyAddCount { get; init; }
    public int WouldApplyRemoveCount { get; init; }
    public int TotalTokenDelta { get; init; }
    public string SelectedCandidateIdsHash { get; init; } = "";
    public string TracePayloadHash { get; init; } = "";
    public int AllowlistedPreviewRouteHitCount { get; init; }
    public int NonAllowlistedPreviewRouteHitCount { get; init; }
    public int KillSwitchPreviewRouteHitCount { get; init; }
    public int NonAllowlistedNoOpCount { get; init; }
    public int KillSwitchNoOpCount { get; init; }
    public double TraceCompletenessPercent { get; init; }
    public bool TracePayloadStable { get; init; }
    public bool TraceReplayable { get; init; }
    public int AppliedAddCount { get; init; }
    public int AppliedRemoveCount { get; init; }
    public int ErrorCount { get; init; }
    public int RiskAfterPolicy { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool RollbackVerified { get; init; }
    public bool KillSwitchTested { get; init; }
}


/// <summary>V7.3R runtime preview observation hardening 报告。
/// 补强 V7.3 observation 使其达到 freeze-ready 证据强度。
/// 不改变 formal output，不启用 runtime switch。</summary>
public sealed class ControlledAppliedMergeRuntimePreviewObservationHardeningReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool HardeningPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; }
        = ControlledAppliedMergeRuntimePreviewObservationHardeningRecommendations.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public bool ObservationWindowPassed { get; init; }
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
    public int MinRequestCountTotal { get; init; }
    public int DurationMinutes { get; init; }
    public int MaxDurationMinutes { get; init; }
    public int ErrorCountTotal { get; init; }
    public int MaxErrorCount { get; init; }

    public int AllowlistedPreviewRouteHitCountTotal { get; init; }
    public int NonAllowlistedPreviewRouteHitCountTotal { get; init; }
    public int KillSwitchPreviewRouteHitCountTotal { get; init; }
    public int NonAllowlistedNoOpCountTotal { get; init; }
    public int KillSwitchNoOpCountTotal { get; init; }

    public double TraceCompletenessPercent { get; init; }
    public bool TracePayloadStable { get; init; }
    public bool TraceReplayable { get; init; }

    public int DistinctStableSignatureCount { get; init; }
    public bool DeterministicStable { get; init; }
    public bool SelectedCandidateIdsStable { get; init; }
    public bool TracePayloadHashStable { get; init; }

    public int WouldApplyAddCountMin { get; init; }
    public int WouldApplyAddCountMax { get; init; }
    public int WouldApplyRemoveCountMin { get; init; }
    public int WouldApplyRemoveCountMax { get; init; }
    public int AppliedAddCountMax { get; init; }
    public int AppliedRemoveCountMax { get; init; }
    public bool AppliedDeltaZero { get; init; }

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

    public IReadOnlyList<ControlledAppliedMergeRuntimePreviewObservationHardeningRunResult> Runs { get; init; } = Array.Empty<ControlledAppliedMergeRuntimePreviewObservationHardeningRunResult>();
    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>V7.4 runtime preview observation freeze 推荐。</summary>
public static class ControlledAppliedMergeRuntimePreviewObservationFreezeRecommendations
{
    public const string ReadyForScopedRuntimePreviewApprovalPlan = nameof(ReadyForScopedRuntimePreviewApprovalPlan);
    public const string BlockedByMissingPrerequisites = nameof(BlockedByMissingPrerequisites);
    public const string BlockedByObservationHardeningNotPassed = nameof(BlockedByObservationHardeningNotPassed);
    public const string BlockedByObservationWindowNotPassed = nameof(BlockedByObservationWindowNotPassed);
    public const string BlockedByPreflightNotPassed = nameof(BlockedByPreflightNotPassed);
    public const string BlockedByDryRunNotPassed = nameof(BlockedByDryRunNotPassed);
    public const string BlockedByFreezeMetricsViolation = nameof(BlockedByFreezeMetricsViolation);
    public const string BlockedBySafetyBoundaryViolation = nameof(BlockedBySafetyBoundaryViolation);
    public const string BlockedByTestBaselineMismatch = nameof(BlockedByTestBaselineMismatch);
    public const string BlockedByGateFailure = nameof(BlockedByGateFailure);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V7.4 observation freeze 选项。</summary>
public sealed class ControlledAppliedMergeRuntimePreviewObservationFreezeOptions
{
    public bool Enabled { get; init; } = true;
    public int TestCountBaseline { get; init; } = 1452;
    public int MinObservationRunCount { get; init; } = 10;
    public int MinRequestCountTotal { get; init; } = 120;
    public int MaxErrorCount { get; init; } = 0;
    public double MinTraceCompletenessPercent { get; init; } = 100.0;
}


/// <summary>V7.4 observation freeze 汇总指标。</summary>
public sealed class ControlledAppliedMergeRuntimePreviewObservationFreezeMetrics
{
    public int ObservationRunCount { get; init; }
    public int RequestCountTotal { get; init; }
    public int ErrorCountTotal { get; init; }
    public int AllowlistedPreviewRouteHitCount { get; init; }
    public int NonAllowlistedPreviewRouteHitCount { get; init; }
    public int KillSwitchPreviewRouteHitCount { get; init; }
    public int NonAllowlistedNoOpCount { get; init; }
    public int KillSwitchNoOpCount { get; init; }
    public double TraceCompletenessPercent { get; init; }
    public bool TracePayloadStable { get; init; }
    public bool TraceReplayable { get; init; }
    public bool DeterministicStable { get; init; }
    public int WouldApplyAddCount { get; init; }
    public int WouldApplyRemoveCount { get; init; }
    public int AppliedAddCount { get; init; }
    public int AppliedRemoveCount { get; init; }
    public bool AppliedDeltaZero { get; init; }
    public bool ResultDiscarded { get; init; }
}


/// <summary>V7.4 runtime preview observation freeze 报告。
/// 冻结 V7 runtime preview observation 结果并做 promotion decision。
/// 不启用 formal retrieval，不切 runtime，不写正式 package。</summary>
public sealed class ControlledAppliedMergeRuntimePreviewObservationFreezeReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool FreezePassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; }
        = ControlledAppliedMergeRuntimePreviewObservationFreezeRecommendations.KeepPreviewOnly;
    public string PromotionDecision { get; init; }
        = ControlledAppliedMergeRuntimePreviewObservationFreezeRecommendations.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public bool V7PlanPresent { get; init; }
    public bool V7DryRunPresent { get; init; }
    public bool V7PreflightPresent { get; init; }
    public bool V7ObservationPresent { get; init; }
    public bool V7HardeningPresent { get; init; }
    public bool V6FreezePresent { get; init; }
    public bool OptFreezePresent { get; init; }
    public bool RuntimeChangeGatePresent { get; init; }
    public bool P15GatePassed { get; init; }

    public bool V7PlanPassed { get; init; }
    public bool V7DryRunPassed { get; init; }
    public bool V7PreflightPassed { get; init; }
    public bool V7ObservationPassed { get; init; }
    public bool V7HardeningPassed { get; init; }
    public bool V6FreezePassed { get; init; }
    public bool OptFreezePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }

    public ControlledAppliedMergeRuntimePreviewObservationFreezeMetrics? HardeningMetrics { get; init; }

    public int TestCountBaseline { get; init; }
    public int CurrentTestCount { get; init; }
    public int TestCountDelta { get; init; }
    public bool TestBaselineFrozen { get; init; }

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

    public IReadOnlyList<string> FrozenMetrics { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SafetyBoundaries { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>V7.5 scoped runtime preview approval plan 推荐。</summary>
public static class ScopedRuntimePreviewApprovalPlanRecommendations
{
    public const string ReadyForScopedRuntimePreviewAuthorization = nameof(ReadyForScopedRuntimePreviewAuthorization);
    public const string BlockedByMissingFreeze = nameof(BlockedByMissingFreeze);
    public const string BlockedByApprovalPlanIncomplete = nameof(BlockedByApprovalPlanIncomplete);
    public const string BlockedByKillSwitchUnavailable = nameof(BlockedByKillSwitchUnavailable);
    public const string BlockedByRollbackUnavailable = nameof(BlockedByRollbackUnavailable);
    public const string BlockedByRevocationUndefined = nameof(BlockedByRevocationUndefined);
    public const string BlockedByTraceRetentionUnconfigured = nameof(BlockedByTraceRetentionUnconfigured);
    public const string BlockedBySafetyBoundaryViolation = nameof(BlockedBySafetyBoundaryViolation);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V7.5 scoped runtime preview approval plan 选项。</summary>
public sealed class ScopedRuntimePreviewApprovalPlanOptions
{
    public bool Enabled { get; init; } = true;
    public string ApprovalAuthority { get; init; } = "ArchitectureReviewBoard";
    public int ValidityDurationDays { get; init; } = 30;
    public int KillSwitchResponseTimeSeconds { get; init; } = 60;
    public int RollbackMaxDurationMinutes { get; init; } = 15;
    public int TraceRetentionDays { get; init; } = 90;
    public IReadOnlyList<string> ApprovedScopes { get; init; } = ["demo-workspace/demo-collection"];
    public IReadOnlyList<string> AuthorizedApprovers { get; init; } = ["ReleaseManager", "ArchitectureLead"];
}


/// <summary>V7.5 scoped runtime preview approval plan 报告。
/// 生成 approval plan / approval gate，明确审批主体、scope、有效期、撤销机制、kill switch、rollback、trace retention。
/// 不启用 runtime activation。不切 runtime switch。不写 formal package。</summary>
public sealed class ScopedRuntimePreviewApprovalPlanReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool PlanPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; }
        = ScopedRuntimePreviewApprovalPlanRecommendations.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public string ApprovalPlanId { get; init; } = "";
    public string ApprovalAuthority { get; init; } = "";
    public IReadOnlyList<string> AuthorizedApprovers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ApprovedScopes { get; init; } = Array.Empty<string>();
    public DateTimeOffset ValidityNotBefore { get; init; }
    public DateTimeOffset ValidityNotAfter { get; init; }
    public int ValidityDurationDays { get; init; }
    public bool ValidityWithinBounds { get; init; }

    public string RevocationMechanism { get; init; } = "";
    public bool RevocationRequiresMajority { get; init; }
    public IReadOnlyList<string> RevocationTriggers { get; init; } = Array.Empty<string>();
    public string EmergencyRevocationContact { get; init; } = "";

    public string KillSwitchPlan { get; init; } = "";
    public string KillSwitchAction { get; init; } = "";
    public int KillSwitchResponseTimeSeconds { get; init; }
    public bool KillSwitchTested { get; init; }
    public bool KillSwitchConfigured { get; init; }

    public string RollbackPlan { get; init; } = "";
    public int RollbackMaxDurationMinutes { get; init; }
    public bool RollbackVerified { get; init; }
    public string RollbackCheckpointStrategy { get; init; } = "";
    public bool RollbackConfigured { get; init; }

    public string TraceRetentionPolicy { get; init; } = "";
    public int TraceRetentionDays { get; init; }
    public string TraceStoragePath { get; init; } = "";
    public bool TraceRetentionConfigured { get; init; }

    public bool V7FreezePassed { get; init; }
    public bool V7HardeningPassed { get; init; }
    public bool V6FreezePassed { get; init; }
    public bool OptFreezePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public bool P15GatePassed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>V7.6 scoped runtime preview authorization 推荐。</summary>
public static class ScopedRuntimePreviewAuthorizationRecommendations
{
    public const string ReadyForScopedRuntimePreviewActivationPreparation = nameof(ReadyForScopedRuntimePreviewActivationPreparation);
    public const string BlockedByMissingAuthorizationRecord = nameof(BlockedByMissingAuthorizationRecord);
    public const string BlockedByExpiredAuthorization = nameof(BlockedByExpiredAuthorization);
    public const string BlockedByWrongScope = nameof(BlockedByWrongScope);
    public const string BlockedByMissingForbiddenActionAcknowledgement = nameof(BlockedByMissingForbiddenActionAcknowledgement);
    public const string BlockedByKillSwitchNotAcknowledged = nameof(BlockedByKillSwitchNotAcknowledged);
    public const string BlockedByRollbackNotAcknowledged = nameof(BlockedByRollbackNotAcknowledged);
    public const string BlockedByTraceRetentionNotAcknowledged = nameof(BlockedByTraceRetentionNotAcknowledged);
    public const string BlockedByApprovalPlanNotPassed = nameof(BlockedByApprovalPlanNotPassed);
    public const string BlockedByFreezeNotPassed = nameof(BlockedByFreezeNotPassed);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V7.6 scoped runtime preview authorization 选项。</summary>
public sealed class ScopedRuntimePreviewAuthorizationOptions
{
    public bool Enabled { get; init; } = true;
    public string ApprovedBy { get; init; } = "ReleaseManager";
    public string ApprovalAuthority { get; init; } = "ArchitectureReviewBoard";
    public IReadOnlyList<string> ForbiddenActionsToAcknowledge { get; init; } =
    [
        "GlobalDefaultOn",
        "FormalRetrievalEnable",
        "FormalPackageWrite",
        "PackingPolicyMutation",
        "PackageOutputMutation",
        "VectorStoreBindingMutation",
        "RuntimeSwitch",
        "RuntimeActivation",
        "WriteConfigPatch",
        "ApplyPreviewResult",
        "ChangeFormalSelectedSet",
        "MutateApprovalPlanAfterAuthorization",
        "ChangeApprovedScopesAfterAuthorization",
        "OverrideValidityWindow",
        "SkipForbiddenActionAcknowledgement",
    ];
}


/// <summary>V7.6 scoped runtime preview authorization 报告。
/// 生成并验证 Scoped Runtime Preview Authorization Record。
/// 不启用 runtime activation，不写 config patch，不切 runtime switch，不写 formal package。</summary>
public sealed class ScopedRuntimePreviewAuthorizationReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Authorized { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; }
        = ScopedRuntimePreviewAuthorizationRecommendations.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public string ApprovalId { get; init; } = "";
    public string ApprovalPlanId { get; init; } = "";
    public string ApprovedBy { get; init; } = "";
    public string ApprovalAuthority { get; init; } = "";
    public IReadOnlyList<string> ApprovedScopes { get; init; } = Array.Empty<string>();
    public DateTimeOffset ValidityNotBefore { get; init; }
    public DateTimeOffset ValidityNotAfter { get; init; }
    public int RemainingValidityDays { get; init; }
    public bool ValidityValid { get; init; }

    public IReadOnlyList<string> AcknowledgedForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> UnacknowledgedForbiddenActions { get; init; } = Array.Empty<string>();
    public bool KillSwitchAcknowledged { get; init; }
    public bool RollbackAcknowledged { get; init; }
    public bool TraceRetentionAcknowledged { get; init; }
    public bool AllForbiddenActionsAcknowledged { get; init; }

    public bool V7ApprovalPlanPassed { get; init; }
    public bool V7FreezePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public bool P15GatePassed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>V7.6R authorization hardening 推荐。</summary>
public static class ScopedRuntimePreviewAuthorizationHardeningRecommendations
{
    public const string ReadyForActivationPreparation = nameof(ReadyForActivationPreparation);
    public const string BlockedByMissingApprovedBy = nameof(BlockedByMissingApprovedBy);
    public const string BlockedByUnacknowledgedForbiddenActions = nameof(BlockedByUnacknowledgedForbiddenActions);
    public const string BlockedByPartialForbiddenAcknowledgement = nameof(BlockedByPartialForbiddenAcknowledgement);
    public const string BlockedByExpiredAuthorization = nameof(BlockedByExpiredAuthorization);
    public const string BlockedByWrongScope = nameof(BlockedByWrongScope);
    public const string BlockedByAuthorizationNotPassed = nameof(BlockedByAuthorizationNotPassed);
    public const string BlockedBySafetyBoundaryViolation = nameof(BlockedBySafetyBoundaryViolation);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V7.6R authorization hardening 选项。</summary>
public sealed class ScopedRuntimePreviewAuthorizationHardeningOptions
{
    public bool Enabled { get; init; } = true;
    public string ApprovedBy { get; init; } = "ReleaseManager";
    public bool RequireExplicitApprovedBy { get; init; } = true;
    public bool ExplicitlyProvided { get; init; }
    public IReadOnlyList<string> RequiredForbiddenActions { get; init; } =
    [
        "GlobalDefaultOn",
        "FormalRetrievalEnable",
        "FormalPackageWrite",
        "PackingPolicyMutation",
        "PackageOutputMutation",
        "VectorStoreBindingMutation",
        "RuntimeSwitch",
        "RuntimeActivation",
        "WriteConfigPatch",
        "ApplyPreviewResult",
        "ChangeFormalSelectedSet",
        "MutateApprovalPlanAfterAuthorization",
        "ChangeApprovedScopesAfterAuthorization",
        "OverrideValidityWindow",
        "SkipForbiddenActionAcknowledgement",
    ];
}


/// <summary>V7.6R authorization hardening negative test 结果。</summary>
public sealed class ScopedRuntimePreviewAuthorizationHardeningNegativeTest
{
    public string TestName { get; init; } = "";
    public string Scenario { get; init; } = "";
    public bool ExpectedBlocked { get; init; }
    public bool ActuallyBlocked { get; init; }
    public bool Passed { get; init; }
    public string BlockedReason { get; init; } = "";
    public string Detail { get; init; } = "";
}


/// <summary>V7.6R scoped runtime preview authorization hardening 报告。
/// 硬化 authorization gate：显式 --approved-by、全量 forbidden 确认、negative tests。
/// 不启用 runtime activation。</summary>
public sealed class ScopedRuntimePreviewAuthorizationHardeningReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool HardeningPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; }
        = ScopedRuntimePreviewAuthorizationHardeningRecommendations.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public bool AuthorizationPassed { get; init; }
    public bool ApprovalPlanPassed { get; init; }
    public bool V7FreezePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public bool P15GatePassed { get; init; }

    public string ApprovedBy { get; init; } = "";
    public bool ExplicitApprovedByProvided { get; init; }

    public IReadOnlyList<string> AcknowledgedForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> UnacknowledgedForbiddenActions { get; init; } = Array.Empty<string>();
    public int RequiredForbiddenActionCount { get; init; }
    public int AcknowledgedCount { get; init; }
    public int UnacknowledgedCount { get; init; }
    public bool AllForbiddenAcknowledged { get; init; }

    public int NegativeTestTotal { get; init; }
    public int NegativeTestPassed { get; init; }
    public int NegativeTestFailed { get; init; }
    public IReadOnlyList<ScopedRuntimePreviewAuthorizationHardeningNegativeTest> NegativeTests { get; init; } = Array.Empty<ScopedRuntimePreviewAuthorizationHardeningNegativeTest>();

    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>V7.7 scoped runtime preview activation preparation 推荐。</summary>
public static class ScopedRuntimePreviewActivationPreparationRecommendations
{
    public const string ReadyForScopedRuntimePreviewActivation = nameof(ReadyForScopedRuntimePreviewActivation);
    public const string BlockedByMissingAuthorization = nameof(BlockedByMissingAuthorization);
    public const string BlockedByMissingHardening = nameof(BlockedByMissingHardening);
    public const string BlockedByMissingApprovalPlan = nameof(BlockedByMissingApprovalPlan);
    public const string BlockedByMissingFreeze = nameof(BlockedByMissingFreeze);
    public const string BlockedByExpiredAuthorization = nameof(BlockedByExpiredAuthorization);
    public const string BlockedByScopeMismatch = nameof(BlockedByScopeMismatch);
    public const string BlockedByKillSwitchUnavailable = nameof(BlockedByKillSwitchUnavailable);
    public const string BlockedByRollbackUnavailable = nameof(BlockedByRollbackUnavailable);
    public const string BlockedByTraceRetentionUnconfigured = nameof(BlockedByTraceRetentionUnconfigured);
    public const string BlockedByForbiddenActionNotAcknowledged = nameof(BlockedByForbiddenActionNotAcknowledged);
    public const string BlockedByApprovedByMissing = nameof(BlockedByApprovedByMissing);
    public const string BlockedBySafetyBoundaryViolation = nameof(BlockedBySafetyBoundaryViolation);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V7.7 activation preparation 选项。</summary>
public sealed class ScopedRuntimePreviewActivationPreparationOptions
{
    public bool Enabled { get; init; } = true;
    public string ApprovedBy { get; init; } = "ReleaseManager";
    public bool ExplicitlyProvided { get; init; }
    public int MaxObservationsBeforeActivation { get; init; } = 3;
    public int ObservationIntervalSeconds { get; init; } = 30;
    public int MaxObservationDurationMinutes { get; init; } = 5;
}


/// <summary>V7.7 scoped runtime preview activation preparation 报告。
/// 准备 activation contract 和 pre-activation artifacts。
/// 不启用 runtime activation。ConfigPatchWritten=false。</summary>
public sealed class ScopedRuntimePreviewActivationPreparationReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool PreparationPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; }
        = ScopedRuntimePreviewActivationPreparationRecommendations.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public string ActivationPreparationId { get; init; } = "";
    public string AuthorizationId { get; init; } = "";
    public string ApprovalPlanId { get; init; } = "";
    public IReadOnlyList<string> ApprovedScopes { get; init; } = Array.Empty<string>();
    public DateTimeOffset ValidityNotBefore { get; init; }
    public DateTimeOffset ValidityNotAfter { get; init; }
    public bool AuthorizationValid { get; init; }

    public string KillSwitchProbePlan { get; init; } = "";
    public bool KillSwitchConfigured { get; init; }
    public string RollbackCheckpointPlan { get; init; } = "";
    public bool RollbackConfigured { get; init; }
    public string TraceSinkPlan { get; init; } = "";
    public bool TraceRetentionConfigured { get; init; }
    public string ConfigPatchPreview { get; init; } = "";
    public bool ConfigPatchWritten { get; init; }
    public string ObservationStartPlan { get; init; } = "";
    public int MaxObservationsBeforeActivation { get; init; }
    public IReadOnlyList<string> StopConditions { get; init; } = Array.Empty<string>();

    public bool AuthorizationPassed { get; init; }
    public bool AuthorizationHardeningPassed { get; init; }
    public bool ApprovalPlanPassed { get; init; }
    public bool ObservationFreezePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public bool P15GatePassed { get; init; }

    public bool ExplicitApprovedByPresent { get; init; }
    public bool AllForbiddenActionsAcknowledged { get; init; }
    public bool ApprovedScopesUnchanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool WriteConfigPatch { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>V7.8 activation dry-run 推荐。</summary>
public static class ScopedRuntimePreviewActivationDryRunRecommendations
{
    public const string ReadyForScopedRuntimePreviewActivationWindow = nameof(ReadyForScopedRuntimePreviewActivationWindow);
    public const string BlockedByMissingPreparation = nameof(BlockedByMissingPreparation);
    public const string BlockedByContractParseFailure = nameof(BlockedByContractParseFailure);
    public const string BlockedByScopeRoutingFailure = nameof(BlockedByScopeRoutingFailure);
    public const string BlockedByKillSwitchFailure = nameof(BlockedByKillSwitchFailure);
    public const string BlockedByRollbackCheckpointFailure = nameof(BlockedByRollbackCheckpointFailure);
    public const string BlockedByTraceSinkFailure = nameof(BlockedByTraceSinkFailure);
    public const string BlockedByConfigPatchWritten = nameof(BlockedByConfigPatchWritten);
    public const string BlockedByRuntimeActivationDetected = nameof(BlockedByRuntimeActivationDetected);
    public const string BlockedBySafetyBoundaryViolation = nameof(BlockedBySafetyBoundaryViolation);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V7.8 activation dry-run 单轮结果。</summary>
public sealed class ScopedRuntimePreviewActivationDryRunResult
{
    public int RunIndex { get; init; }
    public string Scope { get; init; } = "";
    public bool ContractParseable { get; init; }
    public bool ScopeHit { get; init; }
    public bool IsNoOp { get; init; }
    public bool KillSwitchTripped { get; init; }
    public bool RollbackAvailable { get; init; }
    public bool TraceSinkWritable { get; init; }
    public bool ConfigPatchPreviewOnly { get; init; }
    public bool RuntimeActivationRemainsFalse { get; init; }
    public int WouldApplyAdd { get; init; }
    public int WouldApplyRemove { get; init; }
    public int ActualAppliedAdd { get; init; }
    public int ActualAppliedRemove { get; init; }
    public int ErrorCount { get; init; }
    public string Detail { get; init; } = "";
}


/// <summary>V7.8 activation dry-run 选项。</summary>
public sealed class ScopedRuntimePreviewActivationDryRunOptions
{
    public bool Enabled { get; init; } = true;
    public int DryRunCount { get; init; } = 5;
    public bool ExplicitlyProvided { get; init; }
    public string ApprovedBy { get; init; } = "ReleaseManager";
    public IReadOnlyList<string> ApprovedScopes { get; init; } = ["demo-workspace/demo-collection"];
    public IReadOnlyList<string> NonApprovedScopes { get; init; } = ["rogue-workspace/rogue-collection", "unauthorized/scope"];
}


/// <summary>V7.8 scoped runtime preview activation dry-run 报告。
/// No-op harness：验证 activation contract 而不启用 runtime activation。
/// ConfigPatchWritten=false, RuntimeActivation=false。</summary>
public sealed class ScopedRuntimePreviewActivationDryRunReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool DryRunPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; }
        = ScopedRuntimePreviewActivationDryRunRecommendations.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public bool ContractParseable { get; init; }
    public int TotalRuns { get; init; }
    public int PassedRuns { get; init; }
    public int ApprovedScopeHits { get; init; }
    public int NonApprovedScopeNoOps { get; init; }
    public int KillSwitchNoOpCount { get; init; }
    public int AppliedAddTotal { get; init; }
    public int AppliedRemoveTotal { get; init; }
    public bool AppliedDeltaZero { get; init; }
    public bool ConfigPatchWritten { get; init; }
    public bool RuntimeActivation { get; init; }

    public bool PreparationPassed { get; init; }
    public bool AuthorizationPassed { get; init; }
    public bool P15GatePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<ScopedRuntimePreviewActivationDryRunResult> Runs { get; init; } = Array.Empty<ScopedRuntimePreviewActivationDryRunResult>();
    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>V7.9 activation window preflight 推荐。</summary>
public static class ScopedRuntimePreviewActivationWindowPreflightRecommendations
{
    public const string ReadyForScopedRuntimePreviewActivationWindow = nameof(ReadyForScopedRuntimePreviewActivationWindow);
    public const string BlockedByMissingPreparation = nameof(BlockedByMissingPreparation);
    public const string BlockedByMissingDryRun = nameof(BlockedByMissingDryRun);
    public const string BlockedByScopeMismatch = nameof(BlockedByScopeMismatch);
    public const string BlockedByWindowDurationExceeded = nameof(BlockedByWindowDurationExceeded);
    public const string BlockedByRequestCapMissing = nameof(BlockedByRequestCapMissing);
    public const string BlockedByKillSwitchNotVerified = nameof(BlockedByKillSwitchNotVerified);
    public const string BlockedByRollbackCheckpointUnavailable = nameof(BlockedByRollbackCheckpointUnavailable);
    public const string BlockedByTraceSinkUnavailable = nameof(BlockedByTraceSinkUnavailable);
    public const string BlockedByConfigPatchNotPreviewOnly = nameof(BlockedByConfigPatchNotPreviewOnly);
    public const string BlockedByStopConditionsMissing = nameof(BlockedByStopConditionsMissing);
    public const string BlockedByAuthorizationExpired = nameof(BlockedByAuthorizationExpired);
    public const string BlockedBySafetyBoundaryViolation = nameof(BlockedBySafetyBoundaryViolation);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V7.9 preflight 选项。</summary>
public sealed class ScopedRuntimePreviewActivationWindowPreflightOptions
{
    public bool Enabled { get; init; } = true;
    public bool ExplicitlyProvided { get; init; }
    public string ApprovedBy { get; init; } = "ReleaseManager";
    public int MaxWindowDurationMinutes { get; init; } = 30;
    public int MaxRequestsPerWindow { get; init; } = 100;
    public int MinStopConditions { get; init; } = 5;
}


/// <summary>V7.9 scoped runtime preview activation window preflight 报告。
/// 为 activation window 做启动前预检，验证所有条件就绪。
/// ConfigPatchWritten=false, RuntimeActivation=false。</summary>
public sealed class ScopedRuntimePreviewActivationWindowPreflightReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool PreflightPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; }
        = ScopedRuntimePreviewActivationWindowPreflightRecommendations.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public bool PreparationPassed { get; init; }
    public bool DryRunPassed { get; init; }
    public bool AuthorizationPassed { get; init; }
    public bool AuthorizationValid { get; init; }
    public bool P15GatePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }

    public bool ScopesUnchanged { get; init; }
    public int WindowDurationMinutes { get; init; }
    public int MaxWindowDurationMinutes { get; init; }
    public bool WindowDurationWithinLimit { get; init; }
    public int MaxRequestsPerWindow { get; init; }
    public bool RequestCapDefined { get; init; }

    public bool KillSwitchNoOpVerified { get; init; }
    public int KillSwitchNoOpCount { get; init; }
    public bool RollbackCheckpointAvailable { get; init; }
    public bool TraceSinkWritable { get; init; }
    public bool ConfigPatchPreviewOnly { get; init; }
    public bool ConfigPatchWritten { get; init; }
    public int StopConditionsCount { get; init; }
    public bool StopConditionsSufficient { get; init; }

    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool WriteConfigPatch { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>V7.10 activation window no-op execution 推荐。</summary>
public static class ScopedRuntimePreviewActivationWindowNoOpExecutionRecommendations
{
    public const string ReadyForScopedRuntimePreviewActivationLive = nameof(ReadyForScopedRuntimePreviewActivationLive);
    public const string BlockedByInsufficientRuns = nameof(BlockedByInsufficientRuns);
    public const string BlockedByInsufficientRequests = nameof(BlockedByInsufficientRequests);
    public const string BlockedByNoApprovedScopeHits = nameof(BlockedByNoApprovedScopeHits);
    public const string BlockedByNoKillSwitchNoOp = nameof(BlockedByNoKillSwitchNoOp);
    public const string BlockedByAppliedDeltaDetected = nameof(BlockedByAppliedDeltaDetected);
    public const string BlockedByConfigPatchWritten = nameof(BlockedByConfigPatchWritten);
    public const string BlockedByRuntimeActivationDetected = nameof(BlockedByRuntimeActivationDetected);
    public const string BlockedBySafetyBoundaryViolation = nameof(BlockedBySafetyBoundaryViolation);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V7.10 no-op execution 单窗结果。</summary>
public sealed class ScopedRuntimePreviewActivationWindowNoOpRunResult
{
    public int WindowIndex { get; init; }
    public int RequestCount { get; init; }
    public int ApprovedScopeHits { get; init; }
    public int NonApprovedScopeNoOps { get; init; }
    public int KillSwitchNoOps { get; init; }
    public int WouldApplyAdd { get; init; }
    public int WouldApplyRemove { get; init; }
    public int AppliedAdd { get; init; }
    public int AppliedRemove { get; init; }
    public bool RollbackCheckpointVerified { get; init; }
    public bool TraceSinkWritable { get; init; }
    public bool ConfigPatchWritten { get; init; }
    public bool RuntimeActivation { get; init; }
    public int ErrorCount { get; init; }
}


/// <summary>V7.10 no-op execution 选项。</summary>
public sealed class ScopedRuntimePreviewActivationWindowNoOpExecutionOptions
{
    public bool Enabled { get; init; } = true;
    public bool ExplicitlyProvided { get; init; }
    public string ApprovedBy { get; init; } = "ReleaseManager";
    public int MinWindowCount { get; init; } = 3;
    public int MinRequestCountTotal { get; init; } = 30;
    public int RequestsPerWindow { get; init; } = 10;
}


/// <summary>V7.10 scoped runtime preview activation window no-op execution 报告。
/// 模拟 activation window 执行节奏，保持 no-op。
/// ConfigPatchWritten=false, RuntimeActivation=false, AppliedAdd/Remove=0/0。</summary>
public sealed class ScopedRuntimePreviewActivationWindowNoOpExecutionReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool NoOpExecutionPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; }
        = ScopedRuntimePreviewActivationWindowNoOpExecutionRecommendations.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public int WindowCount { get; init; }
    public int MinWindowCount { get; init; }
    public int RequestCountTotal { get; init; }
    public int MinRequestCountTotal { get; init; }
    public int ApprovedScopeHitCount { get; init; }
    public int NonApprovedScopeNoOpCount { get; init; }
    public int KillSwitchNoOpCount { get; init; }
    public int AppliedAddTotal { get; init; }
    public int AppliedRemoveTotal { get; init; }
    public bool AppliedDeltaZero { get; init; }
    public bool RollbackCheckpointVerified { get; init; }
    public bool TraceSinkWritable { get; init; }
    public bool ConfigPatchWritten { get; init; }
    public bool RuntimeActivation { get; init; }

    public bool PreflightPassed { get; init; }
    public bool DryRunPassed { get; init; }
    public bool P15GatePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<ScopedRuntimePreviewActivationWindowNoOpRunResult> Windows { get; init; } = Array.Empty<ScopedRuntimePreviewActivationWindowNoOpRunResult>();
    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>V7.11 activation live readiness freeze 推荐。</summary>
public static class ScopedRuntimePreviewActivationLiveReadinessFreezeRecommendations
{
    public const string ReadyForFinalManualApproval = nameof(ReadyForFinalManualApproval);
    public const string BlockedByMissingPrerequisites = nameof(BlockedByMissingPrerequisites);
    public const string BlockedByNoOpExecutionNotPassed = nameof(BlockedByNoOpExecutionNotPassed);
    public const string BlockedByFinalApprovalNotProvided = nameof(BlockedByFinalApprovalNotProvided);
    public const string BlockedByFrozenMetricsViolation = nameof(BlockedByFrozenMetricsViolation);
    public const string BlockedBySafetyBoundaryViolation = nameof(BlockedBySafetyBoundaryViolation);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V7.11 live readiness freeze 冻结指标。</summary>
public sealed class ScopedRuntimePreviewActivationLiveReadinessFrozenMetrics
{
    public int WindowCount { get; init; }
    public int RequestCountTotal { get; init; }
    public int ApprovedScopeHitCount { get; init; }
    public int NonApprovedScopeNoOpCount { get; init; }
    public int KillSwitchNoOpCount { get; init; }
    public int AppliedAddTotal { get; init; }
    public int AppliedRemoveTotal { get; init; }
    public bool AppliedDeltaZero { get; init; }
    public bool ConfigPatchWritten { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool RollbackCheckpointVerified { get; init; }
    public bool TraceSinkWritable { get; init; }
}


/// <summary>V7.11 live readiness freeze 选项。</summary>
public sealed class ScopedRuntimePreviewActivationLiveReadinessFreezeOptions
{
    public bool Enabled { get; init; } = true;
    public bool ExplicitlyProvided { get; init; }
    public string ApprovedBy { get; init; } = "ReleaseManager";
    public string FinalApprovedBy { get; init; } = "";
    public bool FinalApprovalExplicitlyProvided { get; init; }
}


/// <summary>V7.11 scoped runtime preview activation live readiness freeze 报告。
/// 冻结 activation live readiness 证据链，生成最终人工批准 gate。
/// 不启用 runtime activation。Gate 默认 blocked，需要显式 final approval。</summary>
public sealed class ScopedRuntimePreviewActivationLiveReadinessFreezeReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool FreezePassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; }
        = ScopedRuntimePreviewActivationLiveReadinessFreezeRecommendations.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public bool FinalApprovalRequired { get; init; }
    public string FinalApprovedBy { get; init; } = "";
    public string FinalApprovalId { get; init; } = "";
    public DateTimeOffset FinalApprovalTimestamp { get; init; }
    public IReadOnlyList<string> FinalApprovedScopes { get; init; } = Array.Empty<string>();
    public int ActivationWindowDurationMinutes { get; init; }
    public int ActivationWindowRequestCap { get; init; }
    public string RollbackTriggerPolicy { get; init; } = "";
    public string KillSwitchTriggerPolicy { get; init; } = "";

    public bool V7NoOpExecutionPassed { get; init; }
    public bool V7PreflightPassed { get; init; }
    public bool V7DryRunPassed { get; init; }
    public bool V7PreparationPassed { get; init; }
    public bool V7AuthorizationPassed { get; init; }
    public bool V7HardeningPassed { get; init; }
    public bool V7ApprovalPlanPassed { get; init; }
    public bool V7FreezePassed { get; init; }
    public bool P15GatePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }

    public ScopedRuntimePreviewActivationLiveReadinessFrozenMetrics? FrozenMetrics { get; init; }

    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FrozenEvidenceChain { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>V7.12 live activation execution plan 推荐。</summary>
public static class ScopedRuntimePreviewLiveActivationExecutionPlanRecommendations
{
    public const string ReadyForScopedRuntimePreviewLiveActivation = nameof(ReadyForScopedRuntimePreviewLiveActivation);
    public const string BlockedByMissingFreeze = nameof(BlockedByMissingFreeze);
    public const string BlockedByMissingNoOpExecution = nameof(BlockedByMissingNoOpExecution);
    public const string BlockedByFinalApprovalNotPresent = nameof(BlockedByFinalApprovalNotPresent);
    public const string BlockedByConfigPatchPreviewNotLocked = nameof(BlockedByConfigPatchPreviewNotLocked);
    public const string BlockedByMonitoringPlanMissing = nameof(BlockedByMonitoringPlanMissing);
    public const string BlockedBySafetyBoundaryViolation = nameof(BlockedBySafetyBoundaryViolation);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V7.12 execution plan 选项。</summary>
public sealed class ScopedRuntimePreviewLiveActivationExecutionPlanOptions
{
    public bool Enabled { get; init; } = true;
    public bool ExplicitlyProvided { get; init; }
    public string ApprovedBy { get; init; } = "ReleaseManager";
    public string FinalApprovedBy { get; init; } = "";
    public bool FinalApprovalExplicitlyProvided { get; init; }
}


/// <summary>V7.12 scoped runtime preview live activation execution plan 报告。
/// 生成最终执行计划并冻结 config patch preview。
/// ConfigPatchWritten=false, RuntimeActivation=false。</summary>
public sealed class ScopedRuntimePreviewLiveActivationExecutionPlanReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool PlanPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; }
        = ScopedRuntimePreviewLiveActivationExecutionPlanRecommendations.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public string ExecutionPlanId { get; init; } = "";
    public string FinalApprovalId { get; init; } = "";
    public IReadOnlyList<string> ApprovedScopes { get; init; } = Array.Empty<string>();
    public int ActivationWindowDurationMinutes { get; init; }
    public int ActivationWindowRequestCap { get; init; }
    public string RollbackTriggerPolicy { get; init; } = "";
    public string KillSwitchTriggerPolicy { get; init; } = "";
    public string ConfigPatchPreview { get; init; } = "";
    public string RuntimeSwitchPreview { get; init; } = "";
    public string MonitoringPlan { get; init; } = "";
    public IReadOnlyList<string> AbortConditions { get; init; } = Array.Empty<string>();

    public bool ConfigPatchWritten { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool ConfigPatchPreviewLocked { get; init; }

    public bool V7FreezePassed { get; init; }
    public bool V7NoOpExecutionPassed { get; init; }
    public bool V7PreflightPassed { get; init; }
    public bool V7DryRunPassed { get; init; }
    public bool P15GatePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public bool FinalApprovalPresent { get; init; }

    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>V7.13 live activation execution 推荐。</summary>
public static class ScopedRuntimePreviewLiveActivationExecutionRecommendations
{
    public const string ReadyForExplicitLiveActivationCommand = nameof(ReadyForExplicitLiveActivationCommand);
    public const string BlockedByMissingPlan = nameof(BlockedByMissingPlan);
    public const string BlockedByMissingFreeze = nameof(BlockedByMissingFreeze);
    public const string BlockedByPlanIdMismatch = nameof(BlockedByPlanIdMismatch);
    public const string BlockedByConfigPatchNotLocked = nameof(BlockedByConfigPatchNotLocked);
    public const string BlockedByKillSwitchNotArmed = nameof(BlockedByKillSwitchNotArmed);
    public const string BlockedByRollbackCheckpointMissing = nameof(BlockedByRollbackCheckpointMissing);
    public const string BlockedByTraceSinkMissing = nameof(BlockedByTraceSinkMissing);
    public const string BlockedByScopeMismatch = nameof(BlockedByScopeMismatch);
    public const string BlockedBySafetyBoundaryViolation = nameof(BlockedBySafetyBoundaryViolation);
    public const string ExecuteLiveActivationNotRequested = nameof(ExecuteLiveActivationNotRequested);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V7.13 execution 选项。</summary>
public sealed class ScopedRuntimePreviewLiveActivationExecutionOptions
{
    public bool Enabled { get; init; } = true;
    public bool ExecuteLiveActivation { get; init; }
    public string FinalApprovedBy { get; init; } = "";
    public bool FinalApprovalExplicitlyProvided { get; init; }
    public string ExecutionPlanId { get; init; } = "";
}


/// <summary>V7.13 guarded scoped runtime preview live activation execution 报告。
/// 默认只生成执行记录，不执行 live activation。
/// 需要显式 --execute-live-activation + --execution-plan-id 匹配。</summary>
public sealed class ScopedRuntimePreviewLiveActivationExecutionReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ExecutionGatePassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; }
        = ScopedRuntimePreviewLiveActivationExecutionRecommendations.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public bool ExecuteLiveActivation { get; init; }
    public string ActivationExecutionId { get; init; } = "";
    public string ExecutionPlanId { get; init; } = "";
    public string AppliedConfigPatchId { get; init; } = "";
    public IReadOnlyList<string> ApprovedScopes { get; init; } = Array.Empty<string>();
    public DateTimeOffset ActivationWindowStart { get; init; }
    public DateTimeOffset ActivationWindowEnd { get; init; }
    public int RequestCap { get; init; }
    public bool KillSwitchArmed { get; init; }
    public string RollbackCheckpointId { get; init; } = "";
    public string TraceSinkPath { get; init; } = "";
    public IReadOnlyList<string> StopConditions { get; init; } = Array.Empty<string>();

    public bool ConfigPatchWritten { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool RuntimeSwitchChanged { get; init; }
    public bool PlanIdMatches { get; init; }
    public bool ConfigPatchPreviewLocked { get; init; }

    public bool V7PlanPassed { get; init; }
    public bool V7FreezePassed { get; init; }
    public bool V7NoOpPassed { get; init; }
    public bool V7PreflightPassed { get; init; }
    public bool V7DryRunPassed { get; init; }
    public bool P15GatePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public bool FinalApprovalPresent { get; init; }

    public bool FormalRetrievalAllowed { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>V7.14 live activation observation 推荐。</summary>
public static class ScopedRuntimePreviewLiveActivationObservationRecommendations
{
    public const string ReadyForLiveActivationSummaryFreeze = nameof(ReadyForLiveActivationSummaryFreeze);
    public const string BlockedByMissingExecution = nameof(BlockedByMissingExecution);
    public const string BlockedByMissingPlan = nameof(BlockedByMissingPlan);
    public const string BlockedByScopeMismatch = nameof(BlockedByScopeMismatch);
    public const string BlockedByRequestCapExceeded = nameof(BlockedByRequestCapExceeded);
    public const string BlockedByNonApprovedRouteNotNoOp = nameof(BlockedByNonApprovedRouteNotNoOp);
    public const string BlockedByKillSwitchNotArmed = nameof(BlockedByKillSwitchNotArmed);
    public const string BlockedByAppliedDeltaDetected = nameof(BlockedByAppliedDeltaDetected);
    public const string BlockedByConfigPatchWritten = nameof(BlockedByConfigPatchWritten);
    public const string BlockedByRuntimeActivationDetected = nameof(BlockedByRuntimeActivationDetected);
    public const string BlockedBySafetyBoundaryViolation = nameof(BlockedBySafetyBoundaryViolation);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V7.14 observation 选项。</summary>
public sealed class ScopedRuntimePreviewLiveActivationObservationOptions
{
    public bool Enabled { get; init; } = true;
    public int ObservationRuns { get; init; } = 5;
    public int MaxRequestCap { get; init; } = 100;
    public int RequestsPerRun { get; init; } = 8;
}


/// <summary>V7.14 scoped runtime preview live activation observation 报告。
/// 对 live activation window 做观测与安全审计。
/// 不启用 runtime activation。ConfigPatchWritten=false。</summary>
public sealed class ScopedRuntimePreviewLiveActivationObservationReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ObservationPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; }
        = ScopedRuntimePreviewLiveActivationObservationRecommendations.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public string ActivationExecutionId { get; init; } = "";
    public string ExecutionPlanId { get; init; } = "";
    public IReadOnlyList<string> ApprovedScopes { get; init; } = Array.Empty<string>();
    public int ObservedRequestCount { get; init; }
    public int MaxRequestCap { get; init; }
    public int ApprovedScopeRequestCount { get; init; }
    public int NonApprovedScopeRequestCount { get; init; }
    public int NonApprovedScopeNoOpCount { get; init; }
    public bool KillSwitchArmed { get; init; }
    public int KillSwitchTripCount { get; init; }
    public int KillSwitchNoOpCount { get; init; }
    public bool RollbackCheckpointAvailable { get; init; }
    public bool TraceSinkWritable { get; init; }
    public string TraceSinkPath { get; init; } = "";
    public string ShadowTracePath { get; init; } = "";
    public int TraceRecordCount { get; init; }
    public int AppliedDeltaCount { get; init; }
    public bool AppliedDeltaZero { get; init; }
    public bool ConfigPatchWritten { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool RuntimeSwitchChanged { get; init; }

    public bool ExecutionGatePassed { get; init; }
    public bool ExecutionPlanPassed { get; init; }
    public bool FreezePassed { get; init; }
    public bool NoOpExecutionPassed { get; init; }
    public bool P15GatePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public bool PlanIdUnchanged { get; init; }
    public bool FinalApprovalIdentityUnchanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>V7.15 live activation summary freeze 推荐。</summary>
public static class ScopedRuntimePreviewLiveActivationSummaryFreezeRecommendations
{
    public const string ReadyForLiveActivationCloseout = nameof(ReadyForLiveActivationCloseout);
    public const string BlockedByMissingObservation = nameof(BlockedByMissingObservation);
    public const string BlockedByMissingExecution = nameof(BlockedByMissingExecution);
    public const string BlockedByIdentityMismatch = nameof(BlockedByIdentityMismatch);
    public const string BlockedByFrozenMetricsViolation = nameof(BlockedByFrozenMetricsViolation);
    public const string BlockedBySafetyBoundaryViolation = nameof(BlockedBySafetyBoundaryViolation);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V7.15 summary freeze 冻结指标。</summary>
public sealed class ScopedRuntimePreviewLiveActivationSummaryFrozenMetrics
{
    public int ObservedRequestCount { get; init; }
    public int MaxRequestCap { get; init; }
    public int ApprovedScopeRequestCount { get; init; }
    public int NonApprovedScopeRequestCount { get; init; }
    public int NonApprovedScopeNoOpCount { get; init; }
    public int KillSwitchTripCount { get; init; }
    public int KillSwitchNoOpCount { get; init; }
    public int AppliedDeltaCount { get; init; }
    public bool ConfigPatchWritten { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool RuntimeSwitchChanged { get; init; }
}


/// <summary>V7.15 summary freeze 选项。</summary>
public sealed class ScopedRuntimePreviewLiveActivationSummaryFreezeOptions
{
    public bool Enabled { get; init; } = true;
}


/// <summary>V7.15 live activation summary freeze 报告。
/// 冻结 scoped runtime preview live activation 执行记录与 shadow observation 证据链。
/// ConfigPatchWritten=false, RuntimeActivation=false。</summary>
public sealed class ScopedRuntimePreviewLiveActivationSummaryFreezeReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool FreezePassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; }
        = ScopedRuntimePreviewLiveActivationSummaryFreezeRecommendations.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public string ActivationExecutionId { get; init; } = "";
    public string ExecutionPlanId { get; init; } = "";
    public string FinalApprovedBy { get; init; } = "";
    public string FinalApprovalId { get; init; } = "";
    public IReadOnlyList<string> ApprovedScopes { get; init; } = Array.Empty<string>();
    public string ObservationSource { get; init; } = "";
    public string ShadowTracePath { get; init; } = "";
    public IReadOnlyList<string> FrozenEvidenceChain { get; init; } = Array.Empty<string>();

    public bool V7ObservationPassed { get; init; }
    public bool V7ExecutionGatePassed { get; init; }
    public bool V7PlanPassed { get; init; }
    public bool V7FreezePassed { get; init; }
    public bool V7NoOpPassed { get; init; }
    public bool P15GatePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public bool FinalApprovalIdentityUnchanged { get; init; }
    public bool ExecutionPlanIdUnchanged { get; init; }

    public ScopedRuntimePreviewLiveActivationSummaryFrozenMetrics? FrozenMetrics { get; init; }

    public bool FormalRetrievalAllowed { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>V7.16 live activation closeout 推荐。</summary>
public static class ScopedRuntimePreviewLiveActivationCloseoutRecommendations
{
    public const string ScopedRuntimePreviewCompleted = nameof(ScopedRuntimePreviewCompleted);
    public const string BlockedByMissingSummaryFreeze = nameof(BlockedByMissingSummaryFreeze);
    public const string BlockedByMissingObservation = nameof(BlockedByMissingObservation);
    public const string BlockedBySafetyBoundaryViolation = nameof(BlockedBySafetyBoundaryViolation);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V7.16 closeout 选项。</summary>
public sealed class ScopedRuntimePreviewLiveActivationCloseoutOptions
{
    public bool Enabled { get; init; } = true;
}


/// <summary>V7.16 scoped runtime preview live activation closeout 报告。
/// 最终收尾报告与证据链封存。FormalRetrievalStillBlocked, GlobalDefaultOnStillBlocked。</summary>
public sealed class ScopedRuntimePreviewLiveActivationCloseoutReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool CloseoutPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; }
        = ScopedRuntimePreviewLiveActivationCloseoutRecommendations.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public string ActivationExecutionId { get; init; } = "";
    public string ExecutionPlanId { get; init; } = "";
    public string FinalApprovedBy { get; init; } = "";
    public string FinalApprovalId { get; init; } = "";
    public IReadOnlyList<string> ApprovedScopes { get; init; } = Array.Empty<string>();
    public string ObservationSource { get; init; } = "";
    public IReadOnlyList<string> FrozenEvidenceChain { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FinalDisposition { get; init; } = Array.Empty<string>();

    public bool SummaryFreezePassed { get; init; }
    public bool ObservationPassed { get; init; }
    public bool ExecutionPassed { get; init; }
    public bool P15GatePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }

    public bool ConfigPatchWritten { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>V8.0 formal retrieval promotion readiness audit 推荐。</summary>
public static class FormalRetrievalPromotionReadinessRecommendations
{
    public const string ReadyForFormalRetrievalPromotionPlan = nameof(ReadyForFormalRetrievalPromotionPlan);
    public const string BlockedByMissingCloseout = nameof(BlockedByMissingCloseout);
    public const string BlockedByV7CloseoutNotCompleted = nameof(BlockedByV7CloseoutNotCompleted);
    public const string BlockedByFormalRetrievalStillBlocked = nameof(BlockedByFormalRetrievalStillBlocked);
    public const string BlockedBySafetyBoundaryViolation = nameof(BlockedBySafetyBoundaryViolation);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V8.0 audit 选项。</summary>
public sealed class FormalRetrievalPromotionReadinessAuditOptions
{
    public bool Enabled { get; init; } = true;
}


/// <summary>V8.0 scoped runtime preview formal retrieval promotion readiness audit 报告。
/// 不启用 formal retrieval。FormalRetrievalAllowed=false。</summary>
public sealed class FormalRetrievalPromotionReadinessAuditReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool AuditPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; }
        = FormalRetrievalPromotionReadinessRecommendations.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public bool V7CloseoutCompleted { get; init; }
    public bool FormalRetrievalStillBlocked { get; init; }
    public bool RequiresSeparateFormalRetrievalPromotionGate { get; init; }
    public string ObservationSource { get; init; } = "";
    public bool NoFormalRuntimeMutation { get; init; }
    public bool NoPackageOutputMutation { get; init; }
    public bool NoPackingPolicyMutation { get; init; }
    public bool NoVectorStoreBindingMutation { get; init; }
    public bool P15GatePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<string> AuditItems { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>V8.1 formal retrieval promotion plan 推荐。</summary>
public static class FormalRetrievalPromotionPlanRecommendations
{
    public const string ReadyForFormalRetrievalPromotionApproval = nameof(ReadyForFormalRetrievalPromotionApproval);
    public const string BlockedByMissingReadinessAudit = nameof(BlockedByMissingReadinessAudit);
    public const string BlockedByMissingCloseout = nameof(BlockedByMissingCloseout);
    public const string BlockedBySafetyPlanIncomplete = nameof(BlockedBySafetyPlanIncomplete);
    public const string BlockedBySafetyBoundaryViolation = nameof(BlockedBySafetyBoundaryViolation);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V8.1 promotion plan 选项。</summary>
public sealed class FormalRetrievalPromotionPlanOptions
{
    public bool Enabled { get; init; } = true;
}


/// <summary>V8.1 formal retrieval promotion plan 报告。
/// 不启用 formal retrieval。FormalRetrievalAllowed=false。</summary>
public sealed class FormalRetrievalPromotionPlanReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool PlanPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; }
        = FormalRetrievalPromotionPlanRecommendations.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public string PromotionPlanId { get; init; } = "";
    public string SourceCloseoutId { get; init; } = "";
    public IReadOnlyList<string> ApprovedScopes { get; init; } = Array.Empty<string>();
    public string ObservationSource { get; init; } = "";
    public bool FormalRetrievalStillBlocked { get; init; }
    public bool RuntimeSwitchStillBlocked { get; init; }
    public bool ConfigPatchWritten { get; init; }
    public bool RequiredManualApproval { get; init; }
    public string RollbackPlan { get; init; } = "";
    public string KillSwitchPlan { get; init; } = "";
    public string ShadowValidationPlan { get; init; } = "";
    public string FormalPackageSafetyPlan { get; init; } = "";
    public IReadOnlyList<string> AbortConditions { get; init; } = Array.Empty<string>();

    public bool V8AuditPassed { get; init; }
    public bool V7CloseoutPassed { get; init; }
    public bool P15GatePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }

    public bool V8ReadinessGatePassed { get; init; }
    public bool RequiresSeparateFormalRetrievalPromotionGate { get; init; }
    public string V8ReadinessGateOperationId { get; init; } = "";
    public string UpstreamReadinessArtifactPath { get; init; } = "";
    public bool V8ReadinessFormalRetrievalStillBlocked { get; init; }
    public string V8ReadinessObservationSource { get; init; } = "";
    public bool V7CloseoutGatePassed { get; init; }
    public string V7CloseoutGateOperationId { get; init; } = "";

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>V8.2 formal retrieval promotion approval 推荐。</summary>
public static class FormalRetrievalPromotionApprovalRecommendations
{
    public const string ManualApprovalGranted = nameof(ManualApprovalGranted);
    public const string BlockedByMissingPlanGate = nameof(BlockedByMissingPlanGate);
    public const string BlockedByManualApprovalMissing = nameof(BlockedByManualApprovalMissing);
    public const string BlockedByApprovalIdentityMissing = nameof(BlockedByApprovalIdentityMissing);
    public const string BlockedByApprovalIdMissing = nameof(BlockedByApprovalIdMissing);
    public const string BlockedByUpstreamInvariantViolation = nameof(BlockedByUpstreamInvariantViolation);
    public const string BlockedBySafetyBoundaryViolation = nameof(BlockedBySafetyBoundaryViolation);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V8.2 approval 选项。</summary>
public sealed class FormalRetrievalPromotionApprovalOptions
{
    public bool Enabled { get; init; } = true;
    public string ApprovedBy { get; init; } = "";
    public bool ExplicitlyProvided { get; init; }
    public string ApprovalId { get; init; } = "";
    public bool ApprovalIdExplicitlyProvided { get; init; }
    public IReadOnlyList<string> ApprovalScopes { get; init; } = Array.Empty<string>();
}


/// <summary>V8.2 formal retrieval promotion approval 报告。
/// 人工批准契约。需要显式 --approved-by + --approval-id 才能通过 gate。
/// FormalRetrievalAllowed=false。</summary>
public sealed class FormalRetrievalPromotionApprovalReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ApprovalGatePassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; }
        = FormalRetrievalPromotionApprovalRecommendations.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public string ApprovalRequestId { get; init; } = "";
    public string SourcePromotionPlanId { get; init; } = "";
    public string SourcePromotionPlanGateOperationId { get; init; } = "";
    public string SourceReadinessGateOperationId { get; init; } = "";
    public string SourceCloseoutGateOperationId { get; init; } = "";
    public IReadOnlyList<string> ApprovedScopes { get; init; } = Array.Empty<string>();
    public bool RequiredManualApproval { get; init; }
    public bool ApprovalGranted { get; init; }
    public string ApprovedBy { get; init; } = "";
    public string ApprovalId { get; init; } = "";
    public DateTimeOffset ApprovalTimestamp { get; init; }
    public bool ApprovalIdentityBound { get; init; }
    public bool ApprovalScopeBound { get; init; }
    public IReadOnlyList<string> ApprovalScopes { get; init; } = Array.Empty<string>();
    public bool ApprovalScopeSubsetOfApprovedScopes { get; init; }
    public bool FormalRetrievalStillBlocked { get; init; }
    public bool RuntimeSwitchStillBlocked { get; init; }
    public bool ConfigPatchWritten { get; init; }

    public bool V8PlanGatePassed { get; init; }
    public bool V8ReadinessGatePassed { get; init; }
    public bool V7CloseoutGatePassed { get; init; }
    public bool P15GatePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>V8.3 approval evidence 输入模型。</summary>
public sealed class FormalRetrievalPromotionApprovalEvidence
{
    public string ApprovalEvidenceId { get; init; } = "";
    public string ApprovedBy { get; init; } = "";
    public string ApprovalId { get; init; } = "";
    public IReadOnlyList<string> ApprovalScopes { get; init; } = Array.Empty<string>();
    public string ApprovalSource { get; init; } = "";
    public DateTimeOffset ApprovalTimestamp { get; init; }
    public string SourcePromotionPlanGateOperationId { get; init; } = "";
    public string SourceReadinessGateOperationId { get; init; } = "";
    public string SourceCloseoutGateOperationId { get; init; } = "";
    public string OperatorStatement { get; init; } = "";
    public DateTimeOffset EvidenceCreatedAt { get; init; }

    public string ApprovalEvidenceSourceKind { get; init; } = "";
    public string ApprovalEvidenceProvenanceId { get; init; } = "";
    public string ApprovalEvidenceProvidedBy { get; init; } = "";
    public DateTimeOffset ApprovalEvidenceProvidedAt { get; init; }
    public string ApprovalEvidenceTrustMode { get; init; } = "";
    public bool ApprovalEvidenceIsExternal { get; init; }
    public string ApprovalEvidenceChecksum { get; init; } = "";
    public string SourceApprovalRequestId { get; init; } = "";
    public string BoundPendingApprovalGateOperationId { get; init; } = "";
}


/// <summary>V8.3 approval evidence seal 推荐。</summary>
public static class FormalRetrievalPromotionApprovalEvidenceSealRecommendations
{
    public const string EvidenceSealedManualApprovalComplete = nameof(EvidenceSealedManualApprovalComplete);
    public const string BlockedByEvidenceMissing = nameof(BlockedByEvidenceMissing);
    public const string BlockedByEvidenceIncomplete = nameof(BlockedByEvidenceIncomplete);
    public const string BlockedByUpstreamInvariantViolation = nameof(BlockedByUpstreamInvariantViolation);
    public const string BlockedBySafetyBoundaryViolation = nameof(BlockedBySafetyBoundaryViolation);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V8.3 evidence seal 选项。</summary>
public sealed class FormalRetrievalPromotionApprovalEvidenceSealOptions
{
    public bool Enabled { get; init; } = true;
}


/// <summary>V8.3 formal retrieval promotion approval evidence seal 报告。
/// 读取并绑定真实 approval evidence，生成 seal artifact。
/// FormalRetrievalAllowed=false。</summary>
public sealed class FormalRetrievalPromotionApprovalEvidenceSealReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool SealPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; }
        = FormalRetrievalPromotionApprovalEvidenceSealRecommendations.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public bool EvidencePresent { get; init; }
    public string EvidencePath { get; init; } = "";
    public string ApprovalEvidenceId { get; init; } = "";
    public string ApprovedBy { get; init; } = "";
    public string ApprovalId { get; init; } = "";
    public IReadOnlyList<string> ApprovalScopes { get; init; } = Array.Empty<string>();
    public string ApprovalSource { get; init; } = "";
    public bool ScopeSubsetValidated { get; init; }
    public bool SourceGateIdsMatch { get; init; }

    public bool V8ApprovalPendingGatePresent { get; init; }
    public bool V8PlanGatePassed { get; init; }
    public bool V8ReadinessGatePassed { get; init; }
    public bool V7CloseoutGatePassed { get; init; }
    public bool P15GatePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool ConfigPatchWritten { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }

    public bool ApprovalEvidenceIsExternal { get; init; }
    public string ApprovalEvidenceSourceKind { get; init; } = "";
    public string ApprovalEvidenceProvidedBy { get; init; } = "";
    public bool BoundPendingApprovalGateVerified { get; init; }
    public bool PendingApprovalBlockedReasonsManualOnly { get; init; }
    public bool SourceApprovalRequestIdMatched { get; init; }
    public bool BoundPendingApprovalGateIdMatched { get; init; }
    public bool TrustAnchorPresent { get; init; }
    public bool EvidenceProvenanceTrusted { get; init; }
    public bool EvidenceChecksumMatched { get; init; }

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}


/// <summary>V8.3R3 trust registry 可信证明记录。</summary>
public sealed class FormalRetrievalPromotionApprovalTrustedProvenanceRecord
{
    public string ApprovalEvidenceProvenanceId { get; init; } = "";
    public string ApprovalEvidenceSourceKind { get; init; } = "";
    public string ApprovalEvidenceProvidedBy { get; init; } = "";
    public string ApprovalEvidenceChecksum { get; init; } = "";
    public string SourceApprovalRequestId { get; init; } = "";
    public string BoundPendingApprovalGateOperationId { get; init; } = "";
    public IReadOnlyList<string> AllowedScopes { get; init; } = Array.Empty<string>();
    public string TrustMode { get; init; } = "";
    public DateTimeOffset ValidUntil { get; init; }
}


/// <summary>V8.3R3 approval trust registry。</summary>
public sealed class FormalRetrievalPromotionApprovalTrustRegistry
{
    public string RegistryId { get; init; } = "";
    public DateTimeOffset RegistryCreatedAt { get; init; }
    public IReadOnlyList<string> AllowedSourceKinds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<FormalRetrievalPromotionApprovalTrustedProvenanceRecord> TrustedProvenanceRecords { get; init; }
        = Array.Empty<FormalRetrievalPromotionApprovalTrustedProvenanceRecord>();
}


/// <summary>V8.4 external approval intake 推荐。</summary>
public static class FormalRetrievalPromotionExternalApprovalIntakeRecommendations
{
    public const string ReadyForExternalApprovalProcessing = nameof(ReadyForExternalApprovalProcessing);
    public const string BlockedByExternalApprovalMissing = nameof(BlockedByExternalApprovalMissing);
    public const string BlockedByTrustRegistryMissing = nameof(BlockedByTrustRegistryMissing);
    public const string BlockedByEvidenceStructureInvalid = nameof(BlockedByEvidenceStructureInvalid);
    public const string BlockedByRegistryStructureInvalid = nameof(BlockedByRegistryStructureInvalid);
    public const string BlockedByUpstreamGateMismatch = nameof(BlockedByUpstreamGateMismatch);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>V8.4 external approval intake 选项。</summary>
public sealed class FormalRetrievalPromotionExternalApprovalIntakeOptions
{
    public bool Enabled { get; init; } = true;
}


/// <summary>V8.4 formal retrieval promotion external approval intake 报告。
/// 只验证 evidence / trust registry 结构和绑定，不生成假文件。
/// FormalRetrievalAllowed=false。</summary>
public sealed class FormalRetrievalPromotionExternalApprovalIntakeReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool IntakePassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; }
        = FormalRetrievalPromotionExternalApprovalIntakeRecommendations.KeepPreviewOnly;
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public bool EvidencePresent { get; init; }
    public string EvidencePath { get; init; } = "";
    public bool TrustRegistryPresent { get; init; }
    public string TrustRegistryPath { get; init; } = "";
    public bool EvidenceStructureValid { get; init; }
    public bool RegistryStructureValid { get; init; }
    public bool UpstreamGateIdsMatch { get; init; }
    public bool ApprovalRequestIdBound { get; init; }
    public bool ProvenanceRecordMatched { get; init; }

    public IReadOnlyList<string> EvidenceFields { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RegistryFields { get; init; } = Array.Empty<string>();

    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool ConfigPatchWritten { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }

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
