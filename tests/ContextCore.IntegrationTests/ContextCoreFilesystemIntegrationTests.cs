using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Commands;
using ContextCore.ControlRoom.Services;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Embedding;
using ContextCore.ModelGateway;
using ContextCore.ModelGateway.Infrastructure;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;

namespace ContextCore.IntegrationTests;

/// <summary>覆盖文件系统端到端、模型路由、打包策略和控制室报告导出链路。</summary>
[TestClass]
public sealed class ContextCoreFilesystemIntegrationTests
{
    [TestMethod]
    public async Task FileSystemEndToEnd_ShouldPersistReloadAndWriteLogs()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var storage = new FileStorageOptions { RootPath = rootPath };
            var contextStore = new FileContextStore(storage);
            var memoryStore = new FileMemoryStore(storage);
            var relationStore = new FileRelationStore(storage);
            var constraintStore = new FileConstraintStore(storage);
            var globalStore = new FileGlobalContextStore(storage);
            var index = new FileContextIndex(storage);
            var eventSink = new FileContextEventSink(Path.Combine(rootPath, "logs"));

            var now = DateTimeOffset.UtcNow;
            var item = new ContextItem
            {
                Id = "raw-item-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Type = "note",
                Title = "原始条目",
                Content = "文件系统端到端测试内容。",
                ContentFormat = ContextContentFormat.PlainText,
                Tags = ["end-to-end", "file"],
                SourceRefs = ["source:raw-item-1"],
                Importance = 0.9,
                CreatedAt = now,
                UpdatedAt = now
            };

            var workingMemory = new ContextMemoryItem
            {
                Id = "memory-working-1",
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                Layer = ContextMemoryLayer.Working,
                Status = ContextMemoryStatus.Verified,
                Type = "memory",
                Content = "工作记忆内容。",
                ContentFormat = ContextContentFormat.PlainText,
                Tags = ["memory", "working"],
                SourceRefs = ["source:memory-working-1"],
                RelationRefs = ["relation:memory-working-1"],
                Importance = 0.7,
                Confidence = 0.8,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now
            };

            var stableMemory = new ContextMemoryItem
            {
                Id = "memory-stable-1",
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                Layer = ContextMemoryLayer.Stable,
                Status = ContextMemoryStatus.Stable,
                Type = "memory",
                Content = "稳定记忆内容。",
                ContentFormat = ContextContentFormat.PlainText,
                Tags = ["memory", "stable"],
                SourceRefs = ["source:memory-stable-1"],
                RelationRefs = ["relation:memory-stable-1"],
                Importance = 0.8,
                Confidence = 0.9,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now
            };

            await contextStore.SaveAsync(item);
            await memoryStore.SaveAsync(workingMemory);
            await memoryStore.SaveAsync(stableMemory);
            await relationStore.SaveAsync(new ContextRelation
            {
                Id = "relation-1",
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                SourceId = item.Id,
                TargetId = workingMemory.Id,
                RelationType = ContextRelationTypes.RelatedTo,
                Weight = 0.75,
                Confidence = 0.8,
                CreatedAt = now
            });
            await constraintStore.SaveAsync(new ContextConstraint
            {
                Id = "constraint-1",
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                Scope = ContextScope.Collection,
                Level = ConstraintLevel.Hard,
                Content = "必须保留来源引用。",
                SourceRefs = ["source:constraint-1"],
                Status = ContextMemoryStatus.Verified,
                Confidence = 1.0,
                CreatedAt = now,
                UpdatedAt = now
            });
            await globalStore.SaveAsync(new ContextGlobalItem
            {
                Id = "global-1",
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                Scope = ContextScope.Workspace,
                Type = "preference",
                Content = "全局偏好：输出保持简洁。",
                ContentFormat = ContextContentFormat.PlainText,
                Tags = ["global"],
                SourceRefs = ["source:global-1"],
                Importance = 1.0,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now
            });
            await index.UpsertAsync(new ContextIndexEntry
            {
                Id = "index-1",
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                Key = "端到端",
                Kind = "tag",
                ContextRefs = [item.Id],
                Weight = 1.0,
                CreatedAt = now
            });
            await eventSink.EmitAsync(new ContextOperationEvent
            {
                EventId = "event-1",
                OperationId = "operation-1",
                OperationName = "integration.filesystem",
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                Level = ContextEventLevel.Information,
                Message = "文件系统端到端测试。",
                Duration = TimeSpan.FromMilliseconds(12),
                CreatedAt = now
            });

