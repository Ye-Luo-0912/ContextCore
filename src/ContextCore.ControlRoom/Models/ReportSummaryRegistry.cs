namespace ContextCore.ControlRoom.Models;

public static class ReportSummaryRegistry
{
    // =========================================================================
    // V5 descriptors
    // =========================================================================

    public static readonly ControlRoomReportDescriptor V5FormalRetrievalIntegrationPlan = new()
    {
        ReportId = "FormalRetrievalIntegrationPlan",
        DisplayTitle = "V5 Formal Retrieval Integration Plan Summary",
        PrimaryPath = "vector/v5/formal-retrieval-integration-plan.json",
        GatePath = "vector/v5/formal-retrieval-integration-plan-gate.json",
        PhaseGroup = "V5",
        EvalGateCommand = "eval vector-formal-retrieval-integration-plan-gate",
    };

    public static readonly ControlRoomReportDescriptor V5FormalRetrievalIntegrationDecision = new()
    {
        ReportId = "FormalRetrievalIntegrationDecision",
        DisplayTitle = "V5 Formal Retrieval Integration Decision Summary",
        PrimaryPath = "vector/v5/formal-retrieval-integration-decision.json",
        GatePath = "vector/v5/formal-retrieval-integration-decision-gate.json",
        PhaseGroup = "V5",
        EvalGateCommand = "eval vector-formal-retrieval-integration-decision-gate",
    };

    public static readonly ControlRoomReportDescriptor V5ShadowFormalRetrievalAdapterPlan = new()
    {
        ReportId = "ShadowFormalRetrievalAdapterPlan",
        DisplayTitle = "V5 Shadow Formal Retrieval Adapter Plan Summary",
        PrimaryPath = "vector/v5/shadow-formal-retrieval-adapter-plan.json",
        GatePath = "vector/v5/shadow-formal-retrieval-adapter-plan-gate.json",
        PhaseGroup = "V5",
        EvalGateCommand = "eval vector-shadow-formal-retrieval-adapter-plan-gate",
    };

    public static readonly ControlRoomReportDescriptor V5ShadowFormalRetrievalAdapter = new()
    {
        ReportId = "ShadowFormalRetrievalAdapter",
        DisplayTitle = "V5 Shadow Formal Retrieval Adapter Summary",
        PrimaryPath = "vector/v5/shadow-formal-retrieval-adapter.json",
        GatePath = "vector/v5/shadow-formal-retrieval-adapter-gate.json",
        PhaseGroup = "V5",
        EvalGateCommand = "eval vector-shadow-formal-retrieval-adapter-gate",
    };

    public static readonly ControlRoomReportDescriptor V5FormalAdapterPackageShadowComparison = new()
    {
        ReportId = "FormalAdapterPackageShadowComparison",
        DisplayTitle = "V5 Package Shadow Comparison Summary",
        PrimaryPath = "vector/v5/formal-adapter-package-shadow-comparison.json",
        GatePath = "vector/v5/formal-adapter-package-shadow-comparison-gate.json",
        PhaseGroup = "V5",
        EvalGateCommand = "eval vector-formal-adapter-package-shadow-comparison-gate",
    };

    public static readonly ControlRoomReportDescriptor V5GraphVectorRetrievalQualityAudit = new()
    {
        ReportId = "GraphVectorRetrievalQualityAudit",
        DisplayTitle = "V5 Retrieval Quality Audit Summary",
        PrimaryPath = "vector/v5/graph-vector-retrieval-quality-audit.json",
        GatePath = "vector/v5/graph-vector-retrieval-quality-gate.json",
        PhaseGroup = "V5",
        EvalGateCommand = "eval vector-graph-vector-retrieval-quality-gate",
    };

    public static readonly ControlRoomReportDescriptor V5RetrievalQualityRepairPreview = new()
    {
        ReportId = "RetrievalQualityRepairPreview",
        DisplayTitle = "V5 Retrieval Quality Repair Preview Summary",
        PrimaryPath = "vector/v5/retrieval-quality-repair-preview.json",
        GatePath = "vector/v5/retrieval-quality-repair-gate.json",
        PhaseGroup = "V5",
        EvalGateCommand = "eval vector-retrieval-quality-repair-gate",
    };

    public static readonly ControlRoomReportDescriptor V5RuntimeObservableFeatureContract = new()
    {
        ReportId = "RuntimeObservableFeatureContract",
        DisplayTitle = "V5 Runtime-observable Feature Contract Summary",
        PrimaryPath = "vector/v5/runtime-observable-feature-contract.json",
        GatePath = "vector/v5/runtime-observable-feature-contract-gate.json",
        PhaseGroup = "V5",
        EvalGateCommand = "eval vector-runtime-observable-feature-contract-gate",
    };

