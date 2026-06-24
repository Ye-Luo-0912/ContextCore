using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreFormalRetrievalIntegrationDecisionTests
{
    [TestMethod]
    public void FormalRetrievalIntegrationDecision_CleanV5GatesPass()
    {
        var report = BuildCleanReport();

        Assert.IsTrue(report.DecisionPassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.AreEqual(
            FormalRetrievalIntegrationDecisionRecommendations.ReadyForFormalRetrievalIntegrationFreezeAndAdapterNoOpBindingPlan,
            report.Recommendation);
        Assert.AreEqual(
            FormalRetrievalIntegrationDecisions.ReadyForFormalRetrievalIntegrationFreezeAndAdapterNoOpBindingPlan,
            report.IntegrationDecision);
        Assert.IsTrue(report.ReadyForFormalRetrievalIntegrationFreeze);
        Assert.IsTrue(report.ReadyForAdapterNoOpBindingPlan);
        Assert.IsTrue(report.AdapterNoOpBindingPlanAllowed);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
        Assert.IsFalse(report.UseForRuntime);
        Assert.IsFalse(report.FormalVectorStoreBindingAllowed);
        Assert.IsFalse(report.FormalPackageWriteAllowed);
        Assert.IsFalse(report.PackingPolicyIntegrationAllowed);
        Assert.IsFalse(report.PackageOutputMutationAllowed);
        Assert.AreEqual(0, report.RiskAfterPolicy);
        Assert.AreEqual(0, report.FormalOutputChanged);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.RuntimeMutated);
        Assert.IsFalse(report.VectorStoreBindingChanged);
    }

    [TestMethod]
    public void FormalRetrievalIntegrationDecision_MissingV516ContractBlocks()
    {
        var report = BuildCleanReport(inputContract: new FormalAdapterInputContractReport { GatePassed = false });

        Assert.IsFalse(report.DecisionPassed);
        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(
            FormalRetrievalIntegrationDecisionRecommendations.BlockedByMissingV5Gate,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "V516FormalAdapterInputContractMissingOrNotPassed");
    }

    [TestMethod]
    public void FormalRetrievalIntegrationDecision_RiskBlocks()
    {
        var report = BuildCleanReport(outputPolicy: CleanOutputPolicy(riskAfterPolicy: 1));

        Assert.IsFalse(report.DecisionPassed);
        Assert.AreEqual(FormalRetrievalIntegrationDecisionRecommendations.BlockedByRisk, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RiskAfterPolicyNonZero");
    }

    [TestMethod]
    public void FormalRetrievalIntegrationDecision_V513QualityRegressionCanBeSupersededByV514()
    {
        var report = BuildCleanReport(sourceRepair: CleanSourceRepairGate(
            gatePassed: false,
            recommendation: EnrichedCandidateSourceRepairRecheckRecommendations.BlockedByQualityRegression));

        Assert.IsTrue(report.DecisionPassed, string.Join(",", report.BlockedReasons));
        var v513 = report.Gates.Single(gate => gate.GateId == "V513EnrichedCandidateSourceRepair");
        Assert.IsTrue(v513.Passed);
        Assert.AreEqual("SupersededByV514SourceAwareRankingRepair", v513.Recommendation);
        Assert.AreEqual("V514SourceAwareRankingRepair", v513.SupersededBy);
    }

    [TestMethod]
    public void FormalRetrievalIntegrationDecision_V513RiskCannotBeSupersededByV514()
    {
        var report = BuildCleanReport(sourceRepair: CleanSourceRepairGate(
            gatePassed: false,
            recommendation: EnrichedCandidateSourceRepairRecheckRecommendations.BlockedByQualityRegression,
            riskAfterPolicy: 1));

        Assert.IsFalse(report.DecisionPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "V513EnrichedCandidateSourceRepairMissingOrNotPassed");
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RiskAfterPolicyNonZero");
    }

    [TestMethod]
    public void FormalRetrievalIntegrationDecision_FormalRetrievalAttemptBlocks()
    {
        var report = BuildCleanReport(inputContract: CleanInputContract(formalRetrievalAllowed: true));

        Assert.IsFalse(report.DecisionPassed);
        Assert.AreEqual(FormalRetrievalIntegrationDecisionRecommendations.BlockedByRuntimeInvariant, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "FormalRetrievalAllowed");
    }

    [TestMethod]
    public void FormalRetrievalIntegrationDecision_RuntimeSwitchAttemptBlocks()
    {
        var report = BuildCleanReport(projectState: CleanProjectState(runtimeSwitchAllowed: true));

        Assert.IsFalse(report.DecisionPassed);
        Assert.AreEqual(FormalRetrievalIntegrationDecisionRecommendations.BlockedByMissingV5Gate, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "V50ProjectStateAuditMissingOrNotPassed");
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeSwitchAllowed");
    }

    [TestMethod]
    public void FormalRetrievalIntegrationDecision_SourceDoesNotSpecialCaseSamplesOrItems()
    {
        var source = File.ReadAllText(ResolveRepoFile(
            "src",
            "ContextCore.Core",
            "Services",
            "Vector",
            "FormalRetrievalIntegrationDecisionRunner.cs"));

        Assert.IsFalse(source.Contains("sample.SampleId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("item.ItemId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("mustHitItemId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-pkg", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-shadow", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-audit", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-repair", StringComparison.Ordinal));
    }

    private static FormalRetrievalIntegrationDecisionReport BuildCleanReport(
        ProjectStateAuditReport? projectState = null,
        EnrichedCandidateSourceRepairRecheckReport? sourceRepair = null,
        OutputTokenPriorityShadowGateReport? outputPolicy = null,
        FormalAdapterInputContractReport? inputContract = null)
        => new FormalRetrievalIntegrationDecisionRunner().BuildGate(
            projectState ?? CleanProjectState(),
            CleanIntegrationPlan(),
            CleanAdapterPlan(),
            CleanProtocolGate(),
            CleanEnrichmentGate(),
            sourceRepair ?? CleanSourceRepairGate(),
            CleanRankingGate(),
            outputPolicy ?? CleanOutputPolicy(),
            inputContract ?? CleanInputContract(),
            CleanRuntimeChangeGate(),
            p15GatePassed: true,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static ProjectStateAuditReport CleanProjectState(bool runtimeSwitchAllowed = false)
        => new()
        {
            CurrentOverallStatus = "FoundationFrozen_FormalRetrievalPlanOnly",
            Recommendation = ProjectStateAuditRecommendations.ReadyForMainlineGapRepairPlanning,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
            ReadyForRuntimeSwitch = false,
            PackingPolicyChanged = false,
            PackageOutputChanged = false,
            BlockedReasons = Array.Empty<string>()
        };

    private static FormalRetrievalIntegrationPlanReport CleanIntegrationPlan()
        => new()
        {
            PlanPassed = true,
            Recommendation = FormalRetrievalIntegrationPlanRecommendations.ReadyForShadowFormalRetrievalAdapter,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            FormalOutputChanged = 0,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            VectorStoreBindingChanged = false,
            FormalPackageWritten = false
        };

    private static ShadowFormalRetrievalAdapterPlanReport CleanAdapterPlan()
        => new()
        {
            PlanPassed = true,
            Recommendation = ShadowFormalRetrievalAdapterPlanRecommendations.ReadyForShadowAdapterDesignFreeze,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            VectorStoreBindingChanged = false,
            FormalPackageWritten = false
        };

    private static RetrievalEvalProtocolGateReport CleanProtocolGate()
        => new()
        {
            GatePassed = true,
            Recommendation = RetrievalEvalProtocolRecommendations.ReadyForSourceRepairRecheck,
            RiskAfterPolicy = 0,
            MustNotHitRiskAfterPolicy = 0,
            LifecycleRiskAfterPolicy = 0,
            FormalOutputChanged = 0,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false
        };

    private static InputMetadataEnrichmentPreviewReport CleanEnrichmentGate()
        => new()
        {
            GatePassed = true,
            Recommendation = InputMetadataEnrichmentPreviewRecommendations.ReadyForSourceRepairRecheck,
            RiskAfterPolicy = 0,
            MustNotHitRiskAfterPolicy = 0,
            LifecycleRiskAfterPolicy = 0,
            FormalOutputChanged = 0,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false
        };

    private static EnrichedCandidateSourceRepairRecheckReport CleanSourceRepairGate(
        bool gatePassed = true,
        string? recommendation = null,
        int riskAfterPolicy = 0)
        => new()
        {
            GatePassed = gatePassed,
            Recommendation = recommendation ?? EnrichedCandidateSourceRepairRecheckRecommendations.ReadyForSourceRepairRecheckFreeze,
            RiskAfterPolicy = riskAfterPolicy,
            MustNotHitRiskAfterPolicy = 0,
            LifecycleRiskAfterPolicy = 0,
            FormalOutputChanged = 0,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false
        };

    private static SourceAwareRankingRepairReport CleanRankingGate()
        => new()
        {
            GatePassed = true,
            Recommendation = SourceAwareRankingRepairRecommendations.ReadyForSourceAwareRankingFreeze,
            RiskAfterPolicy = 0,
            MustNotHitRiskAfterPolicy = 0,
            LifecycleRiskAfterPolicy = 0,
            FormalOutputChanged = 0,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false
        };

    private static OutputTokenPriorityShadowGateReport CleanOutputPolicy(int riskAfterPolicy = 0)
        => new()
        {
            GatePassed = true,
            Recommendation = OutputTokenPriorityShadowGateRecommendations.ReadyForOutputPolicyShadowFreeze,
            RiskAfterPolicy = riskAfterPolicy,
            MustNotHitRiskAfterPolicy = 0,
            LifecycleRiskAfterPolicy = 0,
            FormalOutputChanged = 0,
            FormalSelectedSetChanged = false,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false
        };

    private static FormalAdapterInputContractReport CleanInputContract(bool formalRetrievalAllowed = false)
        => new()
        {
            GatePassed = true,
            ContractPassed = true,
            Recommendation = FormalAdapterInputContractRecommendations.ReadyForFormalAdapterInputContractFreeze,
            FormalOutputChanged = 0,
            FormalSelectedSetChanged = false,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalRetrievalAllowed = formalRetrievalAllowed,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false
        };

    private static LearningRuntimeChangeReadinessGateReport CleanRuntimeChangeGate()
        => new()
        {
            Passed = true,
            Recommendation = "RuntimeChangeRulesSatisfied"
        };

    private static string ResolveRepoFile(params string[] segments)
    {
        var current = Directory.GetCurrentDirectory();
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(new[] { current }.Concat(segments).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        return Path.Combine(new[] { Directory.GetCurrentDirectory() }.Concat(segments).ToArray());
    }
}
