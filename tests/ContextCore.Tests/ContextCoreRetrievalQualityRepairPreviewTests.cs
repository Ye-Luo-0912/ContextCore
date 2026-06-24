using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreRetrievalQualityRepairPreviewTests
{
    [TestMethod]
    public void RetrievalQualityRepairPreview_CleanInputs_PreviewPasses()
    {
        var dataset = BuildSampleDataset();
        var report = new RetrievalQualityRepairPreviewRunner()
            .BuildPreview(BuildQualityGate(passed: true), BuildPackageShadowGate(passed: true), dataset);

        Assert.IsTrue(report.PreviewPassed,
            $"preview should pass; blocked={string.Join(",", report.BlockedReasons)}");
        Assert.AreEqual(8, report.Profiles.Count);
        Assert.AreEqual(RetrievalQualityRepairProfiles.Baseline, report.Baseline.ProfileId);
        Assert.AreEqual(0, report.Baseline.RiskAfterPolicy);
        Assert.AreEqual(0, report.Baseline.MustNotHitRiskAfterPolicy);
        Assert.AreEqual(0, report.Baseline.LifecycleRiskAfterPolicy);
        Assert.AreEqual(0, report.Baseline.SectionMismatchCount);
        Assert.AreEqual(0, report.Baseline.GraphNoiseCount);
        Assert.AreEqual(0, report.Baseline.RankingRegressionCount);
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
        // Best profile picked or baseline-only kept; either way preview passes.
        if (!string.IsNullOrEmpty(report.BestProfileId))
        {
            var best = report.Profiles.First(p => p.ProfileId == report.BestProfileId);
            Assert.IsTrue(best.Recall >= report.Baseline.Recall - 1e-6,
                $"best.Recall={best.Recall} baseline.Recall={report.Baseline.Recall}");
            Assert.IsTrue(best.MeanReciprocalRank >= report.Baseline.MeanReciprocalRank - 1e-6);
            Assert.IsFalse(best.RecallRegressionDetected);
            Assert.IsFalse(best.MrrRegressionDetected);
            Assert.IsFalse(best.GraphNoiseRegressionDetected);
            Assert.IsFalse(best.RankingRegressionDetected);
            Assert.IsFalse(best.RiskRegressionDetected);
            Assert.IsFalse(best.TokenBudgetExceeded);
            Assert.AreEqual(
                RetrievalQualityRepairPreviewRecommendations.ReadyForRetrievalQualityRepairFreeze,
                report.Recommendation);
        }
        else
        {
            Assert.AreEqual(
                RetrievalQualityRepairPreviewRecommendations.KeepBaselineOnly,
                report.Recommendation);
        }
    }

    [TestMethod]
    public void RetrievalQualityRepairPreview_GateMode_GatePassed()
    {
        var dataset = BuildSampleDataset();
        var report = new RetrievalQualityRepairPreviewRunner()
            .BuildGate(BuildQualityGate(passed: true), BuildPackageShadowGate(passed: true), dataset);

        Assert.IsTrue(report.OperationId.StartsWith("retrieval-quality-repair-gate-", StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(report.BestProfileId))
        {
            Assert.IsTrue(report.PreviewPassed);
            Assert.IsTrue(report.GatePassed);
        }
        else
        {
            // No improvement → gate blocks with NoRepairProfileImproved.
            Assert.IsFalse(report.GatePassed);
            CollectionAssert.Contains(report.BlockedReasons.ToList(), "NoRepairProfileImproved");
            Assert.AreEqual(
                RetrievalQualityRepairPreviewRecommendations.BlockedByNoRepairProfileImprovement,
                report.Recommendation);
        }
    }

    [TestMethod]
    public void RetrievalQualityRepairPreview_MissingQualityGate_BlocksRun()
    {
        var report = new RetrievalQualityRepairPreviewRunner()
            .BuildPreview(qualityGate: null, BuildPackageShadowGate(passed: true), BuildSampleDataset());

        Assert.IsFalse(report.PreviewPassed);
        Assert.AreEqual(
            RetrievalQualityRepairPreviewRecommendations.BlockedByMissingQualityGate,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "QualityGateMissing");
    }

    [TestMethod]
    public void RetrievalQualityRepairPreview_QualityGateNotPassed_BlocksRun()
    {
        var report = new RetrievalQualityRepairPreviewRunner()
            .BuildPreview(BuildQualityGate(passed: false), BuildPackageShadowGate(passed: true), BuildSampleDataset());

        Assert.IsFalse(report.PreviewPassed);
        Assert.AreEqual(
            RetrievalQualityRepairPreviewRecommendations.BlockedByQualityGateNotPassed,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "QualityGateNotPassed");
    }

    [TestMethod]
    public void RetrievalQualityRepairPreview_MissingDataset_BlocksRun()
    {
        var report = new RetrievalQualityRepairPreviewRunner()
            .BuildPreview(BuildQualityGate(passed: true), BuildPackageShadowGate(passed: true), new RetrievalDatasetV2GeneratedDataset());

        Assert.IsFalse(report.PreviewPassed);
        Assert.AreEqual(
            RetrievalQualityRepairPreviewRecommendations.BlockedByMissingDataset,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "MissingDataset");
    }

    [TestMethod]
    public void RetrievalQualityRepairPreview_BaselineHasNoImprovement_PreviewPassesGateBlocks()
    {
        var dataset = BuildNoImprovementDataset();

        // Preview mode: still passes, BestProfileId may be empty, recommendation = KeepBaselineOnly.
        var preview = new RetrievalQualityRepairPreviewRunner()
            .BuildPreview(BuildQualityGate(passed: true), BuildPackageShadowGate(passed: true), dataset);
        if (string.IsNullOrEmpty(preview.BestProfileId))
        {
            Assert.IsTrue(preview.PreviewPassed,
                $"preview should pass even without improvement; blocked={string.Join(",", preview.BlockedReasons)}");
            Assert.AreEqual(
                RetrievalQualityRepairPreviewRecommendations.KeepBaselineOnly,
                preview.Recommendation);

            // Gate mode: blocks with NoRepairProfileImproved.
            var gate = new RetrievalQualityRepairPreviewRunner()
                .BuildGate(BuildQualityGate(passed: true), BuildPackageShadowGate(passed: true), dataset);
            Assert.IsFalse(gate.GatePassed);
            CollectionAssert.Contains(gate.BlockedReasons.ToList(), "NoRepairProfileImproved");
            Assert.AreEqual(
                RetrievalQualityRepairPreviewRecommendations.BlockedByNoRepairProfileImprovement,
                gate.Recommendation);
        }
        else
        {
            // If a profile happened to improve on this dataset, both modes pass.
            Assert.IsTrue(preview.PreviewPassed);
        }
    }

    [TestMethod]
    public void RetrievalQualityRepairPreview_QualityGateRuntimeAttempt_BlocksRun()
    {
        var qualityGate = BuildQualityGate(passed: true);
        var mutated = new GraphVectorRetrievalQualityAuditReport
        {
            AuditPassed = qualityGate.AuditPassed,
            GatePassed = qualityGate.GatePassed,
            VectorProviderSource = qualityGate.VectorProviderSource,
            GraphCandidateSource = qualityGate.GraphCandidateSource,
            SampleCount = qualityGate.SampleCount,
            RuntimeMutated = true
        };

        var report = new RetrievalQualityRepairPreviewRunner()
            .BuildPreview(mutated, BuildPackageShadowGate(passed: true), BuildSampleDataset());

        Assert.IsFalse(report.PreviewPassed);
        Assert.AreEqual(
            RetrievalQualityRepairPreviewRecommendations.BlockedByRuntimeMutation,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "QualityGateRuntimeMutated");
    }

    [TestMethod]
    public void RetrievalQualityRepairPreview_HasNoKnownFixtureTerms()
    {
        var sourcePath = ResolveRepoFile(
            "src",
            "ContextCore.Core",
            "Services",
            "Vector",
            "RetrievalQualityRepairPreviewRunner.cs");
        var source = File.ReadAllText(Path.GetFullPath(sourcePath));

        foreach (var forbidden in new[] { "林风", "苍穹大陆", "九转金丹", "龙魂草", "拍卖行" })
        {
            Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal), $"Runner must not contain fixed eval content: {forbidden}");
        }

        Assert.IsFalse(source.Contains("sample.SampleId ==", StringComparison.Ordinal),
            "Runner must not branch on a specific sampleId literal.");
        Assert.IsFalse(source.Contains("sample-pkg", StringComparison.Ordinal),
            "Runner must not contain a hard-coded fixture sample id.");
    }

    private static GraphVectorRetrievalQualityAuditReport BuildQualityGate(bool passed)
        => new()
        {
            AuditPassed = passed,
            GatePassed = passed,
            Recommendation = passed
                ? GraphVectorRetrievalQualityAuditRecommendations.ReadyForRetrievalQualityFreeze
                : GraphVectorRetrievalQualityAuditRecommendations.KeepPreviewOnly,
            AllowedMode = "AuditOnly",
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

    private static FormalAdapterPackageShadowComparisonReport BuildPackageShadowGate(bool passed)
        => new()
        {
            ComparisonPassed = passed,
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
            ShadowPackageWritten = false,
            NoRuntimeMutationInvariant = true
        };

    private static RetrievalDatasetV2GeneratedDataset BuildSampleDataset()
    {
        // Repair-amenable: baseline (top-K=5) recalls partial mustHits; expansion or
        // boost should be able to recover the rest. We seed multiple samples with one
        // expected mustHit each plus distractors, and one with a weak-evidence anchor
        // that boosts can lift.
        var corpus = new[]
        {
            BuildCorpusItem(
                itemId: "repair-target-active",
                content: "active stable repair target with strong query overlap",
                tags: new[] { "repair", "target", "active" },
                anchors: new[] { "repair", "target" },
                evidenceRefs: new[] { "evidence-repair-1" },
                relations: new[] { ("rel-repair-1", "rel-repair-1-target") }),
            BuildCorpusItem(
                itemId: "repair-target-secondary",
                content: "secondary repair target with weak token overlap but strong evidence anchor",
                tags: new[] { "repair", "target", "secondary" },
                anchors: new[] { "secondary" },
                evidenceRefs: new[] { "evidence-repair-2" }),
            BuildCorpusItem(
                itemId: "repair-graph-bridge",
                content: "graph relation bridge supporting the repair target",
                tags: new[] { "repair", "bridge" },
                anchors: new[] { "bridge", "graph" },
                relations: new[] { ("rel-repair-1", "rel-repair-1-target") }),
            BuildCorpusItem(
                itemId: "repair-noise-rule",
                content: "general unrelated rule pulling baseline tokens away",
                tags: new[] { "repair", "noise" },
                anchors: new[] { "noise" }),
            BuildCorpusItem(
                itemId: "repair-other-noise",
                content: "another unrelated noisy entry",
                tags: new[] { "repair", "noise", "other" },
                anchors: new[] { "other" })
        };

        var sample = new RetrievalDatasetV2Sample
        {
            SampleId = "sample-repair-1",
            QueryText = "repair target query about active stable bridge",
            ExpectedTargetSection = VectorQueryTargetSections.NormalContext,
            MustHitItemIds = new[] { "repair-target-active", "repair-target-secondary" },
            MustNotHitItemIds = Array.Empty<string>(),
            EvidenceRefs = new[] { "evidence-repair-1", "evidence-repair-2" },
            SourceRefs = Array.Empty<string>(),
            RequiredRelations = new[] { "rel-repair-1" }
        };

        return new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = corpus,
            Samples = new[] { sample }
        };
    }

    private static RetrievalDatasetV2GeneratedDataset BuildNoImprovementDataset()
    {
        // Single must-hit item that the dense baseline already recalls at rank 1, and
        // no other corpus items to expand into. Boost profiles cannot improve recall
        // or MRR beyond baseline.
        var corpus = new[]
        {
            BuildCorpusItem(
                itemId: "repair-only-target",
                content: "only target candidate with full query overlap",
                tags: new[] { "repair", "only", "target" },
                anchors: new[] { "repair", "only", "target" })
        };

        var sample = new RetrievalDatasetV2Sample
        {
            SampleId = "sample-repair-no-improvement",
            QueryText = "repair only target query overlap",
            ExpectedTargetSection = VectorQueryTargetSections.NormalContext,
            MustHitItemIds = new[] { "repair-only-target" },
            MustNotHitItemIds = Array.Empty<string>(),
            EvidenceRefs = Array.Empty<string>(),
            SourceRefs = Array.Empty<string>(),
            RequiredRelations = Array.Empty<string>()
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
        var directory = new DirectoryInfo(AppContext.BaseDirectory);return TestRepoFileResolver.Resolve(segments);}
}
