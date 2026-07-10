using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V5 mainline decision gate：汇总 V5.0-V5.16，只决定是否进入 formal retrieval integration freeze
/// 与 adapter no-op binding plan；不授权 runtime switch 或正式检索接入。
/// </summary>
public sealed class FormalRetrievalIntegrationDecisionRunner
{
    public FormalRetrievalIntegrationDecisionReport BuildDecision(
        ProjectStateAuditReport? projectStateAudit,
        FormalRetrievalIntegrationPlanReport? integrationPlanGate,
        ShadowFormalRetrievalAdapterPlanReport? adapterPlanGate,
        RetrievalEvalProtocolGateReport? protocolGate,
        InputMetadataEnrichmentPreviewReport? enrichmentGate,
        EnrichedCandidateSourceRepairRecheckReport? sourceRepairGate,
        SourceAwareRankingRepairReport? rankingGate,
        OutputTokenPriorityShadowGateReport? outputPolicyGate,
        FormalAdapterInputContractReport? inputContractGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        bool p15GatePassed,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(
            projectStateAudit,
            integrationPlanGate,
            adapterPlanGate,
            protocolGate,
            enrichmentGate,
            sourceRepairGate,
            rankingGate,
            outputPolicyGate,
            inputContractGate,
            runtimeChangeGate,
            p15GatePassed,
            sourceReports,
            gateMode: false);

    public FormalRetrievalIntegrationDecisionReport BuildGate(
        ProjectStateAuditReport? projectStateAudit,
        FormalRetrievalIntegrationPlanReport? integrationPlanGate,
        ShadowFormalRetrievalAdapterPlanReport? adapterPlanGate,
        RetrievalEvalProtocolGateReport? protocolGate,
        InputMetadataEnrichmentPreviewReport? enrichmentGate,
        EnrichedCandidateSourceRepairRecheckReport? sourceRepairGate,
        SourceAwareRankingRepairReport? rankingGate,
        OutputTokenPriorityShadowGateReport? outputPolicyGate,
        FormalAdapterInputContractReport? inputContractGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        bool p15GatePassed,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(
            projectStateAudit,
            integrationPlanGate,
            adapterPlanGate,
            protocolGate,
            enrichmentGate,
            sourceRepairGate,
            rankingGate,
            outputPolicyGate,
            inputContractGate,
            runtimeChangeGate,
            p15GatePassed,
            sourceReports,
            gateMode: true);

    public static string BuildMarkdown(string title, FormalRetrievalIntegrationDecisionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.CreatedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine($"- DecisionPassed: `{report.DecisionPassed}`");
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- IntegrationDecision: `{report.IntegrationDecision}`");
        builder.AppendLine($"- CurrentOverallStatus: `{report.CurrentOverallStatus}`");
        builder.AppendLine($"- AllowedMode: `{report.AllowedMode}`");
        builder.AppendLine($"- NextAllowedPhase: `{report.NextAllowedPhase}`");
        builder.AppendLine($"- ReadyForFormalRetrievalIntegrationFreeze: `{report.ReadyForFormalRetrievalIntegrationFreeze}`");
        builder.AppendLine($"- ReadyForAdapterNoOpBindingPlan: `{report.ReadyForAdapterNoOpBindingPlan}`");
        builder.AppendLine($"- AdapterNoOpBindingPlanAllowed: `{report.AdapterNoOpBindingPlanAllowed}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- FormalVectorStoreBindingAllowed: `{report.FormalVectorStoreBindingAllowed}`");
        builder.AppendLine($"- FormalPackageWriteAllowed: `{report.FormalPackageWriteAllowed}`");
        builder.AppendLine($"- PackingPolicyIntegrationAllowed: `{report.PackingPolicyIntegrationAllowed}`");
        builder.AppendLine($"- PackageOutputMutationAllowed: `{report.PackageOutputMutationAllowed}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- MustNotHitRiskAfterPolicy: `{report.MustNotHitRiskAfterPolicy}`");
        builder.AppendLine($"- LifecycleRiskAfterPolicy: `{report.LifecycleRiskAfterPolicy}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- FormalSelectedSetChanged: `{report.FormalSelectedSetChanged}`");
        builder.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        builder.AppendLine($"- VectorStoreBindingChanged: `{report.VectorStoreBindingChanged}`");
        builder.AppendLine($"- RuntimeChangeGatePassed: `{report.RuntimeChangeGatePassed}`");
        builder.AppendLine($"- P15GatePassed: `{report.P15GatePassed}`");
        AppendGateStatuses(builder, report.Gates);
        AppendList(builder, "Allowed Actions", report.AllowedActions);
        AppendList(builder, "Forbidden Actions", report.ForbiddenActions);
        AppendMap(builder, "Source Reports", report.SourceReports);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("Decision scope: allow planning for formal retrieval integration freeze and adapter no-op binding only. Formal retrieval, runtime switch, formal package writes, PackingPolicy mutation, package output mutation, and formal vector-store binding remain forbidden.");
        return builder.ToString();
    }

    private static FormalRetrievalIntegrationDecisionReport Build(
        ProjectStateAuditReport? projectStateAudit,
        FormalRetrievalIntegrationPlanReport? integrationPlanGate,
        ShadowFormalRetrievalAdapterPlanReport? adapterPlanGate,
        RetrievalEvalProtocolGateReport? protocolGate,
        InputMetadataEnrichmentPreviewReport? enrichmentGate,
        EnrichedCandidateSourceRepairRecheckReport? sourceRepairGate,
        SourceAwareRankingRepairReport? rankingGate,
        OutputTokenPriorityShadowGateReport? outputPolicyGate,
        FormalAdapterInputContractReport? inputContractGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        bool p15GatePassed,
        IReadOnlyDictionary<string, string>? sourceReports,
        bool gateMode)
    {
        var reports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var gates = BuildGateStatuses(
            reports,
            projectStateAudit,
            integrationPlanGate,
            adapterPlanGate,
            protocolGate,
            enrichmentGate,
            sourceRepairGate,
            rankingGate,
            outputPolicyGate,
            inputContractGate,
            runtimeChangeGate,
            p15GatePassed);
        var blocked = new List<string>();
        foreach (var gate in gates.Where(static gate => !gate.Passed))
        {
            blocked.Add(gate.GateId + "MissingOrNotPassed");
        }

        var riskAfterPolicy = Max(
            protocolGate?.RiskAfterPolicy,
            enrichmentGate?.RiskAfterPolicy,
            sourceRepairGate?.RiskAfterPolicy,
            rankingGate?.RiskAfterPolicy,
            outputPolicyGate?.RiskAfterPolicy);
        var mustNotRisk = Max(
            protocolGate?.MustNotHitRiskAfterPolicy,
            enrichmentGate?.MustNotHitRiskAfterPolicy,
            sourceRepairGate?.MustNotHitRiskAfterPolicy,
            rankingGate?.MustNotHitRiskAfterPolicy,
            outputPolicyGate?.MustNotHitRiskAfterPolicy);
        var lifecycleRisk = Max(
            protocolGate?.LifecycleRiskAfterPolicy,
            enrichmentGate?.LifecycleRiskAfterPolicy,
            sourceRepairGate?.LifecycleRiskAfterPolicy,
            rankingGate?.LifecycleRiskAfterPolicy,
            outputPolicyGate?.LifecycleRiskAfterPolicy);
        var formalOutputChanged = Max(
            integrationPlanGate?.FormalOutputChanged,
            protocolGate?.FormalOutputChanged,
            enrichmentGate?.FormalOutputChanged,
            sourceRepairGate?.FormalOutputChanged,
            rankingGate?.FormalOutputChanged,
            outputPolicyGate?.FormalOutputChanged,
            inputContractGate?.FormalOutputChanged);
        var formalSelectedSetChanged = outputPolicyGate?.FormalSelectedSetChanged ?? false;
        var formalPackageWritten = Any(
            integrationPlanGate?.FormalPackageWritten,
            protocolGate?.FormalPackageWritten,
            enrichmentGate?.FormalPackageWritten,
            sourceRepairGate?.FormalPackageWritten,
            rankingGate?.FormalPackageWritten,
            outputPolicyGate?.FormalPackageWritten,
            inputContractGate?.FormalPackageWritten);
        var packageOutputChanged = Any(
            projectStateAudit?.PackageOutputChanged,
            integrationPlanGate?.PackageOutputChanged,
            adapterPlanGate?.PackageOutputChanged,
            protocolGate?.PackageOutputChanged,
            enrichmentGate?.PackageOutputChanged,
            sourceRepairGate?.PackageOutputChanged,
            rankingGate?.PackageOutputChanged,
            outputPolicyGate?.PackageOutputChanged,
            inputContractGate?.PackageOutputChanged);
        var packingPolicyChanged = Any(
            projectStateAudit?.PackingPolicyChanged,
            integrationPlanGate?.PackingPolicyChanged,
            adapterPlanGate?.PackingPolicyChanged,
            protocolGate?.PackingPolicyChanged,
            enrichmentGate?.PackingPolicyChanged,
            sourceRepairGate?.PackingPolicyChanged,
            rankingGate?.PackingPolicyChanged,
            outputPolicyGate?.PackingPolicyChanged,
            inputContractGate?.PackingPolicyChanged);
        var runtimeMutated = Any(
            protocolGate?.RuntimeMutated,
            enrichmentGate?.RuntimeMutated,
            sourceRepairGate?.RuntimeMutated,
            rankingGate?.RuntimeMutated,
            outputPolicyGate?.RuntimeMutated,
            inputContractGate?.RuntimeMutated);
        var vectorStoreBindingChanged = Any(
            integrationPlanGate?.VectorStoreBindingChanged,
            adapterPlanGate?.VectorStoreBindingChanged,
            protocolGate?.VectorStoreBindingChanged,
            enrichmentGate?.VectorStoreBindingChanged,
            sourceRepairGate?.VectorStoreBindingChanged,
            rankingGate?.VectorStoreBindingChanged,
            outputPolicyGate?.VectorStoreBindingChanged,
            inputContractGate?.VectorStoreBindingChanged);
        var formalRetrievalAllowed = Any(
            projectStateAudit?.FormalRetrievalAllowed,
            integrationPlanGate?.FormalRetrievalAllowed,
            adapterPlanGate?.FormalRetrievalAllowed,
            protocolGate?.FormalRetrievalAllowed,
            enrichmentGate?.FormalRetrievalAllowed,
            sourceRepairGate?.FormalRetrievalAllowed,
            rankingGate?.FormalRetrievalAllowed,
            outputPolicyGate?.FormalRetrievalAllowed,
            inputContractGate?.FormalRetrievalAllowed);
        var runtimeSwitchAllowed = Any(
            projectStateAudit?.RuntimeSwitchAllowed,
            integrationPlanGate?.RuntimeSwitchAllowed,
            adapterPlanGate?.RuntimeSwitchAllowed,
            protocolGate?.RuntimeSwitchAllowed,
            enrichmentGate?.RuntimeSwitchAllowed,
            sourceRepairGate?.RuntimeSwitchAllowed,
            rankingGate?.RuntimeSwitchAllowed,
            outputPolicyGate?.RuntimeSwitchAllowed,
            inputContractGate?.RuntimeSwitchAllowed);
        var readyForRuntimeSwitch = Any(
            projectStateAudit?.ReadyForRuntimeSwitch,
            integrationPlanGate?.ReadyForRuntimeSwitch,
            adapterPlanGate?.ReadyForRuntimeSwitch,
            protocolGate?.ReadyForRuntimeSwitch,
            enrichmentGate?.ReadyForRuntimeSwitch,
            sourceRepairGate?.ReadyForRuntimeSwitch,
            rankingGate?.ReadyForRuntimeSwitch,
            outputPolicyGate?.ReadyForRuntimeSwitch,
            inputContractGate?.ReadyForRuntimeSwitch);
        var useForRuntime = Any(
            integrationPlanGate?.UseForRuntime,
            adapterPlanGate?.UseForRuntime,
            protocolGate?.UseForRuntime,
            enrichmentGate?.UseForRuntime,
            sourceRepairGate?.UseForRuntime,
            rankingGate?.UseForRuntime,
            outputPolicyGate?.UseForRuntime,
            inputContractGate?.UseForRuntime);

        AddReasonIf(blocked, riskAfterPolicy > 0, "RiskAfterPolicyNonZero");
        AddReasonIf(blocked, mustNotRisk > 0, "MustNotHitRiskAfterPolicyNonZero");
        AddReasonIf(blocked, lifecycleRisk > 0, "LifecycleRiskAfterPolicyNonZero");
        AddReasonIf(blocked, formalOutputChanged > 0, "FormalOutputChangedNonZero");
        AddReasonIf(blocked, formalSelectedSetChanged, "FormalSelectedSetChanged");
        AddReasonIf(blocked, formalPackageWritten, "FormalPackageWritten");
        AddReasonIf(blocked, packageOutputChanged, "PackageOutputChanged");
        AddReasonIf(blocked, packingPolicyChanged, "PackingPolicyChanged");
        AddReasonIf(blocked, runtimeMutated, "RuntimeMutated");
        AddReasonIf(blocked, vectorStoreBindingChanged, "VectorStoreBindingChanged");
        AddReasonIf(blocked, formalRetrievalAllowed, "FormalRetrievalAllowed");
        AddReasonIf(blocked, runtimeSwitchAllowed, "RuntimeSwitchAllowed");
        AddReasonIf(blocked, readyForRuntimeSwitch, "ReadyForRuntimeSwitchUnexpected");
        AddReasonIf(blocked, useForRuntime, "UseForRuntimeUnexpected");

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = distinctBlocked.Length == 0;
        var decision = passed
            ? FormalRetrievalIntegrationDecisions.ReadyForFormalRetrievalIntegrationFreezeAndAdapterNoOpBindingPlan
            : FormalRetrievalIntegrationDecisions.KeepPreviewOnly;

        return new FormalRetrievalIntegrationDecisionReport
        {
            OperationId = (gateMode
                ? "formal-retrieval-integration-decision-gate-"
                : "formal-retrieval-integration-decision-") + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            DecisionPassed = passed,
            GatePassed = gateMode && passed,
            Recommendation = ResolveRecommendation(distinctBlocked, passed),
            IntegrationDecision = decision,
            CurrentOverallStatus = projectStateAudit?.CurrentOverallStatus ?? string.Empty,
            AllowedMode = "DecisionOnly",
            NextAllowedPhase = passed
                ? "FormalRetrievalIntegrationFreeze / AdapterNoOpBindingPlan"
                : "KeepPreviewOnly",
            ReadyForFormalRetrievalIntegrationFreeze = passed,
            ReadyForAdapterNoOpBindingPlan = passed,
            AdapterNoOpBindingPlanAllowed = passed,
            FormalRetrievalIntegrationFreezeAllowed = passed,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            FormalVectorStoreBindingAllowed = false,
            FormalPackageWriteAllowed = false,
            PackingPolicyIntegrationAllowed = false,
            PackageOutputMutationAllowed = false,
            RiskAfterPolicy = riskAfterPolicy,
            MustNotHitRiskAfterPolicy = mustNotRisk,
            LifecycleRiskAfterPolicy = lifecycleRisk,
            FormalOutputChanged = formalOutputChanged,
            FormalSelectedSetChanged = formalSelectedSetChanged,
            FormalPackageWritten = formalPackageWritten,
            PackageOutputChanged = packageOutputChanged,
            PackingPolicyChanged = packingPolicyChanged,
            RuntimeMutated = runtimeMutated,
            VectorStoreBindingChanged = vectorStoreBindingChanged,
            RuntimeChangeGatePassed = runtimeChangeGate?.Passed ?? false,
            P15GatePassed = p15GatePassed,
            V50ProjectStateAuditPassed = gates.Any(static gate => gate.GateId == "V50ProjectStateAudit" && gate.Passed),
            V50FormalIntegrationPlanGatePassed = gates.Any(static gate => gate.GateId == "V50FormalRetrievalIntegrationPlan" && gate.Passed),
            V51ShadowAdapterPlanGatePassed = gates.Any(static gate => gate.GateId == "V51ShadowFormalRetrievalAdapterPlan" && gate.Passed),
            V511RetrievalEvalProtocolGatePassed = gates.Any(static gate => gate.GateId == "V511RetrievalEvalProtocol" && gate.Passed),
            V512InputMetadataEnrichmentGatePassed = gates.Any(static gate => gate.GateId == "V512InputMetadataEnrichment" && gate.Passed),
            V513EnrichedSourceRepairGatePassed = gates.Any(static gate => gate.GateId == "V513EnrichedCandidateSourceRepair" && gate.Passed),
            V514SourceAwareRankingGatePassed = gates.Any(static gate => gate.GateId == "V514SourceAwareRankingRepair" && gate.Passed),
            V515OutputTokenPriorityGatePassed = gates.Any(static gate => gate.GateId == "V515OutputTokenPriorityShadow" && gate.Passed),
            V516AdapterInputContractGatePassed = gates.Any(static gate => gate.GateId == "V516FormalAdapterInputContract" && gate.Passed),
            Gates = gates,
            AllowedActions =
            [
                "Plan formal retrieval integration freeze",
                "Plan adapter no-op binding only",
                "Define DI registration shape without enabling runtime",
                "Define trace and rollback checks for no-op adapter path"
            ],
            ForbiddenActions =
            [
                "Enable formal retrieval",
                "Switch runtime retrieval provider",
                "Bind IVectorIndexStore as formal runtime store",
                "Write formal package",
                "Mutate PackingPolicy",
                "Mutate package output",
                "Use Dataset/Eval/gold/shadow fields as runtime adapter input",
                "Global default-on"
            ],
            SourceReports = reports,
            BlockedReasons = distinctBlocked
        };
    }

    private static IReadOnlyList<FormalRetrievalIntegrationDecisionGateStatus> BuildGateStatuses(
        IReadOnlyDictionary<string, string> sourceReports,
        ProjectStateAuditReport? projectStateAudit,
        FormalRetrievalIntegrationPlanReport? integrationPlanGate,
        ShadowFormalRetrievalAdapterPlanReport? adapterPlanGate,
        RetrievalEvalProtocolGateReport? protocolGate,
        InputMetadataEnrichmentPreviewReport? enrichmentGate,
        EnrichedCandidateSourceRepairRecheckReport? sourceRepairGate,
        SourceAwareRankingRepairReport? rankingGate,
        OutputTokenPriorityShadowGateReport? outputPolicyGate,
        FormalAdapterInputContractReport? inputContractGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        bool p15GatePassed)
    {
        var v513SupersededByV514 = IsV513SourceRepairSupersededByV514(sourceRepairGate, rankingGate);
        return
        [
            Gate("V50ProjectStateAudit", ProjectStatePassed(projectStateAudit), projectStateAudit?.Recommendation, sourceReports),
            Gate("V50FormalRetrievalIntegrationPlan", integrationPlanGate?.PlanPassed == true, integrationPlanGate?.Recommendation, sourceReports),
            Gate("V51ShadowFormalRetrievalAdapterPlan", adapterPlanGate?.PlanPassed == true, adapterPlanGate?.Recommendation, sourceReports),
            Gate("V511RetrievalEvalProtocol", protocolGate?.GatePassed == true, protocolGate?.Recommendation, sourceReports),
            Gate("V512InputMetadataEnrichment", enrichmentGate?.GatePassed == true, enrichmentGate?.Recommendation, sourceReports),
            Gate(
                "V513EnrichedCandidateSourceRepair",
                sourceRepairGate?.GatePassed == true || v513SupersededByV514,
                v513SupersededByV514
                    ? "SupersededByV514SourceAwareRankingRepair"
                    : sourceRepairGate?.Recommendation,
                sourceReports,
                v513SupersededByV514 ? "V514SourceAwareRankingRepair" : string.Empty),
            Gate("V514SourceAwareRankingRepair", rankingGate?.GatePassed == true, rankingGate?.Recommendation, sourceReports),
            Gate("V515OutputTokenPriorityShadow", outputPolicyGate?.GatePassed == true, outputPolicyGate?.Recommendation, sourceReports),
            Gate("V516FormalAdapterInputContract", inputContractGate?.GatePassed == true, inputContractGate?.Recommendation, sourceReports),
            Gate("RuntimeChangeGate", runtimeChangeGate?.Passed == true, runtimeChangeGate?.Recommendation, sourceReports),
            Gate("P15Gate", p15GatePassed, p15GatePassed ? "Passed" : "NotPassed", sourceReports)
        ];
    }

    private static bool IsV513SourceRepairSupersededByV514(
        EnrichedCandidateSourceRepairRecheckReport? sourceRepairGate,
        SourceAwareRankingRepairReport? rankingGate)
    {
        if (sourceRepairGate is null
            || sourceRepairGate.GatePassed
            || rankingGate?.GatePassed != true)
        {
            return false;
        }

        if (!string.Equals(
                sourceRepairGate.Recommendation,
                EnrichedCandidateSourceRepairRecheckRecommendations.BlockedByQualityRegression,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                rankingGate.Recommendation,
                SourceAwareRankingRepairRecommendations.ReadyForSourceAwareRankingFreeze,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return sourceRepairGate.RiskAfterPolicy == 0
               && sourceRepairGate.MustNotHitRiskAfterPolicy == 0
               && sourceRepairGate.LifecycleRiskAfterPolicy == 0
               && sourceRepairGate.FormalOutputChanged == 0
               && !sourceRepairGate.FormalPackageWritten
               && !sourceRepairGate.PackageOutputChanged
               && !sourceRepairGate.PackingPolicyChanged
               && !sourceRepairGate.RuntimeMutated
               && !sourceRepairGate.VectorStoreBindingChanged
               && !sourceRepairGate.FormalRetrievalAllowed
               && !sourceRepairGate.RuntimeSwitchAllowed
               && !sourceRepairGate.ReadyForRuntimeSwitch
               && !sourceRepairGate.UseForRuntime;
    }

    private static bool ProjectStatePassed(ProjectStateAuditReport? report)
        => report is not null
           && string.Equals(
               report.Recommendation,
               ProjectStateAuditRecommendations.ReadyForMainlineGapRepairPlanning,
               StringComparison.OrdinalIgnoreCase)
           && !report.FormalRetrievalAllowed
           && !report.RuntimeSwitchAllowed
           && !report.ReadyForRuntimeSwitch
           && !report.PackingPolicyChanged
           && !report.PackageOutputChanged
           && report.BlockedReasons.Count == 0;

    private static FormalRetrievalIntegrationDecisionGateStatus Gate(
        string gateId,
        bool passed,
        string? recommendation,
        IReadOnlyDictionary<string, string> sourceReports,
        string supersededBy = "")
        => new()
        {
            GateId = gateId,
            Passed = passed,
            Recommendation = recommendation ?? string.Empty,
            SourcePath = sourceReports.TryGetValue(gateId, out var sourcePath) ? sourcePath : string.Empty,
            SupersededBy = supersededBy
        };

    private static string ResolveRecommendation(IReadOnlyList<string> blocked, bool passed)
    {
        if (passed)
        {
            return FormalRetrievalIntegrationDecisionRecommendations.ReadyForFormalRetrievalIntegrationFreezeAndAdapterNoOpBindingPlan;
        }

        if (blocked.Any(static value => value.EndsWith("MissingOrNotPassed", StringComparison.OrdinalIgnoreCase)))
        {
            return FormalRetrievalIntegrationDecisionRecommendations.BlockedByMissingV5Gate;
        }

        if (blocked.Any(static value => value.Contains("Risk", StringComparison.OrdinalIgnoreCase)))
        {
            return FormalRetrievalIntegrationDecisionRecommendations.BlockedByRisk;
        }

        if (blocked.Contains("FormalOutputChangedNonZero", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("FormalSelectedSetChanged", StringComparer.OrdinalIgnoreCase))
        {
            return FormalRetrievalIntegrationDecisionRecommendations.BlockedByFormalOutputChange;
        }

        if (blocked.Any(static value => value.Contains("Package", StringComparison.OrdinalIgnoreCase)
            || value.Contains("PackingPolicy", StringComparison.OrdinalIgnoreCase)))
        {
            return FormalRetrievalIntegrationDecisionRecommendations.BlockedByPackageOrPolicyChange;
        }

        if (blocked.Any(static value => value.Contains("Runtime", StringComparison.OrdinalIgnoreCase)
            || value.Contains("VectorStoreBinding", StringComparison.OrdinalIgnoreCase)
            || value.Contains("UseForRuntime", StringComparison.OrdinalIgnoreCase)
            || value.Contains("FormalRetrieval", StringComparison.OrdinalIgnoreCase)))
        {
            return FormalRetrievalIntegrationDecisionRecommendations.BlockedByRuntimeInvariant;
        }

        return FormalRetrievalIntegrationDecisionRecommendations.KeepPreviewOnly;
    }

    private static int Max(params int?[] values)
        => values.Where(static value => value.HasValue).Select(static value => value!.Value).DefaultIfEmpty(0).Max();

    private static bool Any(params bool?[] values)
        => values.Any(static value => value == true);

    private static void AddReasonIf(List<string> blocked, bool condition, string reason)
    {
        if (condition)
        {
            blocked.Add(reason);
        }
    }

    private static void AppendGateStatuses(StringBuilder builder, IReadOnlyList<FormalRetrievalIntegrationDecisionGateStatus> gates)
    {
        builder.AppendLine();
        builder.AppendLine("## Gate Summary");
        foreach (var gate in gates)
        {
            builder.AppendLine($"- `{gate.GateId}` passed=`{gate.Passed}` recommendation=`{gate.Recommendation}` supersededBy=`{gate.SupersededBy}` source=`{gate.SourcePath}`");
        }
    }

    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var value in values)
        {
            builder.AppendLine($"- `{value}`");
        }
    }

    private static void AppendMap(StringBuilder builder, string title, IReadOnlyDictionary<string, string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var item in values.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {item.Key}: `{item.Value}`");
        }
    }
}

public sealed class FormalRetrievalIntegrationDecisionGateStatus
{
    public string GateId { get; init; } = string.Empty;

    public bool Passed { get; init; }

    public string Recommendation { get; init; } = string.Empty;

    public string SourcePath { get; init; } = string.Empty;

    public string SupersededBy { get; init; } = string.Empty;
}

public sealed class FormalRetrievalIntegrationDecisionReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool DecisionPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; } = FormalRetrievalIntegrationDecisionRecommendations.KeepPreviewOnly;

