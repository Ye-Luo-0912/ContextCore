using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Storage.InMemory;

namespace ContextCore.Tests;

[TestClass]
public sealed class ContextCoreConstraintGapCandidateTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task HardConstraintMissing_ShouldGenerateGap()
    {
        var root = CreateTempRoot();
        try
        {
            var planningReportPath = await WritePlanningReportAsync(
                root,
                sampleId: "chat-20260529-003",
                expected: "重复解释不应提升",
                missing: true);
            var service = CreateService(new InMemoryConstraintStore());

            var result = await service.GenerateAsync(new ConstraintGapGenerationRequest
            {
                WorkspaceId = "workspace-gap",
                CollectionId = "collection-gap",
                PlanningConstraintReportPath = planningReportPath,
                IncludeExtendedFailureTriageReport = false
            });

            Assert.AreEqual(1, result.CreatedCount);
            Assert.AreEqual(0, result.SkippedMatchedCount);
            Assert.AreEqual(1, result.Gaps.Count);
            var gap = result.Gaps.Single();
            Assert.AreEqual(ConstraintGapStatus.Pending, gap.Status);
            Assert.AreEqual("chat-20260529-003", gap.SourceSampleId);
            Assert.AreEqual("重复解释不应提升", gap.ExpectedConstraintText);
            Assert.AreEqual("Hard", gap.SuggestedConstraintType);
            Assert.AreEqual("Collection", gap.SuggestedConstraintScope);
            CollectionAssert.Contains(gap.EvidenceRefs.ToArray(), "eval:planning-optin-constraint-safety-report:chat-20260529-003");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public async Task ExistingMatchingConstraint_ShouldNotGenerateGap()
    {
        var root = CreateTempRoot();
        try
        {
            var planningReportPath = await WritePlanningReportAsync(
                root,
                sampleId: "sample-existing",
                expected: "输出必须使用中文",
                missing: true);
            var constraintStore = new InMemoryConstraintStore();
            await constraintStore.SaveAsync(new ContextConstraint
            {
                Id = "constraint-language",
                WorkspaceId = "workspace-gap",
                CollectionId = "collection-gap",
                Level = ConstraintLevel.Hard,
                Scope = ContextScope.Collection,
                Status = ContextMemoryStatus.Stable,
                Content = "输出必须使用中文。",
                Confidence = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            var service = CreateService(constraintStore);

            var result = await service.GenerateAsync(new ConstraintGapGenerationRequest
            {
                WorkspaceId = "workspace-gap",
                CollectionId = "collection-gap",
                PlanningConstraintReportPath = planningReportPath,
                IncludeExtendedFailureTriageReport = false
            });

            Assert.AreEqual(0, result.CreatedCount);
            Assert.AreEqual(1, result.SkippedMatchedCount);
            Assert.AreEqual(0, result.Gaps.Count);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public async Task DuplicateGap_ShouldNotBeRecreated()
    {
        var root = CreateTempRoot();
        try
        {
            var planningReportPath = await WritePlanningReportAsync(
                root,
                sampleId: "sample-duplicate",
                expected: "恢复点必须保留",
                missing: true);
            var store = new InMemoryConstraintGapCandidateStore();
            var service = new ConstraintGapCandidateService(store, new InMemoryConstraintStore());
            var request = new ConstraintGapGenerationRequest
            {
                WorkspaceId = "workspace-gap",
                CollectionId = "collection-gap",
                PlanningConstraintReportPath = planningReportPath,
                IncludeExtendedFailureTriageReport = false
            };

            var first = await service.GenerateAsync(request);
            var second = await service.GenerateAsync(request);
            var gaps = await store.QueryAsync(new ConstraintGapCandidateQuery
            {
                WorkspaceId = "workspace-gap",
                CollectionId = "collection-gap",
                Limit = 10
            });

            Assert.AreEqual(1, first.CreatedCount);
            Assert.AreEqual(0, second.CreatedCount);
            Assert.AreEqual(1, second.ExistingCount);
            Assert.AreEqual(1, gaps.Count);
            Assert.AreEqual(first.Gaps[0].GapId, second.Gaps[0].GapId);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public async Task Generator_ShouldNotWriteConstraintStore()
    {
        var root = CreateTempRoot();
        try
        {
            var planningReportPath = await WritePlanningReportAsync(
                root,
                sampleId: "sample-no-write",
                expected: "不自动写入约束库",
                missing: true);
            var constraintStore = new TrackingConstraintStore();
            var service = new ConstraintGapCandidateService(
                new InMemoryConstraintGapCandidateStore(),
                constraintStore);

            await service.GenerateAsync(new ConstraintGapGenerationRequest
            {
                WorkspaceId = "workspace-gap",
                CollectionId = "collection-gap",
                PlanningConstraintReportPath = planningReportPath,
                IncludeExtendedFailureTriageReport = false
            });

            Assert.AreEqual(0, constraintStore.SaveCount);
            Assert.AreEqual(1, constraintStore.QueryCount);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

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

    private static async Task<string> WritePlanningReportAsync(
        string root,
        string sampleId,
        string expected,
        bool missing)
    {
        var path = Path.Combine(root, "planning-report.json");
        var report = new PlanningOptInConstraintSafetyReport
        {
            ReportId = "planning-report-op",
            SampleSet = "unit",
            TotalSamples = 1,
            Samples =
            [
                new PlanningOptInConstraintSafetySample
                {
                    SampleId = sampleId,
                    Mode = "ChatMode",
                    Intent = PlanningIntentDetector.CurrentTask,
                    OptInMatched = true,
                    Applied = !missing,
                    FallbackUsed = missing,
                    ExpectedHardConstraints = [expected],
                    MissingConstraints = missing ? [expected] : [],
                    ConstraintSource = "eval.expectedConstraints",
                    LostAtStage = missing ? "ConstraintNotRetrieved" : "",
                    SuggestedFix = "create corpus gap for review",
                    ConstraintRepairStatus = missing ? "ConstraintRepairFailed" : "ConstraintRepaired"
                }
            ]
        };
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, JsonOptions));
        return path;
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
