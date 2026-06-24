using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreRetrievalEvalProtocolAuditTests
{
    [TestMethod]
    public void RetrievalEvalProtocolAudit_CleanDataset_GatePasses()
    {
        var report = new RetrievalEvalProtocolAuditRunner().Build(
            BuildDataset(),
            BuildRuntimeGate(passed: true),
            BuildSourceScan(clean: true));

        Assert.IsTrue(report.ProtocolAudit.ProtocolPassed, string.Join(",", report.ProtocolAudit.BlockedReasons));
        Assert.IsTrue(report.Gate.GatePassed, string.Join(",", report.Gate.BlockedReasons));
        Assert.AreEqual(0, report.Gate.HashOrderSensitivityCount);
        Assert.IsTrue(report.Gate.TieBreakDeterministic);
        Assert.IsFalse(report.Gate.EvalLabelScoringDetected);
        Assert.IsFalse(report.Gate.EvalLabelCandidateGenerationDetected);
        Assert.AreEqual(0, report.Gate.RiskAfterPolicy);
        Assert.AreEqual(0, report.Gate.MustNotHitRiskAfterPolicy);
        Assert.AreEqual(0, report.Gate.LifecycleRiskAfterPolicy);
        Assert.IsFalse(report.Gate.FormalRetrievalAllowed);
        Assert.IsFalse(report.Gate.RuntimeSwitchAllowed);
        Assert.IsFalse(report.Gate.ReadyForRuntimeSwitch);
        Assert.IsFalse(report.Gate.UseForRuntime);
    }

    [TestMethod]
    public void CandidateSourceDiscriminability_EvidenceSourceReportsUniqueRecovery()
    {
        var report = new RetrievalEvalProtocolAuditRunner().Build(
            BuildDataset(),
            BuildRuntimeGate(passed: true),
            BuildSourceScan(clean: true));

        var evidence = report.SourceDiscriminabilityAudit.SourceSummaries
            .Single(source => source.SourceId == RetrievalCandidateSourceIds.EvidenceSource);
        Assert.IsTrue(evidence.UniqueCandidateCount > 0);
        Assert.IsTrue(evidence.UniqueMustHitRecoveryCount > 0);
        Assert.IsTrue(evidence.MarginalRecall > 0);
    }

    [TestMethod]
    public void RetrievalEvalProtocolGate_MissingRuntimeGate_Blocks()
    {
        var report = new RetrievalEvalProtocolAuditRunner().Build(
            BuildDataset(),
            BuildRuntimeGate(passed: false),
            BuildSourceScan(clean: true));

        Assert.IsFalse(report.Gate.GatePassed);
        CollectionAssert.Contains(report.Gate.BlockedReasons.ToList(), "RuntimeChangeReadinessGateNotPassed");
    }

    [TestMethod]
    public void RetrievalEvalProtocolAudit_SourceScanHit_Blocks()
    {
        var report = new RetrievalEvalProtocolAuditRunner().Build(
            BuildDataset(),
            BuildRuntimeGate(passed: true),
            BuildSourceScan(clean: false));

        Assert.IsFalse(report.Gate.GatePassed);
        CollectionAssert.Contains(report.Gate.BlockedReasons.ToList(), "EvalLabelOrFixtureSpecialCasingDetected");
    }

    [TestMethod]
    public void RetrievalEvalProtocolAudit_HasNoKnownFixtureTerms()
    {
        var source = File.ReadAllText(ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "Evaluation", "V5", "RetrievalEvalProtocolAuditRunner.cs"));
        foreach (var forbidden in new[] { "林风", "苍穹大陆", "九转金丹", "龙魂草", "拍卖行" })
        {
            Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal), $"runner must not contain fixed eval content: {forbidden}");
        }

        Assert.IsFalse(source.Contains("sample.SampleId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("item.ItemId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-pkg", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-shadow", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-audit", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-repair", StringComparison.Ordinal));
    }

    private static RetrievalDatasetV2GeneratedDataset BuildDataset()
    {
        var corpus = new[]
        {
            BuildItem(
                "item-active-1",
                "restore checkpoint guidance rollback signal stable handling",
                ["restore", "rollback"],
                ["checkpoint", "signal"],
                ["ev-restore"],
                ["src-restore"],
                [("rel-restore", "item-active-1")]),
            BuildItem(
                "item-evidence-1",
                "operational record with external marker only",
                ["ops"],
                ["marker"],
                ["ev-special"],
                ["src-special"],
                [("rel-special", "item-evidence-1")]),
            BuildItem(
                "item-negative-1",
                "unrelated archived note",
                ["archive"],
                ["unrelated"],
                ["ev-negative"],
                ["src-negative"],
                [])
        };
        var samples = new[]
        {
            new RetrievalDatasetV2Sample
            {
                SampleId = "sample-active-1",
                QueryText = "restore rollback checkpoint guidance",
                Difficulty = "direct_lexical",
                Split = "train",
                ExpectedTargetSection = VectorQueryTargetSections.NormalContext,
                MustHitItemIds = ["item-active-1"],
                MustNotHitItemIds = ["item-negative-1"]
            },
            new RetrievalDatasetV2Sample
            {
                SampleId = "sample-evidence-1",
                QueryText = "ev-special src-special",
                Difficulty = "metadata_anchor",
                Split = "holdout",
                ExpectedTargetSection = VectorQueryTargetSections.NormalContext,
                MustHitItemIds = ["item-evidence-1"],
                MustNotHitItemIds = ["item-negative-1"]
            }
        };
        return new RetrievalDatasetV2GeneratedDataset { CorpusItems = corpus, Samples = samples };
    }

    private static RetrievalDatasetV2CorpusItem BuildItem(
        string id,
        string content,
        string[] tags,
        string[] anchors,
        string[] evidenceRefs,
        string[] sourceRefs,
        (string Id, string Target)[] relations)
        => new()
        {
            ItemId = id,
            ItemKind = "note",
            SourceKind = "note",
            Layer = "Stable",
            Lifecycle = "Active",
            ReviewStatus = "Reviewed",
            ReplacementState = "Current",
            TargetSection = VectorQueryTargetSections.NormalContext,
            SourceRefs = sourceRefs,
            EvidenceRefs = evidenceRefs,
            Provenance = new RetrievalDatasetV2Provenance
            {
                RecordId = "prov-" + id,
                SourceFingerprint = "fingerprint-" + id,
                IngestionBatchId = "batch-test"
            },
            SourceFingerprint = "fingerprint-" + id,
            Relations = relations
                .Select(relation => new RetrievalDatasetV2Relation
                {
                    RelationId = relation.Id,
                    SourceItemId = id,
                    TargetItemId = relation.Target,
                    RelationType = "supports",
                    SourceRefs = sourceRefs,
                    EvidenceRefs = evidenceRefs
                })
                .ToArray(),
            Tags = tags,
            Anchors = anchors,
            Content = content,
            Split = "train"
        };

    private static LearningRuntimeChangeReadinessGateReport BuildRuntimeGate(bool passed)
        => new()
        {
            Passed = passed,
            Recommendation = passed ? "RuntimeChangeRulesSatisfied" : "KeepRuntimeDefaults",
            FailedConditions = passed ? Array.Empty<string>() : ["RuntimeChangeGateNotPassed"]
        };

    private static RuntimeObservableFeatureContractSourceScan BuildSourceScan(bool clean)
        => new()
        {
            ScanPerformed = true,
            ScannedFileCount = 1,
            FixtureTokenHitCount = clean ? 0 : 1,
            ScannedFiles = ["RetrievalEvalProtocolAuditRunner.cs"],
            FlaggedFiles = clean ? Array.Empty<string>() : ["RetrievalEvalProtocolAuditRunner.cs"],
            FlaggedTokens = clean ? Array.Empty<string>() : ["sample.SampleId =="]
        };

    private static string ResolveRepoFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);return TestRepoFileResolver.Resolve(segments);}
}
