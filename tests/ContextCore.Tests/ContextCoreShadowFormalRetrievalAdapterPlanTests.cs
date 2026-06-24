using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreShadowFormalRetrievalAdapterPlanTests
{
    [TestMethod]
    public void ShadowFormalRetrievalAdapterPlan_CleanPrerequisitesPass()
    {
        var report = BuildCleanReport();

        Assert.IsTrue(report.PlanPassed);
        Assert.AreEqual(
            ShadowFormalRetrievalAdapterPlanRecommendations.ReadyForShadowAdapterDesignFreeze,
            report.Recommendation);
        CollectionAssert.Contains(report.AdapterInputs.ToList(), "query");
        CollectionAssert.Contains(report.AdapterOutputs.ToList(), "shadow candidates only");
        Assert.AreEqual(HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1, report.VectorProviderSource);
        Assert.IsTrue(report.GateOrder.Count > 0);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.PackageOutputChanged);
        CollectionAssert.Contains(report.ForbiddenActions.ToList(), "Formal IVectorIndexStore binding");
        CollectionAssert.Contains(report.ForbiddenActions.ToList(), "Graph/vector candidate direct insertion into formal selected set");
    }

    [TestMethod]
    public void ShadowFormalRetrievalAdapterPlan_MissingV50AuditBlocks()
    {
        var report = new ShadowFormalRetrievalAdapterPlanRunner().BuildPlan(
            null,
            CleanFormalPreviewFreeze(),
            CleanPromotionDecision(),
            CleanGuardedRuntimeExperiment(),
            CleanShadowPackageComparison(),
            CleanRuntimeChangeGate());

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(
            ShadowFormalRetrievalAdapterPlanRecommendations.BlockedByMissingProjectStateAudit,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "V50ProjectStateAuditNotPassed");
    }

    [TestMethod]
    public void ShadowFormalRetrievalAdapterPlan_RuntimeSwitchAttemptBlocks()
    {
        var report = new ShadowFormalRetrievalAdapterPlanRunner().BuildPlan(
            CleanProjectStateAudit(runtimeSwitchAllowed: true),
            CleanFormalPreviewFreeze(),
            CleanPromotionDecision(),
            CleanGuardedRuntimeExperiment(),
            CleanShadowPackageComparison(),
            CleanRuntimeChangeGate());

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(
            ShadowFormalRetrievalAdapterPlanRecommendations.BlockedByRuntimeSwitchAttempt,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeSwitchAllowedUnexpected");
    }

    [TestMethod]
    public void ShadowFormalRetrievalAdapterPlan_FormalRetrievalAttemptBlocks()
    {
        var report = new ShadowFormalRetrievalAdapterPlanRunner().BuildPlan(
            CleanProjectStateAudit(formalRetrievalAllowed: true),
            CleanFormalPreviewFreeze(),
            CleanPromotionDecision(),
            CleanGuardedRuntimeExperiment(),
            CleanShadowPackageComparison(),
            CleanRuntimeChangeGate());

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(
            ShadowFormalRetrievalAdapterPlanRecommendations.BlockedByFormalRetrievalAttempt,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "FormalRetrievalAllowedUnexpected");
    }

    [TestMethod]
    public void ShadowFormalRetrievalAdapterPlan_PackageMutationBlocks()
    {
        var report = new ShadowFormalRetrievalAdapterPlanRunner().BuildPlan(
            CleanProjectStateAudit(),
            CleanFormalPreviewFreeze(packageOutputChanged: true),
            CleanPromotionDecision(),
            CleanGuardedRuntimeExperiment(),
            CleanShadowPackageComparison(),
            CleanRuntimeChangeGate());

        Assert.IsFalse(report.PlanPassed);
        Assert.AreEqual(
            ShadowFormalRetrievalAdapterPlanRecommendations.BlockedByPackageMutation,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "PackageOrRuntimeMutationDetected");
    }

    [TestMethod]
    public void ShadowFormalRetrievalAdapterPlan_HasNoKnownFixtureTerms()
    {
        var sourcePath = ResolveRepoFile(
            "src",
            "ContextCore.Core",
            "Services",
            "Vector",
            "ShadowFormalRetrievalAdapterPlanRunner.cs");
        var source = File.ReadAllText(Path.GetFullPath(sourcePath));

        foreach (var forbidden in new[] { "林风", "苍穹大陆", "九转金丹", "龙魂草", "拍卖行" })
        {
            Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal), $"Runner must not contain fixed eval content: {forbidden}");
        }
    }

    private static ShadowFormalRetrievalAdapterPlanReport BuildCleanReport()
        => new ShadowFormalRetrievalAdapterPlanRunner().BuildPlan(
            CleanProjectStateAudit(),
            CleanFormalPreviewFreeze(),
            CleanPromotionDecision(),
            CleanGuardedRuntimeExperiment(),
            CleanShadowPackageComparison(),
            CleanRuntimeChangeGate());

    private static ProjectStateAuditReport CleanProjectStateAudit(
        bool formalRetrievalAllowed = false,
        bool runtimeSwitchAllowed = false)
        => new()
        {
            Recommendation = ProjectStateAuditRecommendations.ReadyForMainlineGapRepairPlanning,
            FormalRetrievalAllowed = formalRetrievalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
            ReadyForRuntimeSwitch = false,
            PackingPolicyChanged = false,
            PackageOutputChanged = false
        };

    private static VectorFormalPreviewFreezeReport CleanFormalPreviewFreeze(bool packageOutputChanged = false)
        => new()
        {
            FreezePassed = true,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            PackageOutputChanged = packageOutputChanged,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            FormalPackageWritten = false
        };

    private static ScopedRuntimeExperimentObservationFreezeReport CleanPromotionDecision()
        => new()
        {
            FreezePassed = true,
            PromotionDecision = ScopedRuntimeExperimentObservationFreezeDecisions.ReadyForFormalRetrievalIntegrationPlan,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalPackageWritten = false
        };

    private static GuardedScopedRuntimeExperimentReport CleanGuardedRuntimeExperiment()
        => new()
        {
            ExperimentPassed = true,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalPackageWritten = false
        };

    private static VectorShadowPackageComparisonReport CleanShadowPackageComparison()
        => new()
        {
            GatePassed = true,
            FormalRetrievalAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false
        };

    private static LearningRuntimeChangeReadinessGateReport CleanRuntimeChangeGate()
        => new()
        {
            Passed = true
        };

    private static string ResolveRepoFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);return TestRepoFileResolver.Resolve(segments);}
}
