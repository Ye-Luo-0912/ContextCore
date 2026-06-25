namespace ContextCore.ControlRoom.Models;

public static class ReportSummaryRegistry
{
    // =========================================================================
    // V6 descriptors
    // =========================================================================

    public static readonly ControlRoomReportDescriptor V6SourceDiverseShadowAdapter = new()
    {
        ReportId = "SourceDiverseShadowAdapterValidation",
        DisplayTitle = "V6.6 Source-diverse Shadow Adapter Validation Summary",
        PrimaryPath = "vector/v6/source-diverse-shadow-adapter-validation.json",
        GatePath = "vector/v6/source-diverse-shadow-adapter-validation-gate.json",
        PhaseGroup = "V6",
        EvalGateCommand = "eval vector-source-diverse-shadow-adapter-validation-gate",
    };

    public static readonly ControlRoomReportDescriptor V6ShadowCandidateMergePreview = new()
    {
        ReportId = "ShadowCandidateMergePreview",
        DisplayTitle = "V6.7 Shadow Candidate Merge Preview Summary",
        PrimaryPath = "vector/v6/shadow-candidate-merge-preview.json",
        GatePath = "vector/v6/shadow-candidate-merge-preview-gate.json",
        PhaseGroup = "V6",
        EvalGateCommand = "eval vector-shadow-candidate-merge-preview-gate",
    };

    public static readonly ControlRoomReportDescriptor V6ShadowCandidateMergeObservation = new()
    {
        ReportId = "ShadowCandidateMergePreviewObservation",
        DisplayTitle = "V6.7 Shadow Candidate Merge Observation Summary",
        PrimaryPath = "vector/v6/shadow-candidate-merge-preview-observation.json",
        GatePath = "vector/v6/shadow-candidate-merge-preview-observation-gate.json",
        PhaseGroup = "V6",
        EvalGateCommand = "eval vector-shadow-candidate-merge-preview-observation-gate",
    };

    public static readonly ControlRoomReportDescriptor V6ShadowMergeStabilityFreeze = new()
    {
        ReportId = "ShadowMergeStabilityFreeze",
        DisplayTitle = "V6.7 Shadow Merge Stability Freeze / Promotion Decision",
        PrimaryPath = "vector/v6/shadow-merge-stability-freeze.json",
        DecisionPath = "vector/v6/shadow-merge-promotion-decision.json",
        PhaseGroup = "V6",
        EvalGateCommand = "eval vector-shadow-merge-stability-freeze",
    };

    public static readonly ControlRoomReportDescriptor V6ControlledShadowMergeProposal = new()
    {
        ReportId = "ControlledShadowMergeProposal",
        DisplayTitle = "V6.8 Controlled Shadow Merge Proposal",
        PrimaryPath = "vector/v6/controlled-shadow-merge-proposal.json",
        GatePath = "vector/v6/controlled-shadow-merge-proposal-gate.json",
        PhaseGroup = "V6",
        EvalGateCommand = "eval vector-controlled-shadow-merge-proposal-gate",
    };

    public static readonly ControlRoomReportDescriptor V6ControlledShadowMergeDryRun = new()
    {
        ReportId = "ControlledShadowMergeDryRun",
        DisplayTitle = "V6.10 Controlled Shadow Merge Dry-run Gate",
        PrimaryPath = "vector/v6/controlled-shadow-merge-dry-run.json",
        GatePath = "vector/v6/controlled-shadow-merge-dry-run-gate.json",
        PhaseGroup = "V6",
        EvalGateCommand = "eval vector-controlled-shadow-merge-dry-run-gate",
    };

    public static readonly ControlRoomReportDescriptor V6ControlledShadowMergeObservationWindow = new()
    {
        ReportId = "ControlledShadowMergeObservationWindow",
        DisplayTitle = "V6.11 Controlled Shadow Merge Observation Window",
        PrimaryPath = "vector/v6/controlled-shadow-merge-observation-window.json",
        GatePath = "vector/v6/controlled-shadow-merge-observation-window-gate.json",
        PhaseGroup = "V6",
        EvalGateCommand = "eval vector-controlled-shadow-merge-observation-window-gate",
    };

