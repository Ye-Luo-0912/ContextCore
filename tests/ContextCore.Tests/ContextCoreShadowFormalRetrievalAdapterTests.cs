using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreShadowFormalRetrievalAdapterTests
{
    [TestMethod]
    public void ShadowFormalRetrievalAdapter_CleanInputs_AdapterPasses()
    {
        var dataset = BuildSampleDataset();
        var report = new ShadowFormalRetrievalAdapter().BuildAdapter(BuildPlanGate(passed: true), dataset);

        Assert.IsTrue(report.AdapterPassed, $"adapter should pass; blocked={string.Join(",", report.BlockedReasons)}");
        Assert.AreEqual(
            ShadowFormalRetrievalAdapterRecommendations.ReadyForShadowAdapterFreeze,
            report.Recommendation);
        Assert.AreEqual(0, report.RiskAfterPolicy);
        Assert.AreEqual(0, report.MustNotHitRiskAfterPolicy);
        Assert.AreEqual(0, report.LifecycleRiskAfterPolicy);
        Assert.AreEqual(0, report.TargetSectionViolationCount);
        Assert.AreEqual(0, report.FormalOutputChanged);
        Assert.IsFalse(report.FormalSelectedSetChanged);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.RuntimeMutated);
        Assert.IsFalse(report.VectorStoreBindingChanged);
        Assert.IsFalse(report.FormalPackageWritten);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
        Assert.IsFalse(report.UseForRuntime);
        Assert.IsTrue(report.NoRuntimeMutationInvariant);
        Assert.AreEqual(HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1, report.VectorProviderSource);
        Assert.AreEqual("read-only relation evidence / expansion preview", report.GraphCandidateSource);
        Assert.IsTrue(report.SampleCount > 0);
        Assert.IsTrue(report.Samples.Count > 0);
        Assert.IsTrue(report.GateOrder.Contains("must-not risk gate"));
        CollectionAssert.Contains(report.AdapterInputs.ToList(), "query");
        CollectionAssert.Contains(report.AdapterInputs.ToList(), "package context");
        CollectionAssert.Contains(report.AdapterInputs.ToList(), "baseline candidates");
        CollectionAssert.Contains(report.AdapterOutputs.ToList(), "shadow vector candidates");
        CollectionAssert.Contains(report.AdapterOutputs.ToList(), "shadow graph candidates");
        CollectionAssert.Contains(report.AdapterOutputs.ToList(), "merged shadow candidates");
        CollectionAssert.Contains(report.AdapterOutputs.ToList(), "filtered candidates");
        CollectionAssert.Contains(report.AdapterOutputs.ToList(), "trace/explain");
    }

    [TestMethod]
    public void ShadowFormalRetrievalAdapter_GateMode_GatePassed()
    {
        var dataset = BuildSampleDataset();
        var report = new ShadowFormalRetrievalAdapter().BuildGate(BuildPlanGate(passed: true), dataset);

        Assert.IsTrue(report.AdapterPassed);
        Assert.IsTrue(report.GatePassed);
        Assert.IsTrue(report.OperationId.StartsWith("shadow-formal-retrieval-adapter-gate-", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ShadowFormalRetrievalAdapter_MissingPlanGate_BlocksRun()
    {
        var report = new ShadowFormalRetrievalAdapter().BuildAdapter(planGate: null, BuildSampleDataset());

        Assert.IsFalse(report.AdapterPassed);
        Assert.AreEqual(
            ShadowFormalRetrievalAdapterRecommendations.BlockedByMissingPlanGate,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "MissingPlanGate");
    }

    [TestMethod]
    public void ShadowFormalRetrievalAdapter_PlanGateNotPassed_BlocksRun()
    {
        var report = new ShadowFormalRetrievalAdapter().BuildAdapter(BuildPlanGate(passed: false), BuildSampleDataset());

        Assert.IsFalse(report.AdapterPassed);
        Assert.AreEqual(
            ShadowFormalRetrievalAdapterRecommendations.BlockedByPlanGateNotPassed,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "PlanGateNotPassed");
    }

    [TestMethod]
    public void ShadowFormalRetrievalAdapter_MissingDataset_BlocksRun()
    {
        var report = new ShadowFormalRetrievalAdapter().BuildAdapter(
            BuildPlanGate(passed: true),
            new RetrievalDatasetV2GeneratedDataset());

        Assert.IsFalse(report.AdapterPassed);
        Assert.AreEqual(
            ShadowFormalRetrievalAdapterRecommendations.BlockedByMissingDataset,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "MissingDataset");
    }

    [TestMethod]
    public void ShadowFormalRetrievalAdapter_MustNotHitDetected_BlocksRun()
    {
        var dataset = BuildMustNotHitDataset();
        var report = new ShadowFormalRetrievalAdapter().BuildAdapter(BuildPlanGate(passed: true), dataset);

        Assert.IsFalse(report.AdapterPassed);
        Assert.AreEqual(0, report.TotalFilteredCandidateCount);
        Assert.AreEqual(0, report.MustNotHitRiskAfterPolicy, "post-policy must-not metric must remain 0; the gate enforces must-not before merging.");
        Assert.AreEqual(
            ShadowFormalRetrievalAdapterRecommendations.BlockedByEmptyShadowOutput,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "EmptyShadowOutput");
    }

    [TestMethod]
    public void ShadowFormalRetrievalAdapter_PlanGateRuntimeAttempt_BlocksRun()
    {
        var planGate = BuildPlanGate(passed: true);
        var mutated = new ShadowFormalRetrievalAdapterPlanReport
        {
            PlanPassed = planGate.PlanPassed,
            VectorProviderSource = planGate.VectorProviderSource,
            GraphCandidateSource = planGate.GraphCandidateSource,
            AdapterInputs = planGate.AdapterInputs,
            AdapterOutputs = planGate.AdapterOutputs,
            GateOrder = planGate.GateOrder,
            RuntimeSwitchAllowed = true
        };
        var report = new ShadowFormalRetrievalAdapter().BuildAdapter(mutated, BuildSampleDataset());

        Assert.IsFalse(report.AdapterPassed);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "PlanGateRuntimeSwitchAllowed");
    }

    [TestMethod]
    public void ShadowFormalRetrievalAdapter_HasNoKnownFixtureTerms()
    {
        var sourcePath = ResolveRepoFile(
            "src",
            "ContextCore.Core",
            "Services",
            "Vector",
            "ShadowFormalRetrievalAdapter.cs");
        var source = File.ReadAllText(Path.GetFullPath(sourcePath));

        foreach (var forbidden in new[] { "林风", "苍穹大陆", "九转金丹", "龙魂草", "拍卖行" })
        {
            Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal), $"Adapter must not contain fixed eval content: {forbidden}");
        }
    }

    private static ShadowFormalRetrievalAdapterPlanReport BuildPlanGate(bool passed)
        => new()
        {
            PlanPassed = passed,
            Recommendation = passed
                ? ShadowFormalRetrievalAdapterPlanRecommendations.ReadyForShadowAdapterDesignFreeze
                : ShadowFormalRetrievalAdapterPlanRecommendations.KeepPreviewOnly,
            AllowedMode = "PlanOnly",
            VectorProviderSource = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            GraphCandidateSource = "read-only relation evidence / expansion preview",
            AdapterInputs = new[] { "query", "workspaceId", "collectionId", "package context", "baseline formal retrieval/package snapshot" },
            AdapterOutputs = new[] { "shadow candidates only", "comparison artifact", "trace artifact", "risk/eligibility diagnostics" },
            GateOrder = new[]
            {
                "provider scope isolation",
                "candidate eligibility",
                "lifecycle projection",
                "risk projection",
                "must-not risk gate",
                "post-scoring risk gate",
                "formal output/package invariant gate"
            },
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            PackingPolicyChanged = false,
            PackageOutputChanged = false,
            VectorStoreBindingChanged = false,
            FormalPackageWritten = false,
            NoRuntimeMutationInvariant = true
        };

    private static RetrievalDatasetV2GeneratedDataset BuildSampleDataset()
    {
        var corpus = new[]
        {
            BuildCorpusItem(
                itemId: "shadow-target-active",
                content: "active stable target candidate matches the shadow query goals",
                tags: new[] { "shadow", "target", "active" },
                anchors: new[] { "shadow", "target" },
                evidenceRefs: new[] { "evidence-shadow-1" },
                relations: new[] { ("rel-shadow-1", "rel-shadow-1-target") }),
            BuildCorpusItem(
                itemId: "shadow-graph-bridge",
                content: "bridge node referenced by relation evidence supporting the query",
                tags: new[] { "shadow", "bridge" },
                anchors: new[] { "bridge", "graph" },
                sourceRefs: new[] { "src-shadow-1" },
                relations: new[] { ("rel-shadow-1", "rel-shadow-1-target") }),
            BuildCorpusItem(
                itemId: "shadow-deprecated",
                content: "deprecated rule that must not be promoted into shadow output",
                tags: new[] { "shadow", "deprecated" },
                lifecycle: "Deprecated",
                replacementState: "superseded"),
            BuildCorpusItem(
                itemId: "shadow-other-section",
                targetSection: VectorQueryTargetSections.Excluded,
                content: "excluded section item that should not surface as shadow candidate",
                tags: new[] { "shadow", "excluded" })
        };

        var sample = new RetrievalDatasetV2Sample
        {
            SampleId = "sample-shadow-1",
            QueryText = "shadow target query about active stable bridge",
            ExpectedTargetSection = VectorQueryTargetSections.NormalContext,
            MustHitItemIds = new[] { "shadow-target-active" },
            MustNotHitItemIds = new[] { "shadow-deprecated" },
            EvidenceRefs = new[] { "evidence-shadow-1" },
            SourceRefs = new[] { "src-shadow-1" },
            RequiredRelations = new[] { "rel-shadow-1" },
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["workspaceId"] = "ws-shadow",
                ["collectionId"] = "col-shadow"
            }
        };

        return new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = corpus,
            Samples = new[] { sample }
        };
    }

    private static RetrievalDatasetV2GeneratedDataset BuildMustNotHitDataset()
    {
        var corpus = new[]
        {
            BuildCorpusItem(
                itemId: "shadow-mustnot-1",
                content: "must not hit candidate that scores high on shadow query but is forbidden",
                tags: new[] { "shadow", "mustnot" },
                anchors: new[] { "shadow", "mustnot" },
                evidenceRefs: new[] { "evidence-shadow-mn-1" })
        };

        var sample = new RetrievalDatasetV2Sample
        {
            SampleId = "sample-shadow-mustnot",
            QueryText = "shadow query that surfaces the must not hit candidate",
            ExpectedTargetSection = VectorQueryTargetSections.NormalContext,
            MustHitItemIds = Array.Empty<string>(),
            MustNotHitItemIds = new[] { "shadow-mustnot-1" },
            EvidenceRefs = new[] { "evidence-shadow-mn-1" }
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
