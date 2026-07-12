using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

/// <summary>
/// Decision Evidence Contract 单元测试：验证 NullDecisionEvidenceProvider 默认行为和证据 DTO 结构。
/// </summary>
[TestClass]
[TestCategory("Decision")]
public sealed class DecisionEvidenceContractTests
{
    [TestMethod]
    public async Task NullProvider_ReturnsEmptyEvidence_AndMarksAllMissing()
    {
        var provider = new NullDecisionEvidenceProvider();
        var record = CreateRecord(candidates: new[]
        {
            new ContextDecisionCandidate { ItemId = "item-1", Outcome = ContextDecisionCandidateOutcome.Selected },
            new ContextDecisionCandidate { ItemId = "item-2", Outcome = ContextDecisionCandidateOutcome.Dropped }
        });

        var result = await provider.ResolveEvidenceAsync(record);

        Assert.AreEqual(record.DecisionId, result.DecisionId);
        Assert.AreEqual(0, result.Evidence.Count);
        Assert.IsFalse(result.IsComplete);
        CollectionAssert.AreEquivalent(new[] { "item-1", "item-2" }, result.MissingItemIds.ToList());
    }

    [TestMethod]
    public async Task NullProvider_NoCandidates_ReturnsComplete()
    {
        var provider = new NullDecisionEvidenceProvider();
        var record = CreateRecord(candidates: Array.Empty<ContextDecisionCandidate>());

        var result = await provider.ResolveEvidenceAsync(record);

        Assert.IsTrue(result.IsComplete);
        Assert.AreEqual(0, result.MissingItemIds.Count);
    }

    [TestMethod]
    public async Task NullProvider_BlankItemIds_ExcludedFromMissing()
    {
        var provider = new NullDecisionEvidenceProvider();
        var record = CreateRecord(candidates: new[]
        {
            new ContextDecisionCandidate { ItemId = "", Outcome = ContextDecisionCandidateOutcome.Selected },
            new ContextDecisionCandidate { ItemId = "   ", Outcome = ContextDecisionCandidateOutcome.Dropped },
            new ContextDecisionCandidate { ItemId = "real-1", Outcome = ContextDecisionCandidateOutcome.Selected }
        });

        var result = await provider.ResolveEvidenceAsync(record);

        CollectionAssert.AreEquivalent(new[] { "real-1" }, result.MissingItemIds.ToList());
    }

    [TestMethod]
    public async Task NullProvider_DuplicateItemIds_DeduplicatedInMissing()
    {
        var provider = new NullDecisionEvidenceProvider();
        var record = CreateRecord(candidates: new[]
        {
            new ContextDecisionCandidate { ItemId = "dup-1", Outcome = ContextDecisionCandidateOutcome.Selected },
            new ContextDecisionCandidate { ItemId = "DUP-1", Outcome = ContextDecisionCandidateOutcome.Dropped }
        });

        var result = await provider.ResolveEvidenceAsync(record);

        Assert.AreEqual(1, result.MissingItemIds.Count);
    }

    [TestMethod]
    public async Task NullProvider_NullRecord_Throws()
    {
        var provider = new NullDecisionEvidenceProvider();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(() =>
            provider.ResolveEvidenceAsync(null!));
    }

    [TestMethod]
    public void DecisionEvidence_Dto_HasExpectedFields()
    {
        var evidence = new DecisionEvidence
        {
            ItemId = "item-1",
            PrimaryRationale = "score-below-threshold",
            SecondaryRationales = new[] { "token-budget-exceeded" },
            AlternativesConsidered = new[]
            {
                new DecisionAlternative { ItemId = "alt-1", Reason = "lower-score", Score = 0.3 }
            },
            Confidence = 0.85,
            EvidenceRefs = new[] { "trace-001", "build-abc" },
            Provenance = "retrieval-trace"
        };

        Assert.AreEqual("item-1", evidence.ItemId);
        Assert.AreEqual("score-below-threshold", evidence.PrimaryRationale);
        Assert.AreEqual(1, evidence.SecondaryRationales.Count);
        Assert.AreEqual(1, evidence.AlternativesConsidered.Count);
        Assert.AreEqual(0.85, evidence.Confidence);
        Assert.AreEqual(2, evidence.EvidenceRefs.Count);
        Assert.AreEqual("retrieval-trace", evidence.Provenance);
    }

    [TestMethod]
    public void DecisionEvidenceResult_Defaults_AreSafe()
    {
        var result = new DecisionEvidenceResult();

        Assert.AreEqual(string.Empty, result.DecisionId);
        Assert.AreEqual(0, result.Evidence.Count);
        Assert.IsFalse(result.IsComplete);
        Assert.AreEqual(0, result.MissingItemIds.Count);
    }

    private static ContextDecisionRecord CreateRecord(IReadOnlyList<ContextDecisionCandidate> candidates)
    {
        return new ContextDecisionRecord
        {
            DecisionId = "decision-test-001",
            Source = ContextDecisionSource.Package,
            WorkspaceId = "ws-1",
            CollectionId = "col-1",
            Candidates = candidates,
            Outcome = new ContextDecisionOutcome
            {
                SelectedCount = candidates.Count(c => c.Outcome == ContextDecisionCandidateOutcome.Selected),
                DroppedCount = candidates.Count(c => c.Outcome == ContextDecisionCandidateOutcome.Dropped)
            },
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}
