using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreGraphVectorRetrievalQualityAuditTests
{
    [TestMethod]
    public void GraphVectorRetrievalQualityAudit_CleanInputs_AuditPasses()
    {
        var dataset = BuildSampleDataset();
        var report = new GraphVectorRetrievalQualityAuditRunner()
            .BuildAudit(BuildPackageShadowGate(passed: true), BuildAdapterGate(passed: true), dataset);

        Assert.IsTrue(report.AuditPassed,
            $"audit should pass; blocked={string.Join(",", report.BlockedReasons)}");
        Assert.AreEqual(
            GraphVectorRetrievalQualityAuditRecommendations.ReadyForRetrievalQualityFreeze,
            report.Recommendation);
        Assert.AreEqual(0, report.RiskAfterPolicy);
        Assert.AreEqual(0, report.MustNotHitRiskAfterPolicy);
        Assert.AreEqual(0, report.LifecycleRiskAfterPolicy);
        Assert.AreEqual(0, report.SectionMismatchCount);
        Assert.AreEqual(0, report.GraphNoiseCount);
        Assert.AreEqual(0, report.VectorNoiseCount);
        Assert.AreEqual(0, report.RankingRegressionCount);
        Assert.AreEqual(0, report.MustHitBelowTopKCount);
        Assert.AreEqual(0, report.FormalOutputChanged);
        Assert.IsFalse(report.FormalSelectedSetChanged);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.RuntimeMutated);
        Assert.IsFalse(report.VectorStoreBindingChanged);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsTrue(report.NoRuntimeMutationInvariant);
        Assert.AreEqual(HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1, report.VectorProviderSource);
        Assert.AreEqual("read-only relation evidence / expansion preview", report.GraphCandidateSource);
        Assert.IsTrue(report.SampleCount > 0);
        Assert.IsTrue(report.Samples.Count > 0);
        Assert.IsTrue(report.Recall >= 0.99,
            $"recall should be near-perfect on clean dataset; actual={report.Recall:F4}");
        Assert.IsTrue(report.MeanReciprocalRank > 0);
        Assert.AreEqual(0, report.FailureClusters.Count);
    }

    [TestMethod]
    public void GraphVectorRetrievalQualityAudit_GateMode_GatePassed()
    {
        var dataset = BuildSampleDataset();
        var report = new GraphVectorRetrievalQualityAuditRunner()
            .BuildGate(BuildPackageShadowGate(passed: true), BuildAdapterGate(passed: true), dataset);

        Assert.IsTrue(report.AuditPassed);
        Assert.IsTrue(report.GatePassed);
        Assert.IsTrue(report.OperationId.StartsWith("graph-vector-retrieval-quality-audit-gate-", StringComparison.Ordinal));
    }

    [TestMethod]
    public void GraphVectorRetrievalQualityAudit_MissingPackageShadowGate_BlocksRun()
    {
        var report = new GraphVectorRetrievalQualityAuditRunner()
            .BuildAudit(packageShadowGate: null, BuildAdapterGate(passed: true), BuildSampleDataset());

        Assert.IsFalse(report.AuditPassed);
        Assert.AreEqual(
            GraphVectorRetrievalQualityAuditRecommendations.BlockedByMissingPackageShadowGate,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "PackageShadowGateMissing");
    }

    [TestMethod]
    public void GraphVectorRetrievalQualityAudit_PackageShadowGateNotPassed_BlocksRun()
    {
        var report = new GraphVectorRetrievalQualityAuditRunner()
            .BuildAudit(BuildPackageShadowGate(passed: false), BuildAdapterGate(passed: true), BuildSampleDataset());

        Assert.IsFalse(report.AuditPassed);
        Assert.AreEqual(
            GraphVectorRetrievalQualityAuditRecommendations.BlockedByPackageShadowGateNotPassed,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "PackageShadowGateNotPassed");
    }

    [TestMethod]
    public void GraphVectorRetrievalQualityAudit_MissingDataset_BlocksRun()
    {
        var report = new GraphVectorRetrievalQualityAuditRunner()
            .BuildAudit(BuildPackageShadowGate(passed: true), BuildAdapterGate(passed: true), new RetrievalDatasetV2GeneratedDataset());

        Assert.IsFalse(report.AuditPassed);
        Assert.AreEqual(
            GraphVectorRetrievalQualityAuditRecommendations.BlockedByMissingDataset,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "MissingDataset");
    }

    [TestMethod]
    public void GraphVectorRetrievalQualityAudit_GraphNoiseExceedsThreshold_BlocksRun()
    {
        var dataset = BuildGraphNoiseDataset();
        var report = new GraphVectorRetrievalQualityAuditRunner()
            .BuildAudit(BuildPackageShadowGate(passed: true), BuildAdapterGate(passed: true), dataset);

        Assert.IsFalse(report.AuditPassed);
        Assert.IsTrue(report.GraphNoiseCount > 0,
            $"expected non-zero graph noise; counts={report.GraphNoiseCount}");
        Assert.AreEqual(
            GraphVectorRetrievalQualityAuditRecommendations.BlockedByGraphNoiseExceedsThreshold,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "GraphNoiseExceedsThreshold");
        Assert.IsTrue(report.FailureClusters.Any(c => c.ClusterId == GraphVectorRetrievalQualityAuditFailureClusters.GraphNoise));
    }

    [TestMethod]
    public void GraphVectorRetrievalQualityAudit_PackageShadowGateRuntimeAttempt_BlocksRun()
    {
        var packageGate = BuildPackageShadowGate(passed: true);
        var mutated = new FormalAdapterPackageShadowComparisonReport
        {
            ComparisonPassed = packageGate.ComparisonPassed,
            GatePassed = packageGate.GatePassed,
            VectorProviderSource = packageGate.VectorProviderSource,
            GraphCandidateSource = packageGate.GraphCandidateSource,
            SampleCount = packageGate.SampleCount,
            RuntimeMutated = true
        };
        var report = new GraphVectorRetrievalQualityAuditRunner()
            .BuildAudit(mutated, BuildAdapterGate(passed: true), BuildSampleDataset());

        Assert.IsFalse(report.AuditPassed);
        Assert.AreEqual(
            GraphVectorRetrievalQualityAuditRecommendations.BlockedByRuntimeMutation,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "PackageShadowGateRuntimeMutated");
    }

    [TestMethod]
    public void GraphVectorRetrievalQualityAudit_HasNoKnownFixtureTerms()
    {
        var sourcePath = ResolveRepoFile(
            "src",
            "ContextCore.Core",
            "Services",
            "Vector",
            "GraphVectorRetrievalQualityAuditRunner.cs");
        var source = File.ReadAllText(Path.GetFullPath(sourcePath));

        foreach (var forbidden in new[] { "林风", "苍穹大陆", "九转金丹", "龙魂草", "拍卖行" })
        {
            Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal), $"Runner must not contain fixed eval content: {forbidden}");
        }

        Assert.IsFalse(source.Contains("sample.SampleId ==", StringComparison.Ordinal),
            "Runner must not branch on a specific sampleId literal.");
        Assert.IsFalse(source.Contains("sample-shadow", StringComparison.Ordinal),
            "Runner must not contain a hard-coded fixture sample id.");
    }

    private static FormalAdapterPackageShadowComparisonReport BuildPackageShadowGate(bool passed)
        => new()
        {
            ComparisonPassed = passed,
            GatePassed = passed,
            Recommendation = passed
                ? FormalAdapterPackageShadowComparisonRecommendations.ReadyForFormalAdapterPackageShadowFreeze
                : FormalAdapterPackageShadowComparisonRecommendations.KeepPreviewOnly,
            AllowedMode = "ShadowOnly",
            VectorProviderSource = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            GraphCandidateSource = "read-only relation evidence / expansion preview",
            SampleCount = 1,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalPackageWritten = false,
            ShadowPackageWritten = false,
            NoRuntimeMutationInvariant = true
        };

    private static ShadowFormalRetrievalAdapterReport BuildAdapterGate(bool passed)
        => new()
        {
            AdapterPassed = passed,
            GatePassed = passed,
            VectorProviderSource = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            GraphCandidateSource = "read-only relation evidence / expansion preview",
            SampleCount = 1,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalPackageWritten = false,
            NoRuntimeMutationInvariant = true
        };

    private static RetrievalDatasetV2GeneratedDataset BuildSampleDataset()
    {
        var corpus = new[]
        {
            BuildCorpusItem(
                itemId: "audit-target-active",
                content: "active stable target candidate matches the audit query goals",
                tags: new[] { "audit", "target", "active" },
                anchors: new[] { "audit", "target" },
                evidenceRefs: new[] { "evidence-audit-1" },
                relations: new[] { ("rel-audit-1", "rel-audit-1-target") }),
            BuildCorpusItem(
                itemId: "audit-graph-bridge",
                content: "bridge node referenced by relation evidence supporting the audit query",
                tags: new[] { "audit", "bridge" },
                anchors: new[] { "bridge", "graph" },
                sourceRefs: new[] { "src-audit-1" },
                relations: new[] { ("rel-audit-1", "rel-audit-1-target") }),
            BuildCorpusItem(
                itemId: "audit-noise-rule",
                content: "general unrelated rule that may show up in baseline",
                tags: new[] { "audit", "noise" },
                anchors: new[] { "noise" })
        };

        var sample = new RetrievalDatasetV2Sample
        {
            SampleId = "sample-audit-1",
            QueryText = "audit target query about active stable bridge",
            ExpectedTargetSection = VectorQueryTargetSections.NormalContext,
            MustHitItemIds = new[] { "audit-target-active" },
            MustNotHitItemIds = Array.Empty<string>(),
            EvidenceRefs = new[] { "evidence-audit-1" },
            SourceRefs = new[] { "src-audit-1" },
            RequiredRelations = new[] { "rel-audit-1" },
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["workspaceId"] = "ws-audit",
                ["collectionId"] = "col-audit"
            }
        };

        return new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = corpus,
            Samples = new[] { sample }
        };
    }

    private static RetrievalDatasetV2GeneratedDataset BuildGraphNoiseDataset()
    {
        // Graph noise = graph candidate admitted via weak evidence/source overlap alone,
        // not via MustHit and not via RequiredRelations. We give the sample evidence refs
        // the corpus item shares, but never list the corpus item in MustHitItemIds and
        // never declare RequiredRelations the corpus item could match.
        var corpus = new[]
        {
            BuildCorpusItem(
                itemId: "audit-target-active",
                content: "active stable target candidate matches the audit query goals",
                tags: new[] { "audit", "target", "active" },
                anchors: new[] { "audit", "target" }),
            BuildCorpusItem(
                itemId: "audit-noise-stranger",
                content: "graph candidate sharing evidence ref but not anchored by mustHit or required relations",
                tags: new[] { "audit", "stranger" },
                anchors: new[] { "stranger" },
                evidenceRefs: new[] { "evidence-audit-noise" })
        };

        var sample = new RetrievalDatasetV2Sample
        {
            SampleId = "sample-audit-noise",
            QueryText = "audit query that surfaces a stranger graph candidate",
            ExpectedTargetSection = VectorQueryTargetSections.NormalContext,
            MustHitItemIds = new[] { "audit-target-active" },
            MustNotHitItemIds = Array.Empty<string>(),
            EvidenceRefs = new[] { "evidence-audit-noise" }
        };

        return new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = corpus,
            Samples = new[] { sample }
        };
    }

    private static RetrievalDatasetV2CorpusItem BuildCorpusItem(
        string itemId,
        string? targetSection = null,
        string? lifecycle = null,
        string? replacementState = null,
        string? content = null,
        IReadOnlyList<string>? tags = null,
        IReadOnlyList<string>? anchors = null,
        IReadOnlyList<string>? evidenceRefs = null,
        IReadOnlyList<string>? sourceRefs = null,
        IReadOnlyList<(string RelationId, string TargetItemId)>? relations = null)
    {
        return new RetrievalDatasetV2CorpusItem
        {
            ItemId = itemId,
            ItemKind = "note",
            SourceKind = "note",
            Layer = "Stable",
            Lifecycle = lifecycle ?? "Active",
            ReviewStatus = "Reviewed",
            ReplacementState = replacementState ?? "Current",
            TargetSection = targetSection ?? VectorQueryTargetSections.NormalContext,
            Content = content ?? string.Empty,
            Tags = tags ?? Array.Empty<string>(),
            Anchors = anchors ?? Array.Empty<string>(),
            EvidenceRefs = evidenceRefs ?? Array.Empty<string>(),
            SourceRefs = sourceRefs ?? Array.Empty<string>(),
            Relations = (relations ?? Array.Empty<(string, string)>())
                .Select(rel => new RetrievalDatasetV2Relation
                {
                    RelationId = rel.RelationId,
                    SourceItemId = itemId,
                    TargetItemId = rel.TargetItemId,
                    RelationType = "supports"
                })
                .ToArray()
        };
    }

    private static string ResolveRepoFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ContextCore.sln")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory);
        return Path.Combine(new[] { directory!.FullName }.Concat(segments).ToArray());
    }
}