            var reloadedContext = await new FileContextStore(storage).GetAsync(item.WorkspaceId, item.CollectionId, item.Id);
            var reloadedWorking = await new FileMemoryStore(storage).GetAsync(item.WorkspaceId, item.CollectionId, workingMemory.Id);
            var reloadedStable = await new FileMemoryStore(storage).GetAsync(item.WorkspaceId, item.CollectionId, stableMemory.Id);
            var reloadedRelation = await new FileRelationStore(storage).QueryForItemAsync(item.WorkspaceId, item.CollectionId, item.Id);
            var reloadedConstraint = await new FileConstraintStore(storage).QueryAsync(new ContextConstraintQuery
            {
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                Level = ConstraintLevel.Hard,
                Take = 10
            });
            var reloadedGlobal = await new FileGlobalContextStore(storage).QueryAsync(new ContextGlobalQuery
            {
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                Take = 10
            });
            var reloadedIndex = await new FileContextIndex(storage).SearchAsync(new IndexQuery
            {
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                Key = "端到端",
                Take = 10
            });
            var logFiles = Directory.GetFiles(Path.Combine(rootPath, "logs", item.WorkspaceId), "*.jsonl", SearchOption.AllDirectories);

            Assert.IsNotNull(reloadedContext);
            Assert.AreEqual("文件系统端到端测试内容。", reloadedContext!.Content);
            Assert.IsNotNull(reloadedWorking);
            Assert.IsNotNull(reloadedStable);
            Assert.AreEqual(ContextMemoryLayer.Working, reloadedWorking!.Layer);
            Assert.AreEqual(ContextMemoryLayer.Stable, reloadedStable!.Layer);
            Assert.AreEqual(1, reloadedRelation.Count);
            Assert.AreEqual(1, reloadedConstraint.Count);
            Assert.AreEqual(1, reloadedGlobal.Count);
            Assert.AreEqual(1, reloadedIndex.Count);
            Assert.IsTrue(logFiles.Length > 0);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ModelGatewayFallback_ShouldUseFallbackWhenPrimaryFails()
    {
        var gateway = new ConfigurableModelGateway(
            new ModelGatewayOptions
            {
                Models =
                [
                    new ModelEndpointOptions
                    {
                        Name = "primary-model",
                        Provider = "mock",
                        Enabled = true,
                        Metadata = new Dictionary<string, string>
                        {
                            ["apiProviderName"] = "mock-api",
                            ["model"] = "primary-model",
                            ["category"] = "fast",
                            ["capabilities"] = "compression"
                        }
                    },
                    new ModelEndpointOptions
                    {
                        Name = "fallback-model",
                        Provider = "mock",
                        Enabled = true,
                        Metadata = new Dictionary<string, string>
                        {
                            ["apiProviderName"] = "mock-api",
                            ["model"] = "fallback-model",
                            ["category"] = "fast",
                            ["capabilities"] = "compression"
                        }
                    }
                ],
                Routes =
                [
                    new ModelRoleRoute
                    {
                        Role = ModelRole.GeneralCompression,
                        PrimaryModelName = "primary-model",
                        FallbackModelName = "fallback-model",
                        RequiredCapabilities = ["compression"],
                        EnableFallback = true,
                        FallbackOnServerError = true,
                        Priority = 10
                    }
                ]
            },
            [
                TestModelAdapter.Failure("primary-model", "primary failed."),
                TestModelAdapter.Success("fallback-model", "fallback content.")
            ]);

        var response = await gateway.CompleteAsync(new ModelRequest
        {
            OperationId = "model-operation-1",
            Role = ModelRole.GeneralCompression,
            Prompt = "请压缩上下文。",
            Metadata = new Dictionary<string, string>
            {
                ["taskKind"] = "Summarize",
                ["thinkingMode"] = "fast"
            }
        });

        Assert.IsTrue(response.Succeeded);
        Assert.AreEqual("fallback content.", response.Content);
        Assert.AreEqual("true", response.Metadata["fallbackUsed"]);
        Assert.AreEqual("primary-model", response.Metadata["primaryModelName"]);
    }

