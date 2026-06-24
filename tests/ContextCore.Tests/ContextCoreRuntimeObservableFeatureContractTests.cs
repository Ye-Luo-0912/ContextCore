using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreRuntimeObservableFeatureContractTests
{
    [TestMethod]
    public void RuntimeObservableFeatureContract_CleanInputs_ContractPasses()
    {
        var report = new RuntimeObservableRetrievalFeatureContractRunner()
            .BuildContract(BuildRepairGate(passed: true), BuildSourceScan(clean: true));

        Assert.IsTrue(report.ContractPassed,
            $"contract should pass; blocked={string.Join(",", report.BlockedReasons)}");
        Assert.AreEqual(
            RuntimeObservableFeatureContractRecommendations.ReadyForRuntimeObservableFeatureFreeze,
            report.Recommendation);
        Assert.AreEqual(8, report.Profiles.Count);
        Assert.AreEqual(RetrievalQualityRepairProfiles.Combined, report.BestProfileId);
        Assert.AreEqual(
            RuntimeObservableFeatureContractStatuses.RequiresRuntimeDerivation,
            report.BestProfileContractStatus);
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
        // Catalog must contain key sample-side feature ids.
        var featureIds = report.Catalog.Select(f => f.FeatureId).ToList();
        CollectionAssert.Contains(featureIds, "item.Relations × sample.RequiredRelations");
        CollectionAssert.Contains(featureIds, "item.TargetSection × sample.ExpectedTargetSection");
        CollectionAssert.Contains(featureIds, "sample.MustHitItemIds (graph collection)");
        // Best profile must not use forbidden / eval-only features in scoring.
        var best = report.Profiles.First(p => string.Equals(p.ProfileId, report.BestProfileId, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(best.UsesForbiddenForScoring);
        Assert.IsFalse(best.UsesEvalOnlyForScoring);
        Assert.IsTrue(best.RequiresRuntimeDerivation);
        Assert.IsTrue(best.RequiredRuntimeDerivationPaths.Count > 0);
    }

    [TestMethod]
    public void RuntimeObservableFeatureContract_GateMode_GatePassed()
    {
        var report = new RuntimeObservableRetrievalFeatureContractRunner()
            .BuildGate(BuildRepairGate(passed: true), BuildSourceScan(clean: true));

        Assert.IsTrue(report.ContractPassed);
        Assert.IsTrue(report.GatePassed);
        Assert.IsTrue(report.OperationId.StartsWith("runtime-observable-feature-contract-gate-", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RuntimeObservableFeatureContract_MissingRepairGate_BlocksRun()
    {
        var report = new RuntimeObservableRetrievalFeatureContractRunner()
            .BuildContract(repairGate: null, BuildSourceScan(clean: true));

        Assert.IsFalse(report.ContractPassed);
        Assert.AreEqual(
            RuntimeObservableFeatureContractRecommendations.BlockedByMissingRepairGate,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RepairGateMissing");
    }

    [TestMethod]
    public void RuntimeObservableFeatureContract_RepairGateNotPassed_BlocksRun()
    {
        var report = new RuntimeObservableRetrievalFeatureContractRunner()
            .BuildContract(BuildRepairGate(passed: false), BuildSourceScan(clean: true));

        Assert.IsFalse(report.ContractPassed);
        Assert.AreEqual(
            RuntimeObservableFeatureContractRecommendations.BlockedByRepairGateNotPassed,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RepairGateNotPassed");
    }

    [TestMethod]
    public void RuntimeObservableFeatureContract_RepairGateRuntimeAttempt_BlocksRun()
    {
        var repairGate = BuildRepairGate(passed: true);
        var mutated = new RetrievalQualityRepairPreviewReport
        {
            PreviewPassed = repairGate.PreviewPassed,
            GatePassed = repairGate.GatePassed,
            VectorProviderSource = repairGate.VectorProviderSource,
            GraphCandidateSource = repairGate.GraphCandidateSource,
            BestProfileId = repairGate.BestProfileId,
            Profiles = repairGate.Profiles,
            Baseline = repairGate.Baseline,
            RuntimeMutated = true
        };

        var report = new RuntimeObservableRetrievalFeatureContractRunner()
            .BuildContract(mutated, BuildSourceScan(clean: true));

        Assert.IsFalse(report.ContractPassed);
        Assert.AreEqual(
            RuntimeObservableFeatureContractRecommendations.BlockedByRuntimeMutation,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RepairGateRuntimeMutated");
    }

    [TestMethod]
    public void RuntimeObservableFeatureContract_FixtureSpecialCasing_BlocksRun()
    {
        var dirtyScan = new RuntimeObservableFeatureContractSourceScan
        {
            ScanPerformed = true,
            ScannedFileCount = 5,
            FixtureTokenHitCount = 2,
            ScannedFiles = new[] { "RetrievalQualityRepairPreviewRunner.cs" },
            FlaggedFiles = new[] { "RetrievalQualityRepairPreviewRunner.cs" },
            FlaggedTokens = new[] { "苍穹大陆", "sample.SampleId ==" }
        };
        var report = new RuntimeObservableRetrievalFeatureContractRunner()
            .BuildContract(BuildRepairGate(passed: true), dirtyScan);

        Assert.IsFalse(report.ContractPassed);
        Assert.AreEqual(
            RuntimeObservableFeatureContractRecommendations.BlockedByFixtureSpecialCasing,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "FixtureSpecialCasingDetected");
    }

    [TestMethod]
    public void RuntimeObservableFeatureContract_RequiresSourceScan_BlocksRun()
    {
        var report = new RuntimeObservableRetrievalFeatureContractRunner()
            .BuildContract(BuildRepairGate(passed: true), sourceScan: null);

        Assert.IsFalse(report.ContractPassed);
        Assert.AreEqual(
            RuntimeObservableFeatureContractRecommendations.BlockedBySourceScanMissing,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "SourceScanMissing");
    }

    [TestMethod]
    public void RuntimeObservableFeatureContract_HasNoKnownFixtureTerms()
    {
        var sourcePath = ResolveRepoFile(
            "src",
            "ContextCore.Core",
            "Services",
            "Vector",
            "RuntimeObservableRetrievalFeatureContractRunner.cs");
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

    private static RetrievalQualityRepairPreviewReport BuildRepairGate(bool passed)
    {
        var baseline = new RetrievalQualityRepairProfileResult
        {
            ProfileId = RetrievalQualityRepairProfiles.Baseline,
            ProfileLabel = "Baseline",
            Recall = 0.5,
            Precision = 0.1,
            MeanReciprocalRank = 0.25
        };
        var combined = new RetrievalQualityRepairProfileResult
        {
            ProfileId = RetrievalQualityRepairProfiles.Combined,
            ProfileLabel = "Combined repair",
            Recall = 1.0,
            Precision = 0.2,
            MeanReciprocalRank = 0.9
        };

        return new RetrievalQualityRepairPreviewReport
        {
            PreviewPassed = passed,
            GatePassed = passed,
            Recommendation = passed
                ? RetrievalQualityRepairPreviewRecommendations.ReadyForRetrievalQualityRepairFreeze
                : RetrievalQualityRepairPreviewRecommendations.KeepBaselineOnly,
            AllowedMode = "PreviewOnly",
            VectorProviderSource = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            GraphCandidateSource = "read-only relation evidence / expansion preview",
            Baseline = baseline,
            Profiles = new[] { baseline, combined },
            BestProfileId = RetrievalQualityRepairProfiles.Combined,
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
    }

    private static RuntimeObservableFeatureContractSourceScan BuildSourceScan(bool clean)
        => new()
        {
            ScanPerformed = true,
            ScannedFileCount = 5,
            FixtureTokenHitCount = clean ? 0 : 1,
            ScannedFiles = new[]
            {
                "ShadowFormalRetrievalAdapterPlanRunner.cs",
                "ShadowFormalRetrievalAdapter.cs",
                "FormalAdapterPackageShadowComparisonRunner.cs",
                "GraphVectorRetrievalQualityAuditRunner.cs",
                "RetrievalQualityRepairPreviewRunner.cs"
            },
            FlaggedFiles = clean ? Array.Empty<string>() : new[] { "RetrievalQualityRepairPreviewRunner.cs" },
            FlaggedTokens = clean ? Array.Empty<string>() : new[] { "sample.SampleId ==" }
        };

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