    public static readonly ControlRoomReportDescriptor V5RuntimeRetrievalFeatureDerivation = new()
    {
        ReportId = "RuntimeRetrievalFeatureDerivation",
        DisplayTitle = "V5 Runtime Feature Derivation Preview Summary",
        PrimaryPath = "vector/v5/runtime-feature-derivation-preview.json",
        GatePath = "vector/v5/runtime-feature-derivation-gate.json",
        PhaseGroup = "V5",
        EvalGateCommand = "eval vector-runtime-feature-derivation-gate",
    };

    public static readonly ControlRoomReportDescriptor V5RuntimeFeatureDerivationRepair = new()
    {
        ReportId = "RuntimeRetrievalFeatureDerivationRepair",
        DisplayTitle = "V5 Runtime Feature Derivation Repair Summary",
        PrimaryPath = "vector/v5/runtime-feature-derivation-repair.json",
        GatePath = "vector/v5/runtime-feature-derivation-repair-gate.json",
        PhaseGroup = "V5",
        EvalGateCommand = "eval vector-runtime-feature-derivation-repair-gate",
    };

    public static readonly ControlRoomReportDescriptor V5FormalRetrievalIntegrationFreeze = new()
    {
        ReportId = "FormalRetrievalIntegrationFreeze",
        DisplayTitle = "V5 Formal Integration Freeze Summary",
        PrimaryPath = "vector/v5/formal-retrieval-integration-freeze.json",
        GatePath = "vector/v5/formal-retrieval-integration-freeze-gate.json",
        PhaseGroup = "V5",
        EvalGateCommand = "eval vector-formal-retrieval-integration-freeze-gate",
    };

    public static readonly ControlRoomReportDescriptor V5RuntimeFeatureDerivationFailureFreeze = new()
    {
        ReportId = "FeatureDerivationFailureFreeze",
        DisplayTitle = "V5 Runtime Feature Derivation Failure Freeze Summary",
        PrimaryPath = "vector/v5/runtime-feature-derivation-failure-freeze.json",
        PhaseGroup = "V5",
        EvalGateCommand = "eval vector-runtime-feature-derivation-failure-freeze",
    };

    public static readonly ControlRoomReportDescriptor V5GraphHubNoiseControl = new()
    {
        ReportId = "GraphHubNoiseControl",
        DisplayTitle = "V5 Graph Hub Noise Control Summary",
        PrimaryPath = "vector/v5/graph-hub-noise-control-preview.json",
        GatePath = "vector/v5/graph-hub-noise-control-gate.json",
        PhaseGroup = "V5",
        EvalGateCommand = "eval vector-graph-hub-noise-control-gate",
    };

    public static readonly ControlRoomReportDescriptor V5RetrievalEvalProtocol = new()
    {
        ReportId = "RetrievalEvalProtocol",
        DisplayTitle = "V5.11 Retrieval Eval Protocol / Source Discriminability Summary",
        PrimaryPath = "vector/v5/retrieval-eval-protocol-gate.json",
        PhaseGroup = "V5",
        EvalGateCommand = "eval vector-retrieval-eval-protocol-gate",
    };

    public static readonly ControlRoomReportDescriptor V5InputMetadataEnrichment = new()
    {
        ReportId = "InputMetadataEnrichment",
        DisplayTitle = "V5.12 Input Metadata Enrichment Preview Summary",
        PrimaryPath = "vector/v5/input-metadata-enrichment-preview.json",
        GatePath = "vector/v5/input-metadata-enrichment-gate.json",
        PhaseGroup = "V5",
        EvalGateCommand = "eval vector-input-metadata-enrichment-gate",
    };

    public static readonly ControlRoomReportDescriptor V5EnrichedCandidateSourceRepairRecheck = new()
    {
        ReportId = "EnrichedCandidateSourceRepairRecheck",
        DisplayTitle = "V5.13 Enriched Candidate Source Repair Recheck Summary",
        PrimaryPath = "vector/v5/enriched-candidate-source-repair-recheck.json",
        GatePath = "vector/v5/enriched-candidate-source-repair-recheck-gate.json",
        PhaseGroup = "V5",
        EvalGateCommand = "eval vector-enriched-candidate-source-repair-recheck-gate",
    };