    public static readonly ControlRoomReportDescriptor V6ControlledShadowMergeFreeze = new()
    {
        ReportId = "ControlledShadowMergeFreeze",
        DisplayTitle = "V6.13 Controlled Shadow Merge Freeze / Promotion Decision",
        PrimaryPath = "vector/v6/controlled-shadow-merge-freeze.json",
        DecisionPath = "vector/v6/controlled-shadow-merge-promotion-decision.json",
        PhaseGroup = "V6",
        EvalGateCommand = "eval vector-controlled-shadow-merge-freeze",
    };

    public static readonly ControlRoomReportDescriptor V6ControlledAppliedMergeProposal = new()
    {
        ReportId = "ControlledAppliedMergeProposal",
        DisplayTitle = "V6.14 Controlled Applied Merge Proposal",
        PrimaryPath = "vector/v6/controlled-applied-merge-proposal.json",
        GatePath = "vector/v6/controlled-applied-merge-proposal-gate.json",
        PhaseGroup = "V6",
        EvalGateCommand = "eval vector-controlled-applied-merge-proposal-gate",
    };

    public static readonly ControlRoomReportDescriptor V6ControlledAppliedMergePreviewFreeze = new()
    {
        ReportId = "ControlledAppliedMergePreviewFreeze",
        DisplayTitle = "V6.14 Controlled Applied Merge Preview Freeze",
        PrimaryPath = "vector/v6/controlled-applied-merge-preview-freeze.json",
        PhaseGroup = "V6",
        EvalGateCommand = "eval vector-controlled-applied-merge-preview-freeze",
    };

    public static readonly ControlRoomReportDescriptor V6ControlledAppliedMergeScopedPreview = new()
    {
        ReportId = "ControlledAppliedMergeScopedPreview",
        DisplayTitle = "V6.15 Controlled Applied Merge Scoped Preview",
        PrimaryPath = "vector/v6/controlled-applied-merge-scoped-preview.json",
        GatePath = "vector/v6/controlled-applied-merge-scoped-preview-gate.json",
        PhaseGroup = "V6",
        EvalGateCommand = "eval vector-controlled-applied-merge-scoped-preview-gate",
    };

    // =========================================================================
    // V7 descriptors
    // =========================================================================

    public static readonly ControlRoomReportDescriptor V7ControlledAppliedMergeRuntimePreviewPlan = new()
    {
        ReportId = "ControlledAppliedMergeRuntimePreviewPlan",
        DisplayTitle = "V7.0 Controlled Applied Merge Runtime Preview Plan Summary",
        PrimaryPath = "vector/v7/runtime-preview-plan.json",
        GatePath = "vector/v7/runtime-preview-plan-gate.json",
        PhaseGroup = "V7",
        EvalGateCommand = "eval controlled-applied-merge-runtime-preview-plan-gate",
        EvalPlanCommand = "eval controlled-applied-merge-runtime-preview-plan",
    };

    public static readonly ControlRoomReportDescriptor V7ControlledAppliedMergeRuntimePreviewDryRun = new()
    {
        ReportId = "ControlledAppliedMergeRuntimePreviewDryRun",
        DisplayTitle = "V7.1 Controlled Applied Merge Runtime Preview Dry-run Summary",
        PrimaryPath = "vector/v7/runtime-preview-dry-run.json",
        GatePath = "vector/v7/runtime-preview-dry-run-gate.json",
        PhaseGroup = "V7",
        EvalGateCommand = "eval controlled-applied-merge-runtime-preview-dry-run-gate",
        EvalPlanCommand = "eval controlled-applied-merge-runtime-preview-dry-run",
    };

    public static readonly ControlRoomReportDescriptor V7ControlledAppliedMergeRuntimePreviewActivationPreflight = new()
    {
        ReportId = "ControlledAppliedMergeRuntimePreviewActivationPreflight",
        DisplayTitle = "V7.2 Controlled Applied Merge Runtime Preview Activation Preflight Summary",
        PrimaryPath = "vector/v7/activation-preflight.json",
        GatePath = "vector/v7/activation-preflight-gate.json",
        PhaseGroup = "V7",
        EvalGateCommand = "eval controlled-applied-merge-runtime-preview-activation-preflight-gate",
        EvalPlanCommand = "eval controlled-applied-merge-runtime-preview-activation-preflight",
    };

