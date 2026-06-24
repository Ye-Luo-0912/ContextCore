using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

[TestClass]
public sealed class ContextCoreInputPipelineTests
{
    [TestMethod]
    public void ProductionSource_ShouldNotContainFixtureDomainKeywords()
    {
        var sourceRoot = FindRepositoryRoot().FullName;
        var forbiddenTerms = new[]
        {
            "林风",
            "苍穹大陆",
            "九转金丹",
            "龙魂草",
            "拍卖行"
        };

        var violations = Directory
            .EnumerateFiles(Path.Combine(sourceRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path =>
            {
                var text = File.ReadAllText(path);
                return forbiddenTerms
                    .Where(term => text.Contains(term, StringComparison.Ordinal))
                    .Select(term => $"{Path.GetRelativePath(sourceRoot, path)}::{term}");
            })
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), violations);
    }

    [TestMethod]
    public async Task InMemoryContextStore_Query_ShouldUseGenericFieldMatching()
    {
        var store = new InMemoryContextStore();
        await store.SaveAsync(new ContextItem
        {
            Id = "context-item-42",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "runbook",
            Title = "服务恢复手册",
            Content = "恢复流程需要记录失败步骤，并保留 source reference。",
            Tags = ["recovery", "ops"],
            Refs = ["ref:restore"],
            SourceRefs = ["source:incident-42"],
            UpdatedAt = DateTimeOffset.UtcNow
        });

        Assert.AreEqual("context-item-42", (await QueryOneAsync(store, "服务恢复")).Id);
        Assert.AreEqual("context-item-42", (await QueryOneAsync(store, "runbook")).Id);
        Assert.AreEqual("context-item-42", (await QueryOneAsync(store, "source reference")).Id);
        Assert.AreEqual("context-item-42", (await QueryOneAsync(store, "ops")).Id);
        Assert.AreEqual("context-item-42", (await QueryOneAsync(store, "ref:restore")).Id);
        Assert.AreEqual("context-item-42", (await QueryOneAsync(store, "source:incident-42")).Id);
        Assert.AreEqual("context-item-42", (await QueryOneAsync(store, "context-item-42")).Id);
    }

    [TestMethod]
    public async Task InMemoryContextStore_Query_ShouldMatchGenericChineseBigramsWithoutFixtureKeywordList()
    {
        var store = new InMemoryContextStore();
        await store.SaveAsync(new ContextItem
        {
            Id = "generic-cjk",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "note",
            Title = "故障处理记录",
            Content = "恢复流程需要记录失败步骤。",
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var results = await store.QueryAsync(new ContextQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "请检查失败步骤",
            Take = 10
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("generic-cjk", results[0].Id);
    }

    [TestMethod]
    public void ContextInputValidator_ShouldFail_WhenContentIsEmpty()
    {
        var validator = new ContextInputValidator();

        var result = validator.Validate(new ContextInputCommand
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Source = "chat",
            InputKind = "note",
            Content = "   "
        });

        Assert.IsFalse(result.Succeeded);
        CollectionAssert.Contains(result.Issues.Select(issue => issue.Code).ToArray(), "ContentRequired");
    }

    private static async Task<ContextItem> QueryOneAsync(InMemoryContextStore store, string queryText)
    {
        var results = await store.QueryAsync(new ContextQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = queryText,
            Take = 10,
            IncludeContent = true
        });

        Assert.AreEqual(1, results.Count, $"Query should match exactly one item: {queryText}");
        return results[0];
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ContextCore.sln")))
            {
                return directory;
            }

            directory = directory.Parent;
        }

        Assert.Fail("Cannot locate ContextCore.sln from test output directory.");
        throw new InvalidOperationException("Cannot locate repository root.");
    }

    [TestMethod]
    public async Task ContextInputIngestionService_ShouldDedupe_WhenSourceRefAndContentHashMatch()
    {
        var store = new InMemoryContextStore();
        var service = CreateInputIngestionService(store);

        var first = await service.IngestAsync(new ContextInputCommand
        {
            OperationId = "op-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Source = "chat",
            InputKind = "note",
            Content = "同一 sourceRef + contentHash 应去重。",
            SourceRefs = ["source:chat-1"]
        });
        var second = await service.IngestAsync(new ContextInputCommand
        {
            OperationId = "op-2",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Source = "chat",
            InputKind = "note",
            Content = "同一 sourceRef + contentHash 应去重。",
            SourceRefs = ["source:chat-1"]
        });

        Assert.AreEqual(first.Id, second.Id);

        var all = await store.QueryAsync(new ContextQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Take = 10
        });
        Assert.AreEqual(1, all.Count);
    }

    [TestMethod]
    public async Task ContextInputIngestionService_ShouldCreateNewItem_WhenSourceRefDiffers()
    {
        var store = new InMemoryContextStore();
        var service = CreateInputIngestionService(store);

        var first = await service.IngestAsync(new ContextInputCommand
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Source = "chat",
            InputKind = "note",
            Content = "不同 sourceRef + 相同 contentHash 应创建新条目。",
            SourceRefs = ["source:chat-1"]
        });
        var second = await service.IngestAsync(new ContextInputCommand
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Source = "chat",
            InputKind = "note",
            Content = "不同 sourceRef + 相同 contentHash 应创建新条目。",
            SourceRefs = ["source:chat-2"]
        });

        Assert.AreNotEqual(first.Id, second.Id);
    }

    [TestMethod]
    public async Task ContextInputIngestionService_ShouldAssignMonotonicSequence()
    {
        var store = new InMemoryContextStore();
        var service = CreateInputIngestionService(store);

        var first = await service.IngestAsync(new ContextInputCommand
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Source = "chat",
            InputKind = "note",
            Content = "sequence-1",
            SourceRefs = ["source:seq-1"]
        });
        var second = await service.IngestAsync(new ContextInputCommand
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Source = "chat",
            InputKind = "note",
            Content = "sequence-2",
            SourceRefs = ["source:seq-2"]
        });

        var firstSeq = long.Parse(first.Metadata["sequenceId"]);
        var secondSeq = long.Parse(second.Metadata["sequenceId"]);
        Assert.IsTrue(secondSeq > firstSeq);
    }

    [TestMethod]
    public async Task ContextInputIngestionService_ShouldPreserveMetadata()
    {
        var store = new InMemoryContextStore();
        var service = CreateInputIngestionService(store);

        var item = await service.IngestAsync(new ContextInputCommand
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Source = "chat",
            InputKind = "note",
            Content = "metadata",
            Metadata = new Dictionary<string, string>
            {
                ["custom"] = "preserved"
            }
        });

        Assert.AreEqual("preserved", item.Metadata["custom"]);
        Assert.AreEqual("chat", item.Metadata["source"]);
        Assert.AreEqual("note", item.Metadata["inputKind"]);
    }

    [TestMethod]
    public async Task ContextRuntimeService_ShouldPropagateOperationId_FromInputCommand()
    {
        var contextStore = new InMemoryContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var relationStore = new InMemoryRelationStore();
        var constraintStore = new InMemoryConstraintStore();
        var globalStore = new InMemoryGlobalContextStore();
        var eventSink = new InMemoryContextEventSink();
        var packageBuilder = new BasicContextPackageBuilder(
            contextStore,
            constraintStore,
            globalStore,
            memoryStore,
            relationStore);
        var runtime = new ContextRuntimeService(
            contextStore,
            memoryStore,
            new BasicMemoryPromotionService(memoryStore, memoryStore),
            packageBuilder,
            CreateInputIngestionService(contextStore),
            new ContextValidationService(),
            eventSink);

        var item = await runtime.IngestAsync(new ContextInputCommand
        {
            OperationId = "input-op-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Source = "chat",
            InputKind = "note",
            Content = "operation id propagation",
            SourceRefs = ["source:op-1"]
        });

        Assert.AreEqual("input-op-1", item.Item.Metadata["operationId"]);
        Assert.IsTrue(eventSink.Events.All(operationEvent => operationEvent.OperationId == "input-op-1"));
    }

    [TestMethod]
    public async Task ContextRuntimeService_ShouldKeepLegacyIngestWorking()
    {
        var contextStore = new InMemoryContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var relationStore = new InMemoryRelationStore();
        var constraintStore = new InMemoryConstraintStore();
        var globalStore = new InMemoryGlobalContextStore();
        var packageBuilder = new BasicContextPackageBuilder(
            contextStore,
            constraintStore,
            globalStore,
            memoryStore,
            relationStore);
        var runtime = new ContextRuntimeService(
            contextStore,
            memoryStore,
            new BasicMemoryPromotionService(memoryStore, memoryStore),
            packageBuilder,
            CreateInputIngestionService(contextStore),
            new ContextValidationService(),
            new InMemoryContextEventSink());

        var item = await runtime.IngestAsync(new ContextItem
        {
            Id = "legacy-item",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "note",
            Content = "legacy ingest compatibility",
            SourceRefs = ["source:legacy-item"],
            Metadata = new Dictionary<string, string>
            {
                ["source"] = "legacy-test"
            }
        });

        Assert.AreEqual("legacy-item", item.Id);
        Assert.AreEqual("legacy-test", item.Metadata["source"]);
        Assert.IsTrue(item.Metadata.ContainsKey("contentHash"));
        Assert.IsTrue(item.Metadata.ContainsKey("sequenceId"));
        Assert.IsTrue(item.Metadata.ContainsKey("operationId"));
    }

    [TestMethod]
    public async Task ContextInputIngestionService_ShouldCreateActiveTask_ForTaskUpdate()
    {
        var contextStore = new InMemoryContextStore();
        var policy = new ShortTermMemoryPolicy();
        var shortTermStore = new InMemoryShortTermMemoryStore(policy);
        var service = CreateInputIngestionService(contextStore, shortTermStore, policy);

        await service.IngestAsync(new ContextInputCommand
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Source = "chat",
            InputKind = "task_update",
            Content = "完成登录页联调",
            SourceRefs = ["source:task-1"]
        });

        var workingItems = await shortTermStore.QueryWorkingItemsAsync(new ShortTermWorkingItemQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Take = 10
        });

        Assert.AreEqual(1, workingItems.Count);
        Assert.AreEqual("ActiveTask", workingItems[0].Kind);
        Assert.AreEqual("active", workingItems[0].Status);
    }

    [TestMethod]
    public async Task ContextInputIngestionService_ShouldCreateRecentDecision_ForDecision()
    {
        var contextStore = new InMemoryContextStore();
        var policy = new ShortTermMemoryPolicy();
        var shortTermStore = new InMemoryShortTermMemoryStore(policy);
        var service = CreateInputIngestionService(contextStore, shortTermStore, policy);

        await service.IngestAsync(new ContextInputCommand
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Source = "chat",
            InputKind = "decision",
            Content = "决定先启用文件系统后端",
            SourceRefs = ["source:decision-1"]
        });

        var workingItems = await shortTermStore.QueryWorkingItemsAsync(new ShortTermWorkingItemQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Take = 10
        });

        Assert.AreEqual(1, workingItems.Count);
        Assert.AreEqual("RecentDecision", workingItems[0].Kind);
        Assert.AreEqual("recorded", workingItems[0].Status);
    }

    [TestMethod]
    public async Task ContextInputIngestionService_ShouldCreateOpenQuestion_ForOpenQuestion()
    {
        var contextStore = new InMemoryContextStore();
        var policy = new ShortTermMemoryPolicy();
        var shortTermStore = new InMemoryShortTermMemoryStore(policy);
        var service = CreateInputIngestionService(contextStore, shortTermStore, policy);

        await service.IngestAsync(new ContextInputCommand
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Source = "chat",
            InputKind = "open_question",
            Content = "是否需要补齐 Postgres provider？",
            SourceRefs = ["source:question-1"]
        });

        var workingItems = await shortTermStore.QueryWorkingItemsAsync(new ShortTermWorkingItemQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Take = 10
        });

        Assert.AreEqual(1, workingItems.Count);
        Assert.AreEqual("OpenQuestion", workingItems[0].Kind);
        Assert.AreEqual("open", workingItems[0].Status);
    }

    [TestMethod]
    public async Task ContextInputIngestionService_ShouldAllowMetadataWorkingKindOverride()
    {
        var contextStore = new InMemoryContextStore();
        var policy = new ShortTermMemoryPolicy();
        var shortTermStore = new InMemoryShortTermMemoryStore(policy);
        var service = CreateInputIngestionService(contextStore, shortTermStore, policy);

        await service.IngestAsync(new ContextInputCommand
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Source = "chat",
            InputKind = "note",
            Content = "数据库连接在高峰期会抖动",
            SourceRefs = ["source:override-1"],
            Metadata = new Dictionary<string, string>
            {
                ["workingKind"] = "KnownIssue",
                ["workingTitle"] = "数据库连接抖动",
                ["workingStatus"] = "tracked",
                ["workingImportance"] = "0.99",
                ["workingTags"] = "ops,db"
            }
        });

        var item = (await shortTermStore.QueryWorkingItemsAsync(new ShortTermWorkingItemQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Take = 10
        })).Single();

        Assert.AreEqual("KnownIssue", item.Kind);
        Assert.AreEqual("数据库连接抖动", item.Title);
        Assert.AreEqual("tracked", item.Status);
        Assert.AreEqual(0.99, item.Importance, 0.0001);
        CollectionAssert.Contains(item.Tags.ToArray(), "ops");
        CollectionAssert.Contains(item.Tags.ToArray(), "db");
    }

    [TestMethod]
    public async Task ContextInputIngestionService_ShouldUpsertWorkingItem_WhenWorkingKeyMatches()
    {
        var contextStore = new InMemoryContextStore();
        var policy = new ShortTermMemoryPolicy();
        var shortTermStore = new InMemoryShortTermMemoryStore(policy);
        var service = CreateInputIngestionService(contextStore, shortTermStore, policy);

        await service.IngestAsync(new ContextInputCommand
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Source = "chat",
            InputKind = "task_update",
            Content = "发布任务开始执行",
            SourceRefs = ["source:deploy-1"],
            Metadata = new Dictionary<string, string>
            {
                ["workingKey"] = "deploy-main",
                ["workingTitle"] = "主线发布任务"
            }
        });

        await service.IngestAsync(new ContextInputCommand
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Source = "chat",
            InputKind = "task_update",
            Content = "发布任务进入验证阶段",
            SourceRefs = ["source:deploy-2"],
            Metadata = new Dictionary<string, string>
            {
                ["workingKey"] = "deploy-main",
                ["workingTitle"] = "主线发布任务"
            }
        });

        var items = await shortTermStore.QueryWorkingItemsAsync(new ShortTermWorkingItemQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Take = 10
        });

        Assert.AreEqual(1, items.Count);
        CollectionAssert.Contains(items[0].SourceRefs.ToArray(), "source:deploy-1");
        CollectionAssert.Contains(items[0].SourceRefs.ToArray(), "source:deploy-2");
        Assert.AreEqual(2, items[0].Refs.Count);
        StringAssert.Contains(items[0].Summary, "验证阶段");
    }

    [TestMethod]
    public async Task ContextInputIngestionService_ShouldNotDuplicateWorkingItem_WhenIngestIsDeduped()
    {
        var contextStore = new InMemoryContextStore();
        var policy = new ShortTermMemoryPolicy();
        var shortTermStore = new InMemoryShortTermMemoryStore(policy);
        var service = CreateInputIngestionService(contextStore, shortTermStore, policy);

        await service.IngestAsync(new ContextInputCommand
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Source = "chat",
            InputKind = "open_question",
            Content = "是否需要增加只读策略页？",
            SourceRefs = ["source:dedupe-1"]
        });

        await service.IngestAsync(new ContextInputCommand
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Source = "chat",
            InputKind = "open_question",
            Content = "是否需要增加只读策略页？",
            SourceRefs = ["source:dedupe-1"]
        });

        var summary = await shortTermStore.GetSummaryAsync(new ShortTermSummaryQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            LatestRawTake = 10
        });

        Assert.AreEqual(2, summary.RawEventCount);
        Assert.AreEqual(1, summary.WorkingItemCount);
        Assert.AreEqual(1, summary.OpenQuestionCount);
    }

    [TestMethod]
    public async Task ShortTermMemorySummary_ShouldReturnCategorizedWorkingItems()
    {
        var contextStore = new InMemoryContextStore();
        var policy = new ShortTermMemoryPolicy();
        var shortTermStore = new InMemoryShortTermMemoryStore(policy);
        var service = CreateInputIngestionService(contextStore, shortTermStore, policy);

        await service.IngestAsync(new ContextInputCommand
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Source = "chat",
            InputKind = "task_update",
            Content = "active task",
            SourceRefs = ["source:summary-task"]
        });
        await service.IngestAsync(new ContextInputCommand
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Source = "chat",
            InputKind = "decision",
            Content = "recent decision",
            SourceRefs = ["source:summary-decision"]
        });
        await service.IngestAsync(new ContextInputCommand
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Source = "chat",
            InputKind = "open_question",
            Content = "open question",
            SourceRefs = ["source:summary-question"]
        });
        await service.IngestAsync(new ContextInputCommand
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Source = "chat",
            InputKind = "known_issue",
            Content = "known issue",
            SourceRefs = ["source:summary-issue"]
        });
        await service.IngestAsync(new ContextInputCommand
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Source = "chat",
            InputKind = "warning",
            Content = "recent warning",
            SourceRefs = ["source:summary-warning"]
        });

        var summary = await shortTermStore.GetSummaryAsync(new ShortTermSummaryQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            LatestRawTake = 10
        });

        Assert.AreEqual(5, summary.RawEventCount);
        Assert.AreEqual(5, summary.WorkingItemCount);
        Assert.AreEqual(1, summary.ActiveTaskCount);
        Assert.AreEqual(1, summary.RecentDecisionCount);
        Assert.AreEqual(1, summary.OpenQuestionCount);
        Assert.AreEqual(1, summary.KnownIssueCount);
        Assert.AreEqual(1, summary.RecentWarningCount);
        Assert.AreEqual("ActiveTask", summary.ActiveTasks.Single().Kind);
        Assert.AreEqual("RecentDecision", summary.RecentDecisions.Single().Kind);
        Assert.AreEqual("OpenQuestion", summary.OpenQuestions.Single().Kind);
        Assert.AreEqual("KnownIssue", summary.KnownIssues.Single().Kind);
        Assert.AreEqual("RecentWarning", summary.RecentWarnings.Single().Kind);
    }

    [TestMethod]
    public void RetrievalResultAssembler_ShouldPreserveSelectedExcludedDiagnosticsAndMetadata()
    {
        var selectedCandidate = new ContextRetrievalCandidate
        {
            CandidateId = "ContextItem:item-1",
            SourceId = "item-1",
            Kind = ContextRetrievalCandidateKind.ContextItem,
            Type = "note",
            Content = "selected",
            ContentFormat = ContextContentFormat.PlainText,
            Tags = ["alpha"],
            SourceRefs = ["source:item-1"],
            Score = 4.2,
            EstimatedTokens = 12,
            Reasons = ["关键词召回"],
            Metadata = new Dictionary<string, string>
            {
                ["scoreBreakdown"] = "keyword=4.2;total=4.2",
                ["channelSources"] = "keyword,relation",
                ["relationPaths"] = "seed -[related_to]-> item-1"
            }
        };
        var selectedDecision = new ContextRetrievalDecision
        {
            CandidateId = selectedCandidate.CandidateId,
            SourceId = selectedCandidate.SourceId,
            Kind = selectedCandidate.Kind,
            Type = selectedCandidate.Type,
            Reason = "选中",
            Score = selectedCandidate.Score,
            EstimatedTokens = selectedCandidate.EstimatedTokens,
            Metadata = new Dictionary<string, string>(selectedCandidate.Metadata)
        };
        var droppedDecision = new ContextRetrievalDecision
        {
            CandidateId = "ContextItem:item-2",
            SourceId = "item-2",
            Kind = ContextRetrievalCandidateKind.ContextItem,
            Type = "note",
            Reason = "超过 token 预算",
            Score = 1.5,
            EstimatedTokens = 40,
            Metadata = new Dictionary<string, string>
            {
                ["diagnostic"] = "budget-pressure"
            }
        };
        var metadata = new Dictionary<string, string>
        {
            ["queryEmbeddingModelCalls"] = "3",
            ["diagnostic"] = "kept"
        };
        var packing = new RetrievalPackingResult(
            [selectedCandidate],
            [selectedDecision],
            [droppedDecision]);
        var trace = new RetrievalTraceAssembler().Assemble(
            "retrieval-1",
            new ContextRetrievalRequest
            {
                OperationId = "retrieval-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                QueryText = "assembler"
            },
            [
                new ContextRetrievalStageTrace
                {
                    Name = "关键词召回",
                    CandidateCount = 1,
                    Metadata = new Dictionary<string, string>
                    {
                        ["diagnostic"] = "stage-kept"
                    }
                }
            ],
            [selectedCandidate],
            packing,
            Array.Empty<ContextAttentionScore>(),
            new AttentionShadowReport(),
            new AttentionProfileExperimentReport(),
            metadata);
        var result = new RetrievalResultAssembler().Assemble(
            "retrieval-1",
            new ContextRetrievalRequest
            {
                OperationId = "retrieval-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                QueryText = "assembler"
            },
            packing,
            trace,
            metadata);

        Assert.AreEqual("item-1", result.SelectedItems.Single().SourceId);
        Assert.AreEqual("超过 token 预算", result.DroppedItems.Single().Reason);
        Assert.AreEqual("budget-pressure", result.DroppedItems.Single().Metadata["diagnostic"]);
        Assert.AreEqual("keyword=4.2;total=4.2", result.SelectedItems.Single().Metadata["scoreBreakdown"]);
        Assert.AreEqual("keyword,relation", result.SelectedItems.Single().Metadata["channelSources"]);
        Assert.AreEqual("seed -[related_to]-> item-1", result.SelectedItems.Single().Metadata["relationPaths"]);
        Assert.AreEqual("kept", result.Metadata["diagnostic"]);
        Assert.AreEqual("stage-kept", result.Trace.Stages.Single().Metadata["diagnostic"]);
    }

    private static ContextInputIngestionService CreateInputIngestionService(
        IContextStore store,
        IShortTermMemoryStore? shortTermMemoryStore = null,
        ShortTermMemoryPolicy? shortTermPolicy = null)
    {
        return new ContextInputIngestionService(
            store,
            new ContextInputNormalizer(),
            new ContextInputValidator(),
            new ContextInputHasher(),
            new ContextInputSequencer(),
            shortTermMemoryStore,
            shortTermMemoryStore is null ? null : new RuleBasedShortTermWorkingItemExtractor(),
            shortTermPolicy);
    }
}