    public static readonly ControlRoomReportDescriptor V5SourceAwareRankingRepair = new()
    {
        ReportId = "SourceAwareRankingRepair",
        DisplayTitle = "V5.14 Source-aware Ranking Repair Summary",
        PrimaryPath = "vector/v5/source-aware-ranking-repair.json",
        GatePath = "vector/v5/source-aware-ranking-repair-gate.json",
        PhaseGroup = "V5",
        EvalGateCommand = "eval vector-source-aware-ranking-repair-gate",
    };

    public static readonly ControlRoomReportDescriptor V5OutputTokenPriorityShadow = new()
    {
        ReportId = "OutputTokenPriorityShadow",
        DisplayTitle = "V5.15 Output Token Priority Shadow Gate Summary",
        PrimaryPath = "vector/v5/output-token-priority-shadow.json",
        GatePath = "vector/v5/output-token-priority-shadow-gate.json",
        PhaseGroup = "V5",
        EvalGateCommand = "eval vector-output-token-priority-shadow-gate",
    };

    public static readonly ControlRoomReportDescriptor V5FormalAdapterInputContract = new()
    {
        ReportId = "FormalAdapterInputContract",
        DisplayTitle = "Formal Adapter Input Contract Summary",
        PrimaryPath = "vector/v5/formal-adapter-input-contract.json",
        GatePath = "vector/v5/formal-adapter-input-contract-gate.json",
        PhaseGroup = "V5",
        EvalGateCommand = "eval vector-formal-adapter-input-contract-gate",
    };

    // =========================================================================
    // OPT descriptors
    // =========================================================================

    public static readonly ControlRoomReportDescriptor OPTArchitectureCleanupPlan = new()
    {
        ReportId = "ArchitectureCleanupPlan",
        DisplayTitle = "OPT Architecture Cleanup Plan",
        PrimaryPath = "eval/architecture-cleanup-plan.json",
        PhaseGroup = "OPT",
        EvalGateCommand = "eval architecture-cleanup-plan",
    };

    public static readonly ControlRoomReportDescriptor OPTDtoSplitPlan = new()
    {
        ReportId = "DtoSplitPlan",
        DisplayTitle = "OPT DTO Split Plan",
        PrimaryPath = "eval/dto-split-plan.json",
        PhaseGroup = "OPT",
        EvalGateCommand = "eval dto-split-plan",
    };

    public static readonly ControlRoomReportDescriptor OPTArchitectureCleanupFreeze = new()
    {
        ReportId = "ArchitectureCleanupFreeze",
        DisplayTitle = "OPT Architecture Cleanup Freeze",
        PrimaryPath = "eval/architecture-cleanup-freeze.json",
        GatePath = "eval/architecture-cleanup-freeze-gate.json",
        PhaseGroup = "OPT",
        EvalGateCommand = "eval architecture-cleanup-freeze",
    };

    public static readonly ControlRoomReportDescriptor OPTArchitectureCleanupFreezeGate = new()
    {
        ReportId = "ArchitectureCleanupFreezeGate",
        DisplayTitle = "OPT Architecture Cleanup Freeze Gate",
        PrimaryPath = "eval/architecture-cleanup-freeze-gate.json",
        PhaseGroup = "OPT",
        EvalGateCommand = "eval architecture-cleanup-freeze-gate",
    };

    // =========================================================================
    // Grouped accessors
    // =========================================================================

    public static IReadOnlyList<ControlRoomReportDescriptor> V5Descriptors { get; } = new[]
    {
        V5FormalRetrievalIntegrationPlan,
        V5FormalRetrievalIntegrationDecision,
        V5ShadowFormalRetrievalAdapterPlan,
        V5ShadowFormalRetrievalAdapter,
        V5FormalAdapterPackageShadowComparison,
        V5GraphVectorRetrievalQualityAudit,
        V5RetrievalQualityRepairPreview,
        V5RuntimeObservableFeatureContract,
        V5RuntimeRetrievalFeatureDerivation,
        V5RuntimeFeatureDerivationRepair,
        V5FormalRetrievalIntegrationFreeze,
        V5RuntimeFeatureDerivationFailureFreeze,
        V5GraphHubNoiseControl,
        V5RetrievalEvalProtocol,
        V5InputMetadataEnrichment,
        V5EnrichedCandidateSourceRepairRecheck,
        V5SourceAwareRankingRepair,
        V5OutputTokenPriorityShadow,
        V5FormalAdapterInputContract,
    };

    public static IReadOnlyList<ControlRoomReportDescriptor> OPTDescriptors { get; } = new[]
    {
        OPTArchitectureCleanupPlan,
        OPTDtoSplitPlan,
        OPTArchitectureCleanupFreeze,
        OPTArchitectureCleanupFreezeGate,
    };
}