    [TestMethod]
    public async Task PackageBuilderPolicy_ShouldHonorSectionOrderAndBudgets()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var storage = new FileStorageOptions { RootPath = rootPath };
            var contextStore = new FileContextStore(storage);
            var memoryStore = new FileMemoryStore(storage);
            var relationStore = new FileRelationStore(storage);
            var constraintStore = new FileConstraintStore(storage);
            var globalStore = new FileGlobalContextStore(storage);
            var builder = new BasicContextPackageBuilder(
                contextStore,
                constraintStore,
                globalStore,
                memoryStore,
                relationStore);
            var now = DateTimeOffset.UtcNow;

            await contextStore.SaveAsync(new ContextItem
            {
                Id = "raw-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Type = "note",
                Content = "最近原始上下文。",
                Tags = ["recent"],
                SourceRefs = ["source:raw-1"],
                CreatedAt = now,
                UpdatedAt = now
            });
            await memoryStore.SaveAsync(new ContextMemoryItem
            {
                Id = "working-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Layer = ContextMemoryLayer.Working,
                Status = ContextMemoryStatus.Verified,
                Type = "memory",
                Content = "工作记忆。",
                ContentFormat = ContextContentFormat.PlainText,
                Tags = ["working"],
                SourceRefs = ["source:working-1"],
                Importance = 0.5,
                Confidence = 0.7,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now
            });
            await memoryStore.SaveAsync(new ContextMemoryItem
            {
                Id = "stable-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Layer = ContextMemoryLayer.Stable,
                Status = ContextMemoryStatus.Stable,
                Type = "memory",
                Content = "稳定记忆。",
                ContentFormat = ContextContentFormat.PlainText,
                Tags = ["stable"],
                SourceRefs = ["source:stable-1"],
                Importance = 0.9,
                Confidence = 0.9,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now
            });
            await constraintStore.SaveAsync(new ContextConstraint
            {
                Id = "hard-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Scope = ContextScope.Collection,
                Level = ConstraintLevel.Hard,
                Content = "硬约束。",
                SourceRefs = ["source:hard-1"],
                Status = ContextMemoryStatus.Verified,
                Confidence = 1.0,
                CreatedAt = now,
                UpdatedAt = now
            });
            await constraintStore.SaveAsync(new ContextConstraint
            {
                Id = "soft-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Scope = ContextScope.Collection,
                Level = ConstraintLevel.Soft,
                Content = "软约束。",
                SourceRefs = ["source:soft-1"],
                Status = ContextMemoryStatus.Verified,
                Confidence = 0.8,
                CreatedAt = now,
                UpdatedAt = now
            });
            await globalStore.SaveAsync(new ContextGlobalItem
            {
                Id = "global-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Scope = ContextScope.Workspace,
                Type = "preference",
                Content = "全局偏好。",
                ContentFormat = ContextContentFormat.PlainText,
                Tags = ["global"],
                SourceRefs = ["source:global-1"],
                Importance = 1.0,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now
            });

