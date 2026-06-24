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
