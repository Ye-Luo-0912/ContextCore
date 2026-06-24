using System.Reflection;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreFormalAdapterInputContractTests
{
    [TestMethod]
    public void FormalAdapterInputContract_CleanContractPasses()
    {
        var report = new FormalAdapterInputContractRunner().BuildGate(
            CleanAdapterPlan(),
            CleanOutputPolicyGate(),
            CleanRuntimeChangeGate(),
            CleanSourceScan(evalOnlyHits: 2));

        Assert.IsTrue(report.ContractPassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.AreEqual(
            FormalAdapterInputContractRecommendations.ReadyForFormalAdapterInputContractFreeze,
            report.Recommendation);
        Assert.AreEqual(FormalAdapterInputContractRunner.ContractVersion, report.ContractVersion);
        Assert.AreEqual(0, report.ContractForbiddenPropertyCount);
        Assert.AreEqual(0, report.FormalSourceForbiddenReadCount);
        Assert.AreEqual(2, report.EvalOnlyForbiddenReadCount);
        Assert.IsTrue(report.DatasetEvalFieldsBlocked);
        Assert.IsTrue(report.GoldLabelsBlocked);
        Assert.IsTrue(report.SampleMetadataBlocked);
        Assert.IsTrue(report.ShadowArtifactFieldsBlocked);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
        Assert.IsFalse(report.UseForRuntime);
        Assert.IsFalse(report.FormalPackageWritten);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.RuntimeMutated);
        Assert.IsFalse(report.VectorStoreBindingChanged);
    }

    [TestMethod]
    public void FormalAdapterInputContract_RuntimeDtosDoNotExposeEvalOrGoldFields()
    {
        var forbiddenNames = new[]
        {
            "RetrievalDatasetV2Sample",
            "SampleId",
            "SourceEvalSet",
            "Split",
            "Difficulty",
            "TaskKind",
            "Intent",
            "Rationale",
            "MustHitItemIds",
            "MustNotHitItemIds",
            "NegativeDistractorIds",
            "ExpectedTargetSection",
            "RequiredRelations"
        };
        var runtimeProperties = RuntimeContractTypes()
            .SelectMany(static type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var forbidden in forbiddenNames)
        {
            Assert.IsFalse(runtimeProperties.Contains(forbidden), $"Runtime adapter input must not expose {forbidden}.");
        }

        CollectionAssert.Contains(runtimeProperties.ToList(), nameof(FormalAdapterRuntimeInputEnvelope.QueryText));
        CollectionAssert.Contains(runtimeProperties.ToList(), nameof(FormalAdapterRuntimeCandidateInput.Lifecycle));
        CollectionAssert.Contains(runtimeProperties.ToList(), nameof(FormalAdapterRuntimeCandidateInput.TargetSection));
    }

    [TestMethod]
    public void FormalAdapterInputContract_FormalSourceForbiddenReadBlocks()
    {
        var report = new FormalAdapterInputContractRunner().BuildGate(
            CleanAdapterPlan(),
            CleanOutputPolicyGate(),
            CleanRuntimeChangeGate(),
            FormalSourceForbiddenScan());

        Assert.IsFalse(report.ContractPassed);
        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(
            FormalAdapterInputContractRecommendations.BlockedByFormalSourceForbiddenRead,
            report.Recommendation);
        Assert.AreEqual(1, report.FormalSourceForbiddenReadCount);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "FormalAdapterSourceReadsForbiddenField");
    }

    [TestMethod]
    public void FormalAdapterInputContract_MissingOutputPolicyGateBlocks()
    {
        var report = new FormalAdapterInputContractRunner().BuildGate(
            CleanAdapterPlan(),
            new OutputTokenPriorityShadowGateReport { GatePassed = false },
            CleanRuntimeChangeGate(),
            CleanSourceScan());

        Assert.IsFalse(report.ContractPassed);
        Assert.AreEqual(
            FormalAdapterInputContractRecommendations.BlockedByMissingPrerequisiteGate,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "V515OutputPolicyGateMissingOrNotPassed");
    }

    [TestMethod]
    public void FormalAdapterInputContract_RuntimeMutationAttemptBlocks()
    {
        var report = new FormalAdapterInputContractRunner().BuildGate(
            CleanAdapterPlan(),
            CleanOutputPolicyGate(),
            CleanRuntimeChangeGate(),
            CleanSourceScan(),
            new FormalAdapterInputContractOptions
            {
                UseForRuntime = true
            });

        Assert.IsFalse(report.ContractPassed);
        Assert.AreEqual(
            FormalAdapterInputContractRecommendations.BlockedByRuntimeInvariant,
            report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeOrFormalMutationAttempt");
    }

    [TestMethod]
    public void FormalAdapterInputContract_SourceDoesNotSpecialCaseSamplesOrItems()
    {
        var source = File.ReadAllText(ResolveRepoFile(
            "src",
            "ContextCore.Core",
            "Services",
            "Vector",
            "FormalAdapterInputContractRunner.cs"));

        Assert.IsFalse(source.Contains("sample.SampleId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("item.ItemId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("mustHitItemId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-pkg", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-shadow", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-audit", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-repair", StringComparison.Ordinal));
    }

    private static ShadowFormalRetrievalAdapterPlanReport CleanAdapterPlan()
        => new()
        {
            PlanPassed = true,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            VectorStoreBindingChanged = false
        };

    private static OutputTokenPriorityShadowGateReport CleanOutputPolicyGate()
        => new()
        {
            GatePassed = true,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false
        };

    private static LearningRuntimeChangeReadinessGateReport CleanRuntimeChangeGate()
        => new()
        {
            Passed = true
        };

    private static FormalAdapterInputContractSourceScan CleanSourceScan(int evalOnlyHits = 0)
        => new()
        {
            ScanPerformed = true,
            FormalSourceFileCount = 1,
            EvalOnlySourceFileCount = evalOnlyHits > 0 ? 1 : 0,
            FormalSourceForbiddenReadCount = 0,
            EvalOnlyForbiddenReadCount = evalOnlyHits,
            FormalSourceFiles = ["src/ContextCore.Core/Services/Vector/FutureFormalAdapter.cs"],
            EvalOnlySourceFiles = evalOnlyHits > 0
                ? ["src/ContextCore.Core/Services/Vector/ShadowFormalRetrievalAdapter.cs"]
                : Array.Empty<string>(),
            Hits = Enumerable.Range(0, evalOnlyHits)
                .Select(index => new FormalAdapterInputContractSourceHit
                {
                    FilePath = "src/ContextCore.Core/Services/Vector/ShadowFormalRetrievalAdapter.cs",
                    Token = index == 0 ? "sample.MustHitItemIds" : "ShadowFormalRetrievalAdapterReport",
                    Category = index == 0 ? "GoldLabel" : "ShadowArtifact",
                    IsFormalSource = false
                })
                .ToArray()
        };

    private static FormalAdapterInputContractSourceScan FormalSourceForbiddenScan()
        => new()
        {
            ScanPerformed = true,
            FormalSourceFileCount = 1,
            EvalOnlySourceFileCount = 0,
            FormalSourceForbiddenReadCount = 1,
            EvalOnlyForbiddenReadCount = 0,
            FormalSourceFiles = ["src/ContextCore.Core/Services/Vector/FutureFormalAdapter.cs"],
            Hits =
            [
                new FormalAdapterInputContractSourceHit
                {
                    FilePath = "src/ContextCore.Core/Services/Vector/FutureFormalAdapter.cs",
                    Token = "sample.MustHitItemIds",
                    Category = "GoldLabel",
                    IsFormalSource = true
                }
            ]
        };

    private static IReadOnlyList<Type> RuntimeContractTypes()
        =>
        [
            typeof(FormalAdapterRuntimeInputEnvelope),
            typeof(FormalAdapterRuntimePackageContext),
            typeof(FormalAdapterRuntimeCandidateInput),
            typeof(FormalAdapterRuntimeProvenanceInput),
            typeof(FormalAdapterRuntimeRelationEvidenceInput)
        ];

    private static string ResolveRepoFile(params string[] segments)
    {
        var current = Directory.GetCurrentDirectory();return TestRepoFileResolver.Resolve(segments);}
}
