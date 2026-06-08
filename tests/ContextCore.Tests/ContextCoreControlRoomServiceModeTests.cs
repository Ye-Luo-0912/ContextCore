using System.Net;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Screens;
using ContextCore.ControlRoom.Services;
using ContextCore.Storage.FileSystem;

namespace ContextCore.Tests;

/// <summary>覆盖 ControlRoom 的 minimal service mode 观测链路。</summary>
[TestClass]
public sealed class ContextCoreControlRoomServiceModeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task ServiceMode_ShouldFetchStatusAndReadiness()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(_ => Json(CreateStatusResponse()));
        handlers.Enqueue(_ => Json(CreateReadinessResponse(fromCache: true)));

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var state = ControlRoomService.CreateState(
            "filesystem",
            FileStorageOptions.DefaultRootPath,
            "workspace-test",
            "collection-test",
            ControlRoomMode.Service,
            "http://localhost:5079/",
            http);
        var service = new ControlRoomService(state);

        var status = await service.GetStatusAsync();
        var readiness = await service.GetRuntimeReadinessAsync();

        Assert.AreEqual(ControlRoomMode.Service, status.Mode);
        Assert.AreEqual("http://localhost:5079/", status.ServiceBaseUrl);
        Assert.AreEqual("ready", status.ReadinessState);
        Assert.AreEqual("retrieval-orchestration-baseline-v1", status.RetrievalBaseline);
        Assert.AreEqual("ready", readiness.Status);
        Assert.IsTrue(readiness.FromCache);
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task RuntimeSnapshotWithoutDeep_ShouldNotRequestDeepStatus()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual("/api/status", request.RequestUri?.AbsolutePath);
            return Json(CreateStatusResponse());
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual("/api/health/ready", request.RequestUri?.AbsolutePath);
            return Json(CreateReadinessResponse(fromCache: true));
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var state = ControlRoomService.CreateState(
            "filesystem",
            FileStorageOptions.DefaultRootPath,
            "workspace-test",
            "collection-test",
            ControlRoomMode.Service,
            "http://localhost:5079/",
            http);
        var service = new ControlRoomService(state);

        var snapshot = await service.GetServiceDashboardSnapshotAsync();

        Assert.IsNotNull(snapshot);
        Assert.IsNull(snapshot.Snapshot.DeepStatus);
        Assert.AreEqual("ok", snapshot.Snapshot.Status.Status);
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task RuntimeSnapshotWithDeepRefresh_ShouldRequestDeepStatus()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(_ => Json(CreateStatusResponse()));
        handlers.Enqueue(_ => Json(CreateReadinessResponse(fromCache: false)));
        handlers.Enqueue(request =>
        {
            Assert.AreEqual("/api/status/deep", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?refresh=true", request.RequestUri?.Query);
            return Json(CreateDeepReadinessResponse(fromCache: false));
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var state = ControlRoomService.CreateState(
            "filesystem",
            FileStorageOptions.DefaultRootPath,
            "workspace-test",
            "collection-test",
            ControlRoomMode.Service,
            "http://localhost:5079/",
            http);
        var service = new ControlRoomService(state);

        var snapshot = await service.GetServiceDashboardSnapshotAsync(includeDeep: true, refreshDeep: true);

        Assert.IsNotNull(snapshot.Snapshot.DeepStatus);
        Assert.AreEqual("__system__/__health__", snapshot.Snapshot.DeepStatus!.ProbeScope);
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task ServiceMode_DeepRefresh_ShouldCallRefreshTrue()
    {
        using var http = CreateHttpClient(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/status/deep", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("?refresh=true", request.RequestUri?.Query);
            return Json(CreateDeepReadinessResponse(fromCache: false));
        });

        var state = ControlRoomService.CreateState(
            "filesystem",
            FileStorageOptions.DefaultRootPath,
            "workspace-test",
            "collection-test",
            ControlRoomMode.Service,
            "http://localhost:5079/",
            http);
        var service = new ControlRoomService(state);

        var deep = await service.GetRuntimeDeepStatusAsync(refresh: true);

        Assert.AreEqual("__system__/__health__", deep.ProbeScope);
        Assert.IsFalse(deep.FromCache);
    }

    [TestMethod]
    public async Task ServiceIngest_ShouldCallClientIngest()
    {
        using var http = CreateHttpClient(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/context/ingest", request.RequestUri?.AbsolutePath);
            return Json(new ContextInputIngestionResult
            {
                OperationId = "svc-ingest-1",
                Created = true,
                Deduped = false,
                ContentHash = "hash-1",
                SequenceId = 1,
                Item = new ContextItem
                {
                    Id = "item-1",
                    WorkspaceId = "workspace-test",
                    CollectionId = "collection-test",
                    Type = "note",
                    Content = "service ingest content"
                }
            });
        });

        var state = ControlRoomService.CreateState(
            "filesystem",
            FileStorageOptions.DefaultRootPath,
            "workspace-test",
            "collection-test",
            ControlRoomMode.Service,
            "http://localhost:5079/",
            http);
        var service = new ControlRoomService(state);

        var result = await service.IngestServiceAsync(new ContextInputCommand
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Source = "controlroom-service",
            InputKind = "note",
            Content = "service ingest content"
        });
        var rendered = ServiceOperationRenderer.RenderIngestResult(result);

        Assert.AreEqual("item-1", result.Item.Id);
        StringAssert.Contains(rendered, "svc-ingest-1");
        StringAssert.Contains(rendered, "item-1");
    }

    [TestMethod]
    public async Task ServiceQuery_ShouldHandleSuccessAndError()
    {
        using (var http = CreateHttpClient(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/context/query", request.RequestUri?.AbsolutePath);
            return Json(new[]
            {
                new ContextItem
                {
                    Id = "query-1",
                    WorkspaceId = "workspace-test",
                    CollectionId = "collection-test",
                    Type = "note",
                    Content = "query result content"
                }
            });
        }))
        {
            var state = ControlRoomService.CreateState(
                "filesystem",
                FileStorageOptions.DefaultRootPath,
                "workspace-test",
                "collection-test",
                ControlRoomMode.Service,
                "http://localhost:5079/",
                http);
            var service = new ControlRoomService(state);

            var response = await service.QueryServiceAsync(new ContextQueryRequest
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                QueryText = "query"
            });
            var rendered = ServiceOperationRenderer.RenderQueryResult(response);

            Assert.AreEqual(1, response.Count);
            StringAssert.Contains(rendered, "query-1");
        }

        using (var http = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(JsonSerializer.Serialize(new ContextCoreErrorResponse
            {
                OperationId = "query-op-err",
                ErrorCode = ContextCoreErrorCodes.ValidationFailed,
                Message = "Query invalid.",
                Target = "context.query",
                TraceId = "trace-query",
                Details =
                [
                    new ContextCoreErrorDetail
                    {
                        Code = "InvalidTake",
                        Field = "Take",
                        Target = "context.query",
                        Message = "Take must be positive."
                    }
                ]
            }, JsonOptions), Encoding.UTF8, "application/json")
        }))
        {
            var state = ControlRoomService.CreateState(
                "filesystem",
                FileStorageOptions.DefaultRootPath,
                "workspace-test",
                "collection-test",
                ControlRoomMode.Service,
                "http://localhost:5079/",
                http);
            var service = new ControlRoomService(state);

            var exception = await Assert.ThrowsExceptionAsync<ContextCoreApiException>(() =>
                service.QueryServiceAsync(new ContextQueryRequest
                {
                    WorkspaceId = "workspace-test",
                    CollectionId = "collection-test",
                    Take = -1
                }));
            var rendered = ServiceOperationRenderer.RenderError(exception);

            StringAssert.Contains(rendered, "context.query");
            StringAssert.Contains(rendered, "InvalidTake");
        }
    }

    [TestMethod]
    public async Task ServicePackagePreview_ShouldRenderSections()
    {
        using var http = CreateHttpClient(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/package/build-detailed", request.RequestUri?.AbsolutePath);
            return Json(new ContextPackageBuildResult
            {
                BuildId = "build-1",
                TokenBudget = 1200,
                EstimatedTokens = 320,
                SelectedItems = [],
                DroppedItems = [],
                Uncertainties =
                [
                    new ContextPackageUncertainty
                    {
                        Code = "TokenBudgetPressure",
                        Severity = "Warning",
                        Message = "Budget pressure."
                    }
                ],
                Package = new ContextPackage
                {
                    PackageId = "pkg-1",
                    WorkspaceId = "workspace-test",
                    CollectionId = "collection-test",
                    EstimatedTokens = 320,
                    Sections =
                    [
                        new ContextPackageSection
                        {
                            Name = "recent_context",
                            Priority = 10,
                            Content = "recent section content",
                            ContentFormat = ContextContentFormat.Markdown,
                            EstimatedTokens = 120
                        },
                        new ContextPackageSection
                        {
                            Name = "stable_memory",
                            Priority = 20,
                            Content = "stable section content",
                            ContentFormat = ContextContentFormat.Markdown,
                            EstimatedTokens = 200
                        }
                    ]
                }
            });
        });

        var state = ControlRoomService.CreateState(
            "filesystem",
            FileStorageOptions.DefaultRootPath,
            "workspace-test",
            "collection-test",
            ControlRoomMode.Service,
            "http://localhost:5079/",
            http);
        var service = new ControlRoomService(state);

        var result = await service.BuildServicePackageAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "package",
            TokenBudget = 1200
        });
        var rendered = ServiceOperationRenderer.RenderPackageResult(result);

        Assert.AreEqual("pkg-1", result.Package.PackageId);
        StringAssert.Contains(rendered, "recent_context");
        StringAssert.Contains(rendered, "stable_memory");
        StringAssert.Contains(rendered, "TokenBudgetPressure");
    }

    [TestMethod]
    public async Task ServiceJobsPage_ShouldRenderJobList()
    {
        using var http = CreateHttpClient(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/jobs", request.RequestUri?.AbsolutePath);
            return Json(new[]
            {
                new ContextJob
                {
                    JobId = "job-1",
                    WorkspaceId = "workspace-test",
                    CollectionId = "collection-test",
                    Kind = ContextJobKind.Compression,
                    State = ContextJobState.Failed,
                    RetryCount = 1,
                    MaxRetryCount = 3,
                    PayloadJson = "{\"operationId\":\"op-job-1\"}",
                    ErrorMessage = "failed once",
                    CreatedAt = DateTimeOffset.UtcNow
                }
            });
        });

        var state = ControlRoomService.CreateState(
            "filesystem",
            FileStorageOptions.DefaultRootPath,
            "workspace-test",
            "collection-test",
            ControlRoomMode.Service,
            "http://localhost:5079/",
            http);
        var service = new ControlRoomService(state);

        var snapshot = await service.GetServiceJobsSnapshotAsync();
        var rendered = ServiceOperationalRenderer.RenderJobs(snapshot);

        StringAssert.Contains(rendered, "job-1");
        StringAssert.Contains(rendered, "Compression");
        StringAssert.Contains(rendered, "failed once");
    }

    [TestMethod]
    public async Task ServiceJobDetail_ShouldHandleNotFoundWithContextCoreApiException()
    {
        using var http = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(JsonSerializer.Serialize(new ContextCoreErrorResponse
            {
                OperationId = "job-not-found",
                ErrorCode = ContextCoreErrorCodes.NotFound,
                Message = "未找到作业：job-missing",
                Target = "jobs.get",
                TraceId = "trace-job-not-found",
                Details =
                [
                    new ContextCoreErrorDetail
                    {
                        Code = "job_not_found",
                        Target = "jobs.get",
                        Message = "未找到作业：job-missing"
                    }
                ]
            }, JsonOptions), Encoding.UTF8, "application/json")
        });

        var state = ControlRoomService.CreateState(
            "filesystem",
            FileStorageOptions.DefaultRootPath,
            "workspace-test",
            "collection-test",
            ControlRoomMode.Service,
            "http://localhost:5079/",
            http);
        var service = new ControlRoomService(state);

        var exception = await Assert.ThrowsExceptionAsync<ContextCoreApiException>(() =>
            service.GetServiceJobAsync("job-missing"));
        var rendered = ServiceOperationalRenderer.RenderError(exception);

        StringAssert.Contains(rendered, "jobs.get");
        StringAssert.Contains(rendered, "job_not_found");
    }

    [TestMethod]
    public async Task ServiceJobRequeue_ShouldCallClient()
    {
        using var http = CreateHttpClient(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/jobs/job-1/requeue", request.RequestUri?.AbsolutePath);
            return Json(new ContextCoreRequeueJobResponse
            {
                OriginalJobId = "job-1",
                NewJobId = "job-2",
                Job = new ContextJob
                {
                    JobId = "job-2",
                    WorkspaceId = "workspace-test",
                    CollectionId = "collection-test",
                    Kind = ContextJobKind.Compression,
                    State = ContextJobState.Queued,
                    PayloadJson = "{}",
                    CreatedAt = DateTimeOffset.UtcNow
                }
            });
        });

        var state = ControlRoomService.CreateState(
            "filesystem",
            FileStorageOptions.DefaultRootPath,
            "workspace-test",
            "collection-test",
            ControlRoomMode.Service,
            "http://localhost:5079/",
            http);
        var service = new ControlRoomService(state);

        var response = await service.RequeueServiceJobAsync("job-1");

        Assert.AreEqual("job-1", response.OriginalJobId);
        Assert.AreEqual("job-2", response.NewJobId);
    }

    [TestMethod]
    public async Task ServiceModelStatusPage_ShouldRenderProviderAndRoute()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(_ => Json(CreateModelStatusResponse()));
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/model/route/resolve", request.RequestUri?.AbsolutePath);
            return Json(new ContextCoreModelRouteResolveResponse
            {
                Role = "GeneralCompression",
                TaskKind = "Summarize",
                ThinkingMode = "fast",
                RouteSource = "角色精确匹配",
                Primary = new ContextCoreModelSelectionResponse
                {
                    ModelName = "mock-fast",
                    Reason = "命中主模型"
                },
                Fallback = new ContextCoreModelSelectionResponse
                {
                    ModelName = "mock-fallback",
                    Reason = "兜底"
                }
            });
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var state = ControlRoomService.CreateState(
            "filesystem",
            FileStorageOptions.DefaultRootPath,
            "workspace-test",
            "collection-test",
            ControlRoomMode.Service,
            "http://localhost:5079/",
            http);
        var service = new ControlRoomService(state);

        var snapshot = await service.GetServiceModelSnapshotAsync(new ContextCoreModelRouteResolveRequest
        {
            Role = "GeneralCompression",
            TaskKind = "Summarize",
            ThinkingMode = "fast"
        });
        var rendered = ServiceOperationalRenderer.RenderModel(snapshot);

        StringAssert.Contains(rendered, "mock-api");
        StringAssert.Contains(rendered, "mock-fast");
        StringAssert.Contains(rendered, "角色精确匹配");
    }

    [TestMethod]
    public async Task ServiceAdminRuntimePage_ShouldRenderBackupAndStatusData()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(_ => Json(CreateStatusResponse()));
        handlers.Enqueue(_ => Json(CreateReadinessResponse(fromCache: true)));
        handlers.Enqueue(_ => Json(new ContextCoreAdminStatusResponse
        {
            Storage = new ContextCoreStorageInfo
            {
                Provider = "filesystem",
                RootPath = @"D:\context-core-data"
            },
            Workspace = "workspace-test",
            Collection = "collection-test",
            RetrievalBaseline = "retrieval-orchestration-baseline-v1"
        }));
        handlers.Enqueue(_ => Json(new ContextCoreBackupStatusResponse
        {
            Provider = "filesystem",
            Root = @"D:\context-core-data",
            Exists = true,
            FileCount = 12,
            JsonlFileCount = 5
        }));
        handlers.Enqueue(_ => Json(new ContextCoreBackupValidateResponse
        {
            Healthy = true,
            Message = "所有文件通过校验。",
            ScannedFiles = 5,
            CorruptFiles = 0
        }));

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var state = ControlRoomService.CreateState(
            "filesystem",
            FileStorageOptions.DefaultRootPath,
            "workspace-test",
            "collection-test",
            ControlRoomMode.Service,
            "http://localhost:5079/",
            http);
        var service = new ControlRoomService(state);

        var snapshot = await service.GetServiceAdminRuntimeSnapshotAsync();
        var rendered = ServiceOperationalRenderer.RenderAdminRuntime(snapshot);

        StringAssert.Contains(rendered, "retrieval-orchestration-baseline-v1");
        StringAssert.Contains(rendered, "D:\\context-core-data");
        StringAssert.Contains(rendered, "所有文件通过校验。");
    }

    [TestMethod]
    public async Task ServiceMemoryPage_ShouldRenderSummaryAndDetail()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(_ => Json(new[]
        {
            new ContextMemoryItem
            {
                Id = "working-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Layer = ContextMemoryLayer.Working,
                Status = ContextMemoryStatus.Active,
                Type = "note",
                Content = "working item",
                Tags = ["w"],
                SourceRefs = ["src:w1"],
                Importance = 0.8,
                UpdatedAt = DateTimeOffset.UtcNow
            }
        }));
        handlers.Enqueue(_ => Json(new[]
        {
            new ContextMemoryItem
            {
                Id = "candidate-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Layer = ContextMemoryLayer.Working,
                Status = ContextMemoryStatus.Candidate,
                Type = "note",
                Content = "candidate item",
                Tags = ["c"],
                SourceRefs = ["src:c1"],
                Importance = 0.5,
                UpdatedAt = DateTimeOffset.UtcNow
            }
        }));
        handlers.Enqueue(_ => Json(new[]
        {
            new ContextMemoryItem
            {
                Id = "stable-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Layer = ContextMemoryLayer.Stable,
                Status = ContextMemoryStatus.Stable,
                Type = "rule",
                Content = "stable item",
                Tags = ["s"],
                SourceRefs = ["src:s1"],
                Importance = 1.0,
                UpdatedAt = DateTimeOffset.UtcNow
            }
        }));
        handlers.Enqueue(_ => Json(new[]
        {
            new ContextGlobalItem
            {
                Id = "global-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Scope = ContextScope.Workspace,
                Type = "preference",
                Content = "global item",
                Tags = ["g"],
                SourceRefs = ["src:g1"],
                Importance = 0.9,
                UpdatedAt = DateTimeOffset.UtcNow
            }
        }));

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var state = ControlRoomService.CreateState("filesystem", FileStorageOptions.DefaultRootPath, "workspace-test", "collection-test", ControlRoomMode.Service, "http://localhost:5079/", http);
        var service = new ControlRoomService(state);

        var snapshot = await service.GetServiceMemorySnapshotAsync();
        var summary = ServiceOperationalRenderer.RenderMemory(snapshot);
        var detail = ServiceOperationalRenderer.RenderMemoryDetail(snapshot.Working[0]);

        StringAssert.Contains(summary, "Working : 1");
        StringAssert.Contains(summary, "Global  : 1");
        StringAssert.Contains(detail, "working-1");
        StringAssert.Contains(detail, "src:w1");
    }

    [TestMethod]
    public async Task ServiceConstraintsPage_ShouldRenderListAndDetail()
    {
        using var http = CreateHttpClient(_ => Json(new[]
        {
            new ContextConstraint
            {
                Id = "constraint-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Scope = ContextScope.Collection,
                Level = ConstraintLevel.Hard,
                Status = ContextMemoryStatus.Verified,
                Content = "must keep source refs",
                AppliesToRefs = ["item-1"],
                SourceRefs = ["src:constraint-1"]
            }
        }));
        var state = ControlRoomService.CreateState("filesystem", FileStorageOptions.DefaultRootPath, "workspace-test", "collection-test", ControlRoomMode.Service, "http://localhost:5079/", http);
        var service = new ControlRoomService(state);

        var snapshot = await service.GetServiceConstraintsSnapshotAsync();
        var list = ServiceOperationalRenderer.RenderConstraints(snapshot);
        var detail = ServiceOperationalRenderer.RenderConstraintDetail(snapshot.Constraints[0]);

        StringAssert.Contains(list, "constraint-1");
        StringAssert.Contains(detail, "src:constraint-1");
        StringAssert.Contains(detail, "must keep source refs");
    }

    [TestMethod]
    public async Task ServiceConstraintGapsPage_ShouldRenderListAndDetail()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual("/api/constraints/gaps", request.RequestUri?.AbsolutePath);
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "workspaceId=workspace-test");
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "collectionId=collection-test");
            return Json(new[]
            {
                CreateConstraintGap("gap-1")
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual("/api/constraints/gaps/gap-1", request.RequestUri?.AbsolutePath);
            return Json(CreateConstraintGap("gap-1"));
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var state = ControlRoomService.CreateState("filesystem", FileStorageOptions.DefaultRootPath, "workspace-test", "collection-test", ControlRoomMode.Service, "http://localhost:5079/", http);
        var service = new ControlRoomService(state);

        var snapshot = await service.GetServiceConstraintGapsSnapshotAsync();
        var list = ServiceOperationalRenderer.RenderConstraintGaps(snapshot);
        var detail = ServiceOperationalRenderer.RenderConstraintGapDetail(await service.GetServiceConstraintGapAsync("gap-1"));

        StringAssert.Contains(list, "Service Constraint Gaps");
        StringAssert.Contains(list, "重复解释不应提升");
        StringAssert.Contains(list, "scope=Collection type=Hard");
        StringAssert.Contains(detail, "SourceSampleId");
        StringAssert.Contains(detail, "eval:planning");
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task ServiceConstraintGapsScreen_ShouldRequireConfirmationBeforeAccept()
    {
        var gap = CreateConstraintGap("gap-1");
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/constraints/gaps", request.RequestUri?.AbsolutePath);
            return Json(new[] { gap });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/constraints/gaps/gap-1", request.RequestUri?.AbsolutePath);
            return Json(gap);
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/constraints/gaps", request.RequestUri?.AbsolutePath);
            return Json(new[] { gap });
        });
        var acceptCalls = 0;
        using var http = CreateHttpClient(request =>
        {
            if (request.Method == HttpMethod.Post
                && string.Equals(request.RequestUri?.AbsolutePath, "/api/constraints/gaps/gap-1/accept", StringComparison.OrdinalIgnoreCase))
            {
                acceptCalls++;
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return handlers.Dequeue().Invoke(request);
        });
        var state = ControlRoomService.CreateState(
            "filesystem",
            FileStorageOptions.DefaultRootPath,
            "workspace-test",
            "collection-test",
            ControlRoomMode.Service,
            "http://localhost:5079/",
            http);
        var service = new ControlRoomService(state);
        var originalIn = Console.In;
        var originalOut = Console.Out;
        using var input = new StringReader("a gap-1\nNO\nb\n");
        using var output = new StringWriter();

        try
        {
            Console.SetIn(input);
            Console.SetOut(output);
            var action = await ServiceConstraintGapsScreen.ShowAsync(service);

            Assert.AreEqual(ControlRoomActionKind.Back, action);
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }

        Assert.AreEqual(0, acceptCalls);
        Assert.AreEqual(0, handlers.Count);
        StringAssert.Contains(output.ToString(), "Type YES");
        StringAssert.Contains(output.ToString(), "Constraint gap review action canceled.");
    }

    [TestMethod]
    public async Task ServiceCandidateConstraintsScreen_ShouldRequireConfirmationBeforeActivate()
    {
        var candidate = CreateCandidateConstraint("candidate-constraint-1");
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/constraints/candidates", request.RequestUri?.AbsolutePath);
            return Json(new[] { candidate });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/constraints/candidates/candidate-constraint-1", request.RequestUri?.AbsolutePath);
            return Json(candidate);
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/constraints/candidates", request.RequestUri?.AbsolutePath);
            return Json(new[] { candidate });
        });
        var activateCalls = 0;
        using var http = CreateHttpClient(request =>
        {
            if (request.Method == HttpMethod.Post
                && string.Equals(request.RequestUri?.AbsolutePath, "/api/constraints/candidates/candidate-constraint-1/activate", StringComparison.OrdinalIgnoreCase))
            {
                activateCalls++;
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return handlers.Dequeue().Invoke(request);
        });
        var state = ControlRoomService.CreateState(
            "filesystem",
            FileStorageOptions.DefaultRootPath,
            "workspace-test",
            "collection-test",
            ControlRoomMode.Service,
            "http://localhost:5079/",
            http);
        var service = new ControlRoomService(state);
        var originalIn = Console.In;
        var originalOut = Console.Out;
        using var input = new StringReader("a candidate-constraint-1\nNO\nb\n");
        using var output = new StringWriter();

        try
        {
            Console.SetIn(input);
            Console.SetOut(output);
            var action = await ServiceCandidateConstraintsScreen.ShowAsync(service);

            Assert.AreEqual(ControlRoomActionKind.Back, action);
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }

        Assert.AreEqual(0, activateCalls);
        Assert.AreEqual(0, handlers.Count);
        StringAssert.Contains(output.ToString(), "Type YES");
        StringAssert.Contains(output.ToString(), "Candidate constraint review action canceled.");
    }

    [TestMethod]
    public async Task ServiceCandidateMemoryScreen_ShouldRequireConfirmationBeforeReady()
    {
        var candidate = CreateCandidateMemoryRecord("candidate-memory-1");
        var snapshot = new CandidateMemorySnapshot
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            CandidateMemoryCount = 1,
            PendingReviewCount = 1,
            RecentCandidates = [candidate]
        };
        var diagnostics = new CandidateMemoryDiagnosticsReport
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Diagnostics = []
        };
        var explanation = new CandidateMemoryExplanation
        {
            CandidateId = candidate.Id,
            Candidate = candidate,
            EvidenceRefs = candidate.EvidenceRefs,
            ProvenanceChain =
            [
                new CandidateMemoryProvenanceLink
                {
                    SourceType = "CandidateMemoryRecord",
                    SourceId = candidate.Id,
                    Relation = "target",
                    Status = "Candidate"
                }
            ]
        };
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/candidates/snapshot", request.RequestUri?.AbsolutePath);
            return Json(snapshot);
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/candidates/diagnostics", request.RequestUri?.AbsolutePath);
            return Json(diagnostics);
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/candidates/candidate-memory-1", request.RequestUri?.AbsolutePath);
            return Json(candidate);
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/candidates/candidate-memory-1/explain", request.RequestUri?.AbsolutePath);
            return Json(explanation);
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/candidates/snapshot", request.RequestUri?.AbsolutePath);
            return Json(snapshot);
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/candidates/diagnostics", request.RequestUri?.AbsolutePath);
            return Json(diagnostics);
        });
        var readyCalls = 0;
        using var http = CreateHttpClient(request =>
        {
            if (request.Method == HttpMethod.Post
                && string.Equals(request.RequestUri?.AbsolutePath, "/api/memory/candidates/candidate-memory-1/ready-for-stable-review", StringComparison.OrdinalIgnoreCase))
            {
                readyCalls++;
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return handlers.Dequeue().Invoke(request);
        });
        var state = ControlRoomService.CreateState(
            "filesystem",
            FileStorageOptions.DefaultRootPath,
            "workspace-test",
            "collection-test",
            ControlRoomMode.Service,
            "http://localhost:5079/",
            http);
        var service = new ControlRoomService(state);
        var originalIn = Console.In;
        var originalOut = Console.Out;
        using var input = new StringReader("ready candidate-memory-1\nNO\nb\n");
        using var output = new StringWriter();

        try
        {
            Console.SetIn(input);
            Console.SetOut(output);
            var action = await ServiceCandidateMemoryScreen.ShowAsync(service);

            Assert.AreEqual(ControlRoomActionKind.Back, action);
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }

        Assert.AreEqual(0, readyCalls);
        Assert.AreEqual(0, handlers.Count);
        StringAssert.Contains(output.ToString(), "Type YES");
        StringAssert.Contains(output.ToString(), "Candidate memory review action canceled.");
    }

    [TestMethod]
    public async Task ServiceRelationsPage_ShouldHandleItemIdQueryAndNotFound()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual("/api/relations/types", request.RequestUri?.AbsolutePath);
            return Json<IReadOnlyList<RelationTypeDefinition>>(
            [
                new RelationTypeDefinition
                {
                    Type = "references",
                    IsDirectional = true,
                    DefaultWeight = 0.5
                }
            ]);
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual("/api/relations/diagnostics", request.RequestUri?.AbsolutePath);
            return Json(new RelationGraphDiagnosticsReport
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test"
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual("/api/relations/item-1", request.RequestUri?.AbsolutePath);
            return Json(new
            {
                itemId = "item-1",
                outgoing = new[]
                {
                    new ContextRelation
                    {
                        Id = "rel-1",
                        WorkspaceId = "workspace-test",
                        CollectionId = "collection-test",
                        SourceId = "item-1",
                        TargetId = "item-2",
                        RelationType = "references",
                        Weight = 0.8,
                        Confidence = 0.9
                    }
                },
                incoming = Array.Empty<ContextRelation>()
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual("/api/relations/diagnostics/item-1", request.RequestUri?.AbsolutePath);
            return Json(new RelationGraphDiagnosticsReport
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                ItemId = "item-1"
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual("/api/relations/rel-1/explain", request.RequestUri?.AbsolutePath);
            return Json(new RelationExplainResponse
            {
                RelationId = "rel-1",
                Relation = new ContextRelation
                {
                    Id = "rel-1",
                    WorkspaceId = "workspace-test",
                    CollectionId = "collection-test",
                    SourceId = "item-1",
                    TargetId = "item-2",
                    RelationType = "references",
                    Confidence = 0.9
                },
                Confidence = 0.9,
                ConfidenceReason = "manual_reviewed",
                Lifecycle = StableMemoryLifecycle.Active,
                ReviewStatus = "Reviewed",
                EvidenceRefs = ["event-1"]
            });
        });

        using (var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request)))
        {
            var state = ControlRoomService.CreateState("filesystem", FileStorageOptions.DefaultRootPath, "workspace-test", "collection-test", ControlRoomMode.Service, "http://localhost:5079/", http);
            var service = new ControlRoomService(state);
            var snapshot = await service.GetServiceRelationsSnapshotAsync("item-1");
            var rendered = ServiceOperationalRenderer.RenderRelations(snapshot);
            var explain = await service.ExplainServiceRelationAsync("rel-1");
            var explainRendered = ServiceOperationalRenderer.RenderRelationExplain(explain);
            StringAssert.Contains(rendered, "item-1");
            StringAssert.Contains(rendered, "references");
            StringAssert.Contains(rendered, "Relation Types");
            StringAssert.Contains(rendered, "Global Relation Diagnostics");
            StringAssert.Contains(rendered, "Item Relation Diagnostics");
            StringAssert.Contains(explainRendered, "Service Relation Explain");
            StringAssert.Contains(explainRendered, "manual_reviewed");
            Assert.AreEqual(0, handlers.Count);
        }

        var errorHandlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        errorHandlers.Enqueue(_ => Json<IReadOnlyList<RelationTypeDefinition>>([]));
        errorHandlers.Enqueue(_ => Json(new RelationGraphDiagnosticsReport
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test"
        }));
        errorHandlers.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(JsonSerializer.Serialize(new ContextCoreErrorResponse
            {
                OperationId = "rel-op-err",
                ErrorCode = ContextCoreErrorCodes.InvalidRequest,
                Message = "workspaceId 和 collectionId 为必填参数。",
                Target = "relations.get.by-id",
                TraceId = "trace-rel",
                Details =
                [
                    new ContextCoreErrorDetail
                    {
                        Code = ContextCoreErrorCodes.InvalidRequest,
                        Target = "relations.get.by-id",
                        Message = "workspaceId 和 collectionId 为必填参数。"
                    }
                ]
            }, JsonOptions), Encoding.UTF8, "application/json")
        });

        using (var http = CreateHttpClient(request => errorHandlers.Dequeue().Invoke(request)))
        {
            var state = ControlRoomService.CreateState("filesystem", FileStorageOptions.DefaultRootPath, "workspace-test", "collection-test", ControlRoomMode.Service, "http://localhost:5079/", http);
            var service = new ControlRoomService(state);
            var exception = await Assert.ThrowsExceptionAsync<ContextCoreApiException>(() => service.GetServiceRelationsSnapshotAsync("bad"));
            var rendered = ServiceOperationalRenderer.RenderError(exception);
            StringAssert.Contains(rendered, "relations.get.by-id");
        }
    }

    [TestMethod]
    public async Task ServicePolicyPage_ShouldRenderDefaultAndRuntimePolicy()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(_ => Json(CreateStatusResponse()));
        handlers.Enqueue(_ => Json(CreateReadinessResponse(fromCache: true)));
        handlers.Enqueue(_ => Json(Array.Empty<ContextPackagePolicy>()));

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var state = ControlRoomService.CreateState("filesystem", FileStorageOptions.DefaultRootPath, "workspace-test", "collection-test", ControlRoomMode.Service, "http://localhost:5079/", http);
        var service = new ControlRoomService(state);

        var snapshot = await service.GetServicePolicySnapshotAsync();
        var rendered = ServiceOperationalRenderer.RenderPolicy(snapshot);

        StringAssert.Contains(rendered, "Runtime Default Policy");
        StringAssert.Contains(rendered, "TokenBudget       : 1200");
        StringAssert.Contains(rendered, "filesystem [AlphaSupported]");
    }

    [TestMethod]
    public void ServiceShortTermMemoryPage_ShouldRenderSummary()
    {
        var snapshot = new ServiceShortTermMemorySnapshot
        {
            CurrentTime = DateTimeOffset.UtcNow,
            BaseUrl = "http://localhost:5079/",
            Summary = new ShortTermMemorySummary
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                RawEventCount = 3,
                WorkingItemCount = 5,
                ActiveTaskCount = 1,
                RecentDecisionCount = 1,
                OpenQuestionCount = 1,
                KnownIssueCount = 1,
                RecentWarningCount = 1,
                ActiveTasks =
                [
                    new ShortTermWorkingItem { ItemId = "task-1", Kind = "ActiveTask", Status = "active", Summary = "active task" }
                ],
                RecentDecisions =
                [
                    new ShortTermWorkingItem { ItemId = "decision-1", Kind = "RecentDecision", Status = "recorded", Summary = "decision made" }
                ],
                OpenQuestions =
                [
                    new ShortTermWorkingItem { ItemId = "question-1", Kind = "OpenQuestion", Status = "open", Summary = "open question" }
                ],
                KnownIssues =
                [
                    new ShortTermWorkingItem { ItemId = "issue-1", Kind = "KnownIssue", Status = "open", Summary = "known issue" }
                ],
                RecentWarnings =
                [
                    new ShortTermWorkingItem { ItemId = "warning-1", Kind = "RecentWarning", Status = "warning", Summary = "recent warning" }
                ]
            },
            RawEvents =
            [
                new ShortTermRawEvent { EventId = "event-1", EventKind = "ingest_succeeded", SequenceId = 1, Source = "service", Tags = ["note"] }
            ],
            ArchiveSummary = new ShortTermArchiveSummary
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                ArchivedRawEventCount = 1,
                ArchivedWorkingItemCount = 1,
                ArchivedKnownIssueCount = 1
            },
            ArchiveItems = new ShortTermArchiveItemsResponse
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                RawEvents =
                [
                    new ShortTermRawEvent { EventId = "archived-raw-1", EventKind = "ingest_duplicate", Source = "service" }
                ],
                WorkingItems =
                [
                    new ShortTermWorkingItem { ItemId = "archived-issue-1", Kind = "KnownIssue", Status = "resolved", Summary = "archived issue" }
                ]
            },
            RecentRuns =
            [
                new ShortTermCompactionRun
                {
                    RunId = "run-1",
                    WorkspaceId = "workspace-test",
                    CollectionId = "collection-test",
                    Trigger = "Manual",
                    StartedAt = DateTimeOffset.UtcNow,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ArchivedRawEvents = 1,
                    ArchivedWorkingItems = 1,
                    RemovedDuplicates = 1
                }
            ],
            Maintenance = new ShortTermMaintenanceStatusResponse
            {
                Enabled = true,
                IsRunning = false,
                RunOnStartup = true,
                IntervalSeconds = 60,
                LastRun = new ShortTermCompactionRun
                {
                    RunId = "run-1",
                    WorkspaceId = "workspace-test",
                    CollectionId = "collection-test",
                    Trigger = "Manual",
                    StartedAt = DateTimeOffset.UtcNow,
                    CompletedAt = DateTimeOffset.UtcNow
                }
            }
        };

        var rendered = ServiceOperationalRenderer.RenderShortTermMemory(snapshot);

        StringAssert.Contains(rendered, "RawEventCount    : 3");
        StringAssert.Contains(rendered, "WorkingItemCount : 5");
        StringAssert.Contains(rendered, "RecentWarnings   : 1");
        StringAssert.Contains(rendered, "Maintenance");
        StringAssert.Contains(rendered, "ArchivedRawCount        : 1");
        StringAssert.Contains(rendered, "archived-issue-1");
        StringAssert.Contains(rendered, "Short-Term Compaction Runs");
        StringAssert.Contains(rendered, "run-1 [Manual]");
        StringAssert.Contains(rendered, "task-1 [ActiveTask/active] active task");
        StringAssert.Contains(rendered, "warning-1 [RecentWarning/warning] recent warning");
        StringAssert.Contains(rendered, "event-1");
    }

    [TestMethod]
    public async Task ServiceShortTermCompactionAndArchiveSummary_ShouldRenderResult()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/memory/short-term/compact", request.RequestUri?.AbsolutePath);
            return Json(new ShortTermMemoryCompactionResult
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                ActiveRawEventCountBefore = 4,
                ActiveRawEventCountAfter = 2,
                ActiveWorkingItemCountBefore = 3,
                ActiveWorkingItemCountAfter = 1,
                MergedWorkingItems = 2,
                MergedByWorkingKeyGroups = 1,
                MergedByTitleGroups = 0,
                ArchivedRawEventCount = 2,
                ArchivedWorkingItemCount = 2,
                ArchivedResolvedWorkingItemCount = 1,
                EvidenceRefsTrimmed = 1,
                CompletedAt = DateTimeOffset.UtcNow
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/short-term/archive/summary", request.RequestUri?.AbsolutePath);
            return Json(new ShortTermArchiveSummary
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                ArchivedRawEventCount = 2,
                ArchivedWorkingItemCount = 2,
                ArchivedResolvedWorkingItemCount = 1,
                ArchivedActiveTaskCount = 1,
                ArchivedRecentDecisionCount = 0,
                ArchivedOpenQuestionCount = 0,
                ArchivedKnownIssueCount = 1,
                ArchivedRecentWarningCount = 0,
                LatestArchivedAt = DateTimeOffset.UtcNow
            });
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var state = ControlRoomService.CreateState(
            "filesystem",
            FileStorageOptions.DefaultRootPath,
            "workspace-test",
            "collection-test",
            ControlRoomMode.Service,
            "http://localhost:5079/",
            http);
        var service = new ControlRoomService(state);

        var compact = await service.CompactServiceShortTermMemoryAsync();
        var archive = await service.GetServiceShortTermArchiveSummaryAsync();
        var compactRendered = ServiceOperationalRenderer.RenderShortTermCompactionResult(compact);
        var archiveRendered = ServiceOperationalRenderer.RenderShortTermArchiveSummary(archive);

        StringAssert.Contains(compactRendered, "MergedWorkingItems     : 2");
        StringAssert.Contains(compactRendered, "ArchivedRawEvents      : 2");
        StringAssert.Contains(archiveRendered, "ArchivedWorkingItems    : 2");
        StringAssert.Contains(archiveRendered, "ArchivedKnownIssues     : 1");
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public void ServicePromotionCandidatesPage_ShouldRenderCandidates()
    {
        var snapshot = new ServicePromotionCandidatesSnapshot
        {
            CurrentTime = DateTimeOffset.UtcNow,
            BaseUrl = "http://localhost:5079/",
            Candidates =
            [
                new ShortTermPromotionCandidate
                {
                    CandidateId = "candidate-1",
                    WorkspaceId = "workspace-test",
                    CollectionId = "collection-test",
                    SessionId = "session-1",
                    SourceWorkingItemId = "decision-1",
                    Kind = "RecentDecision",
                    Title = "统一错误响应契约",
                    Summary = "统一错误响应契约",
                    SuggestedTargetLayer = "CandidateMemory",
                    Reason = "RecentDecision 具备复用价值，建议进入候选记忆层。",
                    Confidence = 0.88,
                    Importance = 0.88,
                    EvidenceRefs = ["event-1", "source:decision-1"],
                    Tags = ["RecentDecision"],
                    CreatedAt = DateTimeOffset.UtcNow,
                    Status = PromotionCandidateStatus.Candidate
                }
            ]
        };

        var rendered = ServiceOperationalRenderer.RenderPromotionCandidates(snapshot);

        StringAssert.Contains(rendered, "candidate-1");
        StringAssert.Contains(rendered, "CandidateMemory");
        StringAssert.Contains(rendered, "evidenceRefs");
        StringAssert.Contains(rendered, "RecentDecision 具备复用价值");
    }

    [TestMethod]
    public void ServicePromotionCandidateExplain_ShouldRenderExplanation()
    {
        var explanation = new ShortTermPromotionCandidateExplanation
        {
            CandidateId = "candidate-1",
            Candidate = new ShortTermPromotionCandidate
            {
                CandidateId = "candidate-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                SourceWorkingItemId = "issue-1",
                Kind = "KnownIssue",
                Title = "缓存击穿",
                Summary = "缓存击穿",
                SuggestedTargetLayer = "CandidateMemory",
                Reason = "KnownIssue 需要跨轮次保留。",
                Confidence = 0.82,
                Importance = 0.91,
                EvidenceRefs = ["event-1", "source:issue-1"],
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
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Kind = "KnownIssue",
                Status = "open",
                Summary = "缓存击穿"
            },
            SourceRawEvents =
            [
                new ShortTermRawEvent
                {
                    EventId = "event-1",
                    EventKind = "ingest_succeeded",
                    Source = "chat"
                }
            ],
            EvidenceRefs = ["event-1", "source:issue-1"],
            Reason = "KnownIssue 需要跨轮次保留。",
            RuleName = "known-issue-to-candidate-memory",
            RuleVersion = "v1",
            PolicyVersion = "v1",
            Confidence = 0.82,
            Importance = 0.91,
            SuggestedTargetLayer = "CandidateMemory",
            DedupeKey = "dedupe-1",
            SourceFingerprint = "fingerprint-1",
            GeneratedBy = "rule-based",
            Warnings = ["source raw event count is partial"]
        };

        var rendered = ServiceOperationalRenderer.RenderPromotionCandidateExplanation(explanation);

        StringAssert.Contains(rendered, "CandidateId      : candidate-1");
        StringAssert.Contains(rendered, "known-issue-to-candidate-memory");
        StringAssert.Contains(rendered, "SourceWorkingItem");
        StringAssert.Contains(rendered, "event-1");
        StringAssert.Contains(rendered, "Warnings");
    }

    [TestMethod]
    public void ServicePromotionCandidateReview_ShouldRenderResultAndHistory()
    {
        var review = new PromotionCandidateReviewRecord
        {
            ReviewId = "review-1",
            CandidateId = "candidate-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Action = "accept",
            FromStatus = PromotionCandidateStatus.Candidate,
            ToStatus = PromotionCandidateStatus.Accepted,
            Reviewer = "tester",
            Reason = "接受为候选记忆。",
            TargetItemId = "mem:stp:candidate-1",
            TargetItemKind = "memory",
            TargetLayer = "CandidateMemory",
            EvidenceRefs = ["event-1"],
            CreatedAt = DateTimeOffset.UtcNow,
            ReviewedAt = DateTimeOffset.UtcNow
        };
        var response = new ReviewPromotionCandidateResponse
        {
            OperationId = "review-op-1",
            CandidateId = "candidate-1",
            Action = "accept",
            Status = PromotionCandidateStatus.Accepted,
            ReviewId = review.ReviewId,
            Reviewer = review.Reviewer,
            Reason = review.Reason,
            ReviewedAt = review.ReviewedAt,
            TargetItemId = review.TargetItemId,
            CreatedTargetItemId = review.TargetItemId,
            TargetItemKind = review.TargetItemKind,
            TargetLayer = review.TargetLayer,
            Review = review
        };

        var resultRendered = ServiceOperationalRenderer.RenderPromotionCandidateReviewResult(response);
        var historyRendered = ServiceOperationalRenderer.RenderPromotionCandidateReviews([review]);

        StringAssert.Contains(resultRendered, "review-op-1");
        StringAssert.Contains(resultRendered, "mem:stp:candidate-1");
        StringAssert.Contains(resultRendered, "Accepted");
        StringAssert.Contains(resultRendered, "tester");
        StringAssert.Contains(resultRendered, "接受为候选记忆。");
        StringAssert.Contains(historyRendered, "review-1");
        StringAssert.Contains(historyRendered, "Candidate -> Accepted");
        StringAssert.Contains(historyRendered, "reviewedAt");
        StringAssert.Contains(historyRendered, "event-1");
    }

    [TestMethod]
    public void ServiceStableReviewCandidatesPage_ShouldRenderCandidatesAndExplanation()
    {
        var candidate = CreateStableReviewCandidate("src-1", StableReviewValidationStatuses.Ready, []);
        var snapshot = new ServiceStableReviewCandidatesSnapshot
        {
            CurrentTime = DateTimeOffset.UtcNow,
            BaseUrl = "http://localhost:5079/",
            Candidates = [candidate],
            ValidationStatus = StableReviewValidationStatuses.Ready
        };
        var explanation = new StableReviewCandidateExplanation
        {
            StableReviewCandidateId = candidate.StableReviewCandidateId,
            Candidate = candidate,
            SourceCandidate = new ShortTermPromotionCandidate
            {
                CandidateId = "candidate-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                SourceWorkingItemId = "decision-1",
                Kind = "RecentDecision",
                Title = "稳定评审候选",
                Summary = "稳定评审候选",
                SuggestedTargetLayer = "CandidateMemory",
                Status = PromotionCandidateStatus.Accepted,
                EvidenceRefs = ["event-1"]
            },
            SourceLearningCase = CreateLearningCase("case-1", "PositivePromotionSample", ContextFeedbackSignal.Positive, ContextFailureType.None),
            SourceMemoryTarget = new ContextMemoryItem
            {
                Id = "memory-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Layer = ContextMemoryLayer.Structured,
                Status = ContextMemoryStatus.Candidate,
                Type = "recent_decision",
                SourceRefs = ["candidate-1", "event-1"]
            },
            EvidenceRefs = ["event-1"],
            Reason = "ready",
            ValidationStatus = StableReviewValidationStatuses.Ready
        };

        var rendered = ServiceOperationalRenderer.RenderStableReviewCandidates(snapshot);
        var detail = ServiceOperationalRenderer.RenderStableReviewCandidateDetail(candidate);
        var explain = ServiceOperationalRenderer.RenderStableReviewCandidateExplanation(explanation);

        StringAssert.Contains(rendered, "Service Stable Review Candidates");
        StringAssert.Contains(rendered, "src-1");
        StringAssert.Contains(rendered, "StableMemory");
        StringAssert.Contains(rendered, "Ready");
        StringAssert.Contains(rendered, "source       : candidate=candidate-1");
        StringAssert.Contains(rendered, "event-1");
        StringAssert.Contains(detail, "ValidationStatus        : Ready");
        StringAssert.Contains(explain, "Source Promotion Candidate");
        StringAssert.Contains(explain, "Source Learning Case");
        StringAssert.Contains(explain, "Source Target Memory");
    }

    [TestMethod]
    public void ServiceStableReviewDecision_ShouldRenderResultAndHistory()
    {
        var review = new StableReviewRecord
        {
            ReviewId = "stable-review-1",
            StableReviewCandidateId = "src-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Action = "accept",
            FromStatus = StableReviewCandidateStatuses.Candidate,
            ToStatus = StableReviewCandidateStatuses.Accepted,
            Reviewer = "tester",
            Reason = "进入稳定记忆。",
            StableTargetItemId = "stable:mem:src-1",
            StableTargetItemKind = "memory",
            TargetLayer = "StableMemory",
            SourcePromotionCandidateId = "candidate-1",
            SourceTargetItemId = "memory-1",
            SourceLearningCaseId = "case-1",
            EvidenceRefs = ["event-1"],
            ValidationStatus = StableReviewValidationStatuses.ReadyForReview,
            CreatedAt = DateTimeOffset.UtcNow,
            ReviewedAt = DateTimeOffset.UtcNow
        };
        var result = new StableReviewDecisionResult
        {
            OperationId = "stable-review-op-1",
            StableReviewCandidateId = "src-1",
            Action = "accept",
            Status = StableReviewCandidateStatuses.Accepted,
            ReviewId = review.ReviewId,
            Reviewer = review.Reviewer,
            Reason = review.Reason,
            ReviewedAt = review.ReviewedAt,
            CreatedStableTargetItemId = review.StableTargetItemId,
            StableTargetItemKind = review.StableTargetItemKind,
            TargetLayer = review.TargetLayer,
            ValidationStatus = StableReviewValidationStatuses.ReadyForReview,
            Review = review
        };

        var renderedResult = ServiceOperationalRenderer.RenderStableReviewDecisionResult(result);
        var renderedHistory = ServiceOperationalRenderer.RenderStableReviewCandidateReviews([review]);

        StringAssert.Contains(renderedResult, "stable-review-op-1");
        StringAssert.Contains(renderedResult, "stable:mem:src-1");
        StringAssert.Contains(renderedResult, "ReadyForReview");
        StringAssert.Contains(renderedHistory, "Stable Review Decision History");
        StringAssert.Contains(renderedHistory, "Candidate -> Accepted");
        StringAssert.Contains(renderedHistory, "event-1");
        StringAssert.Contains(renderedHistory, "source         : promotion=candidate-1");
    }

    [TestMethod]
    public void ServiceProvenance_ShouldRenderSourceChainAndDiagnostics()
    {
        var provenance = new ContextProvenanceResponse
        {
            ItemId = "stable:mem:src-1",
            TargetItemKind = "memory",
            TargetMemoryItem = new ContextMemoryItem
            {
                Id = "stable:mem:src-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Layer = ContextMemoryLayer.Stable,
                Status = ContextMemoryStatus.Stable,
                Type = "recent_decision",
                SourceRefs = ["src-1", "candidate-1", "event-1"]
            },
            StableReviewCandidate = CreateStableReviewCandidate("src-1", StableReviewValidationStatuses.ReadyForReview, []),
            PromotionCandidate = new ShortTermPromotionCandidate
            {
                CandidateId = "candidate-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                SourceWorkingItemId = "decision-1",
                Kind = "RecentDecision",
                SuggestedTargetLayer = "CandidateMemory",
                Status = PromotionCandidateStatus.Accepted
            },
            FeedbackSignal = CreatePromotionFeedbackSignal("feedback-1", "Accepted"),
            LearningCase = CreateLearningCase("case-1", "PositivePromotionSample", ContextFeedbackSignal.Positive, ContextFailureType.None),
            SourceWorkingItem = new ShortTermWorkingItem
            {
                ItemId = "decision-1",
                Kind = "RecentDecision",
                Status = "recorded",
                Summary = "source working item"
            },
            EvidenceRefs = ["event-1"],
            StableReviewHistory =
            [
                new StableReviewRecord
                {
                    ReviewId = "stable-review-1",
                    StableReviewCandidateId = "src-1",
                    Action = "accept",
                    FromStatus = StableReviewCandidateStatuses.Candidate,
                    ToStatus = StableReviewCandidateStatuses.Accepted,
                    StableTargetItemId = "stable:mem:src-1"
                }
            ],
            Diagnostics =
            [
                new StableDiagnosticWarning
                {
                    Code = "MissingSourceLink",
                    Message = "missing source link"
                }
            ],
            MissingLinks = ["sourceFeedbackId"],
            Warnings = ["missing source link: sourceFeedbackId"]
        };

        var rendered = ServiceOperationalRenderer.RenderProvenance(provenance);

        StringAssert.Contains(rendered, "Service Provenance");
        StringAssert.Contains(rendered, "Stable Review Candidate");
        StringAssert.Contains(rendered, "Promotion Candidate");
        StringAssert.Contains(rendered, "Feedback Signal");
        StringAssert.Contains(rendered, "Learning Case");
        StringAssert.Contains(rendered, "Source Working Item");
        StringAssert.Contains(rendered, "MissingSourceLink");
        StringAssert.Contains(rendered, "MissingLinks");
    }

    [TestMethod]
    public async Task ServiceStableReviewScreen_ShouldRenderProvenance()
    {
        var candidate = CreateStableReviewCandidate("src-1", StableReviewValidationStatuses.ReadyForReview, []);
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/stable-review/candidates", request.RequestUri?.AbsolutePath);
            return Json(new[] { candidate });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/provenance/src-1", request.RequestUri?.AbsolutePath);
            return Json(new ContextProvenanceResponse
            {
                ItemId = "src-1",
                StableReviewCandidate = candidate,
                EvidenceRefs = ["event-1"]
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/stable-review/candidates", request.RequestUri?.AbsolutePath);
            return Json(new[] { candidate });
        });
        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var state = ControlRoomService.CreateState(
            "filesystem",
            FileStorageOptions.DefaultRootPath,
            "workspace-test",
            "collection-test",
            ControlRoomMode.Service,
            "http://localhost:5079/",
            http);
        var service = new ControlRoomService(state);
        var originalIn = Console.In;
        var originalOut = Console.Out;
        using var input = new StringReader("p src-1\nb\n");
        using var output = new StringWriter();

        try
        {
            Console.SetIn(input);
            Console.SetOut(output);
            var action = await ServiceStableReviewCandidatesScreen.ShowAsync(service);

            Assert.AreEqual(ControlRoomActionKind.Back, action);
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }

        Assert.AreEqual(0, handlers.Count);
        StringAssert.Contains(output.ToString(), "Service Provenance");
        StringAssert.Contains(output.ToString(), "src-1");
    }

    [TestMethod]
    public async Task ServiceStableReviewScreen_ShouldRequireConfirmationBeforeAccept()
    {
        var candidate = CreateStableReviewCandidate("src-1", StableReviewValidationStatuses.ReadyForReview, []);
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/stable-review/candidates", request.RequestUri?.AbsolutePath);
            return Json(new[] { candidate });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/stable-review/candidates/src-1", request.RequestUri?.AbsolutePath);
            return Json(candidate);
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/stable-review/candidates/src-1/explain", request.RequestUri?.AbsolutePath);
            return Json(new StableReviewCandidateExplanation
            {
                StableReviewCandidateId = candidate.StableReviewCandidateId,
                Candidate = candidate,
                SourceCandidate = new ShortTermPromotionCandidate
                {
                    CandidateId = "candidate-1",
                    WorkspaceId = "workspace-test",
                    CollectionId = "collection-test",
                    SourceWorkingItemId = "decision-1",
                    Kind = "RecentDecision",
                    Title = "稳定评审候选",
                    Summary = "稳定评审候选",
                    SuggestedTargetLayer = "CandidateMemory",
                    Status = PromotionCandidateStatus.Accepted,
                    EvidenceRefs = ["event-1"]
                },
                EvidenceRefs = ["event-1"],
                Reason = "ready",
                ValidationStatus = StableReviewValidationStatuses.ReadyForReview
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/memory/stable-review/candidates", request.RequestUri?.AbsolutePath);
            return Json(new[] { candidate });
        });
        var acceptCalls = 0;
        using var http = CreateHttpClient(request =>
        {
            if (request.Method == HttpMethod.Post
                && string.Equals(request.RequestUri?.AbsolutePath, "/api/memory/stable-review/candidates/src-1/accept", StringComparison.OrdinalIgnoreCase))
            {
                acceptCalls++;
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return handlers.Dequeue().Invoke(request);
        });
        var state = ControlRoomService.CreateState(
            "filesystem",
            FileStorageOptions.DefaultRootPath,
            "workspace-test",
            "collection-test",
            ControlRoomMode.Service,
            "http://localhost:5079/",
            http);
        var service = new ControlRoomService(state);
        var originalIn = Console.In;
        var originalOut = Console.Out;
        using var input = new StringReader("a src-1\nNO\nb\n");
        using var output = new StringWriter();

        try
        {
            Console.SetIn(input);
            Console.SetOut(output);
            var action = await ServiceStableReviewCandidatesScreen.ShowAsync(service);

            Assert.AreEqual(ControlRoomActionKind.Back, action);
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }

        Assert.AreEqual(0, acceptCalls);
        Assert.AreEqual(0, handlers.Count);
        StringAssert.Contains(output.ToString(), "Type YES");
        StringAssert.Contains(output.ToString(), "Stable review action canceled.");
    }

    [TestMethod]
    public async Task ServiceLearningPage_ShouldFetchRecordsAndCases()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/learning/records", request.RequestUri?.AbsolutePath);
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "workspaceId=workspace-test");
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "collectionId=collection-test");
            return Json(new[]
            {
                CreateLearningRecord("record-positive", ContextFeedbackSignal.Positive, ContextFailureType.None),
                CreateLearningRecord("record-negative", ContextFeedbackSignal.Negative, ContextFailureType.PromotionFalsePositive),
                CreateLearningRecord("record-stale", ContextFeedbackSignal.Stale, ContextFailureType.StaleCandidate)
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/learning/cases", request.RequestUri?.AbsolutePath);
            return Json(new[]
            {
                CreateLearningCase("case-positive", "PositivePromotionSample", ContextFeedbackSignal.Positive, ContextFailureType.None),
                CreateLearningCase("case-negative", "PromotionFalsePositive", ContextFeedbackSignal.Negative, ContextFailureType.PromotionFalsePositive)
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/learning/feedback", request.RequestUri?.AbsolutePath);
            return Json(new[]
            {
                CreatePromotionFeedbackSignal("feedback-positive", "Accepted"),
                CreatePromotionFeedbackSignal("feedback-negative", "Rejected")
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/learning/summary", request.RequestUri?.AbsolutePath);
            return Json(new ContextLearningSummary
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                RecordCount = 3,
                CaseCount = 2,
                PositiveCount = 1,
                NegativeCount = 1,
                StaleCount = 1,
                DraftCaseCount = 2,
                FailureTypeCounts = new Dictionary<ContextFailureType, int>
                {
                    [ContextFailureType.None] = 1,
                    [ContextFailureType.PromotionFalsePositive] = 1,
                    [ContextFailureType.StaleCandidate] = 1
                },
                CaseKindCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PositivePromotionSample"] = 1,
                    ["PromotionFalsePositive"] = 1
                }
            });
        });
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/learning/regression/cases", request.RequestUri?.AbsolutePath);
            return Json(Array.Empty<ContextLearningCase>());
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var state = ControlRoomService.CreateState(
            "filesystem",
            FileStorageOptions.DefaultRootPath,
            "workspace-test",
            "collection-test",
            ControlRoomMode.Service,
            "http://localhost:5079/",
            http);
        var service = new ControlRoomService(state);

        var snapshot = await service.GetServiceLearningSnapshotAsync();
        var rendered = ServiceOperationalRenderer.RenderLearning(snapshot);

        Assert.AreEqual(3, snapshot.Records.Count);
        Assert.AreEqual(2, snapshot.FeedbackSignals.Count);
        Assert.AreEqual(2, snapshot.Cases.Count);
        Assert.AreEqual(1, snapshot.PositiveCount);
        Assert.AreEqual(1, snapshot.NegativeCount);
        Assert.AreEqual(1, snapshot.StaleCount);
        StringAssert.Contains(rendered, "Service Context Learning");
        StringAssert.Contains(rendered, "positive=1 negative=1 stale=1");
        StringAssert.Contains(rendered, "PromotionFalsePositive");
        StringAssert.Contains(rendered, "Promotion Feedback Signals");
        StringAssert.Contains(rendered, "feedback-positive");
        StringAssert.Contains(rendered, "Recent Feedback");
        StringAssert.Contains(rendered, "Learning Cases");
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task ServicePlanningSnapshotPage_ShouldFetchAndRenderSnapshot()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/context/planning/snapshot", request.RequestUri?.AbsolutePath);
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "workspaceId=workspace-test");
            StringAssert.Contains(request.RequestUri?.Query ?? string.Empty, "collectionId=collection-test");
            return Json(new ContextPlanningSnapshot
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                ActiveTasks =
                [
                    new ShortTermWorkingItem
                    {
                        ItemId = "task-planning-1",
                        WorkspaceId = "workspace-test",
                        CollectionId = "collection-test",
                        Kind = "ActiveTask",
                        Title = "规划快照任务",
                        Summary = "展示 planning snapshot",
                        Status = "active",
                        Lifecycle = "Active",
                        Importance = 0.91
                    }
                ],
                RecentDecisions =
                [
                    new ShortTermWorkingItem
                    {
                        ItemId = "decision-working-1",
                        WorkspaceId = "workspace-test",
                        CollectionId = "collection-test",
                        Kind = "RecentDecision",
                        Title = "只读展示",
                        Summary = "Planning Snapshot 只读",
                        Status = "recorded",
                        Lifecycle = "Recent",
                        Importance = 0.85
                    }
                ],
                StableConstraints =
                [
                    new ContextConstraint
                    {
                        Id = "constraint-planning-1",
                        WorkspaceId = "workspace-test",
                        CollectionId = "collection-test",
                        Level = ConstraintLevel.Hard,
                        Status = ContextMemoryStatus.Stable,
                        Content = "planning snapshot 不影响 package"
                    }
                ],
                StablePreferences =
                [
                    new ContextMemoryItem
                    {
                        Id = "preference-planning-1",
                        WorkspaceId = "workspace-test",
                        CollectionId = "collection-test",
                        Layer = ContextMemoryLayer.Stable,
                        Status = ContextMemoryStatus.Stable,
                        Type = "preference",
                        Content = "展示保持简洁"
                    }
                ],
                DecisionRecords =
                [
                    new ContextMemoryItem
                    {
                        Id = "decision-record-1",
                        WorkspaceId = "workspace-test",
                        CollectionId = "collection-test",
                        Layer = ContextMemoryLayer.Stable,
                        Status = ContextMemoryStatus.Stable,
                        Type = "decision",
                        Content = "只读 planning 输入"
                    }
                ],
                LearningSignalsSummary = new ContextLearningSummary
                {
                    WorkspaceId = "workspace-test",
                    CollectionId = "collection-test",
                    RecordCount = 2,
                    CaseCount = 1,
                    PositiveCount = 1,
                    NegativeCount = 1,
                    FailureTypeCounts = new Dictionary<ContextFailureType, int>
                    {
                        [ContextFailureType.PromotionFalsePositive] = 1
                    }
                },
                PolicyVersion = "context-planning-snapshot-policy/v1",
                CreatedAt = DateTimeOffset.UtcNow
            });
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var state = ControlRoomService.CreateState(
            "filesystem",
            FileStorageOptions.DefaultRootPath,
            "workspace-test",
            "collection-test",
            ControlRoomMode.Service,
            "http://localhost:5079/",
            http);
        var service = new ControlRoomService(state);

        var snapshot = await service.GetServicePlanningSnapshotAsync();
        var rendered = ServiceOperationalRenderer.RenderPlanningSnapshot(snapshot);

        Assert.AreEqual("workspace-test", snapshot.Snapshot.WorkspaceId);
        Assert.AreEqual(1, snapshot.Snapshot.ActiveTasks.Count);
        StringAssert.Contains(rendered, "Service Planning Snapshot");
        StringAssert.Contains(rendered, "Active Tasks");
        StringAssert.Contains(rendered, "task-planning-1");
        StringAssert.Contains(rendered, "Stable Constraints");
        StringAssert.Contains(rendered, "constraint-planning-1");
        StringAssert.Contains(rendered, "Decision Records");
        StringAssert.Contains(rendered, "decision-record-1");
        StringAssert.Contains(rendered, "Learning Signals Summary");
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public async Task ServicePlanningProposalPage_ShouldFetchAndRenderProposal()
    {
        var handlers = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
        handlers.Enqueue(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/context/planning/propose", request.RequestUri?.AbsolutePath);
            var payload = JsonSerializer.Deserialize<ContextPlanningProposalRequest>(
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(),
                JsonOptions);
            Assert.IsNotNull(payload);
            Assert.AreEqual("workspace-test", payload!.WorkspaceId);
            Assert.AreEqual("collection-test", payload.CollectionId);
            Assert.AreEqual("当前任务下一步", payload.CurrentInput);

            return Json(new RetrievalPlanProposal
            {
                OperationId = "proposal-controlroom-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Intent = "CurrentTask",
                Mode = "Chat",
                UseExact = true,
                UseKeyword = true,
                UseShortTermMemory = true,
                UseWorkingMemory = true,
                UseStableMemory = true,
                UseRelations = false,
                UseVector = false,
                AuditMode = false,
                ConflictMode = false,
                KeywordTopK = 18,
                MemoryTopK = 22,
                RelationTopK = 0,
                VectorTopK = 0,
                FinalTopK = 20,
                Confidence = 0.8,
                Reasons = ["snapshot:activeTasks=1", "snapshot.activeTask:task-p2"],
                Warnings = ["previewOnly: proposal does not execute retrieval or mutate retrieval output"]
            });
        });

        using var http = CreateHttpClient(request => handlers.Dequeue().Invoke(request));
        var state = ControlRoomService.CreateState(
            "filesystem",
            FileStorageOptions.DefaultRootPath,
            "workspace-test",
            "collection-test",
            ControlRoomMode.Service,
            "http://localhost:5079/",
            http);
        var service = new ControlRoomService(state);

        var snapshot = await service.ProposeServiceRetrievalPlanAsync("当前任务下一步");
        var rendered = ServiceOperationalRenderer.RenderPlanningProposal(snapshot);

        Assert.AreEqual("CurrentTask", snapshot.Proposal.Intent);
        StringAssert.Contains(rendered, "Service Planning Proposal");
        StringAssert.Contains(rendered, "Intent     : CurrentTask");
        StringAssert.Contains(rendered, "Mode       : Chat");
        StringAssert.Contains(rendered, "Exact=True");
        StringAssert.Contains(rendered, "Keyword=18");
        StringAssert.Contains(rendered, "snapshot.activeTask:task-p2");
        StringAssert.Contains(rendered, "previewOnly");
        Assert.AreEqual(0, handlers.Count);
    }

    [TestMethod]
    public void ServiceRankerShadowDebugRenderer_ShouldRenderScoreComparison()
    {
        var snapshot = new ServiceRankerShadowDebugSnapshot
        {
            CurrentTime = DateTimeOffset.UtcNow,
            BaseUrl = "http://localhost:5079/",
            Response = new LifecycleAwareRankerShadowDebugResponse
            {
                OperationId = "ranker-debug-1",
                RetrievalOperationId = "retrieval-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
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
                        CandidateId = "memory:active-rule-v2",
                        Kind = "Stable",
                        Type = "memory",
                        SectionName = "Stable",
                        Selected = true,
                        LegacyRank = 1,
                        ShadowRank = 1,
                        LegacyScore = 10,
                        LifecycleAwareScore = 22,
                        ScoreDelta = 12,
                        Reason = "current_version_boost",
                        LifecycleFeatures = new LifecycleAwareFeatureSet
                        {
                            IsCurrentVersion = true,
                            LifecycleConfidence = 0.7
                        }
                    },
                    new LifecycleAwareRankerShadowCandidateScore
                    {
                        CandidateId = "memory:deprecated-rule-v1",
                        Kind = "historical_context",
                        Type = "memory",
                        SectionName = "historical_context",
                        Selected = false,
                        LegacyRank = 2,
                        ShadowRank = 2,
                        LegacyScore = 20,
                        LifecycleAwareScore = -18,
                        ScoreDelta = -38,
                        Reason = "deprecated_demotion;historical_demotion",
                        LifecycleFeatures = new LifecycleAwareFeatureSet
                        {
                            IsDeprecated = true,
                            IsHistorical = true,
                            LifecycleConfidence = 0.69
                        }
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
                ],
                HistoricalDemotions =
                [
                    new LifecycleAwareRankerShadowCandidateScore
                    {
                        CandidateId = "memory:deprecated-rule-v1",
                        ScoreDelta = -38,
                        Reason = "deprecated_demotion;historical_demotion"
                    }
                ],
                CurrentActivePromotions =
                [
                    new LifecycleAwareRankerShadowCandidateScore
                    {
                        CandidateId = "memory:active-rule-v2",
                        ScoreDelta = 12,
                        Reason = "current_version_boost"
                    }
                ]
            },
            TraceQualitySummary = new RankerShadowTraceQualityReport
            {
                TraceCount = 1,
                CandidateScoreCount = 1,
                DeprecatedDemotionCount = 1,
                HistoricalDemotionCount = 0,
                VersionConflictFixCount = 0,
                CurrentVersionPromotionCount = 0,
                MustHitDemotedCount = 0,
                MustNotHitPromotedCount = 0,
                AverageScoreDelta = -38,
                MaxPositiveDelta = 0,
                MaxNegativeDelta = -38,
                RecommendedNextStep = RankerShadowTraceRecommendedNextSteps.KeepShadowOnly
            },
            RecentShadowTraces =
            [
                new LifecycleAwareRankerShadowTraceRecord
                {
                    RetrievalId = "retrieval-shadow-1",
                    WorkspaceId = "workspace-test",
                    CollectionId = "collection-test",
                    Query = "current rule",
                    Profile = "lifecycle-aware-v1",
                    CreatedAt = DateTimeOffset.UtcNow,
                    CandidateScores =
                    [
                        new LifecycleAwareRankerShadowCandidateScore
                        {
                            CandidateId = "memory:deprecated-rule-v1",
                            ScoreDelta = -38,
                            Reason = "deprecated_demotion"
                        }
                    ],
                    DeprecatedDemotions =
                    [
                        new LifecycleAwareRankerShadowCandidateScore
                        {
                            CandidateId = "memory:deprecated-rule-v1",
                            ScoreDelta = -38,
                            Reason = "deprecated_demotion"
                        }
                    ]
                }
            ]
        };

        var rendered = ServiceOperationalRenderer.RenderRankerShadowDebug(snapshot);

        StringAssert.Contains(rendered, "Service Ranker Shadow Debug");
        StringAssert.Contains(rendered, "Candidate Score Comparison");
        StringAssert.Contains(rendered, "memory:deprecated-rule-v1");
        StringAssert.Contains(rendered, "deprecated_demotion");
        StringAssert.Contains(rendered, "current_version_boost");
        StringAssert.Contains(rendered, "FormalChanged : False");
        StringAssert.Contains(rendered, "SelectedChanged: False");
        StringAssert.Contains(rendered, "Trace Quality Summary");
        StringAssert.Contains(rendered, "next=KeepShadowOnly");
        StringAssert.Contains(rendered, "Recent Shadow Traces");
        StringAssert.Contains(rendered, "retrieval-shadow-1");
    }

    [TestMethod]
    public void ServiceLearningRenderer_ShouldRenderRecordsCasesAndFailureSummary()
    {
        var snapshot = new ServiceLearningSnapshot
        {
            CurrentTime = DateTimeOffset.UtcNow,
            BaseUrl = "http://localhost:5079/",
            FeedbackSignals =
            [
                CreatePromotionFeedbackSignal("feedback-1", "Accepted")
            ],
            Records =
            [
                CreateLearningRecord("record-1", ContextFeedbackSignal.Positive, ContextFailureType.None),
                CreateLearningRecord("record-2", ContextFeedbackSignal.Negative, ContextFailureType.PromotionFalsePositive)
            ],
            Cases =
            [
                CreateLearningCase("case-1", "PositivePromotionSample", ContextFeedbackSignal.Positive, ContextFailureType.None),
                CreateLearningCase("case-2", "PromotionFalsePositive", ContextFeedbackSignal.Negative, ContextFailureType.PromotionFalsePositive)
            ],
            RegressionCases =
            [
                CreateLearningCase("case-regression", "PositivePromotionSample", ContextFeedbackSignal.Positive, ContextFailureType.None, ContextLearningCaseStatus.ActiveRegression)
            ],
            Summary = new ContextLearningSummary
            {
                RecordCount = 2,
                CaseCount = 2,
                PositiveCount = 1,
                NegativeCount = 1,
                DraftCaseCount = 1,
                ActiveRegressionCaseCount = 1,
                FailureTypeCounts = new Dictionary<ContextFailureType, int>
                {
                    [ContextFailureType.None] = 1,
                    [ContextFailureType.PromotionFalsePositive] = 1
                },
                CaseKindCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PositivePromotionSample"] = 1,
                    ["PromotionFalsePositive"] = 1
                }
            },
            PositiveCount = 1,
            NegativeCount = 1,
            StaleCount = 0,
            FailureTypeSummary = new Dictionary<ContextFailureType, int>
            {
                [ContextFailureType.None] = 1,
                [ContextFailureType.PromotionFalsePositive] = 1
            }
        };

        var rendered = ServiceOperationalRenderer.RenderLearning(snapshot);

        StringAssert.Contains(rendered, "record-1");
        StringAssert.Contains(rendered, "feedback-1");
        StringAssert.Contains(rendered, "Promotion Feedback Signals");
        StringAssert.Contains(rendered, "candidate-1");
        StringAssert.Contains(rendered, "case-2");
        StringAssert.Contains(rendered, "PromotionFalsePositive: 1");
        StringAssert.Contains(rendered, "activeRegression=1");
        StringAssert.Contains(rendered, "Active Regression Cases");
        StringAssert.Contains(rendered, "evidence");
    }

    [TestMethod]
    public void ServiceDashboardRenderer_ShouldRenderProviderCapabilities()
    {
        var snapshot = new ServiceDashboardSnapshot
        {
            CurrentTime = DateTimeOffset.UtcNow,
            BaseUrl = "http://localhost:5079/",
            Snapshot = new RuntimeSnapshotResponse
            {
                Status = CreateStatusResponse(),
                Readiness = CreateReadinessResponse(fromCache: true),
                DeepStatus = CreateDeepReadinessResponse(fromCache: false)
            }
        };

        var rendered = ServiceDashboardRenderer.RenderToString(snapshot);

        StringAssert.Contains(rendered, "filesystem");
        StringAssert.Contains(rendered, "AlphaSupported");
        StringAssert.Contains(rendered, "vector-store");
        StringAssert.Contains(rendered, "NotConfigured");
        StringAssert.Contains(rendered, "Short-Term Maintenance");
    }

    [TestMethod]
    public async Task ServiceUnavailable_ShouldExposeStructuredError()
    {
        using var http = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(JsonSerializer.Serialize(new ContextCoreErrorResponse
            {
                OperationId = "svc-op-1",
                ErrorCode = ContextCoreErrorCodes.StorageUnavailable,
                Message = "service unavailable",
                Target = "health.ready",
                TraceId = "trace-1",
                Details =
                [
                    new ContextCoreErrorDetail
                    {
                        Code = "storage_root_missing",
                        Target = "health.ready",
                        Message = "storage root missing"
                    }
                ]
            }, JsonOptions), Encoding.UTF8, "application/json")
        });

        var state = ControlRoomService.CreateState(
            "filesystem",
            FileStorageOptions.DefaultRootPath,
            "workspace-test",
            "collection-test",
            ControlRoomMode.Service,
            "http://localhost:5079/",
            http);
        var service = new ControlRoomService(state);

        var exception = await Assert.ThrowsExceptionAsync<ContextCoreApiException>(() =>
            service.GetServiceDashboardSnapshotAsync());
        var rendered = service.FormatServiceError(exception);

        StringAssert.Contains(rendered, "storage_unavailable");
        StringAssert.Contains(rendered, "health.ready");
        StringAssert.Contains(rendered, "storage root missing");
    }

    [TestMethod]
    public void ServiceDashboardInput_ShouldExposeServiceDashboardEntry()
    {
        var actionByLetter = ControlRoomInteraction.InterpretDashboardInput("s");
        var actionByNumber = ControlRoomInteraction.InterpretDashboardInput("13");

        Assert.AreEqual(ControlRoomActionKind.OpenServiceDashboard, actionByLetter.Kind);
        Assert.AreEqual(ControlRoomActionKind.OpenServiceDashboard, actionByNumber.Kind);
    }

    [TestMethod]
    public void ServiceDashboardInput_ShouldExposeServiceLearningEntry()
    {
        var actionByLetter = ControlRoomInteraction.InterpretDashboardInput("h");
        var actionByNumber = ControlRoomInteraction.InterpretDashboardInput("26");

        Assert.AreEqual(ControlRoomActionKind.OpenServiceLearning, actionByLetter.Kind);
        Assert.AreEqual(ControlRoomActionKind.OpenServiceLearning, actionByNumber.Kind);
    }

    [TestMethod]
    public void ServiceDashboardInput_ShouldExposeServiceStableReviewEntry()
    {
        var actionByLetter = ControlRoomInteraction.InterpretDashboardInput("z");
        var actionByNumber = ControlRoomInteraction.InterpretDashboardInput("27");

        Assert.AreEqual(ControlRoomActionKind.OpenServiceStableReviewCandidates, actionByLetter.Kind);
        Assert.AreEqual(ControlRoomActionKind.OpenServiceStableReviewCandidates, actionByNumber.Kind);
    }

    [TestMethod]
    public void ServiceDashboardInput_ShouldExposeServicePlanningSnapshotEntry()
    {
        var actionByLetter = ControlRoomInteraction.InterpretDashboardInput("x");
        var actionByNumber = ControlRoomInteraction.InterpretDashboardInput("28");

        Assert.AreEqual(ControlRoomActionKind.OpenServicePlanningSnapshot, actionByLetter.Kind);
        Assert.AreEqual(ControlRoomActionKind.OpenServicePlanningSnapshot, actionByNumber.Kind);
    }

    [TestMethod]
    public void ServiceDashboardInput_ShouldExposeServicePlanningProposalEntry()
    {
        var actionByLetter = ControlRoomInteraction.InterpretDashboardInput("f");
        var actionByNumber = ControlRoomInteraction.InterpretDashboardInput("29");

        Assert.AreEqual(ControlRoomActionKind.OpenServicePlanningProposal, actionByLetter.Kind);
        Assert.AreEqual(ControlRoomActionKind.OpenServicePlanningProposal, actionByNumber.Kind);
    }

    [TestMethod]
    public void ServiceDashboardInput_ShouldExposeServiceConstraintGapsEntry()
    {
        var actionByNumber = ControlRoomInteraction.InterpretDashboardInput("30");

        Assert.AreEqual(ControlRoomActionKind.OpenServiceConstraintGaps, actionByNumber.Kind);
    }

    [TestMethod]
    public void ServiceDashboardInput_ShouldExposeServiceCandidateConstraintsEntry()
    {
        var actionByNumber = ControlRoomInteraction.InterpretDashboardInput("31");

        Assert.AreEqual(ControlRoomActionKind.OpenServiceCandidateConstraints, actionByNumber.Kind);
    }

    [TestMethod]
    public void ServiceDashboardInput_ShouldExposeServiceRankerShadowDebugEntry()
    {
        var actionByNumber = ControlRoomInteraction.InterpretDashboardInput("34");

        Assert.AreEqual(ControlRoomActionKind.OpenServiceRankerShadowDebug, actionByNumber.Kind);
    }

    [TestMethod]
    public void DirectModeBehavior_ShouldRemainUnchanged()
    {
        var rendered = DashboardRenderer.RenderToString(new DashboardSnapshot
        {
            CurrentTime = DateTimeOffset.UtcNow,
            Mode = ControlRoomMode.Direct,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            StorageKind = "filesystem",
            RootPath = @"D:\context-core-data",
            Health =
            [
                new SystemHealthItem { Name = "storage", Status = "ok", Detail = "root" }
            ],
            Memory = new MemoryLayerSummary(),
            Jobs = new JobsSummary()
        }, autoRefresh: false, refreshSeconds: 2, width: 120);
        var state = ControlRoomService.CreateState(
            "filesystem",
            FileStorageOptions.DefaultRootPath,
            "workspace-test",
            "collection-test");

        Assert.AreEqual(ControlRoomMode.Direct, state.Mode);
        Assert.IsFalse(state.IsServiceMode);
        StringAssert.Contains(rendered, "策略");
        Assert.IsFalse(rendered.Contains("Ingest", StringComparison.OrdinalIgnoreCase));
    }

    private static RuntimeStatusResponse CreateStatusResponse()
    {
        return new RuntimeStatusResponse
        {
            Status = "ok",
            Utc = DateTimeOffset.UtcNow,
            Storage = new ContextCoreStorageInfo
            {
                Provider = "filesystem",
                RootPath = @"D:\context-core-data"
            },
            Jobs = new ContextCoreServiceJobQueueResponse
            {
                Queued = 2,
                Running = 1
            },
            RetrievalBaseline = "retrieval-orchestration-baseline-v1",
            Capabilities =
            [
                new ProviderCapabilityResponse
                {
                    Name = "filesystem",
                    State = "AlphaSupported",
                    Active = true,
                    Message = "filesystem ready"
                },
                new ProviderCapabilityResponse
                {
                    Name = "vector-store",
                    State = "NotConfigured",
                    Active = false,
                    Message = "vector disabled"
                }
            ],
            Readiness = CreateReadinessResponse(fromCache: false),
            ShortTermMaintenance = new ShortTermMaintenanceStatusResponse
            {
                Enabled = true,
                IsRunning = false,
                RunOnStartup = true,
                IntervalSeconds = 60,
                LastRun = new ShortTermCompactionRun
                {
                    RunId = "run-1",
                    WorkspaceId = "workspace-test",
                    CollectionId = "collection-test",
                    Trigger = "Scheduled",
                    StartedAt = DateTimeOffset.UtcNow,
                    CompletedAt = DateTimeOffset.UtcNow
                }
            }
        };
    }

    private static ContextCoreModelStatusResponse CreateModelStatusResponse()
    {
        return new ContextCoreModelStatusResponse
        {
            ApiProviders =
            [
                new ContextCoreModelApiProviderStatusResponse
                {
                    Name = "mock-api",
                    Provider = "mock",
                    Enabled = true,
                    EndpointConfigured = true,
                    ApiKeyRequired = false,
                    ApiKeyConfigured = false,
                    ApiKeySource = "none"
                }
            ],
            Routes =
            [
                new ContextCoreModelRouteStatusResponse
                {
                    Role = "GeneralCompression",
                    TaskKind = "Summarize",
                    ThinkingMode = "fast",
                    PrimaryModelName = "mock-fast",
                    Primary = new ContextCoreModelSelectionResponse
                    {
                        ModelName = "mock-fast",
                        Reason = "命中主模型"
                    },
                    Fallback = new ContextCoreModelSelectionResponse
                    {
                        ModelName = "mock-fallback",
                        Reason = "兜底"
                    }
                }
            ]
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
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceKind = "ShortTermPromotionCandidate",
            SourceId = "candidate-1",
            CandidateId = "candidate-1",
            ReviewId = "review-1",
            EventKind = signal switch
            {
                ContextFeedbackSignal.Positive => "PromotionAccepted",
                ContextFeedbackSignal.Stale => "PromotionExpired",
                _ => "PromotionRejected"
            },
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

    private static PromotionFeedbackSignal CreatePromotionFeedbackSignal(string feedbackId, string action)
    {
        return new PromotionFeedbackSignal
        {
            FeedbackId = feedbackId,
            CandidateId = "candidate-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Action = action,
            Reviewer = "tester",
            Reason = "review reason",
            SourceWorkingItemId = "working-1",
            CreatedTargetItemId = action == "Accepted" ? "memory-1" : null,
            SuggestedTargetLayer = "CandidateMemory",
            ActualTargetLayer = action == "Accepted" ? "CandidateMemory" : null,
            Confidence = 0.9,
            Importance = 0.9,
            EvidenceRefs = ["event-1"],
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static StableReviewCandidate CreateStableReviewCandidate(
        string stableReviewCandidateId,
        string validationStatus,
        IReadOnlyList<string> riskFlags)
    {
        return new StableReviewCandidate
        {
            StableReviewCandidateId = stableReviewCandidateId,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
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
            RiskFlags = riskFlags.ToArray(),
            ValidationStatus = validationStatus,
            Status = validationStatus == StableReviewValidationStatuses.Ready
                ? StableReviewCandidateStatuses.Candidate
                : StableReviewCandidateStatuses.Blocked,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static ConstraintGapCandidate CreateConstraintGap(string gapId)
    {
        return new ConstraintGapCandidate
        {
            GapId = gapId,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Source = "planning-optin-constraint-safety-report",
            SourceSampleId = "constraint-gap-sample",
            SourceOperationId = "planning-op-1",
            ExpectedConstraintText = "重复解释不应提升",
            SuggestedConstraintTitle = "重复解释不应提升",
            SuggestedConstraintScope = "Collection",
            SuggestedConstraintType = "Hard",
            Severity = ConstraintGapSeverity.High,
            Reason = "Expected hard constraint was missing.",
            EvidenceRefs = ["eval:planning"],
            Status = ConstraintGapStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static ContextConstraint CreateCandidateConstraint(string constraintId)
    {
        return new ContextConstraint
        {
            Id = constraintId,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Scope = ContextScope.Collection,
            Level = ConstraintLevel.User,
            Content = "重复解释不应提升",
            SourceRefs = ["eval:planning"],
            Status = ContextMemoryStatus.Candidate,
            Confidence = 0.9,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["createdFrom"] = "constraint_gap_accept",
                ["sourceConstraintGapId"] = "gap-1",
                ["sourceSampleId"] = "constraint-gap-sample",
                ["sourceOperationId"] = "planning-op-1",
                ["expectedConstraintText"] = "重复解释不应提升",
                ["evidenceRefs"] = "eval:planning"
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
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            CandidateKind = CandidateMemoryKinds.Memory,
            Type = "preference",
            Title = "Candidate preference",
            Summary = "Candidate preference",
            Content = "Candidate preference",
            Status = ContextMemoryStatus.Candidate,
            Lifecycle = CandidateMemoryLifecycle.Current,
            Importance = 0.8,
            Confidence = 0.9,
            EvidenceRefs = ["event-1"],
            SourceRefs = ["event-1"],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
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
            SourceType = "PromotionFeedbackSignal",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceRecordId = "record-1",
            SourceKind = "ShortTermPromotionCandidate",
            SourceId = "candidate-1",
            CaseKind = caseKind,
            Title = "learning case",
            Summary = "learning case",
            InputSummary = "learning case",
            ExpectedBehavior = "expected behavior",
            Signal = signal,
            FailureType = failureType,
            CorrectionReason = "review reason",
            Status = status,
            EvidenceRefs = ["event-1"],
            PositiveRefs = signal == ContextFeedbackSignal.Positive ? ["event-1"] : [],
            NegativeRefs = signal == ContextFeedbackSignal.Negative ? ["event-1"] : [],
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static RuntimeReadinessResponse CreateReadinessResponse(bool fromCache)
    {
        return new RuntimeReadinessResponse
        {
            Status = "ready",
            Message = "runtime ready",
            CheckedAt = DateTimeOffset.UtcNow,
            StorageProvider = "filesystem",
            ProductionReady = false,
            ProviderState = "ServiceReadyAlpha",
            RetrievalBaseline = "retrieval-orchestration-baseline-v1",
            FromCache = fromCache,
            CacheTtlSeconds = 8,
            Capabilities =
            [
                new ProviderCapabilityResponse
                {
                    Name = "filesystem",
                    State = "AlphaSupported",
                    Active = true,
                    Message = "filesystem ready"
                },
                new ProviderCapabilityResponse
                {
                    Name = "memory",
                    State = "TestOnly",
                    Active = false,
                    Message = "memory test only"
                }
            ],
            Checks =
            [
                new RuntimeProbeCheckResponse
                {
                    Name = "storage-root",
                    Status = "ok",
                    Message = "root writable",
                    Severity = "info",
                    HasSideEffect = true,
                    DurationMs = 1.3
                },
                new RuntimeProbeCheckResponse
                {
                    Name = "model-gateway",
                    Status = "warning",
                    Message = "no enabled model",
                    Severity = "warning",
                    HasSideEffect = false,
                    DurationMs = 0.4,
                    Warning = "模型网关不可用会降级，但不阻断 ready。"
                }
            ],
            Warnings = ["模型网关不可用会降级，但不阻断 ready。"],
            ShortTermMaintenance = new ShortTermMaintenanceStatusResponse
            {
                Enabled = false,
                IsRunning = false,
                RunOnStartup = false,
                IntervalSeconds = 300
            }
        };
    }

    private static RuntimeReadinessResponse CreateDeepReadinessResponse(bool fromCache)
    {
        return new RuntimeReadinessResponse
        {
            Status = "warning",
            Message = "deep warning",
            CheckedAt = DateTimeOffset.UtcNow,
            StorageProvider = "filesystem",
            ProductionReady = false,
            ProviderState = "ServiceReadyAlpha",
            RetrievalBaseline = "retrieval-orchestration-baseline-v1",
            FromCache = fromCache,
            CacheTtlSeconds = 8,
            ProbeScope = "__system__/__health__",
            Capabilities =
            [
                new ProviderCapabilityResponse
                {
                    Name = "postgres",
                    State = "Experimental",
                    Active = false,
                    Message = "experimental"
                }
            ],
            Checks =
            [
                new RuntimeProbeCheckResponse
                {
                    Name = "job-queue",
                    Status = "warning",
                    Message = "job queue probe warning",
                    Severity = "warning",
                    HasSideEffect = true,
                    DurationMs = 2.1,
                    Warning = "probe uses fixed id",
                    Detail = "system scope"
                }
            ],
            Warnings = ["probe uses fixed id"],
            ShortTermMaintenance = new ShortTermMaintenanceStatusResponse
            {
                Enabled = true,
                IsRunning = false,
                RunOnStartup = true,
                IntervalSeconds = 60,
                LastRun = new ShortTermCompactionRun
                {
                    RunId = "run-1",
                    WorkspaceId = "workspace-test",
                    CollectionId = "collection-test",
                    Trigger = "Scheduled",
                    StartedAt = DateTimeOffset.UtcNow,
                    CompletedAt = DateTimeOffset.UtcNow
                }
            }
        };
    }

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return new HttpClient(new StubHttpMessageHandler(handler))
        {
            BaseAddress = new Uri("http://localhost:5079/")
        };
    }

    private static HttpResponseMessage Json<T>(T value, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json")
        };
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


