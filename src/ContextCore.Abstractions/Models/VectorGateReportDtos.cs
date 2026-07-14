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
