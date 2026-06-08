using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;

namespace ContextCore.Tests;

/// <summary>覆盖 Promotion Candidate Store 的读写分离和状态过滤。</summary>
[TestClass]
public sealed class ContextCorePromotionCandidateStoreTests
{
    [TestMethod]
    public async Task InMemoryPromotionCandidateStore_ShouldSaveQueryAndUpdateStatus()
    {
        var store = new InMemoryMemoryStore();

        await AssertCandidateStoreAsync(store);
    }

    [TestMethod]
    public async Task FilePromotionCandidateStore_ShouldPersistCandidates()
    {
        var root = Path.Combine(Path.GetTempPath(), "contextcore-candidate-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var firstStore = new FileMemoryStore(new FileStorageOptions { RootPath = root });
            await AssertCandidateStoreAsync(firstStore);

            var secondStore = new FileMemoryStore(new FileStorageOptions { RootPath = root });
            var accepted = await secondStore.QueryPromotionCandidatesAsync(
                "workspace-test",
                "collection-test",
                PromotionCandidateStatus.Accepted,
                take: 10);

            Assert.AreEqual(1, accepted.Count);
            Assert.AreEqual("candidate-a", accepted[0].Id);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task AssertCandidateStoreAsync(IPromotionCandidateStore store)
    {
        await store.SavePromotionCandidateAsync(CreateCandidate(
            "candidate-a",
            PromotionCandidateStatus.Candidate,
            "阶段性结论：候选存储已写入。"));
        await store.SavePromotionCandidateAsync(CreateCandidate(
            "candidate-b",
            PromotionCandidateStatus.NeedsReview,
            "规则信号不足，需要审核。"));

        var candidates = await store.QueryPromotionCandidatesAsync(
            "workspace-test",
            "collection-test",
            PromotionCandidateStatus.Candidate,
            take: 10);
        var needsReview = await store.QueryPromotionCandidatesAsync(
            "workspace-test",
            "collection-test",
            PromotionCandidateStatus.NeedsReview,
            take: 10);

        Assert.AreEqual(1, candidates.Count);
        Assert.AreEqual("candidate-a", candidates[0].Id);
        Assert.AreEqual(1, needsReview.Count);

        var updated = await store.UpdatePromotionCandidateStatusAsync(
            "workspace-test",
            "collection-test",
            "candidate-a",
            PromotionCandidateStatus.Accepted,
            reviewer: "reviewer-test",
            reason: "确认可提升。");
        var accepted = await store.QueryPromotionCandidatesAsync(
            "workspace-test",
            "collection-test",
            PromotionCandidateStatus.Accepted,
            take: 10);
        var fetched = await store.GetPromotionCandidateAsync(
            "workspace-test",
            "collection-test",
            "candidate-a");

        Assert.IsNotNull(updated);
        Assert.AreEqual(PromotionCandidateStatus.Accepted, updated!.Status);
        Assert.AreEqual("reviewer-test", updated.Reviewer);
        Assert.AreEqual("确认可提升。", updated.Reason);
        Assert.AreEqual(1, accepted.Count);
        Assert.IsNotNull(fetched);
        Assert.AreEqual(PromotionCandidateStatus.Accepted, fetched!.Status);
    }

    private static PromotionCandidate CreateCandidate(
        string id,
        PromotionCandidateStatus status,
        string content)
    {
        var now = DateTimeOffset.UtcNow;
        return new PromotionCandidate
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = "source-" + id,
            SourceKind = "context",
            Content = content,
            TargetLayer = ContextMemoryLayer.Working,
            Status = status,
            Decision = PromotionEvaluationDecision.PromoteToWorkingMemory,
            Category = "阶段性结论",
            Reason = "命中中期记忆 Promotion 条件。",
            Confidence = 0.8,
            MatchedRules = ["阶段性结论"],
            SourceRefs = ["source:" + id],
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
