using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

[TestClass]
[TestCategory("Constraint")]
public sealed class ContextCoreConstraintGapCandidateTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task ExtendedFailureTriageConstraintMissing_ShouldGenerateGap()
    {
        var root = CreateTempRoot();
        try
        {
            var extendedReportPath = await WriteExtendedReportAsync(
                root,
                sampleId: "chat-20260529-003",
                expected: "重复解释不应提升");
            var service = CreateService(new InMemoryConstraintStore());

            var result = await service.GenerateAsync(new ConstraintGapGenerationRequest
            {
                WorkspaceId = "workspace-gap",
                CollectionId = "collection-gap",
                ExtendedFailureTriageReportPath = extendedReportPath,
                IncludePlanningConstraintReport = false
            });

            Assert.AreEqual(1, result.CreatedCount);
            var gap = result.Gaps.Single();
            Assert.AreEqual("extended-failure-triage-report", gap.Source);
            Assert.AreEqual("chat-20260529-003", gap.SourceSampleId);
            Assert.AreEqual("ConstraintMiss", gap.Metadata["failureCategories"]);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public async Task AcceptGap_ShouldCreateCandidateConstraint()
    {
        var gapStore = new InMemoryConstraintGapCandidateStore();
        var constraintStore = new InMemoryConstraintStore();
        var gap = await gapStore.SaveAsync(CreateGap("gap-accept"));
        var service = new ConstraintGapCandidateService(gapStore, constraintStore);

        var result = await service.AcceptAsync(gap.GapId, CreateReviewRequest("accept-gap-op", "确认作为候选约束。"));

        Assert.IsNotNull(result);
        Assert.AreEqual(ConstraintGapStatus.Accepted, result!.Status);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.CreatedConstraintId));

        var constraints = await constraintStore.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = gap.WorkspaceId,
            CollectionId = gap.CollectionId,
            Status = ContextMemoryStatus.Candidate,
            Take = 10
        });
        var created = constraints.Single(item => item.Id == result.CreatedConstraintId);
        Assert.AreEqual(ContextMemoryStatus.Candidate, created.Status);
        Assert.AreEqual(ConstraintLevel.User, created.Level);
        Assert.AreEqual(gap.ExpectedConstraintText, created.Content);
        Assert.AreEqual("constraint_gap_accept", created.Metadata["createdFrom"]);
        Assert.AreEqual(gap.GapId, created.Metadata["sourceConstraintGapId"]);
        Assert.AreEqual(gap.SourceSampleId, created.Metadata["sourceSampleId"]);
        Assert.AreEqual(gap.SourceOperationId, created.Metadata["sourceOperationId"]);
        Assert.AreEqual(gap.ExpectedConstraintText, created.Metadata["expectedConstraintText"]);
        Assert.AreEqual("reviewer-1", created.Metadata["reviewer"]);
        Assert.AreEqual("确认作为候选约束。", created.Metadata["reviewReason"]);
        Assert.AreEqual("event-gap-1,event-gap-2", created.Metadata["evidenceRefs"]);
        Assert.AreEqual("Candidate", created.Metadata["status"]);
        CollectionAssert.Contains(created.SourceRefs.ToArray(), gap.GapId);
        CollectionAssert.Contains(created.SourceRefs.ToArray(), gap.SourceSampleId);

        var hardActiveConstraints = await constraintStore.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = gap.WorkspaceId,
            CollectionId = gap.CollectionId,
            Level = ConstraintLevel.Hard,
            Status = ContextMemoryStatus.Active,
            Take = 10
        });
        Assert.AreEqual(0, hardActiveConstraints.Count);
    }

    [TestMethod]
    public async Task RejectGap_ShouldRecordReviewWithoutDeletingGap()
    {
        var gapStore = new InMemoryConstraintGapCandidateStore();
        var constraintStore = new InMemoryConstraintStore();
        var gap = await gapStore.SaveAsync(CreateGap("gap-reject"));
        var service = new ConstraintGapCandidateService(gapStore, constraintStore);

        var result = await service.RejectAsync(gap.GapId, CreateReviewRequest("reject-gap-op", "不是可落库约束。"));

        Assert.IsNotNull(result);
        Assert.AreEqual(ConstraintGapStatus.Rejected, result!.Status);
        Assert.IsNull(result.CreatedConstraintId);
        var updated = await gapStore.GetAsync(gap.GapId);
        Assert.IsNotNull(updated);
        Assert.AreEqual(ConstraintGapStatus.Rejected, updated!.Status);
        var reviews = await service.GetReviewsAsync(gap.GapId);
        Assert.AreEqual(1, reviews.Count);
        Assert.AreEqual("reject", reviews[0].Action);
        Assert.AreEqual("不是可落库约束。", reviews[0].Reason);

        var constraints = await constraintStore.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = gap.WorkspaceId,
            CollectionId = gap.CollectionId,
            Status = ContextMemoryStatus.Candidate,
            Take = 10
        });
        Assert.AreEqual(0, constraints.Count);
    }

    [TestMethod]
    public async Task AcceptedGap_ShouldNotBeAcceptedAgain()
    {
        var gapStore = new InMemoryConstraintGapCandidateStore();
        var service = new ConstraintGapCandidateService(gapStore, new InMemoryConstraintStore());
        var gap = await gapStore.SaveAsync(CreateGap("gap-duplicate-accept"));

        await service.AcceptAsync(gap.GapId, CreateReviewRequest("accept-first", "首次接受。"));

        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            service.AcceptAsync(gap.GapId, CreateReviewRequest("accept-second", "重复接受。")));
    }

    private static ConstraintGapCandidateService CreateService(IConstraintStore constraintStore)
    {
        return new ConstraintGapCandidateService(
            new InMemoryConstraintGapCandidateStore(),
            constraintStore);
    }

    private static ConstraintGapReviewRequest CreateReviewRequest(string operationId, string reason)
    {
        return new ConstraintGapReviewRequest
        {
            OperationId = operationId,
            Reviewer = "reviewer-1",
            Reason = reason
        };
    }

    private static ConstraintGapCandidate CreateGap(string gapId)
    {
        return new ConstraintGapCandidate
        {
            GapId = gapId,
            WorkspaceId = "workspace-gap",
            CollectionId = "collection-gap",
            SessionId = "session-gap",
            Source = "planning-optin-constraint-safety-report",
            SourceSampleId = "sample-gap",
            SourceOperationId = "planning-op-gap",
            ExpectedConstraintText = "恢复点必须保留",
            SuggestedConstraintTitle = "恢复点必须保留",
            SuggestedConstraintScope = "Collection",
            SuggestedConstraintType = "Hard",
            Severity = ConstraintGapSeverity.High,
            Reason = "Expected hard constraint missing.",
            EvidenceRefs = ["event-gap-1", "event-gap-2"],
            Status = ConstraintGapStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static async Task<string> WriteExtendedReportAsync(
        string root,
        string sampleId,
        string expected)
    {
        var path = Path.Combine(root, "extended-triage-report.json");
        var report = new ExtendedFailureTriageReport
        {
            OperationId = "extended-report-op",
            TotalSamples = 1,
            FailedSamples = 1,
            Samples =
            [
                new ExtendedFailureTriageSample
                {
                    SampleId = sampleId,
                    Mode = "ChatMode",
                    FailedReason = "constraint missing",
                    FailureCategories = ["ConstraintMiss"],
                    ConstraintStatus = new ExtendedFailureExpectationStatus
                    {
                        Satisfied = false,
                        Expected = [expected],
                        Missing = [expected]
                    },
                    SuspectedRootCause = "Expected constraint text is not represented in constraints/package sections.",
                    SuggestedFixType = "corpus gap review"
                }
            ]
        };
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, JsonOptions));
        return path;
    }

    private static string CreateTempRoot()
    {
        return Path.Combine(Directory.GetCurrentDirectory(), "constraint-gap-test-data", Guid.NewGuid().ToString("N"));
    }

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TrackingConstraintStore : IConstraintStore
    {
        public int SaveCount { get; private set; }

        public int QueryCount { get; private set; }

        public Task SaveAsync(ContextConstraint constraint, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task<ContextConstraint?> GetAsync(
            string constraintId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ContextConstraint?>(null);
        }

        public Task<IReadOnlyList<ContextConstraint>> QueryAsync(
            ContextConstraintQuery query,
            CancellationToken cancellationToken = default)
        {
            QueryCount++;
            return Task.FromResult<IReadOnlyList<ContextConstraint>>(Array.Empty<ContextConstraint>());
        }
    }
}