            var result = await builder.BuildDetailedAsync(new ContextPackageRequest
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                QueryText = "上下文",
                TokenBudget = 1000,
                Policy = new ContextPackagePolicy
                {
                    WorkspaceId = "workspace-test",
                    CollectionId = "collection-test",
                    TokenBudget = 1000,
                    IncludeGlobalContext = true,
                    IncludeHardConstraints = true,
                    IncludeSoftConstraints = true,
                    IncludeWorkingMemory = true,
                    IncludeStableMemory = true,
                    IncludeRecentRawContext = true,
                    MaxRecentItems = 10,
                    SectionOrder = ["global_context", "hard_constraints", "working_memory", "recent_context", "stable_memory", "soft_constraints"],
                    SectionTokenBudgets = new Dictionary<string, int>
                    {
                        ["recent_context"] = 120
                    }
                }
            });

            CollectionAssert.AreEqual(
                new[] { "global_context", "hard_constraints", "working_memory", "recent_context", "stable_memory", "soft_constraints" },
                result.Package.Sections.Select(section => section.Name).ToArray());
            Assert.AreEqual(1000, result.TokenBudget);
            Assert.IsTrue(result.EstimatedTokens > 0);
            Assert.IsTrue(result.SelectedItems.Count > 0);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task FileContextPackagePolicyStore_ShouldSaveLoadQueryAndPersistPolicies()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var storage = new FileStorageOptions { RootPath = rootPath };
            var store = new FileContextPackagePolicyStore(storage);
            var policy = new ContextPackagePolicy
            {
                Id = "saved-policy",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Name = "中文上下文策略",
                Description = "用于验证上下文包策略持久化与查询。",
                TokenBudget = 1500,
                MaxRecentItems = 12,
                IncludeGlobalContext = true,
                IncludeHardConstraints = true,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = true,
                IncludeStableMemory = true,
                IncludeRecentRawContext = true,
                SectionOrder = ["hard_constraints", "working_memory", "recent_context"],
                SectionTokenBudgets = new Dictionary<string, int>
                {
                    ["hard_constraints"] = 200,
                    ["working_memory"] = 500,
                    ["recent_context"] = 600
                },
                Metadata = new Dictionary<string, string>
                {
                    ["createdBy"] = "integration-test"
                }
            };

            await store.SaveAsync(policy);

            var reloadedStore = new FileContextPackagePolicyStore(storage);
            var loaded = await reloadedStore.GetAsync("workspace-test", "collection-test", "saved-policy");
            var queried = await reloadedStore.QueryAsync(new ContextPackagePolicyQuery
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                QueryText = "中文上下文",
                Take = 10
            });
            var policyPath = Path.Combine(
                rootPath,
                "workspaces",
                "workspace-test",
                "collections",
                "collection-test",
                "packages",
                "policies.jsonl");

            Assert.IsTrue(File.Exists(policyPath));
            Assert.IsNotNull(loaded);
            Assert.AreEqual("中文上下文策略", loaded!.Name);
            Assert.AreEqual(1500, loaded.TokenBudget);
            Assert.AreEqual(12, loaded.MaxRecentItems);
            Assert.IsFalse(loaded.IncludeSoftConstraints);
            CollectionAssert.AreEqual(
                new[] { "hard_constraints", "working_memory", "recent_context" },
                loaded.SectionOrder.ToArray());
            Assert.AreEqual("integration-test", loaded.Metadata["createdBy"]);
            Assert.AreEqual(1, queried.Count);
            Assert.AreEqual("saved-policy", queried[0].Id);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }
    [TestMethod]
    public async Task ControlRoomReportExport_ShouldWriteMarkdownReport()
    {
        var rootPath = CreateTestRootPath();
        var outputPath = Path.Combine(rootPath, "reports", "control-room-report.md");

        try
        {
            var state = CreateControlRoomState(rootPath);
            var service = new ControlRoomService(state);

            await state.ContextStore.SaveAsync(new ContextItem
            {
                Id = "report-raw-1",
                WorkspaceId = state.WorkspaceId,
                CollectionId = state.CollectionId,
                Type = "note",
                Content = "控制室报告导出测试。",
                Tags = ["report"],
                SourceRefs = ["source:report-raw-1"],
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            await ReportCommand.ExecuteAsync(service, ["export", "--out", outputPath]);

            var markdown = await File.ReadAllTextAsync(outputPath);
            StringAssert.Contains(markdown, "# ContextCore Debug Report");
            StringAssert.Contains(markdown, "## Validation Report");
            StringAssert.Contains(markdown, "Raw items: `1`");
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ControlRoomStatus_ShouldExposeLocalReadiness()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            Directory.CreateDirectory(rootPath);
            var state = CreateControlRoomState(rootPath);
            var service = new ControlRoomService(state);

            var status = await service.GetStatusAsync();

            Assert.AreEqual("Ready", status.ReadinessState);
            Assert.AreEqual("ServiceReadyAlpha", status.ProviderState);
            Assert.IsFalse(status.ProductionReady);
            StringAssert.Contains(status.ReadinessMessage, "Alpha");
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    private static ControlRoomState CreateControlRoomState(string rootPath)
    {
        var contextStore = new FileContextStore(new FileStorageOptions { RootPath = rootPath });
        var memoryStore = new FileMemoryStore(new FileStorageOptions { RootPath = rootPath });
        var relationStore = new FileRelationStore(new FileStorageOptions { RootPath = rootPath });
        var constraintStore = new FileConstraintStore(new FileStorageOptions { RootPath = rootPath });
        var globalStore = new FileGlobalContextStore(new FileStorageOptions { RootPath = rootPath });
        var index = new FileContextIndex(new FileStorageOptions { RootPath = rootPath });
        var jobQueue = new InMemoryJobQueue();
        var embeddingProvider = new MockEmbeddingProvider(new EmbeddingOptions
        {
            ModelName = "integration-embedding",
            Dimensions = 4
        });
        var retriever = new HybridContextRetriever(
            contextStore,
            memoryStore,
            relationStore,
            embeddingProvider,
            new InMemoryVectorStore(),
            new InMemoryRetrievalTraceStore());
        var modelOptions = new ModelGatewayOptions
        {
            Models =
            [
                new ModelEndpointOptions
                {
                    Name = "mock",
                    Provider = "mock",
                    Enabled = true,
                    Metadata = new Dictionary<string, string>
                    {
                        ["apiProviderName"] = "mock-api",
                        ["model"] = "mock",
                        ["category"] = "mock",
                        ["capabilities"] = "compression"
                    }
                }
            ]
        };
        var modelHealthService = new ModelHealthService(modelOptions, [new MockModelAdapter("mock")]);
        var promotionService = new BasicMemoryPromotionService(memoryStore, memoryStore);
        var packageBuilder = new BasicContextPackageBuilder(
            contextStore,
            constraintStore,
            globalStore,
            memoryStore,
            relationStore);

        return new ControlRoomState
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            StorageKind = "filesystem",
            RootPath = rootPath,
            ContextStore = contextStore,
            Index = index,
            MemoryStore = memoryStore,
            WorkingMemory = memoryStore,
            ConstraintStore = constraintStore,
            RelationStore = relationStore,
            GlobalContextStore = globalStore,
            JobQueue = jobQueue,
            JobQueryStore = jobQueue,
            PromotionService = promotionService,
            PackageBuilder = packageBuilder,
            PackagePolicyStore = new FileContextPackagePolicyStore(new FileStorageOptions { RootPath = rootPath }),
            VectorStore = new InMemoryVectorStore(),
            EmbeddingProvider = embeddingProvider,
            RetrievalTraceStore = new InMemoryRetrievalTraceStore(),
            Retriever = retriever,
            ModelGatewayOptions = modelOptions,
            ModelHealthService = modelHealthService,
            ModelUsageLogStore = new InMemoryModelUsageLogStore()
        };
    }

    private static string CreateTestRootPath()
    {
        return Path.Combine(
            Directory.GetCurrentDirectory(),
            "context-core-integration-data",
            Guid.NewGuid().ToString("N"));
    }

    private static void DeleteTestRoot(string rootPath)
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private sealed class TestModelAdapter : IModelAdapter
    {
        private readonly Func<ModelRequest, CancellationToken, Task<ModelResponse>> _handler;

        private TestModelAdapter(
            string name,
            Func<ModelRequest, CancellationToken, Task<ModelResponse>> handler)
        {
            Name = name;
            _handler = handler;
        }

        public string Name { get; }

        public Task<ModelResponse> CompleteAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            return _handler(request, cancellationToken);
        }

        public static TestModelAdapter Success(string name, string content)
        {
            return new TestModelAdapter(name, (request, _) => Task.FromResult(new ModelResponse
            {
                OperationId = request.OperationId,
                Content = content,
                Succeeded = true,
                InputTokens = 3,
                OutputTokens = 2
            }));
        }

        public static TestModelAdapter Failure(string name, string message)
        {
            return new TestModelAdapter(name, (request, _) => Task.FromResult(new ModelResponse
            {
                OperationId = request.OperationId,
                Content = string.Empty,
                Succeeded = false,
                ErrorMessage = message,
                Metadata = new Dictionary<string, string>
                {
                    ["failureReason"] = "server_error"
                }
            }));
        }
    }
}
