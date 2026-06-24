using System.Text.Json;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreProjectStateAuditTests
{
    [TestMethod]
    public void ProjectStateAudit_CleanReportsKeepRuntimeBoundariesClosed()
    {
        var root = CreateTempRoot();
        try
        {
            SeedCleanReports(root);

            var report = new ProjectStateAuditRunner().BuildProjectStateAudit(root);

            Assert.AreEqual("FoundationFrozen_FormalRetrievalPlanOnly", report.CurrentOverallStatus);
            Assert.IsTrue(report.ReadyCapabilities.Contains("Foundation"));
            Assert.IsTrue(report.PreviewOnlyCapabilities.Contains("FormalRetrievalIntegrationPlan"));
            Assert.IsTrue(report.BlockedCapabilities.Contains("FormalRetrievalRuntimeSwitch"));
            Assert.IsFalse(report.FormalRetrievalAllowed);
            Assert.IsFalse(report.RuntimeSwitchAllowed);
            Assert.IsFalse(report.ReadyForRuntimeSwitch);
            Assert.IsFalse(report.PackingPolicyChanged);
            Assert.IsFalse(report.PackageOutputChanged);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public void ProjectStateAudit_MissingReportMarksCapabilityBlocked()
    {
        var root = CreateTempRoot();
        try
        {
            SeedCleanReports(root);
            File.Delete(Path.Combine(root, "vector", "v5", "formal-retrieval-integration-plan-gate.json"));

            var report = new ProjectStateAuditRunner().BuildProjectStateAudit(root);

            Assert.IsTrue(report.BlockedCapabilities.Contains("FormalRetrievalIntegrationPlan"));
            Assert.AreEqual(ProjectStateAuditRecommendations.NeedsMissingReportRegeneration, report.Recommendation);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public void MainlineGapMap_CoversRequestedMainlineAreas()
    {
        var root = CreateTempRoot();
        try
        {
            SeedCleanReports(root);

            var report = new ProjectStateAuditRunner().BuildMainlineGapMap(root);
            var areas = report.MainlineGaps.Select(static gap => gap.Area).ToHashSet(StringComparer.OrdinalIgnoreCase);

            CollectionAssert.Contains(areas.ToList(), "Graph");
            CollectionAssert.Contains(areas.ToList(), "Vector");
            CollectionAssert.Contains(areas.ToList(), "Input");
            CollectionAssert.Contains(areas.ToList(), "Output");
            CollectionAssert.Contains(areas.ToList(), "Learning");
            CollectionAssert.Contains(report.MustDoBeforeFormalRetrieval.ToList(), "Build ShadowFormalRetrievalAdapter as the next V5 phase.");
            Assert.IsFalse(report.FormalRetrievalAllowed);
            Assert.IsFalse(report.RuntimeSwitchAllowed);
            Assert.IsFalse(report.PackageOutputChanged);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public void ProjectStateAuditRunner_HasNoKnownFixtureTerms()
    {
        var sourcePath = ResolveRepoFile(
            "src",
            "ContextCore.Core",
            "Services",
            "Learning",
            "ProjectStateAuditRunner.cs");
        var source = File.ReadAllText(Path.GetFullPath(sourcePath));

        foreach (var forbidden in new[] { "林风", "苍穹大陆", "九转金丹", "龙魂草", "拍卖行" })
        {
            Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal), $"Runner must not contain fixed eval content: {forbidden}");
        }
    }

    private static void SeedCleanReports(string root)
    {
        Write(root, "foundation/foundation-release-candidate-gate.json", new { FreezePassed = true, Recommendation = "Frozen", FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false });
        Write(root, "service/service-foundation-freeze-gate.json", new { FreezePassed = true, Recommendation = "Frozen", FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false });
        Write(root, "foundation/foundation-freeze-report.json", new { FreezePassed = true, Recommendation = "Frozen", FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false });
        Write(root, "storage/postgres/postgres-relation-governance-readiness-gate.json", new { GatePassed = true, Recommendation = "Ready", FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false });
        Write(root, "storage/postgres/postgres-learning-feedback-freeze-gate.json", new { FreezePassed = true, Recommendation = "Ready", FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false });
        Write(root, "storage/postgres/postgres-job-queue-freeze-gate.json", new { FreezePassed = true, Recommendation = "Ready", FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false });
        Write(root, "storage/postgres/postgres-vector-freeze-gate.json", new { FreezePassed = true, Recommendation = "PreviewOnly", FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false });
        Write(root, "vector/v4/vector-formal-preview-freeze-gate.json", new { FreezePassed = true, Recommendation = "PreviewOnly", FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false });
        Write(root, "vector/v4/runtime-experiment/promotion-decision.json", new { FreezePassed = true, PromotionDecision = "ReadyForFormalRetrievalIntegrationPlan", FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false });
        Write(root, "vector/v5/formal-retrieval-integration-plan-gate.json", new { PlanPassed = true, Recommendation = "ReadyForShadowFormalRetrievalAdapter", FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false, ReadyForRuntimeSwitch = false, UseForRuntime = false });
        Write(root, "learning/router/router-guarded-optin-readiness-gate.json", new { GatePassed = true, Recommendation = "PreviewOnly", FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false });
        Write(root, "eval/vector-retrieval-shadow-readiness-gate.json", new { GatePassed = true, Recommendation = "PreviewOnly", FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false });
        Write(root, "learning/readiness/learning-runtime-change-readiness-gate.json", new { Passed = true, Recommendation = "Ready", FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false });
        Write(root, "vector/dataset-v2/generated/materialization-gate.json", new { GatePassed = true, Recommendation = "Ready", FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false });
        Write(root, "vector/v4/vector-shadow-package-comparison-gate.json", new { GatePassed = true, Recommendation = "PreviewOnly", FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false, PackageOutputChanged = false, PackingPolicyChanged = false });
    }

    private static void Write(string root, string relativePath, object value)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value));
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "contextcore-project-state-audit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string ResolveRepoFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);return TestRepoFileResolver.Resolve(segments);}
}