    public static readonly ControlRoomReportDescriptor V7ControlledAppliedMergeRuntimePreviewObservationWindow = new()
    {
        ReportId = "ControlledAppliedMergeRuntimePreviewObservationWindow",
        DisplayTitle = "V7.3 Controlled Applied Merge Runtime Preview Observation Window Summary",
        PrimaryPath = "vector/v7/observation-window.json",
        GatePath = "vector/v7/observation-window-gate.json",
        PhaseGroup = "V7",
        EvalGateCommand = "eval controlled-applied-merge-runtime-preview-observation-window-gate",
        EvalPlanCommand = "eval controlled-applied-merge-runtime-preview-observation-window",
    };

    public static readonly ControlRoomReportDescriptor V7ControlledAppliedMergeRuntimePreviewObservationHardening = new()
    {
        ReportId = "ControlledAppliedMergeRuntimePreviewObservationHardening",
        DisplayTitle = "V7.3R Controlled Applied Merge Runtime Preview Observation Hardening Summary",
        PrimaryPath = "vector/v7/observation-hardening.json",
        GatePath = "vector/v7/observation-hardening-gate.json",
        PhaseGroup = "V7",
        EvalGateCommand = "eval controlled-applied-merge-runtime-preview-observation-hardening-gate",
        EvalPlanCommand = "eval controlled-applied-merge-runtime-preview-observation-hardening",
    };

    public static readonly ControlRoomReportDescriptor V7ControlledAppliedMergeRuntimePreviewObservationFreeze = new()
    {
        ReportId = "ControlledAppliedMergeRuntimePreviewObservationFreeze",
        DisplayTitle = "V7.4 Controlled Applied Merge Runtime Preview Observation Freeze / Promotion Decision Summary",
        PrimaryPath = "vector/v7/observation-freeze.json",
        GatePath = "vector/v7/observation-freeze-gate.json",
        PhaseGroup = "V7",
        EvalGateCommand = "eval controlled-applied-merge-runtime-preview-observation-freeze-gate",
        EvalPlanCommand = "eval controlled-applied-merge-runtime-preview-observation-freeze",
    };

    public static readonly ControlRoomReportDescriptor V7ScopedRuntimePreviewApprovalPlan = new()
    {
        ReportId = "ScopedRuntimePreviewApprovalPlan",
        DisplayTitle = "V7.5 Scoped Runtime Preview Approval Plan Summary",
        PrimaryPath = "vector/v7/approval-plan.json",
        GatePath = "vector/v7/approval-plan-gate.json",
        PhaseGroup = "V7",
        EvalGateCommand = "eval scoped-runtime-preview-approval-plan-gate",
        EvalPlanCommand = "eval scoped-runtime-preview-approval-plan",
    };

    public static readonly ControlRoomReportDescriptor V7ScopedRuntimePreviewAuthorization = new()
    {
        ReportId = "ScopedRuntimePreviewAuthorization",
        DisplayTitle = "V7.6 Scoped Runtime Preview Authorization Summary",
        PrimaryPath = "vector/v7/authorization.json",
        GatePath = "vector/v7/authorization-gate.json",
        PhaseGroup = "V7",
        EvalGateCommand = "eval scoped-runtime-preview-authorization-gate",
        EvalPlanCommand = "eval scoped-runtime-preview-authorization",
    };

    public static readonly ControlRoomReportDescriptor V7ScopedRuntimePreviewAuthorizationHardening = new()
    {
        ReportId = "ScopedRuntimePreviewAuthorizationHardening",
        DisplayTitle = "V7.6R Scoped Runtime Preview Authorization Hardening Summary",
        PrimaryPath = "vector/v7/authorization-hardening.json",
        GatePath = "vector/v7/authorization-hardening-gate.json",
        PhaseGroup = "V7",
        EvalGateCommand = "eval scoped-runtime-preview-authorization-hardening-gate",
        EvalPlanCommand = "eval scoped-runtime-preview-authorization-hardening",
    };

    public static readonly ControlRoomReportDescriptor V7ScopedRuntimePreviewActivationPreparation = new()
    {
        ReportId = "ScopedRuntimePreviewActivationPreparation",
        DisplayTitle = "V7.7 Scoped Runtime Preview Activation Preparation Summary",
        PrimaryPath = "vector/v7/activation-preparation.json",
        GatePath = "vector/v7/activation-preparation-gate.json",
        PhaseGroup = "V7",
        EvalGateCommand = "eval scoped-runtime-preview-activation-preparation-gate",
        EvalPlanCommand = "eval scoped-runtime-preview-activation-preparation",
    };

