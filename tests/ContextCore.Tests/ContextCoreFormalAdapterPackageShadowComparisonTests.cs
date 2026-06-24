using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreFormalAdapterPackageShadowComparisonTests
{
    [TestMethod]
    public void FormalAdapterPackageShadowComparison_CleanInputs_ComparisonPasses()
    {
        var dataset = BuildSampleDataset();
        var report = new FormalAdapterPackageShadowComparisonRunner()
            .BuildComparison(BuildAdapterGate(passed: true), dataset);

        Assert.IsTrue(report.ComparisonPassed,
            $"comparison should pass; blocked={string.Join(",", report.BlockedReasons)}");
        Assert.AreEqual(
            FormalAdapterPackageShadowComparisonRecommendations.ReadyForFormalAdapterPackageShadowFreeze,
            report.Recommendation);
        Assert.AreEqual(0, report.RiskAfterPolicy);
        Assert.AreEqual(0, report.MustNotHitRiskAfterPolicy);
        Assert.AreEqual(0, report.LifecycleRiskAfterPolicy);
        Assert.AreEqual(0, report.TargetSectionViolationCount);
        Assert.AreEqual(0, report.FormalOutputChanged);
        Assert.IsFalse(report.FormalSelectedSetChanged);
        Assert.IsFalse(report.FormalPackageWritten);
        Assert.IsFalse(report.ShadowPackageWritten);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.RuntimeMutated);
        Assert.IsFalse(report.VectorStoreBindingChanged);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
        Assert.IsFalse(report.UseForRuntime);
        Assert.IsTrue(report.NoRuntimeMutationInvariant);
        Assert.AreEqual(HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1, report.VectorProviderSource);
        Assert.AreEqual("read-only relation evidence / expansion preview", report.GraphCandidateSource);
        Assert.IsTrue(report.SampleCount > 0);
        Assert.IsTrue(report.Samples.Count > 0);
        Assert.IsTrue(report.TokenDeltaAbsoluteTotal <= report.TokenDeltaBudgetTotal);
        Assert.IsTrue(report.TokenDeltaMax <= report.TokenDeltaBudgetPerSample);
    }

    [TestMethod]
    public void FormalAdapterPackageShadowComparison_GateMode_GatePassed()
    {
        var dataset = BuildSampleDataset();
        var report = new FormalAdapterPackageShadowComparisonRunner()
            .BuildGate(BuildAdapterGate(passed: true), dataset);

        Assert.IsTrue(report.ComparisonPassed);
        Assert.IsTrue(report.GatePassed);
        Assert.IsTrue(report.OperationId.StartsWith("formal-adapter-package-shadow-comparison-gate-", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FormalAdapterPackageShadowComparison_MissingAdapterGate_BlocksRun()
    {
        var report = new FormalAdapterPackageShadowComparisonRunner()
            .BuildComparison(adapterGate: null, BuildSampleDataset());

        Assert.IsFalse(report.ComparisonPassed);
        Assert.AreEqual(
            FormalAdapterPackageShadowComparisonRecommendations.BlockedByMissingAdapterGate,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "AdapterGateMissing");
    }

    [TestMethod]
    public void FormalAdapterPackageShadowComparison_AdapterGateNotPassed_BlocksRun()
    {
        var report = new FormalAdapterPackageShadowComparisonRunner()
            .BuildComparison(BuildAdapterGate(passed: false), BuildSampleDataset());

        Assert.IsFalse(report.ComparisonPassed);
        Assert.AreEqual(
            FormalAdapterPackageShadowComparisonRecommendations.BlockedByAdapterGateNotPassed,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "AdapterGateNotPassed");
    }

    [TestMethod]
    public void FormalAdapterPackageShadowComparison_MissingDataset_BlocksRun()
    {
        var report = new FormalAdapterPackageShadowComparisonRunner()
            .BuildComparison(BuildAdapterGate(passed: true), new RetrievalDatasetV2GeneratedDataset());

        Assert.IsFalse(report.ComparisonPassed);
        Assert.AreEqual(
            FormalAdapterPackageShadowComparisonRecommendations.BlockedByMissingDataset,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "MissingDataset");
    }

    [TestMethod]
    public void FormalAdapterPackageShadowComparison_TokenBudgetExceeded_BlocksRun()
    {
        var dataset = BuildBudgetSensitiveDataset();
        var options = new FormalAdapterPackageShadowComparisonOptions
        {
            MaxTokenDeltaPerSample = 0,
            MaxTokenDeltaTotal = 0
        };
        var report = new FormalAdapterPackageShadowComparisonRunner()
            .BuildComparison(BuildAdapterGate(passed: true), dataset, options);

        Assert.IsFalse(report.ComparisonPassed);
        Assert.IsTrue(report.TokenDeltaAbsoluteTotal > 0,
            $"expected non-zero token delta in budget-sensitive dataset; actual delta={report.TokenDeltaTotal}");
        Assert.AreEqual(
            FormalAdapterPackageShadowComparisonRecommendations.BlockedByTokenBudgetExceeded,
            report.Recommendation);
        Assert.IsTrue(
            report.BlockedReasons.Any(r => r.Contains("TokenDelta", StringComparison.OrdinalIgnoreCase)),
            $"expected TokenDelta blocked reason; actual={string.Join(",", report.BlockedReasons)}");
    }

    [TestMethod]
    public void FormalAdapterPackageShadowComparison_AdapterGateRuntimeAttempt_BlocksRun()
    {
        var adapterGate = BuildAdapterGate(passed: true);
        var mutated = new ShadowFormalRetrievalAdapterReport
        {
            AdapterPassed = adapterGate.AdapterPassed,
            GatePassed = adapterGate.GatePassed,
            VectorProviderSource = adapterGate.VectorProviderSource,
            GraphCandidateSource = adapterGate.GraphCandidateSource,
            SampleCount = adapterGate.SampleCount,
            RuntimeSwitchAllowed = true
        };
        var report = new FormalAdapterPackageShadowComparisonRunner()
            .BuildComparison(mutated, BuildSampleDataset());

        Assert.IsFalse(report.ComparisonPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "AdapterGateRuntimeSwitchAllowed");
    }

    [TestMethod]
    public void FormalAdapterPackageShadowComparison_HasNoKnownFixtureTerms()
    {
        var sourcePath = ResolveRepoFile(
            "src",
            "ContextCore.Core",
            "Services",
            "Vector",
            "FormalAdapterPackageShadowComparisonRunner.cs");
        var source = File.ReadAllText(Path.GetFullPath(sourcePath));

        foreach (var forbidden in new[] { "林风", "苍穹大陆", "九转金丹", "龙魂草", "拍卖行" })
        {
            Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal), $"Runner must not contain fixed eval content: {forbidden}");
        }
    }

    private static ShadowFormalRetrievalAdapterReport BuildAdapterGate(bool passed)
        => new()
        {
            AdapterPassed = passed,
            GatePassed = passed,
            Recommendation = passed
                ? ShadowFormalRetrievalAdapterRecommendations.ReadyForShadowAdapterFreeze
                : ShadowFormalRetrievalAdapterRecommendations.KeepPreviewOnly,
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
            NoRuntimeMutationInvariant = true
        };

    private static RetrievalDatasetV2GeneratedDataset BuildSampleDataset()
    {
        var corpus = new[]
        {
            BuildCorpusItem(
                itemId: "pkg-target-active",
                content: "active stable target candidate matches the package query goals",
                tags: new[] { "package", "target", "active" },
                anchors: new[] { "package", "target" },
                evidenceRefs: new[] { "evidence-pkg-1" },
                relations: new[] { ("rel-pkg-1", "rel-pkg-1-target") }),
            BuildCorpusItem(
                itemId: "pkg-graph-bridge",
                content: "bridge node referenced by relation evidence supporting the package query",
                tags: new[] { "package", "bridge" },
                anchors: new[] { "bridge", "graph" },
                sourceRefs: new[] { "src-pkg-1" },
                relations: new[] { ("rel-pkg-1", "rel-pkg-1-target") }),
            BuildCorpusItem(
                itemId: "pkg-noise-rule",
                content: "general unrelated rule that may show up in baseline but not in shadow",
                tags: new[] { "package", "noise" },
                anchors: new[] { "noise" })
        };

        var sample = new RetrievalDatasetV2Sample
        {
            SampleId = "sample-pkg-1",
            QueryText = "package target query about active stable bridge",
            ExpectedTargetSection = VectorQueryTargetSections.NormalContext,
            MustHitItemIds = new[] { "pkg-target-active" },
            MustNotHitItemIds = Array.Empty<string>(),
            EvidenceRefs = new[] { "evidence-pkg-1" },
            SourceRefs = new[] { "src-pkg-1" },
            RequiredRelations = new[] { "rel-pkg-1" },
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["workspaceId"] = "ws-pkg",
                ["collectionId"] = "col-pkg"
            }
        };

        return new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = corpus,
            Samples = new[] { sample }
        };
    }

    private static RetrievalDatasetV2GeneratedDataset BuildBudgetSensitiveDataset()
    {
        var corpus = new[]
        {
            BuildCorpusItem(
                itemId: "pkg-target-active",
                content: "active stable target candidate matches the package query goals",
                tags: new[] { "package", "target", "active" },
                anchors: new[] { "package", "target" },
                evidenceRefs: new[] { "evidence-pkg-1" }),
            BuildCorpusItem(
                itemId: "pkg-excluded-noise",
                targetSection: VectorQueryTargetSections.Excluded,
                content: "package target heavy noise distractor with many tokens that dense-only happily ranks while post-scoring-risk-gated filters it out due to wrong target section",
                tags: new[] { "package", "noise", "excluded" },
                anchors: new[] { "package", "noise", "target" })
        };

        var sample = new RetrievalDatasetV2Sample
        {
            SampleId = "sample-pkg-budget",
            QueryText = "package target query about active stable noise",
            ExpectedTargetSection = VectorQueryTargetSections.NormalContext,
            MustHitItemIds = new[] { "pkg-target-active" },
            MustNotHitItemIds = Array.Empty<string>(),
            EvidenceRefs = new[] { "evidence-pkg-1" }
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
