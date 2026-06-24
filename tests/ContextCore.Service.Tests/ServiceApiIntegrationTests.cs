using System.Net.Http.Json;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Planning;
using ContextCore.Service;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.InMemory;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ContextCore.Service.Tests;

/// <summary>覆盖 ContextCore.Service 的 HTTP API 与后台作业链路。</summary>
[TestClass]
public sealed class ServiceApiIntegrationTests
{
    [TestMethod]
    public async Task ApiKeyMiddleware_ShouldReturn401WhenKeyMissingAndReturn200WithValidKey()
    {
        var rootPath = CreateTestRootPath();
        const string testKey = "test-api-key-secure";

        try
        {
            using var factory = CreateFactory(
                rootPath,
                jobWorkerEnabled: false,
                pollIntervalMs: 100,
                requireApiKey: true,
                apiKey: testKey);

            // 无 Key 请求写接口应返回 401
            using var clientNoKey = factory.CreateClient();
            var noKeyResponse = await clientNoKey.PostAsJsonAsync(
                "/api/context/ingest",
                new ContextItem
                {
                    Id = "sec-test",
                    WorkspaceId = "ws",
                    CollectionId = "col",
                    Type = "note",
                    Content = "Security test",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            Assert.AreEqual(System.Net.HttpStatusCode.Unauthorized, noKeyResponse.StatusCode);

            // 有效 Key 请求应成功
            using var clientWithKey = factory.CreateClient();
            clientWithKey.DefaultRequestHeaders.TryAddWithoutValidation("X-ContextCore-Key", testKey);
            var withKeyResponse = await clientWithKey.PostAsJsonAsync(
                "/api/context/ingest",
                new ContextItem
                {
                    Id = "sec-test",
                    WorkspaceId = "ws",
                    CollectionId = "col",
                    Type = "note",
                    Content = "Security test",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            withKeyResponse.EnsureSuccessStatusCode();

            // /health 不需要 Key
            var healthResponse = await clientNoKey.GetAsync("/health");
            healthResponse.EnsureSuccessStatusCode();
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task FoundationReadOnlyApis_ShouldEnforceApiKeyWithoutLeakingSecret()
    {
        var rootPath = CreateTestRootPath();
        const string testKey = "foundation-secret-key";

        try
        {
            using var factory = CreateFactory(
                rootPath,
                jobWorkerEnabled: false,
                pollIntervalMs: 100,
                requireApiKey: true,
                apiKey: testKey);

            using var noKeyClient = factory.CreateClient();
            var noKey = await noKeyClient.GetAsync("/api/admin/foundation/status");
            Assert.AreEqual(System.Net.HttpStatusCode.Unauthorized, noKey.StatusCode);

            using var wrongKeyClient = factory.CreateClient();
            wrongKeyClient.DefaultRequestHeaders.TryAddWithoutValidation("X-ContextCore-Key", "wrong-key");
            var wrongKey = await wrongKeyClient.GetAsync("/api/admin/foundation/status");
            Assert.AreEqual(System.Net.HttpStatusCode.Unauthorized, wrongKey.StatusCode);

            using var correctKeyClient = factory.CreateClient();
            correctKeyClient.DefaultRequestHeaders.TryAddWithoutValidation("X-ContextCore-Key", testKey);
            var ok = await correctKeyClient.GetStringAsync("/api/admin/foundation/status");
            Assert.IsFalse(ok.Contains(testKey, StringComparison.Ordinal), "response must not expose API key value");
            AssertNoLocalPathLeak(ok, "foundation status with auth");
            var envelope = JsonSerializer.Deserialize<FoundationApiResponseEnvelope<FoundationServiceStatusResponse>>(
                ok,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            Assert.IsNotNull(envelope);
            Assert.IsTrue(envelope!.Success);
            Assert.IsNotNull(envelope.Data);
            Assert.IsFalse(envelope.Data!.RuntimeMutated);
            Assert.IsFalse(envelope.Data.RuntimeSwitchAllowed);
            Assert.IsFalse(envelope.Data.FormalRetrievalAllowed);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ServiceApi_ShouldIngestQueryAndReportStatus()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();

            var status = await client.GetFromJsonAsync<RuntimeStatusResponse>("/api/status");
            Assert.IsNotNull(status);
            Assert.AreEqual("filesystem", status!.Storage.Provider);
            Assert.AreEqual(FileStorageOptions.ResolveRootPath(rootPath), status.Storage.RootPath);
            Assert.IsNotNull(status.Readiness);
            Assert.AreEqual("ready", status.Readiness.Status);
            Assert.AreEqual("ServiceReadyAlpha", status.Readiness.ProviderState);
            Assert.IsFalse(status.Readiness.ProductionReady);
            Assert.IsTrue(status.Readiness.Checks.Any(check => check.Name == "storage-root" && check.Status == "ok"));
            Assert.AreEqual("retrieval-orchestration-baseline-v1", status.RetrievalBaseline);
            Assert.IsTrue(status.Capabilities.Any(capability =>
                capability.Name == "filesystem" && capability.Active && capability.State == "AlphaSupported"));
            Assert.IsTrue(status.Capabilities.Any(capability =>
                capability.Name == "memory" && !capability.Active && capability.State == "TestOnly"));
            Assert.IsTrue(status.Capabilities.Any(capability =>
                capability.Name == "postgres" && !capability.Active && capability.State == "Experimental"));
            Assert.IsNotNull(status.ShortTermMaintenance);
            Assert.IsFalse(status.ShortTermMaintenance!.Enabled);

            var item = new ContextItem
            {
                Id = "api-item-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Type = "note",
                Content = "HTTP API 集成测试内容。",
                Tags = ["api", "integration"],
                SourceRefs = ["source:api-item-1"],
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            var ingestResponse = await client.PostAsJsonAsync("/api/context/ingest", item);
            ingestResponse.EnsureSuccessStatusCode();

            var stored = await ingestResponse.Content.ReadFromJsonAsync<ContextInputIngestionResult>();
            Assert.IsNotNull(stored);
            Assert.AreEqual(item.Id, stored!.Item.Id);
            Assert.IsTrue(stored.Created);
            Assert.IsFalse(stored.Deduped);
            Assert.IsTrue(stored.Item.Metadata.ContainsKey("contentHash"));
            Assert.IsTrue(stored.Item.Metadata.ContainsKey("sequenceId"));
            Assert.IsTrue(stored.Item.Metadata.ContainsKey("operationId"));

            var commandResponse = await client.PostAsJsonAsync("/api/context/ingest", new ContextInputCommand
            {
                OperationId = "context-cmd-1",
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                Source = "service-test",
                InputKind = "note",
                Content = "HTTP API 命令入口测试内容。",
                SourceRefs = ["source:context-cmd-1"],
                Metadata = new Dictionary<string, string>
                {
                    ["custom"] = "preserved"
                }
            });
            commandResponse.EnsureSuccessStatusCode();
            var commandResult = await commandResponse.Content.ReadFromJsonAsync<ContextInputIngestionResult>();
            Assert.IsNotNull(commandResult);
            Assert.AreEqual("preserved", commandResult!.Item.Metadata["custom"]);
            Assert.AreEqual("context-cmd-1", commandResult.OperationId);

            var queryResponse = await client.PostAsJsonAsync("/api/context/query", new ContextQuery
            {
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                Take = 10
            });
            queryResponse.EnsureSuccessStatusCode();

            var results = await queryResponse.Content.ReadFromJsonAsync<ContextItem[]>();
            Assert.IsNotNull(results);
            Assert.IsTrue(results!.Any(result => result.Id == item.Id));
            Assert.IsTrue(results.Any(result => result.Metadata.GetValueOrDefault("custom") == "preserved"));
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task RankerShadowDebugEndpoint_ShouldReturnScoresWithoutChangingFormalOutput()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(
                rootPath,
                jobWorkerEnabled: false,
                pollIntervalMs: 100,
                configureServices: services =>
                {
                    services.RemoveAll<IContextRetriever>();
                    services.AddSingleton<IContextRetriever>(new FakeRankerDebugRetriever());
                });
            using var client = factory.CreateClient();

            var response = await client.PostAsJsonAsync(
                "/api/retrieval/ranker-shadow/debug",
                new LifecycleAwareRankerShadowDebugRequest
                {
                    WorkspaceId = "workspace-1",
                    CollectionId = "collection-1",
                    Query = "current preference",
                    Mode = "ChatMode"
                });
            response.EnsureSuccessStatusCode();

            var debug = await response.Content.ReadFromJsonAsync<LifecycleAwareRankerShadowDebugResponse>();

            Assert.IsNotNull(debug);
            Assert.IsTrue(debug!.RankerShadowEnabled);
            Assert.IsFalse(debug.FormalOutputChanged);
            Assert.IsFalse(debug.SelectedSetChanged);
            CollectionAssert.AreEqual(debug.LegacySelectedIds.ToArray(), debug.FinalSelectedIds.ToArray());
            Assert.IsTrue(debug.CandidateScores.Any(item => item.CandidateId == "memory:deprecated-rule-v1"));
            Assert.IsTrue(debug.DeprecatedDemotions.Any(item =>
                item.CandidateId == "memory:deprecated-rule-v1"
                && item.Reason.Contains("deprecated_demotion", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task RankerShadowTraceEndpoint_ShouldExportJsonLinesCompatibleTraces()
    {
        var rootPath = CreateTestRootPath();
        var traceStore = new InMemoryRetrievalTraceStore();
        await traceStore.SaveAsync(new ContextRetrievalTrace
        {
            RetrievalId = "retrieval-shadow-api-1",
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            QueryText = "current rule",
            CreatedAt = DateTimeOffset.UtcNow,
            RankerShadowTrace = new LifecycleAwareRankerShadowTrace
            {
                RankerShadowEnabled = true,
                RankerShadowProfile = "lifecycle-aware-v1",
                CandidateShadowScores =
                [
                    new LifecycleAwareRankerShadowCandidateScore
                    {
                        CandidateId = "memory:deprecated-rule-v1",
                        LegacyScore = 20,
                        LifecycleAwareScore = -18,
                        ScoreDelta = -38,
                        Reason = "deprecated_demotion",
                        DemotionReasons = ["deprecated_demotion"]
                    }
                ]
            }
        });

        try
        {
            using var factory = CreateFactory(
                rootPath,
                jobWorkerEnabled: false,
                pollIntervalMs: 100,
                configureServices: services =>
                {
                    services.RemoveAll<IRetrievalTraceStore>();
                    services.AddSingleton<IRetrievalTraceStore>(traceStore);
                });
            using var client = factory.CreateClient();

            var response = await client.GetAsync(
                "/api/learning/ranker-shadow/traces?workspaceId=workspace-1&collectionId=collection-1&take=10&format=jsonl");
            response.EnsureSuccessStatusCode();
            var jsonl = await response.Content.ReadAsStringAsync();

            StringAssert.Contains(jsonl, "\"retrievalId\":\"retrieval-shadow-api-1\"");
            StringAssert.Contains(jsonl, "\"candidateId\":\"memory:deprecated-rule-v1\"");
            Assert.AreEqual("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task GraphExpansionShadowTraceEndpoint_ShouldExportJsonLinesCompatibleTraces()
    {
        var rootPath = CreateTestRootPath();
        var traceStore = new InMemoryRetrievalTraceStore();
        await traceStore.SaveAsync(new ContextRetrievalTrace
        {
            RetrievalId = "retrieval-graph-shadow-api-1",
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            QueryText = "audit conflict",
            CreatedAt = DateTimeOffset.UtcNow,
            GraphExpansionShadowTrace = new GraphExpansionShadowTrace
            {
                GraphExpansionShadowEnabled = true,
                GraphExpansionProfiles = ["audit-v1", "conflict-v1"],
                AcceptedRelations =
                [
                    new RelationExpansionPreviewRelation
                    {
                        RelationId = "rel-api-audit",
                        SourceId = "item-current",
                        TargetId = "item-old",
                        RelationType = ContextRelationTypes.Replaces,
                        TargetSection = GraphExpansionTargetSection.AuditContext,
                        RiskIfNormalSelected = true,
                        RiskAfterSectionRouting = false
                    }
                ],
                TargetSections = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [GraphExpansionTargetSection.AuditContext] = 1
                }
            }
        });

        try
        {
            using var factory = CreateFactory(
                rootPath,
                jobWorkerEnabled: false,
                pollIntervalMs: 100,
                configureServices: services =>
                {
                    services.RemoveAll<IRetrievalTraceStore>();
                    services.AddSingleton<IRetrievalTraceStore>(traceStore);
                });
            using var client = factory.CreateClient();

            var response = await client.GetAsync(
                "/api/learning/graph-expansion-shadow/traces?workspaceId=workspace-1&collectionId=collection-1&take=10&format=jsonl");
            response.EnsureSuccessStatusCode();
            var jsonl = await response.Content.ReadAsStringAsync();

            StringAssert.Contains(jsonl, "\"retrievalId\":\"retrieval-graph-shadow-api-1\"");
            StringAssert.Contains(jsonl, "\"relationId\":\"rel-api-audit\"");
            Assert.AreEqual("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task RouterShadowTraceEndpoint_ShouldExportJsonLinesCompatibleTraces()
    {
        var rootPath = CreateTestRootPath();
        var traceStore = new InMemoryRouterIntentShadowTraceStore();
        await traceStore.SaveAsync(new RouterIntentShadowTrace
        {
            RequestId = "router-shadow-api-1",
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            EntryPoint = "planning",
            QueryText = "build verification",
            RuntimeIntent = PlanningIntentDetector.CodingTask,
            ShadowIntent = PlanningIntentDetector.FuzzyQuestion,
            ShadowConfidence = 0.12,
            Agreement = false,
            LowConfidence = true,
            FormalOutputChanged = false,
            CreatedAt = DateTimeOffset.UtcNow
        });

        try
        {
            using var factory = CreateFactory(
                rootPath,
                jobWorkerEnabled: false,
                pollIntervalMs: 100,
                configureServices: services =>
                {
                    services.RemoveAll<IRouterIntentShadowTraceStore>();
                    services.AddSingleton<IRouterIntentShadowTraceStore>(traceStore);
                });
            using var client = factory.CreateClient();

            var response = await client.GetAsync(
                "/api/learning/router-shadow/traces?workspaceId=workspace-1&collectionId=collection-1&take=10&format=jsonl");
            response.EnsureSuccessStatusCode();
            var jsonl = await response.Content.ReadAsStringAsync();

            StringAssert.Contains(jsonl, "\"requestId\":\"router-shadow-api-1\"");
            StringAssert.Contains(jsonl, "\"formalOutputChanged\":false");
            Assert.AreEqual("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ContextAndAdminIngest_ShouldReturnAlignedResultShape()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();

            var command = new ContextInputCommand
            {
                OperationId = "shape-op-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Source = "shape-test",
                InputKind = "note",
                Content = "shape",
                SourceRefs = ["source:shape-1"]
            };

            using var contextResponse = await client.PostAsJsonAsync("/api/context/ingest", command);
            using var adminResponse = await client.PostAsJsonAsync("/api/admin/ingest", new ContextInputCommand
            {
                OperationId = "shape-op-2",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Source = "shape-test",
                InputKind = "note",
                Content = "shape-admin",
                SourceRefs = ["source:shape-2"]
            });

            contextResponse.EnsureSuccessStatusCode();
            adminResponse.EnsureSuccessStatusCode();

            using var contextJson = JsonDocument.Parse(await contextResponse.Content.ReadAsStringAsync());
            using var adminJson = JsonDocument.Parse(await adminResponse.Content.ReadAsStringAsync());
            var contextKeys = contextJson.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray();
            var adminKeys = adminJson.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray();

            CollectionAssert.AreEqual(contextKeys, adminKeys);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task AdminIngest_ShouldCreateDedupeAndPreserveInputMetadata()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();

            var firstResponse = await client.PostAsJsonAsync("/api/admin/ingest", new ContextInputCommand
            {
                OperationId = "admin-op-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Source = "admin",
                InputKind = "note",
                Content = "admin ingest content",
                SourceRefs = ["source:admin-1"],
                Metadata = new Dictionary<string, string>
                {
                    ["custom"] = "kept"
                }
            });
            firstResponse.EnsureSuccessStatusCode();
            var first = await firstResponse.Content.ReadFromJsonAsync<ContextInputIngestionResult>();

            Assert.IsNotNull(first);
            Assert.IsTrue(first!.Created);
            Assert.IsFalse(first.Deduped);
            Assert.AreEqual("admin-op-1", first.OperationId);
            Assert.AreEqual("kept", first.Item.Metadata["custom"]);
            Assert.AreEqual("admin", first.Item.Metadata["source"]);
            Assert.IsTrue(first.SequenceId > 0);
            Assert.AreEqual(first.SequenceId.ToString(), first.Item.Metadata["sequenceId"]);

            var secondResponse = await client.PostAsJsonAsync("/api/admin/ingest", new ContextInputCommand
            {
                OperationId = "admin-op-2",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Source = "admin",
                InputKind = "note",
                Content = "admin ingest content",
                SourceRefs = ["source:admin-1"]
            });
            secondResponse.EnsureSuccessStatusCode();
            var second = await secondResponse.Content.ReadFromJsonAsync<ContextInputIngestionResult>();

            Assert.IsNotNull(second);
            Assert.IsFalse(second!.Created);
            Assert.IsTrue(second.Deduped);
            Assert.AreEqual(first.Item.Id, second.Item.Id);

            var thirdResponse = await client.PostAsJsonAsync("/api/admin/ingest", new ContextInputCommand
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Source = "admin",
                InputKind = "note",
                Content = "admin ingest content",
                SourceRefs = ["source:admin-2"]
            });
            thirdResponse.EnsureSuccessStatusCode();
            var third = await thirdResponse.Content.ReadFromJsonAsync<ContextInputIngestionResult>();

            Assert.IsNotNull(third);
            Assert.AreNotEqual(first.Item.Id, third!.Item.Id);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task AdminIngest_ShouldRejectEmptyContent_AndExposeAdminStatus()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();

            var invalidResponse = await client.PostAsJsonAsync("/api/admin/ingest", new ContextInputCommand
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Source = "admin",
                InputKind = "note",
                Content = " "
            });
            Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, invalidResponse.StatusCode);
            using var invalidJson = JsonDocument.Parse(await invalidResponse.Content.ReadAsStringAsync());
            Assert.AreEqual("validation_failed", invalidJson.RootElement.GetProperty("errorCode").GetString());
            Assert.AreEqual("admin.ingest", invalidJson.RootElement.GetProperty("target").GetString());
            Assert.IsTrue(invalidJson.RootElement.GetProperty("details").GetArrayLength() > 0);
            Assert.AreEqual("ContentRequired", invalidJson.RootElement.GetProperty("details")[0].GetProperty("code").GetString());

            var adminStatus = await client.GetFromJsonAsync<ContextCoreAdminStatusResponse>("/api/admin/status?workspaceId=workspace-test&collectionId=collection-test");
            Assert.IsNotNull(adminStatus);
            Assert.AreEqual("filesystem", adminStatus!.Storage.Provider);
            Assert.AreEqual(FileStorageOptions.ResolveRootPath(rootPath), adminStatus.Storage.RootPath);
            Assert.AreEqual("workspace-test", adminStatus.Workspace);
            Assert.AreEqual("collection-test", adminStatus.Collection);
            Assert.AreEqual("retrieval-orchestration-baseline-v1", adminStatus.RetrievalBaseline);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task AdminAndHealthEndpoints_ShouldReturnExplicitSuccessDtos()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();

            (await client.PostAsJsonAsync("/api/context/ingest", new ContextItem
            {
                Id = "backup-seed-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Type = "note",
                Content = "seed for backup",
                SourceRefs = ["source:backup-seed-1"],
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            })).EnsureSuccessStatusCode();

            var adminStatus = await client.GetFromJsonAsync<ContextCoreAdminStatusResponse>("/api/admin/status?workspaceId=workspace-test&collectionId=collection-test");
            var backupStatus = await client.GetFromJsonAsync<ContextCoreBackupStatusResponse>("/api/admin/backup/status");
            var backupCreateResponse = await client.PostAsync("/api/admin/backup/create", content: null);
            backupCreateResponse.EnsureSuccessStatusCode();
            var backupCreate = await backupCreateResponse.Content.ReadFromJsonAsync<ContextCoreBackupCreateResponse>();
            var backupValidate = await client.GetFromJsonAsync<ContextCoreBackupValidateResponse>("/api/admin/backup/validate");
            var schemaVersion = await client.GetFromJsonAsync<ContextCoreSchemaVersionResponse>("/api/admin/schema-version");
            var healthReady = await client.GetFromJsonAsync<RuntimeReadinessResponse>("/api/health/ready");

            Assert.IsNotNull(adminStatus);
            Assert.IsNotNull(backupStatus);
            Assert.IsNotNull(backupCreate);
            Assert.IsNotNull(backupValidate);
            Assert.IsNotNull(schemaVersion);
            Assert.IsNotNull(healthReady);

            Assert.AreEqual("filesystem", backupStatus!.Provider);
            Assert.IsTrue(backupStatus.Exists);
            Assert.AreEqual(FileStorageOptions.ResolveRootPath(rootPath), backupStatus.Root);
            Assert.IsFalse(string.IsNullOrWhiteSpace(backupCreate!.BackupPath));
            Assert.AreEqual("filesystem", schemaVersion!.Provider);
            Assert.IsFalse(string.IsNullOrWhiteSpace(schemaVersion.Note));
            Assert.AreEqual("ready", healthReady!.Status);
            Assert.AreEqual("filesystem", healthReady.StorageProvider);
            Assert.AreEqual("retrieval-orchestration-baseline-v1", healthReady.RetrievalBaseline);
            Assert.AreEqual("ServiceReadyAlpha", healthReady.ProviderState);
            Assert.IsTrue(healthReady.Capabilities.Any(capability =>
                capability.Name == "filesystem" && capability.Active));
            Assert.IsTrue(healthReady.Checks.All(check => !string.IsNullOrWhiteSpace(check.Severity)));
            Assert.IsTrue(healthReady.Checks.Count > 0);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task JobsAndModelRouteResolve_ShouldReturnExplicitSuccessDtos()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();
            using var scope = factory.Services.CreateScope();
            var queue = scope.ServiceProvider.GetRequiredService<IContextJobQueue>();

            var queuedJob = new ContextJob
            {
                JobId = "job-requeue-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Kind = ContextJobKind.Custom,
                PayloadJson = "{}",
                State = ContextJobState.Queued,
                MaxRetryCount = 0,
                CreatedAt = DateTimeOffset.UtcNow
            };
            await queue.EnqueueAsync(queuedJob);
            var dequeued = await queue.DequeueAsync();
            Assert.IsNotNull(dequeued);
            await queue.NackAsync(dequeued!.JobId, "simulate failure");

            var requeueResponse = await client.PostAsync($"/api/jobs/{queuedJob.JobId}/requeue", content: null);
            requeueResponse.EnsureSuccessStatusCode();
            var requeue = await requeueResponse.Content.ReadFromJsonAsync<ContextCoreRequeueJobResponse>();

            var routeResponse = await client.PostAsJsonAsync("/api/model/route/resolve", new
            {
                role = "GeneralCompression",
                taskKind = "Summarize",
                thinkingMode = "fast"
            });
            routeResponse.EnsureSuccessStatusCode();
            var route = await routeResponse.Content.ReadFromJsonAsync<ContextCoreModelRouteResolveResponse>();

            Assert.IsNotNull(requeue);
            Assert.AreEqual(queuedJob.JobId, requeue!.OriginalJobId);
            Assert.IsFalse(string.IsNullOrWhiteSpace(requeue.NewJobId));
            Assert.AreEqual(requeue.NewJobId, requeue.Job.JobId);

            Assert.IsNotNull(route);
            Assert.AreEqual("GeneralCompression", route!.Role);
            Assert.IsFalse(string.IsNullOrWhiteSpace(route.RouteSource));
            Assert.IsNotNull(route.Primary);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task StatusRelationAndModelStatusEndpoints_ShouldReturnExplicitSuccessDtos()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();
            using var scope = factory.Services.CreateScope();

            var relationStore = scope.ServiceProvider.GetRequiredService<IRelationStore>();
            await relationStore.SaveAsync(new ContextRelation
            {
                Id = "relation-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                SourceId = "item-a",
                TargetId = "item-b",
                RelationType = "references",
                Confidence = 0.9,
                SourceRefs = ["event-1"],
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["evidenceRefs"] = "event-1",
                    ["lifecycle"] = StableMemoryLifecycle.Active,
                    ["reviewStatus"] = RelationReviewStatuses.Reviewed
                },
                CreatedAt = DateTimeOffset.UtcNow
            });

            var status = await client.GetFromJsonAsync<RuntimeStatusResponse>("/api/status");
            var deepStatus = await client.GetFromJsonAsync<RuntimeReadinessResponse>("/api/status/deep");
            var relations = await client.GetFromJsonAsync<ContextCoreRelationLookupResponse>(
                "/api/relations/item-a?workspaceId=workspace-test&collectionId=collection-test");
            var relationExplain = await client.GetFromJsonAsync<RelationExplainResponse>(
                "/api/relations/relation-1/explain?workspaceId=workspace-test&collectionId=collection-test");
            var expansionProfiles = await client.GetFromJsonAsync<IReadOnlyList<RelationExpansionProfile>>(
                "/api/relations/expansion/profiles");
            var expansionPreviewResponse = await client.PostAsJsonAsync(
                "/api/relations/expansion/preview",
                new RelationExpansionPreviewRequest
                {
                    WorkspaceId = "workspace-test",
                    CollectionId = "collection-test",
                    ItemId = "item-a",
                    ProfileId = "normal-v1"
                });
            var expansionPreview = await expansionPreviewResponse.Content
                .ReadFromJsonAsync<RelationExpansionPreviewResponse>();
            var modelStatus = await client.GetFromJsonAsync<ContextCoreModelStatusResponse>("/api/model/status");

            Assert.IsNotNull(status);
            Assert.IsNotNull(deepStatus);
            Assert.IsNotNull(relations);
            Assert.IsNotNull(relationExplain);
            Assert.IsNotNull(expansionProfiles);
            Assert.IsNotNull(expansionPreview);
            Assert.IsNotNull(modelStatus);

            Assert.AreEqual("filesystem", status!.Storage.Provider);
            Assert.IsTrue(status.Readiness.Checks.Count > 0);
            Assert.AreEqual("__system__/__health__", deepStatus!.ProbeScope);
            Assert.IsTrue(deepStatus.Checks.Count > 0);
            Assert.AreEqual("item-a", relations!.ItemId);
            Assert.AreEqual(1, relations.Outgoing.Count);
            Assert.AreEqual("references", relations.Outgoing[0].RelationType);
            Assert.AreEqual("relation-1", relationExplain!.RelationId);
            Assert.AreEqual("references", relationExplain.Relation!.RelationType);
            Assert.IsTrue(expansionProfiles!.Any(profile => profile.ProfileId == "normal-v1"));
            Assert.IsTrue(expansionPreviewResponse.IsSuccessStatusCode);
            Assert.AreEqual(1, expansionPreview!.AcceptedCount);
            Assert.IsTrue(modelStatus!.ApiProviders.Count > 0);
            Assert.IsTrue(modelStatus.Models.Count > 0);
            Assert.IsTrue(modelStatus.Routes.Count > 0);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task FoundationReadOnlyStatusApis_ShouldReturnFrozenStatusWithoutRuntimeMutation()
    {
        var rootPath = CreateTestRootPath();
        Assert.IsFalse(Directory.Exists(rootPath), "Read-only API smoke 需要从干净的 filesystem root 开始。");

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();

            var endpoints = new[]
            {
                "/api/admin/foundation/status",
                "/api/admin/foundation/release-candidate",
                "/api/admin/foundation/reproducibility",
                "/api/admin/foundation/runtime-change-gate",
                "/api/admin/foundation/vector-formal-preview",
                "/api/admin/foundation/postgres-freeze-status"
            };

            foreach (var endpoint in endpoints)
            {
                var raw = await client.GetStringAsync(endpoint);
                AssertNoLocalPathLeak(raw, endpoint);
                Assert.IsFalse(raw.Contains("test-api-key-secure", StringComparison.Ordinal), endpoint);
                var envelope = JsonSerializer.Deserialize<FoundationApiResponseEnvelope<FoundationServiceStatusResponse>>(
                    raw,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));

                Assert.IsNotNull(envelope, endpoint);
                Assert.IsTrue(envelope!.Success, endpoint);
                Assert.AreEqual("foundation-api-envelope-v1", envelope.SchemaVersion, endpoint);
                Assert.IsTrue(envelope.Status is "Ready" or "Degraded", endpoint);
                Assert.IsNotNull(envelope.Data, endpoint);
                var response = envelope.Data!;
                Assert.IsTrue(response!.ReadOnly, endpoint);
                Assert.IsFalse(response.RuntimeMutated, endpoint);
                Assert.IsFalse(response.FormalRetrievalAllowed, endpoint);
                Assert.IsFalse(response.RuntimeSwitchAllowed, endpoint);
                Assert.IsFalse(response.ReadyForRuntimeSwitch, endpoint);
                Assert.IsFalse(response.PackingPolicyChanged, endpoint);
                Assert.IsFalse(response.PackageOutputChanged, endpoint);
                Assert.IsFalse(response.FormalPackageWritten, endpoint);
                Assert.IsTrue(response.Capabilities.Count >= 6, endpoint);
                Assert.IsTrue(response.Capabilities.All(static item => !item.RuntimeSwitchAllowed), endpoint);
            }

            var reportsRaw = await client.GetStringAsync("/api/admin/foundation/reports");
            AssertNoLocalPathLeak(reportsRaw, "reports");
            var reportsEnvelope = JsonSerializer.Deserialize<FoundationApiResponseEnvelope<FoundationReportNavigationResponse>>(
                reportsRaw,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.IsNotNull(reportsEnvelope);
            Assert.IsNotNull(reportsEnvelope!.Data);
            Assert.AreEqual("foundation-api-envelope-v1", reportsEnvelope.SchemaVersion);
            Assert.IsTrue(reportsEnvelope.Data!.Reports.Count > 0);
            Assert.IsTrue(reportsEnvelope.Data.Reports.All(static report => report.SafeToExpose));
            Assert.IsTrue(reportsEnvelope.Data.Reports.All(static report => !Path.IsPathRooted(report.RelativePath)));

            var firstReportId = reportsEnvelope.Data.Reports[0].ReportId;
            var detailRaw = await client.GetStringAsync($"/api/admin/foundation/reports/{firstReportId}");
            AssertNoLocalPathLeak(detailRaw, "report detail");
            var detailEnvelope = JsonSerializer.Deserialize<FoundationApiResponseEnvelope<FoundationReportNavigationEntry>>(
                detailRaw,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.IsNotNull(detailEnvelope);
            Assert.IsNotNull(detailEnvelope!.Data);
            Assert.AreEqual(firstReportId, detailEnvelope.Data!.ReportId);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task FoundationReadOnlyStatusApis_MissingReportsShouldReturnDegradedEnvelope()
    {
        var rootPath = CreateTestRootPath();
        Directory.CreateDirectory(rootPath);

        try
        {
            using var factory = CreateFactory(
                rootPath,
                jobWorkerEnabled: false,
                pollIntervalMs: 100,
                configureServices: services =>
                {
                    services.RemoveAll<FoundationStatusService>();
                    services.AddSingleton(new FoundationStatusService(rootPath));
                });
            using var client = factory.CreateClient();

            var envelope = await client.GetFromJsonAsync<FoundationApiResponseEnvelope<FoundationServiceStatusResponse>>(
                "/api/admin/foundation/status");

            Assert.IsNotNull(envelope);
            Assert.IsTrue(envelope!.Success);
            Assert.AreEqual("Degraded", envelope.Status);
            Assert.AreEqual("RegenerateReport", envelope.Recommendation);
            Assert.IsTrue(envelope.Diagnostics.TryGetValue("MissingReportIds", out var missing));
            Assert.IsTrue(missing!.Count > 0);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ServiceAlphaRuntimeSmoke_ShouldSupportStatusReadyIngestQueryAndPackageBuild()
    {
        var rootPath = CreateTestRootPath();
        Assert.IsFalse(Directory.Exists(rootPath), "Smoke test 需要从干净的 filesystem root 开始。");

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();

            var status = await client.GetFromJsonAsync<RuntimeStatusResponse>("/api/status");
            var ready = await client.GetFromJsonAsync<RuntimeReadinessResponse>("/api/health/ready");

            Assert.IsNotNull(status);
            Assert.IsNotNull(ready);
            Assert.AreEqual("ok", status!.Status);
            Assert.AreEqual("filesystem", status.Storage.Provider);
            Assert.AreEqual(FileStorageOptions.ResolveRootPath(rootPath), status.Storage.RootPath);
            Assert.IsTrue(Directory.Exists(FileStorageOptions.ResolveRootPath(rootPath)));
            Assert.AreEqual("retrieval-orchestration-baseline-v1", status.RetrievalBaseline);
            CollectionAssert.IsSubsetOf(
                new[] { "storage-root", "context-store", "memory-store", "relation-store", "job-queue", "event-sink", "model-gateway", "retrieval-baseline" },
                status.Readiness.Checks.Select(check => check.Name).ToArray());
            CollectionAssert.IsSubsetOf(
                new[] { "filesystem", "memory", "postgres", "vector-store" },
                status.Capabilities.Select(capability => capability.Name).ToArray());
            Assert.IsTrue(status.Capabilities.Any(capability =>
                capability.Name == "filesystem" && capability.State == "AlphaSupported" && capability.Active));
            Assert.IsTrue(status.Capabilities.Any(capability =>
                capability.Name == "memory" && capability.State == "TestOnly" && !capability.Active));
            Assert.IsTrue(status.Capabilities.Any(capability =>
                capability.Name == "postgres" && capability.State == "Experimental" && !capability.Active));

            Assert.AreEqual("ready", ready!.Status);
            Assert.AreEqual("filesystem", ready.StorageProvider);
            Assert.AreEqual("ServiceReadyAlpha", ready.ProviderState);
            Assert.AreEqual("retrieval-orchestration-baseline-v1", ready.RetrievalBaseline);
            Assert.IsFalse(ready.ProductionReady);
            Assert.IsFalse(ready.FromCache);
            Assert.AreEqual(8, ready.CacheTtlSeconds);
            Assert.IsTrue(ready.Capabilities.Any(capability =>
                capability.Name == "vector-store" && capability.State == "NotConfigured"));

            using var ingestResponse = await client.PostAsJsonAsync("/api/context/ingest", new ContextInputCommand
            {
                OperationId = "alpha-smoke-ingest",
                WorkspaceId = "workspace-alpha",
                CollectionId = "collection-alpha",
                Source = "service-alpha-smoke",
                InputKind = "note",
                Content = "Service Alpha runtime smoke content.",
                SourceRefs = ["source:alpha-smoke-1"]
            });
            ingestResponse.EnsureSuccessStatusCode();
            var ingestion = await ingestResponse.Content.ReadFromJsonAsync<ContextInputIngestionResult>();

            using var queryResponse = await client.PostAsJsonAsync("/api/context/query", new ContextQuery
            {
                WorkspaceId = "workspace-alpha",
                CollectionId = "collection-alpha",
                Take = 10
            });
            queryResponse.EnsureSuccessStatusCode();
            var items = await queryResponse.Content.ReadFromJsonAsync<ContextItem[]>();

            using var packageResponse = await client.PostAsJsonAsync("/api/package/build", new ContextPackageRequest
            {
                WorkspaceId = "workspace-alpha",
                CollectionId = "collection-alpha",
                QueryText = "runtime smoke",
                TokenBudget = 800
            });
            packageResponse.EnsureSuccessStatusCode();
            var package = await packageResponse.Content.ReadFromJsonAsync<ContextPackage>();

            using var invalidIngestResponse = await client.PostAsJsonAsync("/api/context/ingest", new ContextInputCommand
            {
                WorkspaceId = "workspace-alpha",
                CollectionId = "collection-alpha",
                Source = "service-alpha-smoke",
                InputKind = "note",
                Content = " "
            });
            Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, invalidIngestResponse.StatusCode);
            var error = await invalidIngestResponse.Content.ReadFromJsonAsync<ContextCoreErrorResponse>();

            Assert.IsNotNull(ingestion);
            Assert.IsTrue(ingestion!.Created);
            Assert.AreEqual("alpha-smoke-ingest", ingestion.OperationId);
            Assert.IsNotNull(items);
            Assert.IsTrue(items!.Any(item => item.Id == ingestion.Item.Id));
            Assert.IsNotNull(package);
            Assert.AreEqual("workspace-alpha", package!.WorkspaceId);
            Assert.AreEqual("collection-alpha", package.CollectionId);
            Assert.IsNotNull(error);
            Assert.AreEqual(ContextCoreErrorCodes.ValidationFailed, error!.ErrorCode);
            Assert.AreEqual("context.ingest", error.Target);
            Assert.IsTrue(error.Details.Count > 0);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task IngestSuccess_ShouldRecordShortTermRawEvent_AndDuplicateShouldRecordLightweightEvent()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();

            (await client.PostAsJsonAsync("/api/context/ingest", new ContextInputCommand
            {
                OperationId = "short-term-op-1",
                WorkspaceId = "workspace-short",
                CollectionId = "collection-short",
                SessionId = "session-1",
                Source = "service-test",
                InputKind = "note",
                Content = "short-term raw content",
                SourceRefs = ["source:short-1"]
            })).EnsureSuccessStatusCode();

            (await client.PostAsJsonAsync("/api/context/ingest", new ContextInputCommand
            {
                OperationId = "short-term-op-2",
                WorkspaceId = "workspace-short",
                CollectionId = "collection-short",
                SessionId = "session-1",
                Source = "service-test",
                InputKind = "note",
                Content = "short-term raw content",
                SourceRefs = ["source:short-1"]
            })).EnsureSuccessStatusCode();

            var rawEvents = await client.GetFromJsonAsync<ShortTermRawEvent[]>("/api/memory/short-term/raw?workspaceId=workspace-short&collectionId=collection-short&sessionId=session-1&take=10");

            Assert.IsNotNull(rawEvents);
            Assert.AreEqual(2, rawEvents!.Length);
            Assert.IsTrue(rawEvents.Any(item => item.EventKind == "ingest_succeeded" && item.Content == "short-term raw content"));
            Assert.IsTrue(rawEvents.Any(item => item.EventKind == "ingest_duplicate" && item.Content == string.Empty));
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ShortTermRawEvents_ShouldRespectWorkspaceCollectionScope_AndSummaryShouldReturnCounts()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();

            (await client.PostAsJsonAsync("/api/context/ingest", new ContextInputCommand
            {
                OperationId = "scope-op-1",
                WorkspaceId = "workspace-a",
                CollectionId = "collection-a",
                SessionId = "session-a",
                Source = "service-test",
                InputKind = "note",
                Content = "event-a",
                SourceRefs = ["source:a"]
            })).EnsureSuccessStatusCode();

            (await client.PostAsJsonAsync("/api/context/ingest", new ContextInputCommand
            {
                OperationId = "scope-op-2",
                WorkspaceId = "workspace-b",
                CollectionId = "collection-b",
                SessionId = "session-b",
                Source = "service-test",
                InputKind = "note",
                Content = "event-b",
                SourceRefs = ["source:b"]
            })).EnsureSuccessStatusCode();

            var rawA = await client.GetFromJsonAsync<ShortTermRawEvent[]>("/api/memory/short-term/raw?workspaceId=workspace-a&collectionId=collection-a&sessionId=session-a&take=10");
            var summaryA = await client.GetFromJsonAsync<ShortTermMemorySummary>("/api/memory/short-term/summary?workspaceId=workspace-a&collectionId=collection-a&sessionId=session-a&latestRawTake=10");

            Assert.IsNotNull(rawA);
            Assert.AreEqual(1, rawA!.Length);
            Assert.AreEqual("event-a", rawA[0].Content);
            Assert.IsNotNull(summaryA);
            Assert.AreEqual(1, summaryA!.RawEventCount);
            Assert.AreEqual(0, summaryA.WorkingItemCount);
            Assert.AreEqual(1, summaryA.LatestRawEvents.Count);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ShortTermSummaryEndpoint_ShouldReturnCategorizedWorkingItems()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();

            foreach (var command in new[]
            {
                new ContextInputCommand
                {
                    OperationId = "summary-working-1",
                    WorkspaceId = "workspace-short-summary",
                    CollectionId = "collection-short-summary",
                    SessionId = "session-1",
                    Source = "service-test",
                    InputKind = "task_update",
                    Content = "task item",
                    SourceRefs = ["source:summary-task"]
                },
                new ContextInputCommand
                {
                    OperationId = "summary-working-2",
                    WorkspaceId = "workspace-short-summary",
                    CollectionId = "collection-short-summary",
                    SessionId = "session-1",
                    Source = "service-test",
                    InputKind = "decision",
                    Content = "decision item",
                    SourceRefs = ["source:summary-decision"]
                },
                new ContextInputCommand
                {
                    OperationId = "summary-working-3",
                    WorkspaceId = "workspace-short-summary",
                    CollectionId = "collection-short-summary",
                    SessionId = "session-1",
                    Source = "service-test",
                    InputKind = "warning",
                    Content = "warning item",
                    SourceRefs = ["source:summary-warning"]
                }
            })
            {
                (await client.PostAsJsonAsync("/api/context/ingest", command)).EnsureSuccessStatusCode();
            }

            var summary = await client.GetFromJsonAsync<ShortTermMemorySummary>("/api/memory/short-term/summary?workspaceId=workspace-short-summary&collectionId=collection-short-summary&sessionId=session-1&latestRawTake=10");
            var working = await client.GetFromJsonAsync<ShortTermWorkingItem[]>("/api/memory/short-term/working?workspaceId=workspace-short-summary&collectionId=collection-short-summary&sessionId=session-1&take=10");

            Assert.IsNotNull(summary);
            Assert.IsNotNull(working);
            Assert.AreEqual(3, summary!.WorkingItemCount);
            Assert.AreEqual(1, summary.ActiveTaskCount);
            Assert.AreEqual(1, summary.RecentDecisionCount);
            Assert.AreEqual(1, summary.RecentWarningCount);
            Assert.AreEqual("ActiveTask", summary.ActiveTasks.Single().Kind);
            Assert.AreEqual("RecentDecision", summary.RecentDecisions.Single().Kind);
            Assert.AreEqual("RecentWarning", summary.RecentWarnings.Single().Kind);
            Assert.AreEqual(3, working!.Length);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ShortTermCompactEndpoint_AndArchiveSummary_ShouldReturnExpectedResult()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();
            using var scope = factory.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IShortTermMemoryStore>();

            await store.AppendRawEventAsync(new ShortTermRawEvent
            {
                EventId = "archive-raw-1",
                OperationId = "archive-op-1",
                WorkspaceId = "workspace-compact",
                CollectionId = "collection-compact",
                SessionId = "session-1",
                Source = "service-test",
                EventKind = "ingest_succeeded",
                Content = "old raw",
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-10),
                SequenceId = 1
            });
            await store.SaveWorkingItemAsync(new ShortTermWorkingItem
            {
                ItemId = "compact-task-1",
                WorkspaceId = "workspace-compact",
                CollectionId = "collection-compact",
                SessionId = "session-1",
                Kind = "ActiveTask",
                Title = "部署任务",
                Summary = "部署任务开始执行",
                Status = "active",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                Metadata = new Dictionary<string, string>
                {
                    ["workingKey"] = "deploy-main"
                }
            });
            await store.SaveWorkingItemAsync(new ShortTermWorkingItem
            {
                ItemId = "compact-task-2",
                WorkspaceId = "workspace-compact",
                CollectionId = "collection-compact",
                SessionId = "session-1",
                Kind = "ActiveTask",
                Title = "部署任务",
                Summary = "部署任务进入验证阶段",
                Status = "active",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                Metadata = new Dictionary<string, string>
                {
                    ["workingKey"] = "deploy-main"
                }
            });

            var compactResponse = await client.PostAsJsonAsync("/api/memory/short-term/compact", new ShortTermMemoryCompactionRequest
            {
                WorkspaceId = "workspace-compact",
                CollectionId = "collection-compact",
                SessionId = "session-1"
            });
            compactResponse.EnsureSuccessStatusCode();
            var compact = await compactResponse.Content.ReadFromJsonAsync<ShortTermMemoryCompactionResult>();
            var archive = await client.GetFromJsonAsync<ShortTermArchiveSummary>("/api/memory/short-term/archive/summary?workspaceId=workspace-compact&collectionId=collection-compact&sessionId=session-1");
            var archiveItems = await client.GetFromJsonAsync<ShortTermArchiveItemsResponse>("/api/memory/short-term/archive/items?workspaceId=workspace-compact&collectionId=collection-compact&sessionId=session-1&limit=10");
            var runs = await client.GetFromJsonAsync<ShortTermCompactionRun[]>("/api/memory/short-term/compact/runs?workspaceId=workspace-compact&collectionId=collection-compact&sessionId=session-1&take=10");
            var activeWorking = await client.GetFromJsonAsync<ShortTermWorkingItem[]>("/api/memory/short-term/working?workspaceId=workspace-compact&collectionId=collection-compact&sessionId=session-1&take=10");

            Assert.IsNotNull(compact);
            Assert.IsNotNull(archive);
            Assert.IsNotNull(archiveItems);
            Assert.IsNotNull(runs);
            Assert.IsNotNull(activeWorking);
            Assert.AreEqual(1, compact!.MergedWorkingItems);
            Assert.AreEqual(1, compact.MergedByWorkingKeyGroups);
            Assert.AreEqual(1, compact.ArchivedRawEventCount);
            Assert.AreEqual(1, archive!.ArchivedWorkingItemCount);
            Assert.AreEqual(1, archive.ArchivedRawEventCount);
            Assert.IsNotNull(compact.Run);
            Assert.AreEqual("Manual", compact.Run!.Trigger);
            Assert.AreEqual(1, archiveItems!.RawEvents.Count);
            Assert.AreEqual(1, archiveItems.WorkingItems.Count);
            Assert.AreEqual(1, runs!.Length);
            Assert.AreEqual("Manual", runs[0].Trigger);
            var runDetail = await client.GetFromJsonAsync<ShortTermCompactionRun>($"/api/memory/short-term/compact/runs/{compact.Run.RunId}");
            Assert.IsNotNull(runDetail);
            Assert.AreEqual(compact.Run.RunId, runDetail!.RunId);
            Assert.AreEqual(1, activeWorking!.Length);
            StringAssert.Contains(activeWorking[0].Summary, "验证阶段");
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ShortTermMaintenanceWorker_ShouldBeDisabledByDefault_AndExposeStateInReady()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();

            var ready = await client.GetFromJsonAsync<RuntimeReadinessResponse>("/api/health/ready");

            Assert.IsNotNull(ready);
            Assert.IsNotNull(ready!.ShortTermMaintenance);
            Assert.IsFalse(ready.ShortTermMaintenance!.Enabled);
            Assert.AreEqual(300, ready.ShortTermMaintenance.IntervalSeconds);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ShortTermMaintenanceWorker_ShouldRecordScheduledRunHistory()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(
                rootPath,
                jobWorkerEnabled: false,
                pollIntervalMs: 100,
                configureServices: services =>
                {
                    services.Configure<ShortTermMaintenanceOptions>(options =>
                    {
                        options.Enabled = true;
                        options.RunOnStartup = false;
                        options.IntervalSeconds = 1;
                    });
                });
            using var client = factory.CreateClient();

            (await client.PostAsJsonAsync("/api/context/ingest", new ContextInputCommand
            {
                OperationId = "scheduled-short-term-op",
                WorkspaceId = "workspace-scheduled",
                CollectionId = "collection-scheduled",
                SessionId = "session-1",
                Source = "service-test",
                InputKind = "note",
                Content = "worker scheduled run seed",
                SourceRefs = ["source:scheduled-1"]
            })).EnsureSuccessStatusCode();

            ShortTermCompactionRun[]? runs = null;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(6);
            while (DateTimeOffset.UtcNow < deadline)
            {
                runs = await client.GetFromJsonAsync<ShortTermCompactionRun[]>("/api/memory/short-term/compact/runs?workspaceId=workspace-scheduled&collectionId=collection-scheduled&take=10");
                if (runs is { Length: > 0 } && runs.Any(run => run.Trigger == "Scheduled"))
                {
                    break;
                }

                await Task.Delay(250);
            }

            Assert.IsNotNull(runs);
            Assert.IsTrue(runs!.Any(run => run.Trigger == "Scheduled"));

            var status = await client.GetFromJsonAsync<RuntimeStatusResponse>("/api/status");
            Assert.IsNotNull(status);
            Assert.IsNotNull(status!.ShortTermMaintenance);
            Assert.IsTrue(status.ShortTermMaintenance!.Enabled);
            Assert.IsNotNull(status.ShortTermMaintenance.LastRun);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ShortTermPromotionCandidateEndpoint_ShouldGenerateAndReturnCandidates()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();
            using var scope = factory.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IShortTermMemoryStore>();

            await store.SaveWorkingItemAsync(new ShortTermWorkingItem
            {
                ItemId = "decision-candidate-1",
                WorkspaceId = "workspace-candidates",
                CollectionId = "collection-candidates",
                SessionId = "session-1",
                Kind = "RecentDecision",
                Title = "决定统一错误响应契约",
                Summary = "决定统一错误响应契约",
                Status = "recorded",
                Lifecycle = "Recent",
                Importance = 0.88,
                Refs = ["event-1"],
                SourceRefs = ["source:decision-candidate-1"],
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                UpdatedAt = DateTimeOffset.UtcNow
            });

            var generateResponse = await client.PostAsJsonAsync("/api/memory/short-term/promotion/candidates/generate", new ShortTermPromotionCandidateGenerationRequest
            {
                WorkspaceId = "workspace-candidates",
                CollectionId = "collection-candidates",
                SessionId = "session-1"
            });
            generateResponse.EnsureSuccessStatusCode();
            var generated = await generateResponse.Content.ReadFromJsonAsync<ShortTermPromotionCandidate[]>();
            var listed = await client.GetFromJsonAsync<ShortTermPromotionCandidate[]>("/api/memory/short-term/promotion/candidates?workspaceId=workspace-candidates&collectionId=collection-candidates&sessionId=session-1&status=Candidate&take=10");

            Assert.IsNotNull(generated);
            Assert.IsNotNull(listed);
            Assert.AreEqual(1, generated!.Length);
            Assert.AreEqual("CandidateMemory", generated[0].SuggestedTargetLayer);
            Assert.IsTrue(generated[0].EvidenceRefs.Count > 0);
            Assert.IsFalse(string.IsNullOrWhiteSpace(generated[0].Reason));
            Assert.AreEqual(1, listed!.Length);

            var detail = await client.GetFromJsonAsync<ShortTermPromotionCandidate>($"/api/memory/short-term/promotion/candidates/{generated[0].CandidateId}");
            Assert.IsNotNull(detail);
            Assert.AreEqual(generated[0].CandidateId, detail!.CandidateId);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ShortTermPromotionCandidateQuery_ShouldSupportFilters_AndPagination()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();
            using var scope = factory.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IShortTermMemoryStore>();

            await store.SaveWorkingItemAsync(new ShortTermWorkingItem
            {
                ItemId = "decision-filter-1",
                WorkspaceId = "workspace-filter",
                CollectionId = "collection-filter",
                SessionId = "session-1",
                Kind = "RecentDecision",
                Title = "保留显式 DTO",
                Summary = "保留显式 DTO",
                Status = "recorded",
                Lifecycle = "Recent",
                Importance = 0.91,
                Refs = ["event-1"],
                SourceRefs = ["source:decision-filter-1"],
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
            });
            await store.SaveWorkingItemAsync(new ShortTermWorkingItem
            {
                ItemId = "issue-filter-1",
                WorkspaceId = "workspace-filter",
                CollectionId = "collection-filter",
                SessionId = "session-1",
                Kind = "KnownIssue",
                Title = "缓存击穿",
                Summary = "缓存击穿",
                Status = "open",
                Lifecycle = "Tracked",
                Importance = 0.83,
                Refs = ["event-2"],
                SourceRefs = ["source:issue-filter-1"],
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            await store.SaveWorkingItemAsync(new ShortTermWorkingItem
            {
                ItemId = "question-filter-1",
                WorkspaceId = "workspace-filter",
                CollectionId = "collection-filter",
                SessionId = "session-1",
                Kind = "OpenQuestion",
                Title = "是否保留旧接口",
                Summary = "是否保留旧接口",
                Status = "open",
                Lifecycle = "Open",
                Importance = 0.82,
                Refs = ["event-3"],
                SourceRefs = ["source:question-filter-1"],
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            (await client.PostAsJsonAsync("/api/memory/short-term/promotion/candidates/generate", new ShortTermPromotionCandidateGenerationRequest
            {
                WorkspaceId = "workspace-filter",
                CollectionId = "collection-filter",
                SessionId = "session-1"
            })).EnsureSuccessStatusCode();

            var candidateStore = scope.ServiceProvider.GetRequiredService<IShortTermPromotionCandidateStore>();
            await candidateStore.SaveAsync(new ShortTermPromotionCandidate
            {
                CandidateId = "rejected-candidate-1",
                WorkspaceId = "workspace-filter",
                CollectionId = "collection-filter",
                SessionId = "session-1",
                SourceWorkingItemId = "manual",
                Kind = "KnownIssue",
                Title = "手动拒绝候选项",
                Summary = "手动拒绝候选项",
                SuggestedTargetLayer = "CandidateMemory",
                Reason = "manual seed",
                Confidence = 0.2,
                Importance = 0.2,
                EvidenceRefs = ["manual"],
                Tags = ["manual"],
                Status = PromotionCandidateStatus.Rejected,
                CreatedAt = DateTimeOffset.UtcNow
            });

            var statusFiltered = await client.GetFromJsonAsync<ShortTermPromotionCandidate[]>("/api/memory/short-term/promotion/candidates?workspaceId=workspace-filter&collectionId=collection-filter&sessionId=session-1&status=Candidate&limit=10&offset=0");
            var targetFiltered = await client.GetFromJsonAsync<ShortTermPromotionCandidate[]>("/api/memory/short-term/promotion/candidates?workspaceId=workspace-filter&collectionId=collection-filter&sessionId=session-1&suggestedTargetLayer=CandidateMemory&limit=10&offset=0");
            var paged = await client.GetFromJsonAsync<ShortTermPromotionCandidate[]>("/api/memory/short-term/promotion/candidates?workspaceId=workspace-filter&collectionId=collection-filter&sessionId=session-1&limit=1&offset=1");

            Assert.IsNotNull(statusFiltered);
            Assert.IsNotNull(targetFiltered);
            Assert.IsNotNull(paged);
            Assert.IsTrue(statusFiltered!.Length >= 2);
            Assert.IsTrue(statusFiltered.All(candidate => candidate.Status == PromotionCandidateStatus.Candidate));
            Assert.IsFalse(statusFiltered.Any(candidate => candidate.CandidateId == "rejected-candidate-1"));
            Assert.IsTrue(targetFiltered!.All(candidate => candidate.SuggestedTargetLayer == "CandidateMemory"));
            Assert.AreEqual(1, paged!.Length);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ShortTermPromotionCandidateExplain_ShouldReturnSourceWorkingItem_Evidence_AndRuleInfo()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();

            (await client.PostAsJsonAsync("/api/context/ingest", new ContextInputCommand
            {
                OperationId = "explain-op-1",
                WorkspaceId = "workspace-explain",
                CollectionId = "collection-explain",
                SessionId = "session-1",
                Source = "service-test",
                InputKind = "decision",
                Content = "决定保留显式 DTO",
                SourceRefs = ["source:explain-1"]
            })).EnsureSuccessStatusCode();

            (await client.PostAsJsonAsync("/api/memory/short-term/promotion/candidates/generate", new ShortTermPromotionCandidateGenerationRequest
            {
                WorkspaceId = "workspace-explain",
                CollectionId = "collection-explain",
                SessionId = "session-1"
            })).EnsureSuccessStatusCode();

            var candidates = await client.GetFromJsonAsync<ShortTermPromotionCandidate[]>("/api/memory/short-term/promotion/candidates?workspaceId=workspace-explain&collectionId=collection-explain&sessionId=session-1&limit=10&offset=0");
            Assert.IsNotNull(candidates);
            var candidateId = candidates![0].CandidateId;

            var explanation = await client.GetFromJsonAsync<ShortTermPromotionCandidateExplanation>($"/api/memory/short-term/promotion/candidates/{candidateId}/explain");

            Assert.IsNotNull(explanation);
            Assert.AreEqual(candidateId, explanation!.CandidateId);
            Assert.AreEqual(candidates[0].SourceWorkingItemId, explanation.SourceWorkingItem.ItemId);
            Assert.IsTrue(explanation.SourceRawEvents.Count > 0);
            Assert.IsTrue(explanation.EvidenceRefs.Count > 0);
            Assert.IsFalse(string.IsNullOrWhiteSpace(explanation.RuleName));
            Assert.IsFalse(string.IsNullOrWhiteSpace(explanation.RuleVersion));
            Assert.IsFalse(string.IsNullOrWhiteSpace(explanation.PolicyVersion));
            Assert.IsFalse(string.IsNullOrWhiteSpace(explanation.DedupeKey));
            Assert.IsFalse(string.IsNullOrWhiteSpace(explanation.GeneratedBy));
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ShortTermPromotionCandidateExplain_NotFound_ShouldReturnStructuredError()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/api/memory/short-term/promotion/candidates/missing/explain");
            Assert.AreEqual(System.Net.HttpStatusCode.NotFound, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<ContextCoreErrorResponse>();

            Assert.IsNotNull(error);
            Assert.AreEqual(ContextCoreErrorCodes.NotFound, error!.ErrorCode);
            Assert.AreEqual("memory.short-term.promotion.candidate.explain", error.Target);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ShortTermPromotionCandidateReviewEndpoints_ShouldAcceptRejectExpire_AndReturnHistory()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();
            using var scope = factory.Services.CreateScope();
            var shortTermStore = scope.ServiceProvider.GetRequiredService<IShortTermMemoryStore>();
            var memoryStore = scope.ServiceProvider.GetRequiredService<IMemoryStore>();
            var constraintStore = scope.ServiceProvider.GetRequiredService<IConstraintStore>();
            var relationStore = scope.ServiceProvider.GetRequiredService<IRelationStore>();

            await shortTermStore.SaveWorkingItemAsync(CreateShortTermWorkingItem("decision-review-1", "RecentDecision", "决定保留 review history", "recorded", 0.92, ["event-review-1"]));
            await shortTermStore.SaveWorkingItemAsync(CreateShortTermWorkingItem("constraint-review-1", "TemporaryConstraint", "输出必须保持中文", "active", 0.91, ["event-review-2"]));
            await shortTermStore.SaveWorkingItemAsync(CreateShortTermWorkingItem("issue-review-1", "KnownIssue", "缓存击穿", "open", 0.88, ["event-review-3"]));
            await shortTermStore.SaveWorkingItemAsync(CreateShortTermWorkingItem("question-review-1", "OpenQuestion", "是否保留旧接口？", "open", 0.86, ["event-review-4"]));

            (await client.PostAsJsonAsync("/api/memory/short-term/promotion/candidates/generate", new ShortTermPromotionCandidateGenerationRequest
            {
                WorkspaceId = "workspace-review",
                CollectionId = "collection-review",
                SessionId = "session-1"
            })).EnsureSuccessStatusCode();

            var candidates = await client.GetFromJsonAsync<ShortTermPromotionCandidate[]>("/api/memory/short-term/promotion/candidates?workspaceId=workspace-review&collectionId=collection-review&sessionId=session-1&limit=10&offset=0");
            Assert.IsNotNull(candidates);
            var decision = candidates!.Single(item => item.Kind == "RecentDecision");
            var constraintCandidate = candidates.Single(item => item.Kind == "TemporaryConstraint");
            var issue = candidates.Single(item => item.Kind == "KnownIssue");
            var question = candidates.Single(item => item.Kind == "OpenQuestion");

            var acceptResponse = await client.PostAsJsonAsync($"/api/memory/short-term/promotion/candidates/{decision.CandidateId}/accept", CreateReviewRequest("accept-endpoint-1", "接受为候选记忆。"));
            acceptResponse.EnsureSuccessStatusCode();
            var accepted = await acceptResponse.Content.ReadFromJsonAsync<PromotionCandidateReviewResult>();
            Assert.IsNotNull(accepted);
            Assert.AreEqual(PromotionCandidateStatus.Accepted, accepted!.Status);
            Assert.IsFalse(string.IsNullOrWhiteSpace(accepted.TargetItemId));
            Assert.AreEqual(accepted.TargetItemId, accepted.CreatedTargetItemId);
            Assert.AreEqual("service-test", accepted.Reviewer);
            Assert.AreEqual("接受为候选记忆。", accepted.Reason);
            Assert.AreNotEqual(default, accepted.ReviewedAt);

            var targetMemory = await memoryStore.GetAsync("workspace-review", "collection-review", accepted.TargetItemId!);
            Assert.IsNotNull(targetMemory);
            Assert.AreEqual(ContextMemoryLayer.Structured, targetMemory!.Layer);
            Assert.AreEqual(ContextMemoryStatus.Candidate, targetMemory.Status);
            Assert.AreEqual(decision.CandidateId, targetMemory.Metadata["sourceCandidateId"]);
            Assert.AreNotEqual(ContextMemoryLayer.Stable, targetMemory.Layer);
            CollectionAssert.Contains(targetMemory.SourceRefs.ToArray(), decision.CandidateId);
            CollectionAssert.Contains(targetMemory.SourceRefs.ToArray(), "event-review-1");

            var relations = await relationStore.QueryForItemAsync("workspace-review", "collection-review", targetMemory.Id);
            Assert.IsTrue(relations.Any(item => item.RelationType == ContextRelationTypes.PromotedFrom));
            Assert.IsTrue(relations.Any(item => item.RelationType == ContextRelationTypes.EvidenceFor));

            var constraintAcceptResponse = await client.PostAsJsonAsync($"/api/memory/short-term/promotion/candidates/{constraintCandidate.CandidateId}/accept", CreateReviewRequest("accept-constraint-1", "接受为约束候选。"));
            constraintAcceptResponse.EnsureSuccessStatusCode();
            var acceptedConstraint = await constraintAcceptResponse.Content.ReadFromJsonAsync<PromotionCandidateReviewResult>();
            var constraints = await constraintStore.QueryAsync(new ContextConstraintQuery
            {
                WorkspaceId = "workspace-review",
                CollectionId = "collection-review",
                Status = ContextMemoryStatus.Candidate,
                Take = 10
            });
            Assert.IsNotNull(acceptedConstraint);
            Assert.IsTrue(constraints.Any(item => item.Id == acceptedConstraint!.TargetItemId));

            (await client.PostAsJsonAsync($"/api/memory/short-term/promotion/candidates/{issue.CandidateId}/reject", CreateReviewRequest("reject-endpoint-1", "暂不保留。"))).EnsureSuccessStatusCode();
            (await client.PostAsJsonAsync($"/api/memory/short-term/promotion/candidates/{question.CandidateId}/expire", CreateReviewRequest("expire-endpoint-1", "问题已过期。"))).EnsureSuccessStatusCode();

            var issueDetail = await client.GetFromJsonAsync<ShortTermPromotionCandidate>($"/api/memory/short-term/promotion/candidates/{issue.CandidateId}");
            var questionDetail = await client.GetFromJsonAsync<ShortTermPromotionCandidate>($"/api/memory/short-term/promotion/candidates/{question.CandidateId}");
            var reviews = await client.GetFromJsonAsync<PromotionCandidateReviewRecord[]>($"/api/memory/short-term/promotion/candidates/{decision.CandidateId}/reviews");
            var feedback = await client.GetFromJsonAsync<PromotionFeedbackSignal[]>($"/api/learning/feedback?workspaceId=workspace-review&collectionId=collection-review&candidateId={decision.CandidateId}&action=Accepted&limit=10&offset=0");

            Assert.AreEqual(PromotionCandidateStatus.Rejected, issueDetail!.Status);
            Assert.AreEqual(PromotionCandidateStatus.Expired, questionDetail!.Status);
            Assert.IsNotNull(reviews);
            Assert.AreEqual(1, reviews!.Length);
            Assert.AreEqual("accept", reviews[0].Action);
            Assert.AreEqual("service-test", reviews[0].Reviewer);
            Assert.AreEqual("接受为候选记忆。", reviews[0].Reason);
            Assert.AreNotEqual(default, reviews[0].ReviewedAt);
            Assert.IsNotNull(feedback);
            Assert.AreEqual(1, feedback!.Length);
            Assert.AreEqual("Accepted", feedback[0].Action);
            Assert.AreEqual(accepted.CreatedTargetItemId, feedback[0].CreatedTargetItemId);
            Assert.AreEqual(decision.SuggestedTargetLayer, feedback[0].SuggestedTargetLayer);

            var duplicateAccept = await client.PostAsJsonAsync($"/api/memory/short-term/promotion/candidates/{decision.CandidateId}/accept", CreateReviewRequest("accept-endpoint-duplicate", "重复接受。"));
            Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, duplicateAccept.StatusCode);
            var duplicateError = await duplicateAccept.Content.ReadFromJsonAsync<ContextCoreErrorResponse>();
            Assert.IsNotNull(duplicateError);
            Assert.AreEqual(ContextCoreErrorCodes.InvalidRequest, duplicateError!.ErrorCode);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task LearningEndpoints_ShouldReturnRecordsCases_AndStructuredNotFound()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();
            using var scope = factory.Services.CreateScope();
            var shortTermStore = scope.ServiceProvider.GetRequiredService<IShortTermMemoryStore>();

            await shortTermStore.SaveWorkingItemAsync(CreateShortTermWorkingItem("decision-learning-endpoint", "RecentDecision", "学习正样本", "recorded", 0.92, ["event-learning-positive"]));
            await shortTermStore.SaveWorkingItemAsync(CreateShortTermWorkingItem("issue-learning-endpoint", "KnownIssue", "误报候选", "open", 0.88, ["event-learning-negative"]));
            await shortTermStore.SaveWorkingItemAsync(CreateShortTermWorkingItem("question-learning-endpoint", "OpenQuestion", "过期候选", "open", 0.84, ["event-learning-stale"]));

            (await client.PostAsJsonAsync("/api/memory/short-term/promotion/candidates/generate", new ShortTermPromotionCandidateGenerationRequest
            {
                WorkspaceId = "workspace-review",
                CollectionId = "collection-review",
                SessionId = "session-1"
            })).EnsureSuccessStatusCode();

            var candidates = await client.GetFromJsonAsync<ShortTermPromotionCandidate[]>("/api/memory/short-term/promotion/candidates?workspaceId=workspace-review&collectionId=collection-review&sessionId=session-1&limit=10&offset=0");
            Assert.IsNotNull(candidates);
            var decision = candidates!.Single(item => item.SourceWorkingItemId == "decision-learning-endpoint");
            var issue = candidates.Single(item => item.SourceWorkingItemId == "issue-learning-endpoint");
            var question = candidates.Single(item => item.SourceWorkingItemId == "question-learning-endpoint");

            (await client.PostAsJsonAsync($"/api/memory/short-term/promotion/candidates/{decision.CandidateId}/accept", CreateReviewRequest("learning-endpoint-accept", "接受为正样本。"))).EnsureSuccessStatusCode();
            (await client.PostAsJsonAsync($"/api/memory/short-term/promotion/candidates/{issue.CandidateId}/reject", CreateReviewRequest("learning-endpoint-reject", "误报。"))).EnsureSuccessStatusCode();
            (await client.PostAsJsonAsync($"/api/memory/short-term/promotion/candidates/{question.CandidateId}/expire", CreateReviewRequest("learning-endpoint-expire", "已过期。"))).EnsureSuccessStatusCode();

            var records = await client.GetFromJsonAsync<ContextLearningRecord[]>("/api/learning/records?workspaceId=workspace-review&collectionId=collection-review&limit=10&offset=0");
            var feedback = await client.GetFromJsonAsync<PromotionFeedbackSignal[]>("/api/learning/feedback?workspaceId=workspace-review&collectionId=collection-review&limit=10&offset=0");
            var acceptedFeedback = await client.GetFromJsonAsync<PromotionFeedbackSignal[]>("/api/learning/feedback?workspaceId=workspace-review&collectionId=collection-review&action=Accepted&limit=10&offset=0");
            var negativeRecords = await client.GetFromJsonAsync<ContextLearningRecord[]>("/api/learning/records?workspaceId=workspace-review&collectionId=collection-review&signal=Negative&limit=10&offset=0");
            var cases = await client.GetFromJsonAsync<ContextLearningCase[]>("/api/learning/cases?workspaceId=workspace-review&collectionId=collection-review&limit=10&offset=0");
            var falsePositiveCases = await client.GetFromJsonAsync<ContextLearningCase[]>("/api/learning/cases?workspaceId=workspace-review&collectionId=collection-review&failureType=PromotionFalsePositive&limit=10&offset=0");
            var policyFeedback = await client.GetFromJsonAsync<PolicyFeedbackDataset>("/api/learning/policy-feedback?workspaceId=workspace-review&collectionId=collection-review&limit=10&offset=0");
            var policyFeedbackExport = await client.GetStringAsync("/api/learning/policy-feedback/export?workspaceId=workspace-review&collectionId=collection-review&limit=10&offset=0");
            var learningFeatures = await client.GetFromJsonAsync<LearningFeatureDataset>("/api/learning/features?workspaceId=workspace-review&collectionId=collection-review&limit=10&offset=0");
            var learningFeatureQuality = await client.GetFromJsonAsync<LearningDatasetQualityReport>("/api/learning/features/quality");

            Assert.IsNotNull(feedback);
            Assert.AreEqual(2, feedback!.Length);
            Assert.IsTrue(feedback.Any(item => item.Action == "Accepted" && item.CandidateId == decision.CandidateId));
            Assert.IsTrue(feedback.Any(item => item.Action == "Rejected" && item.CandidateId == issue.CandidateId));
            Assert.IsNotNull(acceptedFeedback);
            Assert.AreEqual(1, acceptedFeedback!.Length);
            Assert.IsNotNull(records);
            Assert.AreEqual(3, records!.Length);
            Assert.IsTrue(records.Any(record => record.Signal == ContextFeedbackSignal.Positive && record.EventKind == "PromotionAccepted"));
            Assert.IsTrue(records.Any(record => record.Signal == ContextFeedbackSignal.Negative && record.FailureType == ContextFailureType.PromotionFalsePositive));
            Assert.IsTrue(records.Any(record => record.Signal == ContextFeedbackSignal.Stale && record.EventKind == "PromotionExpired"));
            Assert.IsTrue(records.All(record => record.EvidenceRefs.Count > 0));
            Assert.IsTrue(records.All(record => record.Metadata.ContainsKey("sourceWorkingItemId")));
            Assert.IsNotNull(negativeRecords);
            Assert.AreEqual(1, negativeRecords!.Length);
            Assert.IsNotNull(policyFeedback);
            Assert.AreEqual(1, policyFeedback!.PositiveCount);
            Assert.AreEqual(1, policyFeedback.NegativeCount);
            Assert.AreEqual(1, policyFeedback.NeutralCount);
            Assert.AreEqual(3, policyFeedback.SourceTypes["PromotionCandidateReviewRecord"]);
            Assert.IsTrue(policyFeedback.Records.Any(record => record.Label == PolicyFeedbackLabels.Positive));
            Assert.IsTrue(policyFeedback.Records.Any(record => record.Label == PolicyFeedbackLabels.Negative));
            Assert.IsTrue(policyFeedbackExport.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length >= 3);
            Assert.IsNotNull(learningFeatures);
            Assert.AreEqual(3, learningFeatures!.FeatureCount);
            Assert.IsTrue(learningFeatures.RankingPairCount >= 0);
            Assert.IsTrue(learningFeatures.RouterIntentExampleCount >= 0);
            Assert.AreEqual(PolicyFeedbackLabels.Positive, learningFeatures.FeatureExamples.First(item => item.Accepted).Label);
            Assert.IsTrue(learningFeatures.LabelDistribution.ContainsKey(PolicyFeedbackLabels.Positive));
            Assert.IsTrue(learningFeatures.SourceTypeDistribution.ContainsKey("PromotionCandidateReviewRecord"));
            Assert.IsNotNull(learningFeatureQuality);
            Assert.IsTrue(learningFeatureQuality!.PolicyFeedbackFeatureCount >= 0);
            Assert.IsTrue(learningFeatureQuality.TaskReadiness.ContainsKey(LearningDatasetTaskNames.RouterIntentClassifier));

            Assert.IsNotNull(cases);
            Assert.AreEqual(3, cases!.Length);
            Assert.IsTrue(cases.Any(item => item.CaseKind == "PositivePromotionSample"));
            Assert.IsTrue(cases.Any(item => item.CaseKind == "PromotionFalsePositive"));
            Assert.IsTrue(cases.Any(item => item.CaseKind == "StaleContextSample"));
            Assert.IsTrue(cases.All(item => item.Status == ContextLearningCaseStatus.Draft));
            Assert.IsTrue(cases.All(item => item.EvidenceRefs.Count > 0));
            Assert.IsTrue(cases.Any(item => item.CaseKind == "PositivePromotionSample"
                && item.SourceType == "PromotionFeedbackSignal"
                && item.PositiveRefs.Count > 0
                && !string.IsNullOrWhiteSpace(item.ExpectedBehavior)));
            Assert.IsTrue(cases.Any(item => item.CaseKind == "PromotionFalsePositive"
                && item.NegativeRefs.Count > 0
                && !string.IsNullOrWhiteSpace(item.CorrectionReason)));
            Assert.IsNotNull(falsePositiveCases);
            Assert.AreEqual(1, falsePositiveCases!.Length);

            var generatedResponse = await client.PostAsJsonAsync("/api/learning/cases/generate", new ContextLearningCaseGenerationRequest
            {
                WorkspaceId = "workspace-review",
                CollectionId = "collection-review",
                Limit = 10
            });
            generatedResponse.EnsureSuccessStatusCode();
            var generated = await generatedResponse.Content.ReadFromJsonAsync<ContextLearningCaseGenerationResult>();
            Assert.IsNotNull(generated);
            Assert.AreEqual(3, generated!.RecordsScanned);
            Assert.AreEqual(0, generated.Created);
            Assert.AreEqual(3, generated.Existing);

            var positiveCase = cases.Single(item => item.CaseKind == "PositivePromotionSample");
            var negativeCase = cases.Single(item => item.CaseKind == "PromotionFalsePositive");
            var activateResponse = await client.PostAsJsonAsync($"/api/learning/cases/{positiveCase.CaseId}/activate", new ContextLearningCaseStatusUpdateRequest
            {
                OperationId = "learning-case-activate",
                Reviewer = "tester",
                Reason = "纳入回归。"
            });
            activateResponse.EnsureSuccessStatusCode();
            var activated = await activateResponse.Content.ReadFromJsonAsync<ContextLearningCaseStatusUpdateResponse>();
            Assert.AreEqual(ContextLearningCaseStatus.ActiveRegression, activated!.Status);

            var rejectCaseResponse = await client.PostAsJsonAsync($"/api/learning/cases/{negativeCase.CaseId}/reject", new ContextLearningCaseStatusUpdateRequest
            {
                OperationId = "learning-case-reject",
                Reviewer = "tester",
                Reason = "不进入回归。"
            });
            rejectCaseResponse.EnsureSuccessStatusCode();
            var rejectedCase = await rejectCaseResponse.Content.ReadFromJsonAsync<ContextLearningCaseStatusUpdateResponse>();
            Assert.AreEqual(ContextLearningCaseStatus.Rejected, rejectedCase!.Status);

            var regressionCases = await client.GetFromJsonAsync<ContextLearningCase[]>("/api/learning/regression/cases?workspaceId=workspace-review&collectionId=collection-review&limit=10&offset=0");
            Assert.IsNotNull(regressionCases);
            Assert.AreEqual(1, regressionCases!.Length);
            Assert.AreEqual(positiveCase.CaseId, regressionCases[0].CaseId);
            Assert.IsFalse(regressionCases.Any(item => item.CaseId == negativeCase.CaseId));

            var summary = await client.GetFromJsonAsync<ContextLearningSummary>("/api/learning/summary?workspaceId=workspace-review&collectionId=collection-review");
            Assert.IsNotNull(summary);
            Assert.AreEqual(3, summary!.RecordCount);
            Assert.AreEqual(3, summary.CaseCount);
            Assert.AreEqual(1, summary.PositiveCount);
            Assert.AreEqual(1, summary.NegativeCount);
            Assert.AreEqual(1, summary.StaleCount);
            Assert.AreEqual(1, summary.ActiveRegressionCaseCount);
            Assert.AreEqual(1, summary.RejectedCaseCount);

            var recordDetail = await client.GetFromJsonAsync<ContextLearningRecord>($"/api/learning/records/{records[0].RecordId}");
            var caseDetail = await client.GetFromJsonAsync<ContextLearningCase>($"/api/learning/cases/{cases[0].CaseId}");
            Assert.AreEqual(records[0].RecordId, recordDetail!.RecordId);
            Assert.AreEqual(cases[0].CaseId, caseDetail!.CaseId);

            var createdCaseResponse = await client.PostAsJsonAsync("/api/learning/cases", new ContextLearningCase
            {
                CaseId = "manual-learning-case-1",
                WorkspaceId = "workspace-review",
                CollectionId = "collection-review",
                SourceRecordId = records[0].RecordId,
                SourceKind = "manual",
                SourceId = "manual-1",
                CaseKind = "ManualLearningCase",
                Title = "手工学习案例",
                Summary = "手工学习案例",
                Signal = ContextFeedbackSignal.Positive,
                FailureType = ContextFailureType.None,
                EvidenceRefs = ["manual-evidence"],
                CreatedAt = DateTimeOffset.UtcNow
            });
            createdCaseResponse.EnsureSuccessStatusCode();
            var createdCase = await createdCaseResponse.Content.ReadFromJsonAsync<ContextLearningCase>();
            Assert.AreEqual("manual-learning-case-1", createdCase!.CaseId);

            using var missingRecordResponse = await client.GetAsync("/api/learning/records/missing-record");
            Assert.AreEqual(System.Net.HttpStatusCode.NotFound, missingRecordResponse.StatusCode);
            var missingRecordError = await missingRecordResponse.Content.ReadFromJsonAsync<ContextCoreErrorResponse>();
            Assert.AreEqual(ContextCoreErrorCodes.NotFound, missingRecordError!.ErrorCode);
            Assert.AreEqual("learning.record", missingRecordError.Target);

            using var missingCaseResponse = await client.GetAsync("/api/learning/cases/missing-case");
            Assert.AreEqual(System.Net.HttpStatusCode.NotFound, missingCaseResponse.StatusCode);
            var missingCaseError = await missingCaseResponse.Content.ReadFromJsonAsync<ContextCoreErrorResponse>();
            Assert.AreEqual(ContextCoreErrorCodes.NotFound, missingCaseError!.ErrorCode);
            Assert.AreEqual("learning.case", missingCaseError.Target);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task CandidateMemoryReviewEndpoint_InvalidTransition_ShouldReturnContextCoreErrorResponse()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();
            using var scope = factory.Services.CreateScope();
            var memoryStore = scope.ServiceProvider.GetRequiredService<IMemoryStore>();

            await memoryStore.SaveAsync(new ContextMemoryItem
            {
                Id = "stable-memory-1",
                WorkspaceId = "workspace-candidate-review",
                CollectionId = "collection-candidate-review",
                Layer = ContextMemoryLayer.Stable,
                Status = ContextMemoryStatus.Stable,
                Type = "preference",
                Content = "Already stable memory.",
                SourceRefs = ["event-stable-1"],
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["evidenceRefs"] = "event-stable-1"
                },
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            using var response = await client.PostAsJsonAsync(
                "/api/memory/candidates/stable-memory-1/reject",
                new CandidateMemoryReviewRequest
                {
                    OperationId = "candidate-memory-invalid-transition",
                    WorkspaceId = "workspace-candidate-review",
                    CollectionId = "collection-candidate-review",
                    Reviewer = "service-test",
                    Reason = "should fail"
                });

            Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<ContextCoreErrorResponse>();
            Assert.IsNotNull(error);
            Assert.AreEqual(ContextCoreErrorCodes.InvalidRequest, error!.ErrorCode);
            Assert.AreEqual("memory.candidates.reject", error.Target);
            StringAssert.Contains(error.Message, "Stable");
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task StableLifecycleReviewEndpoint_InvalidTransition_ShouldReturnContextCoreErrorResponse()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();
            using var scope = factory.Services.CreateScope();
            var memoryStore = scope.ServiceProvider.GetRequiredService<IMemoryStore>();

            await memoryStore.SaveAsync(new ContextMemoryItem
            {
                Id = "stable-memory-rejected",
                WorkspaceId = "workspace-stable-review",
                CollectionId = "collection-stable-review",
                Layer = ContextMemoryLayer.Stable,
                Status = ContextMemoryStatus.Rejected,
                Type = "preference",
                Content = "Rejected stable memory should not be rejected twice.",
                SourceRefs = ["event-stable-rejected"],
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["evidenceRefs"] = "event-stable-rejected"
                },
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            using var response = await client.PostAsJsonAsync(
                "/api/memory/stable/stable-memory-rejected/reject",
                new StableLifecycleReviewRequest
                {
                    OperationId = "stable-lifecycle-invalid-transition",
                    WorkspaceId = "workspace-stable-review",
                    CollectionId = "collection-stable-review",
                    Reviewer = "service-test",
                    Reason = "should fail"
                });

            Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<ContextCoreErrorResponse>();
            Assert.IsNotNull(error);
            Assert.AreEqual(ContextCoreErrorCodes.InvalidRequest, error!.ErrorCode);
            Assert.AreEqual("memory.stable.reject", error.Target);
            StringAssert.Contains(error.Message, "Rejected");
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task PlanningSnapshotEndpoint_ShouldReturnAggregatedSnapshot()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();
            using var scope = factory.Services.CreateScope();
            var shortTermStore = scope.ServiceProvider.GetRequiredService<IShortTermMemoryStore>();
            var memoryStore = scope.ServiceProvider.GetRequiredService<IMemoryStore>();
            var constraintStore = scope.ServiceProvider.GetRequiredService<IConstraintStore>();
            var learningStore = scope.ServiceProvider.GetRequiredService<IContextLearningStore>();
            var now = DateTimeOffset.UtcNow;

            await shortTermStore.SaveWorkingItemAsync(new ShortTermWorkingItem
            {
                ItemId = "task-planning-endpoint",
                WorkspaceId = "workspace-review",
                CollectionId = "collection-review",
                SessionId = "session-1",
                Kind = "ActiveTask",
                Title = "Planning endpoint",
                Summary = "只读 planning snapshot endpoint。",
                Status = "active",
                Lifecycle = "Active",
                Importance = 0.91,
                Tags = ["ActiveTask"],
                CreatedAt = now,
                UpdatedAt = now
            });
            await constraintStore.SaveAsync(new ContextConstraint
            {
                Id = "constraint-planning-endpoint",
                WorkspaceId = "workspace-review",
                CollectionId = "collection-review",
                Scope = ContextScope.Collection,
                Level = ConstraintLevel.Hard,
                Content = "planning snapshot 不影响 retrieval/package。",
                Status = ContextMemoryStatus.Stable,
                Confidence = 1,
                CreatedAt = now,
                UpdatedAt = now
            });
            await memoryStore.SaveAsync(new ContextMemoryItem
            {
                Id = "decision-planning-endpoint",
                WorkspaceId = "workspace-review",
                CollectionId = "collection-review",
                Layer = ContextMemoryLayer.Stable,
                Status = ContextMemoryStatus.Stable,
                Type = "decision",
                Content = "Planning snapshot is read-only.",
                Tags = ["DecisionRecord"],
                Importance = 0.86,
                CreatedAt = now,
                UpdatedAt = now
            });
            await learningStore.AddRecordAsync(new ContextLearningRecord
            {
                RecordId = "record-planning-endpoint",
                WorkspaceId = "workspace-review",
                CollectionId = "collection-review",
                SessionId = "session-1",
                SourceKind = "test",
                SourceId = "source-planning",
                EventKind = "PromotionAccepted",
                Signal = ContextFeedbackSignal.Positive,
                FailureType = ContextFailureType.None,
                CreatedAt = now
            });

            var snapshot = await client.GetFromJsonAsync<ContextPlanningSnapshot>(
                "/api/context/planning/snapshot?workspaceId=workspace-review&collectionId=collection-review&sessionId=session-1");

            Assert.IsNotNull(snapshot);
            Assert.AreEqual("workspace-review", snapshot!.WorkspaceId);
            Assert.AreEqual("task-planning-endpoint", snapshot.ActiveTasks.Single().ItemId);
            Assert.AreEqual("constraint-planning-endpoint", snapshot.StableConstraints.Single().Id);
            Assert.AreEqual("decision-planning-endpoint", snapshot.DecisionRecords.Single().Id);
            Assert.AreEqual(1, snapshot.LearningSignalsSummary.RecordCount);
            Assert.AreEqual("context-planning-snapshot-policy/v1", snapshot.PolicyVersion);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task PlanningProposalEndpoint_ShouldReturnProposalWithoutExecutingRetrieval()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();
            using var scope = factory.Services.CreateScope();
            var shortTermStore = scope.ServiceProvider.GetRequiredService<IShortTermMemoryStore>();
            var now = DateTimeOffset.UtcNow;

            await shortTermStore.SaveWorkingItemAsync(new ShortTermWorkingItem
            {
                ItemId = "task-planning-proposal",
                WorkspaceId = "workspace-review",
                CollectionId = "collection-review",
                SessionId = "session-1",
                Kind = "ActiveTask",
                Title = "P2 proposal endpoint",
                Summary = "只读生成 retrieval proposal。",
                Status = "active",
                Lifecycle = "Active",
                Importance = 0.91,
                Tags = ["ActiveTask"],
                CreatedAt = now,
                UpdatedAt = now
            });

            using var response = await client.PostAsJsonAsync(
                "/api/context/planning/propose",
                new ContextPlanningProposalRequest
                {
                    WorkspaceId = "workspace-review",
                    CollectionId = "collection-review",
                    SessionId = "session-1",
                    CurrentInput = "修复 dotnet test 失败",
                    Mode = "Coding"
                });

            response.EnsureSuccessStatusCode();
            var proposal = await response.Content.ReadFromJsonAsync<RetrievalPlanProposal>();

            Assert.IsNotNull(proposal);
            Assert.AreEqual("workspace-review", proposal!.WorkspaceId);
            Assert.AreEqual("collection-review", proposal.CollectionId);
            Assert.AreEqual(PlanningIntentDetector.CodingTask, proposal.Intent);
            Assert.AreEqual("Coding", proposal.Mode);
            Assert.IsTrue(proposal.UseKeyword);
            Assert.IsTrue(proposal.UseWorkingMemory);
            Assert.IsFalse(proposal.UseVector);
            Assert.AreEqual(0, proposal.VectorTopK);
            StringAssert.Contains(string.Join('\n', proposal.Reasons), "snapshot.activeTask:task-planning-proposal");
            CollectionAssert.Contains(proposal.Warnings.ToArray(), "previewOnly: proposal does not execute retrieval or mutate retrieval output");
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ConstraintGapEndpoints_ShouldGenerateQueryDetail_AndNotWriteConstraintStore()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();
            using var scope = factory.Services.CreateScope();
            var constraintStore = scope.ServiceProvider.GetRequiredService<IConstraintStore>();
            var reportPath = Path.Combine(rootPath, "reports", "planning-constraint-report.json");
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new PlanningOptInConstraintSafetyReport
            {
                ReportId = "constraint-gap-report-op",
                SampleSet = "unit",
                TotalSamples = 1,
                Samples =
                [
                    new PlanningOptInConstraintSafetySample
                    {
                        SampleId = "constraint-gap-sample",
                        Mode = "ChatMode",
                        Intent = PlanningIntentDetector.CurrentTask,
                        OptInMatched = true,
                        Applied = false,
                        FallbackUsed = true,
                        ExpectedHardConstraints = ["重复解释不应提升"],
                        MissingConstraints = ["重复解释不应提升"],
                        ConstraintSource = "eval.expectedConstraints",
                        LostAtStage = "ConstraintNotRetrieved",
                        SuggestedFix = "generate corpus gap",
                        ConstraintRepairStatus = "ConstraintRepairFailed"
                    }
                ]
            }));

            var generateResponse = await client.PostAsJsonAsync("/api/constraints/gaps/generate", new ConstraintGapGenerationRequest
            {
                WorkspaceId = "workspace-review",
                CollectionId = "collection-review",
                PlanningConstraintReportPath = reportPath,
                IncludeExtendedFailureTriageReport = false
            });
            generateResponse.EnsureSuccessStatusCode();
            var generated = await generateResponse.Content.ReadFromJsonAsync<ConstraintGapGenerationResult>();
            Assert.IsNotNull(generated);
            Assert.AreEqual(1, generated!.CreatedCount);
            Assert.AreEqual(1, generated.Gaps.Count);

            var listed = await client.GetFromJsonAsync<ConstraintGapCandidate[]>(
                "/api/constraints/gaps?workspaceId=workspace-review&collectionId=collection-review&status=Pending&limit=10&offset=0");
            Assert.IsNotNull(listed);
            Assert.AreEqual(1, listed!.Length);
            Assert.AreEqual("constraint-gap-sample", listed[0].SourceSampleId);

            var detail = await client.GetFromJsonAsync<ConstraintGapCandidate>($"/api/constraints/gaps/{listed[0].GapId}");
            Assert.IsNotNull(detail);
            Assert.AreEqual("重复解释不应提升", detail!.ExpectedConstraintText);
            CollectionAssert.Contains(detail.EvidenceRefs.ToArray(), "eval:planning-optin-constraint-safety-report:constraint-gap-sample");

            var constraints = await constraintStore.QueryAsync(new ContextConstraintQuery
            {
                WorkspaceId = "workspace-review",
                CollectionId = "collection-review",
                Level = ConstraintLevel.Hard,
                Take = 10
            });
            Assert.AreEqual(0, constraints.Count);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ConstraintGapReviewEndpoints_ShouldAcceptCreateCandidateConstraint_AndReturnReviews()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();
            using var scope = factory.Services.CreateScope();
            var constraintStore = scope.ServiceProvider.GetRequiredService<IConstraintStore>();
            var reportPath = Path.Combine(rootPath, "reports", "planning-constraint-review-report.json");
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new PlanningOptInConstraintSafetyReport
            {
                ReportId = "constraint-gap-review-report-op",
                SampleSet = "unit",
                TotalSamples = 1,
                Samples =
                [
                    new PlanningOptInConstraintSafetySample
                    {
                        SampleId = "constraint-gap-review-sample",
                        Mode = "ChatMode",
                        Intent = PlanningIntentDetector.CurrentTask,
                        OptInMatched = true,
                        Applied = false,
                        FallbackUsed = true,
                        ExpectedHardConstraints = ["恢复点必须保留"],
                        MissingConstraints = ["恢复点必须保留"],
                        ConstraintSource = "eval.expectedConstraints",
                        LostAtStage = "ConstraintStoreMissing",
                        SuggestedFix = "review gap",
                        ConstraintRepairStatus = "ConstraintRepairFailed"
                    }
                ]
            }));

            var generatedResponse = await client.PostAsJsonAsync("/api/constraints/gaps/generate", new ConstraintGapGenerationRequest
            {
                WorkspaceId = "workspace-review",
                CollectionId = "collection-review",
                PlanningConstraintReportPath = reportPath,
                IncludeExtendedFailureTriageReport = false
            });
            generatedResponse.EnsureSuccessStatusCode();
            var generated = await generatedResponse.Content.ReadFromJsonAsync<ConstraintGapGenerationResult>();
            Assert.IsNotNull(generated);
            var gap = generated!.Gaps.Single();

            var acceptResponse = await client.PostAsJsonAsync($"/api/constraints/gaps/{gap.GapId}/accept", new ConstraintGapReviewRequest
            {
                OperationId = "constraint-gap-accept-endpoint",
                Reviewer = "reviewer-endpoint",
                Reason = "接受为候选约束。"
            });
            acceptResponse.EnsureSuccessStatusCode();
            var accepted = await acceptResponse.Content.ReadFromJsonAsync<ConstraintGapReviewResult>();
            Assert.IsNotNull(accepted);
            Assert.AreEqual(ConstraintGapStatus.Accepted, accepted!.Status);
            Assert.IsFalse(string.IsNullOrWhiteSpace(accepted.CreatedConstraintId));

            var reviews = await client.GetFromJsonAsync<ConstraintGapReviewRecord[]>($"/api/constraints/gaps/{gap.GapId}/reviews");
            Assert.IsNotNull(reviews);
            Assert.AreEqual(1, reviews!.Length);
            Assert.AreEqual("accept", reviews[0].Action);

            var constraints = await constraintStore.QueryAsync(new ContextConstraintQuery
            {
                WorkspaceId = "workspace-review",
                CollectionId = "collection-review",
                Status = ContextMemoryStatus.Candidate,
                Take = 10
            });
            var created = constraints.Single(item => item.Id == accepted.CreatedConstraintId);
            Assert.AreEqual(ContextMemoryStatus.Candidate, created.Status);
            Assert.AreEqual(ConstraintLevel.User, created.Level);
            Assert.AreEqual("constraint_gap_accept", created.Metadata["createdFrom"]);
            Assert.AreEqual(gap.GapId, created.Metadata["sourceConstraintGapId"]);
            Assert.AreEqual("constraint-gap-review-sample", created.Metadata["sourceSampleId"]);
            Assert.AreEqual("constraint-gap-review-report-op", created.Metadata["sourceOperationId"]);
            Assert.AreEqual("恢复点必须保留", created.Metadata["expectedConstraintText"]);
            Assert.AreEqual("reviewer-endpoint", created.Metadata["reviewer"]);
            Assert.AreEqual("接受为候选约束。", created.Metadata["reviewReason"]);
            Assert.AreEqual("Candidate", created.Metadata["status"]);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task CandidateConstraintEndpoints_ShouldListActivateAndReturnReviews()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();
            using var scope = factory.Services.CreateScope();
            var constraintStore = scope.ServiceProvider.GetRequiredService<IConstraintStore>();
            var reportPath = Path.Combine(rootPath, "reports", "candidate-constraint-review-report.json");
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new PlanningOptInConstraintSafetyReport
            {
                ReportId = "candidate-constraint-review-op",
                SampleSet = "unit",
                TotalSamples = 1,
                Samples =
                [
                    new PlanningOptInConstraintSafetySample
                    {
                        SampleId = "candidate-constraint-review-sample",
                        Mode = "ChatMode",
                        Intent = PlanningIntentDetector.CurrentTask,
                        OptInMatched = true,
                        Applied = false,
                        FallbackUsed = true,
                        ExpectedHardConstraints = ["输出必须使用中文"],
                        MissingConstraints = ["输出必须使用中文"],
                        ConstraintSource = "eval.expectedConstraints",
                        LostAtStage = "ConstraintStoreMissing",
                        SuggestedFix = "review gap",
                        ConstraintRepairStatus = "ConstraintRepairFailed"
                    }
                ]
            }));

            var generatedResponse = await client.PostAsJsonAsync("/api/constraints/gaps/generate", new ConstraintGapGenerationRequest
            {
                WorkspaceId = "workspace-review",
                CollectionId = "collection-review",
                PlanningConstraintReportPath = reportPath,
                IncludeExtendedFailureTriageReport = false
            });
            generatedResponse.EnsureSuccessStatusCode();
            var generated = await generatedResponse.Content.ReadFromJsonAsync<ConstraintGapGenerationResult>();
            Assert.IsNotNull(generated);
            var gap = generated!.Gaps.Single();

            var acceptResponse = await client.PostAsJsonAsync($"/api/constraints/gaps/{gap.GapId}/accept", new ConstraintGapReviewRequest
            {
                OperationId = "candidate-constraint-gap-accept",
                Reviewer = "gap-reviewer",
                Reason = "先创建候选约束。"
            });
            acceptResponse.EnsureSuccessStatusCode();
            var acceptedGap = await acceptResponse.Content.ReadFromJsonAsync<ConstraintGapReviewResult>();
            Assert.IsNotNull(acceptedGap);
            var candidateConstraintId = acceptedGap!.CreatedConstraintId;
            Assert.IsFalse(string.IsNullOrWhiteSpace(candidateConstraintId));

            var candidates = await client.GetFromJsonAsync<ContextConstraint[]>(
                "/api/constraints/candidates?workspaceId=workspace-review&collectionId=collection-review&status=Candidate&limit=10&offset=0");
            Assert.IsNotNull(candidates);
            var candidate = candidates!.Single(item => item.Id == candidateConstraintId);
            Assert.AreEqual(ContextMemoryStatus.Candidate, candidate.Status);
            Assert.AreEqual(ConstraintLevel.User, candidate.Level);

            var detail = await client.GetFromJsonAsync<ContextConstraint>($"/api/constraints/candidates/{candidateConstraintId}");
            Assert.IsNotNull(detail);
            Assert.AreEqual("constraint_gap_accept", detail!.Metadata["createdFrom"]);
            Assert.AreEqual(gap.GapId, detail.Metadata["sourceConstraintGapId"]);

            var activateResponse = await client.PostAsJsonAsync($"/api/constraints/candidates/{candidateConstraintId}/activate", new CandidateConstraintReviewRequest
            {
                OperationId = "candidate-constraint-activate",
                Reviewer = "constraint-reviewer",
                Reason = "人工确认硬约束。"
            });
            activateResponse.EnsureSuccessStatusCode();
            var activated = await activateResponse.Content.ReadFromJsonAsync<CandidateConstraintReviewResult>();
            Assert.IsNotNull(activated);
            Assert.AreEqual(ContextMemoryStatus.Active, activated!.Status);
            Assert.AreEqual(candidateConstraintId, activated.ActivatedConstraintId);
            Assert.AreEqual("ActiveHardConstraint", activated.TargetLayer);
            Assert.AreEqual("candidate_constraint_activate", activated.Constraint.Metadata["createdFrom"]);
            Assert.AreEqual(gap.GapId, activated.Constraint.Metadata["sourceConstraintGapId"]);
            Assert.AreEqual("candidate-constraint-review-sample", activated.Constraint.Metadata["sourceSampleId"]);
            Assert.AreEqual("candidate-constraint-review-op", activated.Constraint.Metadata["sourceOperationId"]);

            var reviews = await client.GetFromJsonAsync<CandidateConstraintReviewRecord[]>($"/api/constraints/candidates/{candidateConstraintId}/reviews");
            Assert.IsNotNull(reviews);
            Assert.AreEqual(1, reviews!.Length);
            Assert.AreEqual("activate", reviews[0].Action);

            var activeConstraints = await constraintStore.QueryAsync(new ContextConstraintQuery
            {
                WorkspaceId = "workspace-review",
                CollectionId = "collection-review",
                Level = ConstraintLevel.Hard,
                Status = ContextMemoryStatus.Active,
                Take = 10
            });
            var active = activeConstraints.Single(item => item.Id == candidateConstraintId);
            Assert.AreEqual(ContextMemoryStatus.Active, active.Status);
            Assert.AreEqual(ConstraintLevel.Hard, active.Level);
            Assert.AreEqual("candidate_constraint_activate", active.Metadata["createdFrom"]);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task StableReviewCandidateEndpoints_ShouldGenerateExplain_AndReturnStructuredNotFound()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();
            using var scope = factory.Services.CreateScope();
            var shortTermStore = scope.ServiceProvider.GetRequiredService<IShortTermMemoryStore>();
            var memoryStore = scope.ServiceProvider.GetRequiredService<IMemoryStore>();

            await shortTermStore.SaveWorkingItemAsync(CreateShortTermWorkingItem("stable-decision-endpoint", "RecentDecision", "稳定评审正样本", "recorded", 0.92, ["event-stable-endpoint"]));
            await shortTermStore.SaveWorkingItemAsync(CreateShortTermWorkingItem("stable-reject-endpoint", "RecentDecision", "被拒绝不进入稳定评审", "recorded", 0.90, ["event-stable-reject"]));

            (await client.PostAsJsonAsync("/api/memory/short-term/promotion/candidates/generate", new ShortTermPromotionCandidateGenerationRequest
            {
                WorkspaceId = "workspace-review",
                CollectionId = "collection-review",
                SessionId = "session-1"
            })).EnsureSuccessStatusCode();

            var promotionCandidates = await client.GetFromJsonAsync<ShortTermPromotionCandidate[]>("/api/memory/short-term/promotion/candidates?workspaceId=workspace-review&collectionId=collection-review&sessionId=session-1&limit=10&offset=0");
            Assert.IsNotNull(promotionCandidates);
            var acceptedSource = promotionCandidates!.Single(item => item.SourceWorkingItemId == "stable-decision-endpoint");
            var rejectedSource = promotionCandidates.Single(item => item.SourceWorkingItemId == "stable-reject-endpoint");

            (await client.PostAsJsonAsync($"/api/memory/short-term/promotion/candidates/{acceptedSource.CandidateId}/accept", CreateReviewRequest("stable-review-accept", "进入稳定评审准备。"))).EnsureSuccessStatusCode();
            (await client.PostAsJsonAsync($"/api/memory/short-term/promotion/candidates/{rejectedSource.CandidateId}/reject", CreateReviewRequest("stable-review-reject", "不进入稳定评审。"))).EnsureSuccessStatusCode();

            var generateResponse = await client.PostAsJsonAsync("/api/memory/stable-review/candidates/generate", new StableReviewCandidateGenerationRequest
            {
                WorkspaceId = "workspace-review",
                CollectionId = "collection-review",
                SessionId = "session-1",
                Limit = 10
            });
            generateResponse.EnsureSuccessStatusCode();
            var generated = await generateResponse.Content.ReadFromJsonAsync<StableReviewCandidate[]>();
            Assert.IsNotNull(generated);
            Assert.AreEqual(1, generated!.Length);
            Assert.AreEqual(acceptedSource.CandidateId, generated[0].SourceCandidateId);
            Assert.AreEqual("StableMemory", generated[0].SuggestedStableTarget);
            Assert.AreEqual(StableReviewValidationStatuses.Ready, generated[0].ValidationStatus);
            Assert.AreEqual(StableReviewCandidateStatuses.Candidate, generated[0].Status);
            CollectionAssert.Contains(generated[0].EvidenceRefs.ToArray(), "event-stable-endpoint");

            var listed = await client.GetFromJsonAsync<StableReviewCandidate[]>("/api/memory/stable-review/candidates?workspaceId=workspace-review&collectionId=collection-review&sessionId=session-1&validationStatus=ReadyForReview&limit=10&offset=0");
            Assert.IsNotNull(listed);
            Assert.AreEqual(1, listed!.Length);

            var detail = await client.GetFromJsonAsync<StableReviewCandidate>($"/api/memory/stable-review/candidates/{generated[0].StableReviewCandidateId}");
            var explanation = await client.GetFromJsonAsync<StableReviewCandidateExplanation>($"/api/memory/stable-review/candidates/{generated[0].StableReviewCandidateId}/explain");
            Assert.AreEqual(generated[0].StableReviewCandidateId, detail!.StableReviewCandidateId);
            Assert.IsNotNull(explanation);
            Assert.AreEqual(acceptedSource.CandidateId, explanation!.SourceCandidate.CandidateId);
            Assert.IsNotNull(explanation.SourceLearningCase);
            Assert.IsNotNull(explanation.SourceMemoryTarget);
            CollectionAssert.Contains(explanation.EvidenceRefs.ToArray(), "event-stable-endpoint");

            var acceptStableResponse = await client.PostAsJsonAsync($"/api/memory/stable-review/candidates/{generated[0].StableReviewCandidateId}/accept", new StableReviewDecisionRequest
            {
                OperationId = "stable-review-final-accept",
                Reviewer = "tester",
                Reason = "进入稳定记忆。"
            });
            acceptStableResponse.EnsureSuccessStatusCode();
            var stableAccept = await acceptStableResponse.Content.ReadFromJsonAsync<StableReviewDecisionResult>();
            Assert.IsNotNull(stableAccept);
            Assert.AreEqual(StableReviewCandidateStatuses.Accepted, stableAccept!.Status);
            Assert.IsFalse(string.IsNullOrWhiteSpace(stableAccept.CreatedStableTargetItemId));

            var stableItems = await memoryStore.QueryAsync(new ContextMemoryQuery
            {
                WorkspaceId = "workspace-review",
                CollectionId = "collection-review",
                Layer = ContextMemoryLayer.Stable,
                Status = ContextMemoryStatus.Stable,
                Take = 10
            });
            Assert.IsTrue(stableItems.Any(item => string.Equals(item.Id, stableAccept.CreatedStableTargetItemId, StringComparison.OrdinalIgnoreCase)));

            var stableReviews = await client.GetFromJsonAsync<StableReviewRecord[]>($"/api/memory/stable-review/candidates/{generated[0].StableReviewCandidateId}/reviews");
            Assert.IsNotNull(stableReviews);
            Assert.AreEqual(1, stableReviews!.Length);
            Assert.AreEqual("accept", stableReviews[0].Action);

            using var missingResponse = await client.GetAsync("/api/memory/stable-review/candidates/missing-stable-review");
            Assert.AreEqual(System.Net.HttpStatusCode.NotFound, missingResponse.StatusCode);
            var missingError = await missingResponse.Content.ReadFromJsonAsync<ContextCoreErrorResponse>();
            Assert.IsNotNull(missingError);
            Assert.AreEqual(ContextCoreErrorCodes.NotFound, missingError!.ErrorCode);
            Assert.AreEqual("memory.stable-review.candidate", missingError.Target);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task VectorLifecycleMetadataReviewCandidateEndpoints_ShouldGenerateExplain_AndReturnStructuredNotFound()
    {
        var rootPath = CreateTestRootPath();
        var repairPlanPath = Path.Combine(
            "vector",
            "eligibility",
            $"vector-lifecycle-metadata-review-candidate-api-test-{Guid.NewGuid():N}.json");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(repairPlanPath)!);
            await File.WriteAllTextAsync(repairPlanPath, JsonSerializer.Serialize(CreateVectorLifecycleRepairPlanSummary(), new JsonSerializerOptions { WriteIndented = true }));

            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();

            var generateResponse = await client.PostAsJsonAsync("/api/vector/lifecycle-metadata/review-candidates/generate", new VectorLifecycleMetadataReviewCandidateGenerationRequest
            {
                WorkspaceId = "workspace-vector-review",
                CollectionId = "collection-vector-review",
                RepairPlanReportPath = repairPlanPath
            });
            generateResponse.EnsureSuccessStatusCode();
            var generated = await generateResponse.Content.ReadFromJsonAsync<VectorLifecycleMetadataReviewCandidateGenerationResult>();
            Assert.IsNotNull(generated);
            Assert.AreEqual(1, generated!.CandidateCount);
            var candidateId = generated.Candidates.Single().CandidateId;

            var listed = await client.GetFromJsonAsync<VectorLifecycleMetadataReviewCandidate[]>("/api/vector/lifecycle-metadata/review-candidates?workspaceId=workspace-vector-review&collectionId=collection-vector-review&status=PendingReview&limit=10&offset=0");
            Assert.IsNotNull(listed);
            Assert.AreEqual(1, listed!.Length);

            var detail = await client.GetFromJsonAsync<VectorLifecycleMetadataReviewCandidate>($"/api/vector/lifecycle-metadata/review-candidates/{candidateId}");
            var explanation = await client.GetFromJsonAsync<VectorLifecycleMetadataReviewCandidateExplanation>($"/api/vector/lifecycle-metadata/review-candidates/{candidateId}/explain");
            Assert.IsNotNull(detail);
            Assert.IsNotNull(explanation);
            Assert.AreEqual(candidateId, detail!.CandidateId);
            CollectionAssert.Contains(explanation!.EvidenceRefs.ToArray(), "evidence-api");
            CollectionAssert.Contains(explanation.RiskIfRejected.ToArray(), "RecallRemainsBlockedByLifecycleMetadata");

            var approveResponse = await client.PostAsJsonAsync($"/api/vector/lifecycle-metadata/review-candidates/{candidateId}/approve", new VectorLifecycleMetadataReviewRequest
            {
                Reviewer = "api-reviewer",
                Reason = "api approval",
                ProposedLifecycle = detail.ProposedLifecycle,
                ProposedReviewStatus = detail.ProposedReviewStatus,
                ProposedTargetSection = detail.ProposedTargetSection,
                EvidenceRefs = detail.EvidenceRefs,
                SourceRefs = detail.SourceRefs,
                Confirmed = true
            });
            approveResponse.EnsureSuccessStatusCode();
            var approveResult = await approveResponse.Content.ReadFromJsonAsync<VectorLifecycleMetadataReviewResult>();
            Assert.IsNotNull(approveResult);
            Assert.IsTrue(approveResult!.Succeeded);
            Assert.IsTrue(approveResult.SidecarWritten);

            var reviews = await client.GetFromJsonAsync<VectorLifecycleMetadataReviewRecord[]>($"/api/vector/lifecycle-metadata/review-candidates/{candidateId}/reviews");
            Assert.IsNotNull(reviews);
            Assert.AreEqual(1, reviews!.Length);

            var sidecars = await client.GetFromJsonAsync<VectorLifecycleSidecarMetadataEntry[]>("/api/vector/lifecycle-metadata/sidecar?workspaceId=workspace-vector-review&collectionId=collection-vector-review");
            Assert.IsNotNull(sidecars);
            Assert.AreEqual(1, sidecars!.Length);
            Assert.AreEqual("item-api", sidecars[0].ItemId);

            var missingResponse = await client.GetAsync("/api/vector/lifecycle-metadata/review-candidates/missing-candidate");
            Assert.AreEqual(System.Net.HttpStatusCode.NotFound, missingResponse.StatusCode);
            var error = await missingResponse.Content.ReadFromJsonAsync<ContextCoreErrorResponse>();
            Assert.IsNotNull(error);
            Assert.AreEqual(ContextCoreErrorCodes.NotFound, error!.ErrorCode);
        }
        finally
        {
            if (File.Exists(repairPlanPath))
            {
                File.Delete(repairPlanPath);
            }

            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ShortTermPromotionCandidateReview_NotFound_ShouldReturnStructuredError()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();

            using var response = await client.PostAsJsonAsync("/api/memory/short-term/promotion/candidates/missing/accept", CreateReviewRequest("accept-missing", "missing"));
            Assert.AreEqual(System.Net.HttpStatusCode.NotFound, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<ContextCoreErrorResponse>();

            Assert.IsNotNull(error);
            Assert.AreEqual(ContextCoreErrorCodes.NotFound, error!.ErrorCode);
            Assert.AreEqual("memory.short-term.promotion.candidate.accept", error.Target);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task Status_ShouldRemainReadOnly_AndNotCreateHealthScopeData()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();

            var response = await client.GetFromJsonAsync<RuntimeStatusResponse>("/api/status");

            Assert.IsNotNull(response);
            Assert.IsTrue(Directory.Exists(FileStorageOptions.ResolveRootPath(rootPath)));
            Assert.IsFalse(Directory.Exists(GetSystemHealthCollectionPath(rootPath)));
            Assert.IsFalse(Directory.Exists(GetSystemHealthLogsPath(rootPath)));
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task Ready_ShouldNotPolluteBusinessData_ByDefault()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();

            var first = await client.GetFromJsonAsync<RuntimeReadinessResponse>("/api/health/ready");
            var second = await client.GetFromJsonAsync<RuntimeReadinessResponse>("/api/health/ready");

            Assert.IsNotNull(first);
            Assert.IsNotNull(second);
            Assert.IsFalse(Directory.Exists(GetSystemHealthCollectionPath(rootPath)));
            Assert.IsFalse(Directory.Exists(GetSystemHealthLogsPath(rootPath)));
            Assert.IsFalse(first!.FromCache);
            Assert.IsTrue(second!.FromCache);
            Assert.AreEqual(8, second.CacheTtlSeconds);
            Assert.IsTrue(first.Checks.Any(check =>
                check.Name == "storage-root" && check.HasSideEffect));
            Assert.IsTrue(first.Checks.Any(check =>
                check.Name == "context-store" && !check.HasSideEffect));
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task DeepProbe_ShouldUseSystemHealthScope_AndSupportRefresh()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();

            var first = await client.GetFromJsonAsync<RuntimeReadinessResponse>("/api/status/deep");
            var second = await client.GetFromJsonAsync<RuntimeReadinessResponse>("/api/status/deep");
            var refreshed = await client.GetFromJsonAsync<RuntimeReadinessResponse>("/api/status/deep?refresh=true");

            Assert.IsNotNull(first);
            Assert.IsNotNull(second);
            Assert.IsNotNull(refreshed);
            Assert.AreEqual("__system__/__health__", first!.ProbeScope);
            Assert.IsFalse(first.FromCache);
            Assert.IsTrue(second!.FromCache);
            Assert.AreEqual(8, second.CacheTtlSeconds);
            Assert.AreEqual(first.CheckedAt, second!.CheckedAt);
            Assert.IsTrue(refreshed!.CheckedAt >= second.CheckedAt);
            Assert.IsTrue(Directory.Exists(GetSystemHealthCollectionPath(rootPath)));
            Assert.IsTrue(first.Checks.All(check => check.HasSideEffect));
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task Ready_ShouldReturnStorageUnavailable_WhenStorageRootIsMissing()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();

            Directory.Delete(FileStorageOptions.ResolveRootPath(rootPath), recursive: true);

            using var response = await client.GetAsync("/api/health/ready");
            Assert.AreEqual(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<ContextCoreErrorResponse>();

            Assert.IsNotNull(error);
            Assert.AreEqual(ContextCoreErrorCodes.StorageUnavailable, error!.ErrorCode);
            Assert.AreEqual("health.ready", error.Target);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task Ready_ShouldTreatModelGatewayUnavailableAsWarning_NotFatal()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(
                rootPath,
                jobWorkerEnabled: false,
                pollIntervalMs: 100,
                configureServices: services =>
                {
                    services.RemoveAll<ModelGatewayOptions>();
                    services.AddSingleton(new ModelGatewayOptions
                    {
                        Models =
                        [
                            new ModelEndpointOptions
                            {
                                Name = "disabled-model",
                                Provider = "mock",
                                Enabled = false
                            }
                        ]
                    });
                });
            using var client = factory.CreateClient();

            var ready = await client.GetFromJsonAsync<RuntimeReadinessResponse>("/api/health/ready");

            Assert.IsNotNull(ready);
            Assert.AreEqual("ready", ready!.Status);
            var modelCheck = ready.Checks.Single(check => check.Name == "model-gateway");
            Assert.AreEqual("warning", modelCheck.Status);
            Assert.AreEqual("warning", modelCheck.Severity);
            Assert.IsTrue(ready.Warnings.Count > 0);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ContextIngest_ShouldRejectEmptyContent_WithStructuredFailure()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: false, pollIntervalMs: 100);
            using var client = factory.CreateClient();

            var invalidResponse = await client.PostAsJsonAsync("/api/context/ingest", new ContextInputCommand
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Source = "chat",
                InputKind = "note",
                Content = " "
            });

            Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, invalidResponse.StatusCode);
            using var invalidJson = JsonDocument.Parse(await invalidResponse.Content.ReadAsStringAsync());
            Assert.AreEqual("validation_failed", invalidJson.RootElement.GetProperty("errorCode").GetString());
            Assert.AreEqual("context.ingest", invalidJson.RootElement.GetProperty("target").GetString());
            Assert.IsTrue(invalidJson.RootElement.GetProperty("details").GetArrayLength() > 0);
            Assert.AreEqual("ContentRequired", invalidJson.RootElement.GetProperty("details")[0].GetProperty("code").GetString());
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task JobWorker_ShouldProcessCompressionJob()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            using var factory = CreateFactory(rootPath, jobWorkerEnabled: true, pollIntervalMs: 100);
            using var client = factory.CreateClient();

            var input = new ContextItem
            {
                Id = "job-input-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Type = "note",
                Content = "后台作业压缩测试内容。",
                Tags = ["job", "compression"],
                SourceRefs = ["source:job-input-1"],
                Importance = 0.8,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            (await client.PostAsJsonAsync("/api/context/ingest", input)).EnsureSuccessStatusCode();

            var enqueueResponse = await client.PostAsJsonAsync("/api/jobs/compression", new CompressionRequest
            {
                OperationId = "job-operation-1",
                WorkspaceId = input.WorkspaceId,
                CollectionId = input.CollectionId,
                TaskKind = CompressionTaskKind.Summarize,
                Options = new CompressionOptions
                {
                    GenerateIndexHints = true,
                    PreserveSourceRefs = true
                }
            });
            enqueueResponse.EnsureSuccessStatusCode();

            var queuedJob = await enqueueResponse.Content.ReadFromJsonAsync<ContextJob>();
            Assert.IsNotNull(queuedJob);

            var completedJob = await WaitForJobAsync(client, queuedJob!.JobId, TimeSpan.FromSeconds(8));
            Assert.IsNotNull(completedJob);
            Assert.AreEqual(ContextJobState.Succeeded, completedJob!.State);

            var queryResponse = await client.PostAsJsonAsync("/api/context/query", new ContextQuery
            {
                WorkspaceId = input.WorkspaceId,
                CollectionId = input.CollectionId,
                Types = ["summary"],
                Take = 10,
                IncludeContent = true
            });
            queryResponse.EnsureSuccessStatusCode();

            var summaries = await queryResponse.Content.ReadFromJsonAsync<ContextItem[]>();
            Assert.IsNotNull(summaries);
            Assert.IsTrue(summaries!.Any(summary => summary.Type == "summary"));
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public void Service_ShouldFailFastWhenPostgresProviderIsConfigured()
    {
        using var factory = CreateFactory(
            CreateTestRootPath(),
            jobWorkerEnabled: false,
            pollIntervalMs: 100,
            storageProvider: "postgres");

        var exception = Assert.ThrowsException<InvalidOperationException>(() => factory.CreateClient());
        StringAssert.Contains(exception.Message, "PostgresConnectionString");
    }

    private static async Task<ContextJob?> WaitForJobAsync(
        HttpClient client,
        string jobId,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var job = await client.GetFromJsonAsync<ContextJob>($"/api/jobs/{jobId}");
            if (job is not null && job.State is ContextJobState.Succeeded or ContextJobState.Failed or ContextJobState.RequiresReview)
            {
                return job;
            }

            await Task.Delay(150);
        }

        return await client.GetFromJsonAsync<ContextJob>($"/api/jobs/{jobId}");
    }

    private static ShortTermWorkingItem CreateShortTermWorkingItem(
        string itemId,
        string kind,
        string summary,
        string status,
        double importance,
        IReadOnlyList<string> refs)
    {
        return new ShortTermWorkingItem
        {
            ItemId = itemId,
            WorkspaceId = "workspace-review",
            CollectionId = "collection-review",
            SessionId = "session-1",
            Kind = kind,
            Title = summary,
            Summary = summary,
            Status = status,
            Lifecycle = "Recent",
            Importance = importance,
            Tags = [kind],
            Refs = refs.ToArray(),
            SourceRefs = [$"source:{itemId}"],
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static ReviewPromotionCandidateRequest CreateReviewRequest(string operationId, string reason)
    {
        return new ReviewPromotionCandidateRequest
        {
            OperationId = operationId,
            Reviewer = "service-test",
            Reason = reason,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["test"] = "service-api"
            }
        };
    }

    private static VectorLifecycleMetadataRepairPlanSummaryReport CreateVectorLifecycleRepairPlanSummary()
        => new()
        {
            OperationId = "repair-summary-api",
            CandidateCount = 1,
            HumanReviewRequiredCount = 1,
            CorrectlyBlockedSkippedCount = 18,
            FormalRetrievalAllowed = false,
            UseForRuntime = false,
            Reports =
            [
                new VectorLifecycleMetadataRepairPlanReport
                {
                    OperationId = "repair-a3-api",
                    DatasetName = "A3",
                    CandidateCount = 1,
                    HumanReviewRequiredCount = 1,
                    CorrectlyBlockedSkippedCount = 18,
                    FormalRetrievalAllowed = false,
                    UseForRuntime = false,
                    Candidates =
                    [
                        new VectorLifecycleMetadataRepairCandidate
                        {
                            DatasetName = "A3",
                            SampleId = "sample-api",
                            MustHitItemId = "item-api",
                            ItemKind = "note",
                            Layer = "context",
                            CurrentLifecycle = "Unknown",
                            ProposedLifecycle = "Active",
                            ProposedReviewStatus = "Current",
                            CurrentTargetSection = VectorQueryTargetSections.Excluded,
                            ProposedTargetSection = VectorQueryTargetSections.NormalContext,
                            EvidenceRefs = ["evidence-api"],
                            SourceRefs = ["source-api"],
                            RelationEvidenceAvailable = true,
                            RepairReason = "review required",
                            RequiresHumanReview = true,
                            CanAutoRepair = false,
                            ForbiddenReason = "MissingProvenance"
                        }
                    ]
                }
            ]
        };

    private static WebApplicationFactory<Program> CreateFactory(
        string rootPath,
        bool jobWorkerEnabled,
        int pollIntervalMs,
        string storageProvider = "filesystem",
        bool requireApiKey = false,
        string? apiKey = null,
        Action<IServiceCollection>? configureServices = null)
    {
        return new ServiceTestFactory(rootPath, jobWorkerEnabled, pollIntervalMs, storageProvider, requireApiKey, apiKey, configureServices);
    }

    private static string CreateTestRootPath()
    {
        return Path.Combine(
            Directory.GetCurrentDirectory(),
            "context-core-service-integration-data",
            Guid.NewGuid().ToString("N"));
    }

    private static void DeleteTestRoot(string rootPath)
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static void AssertNoLocalPathLeak(string payload, string context)
    {
        Assert.IsFalse(payload.Contains(@":\", StringComparison.Ordinal), context);
        Assert.IsFalse(payload.Contains(":/", StringComparison.Ordinal), context);
        Assert.IsFalse(payload.Contains("/home/", StringComparison.OrdinalIgnoreCase), context);
        Assert.IsFalse(payload.Contains(".contextcore", StringComparison.OrdinalIgnoreCase), context);
        Assert.IsFalse(payload.Contains("secrets.json", StringComparison.OrdinalIgnoreCase), context);
        Assert.IsFalse(payload.Contains(".onnx", StringComparison.OrdinalIgnoreCase), context);
    }

    private static string GetSystemHealthCollectionPath(string rootPath)
    {
        return Path.Combine(
            FileStorageOptions.ResolveRootPath(rootPath),
            "workspaces",
            "__system__",
            "collections",
            "__health__");
    }

    private static string GetSystemHealthLogsPath(string rootPath)
    {
        return Path.Combine(
            FileStorageOptions.ResolveRootPath(rootPath),
            "logs",
            "__system__");
    }

    private sealed class ServiceTestFactory : WebApplicationFactory<Program>
    {
        private readonly string _rootPath;
        private readonly bool _jobWorkerEnabled;
        private readonly int _pollIntervalMs;
        private readonly string _storageProvider;
        private readonly bool _requireApiKey;
        private readonly string? _apiKey;
        private readonly Action<IServiceCollection>? _configureServices;

        public ServiceTestFactory(
            string rootPath,
            bool jobWorkerEnabled,
            int pollIntervalMs,
            string storageProvider,
            bool requireApiKey = false,
            string? apiKey = null,
            Action<IServiceCollection>? configureServices = null)
        {
            _rootPath = rootPath;
            _jobWorkerEnabled = jobWorkerEnabled;
            _pollIntervalMs = pollIntervalMs;
            _storageProvider = storageProvider;
            _requireApiKey = requireApiKey;
            _apiKey = apiKey;
            _configureServices = configureServices;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Storage:Provider", _storageProvider);
            builder.UseSetting("Storage:RootPath", _rootPath);
            builder.UseSetting("Compression:Provider", "mock");
            builder.UseSetting("JobWorker:Enabled", _jobWorkerEnabled ? "true" : "false");
            builder.UseSetting("JobWorker:PollIntervalMilliseconds", _pollIntervalMs.ToString());
            builder.UseSetting("Security:RequireApiKey", _requireApiKey ? "true" : "false");
            if (_apiKey is not null)
            {
                builder.UseSetting("Security:ApiKey", _apiKey);
            }

            if (_configureServices is not null)
            {
                builder.ConfigureServices(_configureServices);
            }
        }
    }

    private sealed class FakeRankerDebugRetriever : IContextRetriever
    {
        public Task<ContextRetrievalResult> RetrieveAsync(
            ContextRetrievalRequest request,
            CancellationToken cancellationToken = default)
        {
            var active = CreateCandidate(
                "memory:active-rule-v2",
                score: 10,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["memoryLayer"] = "Stable",
                    ["lifecycleStatus"] = "Stable",
                    ["status"] = "Active",
                    ["version"] = "v2"
                });
            var deprecated = CreateCandidate(
                "memory:deprecated-rule-v1",
                score: 20,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["memoryLayer"] = "historical_context",
                    ["lifecycleStatus"] = "Deprecated",
                    ["section"] = "historical_context",
                    ["version"] = "v1"
                });

            return Task.FromResult(new ContextRetrievalResult
            {
                OperationId = request.OperationId,
                SelectedItems = [active],
                DroppedItems =
                [
                    new ContextRetrievalDecision
                    {
                        CandidateId = deprecated.CandidateId,
                        SourceId = deprecated.SourceId,
                        Kind = deprecated.Kind,
                        Type = deprecated.Type,
                        Reason = "debug dropped",
                        Score = deprecated.Score,
                        EstimatedTokens = deprecated.EstimatedTokens,
                        Metadata = deprecated.Metadata
                    }
                ],
                Trace = new ContextRetrievalTrace
                {
                    Candidates = [active, deprecated]
                },
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        private static ContextRetrievalCandidate CreateCandidate(
            string id,
            double score,
            Dictionary<string, string> metadata)
        {
            return new ContextRetrievalCandidate
            {
                CandidateId = $"MemoryItem:{id}",
                SourceId = id,
                Kind = ContextRetrievalCandidateKind.MemoryItem,
                Type = "memory",
                Content = id,
                Score = score,
                EstimatedTokens = 8,
                Reasons = [id],
                SourceRefs = [id],
                Metadata = metadata
            };
        }
    }

}
