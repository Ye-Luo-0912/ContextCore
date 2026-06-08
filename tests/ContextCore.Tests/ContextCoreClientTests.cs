using System.Net;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;

namespace ContextCore.Tests;

/// <summary>覆盖 P0-4 HTTP Client 对 P0 Minimal API 路由的封装。</summary>
[TestClass]
public sealed class ContextCoreClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task GetStatusAsync_ShouldDeserializeNestedServiceStatus()
    {
        using var http = CreateHttpClient(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/status", request.RequestUri?.AbsolutePath);

            return Json(new
            {
                status = "ok",
                utc = DateTimeOffset.Parse("2026-05-11T10:59:17.2045019+00:00"),
                storage = new
                {
                    provider = "filesystem",
                        rootPath = @"D:\Users\Ye_Luo\AppData\Local\Context\context-core-data"
                },
                readiness = new
                {
                    status = "ready",
                    message = "filesystem 就绪",
                    checkedAt = DateTimeOffset.Parse("2026-05-11T10:59:17.2045019+00:00"),
                    storageProvider = "filesystem",
                    productionReady = false,
                    providerState = "ServiceReadyAlpha",
                    retrievalBaseline = "retrieval-orchestration-baseline-v1",
                    fromCache = false,
                    cacheTtlSeconds = 0,
                    probeScope = (string?)null,
                    capabilities = new[]
                    {
                        new
                        {
                            name = "filesystem",
                            state = "AlphaSupported",
                            active = true,
                            message = "filesystem alpha supported"
                        }
                    },
                    checks = new[]
                    {
                        new
                        {
                            name = "storage-root",
                            status = "ok",
                            message = "可写",
                            severity = "info",
                            hasSideEffect = false,
                            durationMs = 0.4,
                            warning = (string?)null,
                            detail = "只读"
                        }
                    },
                    warnings = Array.Empty<string>(),
                    shortTermMaintenance = new
                    {
                        enabled = false,
                        isRunning = false,
                        runOnStartup = false,
                        intervalSeconds = 300,
                        lastError = (string?)null,
                        lastRun = (object?)null
                    }
                },
                retrievalBaseline = "retrieval-orchestration-baseline-v1",
                capabilities = new[]
                {
                    new
                    {
                        name = "filesystem",
                        state = "AlphaSupported",
                        active = true,
                        message = "filesystem alpha supported"
                    }
                },
                jobs = new
                {
                    queued = 2,
                    running = 1
                },
                shortTermMaintenance = new
                {
                    enabled = false,
                    isRunning = false,
                    runOnStartup = false,
                    intervalSeconds = 300,
                    lastError = (string?)null,
                    lastRun = (object?)null
                }
            });
        });
        var client = new ContextCoreClient(http);

        var status = await client.GetStatusAsync();

        Assert.AreEqual("ok", status.Status);
        Assert.AreEqual("filesystem", status.Storage.Provider);
        Assert.AreEqual("ready", status.Readiness.Status);
        Assert.AreEqual("ServiceReadyAlpha", status.Readiness.ProviderState);
        Assert.AreEqual("retrieval-orchestration-baseline-v1", status.RetrievalBaseline);
        Assert.AreEqual("filesystem", status.Capabilities[0].Name);
        Assert.AreEqual(2, status.Jobs.Queued);
        Assert.AreEqual(1, status.Jobs.Running);
        Assert.IsNotNull(status.ShortTermMaintenance);
        Assert.IsFalse(status.ShortTermMaintenance!.Enabled);
    }

    [TestMethod]
    public async Task GetReadinessAndDeepStatusAsync_ShouldDeserializeRuntimeObservabilityResponses()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/health/ready", request.RequestUri?.AbsolutePath);

            return Json(new
            {
                status = "ready",
                message = "runtime ready",
                checkedAt = DateTimeOffset.Parse("2026-05-30T10:00:00+00:00"),
                storageProvider = "filesystem",
                productionReady = false,
                providerState = "ServiceReadyAlpha",
                retrievalBaseline = "retrieval-orchestration-baseline-v1",
                fromCache = true,
                cacheTtlSeconds = 8,
                probeScope = (string?)null,
                capabilities = new[]
                {
                    new
                    {
                        name = "filesystem",
                        state = "AlphaSupported",
                        active = true,
                        message = "filesystem ready"
                    }
                },
                checks = new[]
                {
                    new
                    {
                        name = "storage-root",
                        status = "ok",
                        message = "root writable",
                        severity = "info",
                        hasSideEffect = true,
                        durationMs = 1.5,
                        warning = (string?)null,
                        detail = "temp file"
                    }
                },
                warnings = Array.Empty<string>(),
                shortTermMaintenance = new
                {
                    enabled = false,
                    isRunning = false,
                    runOnStartup = false,
                    intervalSeconds = 300,
                    lastError = (string?)null,
                    lastRun = (object?)null
                }
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/status/deep", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?refresh=true", request.RequestUri?.Query);

            return Json(new
            {
                status = "warning",
                message = "deep warning",
                checkedAt = DateTimeOffset.Parse("2026-05-30T10:00:02+00:00"),
                storageProvider = "filesystem",
                productionReady = false,
                providerState = "ServiceReadyAlpha",
                retrievalBaseline = "retrieval-orchestration-baseline-v1",
                fromCache = false,
                cacheTtlSeconds = 8,
                probeScope = "__system__/__health__",
                capabilities = new[]
                {
                    new
                    {
                        name = "postgres",
                        state = "Experimental",
                        active = false,
                        message = "experimental"
                    }
                },
                checks = new[]
                {
                    new
                    {
                        name = "job-queue",
                        status = "warning",
                        message = "job probe warning",
                        severity = "warning",
                        hasSideEffect = true,
                        durationMs = 2.5,
                        warning = "probe uses fixed id",
                        detail = "system scope"
                    }
                },
                warnings = new[] { "probe uses fixed id" },
                shortTermMaintenance = new
                {
                    enabled = true,
                    isRunning = false,
                    runOnStartup = true,
                    intervalSeconds = 60,
                    lastError = (string?)null,
                    lastRun = new
                    {
                        runId = "run-1",
                        workspaceId = "workspace-1",
                        collectionId = "collection-1",
                        trigger = "Scheduled",
                        startedAt = DateTimeOffset.Parse("2026-05-30T10:00:01+00:00"),
                        completedAt = DateTimeOffset.Parse("2026-05-30T10:00:02+00:00"),
                        durationMs = 500.0,
                        compactedRawEvents = 1,
                        compactedWorkingItems = 1,
                        archivedRawEvents = 0,
                        archivedWorkingItems = 0,
                        removedDuplicates = 0,
                        warnings = Array.Empty<string>(),
                        errors = Array.Empty<string>()
                    }
                }
            });
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var client = new ContextCoreClient(http);

        var readiness = await client.GetReadinessAsync();
        var deep = await client.GetDeepStatusAsync(refresh: true);

        Assert.AreEqual("ready", readiness.Status);
        Assert.IsTrue(readiness.FromCache);
        Assert.AreEqual(8, readiness.CacheTtlSeconds);
        Assert.AreEqual("storage-root", readiness.Checks[0].Name);
        Assert.AreEqual("info", readiness.Checks[0].Severity);
        Assert.AreEqual("warning", deep.Status);
        Assert.AreEqual("__system__/__health__", deep.ProbeScope);
        Assert.IsFalse(deep.FromCache);
        Assert.AreEqual("probe uses fixed id", deep.Warnings[0]);
        Assert.IsNotNull(deep.ShortTermMaintenance);
        Assert.AreEqual("run-1", deep.ShortTermMaintenance!.LastRun!.RunId);
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task GetPlanningSnapshotAsync_ShouldCallExpectedRoute()
    {
        using var http = CreateHttpClient(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/context/planning/snapshot", request.RequestUri?.AbsolutePath);
            var query = request.RequestUri?.Query ?? string.Empty;
            StringAssert.Contains(query, "workspaceId=workspace-1");
            StringAssert.Contains(query, "collectionId=collection-1");
            StringAssert.Contains(query, "sessionId=session-1");
            return Json(new ContextPlanningSnapshot
            {
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1",
                SessionId = "session-1",
                ActiveTasks =
                [
                    new ShortTermWorkingItem
                    {
                        ItemId = "task-1",
                        WorkspaceId = "workspace-1",
                        CollectionId = "collection-1",
                        Kind = "ActiveTask",
                        Status = "active"
                    }
                ],
                StableConstraints =
                [
                    new ContextConstraint
                    {
                        Id = "constraint-1",
                        WorkspaceId = "workspace-1",
                        CollectionId = "collection-1",
                        Status = ContextMemoryStatus.Stable
                    }
                ],
                DecisionRecords =
                [
                    new ContextMemoryItem
                    {
                        Id = "decision-1",
                        WorkspaceId = "workspace-1",
                        CollectionId = "collection-1",
                        Layer = ContextMemoryLayer.Stable,
                        Status = ContextMemoryStatus.Stable,
                        Type = "decision"
                    }
                ],
                LearningSignalsSummary = new ContextLearningSummary
                {
                    WorkspaceId = "workspace-1",
                    CollectionId = "collection-1",
                    RecordCount = 1
                },
                PolicyVersion = "context-planning-snapshot-policy/v1",
                CreatedAt = DateTimeOffset.UtcNow
            });
        });
        var client = new ContextCoreClient(http);

        var snapshot = await client.GetPlanningSnapshotAsync("workspace-1", "collection-1", "session-1");

        Assert.AreEqual("workspace-1", snapshot.WorkspaceId);
        Assert.AreEqual("task-1", snapshot.ActiveTasks.Single().ItemId);
        Assert.AreEqual("constraint-1", snapshot.StableConstraints.Single().Id);
        Assert.AreEqual("decision-1", snapshot.DecisionRecords.Single().Id);
        Assert.AreEqual(1, snapshot.LearningSignalsSummary.RecordCount);
    }

    [TestMethod]
    public async Task ProposeRetrievalPlanAsync_ShouldCallExpectedRoute()
    {
        using var http = CreateHttpClient(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/context/planning/propose", request.RequestUri?.AbsolutePath);
            var payload = JsonSerializer.Deserialize<ContextPlanningProposalRequest>(
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(),
                JsonOptions);
            Assert.IsNotNull(payload);
            Assert.AreEqual("workspace-1", payload!.WorkspaceId);
            Assert.AreEqual("collection-1", payload.CollectionId);
            Assert.AreEqual("session-1", payload.SessionId);
            Assert.AreEqual("当前任务下一步", payload.CurrentInput);
            Assert.AreEqual("Chat", payload.Mode);

            return Json(new RetrievalPlanProposal
            {
                OperationId = "proposal-op-1",
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1",
                Intent = "CurrentTask",
                Mode = "Chat",
                UseExact = true,
                UseKeyword = true,
                UseShortTermMemory = true,
                UseWorkingMemory = true,
                UseStableMemory = true,
                UseRelations = false,
                UseVector = false,
                KeywordTopK = 18,
                MemoryTopK = 22,
                RelationTopK = 0,
                VectorTopK = 0,
                FinalTopK = 20,
                Confidence = 0.8,
                Reasons = ["matched current-task terms"],
                Warnings = ["previewOnly: proposal does not execute retrieval or mutate retrieval output"]
            });
        });
        var client = new ContextCoreClient(http);

        var proposal = await client.ProposeRetrievalPlanAsync(
            "workspace-1",
            "collection-1",
            "session-1",
            "当前任务下一步",
            "Chat");

        Assert.AreEqual("proposal-op-1", proposal.OperationId);
        Assert.AreEqual("CurrentTask", proposal.Intent);
        Assert.IsFalse(proposal.UseVector);
        Assert.AreEqual(20, proposal.FinalTopK);
    }

    [TestMethod]
    public async Task ConstraintGapMethods_ShouldCallExpectedRoutes()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/constraints/gaps/generate", request.RequestUri?.AbsolutePath);
            var payload = JsonSerializer.Deserialize<ConstraintGapGenerationRequest>(
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(),
                JsonOptions);
            Assert.IsNotNull(payload);
            Assert.AreEqual("workspace-1", payload!.WorkspaceId);
            Assert.AreEqual("collection-1", payload.CollectionId);
            Assert.AreEqual("eval/planning.json", payload.PlanningConstraintReportPath);

            return Json(new ConstraintGapGenerationResult
            {
                OperationId = "gap-generate-op-1",
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1",
                CreatedCount = 1,
                Gaps = [CreateConstraintGap("gap-1")]
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/constraints/gaps", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?workspaceId=workspace-1&limit=5&offset=1&collectionId=collection-1&sessionId=session-1&source=planning&sourceSampleId=sample-1&status=Pending&severity=High", request.RequestUri?.Query);
            return Json(new[] { CreateConstraintGap("gap-1") });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/constraints/gaps/gap-1", request.RequestUri?.AbsolutePath);
            return Json(CreateConstraintGap("gap-1"));
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/constraints/gaps/gap-1/accept", request.RequestUri?.AbsolutePath);
            var payload = JsonSerializer.Deserialize<ConstraintGapReviewRequest>(
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(),
                JsonOptions);
            Assert.IsNotNull(payload);
            Assert.AreEqual("reviewer-1", payload!.Reviewer);
            return Json(CreateConstraintGapReviewResult("gap-1", "accept", ConstraintGapStatus.Accepted, "constraint:gap:gap-1"));
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/constraints/gaps/gap-2/reject", request.RequestUri?.AbsolutePath);
            return Json(CreateConstraintGapReviewResult("gap-2", "reject", ConstraintGapStatus.Rejected, null));
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/constraints/gaps/gap-1/reviews", request.RequestUri?.AbsolutePath);
            return Json(new[]
            {
                CreateConstraintGapReview("gap-1", "accept", ConstraintGapStatus.Accepted, "constraint:gap:gap-1")
            });
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var client = new ContextCoreClient(http);

        var generated = await client.GenerateConstraintGapsAsync(new ConstraintGapGenerationRequest
        {
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            PlanningConstraintReportPath = "eval/planning.json"
        });
        var queried = await client.GetConstraintGapsAsync(
            "workspace-1",
            "collection-1",
            "session-1",
            "planning",
            "sample-1",
            ConstraintGapStatus.Pending,
            ConstraintGapSeverity.High,
            5,
            1);
        var detail = await client.GetConstraintGapAsync("gap-1");
        var reviewRequest = new ConstraintGapReviewRequest
        {
            OperationId = "gap-review-op",
            Reviewer = "reviewer-1",
            Reason = "accept"
        };
        var accepted = await client.AcceptConstraintGapAsync("gap-1", reviewRequest);
        var rejected = await client.RejectConstraintGapAsync("gap-2", reviewRequest);
        var reviews = await client.GetConstraintGapReviewsAsync("gap-1");

        Assert.AreEqual("gap-generate-op-1", generated.OperationId);
        Assert.AreEqual(1, queried.Count);
        Assert.AreEqual("gap-1", detail.GapId);
        Assert.AreEqual("constraint:gap:gap-1", accepted.CreatedConstraintId);
        Assert.AreEqual(ConstraintGapStatus.Rejected, rejected.Status);
        Assert.AreEqual(1, reviews.Count);
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task CandidateConstraintMethods_ShouldCallExpectedRoutes()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/constraints/candidates", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?workspaceId=workspace-1&limit=5&offset=1&collectionId=collection-1&status=Candidate", request.RequestUri?.Query);
            return Json(new[] { CreateCandidateConstraint("candidate-constraint-1") });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/constraints/candidates/candidate-constraint-1", request.RequestUri?.AbsolutePath);
            return Json(CreateCandidateConstraint("candidate-constraint-1"));
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/constraints/candidates/candidate-constraint-1/activate", request.RequestUri?.AbsolutePath);
            var payload = JsonSerializer.Deserialize<CandidateConstraintReviewRequest>(
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(),
                JsonOptions);
            Assert.IsNotNull(payload);
            Assert.AreEqual("reviewer-1", payload!.Reviewer);
            return Json(CreateCandidateConstraintReviewResult("candidate-constraint-1", "activate", ContextMemoryStatus.Active));
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/constraints/candidates/candidate-constraint-2/reject", request.RequestUri?.AbsolutePath);
            return Json(CreateCandidateConstraintReviewResult("candidate-constraint-2", "reject", ContextMemoryStatus.Rejected));
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/constraints/candidates/candidate-constraint-1/reviews", request.RequestUri?.AbsolutePath);
            return Json(new[]
            {
                CreateCandidateConstraintReview("candidate-constraint-1", "activate", ContextMemoryStatus.Active)
            });
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var client = new ContextCoreClient(http);
        var request = new CandidateConstraintReviewRequest
        {
            OperationId = "candidate-constraint-review-op-1",
            Reviewer = "reviewer-1",
            Reason = "manual review"
        };

        var list = await client.GetCandidateConstraintsAsync(
            "workspace-1",
            "collection-1",
            ContextMemoryStatus.Candidate,
            5,
            1);
        var detail = await client.GetCandidateConstraintAsync("candidate-constraint-1");
        var activated = await client.ActivateCandidateConstraintAsync("candidate-constraint-1", request);
        var rejected = await client.RejectCandidateConstraintAsync("candidate-constraint-2", request);
        var reviews = await client.GetCandidateConstraintReviewsAsync("candidate-constraint-1");

        Assert.AreEqual(1, list.Count);
        Assert.AreEqual("candidate-constraint-1", detail.Id);
        Assert.AreEqual(ContextMemoryStatus.Active, activated.Status);
        Assert.AreEqual("candidate-constraint-1", activated.ActivatedConstraintId);
        Assert.AreEqual(ContextMemoryStatus.Rejected, rejected.Status);
        Assert.AreEqual(1, reviews.Count);
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task CandidateMemoryGovernanceMethods_ShouldCallExpectedRoutes()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/candidates/snapshot", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?workspaceId=workspace-1&take=5&collectionId=collection-1", request.RequestUri?.Query);
            return Json(new CandidateMemorySnapshot
            {
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1",
                CandidateMemoryCount = 1,
                RecentCandidates = [CreateCandidateMemoryRecord("candidate-memory-1")]
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/candidates/candidate-memory-1", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?workspaceId=workspace-1&collectionId=collection-1", request.RequestUri?.Query);
            return Json(CreateCandidateMemoryRecord("candidate-memory-1"));
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/candidates/candidate-memory-1/explain", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?workspaceId=workspace-1&collectionId=collection-1", request.RequestUri?.Query);
            return Json(new CandidateMemoryExplanation
            {
                CandidateId = "candidate-memory-1",
                Candidate = CreateCandidateMemoryRecord("candidate-memory-1"),
                SourcePromotionCandidate = new ShortTermPromotionCandidate
                {
                    CandidateId = "stpc-1",
                    WorkspaceId = "workspace-1",
                    CollectionId = "collection-1",
                    SuggestedTargetLayer = "CandidateMemory"
                }
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/candidates/diagnostics", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?workspaceId=workspace-1&collectionId=collection-1", request.RequestUri?.Query);
            return Json(new CandidateMemoryDiagnosticsReport
            {
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1",
                DiagnosticCount = 1,
                CandidateWithoutEvidenceCount = 1
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/memory/candidates/candidate-memory-1/ready-for-stable-review", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?workspaceId=workspace-1&collectionId=collection-1", request.RequestUri?.Query);
            return Json(CreateCandidateMemoryReviewResult("candidate-memory-1", CandidateMemoryReviewActions.MarkReadyForStableReview, ContextMemoryStatus.Candidate));
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/memory/candidates/candidate-memory-1/needs-more-evidence", request.RequestUri?.AbsolutePath);
            return Json(CreateCandidateMemoryReviewResult("candidate-memory-1", CandidateMemoryReviewActions.NeedsMoreEvidence, ContextMemoryStatus.Candidate));
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/memory/candidates/candidate-memory-1/reject", request.RequestUri?.AbsolutePath);
            return Json(CreateCandidateMemoryReviewResult("candidate-memory-1", CandidateMemoryReviewActions.Reject, ContextMemoryStatus.Rejected));
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/memory/candidates/candidate-memory-1/expire", request.RequestUri?.AbsolutePath);
            return Json(CreateCandidateMemoryReviewResult("candidate-memory-1", CandidateMemoryReviewActions.Expire, ContextMemoryStatus.Deprecated));
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/memory/candidates/candidate-memory-1/supersede", request.RequestUri?.AbsolutePath);
            return Json(CreateCandidateMemoryReviewResult(
                "candidate-memory-1",
                CandidateMemoryReviewActions.Supersede,
                ContextMemoryStatus.Deprecated,
                "candidate-memory-2"));
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/candidates/candidate-memory-1/reviews", request.RequestUri?.AbsolutePath);
            return Json<IReadOnlyList<CandidateMemoryReviewRecord>>(
            [
                new CandidateMemoryReviewRecord
                {
                    ReviewId = "cmr-1",
                    CandidateId = "candidate-memory-1",
                    Action = CandidateMemoryReviewActions.Reject
                }
            ]);
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var client = new ContextCoreClient(http);

        var snapshot = await client.GetCandidateMemorySnapshotAsync("workspace-1", "collection-1", 5);
        var detail = await client.GetCandidateMemoryAsync("candidate-memory-1", "workspace-1", "collection-1");
        var explanation = await client.ExplainCandidateMemoryAsync("candidate-memory-1", "workspace-1", "collection-1");
        var diagnostics = await client.GetCandidateMemoryDiagnosticsAsync("workspace-1", "collection-1");
        var ready = await client.MarkCandidateMemoryReadyForStableReviewAsync("candidate-memory-1", CreateCandidateMemoryReviewRequest());
        var needsEvidence = await client.MarkCandidateMemoryNeedsMoreEvidenceAsync("candidate-memory-1", CreateCandidateMemoryReviewRequest());
        var rejected = await client.RejectCandidateMemoryAsync("candidate-memory-1", CreateCandidateMemoryReviewRequest());
        var expired = await client.ExpireCandidateMemoryAsync("candidate-memory-1", CreateCandidateMemoryReviewRequest());
        var superseded = await client.SupersedeCandidateMemoryAsync("candidate-memory-1", CreateCandidateMemoryReviewRequest("candidate-memory-2"));
        var reviews = await client.GetCandidateMemoryReviewsAsync("candidate-memory-1");

        Assert.AreEqual(1, snapshot.CandidateMemoryCount);
        Assert.AreEqual("candidate-memory-1", detail.Id);
        Assert.AreEqual("stpc-1", explanation.SourcePromotionCandidate!.CandidateId);
        Assert.AreEqual(1, diagnostics.CandidateWithoutEvidenceCount);
        Assert.AreEqual(CandidateMemoryReviewActions.MarkReadyForStableReview, ready.Action);
        Assert.AreEqual(CandidateMemoryReviewActions.NeedsMoreEvidence, needsEvidence.Action);
        Assert.AreEqual(ContextMemoryStatus.Rejected, rejected.ToStatus);
        Assert.AreEqual(ContextMemoryStatus.Deprecated, expired.ToStatus);
        Assert.AreEqual("candidate-memory-2", superseded.SupersedeTargetCandidateId);
        Assert.AreEqual(1, reviews.Count);
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task StableMemoryGovernanceMethods_ShouldCallExpectedRoutes()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/stable/snapshot", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?workspaceId=workspace-1&take=5&collectionId=collection-1", request.RequestUri?.Query);
            return Json(new StableMemorySnapshot
            {
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1",
                StableMemoryCount = 1,
                RecentStableItems =
                [
                    new StableMemoryRecord
                    {
                        Id = "stable-memory-1",
                        WorkspaceId = "workspace-1",
                        CollectionId = "collection-1",
                        StableKind = StableMemoryKinds.StableMemory,
                        Type = "preference"
                    }
                ]
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/stable/diagnostics", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?workspaceId=workspace-1&collectionId=collection-1", request.RequestUri?.Query);
            return Json(new StableMemoryDiagnosticsReport
            {
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1",
                DiagnosticCount = 1,
                MissingProvenanceCount = 1
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/stable/stable-memory-1/explain", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?workspaceId=workspace-1&collectionId=collection-1", request.RequestUri?.Query);
            return Json(new StableMemoryExplanation
            {
                StableItemId = "stable-memory-1",
                StableItem = new StableMemoryRecord
                {
                    Id = "stable-memory-1",
                    WorkspaceId = "workspace-1",
                    CollectionId = "collection-1",
                    StableKind = StableMemoryKinds.StableMemory,
                    Type = "preference"
                },
                EvidenceRefs = ["event-1"]
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/stable/stable-memory-1/replacement-chain", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?workspaceId=workspace-1&collectionId=collection-1", request.RequestUri?.Query);
            return Json(new StableReplacementChainResponse
            {
                ItemId = "stable-memory-1",
                CurrentItem = new StableMemoryRecord
                {
                    Id = "stable-memory-1",
                    WorkspaceId = "workspace-1",
                    CollectionId = "collection-1",
                    StableKind = StableMemoryKinds.StableMemory,
                    Type = "preference"
                },
                LatestItem = new StableMemoryRecord
                {
                    Id = "stable-memory-2",
                    WorkspaceId = "workspace-1",
                    CollectionId = "collection-1",
                    StableKind = StableMemoryKinds.StableMemory,
                    Type = "preference"
                },
                Relations =
                [
                    new ContextRelation
                    {
                        Id = "rel-1",
                        WorkspaceId = "workspace-1",
                        CollectionId = "collection-1",
                        SourceId = "stable-memory-1",
                        TargetId = "stable-memory-2",
                        RelationType = ContextRelationTypes.SupersededBy,
                        Confidence = 1.0
                    }
                ]
            });
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var client = new ContextCoreClient(http);

        var snapshot = await client.GetStableMemorySnapshotAsync("workspace-1", "collection-1", 5);
        var diagnostics = await client.GetStableMemoryDiagnosticsAsync("workspace-1", "collection-1");
        var explanation = await client.ExplainStableMemoryAsync("stable-memory-1", "workspace-1", "collection-1");
        var chain = await client.GetStableReplacementChainAsync("stable-memory-1", "workspace-1", "collection-1");

        Assert.AreEqual(1, snapshot.StableMemoryCount);
        Assert.AreEqual(1, diagnostics.MissingProvenanceCount);
        Assert.AreEqual("stable-memory-1", explanation.StableItemId);
        Assert.AreEqual("stable-memory-2", chain.LatestItem!.Id);
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task StableLifecycleReviewMethods_ShouldCallExpectedRoutes()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/memory/stable/stable-memory-1/deprecate", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?workspaceId=workspace-1&collectionId=collection-1", request.RequestUri?.Query);
            return Json(CreateStableLifecycleReviewResult(
                "stable-memory-1",
                StableLifecycleReviewActions.Deprecate,
                ContextMemoryStatus.Deprecated,
                StableMemoryLifecycle.Deprecated));
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/memory/stable/stable-memory-1/supersede", request.RequestUri?.AbsolutePath);
            return Json(CreateStableLifecycleReviewResult(
                "stable-memory-1",
                StableLifecycleReviewActions.Supersede,
                ContextMemoryStatus.Deprecated,
                StableMemoryLifecycle.Superseded,
                "stable-memory-2"));
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/memory/stable/stable-memory-1/reject", request.RequestUri?.AbsolutePath);
            return Json(CreateStableLifecycleReviewResult(
                "stable-memory-1",
                StableLifecycleReviewActions.Reject,
                ContextMemoryStatus.Rejected,
                StableMemoryLifecycle.Rejected));
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/stable/stable-memory-1/reviews", request.RequestUri?.AbsolutePath);
            return Json<IReadOnlyList<StableLifecycleReviewRecord>>(
            [
                new StableLifecycleReviewRecord
                {
                    ReviewId = "slr-1",
                    StableItemId = "stable-memory-1",
                    Action = StableLifecycleReviewActions.Deprecate,
                    ToStatus = ContextMemoryStatus.Deprecated
                }
            ]);
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var client = new ContextCoreClient(http);
        var requestDto = CreateStableLifecycleReviewRequest("stable-memory-2");

        var deprecated = await client.DeprecateStableMemoryAsync("stable-memory-1", requestDto);
        var superseded = await client.SupersedeStableMemoryAsync("stable-memory-1", requestDto);
        var rejected = await client.RejectStableMemoryAsync("stable-memory-1", requestDto);
        var reviews = await client.GetStableMemoryReviewsAsync("stable-memory-1");

        Assert.AreEqual(ContextMemoryStatus.Deprecated, deprecated.ToStatus);
        Assert.AreEqual("stable-memory-2", superseded.ReplacementItemId);
        Assert.AreEqual(ContextMemoryStatus.Rejected, rejected.ToStatus);
        Assert.AreEqual(1, reviews.Count);
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task ShortTermArchiveAndRunMethods_ShouldCallExpectedRoutes()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/short-term/archive/items", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?workspaceId=workspace-1&limit=5&collectionId=collection-1&sessionId=session-1&kind=KnownIssue", request.RequestUri?.Query);
            return Json(new ShortTermArchiveItemsResponse
            {
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1",
                SessionId = "session-1",
                Kind = "KnownIssue",
                RawEvents =
                [
                    new ShortTermRawEvent
                    {
                        EventId = "raw-1",
                        WorkspaceId = "workspace-1",
                        CollectionId = "collection-1",
                        EventKind = "ingest_succeeded",
                        Source = "chat"
                    }
                ],
                WorkingItems =
                [
                    new ShortTermWorkingItem
                    {
                        ItemId = "issue-1",
                        WorkspaceId = "workspace-1",
                        CollectionId = "collection-1",
                        Kind = "KnownIssue",
                        Status = "open",
                        Summary = "issue"
                    }
                ]
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/short-term/compact/runs", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?take=5&workspaceId=workspace-1&collectionId=collection-1&sessionId=session-1&trigger=Manual", request.RequestUri?.Query);
            return Json(new[]
            {
                new ShortTermCompactionRun
                {
                    RunId = "run-1",
                    WorkspaceId = "workspace-1",
                    CollectionId = "collection-1",
                    SessionId = "session-1",
                    Trigger = "Manual",
                    StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                    CompletedAt = DateTimeOffset.UtcNow,
                    DurationMs = 50
                }
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/short-term/compact/runs/run-1", request.RequestUri?.AbsolutePath);
            return Json(new ShortTermCompactionRun
            {
                RunId = "run-1",
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1",
                SessionId = "session-1",
                Trigger = "Manual",
                StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                CompletedAt = DateTimeOffset.UtcNow,
                DurationMs = 50
            });
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var client = new ContextCoreClient(http);

        var archive = await client.GetShortTermArchiveItemsAsync("workspace-1", "collection-1", "session-1", "KnownIssue", 5);
        var runs = await client.GetShortTermCompactionRunsAsync("workspace-1", "collection-1", "session-1", "Manual", 5);
        var run = await client.GetShortTermCompactionRunAsync("run-1");

        Assert.AreEqual(1, archive.RawEvents.Count);
        Assert.AreEqual(1, archive.WorkingItems.Count);
        Assert.AreEqual(1, runs.Count);
        Assert.AreEqual("run-1", run.RunId);
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task ShortTermPromotionCandidateQueryAndExplain_ShouldCallExpectedRoutes()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/short-term/promotion/candidates", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?workspaceId=workspace-1&limit=5&offset=1&collectionId=collection-1&sessionId=session-1&status=Candidate&kind=KnownIssue&suggestedTargetLayer=CandidateMemory&minConfidence=0.8&minImportance=0.7", request.RequestUri?.Query);
            return Json(new[]
            {
                new ShortTermPromotionCandidate
                {
                    CandidateId = "candidate-1",
                    WorkspaceId = "workspace-1",
                    CollectionId = "collection-1",
                    SessionId = "session-1",
                    SourceWorkingItemId = "issue-1",
                    Kind = "KnownIssue",
                    Title = "缓存击穿",
                    Summary = "缓存击穿",
                    SuggestedTargetLayer = "CandidateMemory",
                    Reason = "KnownIssue 需要跨轮次保留。",
                    Confidence = 0.8,
                    Importance = 0.9,
                    EvidenceRefs = ["event-1"],
                    Tags = ["KnownIssue"],
                    Status = PromotionCandidateStatus.Candidate,
                    DedupeKey = "dedupe-1",
                    SourceFingerprint = "fingerprint-1",
                    GeneratedBy = "rule-based",
                    PolicyVersion = "v1",
                    RuleName = "known-issue-to-candidate-memory",
                    RuleVersion = "v1"
                }
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/short-term/promotion/candidates/candidate-1/explain", request.RequestUri?.AbsolutePath);
            return Json(new ShortTermPromotionCandidateExplanation
            {
                CandidateId = "candidate-1",
                Candidate = new ShortTermPromotionCandidate
                {
                    CandidateId = "candidate-1",
                    WorkspaceId = "workspace-1",
                    CollectionId = "collection-1",
                    SourceWorkingItemId = "issue-1",
                    Kind = "KnownIssue",
                    Title = "缓存击穿",
                    Summary = "缓存击穿",
                    SuggestedTargetLayer = "CandidateMemory",
                    Reason = "KnownIssue 需要跨轮次保留。",
                    Confidence = 0.8,
                    Importance = 0.9,
                    EvidenceRefs = ["event-1"],
                    Tags = ["KnownIssue"],
                    Status = PromotionCandidateStatus.Candidate,
                    DedupeKey = "dedupe-1",
                    SourceFingerprint = "fingerprint-1",
                    GeneratedBy = "rule-based",
                    PolicyVersion = "v1",
                    RuleName = "known-issue-to-candidate-memory",
                    RuleVersion = "v1"
                },
                SourceWorkingItem = new ShortTermWorkingItem
                {
                    ItemId = "issue-1",
                    WorkspaceId = "workspace-1",
                    CollectionId = "collection-1",
                    Kind = "KnownIssue",
                    Status = "open",
                    Summary = "缓存击穿"
                },
                SourceRawEvents =
                [
                    new ShortTermRawEvent
                    {
                        EventId = "event-1",
                        WorkspaceId = "workspace-1",
                        CollectionId = "collection-1",
                        EventKind = "ingest_succeeded",
                        Source = "chat"
                    }
                ],
                EvidenceRefs = ["event-1"],
                Reason = "KnownIssue 需要跨轮次保留。",
                RuleName = "known-issue-to-candidate-memory",
                RuleVersion = "v1",
                PolicyVersion = "v1",
                Confidence = 0.8,
                Importance = 0.9,
                SuggestedTargetLayer = "CandidateMemory",
                DedupeKey = "dedupe-1",
                SourceFingerprint = "fingerprint-1",
                GeneratedBy = "rule-based"
            });
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var client = new ContextCoreClient(http);

        var candidates = await client.QueryShortTermPromotionCandidatesAsync(
            "workspace-1",
            "collection-1",
            "session-1",
            PromotionCandidateStatus.Candidate,
            "KnownIssue",
            "CandidateMemory",
            0.8,
            0.7,
            5,
            1);
        var explanation = await client.ExplainShortTermPromotionCandidateAsync("candidate-1");

        Assert.AreEqual(1, candidates.Count);
        Assert.AreEqual("candidate-1", explanation.CandidateId);
        Assert.AreEqual("issue-1", explanation.SourceWorkingItem.ItemId);
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task ExplainShortTermPromotionCandidateAsync_ShouldThrowContextCoreApiException_WhenNotFound()
    {
        using var http = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(JsonSerializer.Serialize(new ContextCoreErrorResponse
            {
                OperationId = "promotion-explain-missing",
                ErrorCode = ContextCoreErrorCodes.NotFound,
                Message = "未找到短期晋升候选项或其来源。",
                Target = "memory.short-term.promotion.candidate.explain",
                TraceId = "trace-promotion-explain-missing",
                Details =
                [
                    new ContextCoreErrorDetail
                    {
                        Code = "short_term_promotion_candidate_explain_not_found",
                        Target = "memory.short-term.promotion.candidate.explain",
                        Message = "未找到短期晋升候选项或其来源。"
                    }
                ]
            }, JsonOptions), Encoding.UTF8, "application/json")
        });

        var client = new ContextCoreClient(http);

        var exception = await Assert.ThrowsExceptionAsync<ContextCoreApiException>(() =>
            client.ExplainShortTermPromotionCandidateAsync("missing"));

        Assert.AreEqual(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.AreEqual(ContextCoreErrorCodes.NotFound, exception.ErrorResponse.ErrorCode);
        Assert.AreEqual("memory.short-term.promotion.candidate.explain", exception.ErrorResponse.Target);
    }

    [TestMethod]
    public async Task ShortTermPromotionCandidateReviewMethods_ShouldCallExpectedRoutes()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/memory/short-term/promotion/candidates/candidate-1/accept", request.RequestUri?.AbsolutePath);
            return Json(CreateReviewResponse("accept", PromotionCandidateStatus.Accepted, "memory-1"));
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/memory/short-term/promotion/candidates/candidate-2/reject", request.RequestUri?.AbsolutePath);
            return Json(CreateReviewResponse("reject", PromotionCandidateStatus.Rejected, null));
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/memory/short-term/promotion/candidates/candidate-3/expire", request.RequestUri?.AbsolutePath);
            return Json(CreateReviewResponse("expire", PromotionCandidateStatus.Expired, null));
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/short-term/promotion/candidates/candidate-1/reviews", request.RequestUri?.AbsolutePath);
            return Json(new[]
            {
                new PromotionCandidateReviewRecord
                {
                    ReviewId = "review-1",
                    CandidateId = "candidate-1",
                    WorkspaceId = "workspace-1",
                    CollectionId = "collection-1",
                    Action = "accept",
                    FromStatus = PromotionCandidateStatus.Candidate,
                    ToStatus = PromotionCandidateStatus.Accepted,
                    Reviewer = "tester",
                    Reason = "accepted",
                    TargetItemId = "memory-1",
                    TargetItemKind = "memory",
                    TargetLayer = "CandidateMemory",
                    EvidenceRefs = ["event-1"],
                    CreatedAt = DateTimeOffset.UtcNow
                }
            });
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var client = new ContextCoreClient(http);
        var request = new ReviewPromotionCandidateRequest
        {
            OperationId = "review-op-1",
            Reviewer = "tester",
            Reason = "manual review"
        };

        var accepted = await client.AcceptShortTermPromotionCandidateAsync("candidate-1", request);
        var rejected = await client.RejectShortTermPromotionCandidateAsync("candidate-2", request);
        var expired = await client.ExpireShortTermPromotionCandidateAsync("candidate-3", request);
        var reviews = await client.GetShortTermPromotionCandidateReviewsAsync("candidate-1");

        Assert.AreEqual(PromotionCandidateStatus.Accepted, accepted.Status);
        Assert.AreEqual(PromotionCandidateStatus.Rejected, rejected.Status);
        Assert.AreEqual(PromotionCandidateStatus.Expired, expired.Status);
        Assert.AreEqual(1, reviews.Count);
        Assert.AreEqual("memory-1", reviews[0].TargetItemId);
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task ShortTermPromotionCandidateReviewMethods_ShouldSupportPhase6DtoNames()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/memory/short-term/promotion/candidates/candidate-1/accept", request.RequestUri?.AbsolutePath);
            return Json(new PromotionCandidateReviewResult
            {
                OperationId = "review-op-2",
                CandidateId = "candidate-1",
                Action = "accept",
                Status = PromotionCandidateStatus.Accepted,
                ReviewId = "review-2",
                Reviewer = "tester",
                Reason = "accepted",
                ReviewedAt = DateTimeOffset.UtcNow,
                TargetItemId = "memory-2",
                CreatedTargetItemId = "memory-2",
                TargetItemKind = "memory",
                TargetLayer = "CandidateMemory"
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/memory/short-term/promotion/candidates/candidate-2/reject", request.RequestUri?.AbsolutePath);
            return Json(new PromotionCandidateReviewResult
            {
                OperationId = "review-op-2",
                CandidateId = "candidate-2",
                Action = "reject",
                Status = PromotionCandidateStatus.Rejected,
                ReviewId = "review-3",
                Reviewer = "tester",
                Reason = "rejected",
                ReviewedAt = DateTimeOffset.UtcNow
            });
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var client = new ContextCoreClient(http);
        var requestDto = new PromotionCandidateReviewRequest
        {
            OperationId = "review-op-2",
            Reviewer = "tester",
            Reason = "manual review"
        };

        var accepted = await client.AcceptShortTermPromotionCandidateAsync("candidate-1", requestDto);
        var rejected = await client.RejectShortTermPromotionCandidateAsync("candidate-2", requestDto);

        Assert.AreEqual("memory-2", accepted.CreatedTargetItemId);
        Assert.AreEqual(PromotionCandidateStatus.Rejected, rejected.Status);
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task AcceptShortTermPromotionCandidateAsync_ShouldThrowContextCoreApiException_WhenNotFound()
    {
        using var http = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(JsonSerializer.Serialize(new ContextCoreErrorResponse
            {
                OperationId = "promotion-accept-missing",
                ErrorCode = ContextCoreErrorCodes.NotFound,
                Message = "未找到短期晋升候选项。",
                Target = "memory.short-term.promotion.candidate.accept",
                TraceId = "trace-promotion-accept-missing"
            }, JsonOptions), Encoding.UTF8, "application/json")
        });

        var client = new ContextCoreClient(http);

        var exception = await Assert.ThrowsExceptionAsync<ContextCoreApiException>(() =>
            client.AcceptShortTermPromotionCandidateAsync("missing", new ReviewPromotionCandidateRequest
            {
                OperationId = "promotion-accept-missing",
                Reviewer = "tester",
                Reason = "missing"
            }));

        Assert.AreEqual(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.AreEqual(ContextCoreErrorCodes.NotFound, exception.ErrorResponse.ErrorCode);
        Assert.AreEqual("memory.short-term.promotion.candidate.accept", exception.ErrorResponse.Target);
    }

    [TestMethod]
    public async Task StableReviewCandidateMethods_ShouldCallExpectedRoutes()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/memory/stable-review/candidates/generate", request.RequestUri?.AbsolutePath);
            return Json(new[] { CreateStableReviewCandidate("src-1") });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/stable-review/candidates", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?workspaceId=workspace-1&limit=5&offset=1&collectionId=collection-1&sessionId=session-1&status=Candidate&validationStatus=ReadyForReview&kind=RecentDecision&suggestedStableTarget=StableMemory", request.RequestUri?.Query);
            return Json(new[] { CreateStableReviewCandidate("src-1") });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/stable-review/candidates/src-1", request.RequestUri?.AbsolutePath);
            return Json(CreateStableReviewCandidate("src-1"));
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/stable-review/candidates/src-1/explain", request.RequestUri?.AbsolutePath);
            return Json(new StableReviewCandidateExplanation
            {
                StableReviewCandidateId = "src-1",
                Candidate = CreateStableReviewCandidate("src-1"),
                SourceCandidate = new ShortTermPromotionCandidate
                {
                    CandidateId = "candidate-1",
                    WorkspaceId = "workspace-1",
                    CollectionId = "collection-1",
                    SourceWorkingItemId = "working-1",
                    Kind = "RecentDecision",
                    SuggestedTargetLayer = "CandidateMemory",
                    Status = PromotionCandidateStatus.Accepted,
                    EvidenceRefs = ["event-1"]
                },
                SourceLearningCase = CreateLearningCase("case-1", "PositivePromotionSample", ContextFeedbackSignal.Positive, ContextFailureType.None),
                EvidenceRefs = ["event-1"],
                ValidationStatus = StableReviewValidationStatuses.Ready
            });
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var client = new ContextCoreClient(http);

        var generated = await client.GenerateStableReviewCandidatesAsync(new StableReviewCandidateGenerationRequest
        {
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            SessionId = "session-1"
        });
        var queried = await client.GetStableReviewCandidatesAsync(
            "workspace-1",
            "collection-1",
            "session-1",
            StableReviewCandidateStatuses.Candidate,
            StableReviewValidationStatuses.Ready,
            "RecentDecision",
            "StableMemory",
            5,
            1);
        var detail = await client.GetStableReviewCandidateAsync("src-1");
        var explanation = await client.ExplainStableReviewCandidateAsync("src-1");

        Assert.AreEqual(1, generated.Count);
        Assert.AreEqual(1, queried.Count);
        Assert.AreEqual("src-1", detail.StableReviewCandidateId);
        Assert.AreEqual("candidate-1", explanation.SourceCandidate.CandidateId);
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task StableReviewDecisionMethods_ShouldCallExpectedRoutes()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/memory/stable-review/candidates/src-1/accept", request.RequestUri?.AbsolutePath);
            return Json(CreateStableReviewDecisionResult("src-1", "accept", StableReviewCandidateStatuses.Accepted, "stable:mem:src-1"));
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/memory/stable-review/candidates/src-2/reject", request.RequestUri?.AbsolutePath);
            return Json(CreateStableReviewDecisionResult("src-2", "reject", StableReviewCandidateStatuses.Rejected, null));
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/stable-review/candidates/src-1/reviews", request.RequestUri?.AbsolutePath);
            return Json(new[]
            {
                new StableReviewRecord
                {
                    ReviewId = "stable-review-1",
                    StableReviewCandidateId = "src-1",
                    WorkspaceId = "workspace-1",
                    CollectionId = "collection-1",
                    Action = "accept",
                    FromStatus = StableReviewCandidateStatuses.Candidate,
                    ToStatus = StableReviewCandidateStatuses.Accepted,
                    Reviewer = "tester",
                    Reason = "accepted",
                    StableTargetItemId = "stable:mem:src-1",
                    StableTargetItemKind = "memory",
                    TargetLayer = "StableMemory",
                    SourcePromotionCandidateId = "candidate-1",
                    SourceTargetItemId = "memory-1",
                    EvidenceRefs = ["event-1"],
                    ValidationStatus = StableReviewValidationStatuses.ReadyForReview,
                    CreatedAt = DateTimeOffset.UtcNow,
                    ReviewedAt = DateTimeOffset.UtcNow
                }
            });
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var client = new ContextCoreClient(http);
        var requestDto = new StableReviewDecisionRequest
        {
            OperationId = "stable-review-op-1",
            Reviewer = "tester",
            Reason = "accepted"
        };

        var accepted = await client.AcceptStableReviewCandidateAsync("src-1", requestDto);
        var rejected = await client.RejectStableReviewCandidateAsync("src-2", requestDto);
        var reviews = await client.GetStableReviewCandidateReviewsAsync("src-1");

        Assert.AreEqual(StableReviewCandidateStatuses.Accepted, accepted.Status);
        Assert.AreEqual("stable:mem:src-1", accepted.CreatedStableTargetItemId);
        Assert.AreEqual(StableReviewCandidateStatuses.Rejected, rejected.Status);
        Assert.AreEqual(1, reviews.Count);
        Assert.AreEqual("stable:mem:src-1", reviews[0].StableTargetItemId);
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task GetProvenanceAsync_ShouldCallExpectedRoute()
    {
        using var http = CreateHttpClient(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/provenance/stable%3Amem%3Asrc-1", request.RequestUri?.AbsolutePath);
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "workspaceId=workspace-1");
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "collectionId=collection-1");
            return Json(new ContextProvenanceResponse
            {
                ItemId = "stable:mem:src-1",
                TargetItemKind = "memory",
                TargetMemoryItem = new ContextMemoryItem
                {
                    Id = "stable:mem:src-1",
                    WorkspaceId = "workspace-1",
                    CollectionId = "collection-1",
                    Layer = ContextMemoryLayer.Stable,
                    Status = ContextMemoryStatus.Stable,
                    Type = "note",
                    SourceRefs = ["src-1", "candidate-1"]
                },
                StableReviewCandidate = CreateStableReviewCandidate("src-1"),
                EvidenceRefs = ["event-1"],
                Diagnostics =
                [
                    new StableDiagnosticWarning
                    {
                        Code = "DuplicateStable",
                        Message = "duplicate stable warning"
                    }
                ]
            });
        });
        var client = new ContextCoreClient(http);

        var provenance = await client.GetProvenanceAsync("stable:mem:src-1", "workspace-1", "collection-1");

        Assert.AreEqual("stable:mem:src-1", provenance.ItemId);
        Assert.AreEqual("src-1", provenance.StableReviewCandidate!.StableReviewCandidateId);
        CollectionAssert.Contains(provenance.Diagnostics.Select(item => item.Code).ToArray(), "DuplicateStable");
    }

    [TestMethod]
    public async Task GetStableReviewCandidateAsync_ShouldThrowContextCoreApiException_WhenNotFound()
    {
        using var http = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(JsonSerializer.Serialize(new ContextCoreErrorResponse
            {
                OperationId = "stable-review-missing",
                ErrorCode = ContextCoreErrorCodes.NotFound,
                Message = "未找到 Stable Review 候选项。",
                Target = "memory.stable-review.candidate",
                TraceId = "trace-stable-review-missing"
            }, JsonOptions), Encoding.UTF8, "application/json")
        });

        var client = new ContextCoreClient(http);

        var exception = await Assert.ThrowsExceptionAsync<ContextCoreApiException>(() =>
            client.GetStableReviewCandidateAsync("missing"));

        Assert.AreEqual(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.AreEqual(ContextCoreErrorCodes.NotFound, exception.ErrorResponse.ErrorCode);
        Assert.AreEqual("memory.stable-review.candidate", exception.ErrorResponse.Target);
    }

    [TestMethod]
    public async Task GetRuntimeSnapshotAsync_ShouldSupportOptionalDeep()
    {
        var withoutDeepHandlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        withoutDeepHandlers.Enqueue(_ => Json(new
        {
            status = "ok",
            utc = DateTimeOffset.Parse("2026-05-30T10:00:00+00:00"),
            storage = new { provider = "filesystem", rootPath = @"D:\context-core-data" },
            jobs = new { queued = 1, running = 0 },
            retrievalBaseline = "retrieval-orchestration-baseline-v1",
            capabilities = Array.Empty<object>(),
            readiness = new
            {
                status = "ready",
                message = "ready",
                checkedAt = DateTimeOffset.Parse("2026-05-30T10:00:00+00:00"),
                storageProvider = "filesystem",
                productionReady = false,
                providerState = "ServiceReadyAlpha",
                retrievalBaseline = "retrieval-orchestration-baseline-v1",
                fromCache = false,
                cacheTtlSeconds = 0,
                probeScope = (string?)null,
                capabilities = Array.Empty<object>(),
                checks = Array.Empty<object>(),
                warnings = Array.Empty<string>()
            }
        }));
        withoutDeepHandlers.Enqueue(_ => Json(new
        {
            status = "ready",
            message = "ready",
            checkedAt = DateTimeOffset.Parse("2026-05-30T10:00:01+00:00"),
            storageProvider = "filesystem",
            productionReady = false,
            providerState = "ServiceReadyAlpha",
            retrievalBaseline = "retrieval-orchestration-baseline-v1",
            fromCache = true,
            cacheTtlSeconds = 8,
            probeScope = (string?)null,
            capabilities = Array.Empty<object>(),
            checks = Array.Empty<object>(),
            warnings = Array.Empty<string>()
        }));

        using (var http = CreateHttpClient(request => withoutDeepHandlers.Dequeue().Invoke(request)))
        {
            var client = new ContextCoreClient(http);
            var snapshot = await client.GetRuntimeSnapshotAsync();

            Assert.IsNotNull(snapshot);
            Assert.IsNull(snapshot.DeepStatus);
            Assert.AreEqual("ok", snapshot.Status.Status);
            Assert.AreEqual("ready", snapshot.Readiness.Status);
            Assert.AreEqual(0, withoutDeepHandlers.Count);
        }

        var withDeepHandlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        withDeepHandlers.Enqueue(_ => Json(new
        {
            status = "ok",
            utc = DateTimeOffset.Parse("2026-05-30T10:00:00+00:00"),
            storage = new { provider = "filesystem", rootPath = @"D:\context-core-data" },
            jobs = new { queued = 1, running = 0 },
            retrievalBaseline = "retrieval-orchestration-baseline-v1",
            capabilities = Array.Empty<object>(),
            readiness = new
            {
                status = "ready",
                message = "ready",
                checkedAt = DateTimeOffset.Parse("2026-05-30T10:00:00+00:00"),
                storageProvider = "filesystem",
                productionReady = false,
                providerState = "ServiceReadyAlpha",
                retrievalBaseline = "retrieval-orchestration-baseline-v1",
                fromCache = false,
                cacheTtlSeconds = 0,
                probeScope = (string?)null,
                capabilities = Array.Empty<object>(),
                checks = Array.Empty<object>(),
                warnings = Array.Empty<string>()
            }
        }));
        withDeepHandlers.Enqueue(_ => Json(new
        {
            status = "ready",
            message = "ready",
            checkedAt = DateTimeOffset.Parse("2026-05-30T10:00:01+00:00"),
            storageProvider = "filesystem",
            productionReady = false,
            providerState = "ServiceReadyAlpha",
            retrievalBaseline = "retrieval-orchestration-baseline-v1",
            fromCache = false,
            cacheTtlSeconds = 8,
            probeScope = (string?)null,
            capabilities = Array.Empty<object>(),
            checks = Array.Empty<object>(),
            warnings = Array.Empty<string>()
        }));
        withDeepHandlers.Enqueue(request =>
        {
            Assert.AreEqual("/api/status/deep", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?refresh=true", request.RequestUri?.Query);
            return Json(new
            {
                status = "warning",
                message = "deep",
                checkedAt = DateTimeOffset.Parse("2026-05-30T10:00:02+00:00"),
                storageProvider = "filesystem",
                productionReady = false,
                providerState = "ServiceReadyAlpha",
                retrievalBaseline = "retrieval-orchestration-baseline-v1",
                fromCache = false,
                cacheTtlSeconds = 8,
                probeScope = "__system__/__health__",
                capabilities = Array.Empty<object>(),
                checks = Array.Empty<object>(),
                warnings = new[] { "deep warning" }
            });
        });

        using (var http = CreateHttpClient(request => withDeepHandlers.Dequeue().Invoke(request)))
        {
            var client = new ContextCoreClient(http);
            var snapshot = await client.GetRuntimeSnapshotAsync(includeDeep: true, refreshDeep: true);

            Assert.IsNotNull(snapshot.DeepStatus);
            Assert.AreEqual("__system__/__health__", snapshot.DeepStatus!.ProbeScope);
            Assert.AreEqual("warning", snapshot.DeepStatus.Status);
            Assert.AreEqual(0, withDeepHandlers.Count);
        }
    }

    [TestMethod]
    public async Task QueryRelationsAsync_ShouldDeserializeExplicitRelationResponse()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/relations/item-1", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?workspaceId=workspace-1&collectionId=collection-1", request.RequestUri?.Query);

            return Json(new
            {
                itemId = "item-1",
                outgoing = new[]
                {
                    new ContextRelation
                    {
                        Id = "rel-1",
                        WorkspaceId = "workspace-1",
                        CollectionId = "collection-1",
                        SourceId = "item-1",
                        TargetId = "item-2",
                        RelationType = "references"
                    }
                },
                incoming = Array.Empty<ContextRelation>()
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/relations/types", request.RequestUri?.AbsolutePath);
            return Json<IReadOnlyList<RelationTypeDefinition>>(
            [
                new RelationTypeDefinition
                {
                    Type = "references",
                    DefaultWeight = 0.5
                }
            ]);
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/relations/diagnostics", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?workspaceId=workspace-1&collectionId=collection-1", request.RequestUri?.Query);
            return Json(new RelationGraphDiagnosticsReport
            {
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1",
                RelationCount = 1
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/relations/diagnostics/item-1", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?workspaceId=workspace-1&collectionId=collection-1", request.RequestUri?.Query);
            return Json(new RelationGraphDiagnosticsReport
            {
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1",
                ItemId = "item-1",
                DiagnosticCount = 1
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/relations/rel-1/explain", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?workspaceId=workspace-1&collectionId=collection-1", request.RequestUri?.Query);
            return Json(new RelationExplainResponse
            {
                RelationId = "rel-1",
                Relation = new ContextRelation
                {
                    Id = "rel-1",
                    WorkspaceId = "workspace-1",
                    CollectionId = "collection-1",
                    SourceId = "item-1",
                    TargetId = "item-2",
                    RelationType = "references",
                    Confidence = 0.9
                },
                Confidence = 0.9,
                Lifecycle = StableMemoryLifecycle.Active,
                ReviewStatus = "Reviewed",
                EvidenceRefs = ["event-1"]
            });
        });
        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var client = new ContextCoreClient(http);

        var response = await client.QueryRelationsAsync("item-1", "workspace-1", "collection-1");
        var types = await client.GetRelationTypesAsync();
        var diagnostics = await client.GetRelationDiagnosticsAsync("workspace-1", "collection-1");
        var itemDiagnostics = await client.GetItemRelationDiagnosticsAsync("item-1", "workspace-1", "collection-1");
        var explain = await client.ExplainRelationAsync("rel-1", "workspace-1", "collection-1");

        Assert.AreEqual("item-1", response.ItemId);
        Assert.AreEqual(1, response.Outgoing.Count);
        Assert.AreEqual("references", response.Outgoing[0].RelationType);
        Assert.AreEqual(0, response.Incoming.Count);
        Assert.AreEqual("references", types[0].Type);
        Assert.AreEqual(1, diagnostics.RelationCount);
        Assert.AreEqual(1, itemDiagnostics.DiagnosticCount);
        Assert.AreEqual("rel-1", explain.RelationId);
        Assert.AreEqual(0.9, explain.Confidence);
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task IngestMethods_ShouldSupportCommandAndLegacyResponses()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/context/ingest", request.RequestUri?.AbsolutePath);

            return Json(new ContextInputIngestionResult
            {
                Created = true,
                Deduped = false,
                ContentHash = "hash-1",
                SequenceId = 1,
                OperationId = "command-op-1",
                Item = new ContextItem
                {
                    Id = "cmd-item",
                    WorkspaceId = "workspace-1",
                    CollectionId = "collection-1",
                    Type = "note",
                    Content = "command"
                }
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/context/ingest", request.RequestUri?.AbsolutePath);

            return Json(new ContextInputIngestionResult
            {
                Created = false,
                Deduped = true,
                ContentHash = "hash-1",
                SequenceId = 1,
                OperationId = "legacy-op-1",
                Item = new ContextItem
                {
                    Id = "legacy-item",
                    WorkspaceId = "workspace-1",
                    CollectionId = "collection-1",
                    Type = "note",
                    Content = "legacy"
                }
            });
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var client = new ContextCoreClient(http);

        var commandResult = await client.IngestAsync(new ContextInputCommand
        {
            OperationId = "command-op-1",
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            Source = "chat",
            InputKind = "note",
            Content = "command"
        });
        var legacyItem = await client.IngestAsync(new ContextItem
        {
            Id = "legacy-item",
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            Type = "note",
            Content = "legacy"
        });

        Assert.IsTrue(commandResult.Created);
        Assert.AreEqual("cmd-item", commandResult.Item.Id);
        Assert.AreEqual("legacy-item", legacyItem.Id);
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task Client_ShouldParseStructuredErrorResponse()
    {
        using var http = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(JsonSerializer.Serialize(new ContextCoreErrorResponse
            {
                OperationId = "op-err-1",
                ErrorCode = ContextCoreErrorCodes.ValidationFailed,
                Message = "Input validation failed.",
                Target = "context.ingest",
                TraceId = "trace-1",
                Details =
                [
                    new ContextCoreErrorDetail
                    {
                        Code = "ContentRequired",
                        Field = "Content",
                        Target = "context.ingest",
                        Message = "Content is required unless ContentFormat is BinaryRef."
                    }
                ]
            }, JsonOptions), Encoding.UTF8, "application/json")
        });
        var client = new ContextCoreClient(http);

        var exception = await Assert.ThrowsExceptionAsync<ContextCoreApiException>(() =>
            client.IngestAsync(new ContextInputCommand
            {
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1",
                Source = "chat",
                InputKind = "note",
                Content = " "
            }));

        Assert.AreEqual(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.AreEqual(ContextCoreErrorCodes.ValidationFailed, exception.ErrorResponse.ErrorCode);
        Assert.AreEqual("context.ingest", exception.ErrorResponse.Target);
        Assert.AreEqual("ContentRequired", exception.ErrorResponse.Details[0].Code);
    }

    [TestMethod]
    public async Task ClientMethods_ShouldCallPhase0Routes()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/context/item%201", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?workspaceId=workspace%201&collectionId=collection%2F1", request.RequestUri?.Query);

            return Json(new ContextItem
            {
                Id = "item 1",
                WorkspaceId = "workspace 1",
                CollectionId = "collection/1"
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/package/preview", request.RequestUri?.AbsolutePath);

            return Json(new ContextPackage
            {
                PackageId = "package-1",
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1"
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/jobs", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?workspaceId=workspace-1&collectionId=collection-1&state=Queued&take=10", request.RequestUri?.Query);

            return Json(new[]
            {
                new ContextJob
                {
                    JobId = "job-1",
                    WorkspaceId = "workspace-1",
                    CollectionId = "collection-1",
                    Kind = ContextJobKind.Compression,
                    State = ContextJobState.Queued
                }
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/jobs/job-1", request.RequestUri?.AbsolutePath);

            return Json(new ContextJob
            {
                JobId = "job-1",
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1",
                Kind = ContextJobKind.Compression,
                State = ContextJobState.Queued
            });
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var client = new ContextCoreClient(http);

        var item = await client.GetContextAsync("item 1", "workspace 1", "collection/1");
        var package = await client.PreviewPackageAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1"
        });
        var jobs = await client.QueryJobsAsync(new ContextJobQuery
        {
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            State = ContextJobState.Queued,
            Take = 10
        });
        var job = await client.GetJobAsync("job-1");

        Assert.AreEqual("item 1", item.Id);
        Assert.AreEqual("package-1", package.PackageId);
        Assert.AreEqual(1, jobs.Count);
        Assert.AreEqual("job-1", job.JobId);
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task WorkingMemoryMethods_ShouldCallWorkingMemoryRoutes()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/memory/working/add", request.RequestUri?.AbsolutePath);

            return Json(new WorkingMemoryItem
            {
                Id = "work-1",
                WorkspaceId = "workspace 1",
                CollectionId = "collection/1",
                Type = "note",
                Content = "Remember this for the current task."
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/working/recent", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?workspaceId=workspace%201&collectionId=collection%2F1&take=5", request.RequestUri?.Query);

            return Json(new[]
            {
                new WorkingMemoryItem
                {
                    Id = "work-1",
                    WorkspaceId = "workspace 1",
                    CollectionId = "collection/1",
                    Type = "note",
                    Content = "Remember this for the current task."
                }
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/memory/working/active-context", request.RequestUri?.AbsolutePath);

            return Json(new WorkingMemoryActiveContext
            {
                WorkspaceId = "workspace 1",
                CollectionId = "collection/1",
                CurrentTaskId = "task-1",
                Summary = "Active context."
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/working/active-context", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?workspaceId=workspace%201&collectionId=collection%2F1", request.RequestUri?.Query);

            return Json(new WorkingMemoryActiveContext
            {
                WorkspaceId = "workspace 1",
                CollectionId = "collection/1",
                CurrentTaskId = "task-1",
                Summary = "Active context."
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/memory/working/current-task", request.RequestUri?.AbsolutePath);

            return Json(new WorkingMemoryCurrentTask
            {
                TaskId = "task-1",
                WorkspaceId = "workspace 1",
                CollectionId = "collection/1",
                Title = "Current task",
                Status = "active"
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/working/current-task", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?workspaceId=workspace%201&collectionId=collection%2F1", request.RequestUri?.Query);

            return Json(new WorkingMemoryCurrentTask
            {
                TaskId = "task-1",
                WorkspaceId = "workspace 1",
                CollectionId = "collection/1",
                Title = "Current task",
                Status = "active"
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/memory/working/clear", request.RequestUri?.AbsolutePath);

            return NoContent();
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/working/active-context", request.RequestUri?.AbsolutePath);

            return Json(new ContextCoreErrorResponse
            {
                OperationId = "missing-active-1",
                ErrorCode = ContextCoreErrorCodes.NotFound,
                Message = "未找到活跃上下文。",
                Target = "memory.working.active-context",
                TraceId = "trace-missing-active",
                Details =
                [
                    new ContextCoreErrorDetail
                    {
                        Code = "active_context_not_found",
                        Target = "memory.working.active-context",
                        Message = "未找到活跃上下文。"
                    }
                ]
            }, HttpStatusCode.NotFound);
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var client = new ContextCoreClient(http);

        var added = await client.AddWorkingMemoryItemAsync(new WorkingMemoryItem
        {
            Id = "work-1",
            WorkspaceId = "workspace 1",
            CollectionId = "collection/1",
            Type = "note",
            Content = "Remember this for the current task."
        });
        var recent = await client.GetRecentWorkingMemoryAsync("workspace 1", "collection/1", take: 5);
        var active = await client.SetWorkingMemoryActiveContextAsync(new WorkingMemoryActiveContext
        {
            WorkspaceId = "workspace 1",
            CollectionId = "collection/1",
            CurrentTaskId = "task-1",
            Summary = "Active context."
        });
        var activeFromGet = await client.GetWorkingMemoryActiveContextAsync("workspace 1", "collection/1");
        var currentTask = await client.SetWorkingMemoryCurrentTaskAsync(new WorkingMemoryCurrentTask
        {
            TaskId = "task-1",
            WorkspaceId = "workspace 1",
            CollectionId = "collection/1",
            Title = "Current task",
            Status = "active"
        });
        var currentTaskFromGet = await client.GetWorkingMemoryCurrentTaskAsync("workspace 1", "collection/1");
        await client.ClearWorkingMemoryAsync("workspace 1", "collection/1");
        var missingActiveException = await Assert.ThrowsExceptionAsync<ContextCoreApiException>(() =>
            client.GetWorkingMemoryActiveContextAsync("workspace 1", "collection/1"));

        Assert.AreEqual("work-1", added.Id);
        Assert.AreEqual(1, recent.Count);
        Assert.AreEqual("task-1", active.CurrentTaskId);
        Assert.AreEqual("task-1", activeFromGet?.CurrentTaskId);
        Assert.AreEqual("task-1", currentTask.TaskId);
        Assert.AreEqual("task-1", currentTaskFromGet?.TaskId);
        Assert.AreEqual(ContextCoreErrorCodes.NotFound, missingActiveException.ErrorResponse.ErrorCode);
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task LearningClientMethods_ShouldCallExpectedRoutes_AndParseErrors()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/learning/records", request.RequestUri?.AbsolutePath);
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "workspaceId=workspace-1");
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "signal=Positive");
            return Json(new[]
            {
                CreateLearningRecord("record-1", ContextFeedbackSignal.Positive, ContextFailureType.None)
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/learning/records/record-1", request.RequestUri?.AbsolutePath);
            return Json(CreateLearningRecord("record-1", ContextFeedbackSignal.Positive, ContextFailureType.None));
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/learning/feedback", request.RequestUri?.AbsolutePath);
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "workspaceId=workspace-1");
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "action=Accepted");
            return Json(new[]
            {
                new PromotionFeedbackSignal
                {
                    FeedbackId = "feedback-1",
                    CandidateId = "candidate-1",
                    WorkspaceId = "workspace-1",
                    CollectionId = "collection-1",
                    Action = "Accepted",
                    Reviewer = "tester",
                    Reason = "accepted",
                    SourceWorkingItemId = "working-1",
                    CreatedTargetItemId = "memory-1",
                    SuggestedTargetLayer = "CandidateMemory",
                    ActualTargetLayer = "CandidateMemory",
                    Confidence = 0.9,
                    Importance = 0.9,
                    EvidenceRefs = ["event-1"],
                    CreatedAt = DateTimeOffset.UtcNow
                }
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/learning/cases", request.RequestUri?.AbsolutePath);
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "caseKind=PromotionFalsePositive");
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "status=Draft");
            return Json(new[]
            {
                CreateLearningCase("case-1", "PromotionFalsePositive", ContextFeedbackSignal.Negative, ContextFailureType.PromotionFalsePositive)
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/learning/cases", request.RequestUri?.AbsolutePath);
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "sourceRecordId=record-1");
            return Json(new[]
            {
                CreateLearningCase("case-from-alias", "PositivePromotionSample", ContextFeedbackSignal.Positive, ContextFailureType.None)
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/learning/cases/case-1", request.RequestUri?.AbsolutePath);
            return Json(CreateLearningCase("case-1", "PromotionFalsePositive", ContextFeedbackSignal.Negative, ContextFailureType.PromotionFalsePositive));
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/learning/cases", request.RequestUri?.AbsolutePath);
            return Json(CreateLearningCase("manual-case-1", "ManualLearningCase", ContextFeedbackSignal.Positive, ContextFailureType.None));
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/learning/cases/generate", request.RequestUri?.AbsolutePath);
            return Json(new ContextLearningCaseGenerationResult
            {
                RecordsScanned = 1,
                Created = 1,
                Existing = 0,
                Cases =
                [
                    CreateLearningCase("generated-case-1", "PositivePromotionSample", ContextFeedbackSignal.Positive, ContextFailureType.None)
                ]
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/learning/cases/case-1/activate", request.RequestUri?.AbsolutePath);
            return Json(new ContextLearningCaseStatusUpdateResponse
            {
                OperationId = "case-activate",
                CaseId = "case-1",
                Status = ContextLearningCaseStatus.ActiveRegression,
                Case = CreateLearningCase("case-1", "PromotionFalsePositive", ContextFeedbackSignal.Negative, ContextFailureType.PromotionFalsePositive, ContextLearningCaseStatus.ActiveRegression)
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/learning/cases/case-1/archive", request.RequestUri?.AbsolutePath);
            return Json(new ContextLearningCaseStatusUpdateResponse
            {
                OperationId = "case-archive",
                CaseId = "case-1",
                Status = ContextLearningCaseStatus.Archived,
                Case = CreateLearningCase("case-1", "PromotionFalsePositive", ContextFeedbackSignal.Negative, ContextFailureType.PromotionFalsePositive, ContextLearningCaseStatus.Archived)
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/learning/cases/case-1/reject", request.RequestUri?.AbsolutePath);
            return Json(new ContextLearningCaseStatusUpdateResponse
            {
                OperationId = "case-reject",
                CaseId = "case-1",
                Status = ContextLearningCaseStatus.Rejected,
                Case = CreateLearningCase("case-1", "PromotionFalsePositive", ContextFeedbackSignal.Negative, ContextFailureType.PromotionFalsePositive, ContextLearningCaseStatus.Rejected)
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/learning/summary", request.RequestUri?.AbsolutePath);
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "workspaceId=workspace-1");
            return Json(new ContextLearningSummary
            {
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1",
                RecordCount = 1,
                CaseCount = 1,
                PositiveCount = 1,
                DraftCaseCount = 1
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/learning/regression/cases", request.RequestUri?.AbsolutePath);
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "limit=10");
            return Json(new[]
            {
                CreateLearningCase("case-1", "PromotionFalsePositive", ContextFeedbackSignal.Negative, ContextFailureType.PromotionFalsePositive, ContextLearningCaseStatus.ActiveRegression)
            });
        });
        handlers.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(JsonSerializer.Serialize(new ContextCoreErrorResponse
            {
                OperationId = "learning-missing",
                ErrorCode = ContextCoreErrorCodes.NotFound,
                Message = "missing learning record",
                Target = "learning.record",
                TraceId = "trace-learning-missing"
            }, JsonOptions), Encoding.UTF8, "application/json")
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var client = new ContextCoreClient(http);

        var records = await client.QueryLearningRecordsAsync(new ContextLearningRecordQuery
        {
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            Signal = ContextFeedbackSignal.Positive,
            Limit = 10
        });
        var record = await client.GetLearningRecordAsync("record-1");
        var feedback = await client.GetLearningFeedbackAsync(
            "workspace-1",
            "collection-1",
            action: "Accepted",
            limit: 10);
        var cases = await client.QueryLearningCasesAsync(new ContextLearningCaseQuery
        {
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            CaseKind = "PromotionFalsePositive",
            Status = ContextLearningCaseStatus.Draft,
            Limit = 10
        });
        var casesViaAlias = await client.GetLearningCasesAsync(
            "workspace-1",
            "collection-1",
            sourceRecordId: "record-1",
            limit: 10);
        var learningCase = await client.GetLearningCaseAsync("case-1");
        var created = await client.CreateLearningCaseAsync(CreateLearningCase("manual-case-1", "ManualLearningCase", ContextFeedbackSignal.Positive, ContextFailureType.None));
        var generated = await client.GenerateLearningCasesAsync(new ContextLearningCaseGenerationRequest
        {
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            Limit = 10
        });
        var activated = await client.ActivateLearningCaseAsync("case-1", new ContextLearningCaseStatusUpdateRequest { OperationId = "case-activate", Reason = "activate" });
        var archived = await client.ArchiveLearningCaseAsync("case-1", new ContextLearningCaseStatusUpdateRequest { OperationId = "case-archive", Reason = "archive" });
        var rejected = await client.RejectLearningCaseAsync("case-1", new ContextLearningCaseStatusUpdateRequest { OperationId = "case-reject", Reason = "reject" });
        var summary = await client.GetLearningSummaryAsync("workspace-1", "collection-1");
        var regressionCases = await client.GetRegressionLearningCasesAsync("workspace-1", "collection-1", limit: 10);
        var exception = await Assert.ThrowsExceptionAsync<ContextCoreApiException>(() =>
            client.GetLearningRecordAsync("missing"));

        Assert.AreEqual(1, records.Count);
        Assert.AreEqual("record-1", record.RecordId);
        Assert.AreEqual(1, feedback.Count);
        Assert.AreEqual("feedback-1", feedback[0].FeedbackId);
        Assert.AreEqual(1, cases.Count);
        Assert.AreEqual(1, casesViaAlias.Count);
        Assert.AreEqual("case-1", learningCase.CaseId);
        Assert.AreEqual("manual-case-1", created.CaseId);
        Assert.AreEqual(1, generated.Created);
        Assert.AreEqual(ContextLearningCaseStatus.ActiveRegression, activated.Status);
        Assert.AreEqual(ContextLearningCaseStatus.Archived, archived.Status);
        Assert.AreEqual(ContextLearningCaseStatus.Rejected, rejected.Status);
        Assert.AreEqual(1, summary.RecordCount);
        Assert.AreEqual(1, regressionCases.Count);
        Assert.AreEqual(ContextCoreErrorCodes.NotFound, exception.ErrorResponse.ErrorCode);
        Assert.AreEqual("learning.record", exception.ErrorResponse.Target);
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task PolicyFeedbackClientMethods_ShouldCallExpectedRoutes()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/learning/policy-feedback", request.RequestUri?.AbsolutePath);
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "workspaceId=workspace-1");
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "collectionId=collection-1");
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "limit=10");
            return Json(new PolicyFeedbackDataset
            {
                DatasetId = "policy-feedback-dataset-1",
                Name = "Policy Feedback Dataset",
                Scope = "workspace:workspace-1/collection:collection-1",
                PositiveCount = 1,
                NegativeCount = 0,
                NeutralCount = 0,
                SourceTypes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PromotionCandidateReviewRecord"] = 1
                },
                PolicyVersion = "policy-feedback-dataset/v1",
                EvalBaselineRef = "docs/eval-baseline-p15.md",
                Records =
                [
                    new PolicyFeedbackRecord
                    {
                        FeedbackRecordId = "policy-feedback-record-1",
                        WorkspaceId = "workspace-1",
                        CollectionId = "collection-1",
                        SourceType = "PromotionCandidateReviewRecord",
                        SourceId = "review-1",
                        Action = "accept",
                        Label = PolicyFeedbackLabels.Positive,
                        Reason = "accepted",
                        PositiveRefs = ["event-1"],
                        EvidenceRefs = ["event-1"],
                        TargetLayer = "CandidateMemory",
                        CreatedAt = DateTimeOffset.UtcNow,
                        Reviewer = "tester",
                        PolicyVersion = "policy-feedback-dataset/v1"
                    }
                ]
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/learning/policy-feedback/export", request.RequestUri?.AbsolutePath);
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "workspaceId=workspace-1");
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "limit=100");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"feedbackRecordId\":\"policy-feedback-record-1\",\"label\":\"Positive\"}",
                    Encoding.UTF8,
                    "application/x-ndjson")
            };
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var client = new ContextCoreClient(http);

        var dataset = await client.GetPolicyFeedbackAsync("workspace-1", "collection-1", limit: 10);
        var jsonl = await client.ExportPolicyFeedbackAsync("workspace-1", "collection-1", limit: 100);

        Assert.AreEqual("policy-feedback-dataset-1", dataset.DatasetId);
        Assert.AreEqual(1, dataset.PositiveCount);
        Assert.AreEqual("policy-feedback-record-1", dataset.Records[0].FeedbackRecordId);
        StringAssert.Contains(jsonl, "policy-feedback-record-1");
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task LearningFeatureClientMethods_ShouldCallExpectedRoutes()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/learning/features", request.RequestUri?.AbsolutePath);
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "workspaceId=workspace-1");
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "collectionId=collection-1");
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "limit=10");
            return Json(new LearningFeatureDataset
            {
                DatasetId = "learning-feature-dataset-1",
                FeatureCount = 1,
                RankingPairCount = 2,
                RouterIntentExampleCount = 3,
                LatestExportPath = "learning/features",
                PolicyVersion = "learning-feature-dataset/v1",
                LabelDistribution = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [PolicyFeedbackLabels.Positive] = 1
                },
                SourceTypeDistribution = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PromotionCandidateReviewRecord"] = 1
                },
                FeatureExamples =
                [
                    new ContextPolicyFeatureExample
                    {
                        ExampleId = "feature-1",
                        WorkspaceId = "workspace-1",
                        CollectionId = "collection-1",
                        SourceType = "PromotionCandidateReviewRecord",
                        SourceId = "review-1",
                        TaskKind = "PolicyFeedback",
                        Label = PolicyFeedbackLabels.Positive,
                        CandidateId = "candidate-1",
                        CandidateLayer = "CandidateMemory",
                        Accepted = true,
                        Selected = true,
                        EvidenceRefs = ["event-1"],
                        PolicyVersion = "learning-feature-dataset/v1",
                        CreatedAt = DateTimeOffset.UtcNow
                    }
                ]
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/learning/features/export", request.RequestUri?.AbsolutePath);
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "workspaceId=workspace-1");
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "collectionId=collection-1");
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "outputDirectory=learning%2Ffeatures");
            return Json(new LearningFeatureExportResult
            {
                OutputDirectory = "learning/features",
                PolicyFeedbackFeaturesPath = "learning/features/policy-feedback-features.jsonl",
                RankingPairsPath = "learning/features/ranking-pairs.jsonl",
                RouterIntentExamplesPath = "learning/features/router-intent-examples.jsonl",
                FeatureCount = 1,
                RankingPairCount = 2,
                RouterIntentExampleCount = 3,
                PolicyVersion = "learning-feature-dataset/v1",
                ExportedAt = DateTimeOffset.UtcNow
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/learning/features/quality", request.RequestUri?.AbsolutePath);
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "featureDirectory=learning%2Ffeatures");
            return Json(new LearningDatasetQualityReport
            {
                PolicyFeedbackFeatureCount = 1,
                RankingPairCount = 2,
                RouterIntentExampleCount = 3,
                PositiveCount = 1,
                NegativeCount = 0,
                DataRisks =
                [
                    LearningDatasetDataRisks.MissingNegativeSamples
                ],
                TaskReadiness = new Dictionary<string, LearningDatasetTaskReadiness>(StringComparer.OrdinalIgnoreCase)
                {
                    [LearningDatasetTaskNames.RouterIntentClassifier] = new LearningDatasetTaskReadiness
                    {
                        TaskName = LearningDatasetTaskNames.RouterIntentClassifier,
                        Status = LearningDatasetReadinessStatus.Ready,
                        Ready = true,
                        RecommendedNextAction = "offline only"
                    }
                },
                RecommendedNextAction = "Add negative examples.",
                PolicyVersion = "learning-dataset-quality/v1"
            });
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var client = new ContextCoreClient(http);

        var dataset = await client.GetLearningFeaturesAsync("workspace-1", "collection-1", limit: 10);
        var export = await client.ExportLearningFeaturesAsync("workspace-1", "collection-1", outputDirectory: "learning/features");
        var quality = await client.GetLearningDatasetQualityAsync("learning/features");

        Assert.AreEqual("learning-feature-dataset-1", dataset.DatasetId);
        Assert.AreEqual(1, dataset.FeatureCount);
        Assert.AreEqual(2, dataset.RankingPairCount);
        Assert.AreEqual(3, dataset.RouterIntentExampleCount);
        Assert.AreEqual("feature-1", dataset.FeatureExamples[0].ExampleId);
        Assert.AreEqual("learning/features/policy-feedback-features.jsonl", export.PolicyFeedbackFeaturesPath);
        Assert.AreEqual(1, quality.PolicyFeedbackFeatureCount);
        CollectionAssert.Contains(quality.DataRisks.ToArray(), LearningDatasetDataRisks.MissingNegativeSamples);
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task DebugLifecycleAwareRankerAsync_ShouldCallExpectedRoute()
    {
        using var http = CreateHttpClient(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/retrieval/ranker-shadow/debug", request.RequestUri?.AbsolutePath);

            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var json = JsonDocument.Parse(body);
            Assert.AreEqual("workspace-1", json.RootElement.GetProperty("workspaceId").GetString());
            Assert.AreEqual("collection-1", json.RootElement.GetProperty("collectionId").GetString());
            Assert.AreEqual("current rule", json.RootElement.GetProperty("query").GetString());
            Assert.IsTrue(json.RootElement.GetProperty("includeLifecycleDetails").GetBoolean());

            return Json(new LifecycleAwareRankerShadowDebugResponse
            {
                OperationId = "ranker-debug-1",
                RetrievalOperationId = "retrieval-1",
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1",
                Query = "current rule",
                Mode = "ChatMode",
                RankerShadowEnabled = true,
                DebugEndpointEnabled = true,
                RankerShadowProfile = "lifecycle-aware-v1",
                FormalOutputChanged = false,
                SelectedSetChanged = false,
                LegacySelectedIds = ["memory:active-rule-v2"],
                FinalSelectedIds = ["memory:active-rule-v2"],
                CandidateScores =
                [
                    new LifecycleAwareRankerShadowCandidateScore
                    {
                        CandidateId = "memory:deprecated-rule-v1",
                        LegacyScore = 20,
                        LifecycleAwareScore = -18,
                        ScoreDelta = -38,
                        Reason = "deprecated_demotion;historical_demotion"
                    }
                ],
                DeprecatedDemotions =
                [
                    new LifecycleAwareRankerShadowCandidateScore
                    {
                        CandidateId = "memory:deprecated-rule-v1",
                        ScoreDelta = -38,
                        Reason = "deprecated_demotion;historical_demotion"
                    }
                ]
            });
        });
        var client = new ContextCoreClient(http);

        var response = await client.DebugLifecycleAwareRankerAsync(
            "workspace-1",
            "collection-1",
            "current rule");

        Assert.AreEqual("ranker-debug-1", response.OperationId);
        Assert.IsFalse(response.FormalOutputChanged);
        Assert.AreEqual("memory:deprecated-rule-v1", response.DeprecatedDemotions[0].CandidateId);
    }

    [TestMethod]
    public async Task RankerShadowTraceClientMethods_ShouldCallExpectedRoutes()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/learning/ranker-shadow/traces", request.RequestUri?.AbsolutePath);
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "workspaceId=workspace-1");
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "collectionId=collection-1");
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "take=5");
            return Json(new[]
            {
                new LifecycleAwareRankerShadowTraceRecord
                {
                    RetrievalId = "retrieval-shadow-1",
                    WorkspaceId = "workspace-1",
                    CollectionId = "collection-1",
                    Query = "current rule",
                    Profile = "lifecycle-aware-v1",
                    CandidateScores =
                    [
                        new LifecycleAwareRankerShadowCandidateScore
                        {
                            CandidateId = "memory:deprecated-rule-v1",
                            ScoreDelta = -38,
                            Reason = "deprecated_demotion"
                        }
                    ]
                }
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/learning/ranker-shadow/traces", request.RequestUri?.AbsolutePath);
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "format=jsonl");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"retrievalId\":\"retrieval-shadow-1\",\"candidateScores\":[]}",
                    Encoding.UTF8,
                    "application/x-ndjson")
            };
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var client = new ContextCoreClient(http);

        var records = await client.GetRankerShadowTracesAsync("workspace-1", "collection-1", take: 5);
        var jsonl = await client.ExportRankerShadowTracesAsync("workspace-1", "collection-1", take: 5);

        Assert.AreEqual("retrieval-shadow-1", records[0].RetrievalId);
        StringAssert.Contains(jsonl, "retrieval-shadow-1");
        Assert.AreEqual(0, handlers.Count);
    }

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return new HttpClient(new StubHttpMessageHandler(handler))
        {
            BaseAddress = new Uri("http://localhost:5079")
        };
    }

    private static HttpResponseMessage Json<T>(T value, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json")
        };
    }

    private static ContextLearningRecord CreateLearningRecord(
        string recordId,
        ContextFeedbackSignal signal,
        ContextFailureType failureType)
    {
        return new ContextLearningRecord
        {
            RecordId = recordId,
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            SourceKind = "ShortTermPromotionCandidate",
            SourceId = "candidate-1",
            CandidateId = "candidate-1",
            ReviewId = "review-1",
            EventKind = signal == ContextFeedbackSignal.Positive ? "PromotionAccepted" : "PromotionRejected",
            Signal = signal,
            FailureType = failureType,
            Reason = "review reason",
            Confidence = 0.9,
            Importance = 0.9,
            EvidenceRefs = ["event-1"],
            CreatedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sourceWorkingItemId"] = "working-1"
            }
        };
    }

    private static ContextLearningCase CreateLearningCase(
        string caseId,
        string caseKind,
        ContextFeedbackSignal signal,
        ContextFailureType failureType,
        ContextLearningCaseStatus status = ContextLearningCaseStatus.Draft)
    {
        return new ContextLearningCase
        {
            CaseId = caseId,
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            SourceRecordId = "record-1",
            SourceKind = "ShortTermPromotionCandidate",
            SourceId = "candidate-1",
            CaseKind = caseKind,
            Title = "learning case",
            Summary = "learning case",
            Signal = signal,
            FailureType = failureType,
            Status = status,
            EvidenceRefs = ["event-1"],
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static StableReviewCandidate CreateStableReviewCandidate(string stableReviewCandidateId)
    {
        return new StableReviewCandidate
        {
            StableReviewCandidateId = stableReviewCandidateId,
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            SessionId = "session-1",
            SourceCandidateId = "candidate-1",
            SourceTargetItemId = "memory-1",
            SourceLearningCaseId = "case-1",
            Kind = "RecentDecision",
            Title = "稳定评审候选",
            Summary = "稳定评审候选",
            SuggestedStableTarget = "StableMemory",
            Reason = "ready",
            Confidence = 0.9,
            Importance = 0.9,
            EvidenceRefs = ["event-1"],
            ValidationStatus = StableReviewValidationStatuses.Ready,
            Status = StableReviewCandidateStatuses.Candidate,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static StableReviewDecisionResult CreateStableReviewDecisionResult(
        string stableReviewCandidateId,
        string action,
        string status,
        string? stableTargetItemId)
    {
        var review = new StableReviewRecord
        {
            ReviewId = $"stable-review-{action}",
            StableReviewCandidateId = stableReviewCandidateId,
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            Action = action,
            FromStatus = StableReviewCandidateStatuses.Candidate,
            ToStatus = status,
            Reviewer = "tester",
            Reason = action,
            StableTargetItemId = stableTargetItemId,
            StableTargetItemKind = stableTargetItemId is null ? null : "memory",
            TargetLayer = stableTargetItemId is null ? null : "StableMemory",
            SourcePromotionCandidateId = "candidate-1",
            SourceTargetItemId = "memory-1",
            EvidenceRefs = ["event-1"],
            ValidationStatus = StableReviewValidationStatuses.ReadyForReview,
            CreatedAt = DateTimeOffset.UtcNow,
            ReviewedAt = DateTimeOffset.UtcNow
        };
        return new StableReviewDecisionResult
        {
            OperationId = "stable-review-op-1",
            StableReviewCandidateId = stableReviewCandidateId,
            Action = action,
            Status = status,
            ReviewId = review.ReviewId,
            Reviewer = review.Reviewer,
            Reason = review.Reason,
            ReviewedAt = review.ReviewedAt,
            CreatedStableTargetItemId = stableTargetItemId,
            CreatedTargetItemId = stableTargetItemId,
            StableTargetItemKind = review.StableTargetItemKind,
            TargetLayer = review.TargetLayer,
            ValidationStatus = StableReviewValidationStatuses.ReadyForReview,
            Candidate = CreateStableReviewCandidate(stableReviewCandidateId),
            Review = review
        };
    }

    private static ConstraintGapCandidate CreateConstraintGap(string gapId)
    {
        return new ConstraintGapCandidate
        {
            GapId = gapId,
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            SessionId = "session-1",
            Source = "planning",
            SourceSampleId = "sample-1",
            SourceOperationId = "op-1",
            ExpectedConstraintText = "输出必须使用中文",
            SuggestedConstraintTitle = "输出必须使用中文",
            SuggestedConstraintScope = "Collection",
            SuggestedConstraintType = "Hard",
            Severity = ConstraintGapSeverity.High,
            Reason = "missing hard constraint",
            EvidenceRefs = ["eval:sample-1"],
            Status = ConstraintGapStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static ConstraintGapReviewResult CreateConstraintGapReviewResult(
        string gapId,
        string action,
        string status,
        string? createdConstraintId)
    {
        var review = CreateConstraintGapReview(gapId, action, status, createdConstraintId);
        return new ConstraintGapReviewResult
        {
            OperationId = "gap-review-op-1",
            GapId = gapId,
            Action = action,
            Status = status,
            ReviewId = review.ReviewId,
            Reviewer = review.Reviewer,
            Reason = review.Reason,
            ReviewedAt = review.ReviewedAt,
            CreatedConstraintId = createdConstraintId,
            TargetItemId = createdConstraintId,
            TargetItemKind = review.TargetItemKind,
            TargetLayer = review.TargetLayer,
            Gap = CreateConstraintGap(gapId),
            Review = review
        };
    }

    private static ConstraintGapReviewRecord CreateConstraintGapReview(
        string gapId,
        string action,
        string status,
        string? createdConstraintId)
    {
        return new ConstraintGapReviewRecord
        {
            ReviewId = $"constraint-gap-review-{action}",
            GapId = gapId,
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            SessionId = "session-1",
            Action = action,
            FromStatus = ConstraintGapStatus.Pending,
            ToStatus = status,
            Reviewer = "tester",
            Reason = action,
            CreatedConstraintId = createdConstraintId,
            TargetItemKind = createdConstraintId is null ? null : "constraint",
            TargetLayer = createdConstraintId is null ? null : "CandidateConstraint",
            SourceSampleId = "sample-1",
            SourceOperationId = "op-1",
            ExpectedConstraintText = "输出必须使用中文",
            EvidenceRefs = ["eval:sample-1"],
            CreatedAt = DateTimeOffset.UtcNow,
            ReviewedAt = DateTimeOffset.UtcNow
        };
    }

    private static ContextConstraint CreateCandidateConstraint(string constraintId)
    {
        return new ContextConstraint
        {
            Id = constraintId,
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            Scope = ContextScope.Collection,
            Level = ConstraintLevel.User,
            Content = "输出必须使用中文",
            SourceRefs = ["eval:sample-1"],
            Status = ContextMemoryStatus.Candidate,
            Confidence = 0.9,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["createdFrom"] = "constraint_gap_accept",
                ["sourceConstraintGapId"] = "gap-1",
                ["sourceSampleId"] = "sample-1",
                ["sourceOperationId"] = "operation-1",
                ["evidenceRefs"] = "eval:sample-1"
            },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static CandidateMemoryRecord CreateCandidateMemoryRecord(string candidateId)
    {
        return new CandidateMemoryRecord
        {
            Id = candidateId,
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            SessionId = "session-1",
            CandidateKind = CandidateMemoryKinds.Memory,
            Type = "preference",
            Title = "Use concise answers.",
            Summary = "Use concise answers.",
            Content = "Use concise answers.",
            Status = ContextMemoryStatus.Candidate,
            Lifecycle = CandidateMemoryLifecycle.Current,
            Importance = 0.8,
            Confidence = 0.9,
            SourceRefs = ["stpc-1", "evidence-1"],
            EvidenceRefs = ["evidence-1"],
            PromotionCandidateId = "stpc-1",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static CandidateMemoryReviewRequest CreateCandidateMemoryReviewRequest(
        string? supersedeTargetCandidateId = null)
    {
        return new CandidateMemoryReviewRequest
        {
            OperationId = "candidate-memory-review-op-1",
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            Reviewer = "tester",
            Reason = "review reason",
            SupersedeTargetCandidateId = supersedeTargetCandidateId,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["test"] = "true"
            }
        };
    }

    private static CandidateMemoryReviewResult CreateCandidateMemoryReviewResult(
        string candidateId,
        string action,
        ContextMemoryStatus toStatus,
        string? supersedeTargetCandidateId = null)
    {
        var review = new CandidateMemoryReviewRecord
        {
            ReviewId = "cmr-1",
            CandidateId = candidateId,
            CandidateKind = CandidateMemoryKinds.Memory,
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            Action = action,
            FromStatus = ContextMemoryStatus.Candidate,
            ToStatus = toStatus,
            Reviewer = "tester",
            Reason = "review reason",
            SupersedeTargetCandidateId = supersedeTargetCandidateId,
            EvidenceRefs = ["evidence-1"],
            SourceRefs = ["stpc-1", "evidence-1"],
            CreatedAt = DateTimeOffset.UtcNow,
            ReviewedAt = DateTimeOffset.UtcNow
        };
        return new CandidateMemoryReviewResult
        {
            OperationId = "candidate-memory-review-op-1",
            CandidateId = candidateId,
            CandidateKind = CandidateMemoryKinds.Memory,
            Action = action,
            FromStatus = ContextMemoryStatus.Candidate,
            ToStatus = toStatus,
            ReviewId = review.ReviewId,
            Reviewer = review.Reviewer,
            Reason = review.Reason,
            ReviewedAt = review.ReviewedAt,
            SupersedeTargetCandidateId = supersedeTargetCandidateId,
            Candidate = CreateCandidateMemoryRecord(candidateId),
            Review = review
        };
    }

    private static StableLifecycleReviewRequest CreateStableLifecycleReviewRequest(
        string? replacementItemId = null)
    {
        return new StableLifecycleReviewRequest
        {
            OperationId = "stable-lifecycle-review-op-1",
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            Reviewer = "tester",
            Reason = "review reason",
            ReplacementItemId = replacementItemId,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["test"] = "true"
            }
        };
    }

    private static StableLifecycleReviewResult CreateStableLifecycleReviewResult(
        string itemId,
        string action,
        ContextMemoryStatus toStatus,
        string toLifecycle,
        string? replacementItemId = null)
    {
        var review = new StableLifecycleReviewRecord
        {
            ReviewId = "slr-1",
            StableItemId = itemId,
            StableKind = StableMemoryKinds.StableMemory,
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            Action = action,
            FromStatus = ContextMemoryStatus.Stable,
            ToStatus = toStatus,
            FromLifecycle = StableMemoryLifecycle.Current,
            ToLifecycle = toLifecycle,
            Reviewer = "tester",
            Reason = "review reason",
            ReplacementItemId = replacementItemId,
            EvidenceRefs = ["evidence-1"],
            SourceRefs = ["src-1", "evidence-1"],
            CreatedAt = DateTimeOffset.UtcNow,
            ReviewedAt = DateTimeOffset.UtcNow
        };
        return new StableLifecycleReviewResult
        {
            OperationId = "stable-lifecycle-review-op-1",
            StableItemId = itemId,
            StableKind = StableMemoryKinds.StableMemory,
            Action = action,
            FromStatus = ContextMemoryStatus.Stable,
            ToStatus = toStatus,
            FromLifecycle = StableMemoryLifecycle.Current,
            ToLifecycle = toLifecycle,
            ReviewId = review.ReviewId,
            Reviewer = review.Reviewer,
            Reason = review.Reason,
            ReviewedAt = review.ReviewedAt,
            ReplacementItemId = replacementItemId,
            StableItem = new StableMemoryRecord
            {
                Id = itemId,
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1",
                StableKind = StableMemoryKinds.StableMemory,
                Type = "preference",
                Status = toStatus,
                Lifecycle = toLifecycle
            },
            Review = review
        };
    }

    private static CandidateConstraintReviewResult CreateCandidateConstraintReviewResult(
        string constraintId,
        string action,
        ContextMemoryStatus status)
    {
        var review = CreateCandidateConstraintReview(constraintId, action, status);
        return new CandidateConstraintReviewResult
        {
            OperationId = "candidate-constraint-review-op-1",
            ConstraintId = constraintId,
            Action = action,
            Status = status,
            ReviewId = review.ReviewId,
            Reviewer = review.Reviewer,
            Reason = review.Reason,
            ReviewedAt = review.ReviewedAt,
            ActivatedConstraintId = status == ContextMemoryStatus.Active ? constraintId : null,
            TargetLayer = status == ContextMemoryStatus.Active ? "ActiveHardConstraint" : "RejectedCandidateConstraint",
            Constraint = CreateCandidateConstraintWithStatus(constraintId, status),
            Review = review
        };
    }

    private static ContextConstraint CreateCandidateConstraintWithStatus(string constraintId, ContextMemoryStatus status)
    {
        var item = CreateCandidateConstraint(constraintId);
        return new ContextConstraint
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Scope = item.Scope,
            Level = status == ContextMemoryStatus.Active ? ConstraintLevel.Hard : item.Level,
            Content = item.Content,
            AppliesToRefs = item.AppliesToRefs,
            SourceRefs = item.SourceRefs,
            Status = status,
            Confidence = item.Confidence,
            Metadata = item.Metadata,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }

    private static CandidateConstraintReviewRecord CreateCandidateConstraintReview(
        string constraintId,
        string action,
        ContextMemoryStatus status)
    {
        return new CandidateConstraintReviewRecord
        {
            ReviewId = $"candidate-constraint-review-{action}",
            ConstraintId = constraintId,
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            Action = action,
            FromStatus = ContextMemoryStatus.Candidate,
            ToStatus = status,
            Reviewer = "tester",
            Reason = action,
            ActivatedConstraintId = status == ContextMemoryStatus.Active ? constraintId : null,
            SourceConstraintGapId = "gap-1",
            SourceSampleId = "sample-1",
            SourceOperationId = "operation-1",
            EvidenceRefs = ["eval:sample-1"],
            CreatedAt = DateTimeOffset.UtcNow,
            ReviewedAt = DateTimeOffset.UtcNow
        };
    }

    private static ReviewPromotionCandidateResponse CreateReviewResponse(
        string action,
        PromotionCandidateStatus status,
        string? targetItemId)
    {
        var review = new PromotionCandidateReviewRecord
        {
            ReviewId = $"review-{action}",
            CandidateId = $"candidate-{action}",
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            Action = action,
            FromStatus = PromotionCandidateStatus.Candidate,
            ToStatus = status,
            Reviewer = "tester",
            Reason = action,
            TargetItemId = targetItemId,
            TargetItemKind = targetItemId is null ? null : "memory",
            TargetLayer = targetItemId is null ? null : "CandidateMemory",
            EvidenceRefs = ["event-1"],
            CreatedAt = DateTimeOffset.UtcNow
        };

        return new ReviewPromotionCandidateResponse
        {
            OperationId = $"op-{action}",
            CandidateId = review.CandidateId,
            Action = action,
            Status = status,
            ReviewId = review.ReviewId,
            TargetItemId = targetItemId,
            TargetItemKind = review.TargetItemKind,
            TargetLayer = review.TargetLayer,
            Review = review,
            Candidate = new ShortTermPromotionCandidate
            {
                CandidateId = review.CandidateId,
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1",
                SourceWorkingItemId = "working-1",
                Kind = "KnownIssue",
                Title = "缓存击穿",
                Summary = "缓存击穿",
                SuggestedTargetLayer = "CandidateMemory",
                Status = status
            }
        };
    }

    private static HttpResponseMessage NoContent()
    {
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
