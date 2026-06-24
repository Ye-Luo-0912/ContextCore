using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreSourceDiverseShadowAdapterValidationTests
{
    [TestMethod]
    public void SourceDiverseShadowAdapterValidation_GatePassesWithSourceDiverseSet()
    {
        var report = new SourceDiverseShadowAdapterValidationRunner().RunValidation(
            CleanV65Gate(),
            options: new SourceDiverseShadowAdapterValidationOptions { GateMode = true });

        Assert.IsTrue(report.ValidationPassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.AreEqual(SourceDiverseShadowAdapterValidationRecommendations.ReadyForAdapterDeltaDecision, report.Recommendation);
        Assert.IsTrue(report.ValidationSetSourceDiverse);
        Assert.IsTrue(report.AllowlistedScopeMetadataPresent);
        Assert.IsTrue(report.ShadowOnlyCount > 0);
        Assert.IsTrue(report.HypotheticalAddCount > 0);
        Assert.IsTrue(report.HypotheticalRemoveCount > 0);
        Assert.AreEqual(0, report.AppliedAddCount);
        Assert.AreEqual(0, report.AppliedRemoveCount);
        Assert.AreEqual(0, report.RiskAfterPolicy);
        Assert.AreEqual(0, report.MustNotHitRiskAfterPolicy);
        Assert.AreEqual(0, report.LifecycleRiskAfterPolicy);
        Assert.IsFalse(report.FormalSelectedSetChanged);
        Assert.IsFalse(report.FormalPackageWritten);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.RuntimeMutated);
        Assert.IsFalse(report.VectorStoreBindingChanged);
        Assert.IsFalse(report.UseForRuntime);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
    }

    [TestMethod]
    public void SourceDiverseShadowAdapterValidation_MissingV65GateBlocks()
    {
        var report = new SourceDiverseShadowAdapterValidationRunner().RunValidation(
            null,
            options: new SourceDiverseShadowAdapterValidationOptions { GateMode = true });

        Assert.IsFalse(report.ValidationPassed);
        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(SourceDiverseShadowAdapterValidationRecommendations.BlockedByMissingV65Gate, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "V65GateMissingOrNotPassed");
    }

    [TestMethod]
    public void SourceDiverseShadowAdapterValidation_NonDiverseSetBlocks()
    {
        var report = new SourceDiverseShadowAdapterValidationRunner().RunValidation(
            CleanV65Gate(),
            BuildNonDiverseDataset(),
            new SourceDiverseShadowAdapterValidationOptions { GateMode = true });

        Assert.IsFalse(report.ValidationPassed);
        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(SourceDiverseShadowAdapterValidationRecommendations.NeedsSourceDiverseValidationSet, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ValidationSetNotSourceDiverse");
    }

    [TestMethod]
    public void SourceDiverseShadowAdapterValidation_RuntimeMutationAttemptBlocks()
    {
        var report = new SourceDiverseShadowAdapterValidationRunner().RunValidation(
            CleanV65Gate(),
            options: new SourceDiverseShadowAdapterValidationOptions
            {
                GateMode = true,
                UseForRuntime = true
            });

        Assert.IsFalse(report.ValidationPassed);
        Assert.AreEqual(SourceDiverseShadowAdapterValidationRecommendations.BlockedByRuntimeInvariant, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeOrFormalInvariantChanged");
    }

    [TestMethod]
    public void SourceDiverseShadowAdapterValidation_SourceDoesNotSpecialCaseSamplesOrItems()
    {
        var source = File.ReadAllText(ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "Evaluation", "V6", "SourceDiverseShadowAdapterValidationRunner.cs"));
        Assert.IsFalse(source.Contains("sample.SampleId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("item.ItemId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("mustHitItemId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-shadow", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-audit", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-repair", StringComparison.Ordinal));
    }

    private static ShadowAdapterDeltaDiagnosticsReport CleanV65Gate() => new()
    {
        DiagnosticsPassed = true,
        Recommendations = "ReadyForShadowAdapterDeltaTriage"
    };

    private static RetrievalDatasetV2GeneratedDataset BuildNonDiverseDataset()
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["workspaceId"] = "contextcore-foundation",
            ["collectionId"] = "source-diverse-shadow-validation",
            ["evalScope"] = "v6-source-diverse-shadow-validation"
        };

        return new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = new[]
            {
                new RetrievalDatasetV2CorpusItem
                {
                    ItemId = "validation-item-a",
                    ItemKind = "note",
                    SourceKind = "note",
                    Layer = "runtime_observable",
                    Lifecycle = "Active",
                    ReviewStatus = "Stable",
                    ReplacementState = "current",
                    TargetSection = VectorQueryTargetSections.NormalContext,
                    SourceRefs = new[] { "src-one" },
                    EvidenceRefs = new[] { "ev-one" },
                    Provenance = new RetrievalDatasetV2Provenance
                    {
                        RecordId = "prov-one",
                        SourceFingerprint = "fp-one",
                        IngestionBatchId = "batch-one"
                    },
                    SourceFingerprint = "fp-one",
                    Relations = Array.Empty<RetrievalDatasetV2Relation>(),
                    Tags = new[] { "anchor-one" },
                    Anchors = new[] { "anchor-one" },
                    Content = "generic validation item",
                    Metadata = metadata
                }
            },
            Samples = new[]
            {
                new RetrievalDatasetV2Sample
                {
                    SampleId = "validation-sample-a",
                    QueryText = "generic validation query src-one ev-one",
                    Difficulty = "direct",
                    ExpectedTargetSection = VectorQueryTargetSections.NormalContext,
                    MustHitItemIds = new[] { "validation-item-a" },
                    Split = "test",
                    Metadata = metadata
                }
            }
        };
    }

    private static string ResolveRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);return TestRepoFileResolver.Resolve(parts);}
}