    public static readonly ControlRoomReportDescriptor V7ScopedRuntimePreviewActivationDryRun = new()
    {
        ReportId = "ScopedRuntimePreviewActivationDryRun",
        DisplayTitle = "V7.8 Scoped Runtime Preview Activation Dry-run Summary",
        PrimaryPath = "vector/v7/activation-dry-run.json",
        GatePath = "vector/v7/activation-dry-run-gate.json",
        PhaseGroup = "V7",
        EvalGateCommand = "eval scoped-runtime-preview-activation-dry-run-gate",
        EvalPlanCommand = "eval scoped-runtime-preview-activation-dry-run",
    };

    public static readonly ControlRoomReportDescriptor V7ScopedRuntimePreviewActivationWindowPreflight = new()
    {
        ReportId = "ScopedRuntimePreviewActivationWindowPreflight",
        DisplayTitle = "V7.9 Scoped Runtime Preview Activation Window Preflight Summary",
        PrimaryPath = "vector/v7/activation-window-preflight.json",
        GatePath = "vector/v7/activation-window-preflight-gate.json",
        PhaseGroup = "V7",
        EvalGateCommand = "eval scoped-runtime-preview-activation-window-preflight-gate",
        EvalPlanCommand = "eval scoped-runtime-preview-activation-window-preflight",
    };

    public static readonly ControlRoomReportDescriptor V7ScopedRuntimePreviewActivationWindowNoOpExecution = new()
    {
        ReportId = "ScopedRuntimePreviewActivationWindowNoOpExecution",
        DisplayTitle = "V7.10 Scoped Runtime Preview Activation Window No-op Execution Summary",
        PrimaryPath = "vector/v7/activation-window-noop-execution.json",
        GatePath = "vector/v7/activation-window-noop-execution-gate.json",
        PhaseGroup = "V7",
        EvalGateCommand = "eval scoped-runtime-preview-activation-window-noop-execution-gate",
        EvalPlanCommand = "eval scoped-runtime-preview-activation-window-noop-execution",
    };

    public static readonly ControlRoomReportDescriptor V7ScopedRuntimePreviewActivationLiveReadinessFreeze = new()
    {
        ReportId = "ScopedRuntimePreviewActivationLiveReadinessFreeze",
        DisplayTitle = "V7.11 Scoped Runtime Preview Activation Live Readiness Freeze / Final Manual Approval Summary",
        PrimaryPath = "vector/v7/activation-live-readiness-freeze.json",
        GatePath = "vector/v7/activation-live-readiness-freeze-gate.json",
        PhaseGroup = "V7",
        EvalGateCommand = "eval scoped-runtime-preview-activation-live-readiness-freeze-gate",
        EvalPlanCommand = "eval scoped-runtime-preview-activation-live-readiness-freeze",
    };

    public static readonly ControlRoomReportDescriptor V7ScopedRuntimePreviewLiveActivationExecutionPlan = new()
    {
        ReportId = "ScopedRuntimePreviewLiveActivationExecutionPlan",
        DisplayTitle = "V7.12 Scoped Runtime Preview Live Activation Execution Plan Summary",
        PrimaryPath = "vector/v7/live-activation-execution-plan.json",
        GatePath = "vector/v7/live-activation-execution-plan-gate.json",
        PhaseGroup = "V7",
        EvalGateCommand = "eval scoped-runtime-preview-live-activation-execution-plan-gate",
        EvalPlanCommand = "eval scoped-runtime-preview-live-activation-execution-plan",
    };

    public static readonly ControlRoomReportDescriptor V7ScopedRuntimePreviewLiveActivationExecution = new()
    {
        ReportId = "ScopedRuntimePreviewLiveActivationExecution",
        DisplayTitle = "V7.13 Scoped Runtime Preview Live Activation Execution Gate Summary",
        PrimaryPath = "vector/v7/live-activation-execution.json",
        GatePath = "vector/v7/live-activation-execution-gate.json",
        PhaseGroup = "V7",
        EvalGateCommand = "eval scoped-runtime-preview-live-activation-execution-gate",
        EvalPlanCommand = "eval scoped-runtime-preview-live-activation-execution",
    };