    public string IntegrationDecision { get; init; } = FormalRetrievalIntegrationDecisions.KeepPreviewOnly;

    public string CurrentOverallStatus { get; init; } = string.Empty;

    public string AllowedMode { get; init; } = "DecisionOnly";

    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public bool ReadyForFormalRetrievalIntegrationFreeze { get; init; }

    public bool ReadyForAdapterNoOpBindingPlan { get; init; }

    public bool AdapterNoOpBindingPlanAllowed { get; init; }

    public bool FormalRetrievalIntegrationFreezeAllowed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalVectorStoreBindingAllowed { get; init; }

    public bool FormalPackageWriteAllowed { get; init; }

    public bool PackingPolicyIntegrationAllowed { get; init; }

    public bool PackageOutputMutationAllowed { get; init; }

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

    public bool RuntimeChangeGatePassed { get; init; }

    public bool P15GatePassed { get; init; }

    public bool V50ProjectStateAuditPassed { get; init; }

    public bool V50FormalIntegrationPlanGatePassed { get; init; }

    public bool V51ShadowAdapterPlanGatePassed { get; init; }

    public bool V511RetrievalEvalProtocolGatePassed { get; init; }

    public bool V512InputMetadataEnrichmentGatePassed { get; init; }

    public bool V513EnrichedSourceRepairGatePassed { get; init; }

    public bool V514SourceAwareRankingGatePassed { get; init; }

    public bool V515OutputTokenPriorityGatePassed { get; init; }

    public bool V516AdapterInputContractGatePassed { get; init; }

    public IReadOnlyList<FormalRetrievalIntegrationDecisionGateStatus> Gates { get; init; } =
        Array.Empty<FormalRetrievalIntegrationDecisionGateStatus>();

    public IReadOnlyList<string> AllowedActions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public static class FormalRetrievalIntegrationDecisions
{
    public const string ReadyForFormalRetrievalIntegrationFreezeAndAdapterNoOpBindingPlan =
        nameof(ReadyForFormalRetrievalIntegrationFreezeAndAdapterNoOpBindingPlan);

    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

public static class FormalRetrievalIntegrationDecisionRecommendations
{
    public const string ReadyForFormalRetrievalIntegrationFreezeAndAdapterNoOpBindingPlan =
        nameof(ReadyForFormalRetrievalIntegrationFreezeAndAdapterNoOpBindingPlan);

    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingV5Gate = nameof(BlockedByMissingV5Gate);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOrPolicyChange = nameof(BlockedByPackageOrPolicyChange);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
}
