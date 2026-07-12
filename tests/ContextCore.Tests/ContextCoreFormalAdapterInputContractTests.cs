using System.Reflection;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Evaluation.Contracts;

namespace ContextCore.Tests;

[TestClass]
[TestCategory("Contract")]
public class ContextCoreFormalAdapterInputContractTests
{
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

    private static IReadOnlyList<Type> RuntimeContractTypes()
        =>
        [
            typeof(FormalAdapterRuntimeInputEnvelope),
            typeof(FormalAdapterRuntimePackageContext),
            typeof(FormalAdapterRuntimeCandidateInput),
            typeof(FormalAdapterRuntimeProvenanceInput),
            typeof(FormalAdapterRuntimeRelationEvidenceInput)
        ];
}