    public static readonly ControlRoomReportDescriptor V7ScopedRuntimePreviewLiveActivationObservation = new()
    {
        ReportId = "ScopedRuntimePreviewLiveActivationObservation",
        DisplayTitle = "V7.14 Scoped Runtime Preview Live Activation Observation Summary",
        PrimaryPath = "vector/v7/live-activation-observation.json",
        GatePath = "vector/v7/live-activation-observation-gate.json",
        PhaseGroup = "V7",
        EvalGateCommand = "eval scoped-runtime-preview-live-activation-observation-gate",
        EvalPlanCommand = "eval scoped-runtime-preview-live-activation-observation",
    };

    public static readonly ControlRoomReportDescriptor V7ScopedRuntimePreviewLiveActivationSummaryFreeze = new()
    {
        ReportId = "ScopedRuntimePreviewLiveActivationSummaryFreeze",
        DisplayTitle = "V7.15 Scoped Runtime Preview Live Activation Summary Freeze",
        PrimaryPath = "vector/v7/live-activation-summary-freeze.json",
        GatePath = "vector/v7/live-activation-summary-freeze-gate.json",
        PhaseGroup = "V7",
        EvalGateCommand = "eval scoped-runtime-preview-live-activation-summary-freeze-gate",
        EvalPlanCommand = "eval scoped-runtime-preview-live-activation-summary-freeze",
    };

    public static readonly ControlRoomReportDescriptor V7ScopedRuntimePreviewLiveActivationCloseout = new()
    {
        ReportId = "ScopedRuntimePreviewLiveActivationCloseout",
        DisplayTitle = "V7.16 Scoped Runtime Preview Live Activation Closeout Summary",
        PrimaryPath = "vector/v7/live-activation-closeout.json",
        GatePath = "vector/v7/live-activation-closeout-gate.json",
        PhaseGroup = "V7",
        EvalGateCommand = "eval scoped-runtime-preview-live-activation-closeout-gate",
        EvalPlanCommand = "eval scoped-runtime-preview-live-activation-closeout",
    };

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

    public static IReadOnlyList<ControlRoomReportDescriptor> V6Descriptors { get; } = new[]
    {
        V6SourceDiverseShadowAdapter,
        V6ShadowCandidateMergePreview,
        V6ShadowCandidateMergeObservation,
        V6ShadowMergeStabilityFreeze,
        V6ControlledShadowMergeProposal,
        V6ControlledShadowMergeDryRun,
        V6ControlledShadowMergeObservationWindow,
        V6ControlledShadowMergeFreeze,
        V6ControlledAppliedMergeProposal,
        V6ControlledAppliedMergePreviewFreeze,
        V6ControlledAppliedMergeScopedPreview,
    };

    public static IReadOnlyList<ControlRoomReportDescriptor> V7Descriptors { get; } = new[]
    {
        V7ControlledAppliedMergeRuntimePreviewPlan,
        V7ControlledAppliedMergeRuntimePreviewDryRun,
        V7ControlledAppliedMergeRuntimePreviewActivationPreflight,
        V7ControlledAppliedMergeRuntimePreviewObservationWindow,
        V7ControlledAppliedMergeRuntimePreviewObservationHardening,
        V7ControlledAppliedMergeRuntimePreviewObservationFreeze,
        V7ScopedRuntimePreviewApprovalPlan,
        V7ScopedRuntimePreviewAuthorization,
        V7ScopedRuntimePreviewAuthorizationHardening,
        V7ScopedRuntimePreviewActivationPreparation,
        V7ScopedRuntimePreviewActivationDryRun,
        V7ScopedRuntimePreviewActivationWindowPreflight,
        V7ScopedRuntimePreviewActivationWindowNoOpExecution,
        V7ScopedRuntimePreviewActivationLiveReadinessFreeze,
        V7ScopedRuntimePreviewLiveActivationExecutionPlan,
        V7ScopedRuntimePreviewLiveActivationExecution,
        V7ScopedRuntimePreviewLiveActivationObservation,
        V7ScopedRuntimePreviewLiveActivationSummaryFreeze,
        V7ScopedRuntimePreviewLiveActivationCloseout,
    };

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
