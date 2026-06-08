using System.Net;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Commands;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Screens;
using ContextCore.ControlRoom.Services;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.ModelGateway;
using ContextCore.ModelGateway.Adapters;
using ContextCore.ModelGateway.Infrastructure;
using ContextCore.Service;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;

namespace ContextCore.Tests;

/// <summary>覆盖 MVP 阶段核心存储、打包、控制室和模型网关行为的回归测试。</summary>
[TestClass]
public sealed class ContextCoreMvpTests
{
    [TestMethod]
    public async Task FileContextStore_SaveAndGet_ShouldReturnSameItem()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var store = new FileContextStore(new FileStorageOptions { RootPath = rootPath });
            var item = CreateItem(
                id: "item-1",
                type: "note",
                content: "# Stored content",
                format: ContextContentFormat.Markdown,
                tags: new[] { "alpha" });

            await store.SaveAsync(item);

            var actual = await store.GetAsync(item.WorkspaceId, item.CollectionId, item.Id);

            Assert.IsNotNull(actual);
            Assert.AreEqual(item.Id, actual!.Id);
            Assert.AreEqual(item.WorkspaceId, actual.WorkspaceId);
            Assert.AreEqual(item.CollectionId, actual.CollectionId);
            Assert.AreEqual(item.Content, actual.Content);
            Assert.AreEqual(item.ContentFormat, actual.ContentFormat);
            CollectionAssert.AreEqual(item.Tags.ToArray(), actual.Tags.ToArray());
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task FileContextStore_QueryByTag_ShouldReturnMatchedItems()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var store = new FileContextStore(new FileStorageOptions { RootPath = rootPath });

            await store.SaveAsync(CreateItem(
                id: "matched",
                type: "note",
                content: "Matched content",
                tags: new[] { "shared", "target" }));

            await store.SaveAsync(CreateItem(
                id: "other",
                type: "note",
                content: "Other content",
                tags: new[] { "shared" }));

            var results = await store.QueryAsync(new ContextQuery
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Tags = new[] { "target" },
                Take = 10,
                IncludeContent = true
            });

            Assert.AreEqual(1, results.Count);
            var result = results[0];
            Assert.AreEqual("matched", result.Id);
            Assert.AreEqual("Matched content", result.Content);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task MockContextCompressor_ShouldGenerateSummaryItem()
    {
        var compressor = new MockContextCompressor();
        var input = CreateItem(
            id: "source-1",
            type: "note",
            content: "A source item that should be represented in the summary.",
            tags: new[] { "alpha" });

        var response = await compressor.CompressAsync(new CompressionRequest
        {
            OperationId = "operation-1",
            WorkspaceId = input.WorkspaceId,
            CollectionId = input.CollectionId,
            TaskKind = CompressionTaskKind.Summarize,
            Inputs = new[] { input },
            Options = new CompressionOptions
            {
                GenerateIndexHints = true,
                PreserveSourceRefs = true
            }
        });

        Assert.AreEqual(1, response.GeneratedItems.Count);
        var generated = response.GeneratedItems[0];

        Assert.AreEqual(CompressionStatus.Succeeded, response.Status);
        Assert.AreEqual("summary", generated.Type);
        Assert.IsTrue(generated.Content.Contains("source item", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(response.IndexHints.Count > 0);
        Assert.IsNotNull(response.QualityReport);
        Assert.IsTrue(response.QualityReport!.CompletenessScore > 0);
        Assert.IsFalse(response.QualityReport.RequiresReview);
        Assert.AreEqual(response.QualityReport.GeneratedItemId, generated.Metadata["quality.generatedItemId"]);
    }

    [TestMethod]
    public void CompressionPromptBuilder_ShouldBuildStructuredJsonModelRequest()
    {
        var input = CreateItem(
            id: "prompt-input",
            type: "note",
            content: "Prompt source content.",
            tags: new[] { "prompt" });
        var builder = new CompressionPromptBuilder();

        var modelRequest = builder.Build(new CompressionRequest
        {
            OperationId = "operation-prompt",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TaskKind = CompressionTaskKind.Extract,
            SubKind = "ExtractKeyPoints",
            Inputs = new[] { input },
            Options = new CompressionOptions
            {
                Depth = CompressionDepth.Deep,
                GenerateIndexHints = true,
                PreserveSourceRefs = true,
                TargetTokenBudget = 120,
                ModelRole = "StrongReasoning"
            }
        }, "operation-prompt");

        using var promptJson = JsonDocument.Parse(modelRequest.Prompt);
        var root = promptJson.RootElement;

        Assert.AreEqual(ModelRole.StrongReasoning, modelRequest.Role);
        Assert.AreEqual("json", modelRequest.ResponseFormat);
        Assert.IsTrue(modelRequest.SystemPrompt!.Contains("合法 JSON 对象", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("ExtractKeyPoints", root.GetProperty("task").GetString());
        Assert.AreEqual("Deep", root.GetProperty("depth").GetString());
        Assert.AreEqual("deep", root.GetProperty("thinkingMode").GetString());
        Assert.AreEqual(120, root.GetProperty("targetTokenBudget").GetInt32());
        Assert.IsTrue(root.GetProperty("generateIndexHints").GetBoolean());
        Assert.AreEqual("prompt-input", root.GetProperty("inputs")[0].GetProperty("id").GetString());
        Assert.AreEqual("Prompt source content.", root.GetProperty("inputs")[0].GetProperty("content").GetString());
        Assert.AreEqual("ExtractKeyPoints", modelRequest.Metadata["compressionTask"]);
        Assert.AreEqual("deep", modelRequest.Metadata["thinkingMode"]);
    }

    [TestMethod]
    public async Task LlmContextCompressor_ShouldGenerateSummaryAndIndexHints()
    {
        var gateway = RecordingModelGateway.Success("""
        {
          "status": "succeeded",
          "title": "Compressed Notes",
          "summary": "# Summary\nImportant source content was retained.",
          "tags": ["compressed", "important"],
          "indexHints": [
            { "key": "important source", "kind": "keyword", "weight": 0.9 }
          ],
          "confidence": 0.87
        }
        """, inputTokens: 41, outputTokens: 13);
        var compressor = new LlmContextCompressor(gateway);
        var input = CreateItem(
            id: "llm-source",
            type: "note",
            content: "Important source content for LLM compression.",
            tags: new[] { "source" });

        var response = await compressor.CompressAsync(new CompressionRequest
        {
            OperationId = "operation-llm",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TaskKind = CompressionTaskKind.Summarize,
            Inputs = new[] { input },
            Options = new CompressionOptions
            {
                Depth = CompressionDepth.Normal,
                GenerateIndexHints = true,
                PreserveSourceRefs = true,
                TargetTokenBudget = 160
            }
        });

        var generated = response.GeneratedItems.Single();

        Assert.AreEqual(CompressionStatus.Succeeded, response.Status);
        Assert.AreEqual("operation-llm", response.OperationId);
        Assert.AreEqual("operation-llm-summary", generated.Id);
        Assert.AreEqual("summary", generated.Type);
        Assert.AreEqual("Compressed Notes", generated.Title);
        Assert.IsTrue(generated.Content.Contains("Important source content", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("true", generated.Metadata["isDerived"]);
        Assert.AreEqual("llm-source", generated.Metadata["derivedFrom"]);
        Assert.AreEqual("0.87", generated.Metadata["confidence"]);
        CollectionAssert.Contains(generated.SourceRefs.ToArray(), "llm-source");
        CollectionAssert.Contains(generated.SourceRefs.ToArray(), "source:llm-source");
        CollectionAssert.Contains(generated.Tags.ToArray(), "source");
        CollectionAssert.Contains(generated.Tags.ToArray(), "compressed");
        Assert.IsTrue(response.IndexHints.Count >= 3);
        Assert.IsTrue(response.IndexHints.Any(entry => entry.Key == "important source" && entry.Kind == "keyword"));
        Assert.AreEqual(41, response.Usage.InputTokens);
        Assert.AreEqual(13, response.Usage.OutputTokens);
        Assert.AreEqual(1, response.Usage.ModelCalls);
        Assert.IsNotNull(response.QualityReport);
        Assert.IsTrue(response.QualityReport!.CompletenessScore >= 0.9);
        Assert.IsTrue(response.QualityReport.ConsistencyScore >= 0.9);
        Assert.IsTrue(response.QualityReport.UsabilityScore >= 0.8);
        Assert.AreEqual(0.317, response.QualityReport.CompressionRatio);
        Assert.IsFalse(response.QualityReport.RequiresReview);
        Assert.AreEqual("operation-llm-summary", response.QualityReport.GeneratedItemId);
        Assert.AreEqual(response.QualityReport.RiskScore.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), generated.Metadata["quality.riskScore"]);
        Assert.AreEqual(ModelRole.GeneralCompression, gateway.LastRequest!.Role);
        Assert.AreEqual("json", gateway.LastRequest.ResponseFormat);
    }

    [TestMethod]
    public async Task LlmContextCompressor_ShouldSupportExtractKeyPoints()
    {
        var gateway = RecordingModelGateway.Success("""
        {
          "status": "requires_review",
          "keyPoints": [
            "Keep the source relationship.",
            "Review uncertainty before promotion."
          ],
          "tags": ["memory"],
          "requiresReview": true
        }
        """, inputTokens: 20, outputTokens: 9);
        var compressor = new LlmContextCompressor(gateway);

        var response = await compressor.CompressAsync(new CompressionRequest
        {
            OperationId = "operation-keypoints",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TaskKind = CompressionTaskKind.Extract,
            SubKind = "ExtractKeyPoints",
            Inputs = new[]
            {
                CreateItem(
                    id: "extract-source",
                    type: "note",
                    content: "Extract durable key points.",
                    tags: new[] { "extract" })
            },
            Options = new CompressionOptions
            {
                Depth = CompressionDepth.Audit,
                PreserveSourceRefs = false
            }
        });

        var generated = response.GeneratedItems.Single();

        Assert.AreEqual(CompressionStatus.RequiresReview, response.Status);
        Assert.IsNotNull(response.QualityReport);
        Assert.IsTrue(response.QualityReport!.RequiresReview);
        Assert.IsTrue(response.QualityReport.RiskScore > 0);
        Assert.AreEqual("operation-keypoints-key_points", generated.Id);
        Assert.AreEqual("key_points", generated.Type);
        Assert.IsTrue(generated.Content.Contains("- Keep the source relationship.", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("true", generated.Metadata["requiresReview"]);
        Assert.AreEqual("2", generated.Metadata["keyPointCount"]);
        CollectionAssert.Contains(generated.SourceRefs.ToArray(), "extract-source");
        Assert.IsFalse(generated.SourceRefs.Contains("source:extract-source"));
        CollectionAssert.Contains(generated.Tags.ToArray(), "key_points");
    }

    [TestMethod]
    public async Task LlmContextCompressor_ShouldFail_WhenModelReturnsInvalidJson()
    {
        var gateway = RecordingModelGateway.Success("not-json", inputTokens: 10, outputTokens: 3);
        var compressor = new LlmContextCompressor(gateway);

        var response = await compressor.CompressAsync(new CompressionRequest
        {
            OperationId = "operation-invalid-json",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Inputs = new[]
            {
                CreateItem("invalid-json-source", "note", "Content")
            }
        });

        Assert.AreEqual(CompressionStatus.Failed, response.Status);
        Assert.AreEqual(0, response.GeneratedItems.Count);
        Assert.AreEqual(0, response.IndexHints.Count);
        Assert.IsTrue(response.Errors.Any(error => error.Code == "InvalidModelJson"));
        Assert.IsTrue(response.Errors.Any(error => error.Code == "CompressionModelFailure"));
        Assert.AreEqual(1, response.Usage.ModelCalls);
        Assert.IsNotNull(response.QualityReport);
        Assert.IsTrue(response.QualityReport!.RequiresReview);
        Assert.AreEqual(CompressionStatus.Failed, response.QualityReport.Status);
        Assert.IsTrue(response.QualityReport.RiskScore >= 0.65);
    }

    [TestMethod]
    public void CompressionQualityEvaluator_ShouldExposeActionableSignals()
    {
        var evaluator = new CompressionQualityEvaluator();
        var inputA = CreateItem(
            id: "quality-a",
            type: "decision",
            content: "Payment workflow requires audit logging and rollback handling.",
            tags: new[] { "payment", "audit" });
        var inputB = CreateItem(
            id: "quality-b",
            type: "constraint",
            content: "The implementation must preserve source references.",
            tags: new[] { "source-ref" });
        var generated = CreateItem(
            id: "quality-generated",
            type: "summary",
            content: "Payment workflow summary is much longer than requested because it repeats several operational details.",
            tags: Array.Empty<string>());

        var report = evaluator.Evaluate(
            new CompressionRequest
            {
                OperationId = "quality-eval",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Inputs = new[] { inputA, inputB },
                Options = new CompressionOptions
                {
                    GenerateIndexHints = true,
                    PreserveSourceRefs = true,
                    TargetTokenBudget = 3
                }
            },
            new CompressionResponse
            {
                OperationId = "quality-eval",
                Status = CompressionStatus.RequiresReview,
                GeneratedItems = new[]
                {
                    generated
                },
                Warnings = new[]
                {
                    new ContextWarning
                    {
                        Code = "MissingTrace",
                        Message = "One input is not traceable from generated output."
                    }
                },
                Usage = new ContextOperationUsage
                {
                    InputTokens = 12,
                    OutputTokens = 18,
                    ModelCalls = 1
                }
            });

        Assert.IsTrue(report.RequiresReview);
        Assert.IsTrue(report.RiskScore > 0);
        Assert.IsTrue(report.CompletenessScore < 1);
        CollectionAssert.Contains(report.Signals.ToArray(), "missing-source-refs");
        CollectionAssert.Contains(report.Signals.ToArray(), "over-token-budget");
        CollectionAssert.Contains(report.Signals.ToArray(), "output-longer-than-input");
        CollectionAssert.Contains(report.Signals.ToArray(), "requires-review");
        Assert.IsTrue(report.Signals.Any(signal => signal.StartsWith("source-coverage:", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task BasicContextPackageBuilder_ShouldRespectTokenBudget()
    {
        var store = new InMemoryContextStore();
        var builder = new BasicContextPackageBuilder(store);

        await store.SaveAsync(CreateItem(
            id: "large",
            type: "note",
            content: new string('a', 200),
            tags: new[] { "budget" },
            importance: 1.0));

        await store.SaveAsync(CreateItem(
            id: "small",
            type: "note",
            content: "small",
            tags: new[] { "budget" },
            importance: 0.5));

        var package = await builder.BuildAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            RequiredTags = new[] { "budget" },
            TokenBudget = 10
        });

        Assert.IsTrue(package.EstimatedTokens <= 10);
        Assert.IsTrue(package.Sections.Count > 0);
        Assert.IsTrue(package.Sections.Sum(section => section.EstimatedTokens) <= 10);
    }

    [TestMethod]
    public void ContextTokenizerResolver_ShouldRouteByModelAndUseChineseAwareEstimate()
    {
        var resolver = new DefaultContextTokenizerResolver();
        var content = "上下文管理系统需要稳定处理中文记忆。";
        var legacyTokens = BasicContextPackageBuilder.EstimateTokens(content);

        var deepSeekEstimate = resolver.Estimate(content, "deepseek-v4-flash");
        var gptEstimate = resolver.Estimate(content, "gpt-5.5");

        Assert.AreEqual("deepseek-compatible-v1", deepSeekEstimate.Source);
        Assert.AreEqual("deepseek-v4-flash", deepSeekEstimate.ModelName);
        Assert.IsFalse(deepSeekEstimate.IsFallback);
        Assert.IsTrue(deepSeekEstimate.TokenCount > legacyTokens);
        Assert.AreEqual("openai-cl100k-compatible-v1", gptEstimate.Source);
    }

    [TestMethod]
    public async Task BasicContextPackageBuilder_ShouldRecordTokenEstimateMetadataAndControlRoomRenderIt()
    {
        var store = new InMemoryContextStore();
        var resolver = new DefaultContextTokenizerResolver();
        var builder = new BasicContextPackageBuilder(
            store,
            null,
            null,
            null,
            null,
            null,
            resolver);

        await store.SaveAsync(CreateItem(
            id: "tokenizer-source",
            type: "note",
            content: "上下文包需要记录 Token 估算来源，便于预算问题排查。",
            tags: new[] { "tokenizer" },
            importance: 1.0));

        var package = await builder.BuildAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            RequiredTags = new[] { "tokenizer" },
            TokenBudget = 200,
            Metadata = new Dictionary<string, string>
            {
                ["modelName"] = "deepseek-v4-flash"
            }
        });

        Assert.AreEqual("deepseek-compatible-v1", package.Metadata[ContextTokenizationMetadataKeys.Source]);
        Assert.AreEqual("deepseek-v4-flash", package.Metadata[ContextTokenizationMetadataKeys.Model]);
        Assert.AreEqual("false", package.Metadata[ContextTokenizationMetadataKeys.IsFallback]);

        var dashboard = new DashboardSnapshot
        {
            CurrentTime = DateTimeOffset.UtcNow,
            StorageKind = "memory",
            RootPath = "memory",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            WorkspaceDataFound = true,
            Health =
            [
                new SystemHealthItem { Name = "storage", Status = "ok" }
            ],
            Memory = new MemoryLayerSummary(),
            Jobs = new JobsSummary(),
            LatestPackage = PackageSummary.FromPackage(package),
            Alerts = []
        };
        var rendered = DashboardRenderer.RenderToString(dashboard, autoRefresh: false, refreshSeconds: 2, width: 120);

        StringAssert.Contains(rendered, "估算源");
        StringAssert.Contains(rendered, "deepseek-compatible-v1");
        StringAssert.Contains(rendered, "deepseek-v4-flash");
    }
    [TestMethod]
    public async Task BasicContextPackageBuilder_BuildDetailed_ShouldReturnDecisionLog()
    {
        var store = new InMemoryContextStore();
        var builder = new BasicContextPackageBuilder(store);

        await store.SaveAsync(CreateItem(
            id: "large",
            type: "note",
            content: new string('a', 200),
            tags: new[] { "decision" },
            importance: 1.0));

        await store.SaveAsync(CreateItem(
            id: "small",
            type: "note",
            content: "small",
            tags: new[] { "decision" },
            importance: 0.5));

        var result = await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            RequiredTags = new[] { "decision" },
            TokenBudget = 10
        });

        Assert.IsNotNull(result.Package);
        Assert.AreEqual(10, result.TokenBudget);
        Assert.IsTrue(result.Package.EstimatedTokens <= 10);

        var selected = result.SelectedItems.Single(item => item.ItemId == "large");
        Assert.AreEqual("raw", selected.Kind);
        Assert.AreEqual("large", selected.SectionName);
        Assert.IsFalse(string.IsNullOrWhiteSpace(selected.Reason));
        Assert.IsTrue(selected.Score > 0);
        Assert.IsTrue(selected.EstimatedTokens > 0);

        var dropped = result.DroppedItems.Single(item => item.ItemId == "small");
        Assert.AreEqual("raw", dropped.Kind);
        Assert.AreEqual("token budget exhausted", dropped.Reason);
        Assert.IsTrue(dropped.Score > 0);
        Assert.IsTrue(dropped.EstimatedTokens > 0);
    }

    [TestMethod]
    public async Task FileContextPackageBuildTraceStore_ShouldPersistBuildTrace()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var options = new FileStorageOptions { RootPath = rootPath };
            var contextStore = new FileContextStore(options);
            var traceStore = new FileContextPackageBuildTraceStore(options);
            var builder = new BasicContextPackageBuilder(
                contextStore,
                null,
                null,
                null,
                null,
                traceStore);

            await contextStore.SaveAsync(CreateItem(
                id: "large",
                type: "note",
                content: new string('a', 200),
                tags: new[] { "trace" },
                importance: 1.0));

            await contextStore.SaveAsync(CreateItem(
                id: "small",
                type: "note",
                content: "small",
                tags: new[] { "trace" },
                importance: 0.5));

            var result = await builder.BuildDetailedAsync(new ContextPackageRequest
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                RequiredTags = new[] { "trace" },
                TokenBudget = 10
            });

            var traces = await traceStore.QueryRecentAsync("workspace-test", "collection-test", 10);
            var trace = traces.Single();

            Assert.AreEqual(result.BuildId, trace.BuildId);
            Assert.AreEqual(result.Package.PackageId, trace.Package.PackageId);
            Assert.IsTrue(trace.SelectedItems.Any(item => item.ItemId == "large"));
            Assert.IsTrue(trace.DroppedItems.Any(item => item.ItemId == "small"));
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ControlRoomPackagePreview_ShouldExposeDecisionReasons()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var state = CreateControlRoomState(rootPath);
            var service = new ControlRoomService(state);

            await state.ContextStore.SaveAsync(CreateItem(
                id: "large",
                type: "note",
                content: new string('a', 200),
                tags: new[] { "preview" },
                importance: 1.0));

            await state.ContextStore.SaveAsync(CreateItem(
                id: "small",
                type: "note",
                content: "small",
                tags: new[] { "preview" },
                importance: 0.5));

            var details = await service.BuildPackagePreviewDetailsAsync(
                tokenBudget: 10,
                usePolicy: false);

            var selected = details.SelectedItems.Single(item => item.Id == "large");
            var dropped = details.DroppedItems.Single(item => item.Id == "small");

            Assert.AreEqual("large", selected.SectionName);
            Assert.IsFalse(string.IsNullOrWhiteSpace(selected.Reason));
            Assert.IsTrue(selected.Score > 0);
            Assert.IsTrue(selected.EstimatedTokens > 0);
            Assert.AreEqual("token budget exhausted", dropped.Reason);
            Assert.IsTrue(dropped.Score > 0);
            Assert.IsTrue(dropped.EstimatedTokens > 0);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ControlRoomPackagePreview_ShouldLoadSavedPolicy()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var state = CreateControlRoomState(rootPath);
            var service = new ControlRoomService(state);

            await service.SavePolicyAsync(new ContextPackagePolicy
            {
                Id = "saved-policy",
                Name = "已保存策略",
                Description = "用于测试 ControlRoom 加载策略。",
                TokenBudget = 500,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = false,
                IncludeStableMemory = false,
                IncludeRecentRawContext = true,
                MaxRecentItems = 5,
                SectionOrder = ["recent_context"]
            });
            await state.ContextStore.SaveAsync(CreateItem(
                id: "policy-item",
                type: "note",
                content: "通过已保存 policy 构建上下文包。",
                tags: new[] { "policy" },
                importance: 1.0));

            var policies = await service.ListPoliciesAsync();
            var details = await service.BuildPackagePreviewDetailsAsync(
                tokenBudget: 0,
                usePolicy: true,
                policyId: "saved-policy");

            Assert.AreEqual(1, policies.Count);
            Assert.AreEqual("saved-policy", policies[0].Id);
            Assert.AreEqual("saved-policy", details.Package.Metadata["policyId"]);
            Console.WriteLine("DEBUG_SECTIONS: " + string.Join(", ", details.Package.Sections.Select(section => section.Name)));
            CollectionAssert.AreEqual(
                new[] { "recent_context" },
                details.Package.Sections.Select(section => section.Name).ToArray());
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }
    [TestMethod]
    public async Task ControlRoomPolicyCommand_ShouldEditSavedPolicy()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var state = CreateControlRoomState(rootPath);
            var service = new ControlRoomService(state);
            await service.SavePolicyAsync(new ContextPackagePolicy
            {
                Id = "saved-policy",
                Name = "编辑前策略",
                Description = "编辑前说明。",
                TokenBudget = 500,
                IncludeGlobalContext = true,
                IncludeHardConstraints = true,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = true,
                IncludeStableMemory = true,
                IncludeRecentRawContext = true,
                MaxRecentItems = 5,
                SectionOrder = ["recent_context"]
            });

            await PolicyCommand.ExecuteAsync(service,
            [
                "edit",
                "saved-policy",
                "--name",
                "编辑后策略",
                "--description",
                "编辑后的说明。",
                "--token-budget",
                "900",
                "--max-recent-items",
                "3",
                "--include-global",
                "否",
                "--include-soft",
                "是",
                "--include-working",
                "false",
                "--section-order",
                "recent_context,working_memory",
                "--section-budget",
                "recent_context=300,working_memory=250"
            ]);

            var updated = await service.GetPolicyAsync("saved-policy");

            Assert.IsNotNull(updated);
            Assert.AreEqual("编辑后策略", updated!.Name);
            Assert.AreEqual("编辑后的说明。", updated.Description);
            Assert.AreEqual(900, updated.TokenBudget);
            Assert.AreEqual(3, updated.MaxRecentItems);
            Assert.IsFalse(updated.IncludeGlobalContext);
            Assert.IsTrue(updated.IncludeSoftConstraints);
            Assert.IsFalse(updated.IncludeWorkingMemory);
            CollectionAssert.AreEqual(
                new[] { "recent_context", "working_memory" },
                updated.SectionOrder.ToArray());
            Assert.AreEqual(300, updated.SectionTokenBudgets["recent_context"]);
            Assert.AreEqual(250, updated.SectionTokenBudgets["working_memory"]);
            Assert.AreEqual("ControlRoom", updated.Metadata["updatedBy"]);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }
    [TestMethod]
    public async Task InMemoryJobQueue_ShouldEnqueueAndDequeue()
    {
        var queue = new InMemoryJobQueue();
        var job = new ContextJob
        {
            JobId = "job-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Kind = ContextJobKind.Compression,
            PayloadJson = "{}",
            State = ContextJobState.Queued,
            Priority = 10,
            MaxRetryCount = 3,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await queue.EnqueueAsync(job);

        var dequeued = await queue.DequeueAsync();

        Assert.IsNotNull(dequeued);
        Assert.AreEqual("job-1", dequeued!.JobId);
        Assert.AreEqual(ContextJobState.Running, dequeued.State);
        Assert.IsNotNull(dequeued.StartedAt);
    }

    [TestMethod]
    public async Task RelationStore_SaveAndQuery_ShouldWork()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var store = new FileRelationStore(new FileStorageOptions { RootPath = rootPath });
            var relation = new ContextRelation
            {
                Id = "relation-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                SourceId = "source-1",
                TargetId = "target-1",
                RelationType = "supports",
                Weight = 0.8,
                Confidence = 0.9,
                SourceRefs = new[] { "source:relation" },
                CreatedAt = DateTimeOffset.UtcNow
            };

            await store.SaveAsync(relation);

            var bySource = await store.QueryBySourceAsync("workspace-test", "collection-test", "source-1");
            var byTarget = await store.QueryByTargetAsync("workspace-test", "collection-test", "target-1");
            var byType = await store.QueryByTypeAsync("workspace-test", "collection-test", "supports");

            Assert.AreEqual(1, bySource.Count);
            Assert.AreEqual(1, byTarget.Count);
            Assert.AreEqual(1, byType.Count);
            Assert.AreEqual("relation-1", bySource[0].Id);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ConstraintStore_QueryHardConstraints_ShouldWork()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var store = new FileConstraintStore(new FileStorageOptions { RootPath = rootPath });

            await store.SaveAsync(CreateConstraint("hard-1", ConstraintLevel.Hard, "Must keep system boundaries."));
            await store.SaveAsync(CreateConstraint("soft-1", ConstraintLevel.Soft, "Prefer short answers."));

            var results = await store.QueryAsync(new ContextConstraintQuery
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Level = ConstraintLevel.Hard,
                Take = 10
            });

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual("hard-1", results[0].Id);
            Assert.AreEqual(ConstraintLevel.Hard, results[0].Level);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task FileConstraintStore_Query_ShouldSupportAllLevelsAndAppliesToRefs()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var store = new FileConstraintStore(new FileStorageOptions { RootPath = rootPath });

            foreach (var level in Enum.GetValues<ConstraintLevel>())
            {
                await store.SaveAsync(CreateConstraint(
                    $"constraint-{level.ToString().ToLowerInvariant()}",
                    level,
                    $"{level} constraint.",
                    appliesToRefs: new[] { $"target:{level}" },
                    confidence: 0.5 + ((int)level * 0.01)));
            }

            var all = await store.QueryAsync(new ContextConstraintQuery
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Take = 10
            });
            var levels = all.Select(item => item.Level).ToArray();

            Assert.AreEqual(Enum.GetValues<ConstraintLevel>().Length, all.Count);
            CollectionAssert.Contains(levels, ConstraintLevel.Hard);
            CollectionAssert.Contains(levels, ConstraintLevel.Soft);
            CollectionAssert.Contains(levels, ConstraintLevel.Runtime);
            CollectionAssert.Contains(levels, ConstraintLevel.System);
            CollectionAssert.Contains(levels, ConstraintLevel.User);
            CollectionAssert.Contains(levels, ConstraintLevel.Domain);

            var domain = await store.QueryAsync(new ContextConstraintQuery
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Level = ConstraintLevel.Domain,
                AppliesToRefs = new[] { "target:Domain" },
                Take = 10
            });

            Assert.AreEqual(1, domain.Count);
            Assert.AreEqual("constraint-domain", domain[0].Id);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task InMemoryConstraintStore_Query_ShouldFilterAppliesToRefsAndOrderByConfidence()
    {
        var store = new InMemoryConstraintStore();

        await store.SaveAsync(CreateConstraint(
            "low-confidence",
            ConstraintLevel.System,
            "Low confidence matching constraint.",
            appliesToRefs: new[] { "item-1" },
            confidence: 0.5));
        await store.SaveAsync(CreateConstraint(
            "high-confidence",
            ConstraintLevel.System,
            "High confidence matching constraint.",
            appliesToRefs: new[] { "item-1" },
            confidence: 0.9));
        await store.SaveAsync(CreateConstraint(
            "source-match",
            ConstraintLevel.User,
            "Source ref matching constraint.",
            sourceRefs: new[] { "source:item-2" },
            confidence: 0.7));

        var appliesToResults = await store.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Level = ConstraintLevel.System,
            AppliesToRefs = new[] { "item-1" },
            Take = 10
        });
        var sourceResults = await store.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            AppliesToRefs = new[] { "source:item-2" },
            Take = 10
        });

        Assert.AreEqual(2, appliesToResults.Count);
        Assert.AreEqual("high-confidence", appliesToResults[0].Id);
        Assert.AreEqual("low-confidence", appliesToResults[1].Id);
        Assert.AreEqual(1, sourceResults.Count);
        Assert.AreEqual("source-match", sourceResults[0].Id);
    }

    [TestMethod]
    public async Task WorkingMemory_GetRecent_ShouldReturnLatestItems()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var store = new FileMemoryStore(new FileStorageOptions { RootPath = rootPath });
            var now = DateTimeOffset.UtcNow;

            await store.AddAsync(CreateMemoryItem("old", ContextMemoryLayer.Working, now.AddMinutes(-2)));
            await store.AddAsync(CreateMemoryItem("middle", ContextMemoryLayer.Working, now.AddMinutes(-1)));
            await store.AddAsync(CreateMemoryItem("latest", ContextMemoryLayer.Working, now));

            var results = await store.GetRecentAsync("workspace-test", "collection-test", 2);

            Assert.AreEqual(2, results.Count);
            Assert.AreEqual("latest", results[0].Id);
            Assert.AreEqual("middle", results[1].Id);
            Assert.IsTrue(File.Exists(Path.Combine(
                rootPath,
                "workspaces",
                "workspace-test",
                "collections",
                "collection-test",
                "working",
                "recent-memory.jsonl")));
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task WorkingMemory_ActiveContextAndCurrentTask_ShouldPersistUnderWorkingDirectory()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var store = new FileMemoryStore(new FileStorageOptions { RootPath = rootPath });

            await store.SetActiveContextAsync(new WorkingMemoryActiveContext
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                CurrentTaskId = "task-1",
                Summary = "Active context for the current test task.",
                MemoryRefs = new[] { "memory-1" },
                ContextRefs = new[] { "context-1" },
                Metadata = new Dictionary<string, string>
                {
                    ["source"] = "test"
                }
            });
            await store.SetCurrentTaskAsync(new WorkingMemoryCurrentTask
            {
                TaskId = "task-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Title = "Test task",
                Description = "Verify working memory state files.",
                Status = "running",
                Tags = new[] { "test" }
            });

            var activeContext = await store.GetActiveContextAsync("workspace-test", "collection-test");
            var currentTask = await store.GetCurrentTaskAsync("workspace-test", "collection-test");
            var workingDirectory = Path.Combine(
                rootPath,
                "workspaces",
                "workspace-test",
                "collections",
                "collection-test",
                "working");

            Assert.IsNotNull(activeContext);
            Assert.AreEqual("task-1", activeContext!.CurrentTaskId);
            CollectionAssert.Contains(activeContext.MemoryRefs.ToArray(), "memory-1");
            Assert.IsNotNull(currentTask);
            Assert.AreEqual("Test task", currentTask!.Title);
            Assert.IsTrue(File.Exists(Path.Combine(workingDirectory, "active-context.json")));
            Assert.IsTrue(File.Exists(Path.Combine(workingDirectory, "current-task.json")));
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task WorkingMemory_Clear_ShouldClearRecentAndActiveState()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var store = new FileMemoryStore(new FileStorageOptions { RootPath = rootPath });

            await store.AddAsync(CreateWorkingMemoryItem("memory-1", DateTimeOffset.UtcNow));
            await store.SetActiveContextAsync(new WorkingMemoryActiveContext
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                CurrentTaskId = "task-1",
                MemoryRefs = new[] { "memory-1" }
            });
            await store.SetCurrentTaskAsync(new WorkingMemoryCurrentTask
            {
                TaskId = "task-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Title = "Clear test"
            });

            await store.ClearAsync("workspace-test", "collection-test");

            Assert.AreEqual(0, (await store.GetRecentAsync("workspace-test", "collection-test", 10)).Count);
            Assert.IsNull(await store.GetActiveContextAsync("workspace-test", "collection-test"));
            Assert.IsNull(await store.GetCurrentTaskAsync("workspace-test", "collection-test"));
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ControlRoomWorkingMemory_ShouldExposeRecentAndActiveContext()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var state = CreateControlRoomState(rootPath);
            var service = new ControlRoomService(state);

            await state.WorkingMemory.AddAsync(CreateWorkingMemoryItem("memory-1", DateTimeOffset.UtcNow));
            await service.SetActiveContextAsync(new WorkingMemoryActiveContext
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Summary = "ControlRoom active context.",
                MemoryRefs = new[] { "memory-1" }
            });

            var recent = await service.GetRecentWorkingMemoryAsync(5);
            var activeContext = await service.GetActiveContextAsync();

            Assert.AreEqual(1, recent.Count);
            Assert.AreEqual("memory-1", recent[0].Id);
            Assert.IsNotNull(activeContext);
            CollectionAssert.Contains(activeContext!.MemoryRefs.ToArray(), "memory-1");
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task FileMemoryStore_Query_ShouldFilterAllMemoryStatuses()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var store = new FileMemoryStore(new FileStorageOptions { RootPath = rootPath });
            var now = DateTimeOffset.UtcNow;

            await store.SaveAsync(CreateMemoryItem("candidate", ContextMemoryLayer.Structured, now, ContextMemoryStatus.Candidate));
            await store.SaveAsync(CreateMemoryItem("verified", ContextMemoryLayer.Working, now.AddMinutes(1), ContextMemoryStatus.Verified));
            await store.SaveAsync(CreateMemoryItem("stable", ContextMemoryLayer.Stable, now.AddMinutes(2), ContextMemoryStatus.Stable));
            await store.SaveAsync(CreateMemoryItem("deprecated", ContextMemoryLayer.Structured, now.AddMinutes(3), ContextMemoryStatus.Deprecated));
            await store.SaveAsync(CreateMemoryItem("rejected", ContextMemoryLayer.Structured, now.AddMinutes(4), ContextMemoryStatus.Rejected));

            foreach (var status in new[]
            {
                ContextMemoryStatus.Candidate,
                ContextMemoryStatus.Verified,
                ContextMemoryStatus.Stable,
                ContextMemoryStatus.Deprecated,
                ContextMemoryStatus.Rejected
            })
            {
                var results = await store.QueryAsync(new ContextMemoryQuery
                {
                    WorkspaceId = "workspace-test",
                    CollectionId = "collection-test",
                    Status = status,
                    Take = 10
                });

                Assert.AreEqual(1, results.Count, $"Expected one memory item for status {status}.");
                Assert.AreEqual(status, results[0].Status);
            }

            var stableResults = await store.QueryAsync(new ContextMemoryQuery
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Layer = ContextMemoryLayer.Stable,
                Status = ContextMemoryStatus.Stable,
                Take = 10
            });

            Assert.AreEqual(1, stableResults.Count);
            Assert.AreEqual("stable", stableResults[0].Id);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ControlRoomMemoryStatusBreakdown_ShouldCountStatusesAndLayers()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var state = CreateControlRoomState(rootPath);
            var service = new ControlRoomService(state);
            var now = DateTimeOffset.UtcNow;

            await state.MemoryStore.SaveAsync(CreateMemoryItem("candidate", ContextMemoryLayer.Working, now, ContextMemoryStatus.Candidate));
            await state.MemoryStore.SaveAsync(CreateMemoryItem("verified", ContextMemoryLayer.Structured, now, ContextMemoryStatus.Verified));
            await state.MemoryStore.SaveAsync(CreateMemoryItem("stable", ContextMemoryLayer.Stable, now, ContextMemoryStatus.Stable));
            await state.MemoryStore.SaveAsync(CreateMemoryItem("deprecated", ContextMemoryLayer.Stable, now, ContextMemoryStatus.Deprecated));
            await state.MemoryStore.SaveAsync(CreateMemoryItem("rejected", ContextMemoryLayer.Structured, now, ContextMemoryStatus.Rejected));

            var summary = await service.GetMemoryStatusBreakdownAsync();

            Assert.AreEqual(5, summary.Total);
            Assert.AreEqual(1, summary.WorkingLayer);
            Assert.AreEqual(2, summary.StructuredLayer);
            Assert.AreEqual(2, summary.StableLayer);
            Assert.AreEqual(1, summary.Candidate);
            Assert.AreEqual(1, summary.Verified);
            Assert.AreEqual(1, summary.Stable);
            Assert.AreEqual(1, summary.Deprecated);
            Assert.AreEqual(1, summary.Rejected);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ControlRoomShowMemoryItem_ShouldExposeSourceRefs()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var state = CreateControlRoomState(rootPath);
            var service = new ControlRoomService(state);

            await state.MemoryStore.SaveAsync(CreateMemoryItem(
                "memory-source",
                ContextMemoryLayer.Stable,
                DateTimeOffset.UtcNow,
                ContextMemoryStatus.Stable));

            var detail = await service.ShowAsync("memory-source");

            Assert.IsNotNull(detail);
            Assert.AreEqual("ContextMemoryItem memory-source", detail!.Title);
            CollectionAssert.Contains(detail.SourceRefs.ToArray(), "source:memory-source");
            Assert.AreEqual("Stable", detail.Fields["layer"]);
            Assert.AreEqual("Stable", detail.Fields["status"]);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task InMemoryMemoryStore_Query_ShouldFilterTagsAndSourceRefs()
    {
        var store = new InMemoryMemoryStore();

        await store.SaveAsync(CreateMemoryItem(
            "matched",
            ContextMemoryLayer.Structured,
            DateTimeOffset.UtcNow,
            ContextMemoryStatus.Verified));
        await store.SaveAsync(new ContextMemoryItem
        {
            Id = "other",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = ContextMemoryLayer.Structured,
            Status = ContextMemoryStatus.Verified,
            Type = "memory",
            Content = "Other memory content.",
            Tags = new[] { "other" },
            SourceRefs = new[] { "source:other" },
            Importance = 0.5,
            Confidence = 0.8,
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var results = await store.QueryAsync(new ContextMemoryQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Tags = new[] { "memory" },
            SourceRefs = new[] { "source:matched" },
            Take = 10
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("matched", results[0].Id);
    }

    [TestMethod]
    public async Task MemoryPromotionService_Promote_ShouldUpdateStatus()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var store = new FileMemoryStore(new FileStorageOptions { RootPath = rootPath });
            var service = new BasicMemoryPromotionService(store, store);

            await store.SaveAsync(CreateMemoryItem(
                "memory-1",
                ContextMemoryLayer.Structured,
                DateTimeOffset.UtcNow,
                ContextMemoryStatus.Candidate));

            var record = await service.PromoteAsync(
                "workspace-test",
                "collection-test",
                "memory-1",
                "manual",
                reviewer: "reviewer-test");

            var promoted = await store.GetAsync("workspace-test", "collection-test", "memory-1");

            Assert.AreEqual(ContextMemoryStatus.Candidate, record.FromStatus);
            Assert.AreEqual(ContextMemoryStatus.Stable, record.ToStatus);
            Assert.AreEqual("reviewer-test", record.Reviewer);
            Assert.AreEqual(ContextMemoryLayer.Stable, record.TargetLayer);
            Assert.IsNotNull(promoted);
            Assert.AreEqual(ContextMemoryLayer.Stable, promoted!.Layer);
            Assert.AreEqual(ContextMemoryStatus.Stable, promoted.Status);
            CollectionAssert.Contains(promoted.SourceRefs.ToArray(), "source:memory-1");
            CollectionAssert.Contains(promoted.RelationRefs.ToArray(), "relation:memory-1");
            CollectionAssert.Contains(record.SourceRefs.ToArray(), "source:memory-1");
            CollectionAssert.Contains(record.RelationRefs.ToArray(), "relation:memory-1");

            var promotionLogPath = Path.Combine(
                rootPath,
                "workspaces",
                "workspace-test",
                "collections",
                "collection-test",
                "memory",
                "promotion-log.jsonl");
            var records = await store.QueryPromotionRecordsAsync("workspace-test", "collection-test", 10);

            Assert.IsTrue(File.Exists(promotionLogPath));
            Assert.AreEqual(1, records.Count);
            Assert.AreEqual(record.Id, records[0].Id);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task MemoryPromotionService_Deprecate_ShouldUpdateStatusAndRecordLog()
    {
        var store = new InMemoryMemoryStore();
        var service = new BasicMemoryPromotionService(store, store);

        await store.SaveAsync(CreateMemoryItem(
            "memory-1",
            ContextMemoryLayer.Stable,
            DateTimeOffset.UtcNow,
            ContextMemoryStatus.Stable));

        var record = await service.DeprecateAsync(
            "workspace-test",
            "collection-test",
            "memory-1",
            "manual",
            "No longer valid",
            reviewer: "reviewer-test");

        var deprecated = await store.GetAsync("workspace-test", "collection-test", "memory-1");
        var records = await store.QueryPromotionRecordsAsync("workspace-test", "collection-test", 10);

        Assert.IsNotNull(deprecated);
        Assert.AreEqual(ContextMemoryStatus.Deprecated, deprecated!.Status);
        Assert.AreEqual(ContextMemoryStatus.Stable, record.FromStatus);
        Assert.AreEqual(ContextMemoryStatus.Deprecated, record.ToStatus);
        Assert.AreEqual("No longer valid", record.Reason);
        Assert.AreEqual("reviewer-test", record.Reviewer);
        Assert.AreEqual(ContextMemoryLayer.Stable, record.TargetLayer);
        Assert.AreEqual(1, records.Count);
        CollectionAssert.Contains(records[0].RelationRefs.ToArray(), "relation:memory-1");
    }

    [TestMethod]
    public async Task ControlRoomMemoryCommand_ShouldRejectAndDeprecateMemory()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var state = CreateControlRoomState(rootPath);
            var service = new ControlRoomService(state);

            await state.MemoryStore.SaveAsync(CreateMemoryItem(
                "reject-me",
                ContextMemoryLayer.Structured,
                DateTimeOffset.UtcNow,
                ContextMemoryStatus.Candidate));
            await state.MemoryStore.SaveAsync(CreateMemoryItem(
                "deprecate-me",
                ContextMemoryLayer.Stable,
                DateTimeOffset.UtcNow,
                ContextMemoryStatus.Stable));

            var originalOut = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);

                await MemoryCommand.ExecuteAsync(service, ["reject", "reject-me"]);
                await MemoryCommand.ExecuteAsync(service, ["deprecate", "deprecate-me"]);

                var output = writer.ToString();
                StringAssert.Contains(output, "已拒绝 reject-me");
                StringAssert.Contains(output, "已废弃 deprecate-me");
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            var rejected = await state.MemoryStore.GetAsync("workspace-test", "collection-test", "reject-me");
            var deprecated = await state.MemoryStore.GetAsync("workspace-test", "collection-test", "deprecate-me");

            Assert.IsNotNull(rejected);
            Assert.AreEqual(ContextMemoryStatus.Rejected, rejected!.Status);
            Assert.IsNotNull(deprecated);
            Assert.AreEqual(ContextMemoryStatus.Deprecated, deprecated!.Status);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task FileGlobalContextStore_Query_ShouldFilterScopesAndCollection()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var store = new FileGlobalContextStore(new FileStorageOptions { RootPath = rootPath });

            await store.SaveAsync(CreateGlobalItem(
                "workspace-global",
                ContextScope.Workspace,
                collectionId: null,
                tags: new[] { "shared", "workspace" }));
            await store.SaveAsync(CreateGlobalItem(
                "collection-global",
                ContextScope.Collection,
                collectionId: "collection-test",
                tags: new[] { "shared", "collection" }));
            await store.SaveAsync(CreateGlobalItem(
                "session-global",
                ContextScope.Session,
                collectionId: "collection-test",
                tags: new[] { "shared", "session" }));
            await store.SaveAsync(CreateGlobalItem(
                "task-global",
                ContextScope.Task,
                collectionId: "collection-test",
                tags: new[] { "shared", "task" }));
            await store.SaveAsync(CreateGlobalItem(
                "other-collection-global",
                ContextScope.Collection,
                collectionId: "other-collection",
                tags: new[] { "shared", "other" }));

            var collectionItems = await store.QueryAsync(new ContextGlobalQuery
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Take = 10
            });
            var collectionIds = collectionItems.Select(item => item.Id).ToArray();

            CollectionAssert.Contains(collectionIds, "workspace-global");
            CollectionAssert.Contains(collectionIds, "collection-global");
            CollectionAssert.Contains(collectionIds, "session-global");
            CollectionAssert.Contains(collectionIds, "task-global");
            CollectionAssert.DoesNotContain(collectionIds, "other-collection-global");

            var sessionItems = await store.QueryAsync(new ContextGlobalQuery
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Scope = ContextScope.Session,
                Take = 10
            });
            var taskItems = await store.QueryAsync(new ContextGlobalQuery
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Scope = ContextScope.Task,
                Take = 10
            });

            Assert.AreEqual(1, sessionItems.Count);
            Assert.AreEqual("session-global", sessionItems[0].Id);
            Assert.AreEqual(1, taskItems.Count);
            Assert.AreEqual("task-global", taskItems[0].Id);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task InMemoryGlobalContextStore_Query_ShouldFilterTagsAndScope()
    {
        var store = new InMemoryGlobalContextStore();

        await store.SaveAsync(CreateGlobalItem(
            "matched",
            ContextScope.Task,
            collectionId: "collection-test",
            tags: new[] { "Shared", "Target" }));
        await store.SaveAsync(CreateGlobalItem(
            "missing-tag",
            ContextScope.Task,
            collectionId: "collection-test",
            tags: new[] { "shared" }));
        await store.SaveAsync(CreateGlobalItem(
            "wrong-scope",
            ContextScope.Session,
            collectionId: "collection-test",
            tags: new[] { "shared", "target" }));

        var results = await store.QueryAsync(new ContextGlobalQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Scope = ContextScope.Task,
            Tags = new[] { "shared", "target" },
            Take = 10
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("matched", results[0].Id);
    }

    [TestMethod]
    public async Task ControlRoomDashboard_ShouldShowGlobalItemsCount()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var state = CreateControlRoomState(rootPath);
            var service = new ControlRoomService(state);

            await state.GlobalContextStore.SaveAsync(CreateGlobalItem(
                "workspace-global",
                ContextScope.Workspace,
                collectionId: null));
            await state.GlobalContextStore.SaveAsync(CreateGlobalItem(
                "collection-global",
                ContextScope.Collection,
                collectionId: "collection-test"));
            await state.GlobalContextStore.SaveAsync(CreateGlobalItem(
                "other-collection-global",
                ContextScope.Collection,
                collectionId: "other-collection"));

            var dashboard = await service.GetDashboardAsync();

            Assert.AreEqual(2, dashboard.Memory.GlobalItems);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task PackageBuilder_ShouldIncludeConstraintsAndGlobalContext()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var options = new FileStorageOptions { RootPath = rootPath };
            var contextStore = new FileContextStore(options);
            var constraintStore = new FileConstraintStore(options);
            var globalStore = new FileGlobalContextStore(options);
            var memoryStore = new FileMemoryStore(options);
            var relationStore = new FileRelationStore(options);
            var builder = new BasicContextPackageBuilder(
                contextStore,
                constraintStore,
                globalStore,
                memoryStore,
                relationStore);

            await constraintStore.SaveAsync(CreateConstraint(
                "hard-1",
                ConstraintLevel.Hard,
                "Never leak private workspace data."));

            await globalStore.SaveAsync(new ContextGlobalItem
            {
                Id = "global-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Scope = ContextScope.Workspace,
                Type = "profile",
                Content = "Global preference: keep context packages compact.",
                ContentFormat = ContextContentFormat.PlainText,
                Tags = new[] { "global" },
                SourceRefs = new[] { "source:global" },
                Importance = 1.0,
                Version = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            await memoryStore.AddAsync(CreateMemoryItem(
                "working-1",
                ContextMemoryLayer.Working,
                DateTimeOffset.UtcNow,
                ContextMemoryStatus.Verified));

            await contextStore.SaveAsync(CreateItem(
                id: "raw-1",
                type: "note",
                content: "Recent raw context.",
                tags: new[] { "package" }));

            var package = await builder.BuildAsync(new ContextPackageRequest
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 500,
                Policy = new ContextPackagePolicy
                {
                    Id = "policy-1",
                    WorkspaceId = "workspace-test",
                    CollectionId = "collection-test",
                    TokenBudget = 500,
                    IncludeGlobalContext = true,
                    IncludeHardConstraints = true,
                    IncludeWorkingMemory = true,
                    IncludeRecentRawContext = true,
                    IncludeStableMemory = false,
                    IncludeSoftConstraints = false,
                    MaxRecentItems = 5
                }
            });

            var sectionNames = package.Sections.Select(section => section.Name).ToArray();

            CollectionAssert.Contains(sectionNames, "hard_constraints");
            CollectionAssert.Contains(sectionNames, "working_memory");
            CollectionAssert.Contains(sectionNames, "global_context");
            CollectionAssert.Contains(sectionNames, "recent_context");
            StringAssert.Contains(
                package.Sections.Single(section => section.Name == "global_context").Content,
                "Global preference: keep context packages compact.");
            Assert.IsTrue(package.EstimatedTokens <= 500);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task PackageBuilder_ShouldForceHardConstraintsAndOptionallyIncludeSoftConstraints()
    {
        var contextStore = new InMemoryContextStore();
        var constraintStore = new InMemoryConstraintStore();
        var builder = new BasicContextPackageBuilder(
            contextStore,
            constraintStore,
            globalContextStore: null,
            memoryStore: null,
            relationStore: null);

        await constraintStore.SaveAsync(CreateConstraint(
            "hard-constraint",
            ConstraintLevel.Hard,
            "Must preserve hard constraints."));
        await constraintStore.SaveAsync(CreateConstraint(
            "soft-constraint",
            ConstraintLevel.Soft,
            "Prefer concise package output."));

        var hardOnly = await builder.BuildAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TokenBudget = 500,
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 500,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeGlobalContext = false,
                IncludeWorkingMemory = false,
                IncludeStableMemory = false,
                IncludeRecentRawContext = false
            }
        });

        var hardOnlyNames = hardOnly.Sections.Select(section => section.Name).ToArray();

        CollectionAssert.Contains(hardOnlyNames, "hard_constraints");
        CollectionAssert.DoesNotContain(hardOnlyNames, "soft_constraints");
        StringAssert.Contains(
            hardOnly.Sections.Single(section => section.Name == "hard_constraints").Content,
            "Must preserve hard constraints.");

        var withSoft = await builder.BuildAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TokenBudget = 500,
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 500,
                IncludeSoftConstraints = true,
                IncludeGlobalContext = false,
                IncludeWorkingMemory = false,
                IncludeStableMemory = false,
                IncludeRecentRawContext = false
            }
        });

        var withSoftNames = withSoft.Sections.Select(section => section.Name).ToArray();

        CollectionAssert.Contains(withSoftNames, "hard_constraints");
        CollectionAssert.Contains(withSoftNames, "soft_constraints");
        StringAssert.Contains(
            withSoft.Sections.Single(section => section.Name == "soft_constraints").Content,
            "Prefer concise package output.");
    }

    [TestMethod]
    public async Task PackageBuilderPolicy_ShouldOrderSectionsByPolicyAndPriority()
    {
        var contextStore = new InMemoryContextStore();
        var constraintStore = new InMemoryConstraintStore();
        var globalStore = new InMemoryGlobalContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var builder = new BasicContextPackageBuilder(
            contextStore,
            constraintStore,
            globalStore,
            memoryStore,
            relationStore: null);
        var now = DateTimeOffset.UtcNow;

        await constraintStore.SaveAsync(CreateConstraint(
            "hard-order",
            ConstraintLevel.Hard,
            "Hard order constraint."));
        await constraintStore.SaveAsync(CreateConstraint(
            "soft-order",
            ConstraintLevel.Soft,
            "Soft order constraint."));
        await memoryStore.SaveAsync(CreateMemoryItem(
            "working-order",
            ContextMemoryLayer.Working,
            now,
            ContextMemoryStatus.Verified));
        await memoryStore.SaveAsync(CreateMemoryItem(
            "stable-order",
            ContextMemoryLayer.Stable,
            now,
            ContextMemoryStatus.Stable));
        await globalStore.SaveAsync(CreateGlobalItem(
            "global-order",
            ContextScope.Workspace,
            collectionId: null));
        await contextStore.SaveAsync(CreateItem(
            id: "recent-order",
            type: "note",
            content: "Recent policy ordered context."));

        var package = await builder.BuildAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TokenBudget = 1_000,
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 1_000,
                IncludeGlobalContext = true,
                IncludeSoftConstraints = true,
                IncludeWorkingMemory = true,
                IncludeStableMemory = true,
                IncludeRecentRawContext = true,
                SectionOrder = new[] { "recent-raw-context", "global-context", "hard-constraints" },
                SectionPriorities = new Dictionary<string, int>
                {
                    ["SOFT-CONSTRAINTS"] = 95,
                    ["stable-memory"] = 10
                }
            }
        });

        var sectionNames = package.Sections.Select(section => section.Name).ToArray();

        CollectionAssert.AreEqual(
            new[]
            {
                "recent_context",
                "global_context",
                "hard_constraints",
                "soft_constraints",
                "working_memory",
                "stable_memory"
            },
            sectionNames);
        Assert.AreEqual(95, package.Sections.Single(section => section.Name == "soft_constraints").Priority);
        Assert.AreEqual(10, package.Sections.Single(section => section.Name == "stable_memory").Priority);
    }

    [TestMethod]
    public async Task PackageBuilderPolicy_ShouldApplySectionTokenBudgetsAndDefaultPriority()
    {
        var contextStore = new InMemoryContextStore();
        var builder = new BasicContextPackageBuilder(
            contextStore,
            constraintStore: null,
            globalContextStore: null,
            memoryStore: null,
            relationStore: null);

        await contextStore.SaveAsync(CreateItem(
            id: "recent-budget",
            type: "note",
            content: new string('x', 80)));

        var package = await builder.BuildAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TokenBudget = 100,
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 100,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = false,
                IncludeStableMemory = false,
                IncludeRecentRawContext = true,
                SectionPriorities = new Dictionary<string, int>
                {
                    ["default"] = 12
                },
                SectionTokenBudgets = new Dictionary<string, int>
                {
                    ["recent-raw-context"] = 5
                }
            }
        });

        var section = package.Sections.Single();

        Assert.AreEqual("recent_context", section.Name);
        Assert.AreEqual(12, section.Priority);
        Assert.IsTrue(section.EstimatedTokens <= 5);
        Assert.IsTrue(package.EstimatedTokens <= 5);
    }

    [TestMethod]
    public async Task PackageBuilderPolicy_ShouldRespectOptionalIncludeFlagsAndKeepSafetyHardConstraints()
    {
        var contextStore = new InMemoryContextStore();
        var constraintStore = new InMemoryConstraintStore();
        var globalStore = new InMemoryGlobalContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var builder = new BasicContextPackageBuilder(
            contextStore,
            constraintStore,
            globalStore,
            memoryStore,
            relationStore: null);
        var now = DateTimeOffset.UtcNow;

        await constraintStore.SaveAsync(CreateConstraint(
            "hard-flag",
            ConstraintLevel.Hard,
            "Hard constraints remain safety enforced."));
        await constraintStore.SaveAsync(CreateConstraint(
            "soft-flag",
            ConstraintLevel.Soft,
            "Soft constraint should be optional."));
        await memoryStore.SaveAsync(CreateMemoryItem(
            "working-flag",
            ContextMemoryLayer.Working,
            now));
        await memoryStore.SaveAsync(CreateMemoryItem(
            "stable-flag",
            ContextMemoryLayer.Stable,
            now,
            ContextMemoryStatus.Stable));
        await globalStore.SaveAsync(CreateGlobalItem(
            "global-flag",
            ContextScope.Workspace,
            collectionId: null));
        await contextStore.SaveAsync(CreateItem(
            id: "recent-flag",
            type: "note",
            content: "Recent context should be optional."));

        var package = await builder.BuildAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TokenBudget = 1_000,
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 1_000,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = false,
                IncludeStableMemory = false,
                IncludeRecentRawContext = false
            }
        });

        var sectionNames = package.Sections.Select(section => section.Name).ToArray();

        CollectionAssert.AreEqual(new[] { "hard_constraints" }, sectionNames);
        StringAssert.Contains(
            package.Sections.Single().Content,
            "Hard constraints remain safety enforced.");
    }

    [TestMethod]
    public async Task CollectionValidationService_ShouldDetectCollectionIntegrityIssues()
    {
        var duplicateOne = CreateItem(
            id: "duplicate",
            type: "note",
            content: "Duplicate one.");
        var duplicateTwo = CreateItem(
            id: "duplicate",
            type: "note",
            content: "Duplicate two.");
        var items = new[]
        {
            CreateItem(
                id: "root",
                type: "note",
                content: "Root content.",
                refs: new[] { "missing-ref" }),
            CreateItem(
                id: "derived",
                type: "summary",
                content: "Derived content.",
                metadata: new Dictionary<string, string>
                {
                    ["derivedFrom"] = "root,missing-derived"
                }),
            CreateItem(
                id: "cycle-a",
                type: "note",
                content: "Cycle A.",
                refs: new[] { "cycle-b" }),
            CreateItem(
                id: "cycle-b",
                type: "note",
                content: "Cycle B.",
                refs: new[] { "cycle-a" }),
            duplicateOne,
            duplicateTwo
        };
        var contextStore = new DuplicateContextStore(items);
        var relationStore = new InMemoryRelationStore();
        var service = new CollectionValidationService(contextStore, relationStore);

        await relationStore.SaveAsync(CreateRelation(
            id: "missing-source",
            sourceId: "missing-source-item",
            targetId: "root",
            relationType: ContextRelationTypes.RelatedTo));
        await relationStore.SaveAsync(CreateRelation(
            id: "missing-target",
            sourceId: "root",
            targetId: "missing-target-item",
            relationType: ContextRelationTypes.RelatedTo));

        var report = await service.ValidateAsync("workspace-test", "collection-test");
        var issueCodes = report.Issues.Select(issue => issue.Code).ToArray();

        Assert.IsFalse(report.Succeeded);
        Assert.AreEqual(items.Length, report.ItemCount);
        Assert.AreEqual(2, report.RelationCount);
        CollectionAssert.Contains(issueCodes, "DuplicateId");
        CollectionAssert.Contains(issueCodes, "OrphanRef");
        CollectionAssert.Contains(issueCodes, "CircularReference");
        CollectionAssert.Contains(issueCodes, "MissingRelationSource");
        CollectionAssert.Contains(issueCodes, "MissingRelationTarget");
        Assert.IsTrue(report.Issues.Any(issue => issue.Message.Contains("missing-derived", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task CollectionValidationService_ShouldPassForValidCollection()
    {
        var contextStore = new InMemoryContextStore();
        var relationStore = new InMemoryRelationStore();
        var service = new CollectionValidationService(contextStore, relationStore);

        await contextStore.SaveAsync(CreateItem(
            id: "source",
            type: "note",
            content: "Source content."));
        await contextStore.SaveAsync(CreateItem(
            id: "summary",
            type: "summary",
            content: "Summary content.",
            refs: new[] { "source" },
            sourceRefs: new[] { "source", "source:external" },
            metadata: new Dictionary<string, string>
            {
                ["derivedFrom"] = "source"
            }));
        await relationStore.SaveAsync(CreateRelation(
            id: "summary-source",
            sourceId: "summary",
            targetId: "source",
            relationType: ContextRelationTypes.DerivedFrom));

        var report = await service.ValidateAsync("workspace-test", "collection-test");

        Assert.IsTrue(report.Succeeded);
        Assert.AreEqual(2, report.ItemCount);
        Assert.AreEqual(1, report.RelationCount);
        Assert.AreEqual(0, report.Issues.Count);
    }

    [TestMethod]
    public async Task ControlRoomReport_ShouldIncludeValidationReport()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var state = CreateControlRoomState(rootPath);
            var service = new ControlRoomService(state);

            await state.ContextStore.SaveAsync(CreateItem(
                id: "report-item",
                type: "note",
                content: "Report item.",
                refs: new[] { "missing-report-ref" }));

            var markdown = await service.BuildMarkdownReportAsync();

            StringAssert.Contains(markdown, "## Validation Report");
            StringAssert.Contains(markdown, "failed");
            StringAssert.Contains(markdown, "OrphanRef");
            StringAssert.Contains(markdown, "missing-report-ref");
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task P1EndToEnd_ShouldSupportContextLifecycleAndReporting()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var state = ControlRoomService.CreateState(
                "filesystem",
                rootPath,
                "workspace-test",
                "collection-test");
            var service = new ControlRoomService(state);
            var relationBuilder = new RelationBuilder();
            var validationService = new CollectionValidationService(state.ContextStore, state.RelationStore);
            var now = DateTimeOffset.UtcNow;

            await state.ContextStore.SaveAsync(CreateItem(
                id: "raw-1",
                type: "note",
                content: "Raw context alpha.",
                tags: new[] { "p1", "source" },
                refs: new[] { "raw-2" }));
            await state.ContextStore.SaveAsync(CreateItem(
                id: "raw-2",
                type: "note",
                content: "Raw context beta.",
                tags: new[] { "p1", "support" }));
            await state.ContextStore.SaveAsync(CreateItem(
                id: "summary-1",
                type: "summary",
                content: "Derived summary for alpha.",
                tags: new[] { "p1", "summary" },
                metadata: new Dictionary<string, string> { ["derivedFrom"] = "raw-1" }));

            await state.WorkingMemory.AddAsync(CreateWorkingMemoryItem("work-1", now.AddMinutes(-2)));
            await state.MemoryStore.SaveAsync(CreateMemoryItem(
                "memory-1",
                ContextMemoryLayer.Working,
                now.AddMinutes(-1),
                ContextMemoryStatus.Verified));
            await state.PromotionService.PromoteAsync(
                "workspace-test",
                "collection-test",
                "memory-1",
                "manual",
                "Preserve stable context");
            await state.WorkingMemory.SetActiveContextAsync(new WorkingMemoryActiveContext
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                CurrentTaskId = "task-1",
                Summary = "Working on P1 smoke test.",
                MemoryRefs = new[] { "work-1", "memory-1" },
                ContextRefs = new[] { "raw-1", "summary-1" },
                UpdatedAt = now
            });
            await state.WorkingMemory.SetCurrentTaskAsync(new WorkingMemoryCurrentTask
            {
                TaskId = "task-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Title = "P1 smoke",
                Description = "Validate context management.",
                Status = "running",
                Tags = new[] { "p1", "smoke" },
                CreatedAt = now.AddMinutes(-5),
                UpdatedAt = now
            });

            await state.GlobalContextStore.SaveAsync(CreateGlobalItem(
                "global-workspace",
                ContextScope.Workspace,
                collectionId: null,
                tags: new[] { "global" }));
            await state.GlobalContextStore.SaveAsync(CreateGlobalItem(
                "global-collection",
                ContextScope.Collection,
                collectionId: "collection-test",
                tags: new[] { "global", "collection" }));

            await state.ConstraintStore.SaveAsync(CreateConstraint(
                "hard-keep-source",
                ConstraintLevel.Hard,
                "Keep source context available."));
            await state.ConstraintStore.SaveAsync(CreateConstraint(
                "soft-prefer-compact",
                ConstraintLevel.Soft,
                "Prefer compact packages."));

            await state.RelationStore.SaveAsync(CreateRelation(
                "relation-raw",
                "raw-1",
                "raw-2",
                ContextRelationTypes.RelatedTo));
            await state.RelationStore.SaveAsync(new ContextRelation
            {
                Id = "generated-summary",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                SourceId = "summary-1",
                TargetId = "operation-1",
                RelationType = ContextRelationTypes.GeneratedBy,
                Weight = 1.0,
                Confidence = 1.0,
                SourceRefs = new[] { "summary-1", "operation-1" },
                CreatedAt = now
            });

            var package = await state.PackageBuilder.BuildAsync(new ContextPackageRequest
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                QueryText = "Raw",
                RequiredTags = new[] { "p1" },
                TokenBudget = 500,
                Policy = new ContextPackagePolicy
                {
                    WorkspaceId = "workspace-test",
                    CollectionId = "collection-test",
                    TokenBudget = 500,
                    IncludeGlobalContext = true,
                    IncludeHardConstraints = true,
                    IncludeSoftConstraints = true,
                    IncludeWorkingMemory = true,
                    IncludeStableMemory = true,
                    IncludeRecentRawContext = true,
                    MaxRecentItems = 10,
                    SectionOrder = new[]
                    {
                        "hard_constraints",
                        "working_memory",
                        "global_context",
                        "recent_context",
                        "stable_memory",
                        "soft_constraints",
                        "related_context"
                    },
                    SectionTokenBudgets = new Dictionary<string, int>
                    {
                        ["recent_context"] = 120
                    }
                }
            });

            var packageRelations = relationBuilder.BuildForPackage(package);
            await state.RelationStore.SaveManyAsync(packageRelations);

            var validationReport = await validationService.ValidateAsync("workspace-test", "collection-test");
            var dashboard = await service.GetDashboardAsync();
            var markdown = await service.BuildMarkdownReportAsync();
            var sectionNames = package.Sections.Select(section => section.Name).ToArray();

            CollectionAssert.Contains(sectionNames, "hard_constraints");
            CollectionAssert.Contains(sectionNames, "working_memory");
            CollectionAssert.Contains(sectionNames, "global_context");
            CollectionAssert.Contains(sectionNames, "recent_context");
            CollectionAssert.Contains(sectionNames, "stable_memory");
            CollectionAssert.Contains(sectionNames, "soft_constraints");
            CollectionAssert.Contains(sectionNames, "related_context");
            Assert.IsTrue(package.EstimatedTokens <= 500);
            Assert.IsTrue(packageRelations.Count > 0);
            Assert.IsTrue(packageRelations.All(item => item.RelationType == ContextRelationTypes.IncludedInPackage));
            Assert.IsTrue(validationReport.Succeeded);
            Assert.IsTrue(validationReport.Issues.Count == 0);
            Assert.AreEqual(2, dashboard.Memory.GlobalItems);
            Assert.AreEqual(2, dashboard.Memory.Constraints);
            StringAssert.Contains(markdown, "## Validation Report");
            StringAssert.Contains(markdown, "passed");
            StringAssert.Contains(markdown, "No validation issues");
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ConstraintScreen_ShouldShowConstraintDetails()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var state = CreateControlRoomState(rootPath);
            var service = new ControlRoomService(state);

            await state.ConstraintStore.SaveAsync(CreateConstraint(
                "constraint-detail",
                ConstraintLevel.User,
                "User-level constraint details.",
                appliesToRefs: new[] { "item-1" },
                sourceRefs: new[] { "source:constraint-detail" }));

            var originalIn = Console.In;
            var originalOut = Console.Out;
            try
            {
                using var reader = new StringReader(string.Join(
                    Environment.NewLine,
                    "constraint-detail",
                    "0",
                    "0"));
                using var writer = new StringWriter();
                Console.SetIn(reader);
                Console.SetOut(writer);

                var action = await ConstraintScreen.ShowAsync(service);
                var output = writer.ToString();

                Assert.AreEqual(ControlRoomActionKind.Back, action);
                StringAssert.Contains(output, "ContextConstraint constraint-detail");
                StringAssert.Contains(output, "User");
                StringAssert.Contains(output, "User-level constraint details.");
                StringAssert.Contains(output, "source:constraint-detail");
            }
            finally
            {
                Console.SetIn(originalIn);
                Console.SetOut(originalOut);
            }
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ContextRuntimeService_Ingest_ShouldPersistAndEmitEvents()
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
        var promotionService = new BasicMemoryPromotionService(memoryStore, memoryStore);
        var runtime = new ContextRuntimeService(
            contextStore,
            memoryStore,
            promotionService,
            packageBuilder,
            new ContextInputIngestionService(
                contextStore,
                new ContextInputNormalizer(),
                new ContextInputValidator(),
                new ContextInputHasher(),
                new ContextInputSequencer()),
            new ContextValidationService(),
            eventSink);

        var item = await runtime.IngestAsync(CreateItem(
            id: "",
            type: "note",
            content: "Runtime ingested content.",
            tags: new[] { "runtime" }));

        var stored = await contextStore.GetAsync(item.WorkspaceId, item.CollectionId, item.Id);

        Assert.IsNotNull(stored);
        Assert.AreEqual("Runtime ingested content.", stored!.Content);
        Assert.AreEqual(2, eventSink.Events.Count);
        Assert.IsTrue(eventSink.Events.Any(item => item.OperationName == "context.ingest"
            && item.Level == ContextEventLevel.Information));
    }

    [TestMethod]
    public async Task ContextRuntimeService_InvalidItem_ShouldEmitFailureEvent()
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
            new ContextInputIngestionService(
                contextStore,
                new ContextInputNormalizer(),
                new ContextInputValidator(),
                new ContextInputHasher(),
                new ContextInputSequencer()),
            new ContextValidationService(),
            eventSink);

        await Assert.ThrowsExceptionAsync<ArgumentException>(() => runtime.IngestAsync(new ContextItem
        {
            WorkspaceId = "",
            CollectionId = "collection-test",
            Type = "note",
            Content = "invalid"
        }));

        Assert.IsTrue(eventSink.Events.Any(item => item.OperationName == "context.ingest"
            && item.Level == ContextEventLevel.Error));
    }

    [TestMethod]
    public async Task FileConstraintStore_CorruptJsonLine_ShouldSkipBadLine()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var store = new FileConstraintStore(new FileStorageOptions { RootPath = rootPath });
            await store.SaveAsync(CreateConstraint(
                "hard-1",
                ConstraintLevel.Hard,
                "Valid constraint."));

            var constraintsPath = Path.Combine(
                rootPath,
                "workspaces",
                "workspace-test",
                "collections",
                "collection-test",
                "constraints",
                "constraints.jsonl");
            await File.AppendAllTextAsync(constraintsPath, Environment.NewLine + "{not valid json");

            var results = await store.QueryAsync(new ContextConstraintQuery
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Level = ConstraintLevel.Hard,
                Take = 10
            });

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual("hard-1", results[0].Id);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task Query_ExcludeDerived_ShouldNotReturnGeneratedSummary()
    {
        var store = new InMemoryContextStore();

        await store.SaveAsync(CreateItem(
            id: "raw-1",
            type: "note",
            content: "Base context.",
            tags: new[] { "base" }));
        await store.SaveAsync(CreateItem(
            id: "summary-1",
            type: "summary",
            content: "Generated summary.",
            tags: new[] { "summary" },
            metadata: new Dictionary<string, string>
            {
                ["isDerived"] = "true"
            }));

        var results = await store.QueryAsync(new ContextQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            IncludeDerived = false,
            Take = 10
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("raw-1", results[0].Id);
    }

    [TestMethod]
    public async Task Query_ExcludedTypes_ShouldExcludeSummary()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var store = new FileContextStore(new FileStorageOptions { RootPath = rootPath });

            await store.SaveAsync(CreateItem(
                id: "raw-1",
                type: "note",
                content: "Base context.",
                tags: new[] { "base" }));
            await store.SaveAsync(CreateItem(
                id: "summary-1",
                type: "summary",
                content: "Generated summary.",
                tags: new[] { "summary" }));

            var results = await store.QueryAsync(new ContextQuery
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                ExcludedTypes = new[] { "summary" },
                Take = 10
            });

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual("raw-1", results[0].Id);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task MockCompressor_GeneratedSummary_ShouldHaveDerivedMetadata()
    {
        var compressor = new MockContextCompressor();
        var input1 = CreateItem(
            id: "input-1",
            type: "note",
            content: "First input.",
            tags: new[] { "alpha" });
        var input2 = CreateItem(
            id: "input-2",
            type: "note",
            content: "Second input.",
            tags: new[] { "beta" });

        var response = await compressor.CompressAsync(new CompressionRequest
        {
            OperationId = "operation-derived",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TaskKind = CompressionTaskKind.Summarize,
            Inputs = new[] { input1, input2 },
            Options = new CompressionOptions()
        });

        var summary = response.GeneratedItems.Single();

        Assert.AreEqual("summary", summary.Type);
        Assert.AreEqual(ContextContentFormat.Markdown, summary.ContentFormat);
        Assert.AreEqual("true", summary.Metadata["isDerived"]);
        Assert.AreEqual("operation-derived", summary.Metadata["operationId"]);
        Assert.AreEqual(CompressionTaskKind.Summarize.ToString(), summary.Metadata["taskKind"]);
        Assert.AreEqual("input-1,input-2", summary.Metadata["derivedFrom"]);
        CollectionAssert.Contains(summary.SourceRefs.ToArray(), "input-1");
        CollectionAssert.Contains(summary.SourceRefs.ToArray(), "input-2");
        CollectionAssert.Contains(summary.SourceRefs.ToArray(), "source:input-1");
        CollectionAssert.Contains(summary.SourceRefs.ToArray(), "source:input-2");
    }

    [TestMethod]
    public async Task CompressionInput_ShouldNotIncludePreviousSummary()
    {
        var store = new InMemoryContextStore();
        var compressor = new MockContextCompressor();
        var rawItem = CreateItem(
            id: "raw-1",
            type: "note",
            content: "Raw context to summarize.",
            tags: new[] { "raw" });
        await store.SaveAsync(rawItem);

        var firstResponse = await compressor.CompressAsync(new CompressionRequest
        {
            OperationId = "first-compression",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TaskKind = CompressionTaskKind.Summarize,
            Inputs = new[] { rawItem },
            Options = new CompressionOptions()
        });
        await store.SaveAsync(firstResponse.GeneratedItems.Single());

        var nextInputs = await store.QueryAsync(new ContextQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            ExcludedTypes = new[] { "summary" },
            IncludeDerived = false,
            Take = 10,
            IncludeContent = true
        });

        Assert.AreEqual(1, nextInputs.Count);
        Assert.AreEqual("raw-1", nextInputs[0].Id);
        Assert.IsFalse(nextInputs.Any(item => item.Type == "summary"));
        Assert.IsFalse(nextInputs.Any(item => item.Metadata.TryGetValue("isDerived", out var isDerived)
            && string.Equals(isDerived, "true", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task RelationStore_SaveAndQueryBySource_ShouldWork()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var store = new FileRelationStore(new FileStorageOptions { RootPath = rootPath });
            var relation = CreateRelation(
                id: "relation-source",
                sourceId: "source-1",
                targetId: "target-1",
                relationType: ContextRelationTypes.RelatedTo);

            await store.SaveAsync(relation);

            var results = await store.QueryBySourceAsync("workspace-test", "collection-test", "source-1");

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual("relation-source", results[0].Id);
            Assert.AreEqual("workspace-test", results[0].WorkspaceId);
            Assert.AreEqual("collection-test", results[0].CollectionId);
            Assert.AreEqual(ContextRelationTypes.RelatedTo, results[0].RelationType);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task RelationStore_QueryByTarget_ShouldWork()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var store = new FileRelationStore(new FileStorageOptions { RootPath = rootPath });

            await store.SaveAsync(CreateRelation(
                id: "matched",
                sourceId: "source-1",
                targetId: "target-1",
                relationType: ContextRelationTypes.DependsOn));
            await store.SaveAsync(CreateRelation(
                id: "other",
                sourceId: "source-2",
                targetId: "target-2",
                relationType: ContextRelationTypes.DependsOn));

            var results = await store.QueryByTargetAsync("workspace-test", "collection-test", "target-1");

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual("matched", results[0].Id);
            Assert.AreEqual("source-1", results[0].SourceId);
            Assert.AreEqual("target-1", results[0].TargetId);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task RelationStore_SaveManyAndQueryForItem_ShouldWorkForFileSystem()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var store = new FileRelationStore(new FileStorageOptions { RootPath = rootPath });

            await AssertSaveManyAndQueryForItemAsync(store);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task RelationStore_SaveManyAndQueryForItem_ShouldWorkForInMemory()
    {
        var store = new InMemoryRelationStore();

        await AssertSaveManyAndQueryForItemAsync(store);
    }

    [TestMethod]
    public async Task RelationBuilder_ShouldCreateDerivedFromRelations()
    {
        var compressor = new MockContextCompressor();
        var builder = new RelationBuilder();
        var input1 = CreateItem(
            id: "input-1",
            type: "note",
            content: "First source.",
            tags: new[] { "alpha" });
        var input2 = CreateItem(
            id: "input-2",
            type: "note",
            content: "Second source.",
            tags: new[] { "beta" });

        var response = await compressor.CompressAsync(new CompressionRequest
        {
            OperationId = "relation-compression",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TaskKind = CompressionTaskKind.Summarize,
            Inputs = new[] { input1, input2 },
            Options = new CompressionOptions()
        });

        var relations = builder.BuildForCompressionResponse(response);
        var derivedFromRelations = relations
            .Where(item => item.RelationType == ContextRelationTypes.DerivedFrom)
            .ToArray();
        var summary = response.GeneratedItems.Single();

        Assert.AreEqual(2, derivedFromRelations.Length);
        Assert.IsTrue(derivedFromRelations.All(item => item.SourceId == summary.Id));
        CollectionAssert.AreEquivalent(
            new[] { "input-1", "input-2" },
            derivedFromRelations.Select(item => item.TargetId).ToArray());
        Assert.IsTrue(derivedFromRelations.All(item => item.WorkspaceId == "workspace-test"));
        Assert.IsTrue(derivedFromRelations.All(item => item.CollectionId == "collection-test"));
    }

    [TestMethod]
    public async Task RelationBuilder_ShouldCreateSummarizesRelationsForSummary()
    {
        var compressor = new MockContextCompressor();
        var builder = new RelationBuilder();
        var input = CreateItem(
            id: "input-1",
            type: "note",
            content: "Source to summarize.",
            tags: new[] { "alpha" });

        var response = await compressor.CompressAsync(new CompressionRequest
        {
            OperationId = "summary-relations",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TaskKind = CompressionTaskKind.Summarize,
            Inputs = new[] { input },
            Options = new CompressionOptions()
        });

        var relations = builder.BuildForCompressionResponse(response);
        var summary = response.GeneratedItems.Single();
        var summarizes = relations.Single(item => item.RelationType == ContextRelationTypes.Summarizes);

        Assert.AreEqual(summary.Id, summarizes.SourceId);
        Assert.AreEqual("input-1", summarizes.TargetId);
        Assert.AreEqual("summary-relations", summarizes.Metadata["operationId"]);
        CollectionAssert.Contains(summarizes.SourceRefs.ToArray(), "input-1");
    }

    [TestMethod]
    public async Task RelationBuilder_ShouldCreateGeneratedByRelationsForCompressionOutput()
    {
        var compressor = new MockContextCompressor();
        var builder = new RelationBuilder();
        var input = CreateItem(
            id: "input-1",
            type: "note",
            content: "Source to summarize.",
            tags: new[] { "alpha" });

        var response = await compressor.CompressAsync(new CompressionRequest
        {
            OperationId = "operation-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TaskKind = CompressionTaskKind.Summarize,
            Inputs = new[] { input },
            Options = new CompressionOptions()
        });

        var relations = builder.BuildForCompressionResponse(response);
        var summary = response.GeneratedItems.Single();
        var generatedBy = relations.Single(item => item.RelationType == ContextRelationTypes.GeneratedBy);

        Assert.AreEqual(summary.Id, generatedBy.SourceId);
        Assert.AreEqual("operation-1", generatedBy.TargetId);
        Assert.AreEqual("operation", generatedBy.Metadata["targetKind"]);
        CollectionAssert.Contains(generatedBy.SourceRefs.ToArray(), "operation-1");
    }

    [TestMethod]
    public void RelationBuilder_ShouldCreateRelatedToRelationsFromItemRefs()
    {
        var builder = new RelationBuilder();
        var now = DateTimeOffset.UtcNow;
        var item = new ContextItem
        {
            Id = "source-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "note",
            Content = "A note with references.",
            Refs = new[] { "target-1", "target-2", "target-1", "source-1" },
            SourceRefs = new[] { "source:source-1" },
            Importance = 0.7,
            CreatedAt = now,
            UpdatedAt = now
        };

        var relations = builder.BuildForContextItem(item);

        Assert.AreEqual(2, relations.Count);
        Assert.IsTrue(relations.All(relation => relation.RelationType == ContextRelationTypes.RelatedTo));
        Assert.IsTrue(relations.All(relation => relation.SourceId == "source-1"));
        CollectionAssert.AreEquivalent(
            new[] { "target-1", "target-2" },
            relations.Select(relation => relation.TargetId).ToArray());
        Assert.IsTrue(relations.All(relation => relation.Weight == 0.7));
    }

    [TestMethod]
    public void PackageRelationBuilder_ShouldCreateIncludedInPackageRelations()
    {
        var builder = new RelationBuilder();
        var package = new ContextPackage
        {
            PackageId = "package-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Sections = new[]
            {
                new ContextPackageSection
                {
                    Name = "recent_context",
                    Priority = 70,
                    Content = "Recent raw context.",
                    ContentFormat = ContextContentFormat.Markdown,
                    SourceRefs = new[] { "source:raw-1" },
                    ItemRefs = new[] { "raw-1" },
                    EstimatedTokens = 10
                },
                new ContextPackageSection
                {
                    Name = "legacy_section",
                    Priority = 10,
                    Content = "Fallback source refs.",
                    ContentFormat = ContextContentFormat.Markdown,
                    SourceRefs = new[] { "fallback-ref-1" },
                    EstimatedTokens = 6
                }
            },
            EstimatedTokens = 16,
            SourceRefs = new[] { "source:raw-1", "fallback-ref-1" },
            CreatedAt = DateTimeOffset.UtcNow
        };

        var relations = builder.BuildForPackage(package);

        Assert.AreEqual(2, relations.Count);
        Assert.IsTrue(relations.All(item => item.RelationType == ContextRelationTypes.IncludedInPackage));
        Assert.IsTrue(relations.Any(item => item.SourceId == "raw-1" && item.TargetId == "package-1"));
        Assert.IsTrue(relations.Any(item => item.SourceId == "fallback-ref-1" && item.TargetId == "package-1"));
        Assert.IsTrue(relations.All(item => item.WorkspaceId == "workspace-test"));
        Assert.IsTrue(relations.All(item => item.CollectionId == "collection-test"));
    }

    [TestMethod]
    public async Task ModelGateway_ShouldRouteByThinkingMode()
    {
        var deepSeek = TestModelAdapter.Success("deepseek-chat", "deepseek response");
        var pinai = TestModelAdapter.Success("pinai-gpt", "pinai response");
        var gateway = new ConfigurableModelGateway(
            CreateThinkingModeGatewayOptions(),
            new IModelAdapter[] { deepSeek, pinai });

        var balanced = await gateway.CompleteAsync(new ModelRequest
        {
            OperationId = "route-balanced",
            Role = ModelRole.GeneralCompression,
            Prompt = "Compress with balanced mode.",
            Metadata = new Dictionary<string, string>
            {
                ["compressionTask"] = "Summarize",
                ["thinkingMode"] = "balanced"
            }
        });
        var deep = await gateway.CompleteAsync(new ModelRequest
        {
            OperationId = "route-deep",
            Role = ModelRole.GeneralCompression,
            Prompt = "Compress with deep mode.",
            Metadata = new Dictionary<string, string>
            {
                ["compressionTask"] = "Summarize",
                ["thinkingMode"] = "deep"
            }
        });

        Assert.IsTrue(balanced.Succeeded);
        Assert.AreEqual("deepseek-chat", balanced.Metadata["modelName"]);
        Assert.AreEqual("deepseek response", balanced.Content);
        Assert.IsTrue(deep.Succeeded);
        Assert.AreEqual("pinai-gpt", deep.Metadata["modelName"]);
        Assert.AreEqual("pinai response", deep.Content);
        Assert.AreEqual(1, deepSeek.CallCount);
        Assert.AreEqual(1, pinai.CallCount);
    }

    [TestMethod]
    public async Task ModelGateway_ShouldPreferTaskSpecificRoute_WhenThinkingModeAlsoMatches()
    {
        var deepSeek = TestModelAdapter.Success("deepseek-chat", "generic response");
        var pinai = TestModelAdapter.Success("pinai-gpt", "extract response");
        var gateway = new ConfigurableModelGateway(
            CreateThinkingModeGatewayOptions(),
            new IModelAdapter[] { deepSeek, pinai });

        var response = await gateway.CompleteAsync(new ModelRequest
        {
            OperationId = "route-extract",
            Role = ModelRole.GeneralCompression,
            Prompt = "Extract key points with balanced mode.",
            Metadata = new Dictionary<string, string>
            {
                ["compressionTask"] = "ExtractKeyPoints",
                ["thinkingMode"] = "balanced"
            }
        });

        Assert.IsTrue(response.Succeeded);
        Assert.AreEqual("pinai-gpt", response.Metadata["modelName"]);
        Assert.AreEqual("extract response", response.Content);
        Assert.AreEqual(0, deepSeek.CallCount);
        Assert.AreEqual(1, pinai.CallCount);
    }

    [TestMethod]
    public async Task ModelGateway_ShouldRouteByConfiguredCategoryAndCapabilities()
    {
        var fast = TestModelAdapter.Success("fast-model", "fast response");
        var audit = TestModelAdapter.Success("audit-model", "audit response");
        var gateway = new ConfigurableModelGateway(
            CreateProfileGatewayOptions(),
            new IModelAdapter[] { fast, audit });

        var response = await gateway.CompleteAsync(new ModelRequest
        {
            OperationId = "route-category",
            Role = ModelRole.GeneralCompression,
            Prompt = "Audit this compression result.",
            Metadata = new Dictionary<string, string>
            {
                ["compressionTask"] = "Validate",
                ["thinkingMode"] = "audit"
            }
        });

        Assert.IsTrue(response.Succeeded);
        Assert.AreEqual("audit-model", response.Metadata["modelName"]);
        Assert.AreEqual("audit response", response.Content);
        Assert.AreEqual(0, fast.CallCount);
        Assert.AreEqual(1, audit.CallCount);
    }

    [TestMethod]
    public async Task ModelGateway_ShouldUsePrimaryModel_WhenAvailable()
    {
        var primary = TestModelAdapter.Success("primary-model", "primary response");
        var fallback = TestModelAdapter.Success("fallback-model", "fallback response");
        var gateway = new ConfigurableModelGateway(
            CreateModelGatewayOptions(highRisk: false, enableFallback: true),
            new IModelAdapter[] { primary, fallback });

        var response = await gateway.CompleteAsync(new ModelRequest
        {
            OperationId = "model-primary",
            Role = ModelRole.Router,
            Prompt = "Hello"
        });

        Assert.IsTrue(response.Succeeded);
        Assert.AreEqual("primary response", response.Content);
        Assert.AreEqual("false", response.Metadata["fallbackUsed"]);
        Assert.AreEqual(1, primary.CallCount);
        Assert.AreEqual(0, fallback.CallCount);
    }

    [TestMethod]
    public async Task ModelGateway_ShouldFallback_WhenPrimaryTimeout()
    {
        var primary = TestModelAdapter.Throws("primary-model", new TimeoutException("primary timed out"));
        var fallback = TestModelAdapter.Success("fallback-model", "fallback response");
        var gateway = new ConfigurableModelGateway(
            CreateModelGatewayOptions(highRisk: false, enableFallback: true),
            new IModelAdapter[] { primary, fallback });

        var response = await gateway.CompleteAsync(new ModelRequest
        {
            OperationId = "model-timeout",
            Role = ModelRole.Router,
            Prompt = "Hello"
        });

        Assert.IsTrue(response.Succeeded);
        Assert.AreEqual("fallback response", response.Content);
        Assert.AreEqual("true", response.Metadata["fallbackUsed"]);
        Assert.AreEqual("timeout", response.Metadata["fallbackReason"]);
        Assert.AreEqual(1, primary.CallCount);
        Assert.AreEqual(1, fallback.CallCount);
    }

    [TestMethod]
    public async Task ModelGateway_ShouldNotFallback_ForHighRiskTask_WhenDisabled()
    {
        var primary = TestModelAdapter.Throws("primary-model", new TimeoutException("primary timed out"));
        var fallback = TestModelAdapter.Success("fallback-model", "fallback response");
        var gateway = new ConfigurableModelGateway(
            CreateModelGatewayOptions(highRisk: true, enableFallback: false),
            new IModelAdapter[] { primary, fallback });

        var response = await gateway.CompleteAsync(new ModelRequest
        {
            OperationId = "model-high-risk",
            Role = ModelRole.Validator,
            Prompt = "Validate"
        });

        Assert.IsFalse(response.Succeeded);
        Assert.AreEqual("true", response.Metadata["requiresReview"]);
        Assert.AreEqual("highRiskTask", response.Metadata["fallbackBlocked"]);
        Assert.AreEqual(1, primary.CallCount);
        Assert.AreEqual(0, fallback.CallCount);
    }

    [TestMethod]
    public async Task ModelGateway_ShouldRecordUsageLog()
    {
        var primary = TestModelAdapter.Success("primary-model", "primary response");
        var usageLogStore = new InMemoryModelUsageLogStore();
        var gateway = new ConfigurableModelGateway(
            CreateModelGatewayOptions(highRisk: false, enableFallback: true),
            new IModelAdapter[] { primary, TestModelAdapter.Success("fallback-model", "fallback") },
            usageLogStore);

        await gateway.CompleteAsync(new ModelRequest
        {
            OperationId = "model-usage",
            Role = ModelRole.Router,
            Prompt = "Hello"
        });

        var logs = await usageLogStore.QueryRecentAsync(10);

        Assert.AreEqual(1, logs.Count);
        Assert.AreEqual("model-usage", logs[0].OperationId);
        Assert.AreEqual("primary-model", logs[0].ModelName);
        Assert.IsTrue(logs[0].Succeeded);
        Assert.IsFalse(logs[0].FallbackUsed);
    }

    [TestMethod]
    public async Task ModelGateway_ShouldUseFallbackRoleRoute_WhenExactRouteMissing()
    {
        var primary = TestModelAdapter.Success("primary-model", "primary response");
        var fallback = TestModelAdapter.Success("fallback-model", "fallback response");
        var gateway = new ConfigurableModelGateway(
            CreateModelGatewayOptions(
                highRisk: false,
                enableFallback: true,
                routeRole: ModelRole.Fallback),
            new IModelAdapter[] { primary, fallback });

        var response = await gateway.CompleteAsync(new ModelRequest
        {
            OperationId = "model-fallback-route",
            Role = ModelRole.StrongReasoning,
            Prompt = "Route through default fallback role."
        });

        Assert.IsTrue(response.Succeeded);
        Assert.AreEqual("primary response", response.Content);
        Assert.AreEqual("primary-model", response.Metadata["modelName"]);
        Assert.AreEqual(1, primary.CallCount);
        Assert.AreEqual(0, fallback.CallCount);
    }

    [TestMethod]
    public async Task ModelGateway_ShouldRetryPrimary_WhenFirstAttemptFails()
    {
        var primary = TestModelAdapter.Sequence(
            "primary-model",
            request => new ModelResponse
            {
                OperationId = request.OperationId,
                Content = string.Empty,
                Succeeded = false,
                ErrorMessage = "HTTP 500 server error",
                Metadata = new Dictionary<string, string>
                {
                    ["failureReason"] = "server_error"
                }
            },
            request => new ModelResponse
            {
                OperationId = request.OperationId,
                Content = "retry response",
                InputTokens = 4,
                OutputTokens = 3,
                Succeeded = true
            });
        var fallback = TestModelAdapter.Success("fallback-model", "fallback response");
        var usageLogStore = new InMemoryModelUsageLogStore();
        var gateway = new ConfigurableModelGateway(
            CreateModelGatewayOptions(
                highRisk: false,
                enableFallback: true,
                maxRetryCount: 1),
            new IModelAdapter[] { primary, fallback },
            usageLogStore);

        var response = await gateway.CompleteAsync(new ModelRequest
        {
            OperationId = "model-retry",
            Role = ModelRole.Router,
            Prompt = "Retry once."
        });

        var logs = await usageLogStore.QueryRecentAsync(10);

        Assert.IsTrue(response.Succeeded);
        Assert.AreEqual("retry response", response.Content);
        Assert.AreEqual("2", response.Metadata["attempt"]);
        Assert.AreEqual("false", response.Metadata["fallbackUsed"]);
        Assert.AreEqual(2, primary.CallCount);
        Assert.AreEqual(0, fallback.CallCount);
        Assert.AreEqual(2, logs.Count(log => log.ModelName == "primary-model"));
        Assert.IsTrue(logs.Any(log => log.ModelName == "primary-model" && !log.Succeeded));
        Assert.IsTrue(logs.Any(log => log.ModelName == "primary-model" && log.Succeeded));
    }

    [TestMethod]
    public async Task ModelGateway_ShouldTimeout_WhenAdapterExceedsEndpointTimeout()
    {
        var primary = TestModelAdapter.DelayedSuccess(
            "primary-model",
            TimeSpan.FromMilliseconds(250),
            "late response");
        var fallback = TestModelAdapter.Success("fallback-model", "fallback response");
        var gateway = new ConfigurableModelGateway(
            CreateModelGatewayOptions(
                highRisk: false,
                enableFallback: false,
                primaryTimeout: TimeSpan.FromMilliseconds(20)),
            new IModelAdapter[] { primary, fallback });

        var response = await gateway.CompleteAsync(new ModelRequest
        {
            OperationId = "model-timeout-enforced",
            Role = ModelRole.Router,
            Prompt = "Timeout quickly."
        });

        Assert.IsFalse(response.Succeeded);
        Assert.AreEqual("timeout", response.Metadata["failureReason"]);
        Assert.AreEqual("false", response.Metadata["fallbackUsed"]);
        Assert.AreEqual(1, primary.CallCount);
        Assert.AreEqual(0, fallback.CallCount);
    }

    [TestMethod]
    public async Task ModelGateway_ShouldNotFallback_WhenTimeoutFallbackDisabled()
    {
        var primary = TestModelAdapter.Throws("primary-model", new TimeoutException("primary timed out"));
        var fallback = TestModelAdapter.Success("fallback-model", "fallback response");
        var gateway = new ConfigurableModelGateway(
            CreateModelGatewayOptions(
                highRisk: false,
                enableFallback: true,
                fallbackOnTimeout: false),
            new IModelAdapter[] { primary, fallback });

        var response = await gateway.CompleteAsync(new ModelRequest
        {
            OperationId = "model-timeout-no-fallback",
            Role = ModelRole.Router,
            Prompt = "Do not fallback on timeout."
        });

        Assert.IsFalse(response.Succeeded);
        Assert.AreEqual("timeout", response.Metadata["failureReason"]);
        Assert.AreEqual("false", response.Metadata["fallbackUsed"]);
        Assert.AreEqual(1, primary.CallCount);
        Assert.AreEqual(0, fallback.CallCount);
    }

    [TestMethod]
    public async Task ModelGateway_ShouldFallback_WhenPrimaryReturnsInvalidJsonAndRouteAllowsIt()
    {
        var primary = TestModelAdapter.Success("primary-model", "not json");
        var fallback = TestModelAdapter.Success("fallback-model", "{\"ok\":true}");
        var gateway = new ConfigurableModelGateway(
            CreateModelGatewayOptions(highRisk: false, enableFallback: true),
            new IModelAdapter[] { primary, fallback });

        var response = await gateway.CompleteAsync(new ModelRequest
        {
            OperationId = "model-invalid-json",
            Role = ModelRole.Router,
            Prompt = "Return structured JSON.",
            ResponseFormat = "json"
        });

        Assert.IsTrue(response.Succeeded);
        Assert.AreEqual("{\"ok\":true}", response.Content);
        Assert.AreEqual("true", response.Metadata["fallbackUsed"]);
        Assert.AreEqual("invalid_json", response.Metadata["fallbackReason"]);
        Assert.AreEqual("primary-model", response.Metadata["primaryModelName"]);
        Assert.AreEqual(1, primary.CallCount);
        Assert.AreEqual(1, fallback.CallCount);
    }

    [TestMethod]
    public async Task ModelGateway_ShouldRecordFallbackUsageLog()
    {
        var primary = TestModelAdapter.Throws("primary-model", new TimeoutException("primary timed out"));
        var fallback = TestModelAdapter.Success("fallback-model", "fallback response");
        var usageLogStore = new InMemoryModelUsageLogStore();
        var gateway = new ConfigurableModelGateway(
            CreateModelGatewayOptions(highRisk: false, enableFallback: true),
            new IModelAdapter[] { primary, fallback },
            usageLogStore);

        var response = await gateway.CompleteAsync(new ModelRequest
        {
            OperationId = "model-fallback-usage",
            Role = ModelRole.Router,
            Prompt = "Fallback and log."
        });

        var logs = await usageLogStore.QueryRecentAsync(10);
        var primaryLog = logs.Single(log => log.ModelName == "primary-model");
        var fallbackLog = logs.Single(log => log.ModelName == "fallback-model");

        Assert.IsTrue(response.Succeeded);
        Assert.AreEqual("true", response.Metadata["fallbackUsed"]);
        Assert.IsFalse(primaryLog.Succeeded);
        Assert.IsFalse(primaryLog.FallbackUsed);
        Assert.IsTrue(fallbackLog.Succeeded);
        Assert.IsTrue(fallbackLog.FallbackUsed);
    }

    [TestMethod]
    public async Task OpenAiCompatibleAdapter_ShouldPostChatCompletions_WithJsonResponseFormatAndUsage()
    {
        var handler = CaptureHttpMessageHandler.Json("""
        {
          "model": "glm-4.7-flash",
          "choices": [
            {
              "message": {
                "role": "assistant",
                "content": "{\"summary\":\"ok\"}"
              },
              "finish_reason": "stop"
            }
          ],
          "usage": {
            "prompt_tokens": 11,
            "completion_tokens": 7,
            "total_tokens": 18
          }
        }
        """);
        var adapter = new OpenAiCompatibleModelAdapter(
            new ModelEndpointOptions
            {
                Name = "glm-4.7-flash",
                Provider = "openai-compatible",
                Endpoint = "https://open.bigmodel.cn/api/paas/v4/",
                ApiKey = "glm-secret",
                Enabled = true,
                Metadata = new Dictionary<string, string>
                {
                    ["model"] = "glm-4.7-flash"
                }
            },
            new HttpClient(handler));

        var response = await adapter.CompleteAsync(new ModelRequest
        {
            OperationId = "adapter-json",
            Role = ModelRole.GeneralCompression,
            SystemPrompt = "Return JSON.",
            Prompt = "Summarize.",
            ResponseFormat = "json"
        });

        using var payload = JsonDocument.Parse(handler.RequestBody);
        var root = payload.RootElement;
        var messages = root.GetProperty("messages");

        Assert.IsTrue(response.Succeeded);
        Assert.AreEqual("{\"summary\":\"ok\"}", response.Content);
        Assert.AreEqual(11, response.InputTokens);
        Assert.AreEqual(7, response.OutputTokens);
        Assert.AreEqual("18", response.Metadata["totalTokens"]);
        Assert.AreEqual("stop", response.Metadata["finishReason"]);
        Assert.AreEqual("glm-4.7-flash", response.Metadata["responseModel"]);
        Assert.AreEqual(new Uri("https://open.bigmodel.cn/api/paas/v4/chat/completions"), handler.RequestUri);
        Assert.AreEqual(HttpMethod.Post, handler.Method);
        Assert.AreEqual("Bearer", handler.Authorization?.Scheme);
        Assert.AreEqual("glm-secret", handler.Authorization?.Parameter);
        Assert.AreEqual("glm-4.7-flash", root.GetProperty("model").GetString());
        Assert.AreEqual("system", messages[0].GetProperty("role").GetString());
        Assert.AreEqual("Return JSON.", messages[0].GetProperty("content").GetString());
        Assert.AreEqual("user", messages[1].GetProperty("role").GetString());
        Assert.AreEqual("Summarize.", messages[1].GetProperty("content").GetString());
        Assert.AreEqual("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
    }

    [TestMethod]
    public async Task LocalHttpModelAdapter_ShouldSupportLocalOpenAiCompatibleServerWithoutApiKey()
    {
        var handler = CaptureHttpMessageHandler.Json("""
        {
          "choices": [
            {
              "message": {
                "content": "local response"
              }
            }
          ],
          "usage": {
            "input_tokens": 5,
            "output_tokens": 6
          }
        }
        """);
        var adapter = new LocalHttpModelAdapter(
            new ModelEndpointOptions
            {
                Name = "local-qwen",
                Provider = "local-http",
                Endpoint = "http://127.0.0.1:8080/v1/chat/completions",
                Enabled = true,
                Metadata = new Dictionary<string, string>
                {
                    ["model"] = "qwen3:1.7b"
                }
            },
            new HttpClient(handler));

        var response = await adapter.CompleteAsync(new ModelRequest
        {
            OperationId = "adapter-local",
            Role = ModelRole.GeneralCompression,
            Prompt = "Local call."
        });

        using var payload = JsonDocument.Parse(handler.RequestBody);

        Assert.IsTrue(response.Succeeded);
        Assert.AreEqual("local response", response.Content);
        Assert.AreEqual(5, response.InputTokens);
        Assert.AreEqual(6, response.OutputTokens);
        Assert.AreEqual("local-http", response.Metadata["provider"]);
        Assert.AreEqual(new Uri("http://127.0.0.1:8080/v1/chat/completions"), handler.RequestUri);
        Assert.IsNull(handler.Authorization);
        Assert.AreEqual("qwen3:1.7b", payload.RootElement.GetProperty("model").GetString());
        Assert.IsFalse(payload.RootElement.TryGetProperty("response_format", out _));
    }

    [TestMethod]
    public async Task OpenAiCompatibleAdapter_ShouldSkipJsonResponseFormat_WhenModelDisablesIt()
    {
        var handler = CaptureHttpMessageHandler.Json("""
        {
          "model": "deepseek-v4-pro",
          "choices": [
            {
              "message": {
                "role": "assistant",
                "content": "{\"ok\":true}"
              },
              "finish_reason": "stop"
            }
          ],
          "usage": {
            "prompt_tokens": 9,
            "completion_tokens": 4,
            "total_tokens": 13
          }
        }
        """);
        var adapter = new OpenAiCompatibleModelAdapter(
            new ModelEndpointOptions
            {
                Name = "deepseek-v4-pro",
                Provider = "deepseek",
                Endpoint = "https://api.deepseek.com/v1",
                ApiKey = "deepseek-secret",
                Enabled = true,
                Metadata = new Dictionary<string, string>
                {
                    ["model"] = "deepseek-v4-pro",
                    ["supportsJsonResponseFormat"] = "false"
                }
            },
            new HttpClient(handler));

        var response = await adapter.CompleteAsync(new ModelRequest
        {
            OperationId = "adapter-no-response-format",
            Role = ModelRole.GeneralCompression,
            Prompt = "Return JSON.",
            ResponseFormat = "json"
        });

        using var payload = JsonDocument.Parse(handler.RequestBody);

        Assert.IsTrue(response.Succeeded);
        Assert.AreEqual("{\"ok\":true}", response.Content);
        Assert.IsFalse(payload.RootElement.TryGetProperty("response_format", out _));
    }

    [TestMethod]
    public void ModelAdapterFactory_ShouldCreateAdapters_ForGlmDeepSeekAndLocalOpenAiCompatibleProviders()
    {
        var adapters = ModelAdapterFactory.CreateAdapters(new ModelGatewayOptions
        {
            Models = new[]
            {
                new ModelEndpointOptions
                {
                    Name = "glm-4.7-flash",
                    Provider = "bigmodel",
                    Endpoint = "https://open.bigmodel.cn/api/paas/v4",
                    ApiKey = "glm-secret",
                    Enabled = true
                },
                new ModelEndpointOptions
                {
                    Name = "deepseek-chat",
                    Provider = "deepseek",
                    Endpoint = "https://api.deepseek.com/v1",
                    ApiKey = "deepseek-secret",
                    Enabled = true
                },
                new ModelEndpointOptions
                {
                    Name = "local-qwen",
                    Provider = "local-openai-compatible",
                    Endpoint = "http://localhost:11434/v1",
                    Enabled = true
                }
            }
        });

        Assert.AreEqual(3, adapters.Count);
        Assert.IsInstanceOfType(adapters.Single(adapter => adapter.Name == "glm-4.7-flash"), typeof(OpenAiCompatibleModelAdapter));
        Assert.IsInstanceOfType(adapters.Single(adapter => adapter.Name == "deepseek-chat"), typeof(OpenAiCompatibleModelAdapter));
        Assert.IsInstanceOfType(adapters.Single(adapter => adapter.Name == "local-qwen"), typeof(LocalHttpModelAdapter));
    }

    [TestMethod]
    public async Task ModelHealthService_ShouldReportUnavailable_WhenAdapterFails()
    {
        var options = CreateModelGatewayOptions(highRisk: false, enableFallback: true);
        var failedAdapter = TestModelAdapter.Failure("primary-model", "adapter failed");
        var healthService = new ModelHealthService(options, new IModelAdapter[] { failedAdapter });

        var result = await healthService.CheckAsync("primary-model");

        Assert.AreEqual(ModelAvailability.Unavailable, result.Availability);
        Assert.AreEqual("primary-model", result.ModelName);
        Assert.IsTrue(result.LastError!.Contains("adapter failed", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ApiKeyResolver_ShouldResolveBigModelAndDeepSeekEnvironmentKeys()
    {
        var previousBigModel = Environment.GetEnvironmentVariable("BIGMODEL_API_KEY");
        var previousDeepSeek = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        var previousPinai = Environment.GetEnvironmentVariable("PINAI_OPENAI_API_KEY");

        try
        {
            Environment.SetEnvironmentVariable("BIGMODEL_API_KEY", "bigmodel-secret");
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "deepseek-secret");
            Environment.SetEnvironmentVariable("PINAI_OPENAI_API_KEY", "pinai-secret");
            var resolver = new ApiKeyResolver();

            var bigModel = resolver.Resolve(new ModelEndpointOptions
            {
                Name = "glm-4.7-flash",
                Provider = "openai-compatible",
                ApiKey = "env:BIGMODEL_API_KEY",
                Enabled = true
            });
            var deepSeek = resolver.Resolve(new ModelEndpointOptions
            {
                Name = "deepseek-v4-flash",
                Provider = "openai-compatible",
                ApiKey = "env:DEEPSEEK_API_KEY",
                Enabled = true
            });
            var pinai = resolver.Resolve(new ModelEndpointOptions
            {
                Name = "pinai-gpt",
                Provider = "openai-compatible",
                ApiKey = "env:PINAI_OPENAI_API_KEY",
                Enabled = true
            });

            Assert.IsTrue(bigModel.Required);
            Assert.IsTrue(bigModel.Configured);
            Assert.AreEqual("BIGMODEL_API_KEY", bigModel.EnvironmentVariableName);
            Assert.AreEqual("bigmodel-secret", bigModel.Value);
            Assert.IsTrue(deepSeek.Required);
            Assert.IsTrue(deepSeek.Configured);
            Assert.AreEqual("DEEPSEEK_API_KEY", deepSeek.EnvironmentVariableName);
            Assert.AreEqual("deepseek-secret", deepSeek.Value);
            Assert.IsTrue(pinai.Required);
            Assert.IsTrue(pinai.Configured);
            Assert.AreEqual("PINAI_OPENAI_API_KEY", pinai.EnvironmentVariableName);
            Assert.AreEqual("pinai-secret", pinai.Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BIGMODEL_API_KEY", previousBigModel);
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", previousDeepSeek);
            Environment.SetEnvironmentVariable("PINAI_OPENAI_API_KEY", previousPinai);
        }
    }

    [TestMethod]
    public void UserPrivateConfiguration_ShouldLoadPrivateApiKeysFromJson()
    {
        var rootPath = CreateTestRootPath();
        var jsonPath = Path.Combine(rootPath, "secrets.json");
        var previousDeepSeek = Environment.GetEnvironmentVariable("CONTEXTCORE_TEST_JSON_DEEPSEEK");
        var previousPinai = Environment.GetEnvironmentVariable("CONTEXTCORE_TEST_JSON_PINAI");
        var previousExisting = Environment.GetEnvironmentVariable("CONTEXTCORE_TEST_JSON_EXISTING");

        try
        {
            Directory.CreateDirectory(rootPath);
            File.WriteAllText(
                jsonPath,
                """
                {
                  "PrivateApiKeys": {
                    "CONTEXTCORE_TEST_JSON_DEEPSEEK": "json-deepseek-key",
                    "CONTEXTCORE_TEST_JSON_PINAI": "json-pinai-key",
                    "CONTEXTCORE_TEST_JSON_EXISTING": "json-should-not-overwrite"
                  }
                }
                """);
            Environment.SetEnvironmentVariable("CONTEXTCORE_TEST_JSON_DEEPSEEK", null);
            Environment.SetEnvironmentVariable("CONTEXTCORE_TEST_JSON_PINAI", null);
            Environment.SetEnvironmentVariable("CONTEXTCORE_TEST_JSON_EXISTING", "already-set");

            var loaded = UserPrivateConfiguration.LoadPrivateApiKeysFile(jsonPath, overwriteExisting: false);

            Assert.AreEqual(2, loaded);
            Assert.AreEqual("json-deepseek-key", Environment.GetEnvironmentVariable("CONTEXTCORE_TEST_JSON_DEEPSEEK"));
            Assert.AreEqual("json-pinai-key", Environment.GetEnvironmentVariable("CONTEXTCORE_TEST_JSON_PINAI"));
            Assert.AreEqual("already-set", Environment.GetEnvironmentVariable("CONTEXTCORE_TEST_JSON_EXISTING"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONTEXTCORE_TEST_JSON_DEEPSEEK", previousDeepSeek);
            Environment.SetEnvironmentVariable("CONTEXTCORE_TEST_JSON_PINAI", previousPinai);
            Environment.SetEnvironmentVariable("CONTEXTCORE_TEST_JSON_EXISTING", previousExisting);
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public void UserPrivateConfiguration_ShouldLoadEnvironmentFileWithoutOverwritingExistingVariables()
    {
        var rootPath = CreateTestRootPath();
        var envPath = Path.Combine(rootPath, ".env");
        var previousKey = Environment.GetEnvironmentVariable("CONTEXTCORE_TEST_PRIVATE_ENV");
        var previousExisting = Environment.GetEnvironmentVariable("CONTEXTCORE_TEST_EXISTING_ENV");

        try
        {
            Directory.CreateDirectory(rootPath);
            File.WriteAllText(
                envPath,
                """
                # local-only secrets
                CONTEXTCORE_TEST_PRIVATE_ENV="loaded-private-value"
                CONTEXTCORE_TEST_EXISTING_ENV=from-file
                """);
            Environment.SetEnvironmentVariable("CONTEXTCORE_TEST_PRIVATE_ENV", null);
            Environment.SetEnvironmentVariable("CONTEXTCORE_TEST_EXISTING_ENV", "already-set");

            var loaded = UserPrivateConfiguration.LoadEnvironmentFile(envPath, overwriteExisting: false);

            Assert.AreEqual(1, loaded);
            Assert.AreEqual("loaded-private-value", Environment.GetEnvironmentVariable("CONTEXTCORE_TEST_PRIVATE_ENV"));
            Assert.AreEqual("already-set", Environment.GetEnvironmentVariable("CONTEXTCORE_TEST_EXISTING_ENV"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONTEXTCORE_TEST_PRIVATE_ENV", previousKey);
            Environment.SetEnvironmentVariable("CONTEXTCORE_TEST_EXISTING_ENV", previousExisting);
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public void ModelGatewayDefaults_ShouldExposeMultiModelThinkingModeRoutes()
    {
        var options = ModelGatewayDefaults.CreateDefaultOptions();
        var modelNames = options.Models.Select(model => model.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        CollectionAssert.Contains(modelNames.ToArray(), "deepseek-v4-flash");
        CollectionAssert.Contains(modelNames.ToArray(), "deepseek-v4-pro");
        CollectionAssert.Contains(modelNames.ToArray(), "pinai-gpt-5.4-mini");
        CollectionAssert.Contains(modelNames.ToArray(), "pinai-gpt-5.4");
        CollectionAssert.Contains(modelNames.ToArray(), "pinai-gpt-5.5");
        Assert.IsTrue(options.Routes.Any(route =>
            route.Role == ModelRole.GeneralCompression
            && route.ThinkingMode == "fast"
            && route.PrimaryModelCategory == "fast"));
        Assert.IsTrue(options.Routes.Any(route =>
            route.Role == ModelRole.GeneralCompression
            && route.ThinkingMode == "balanced"
            && route.PrimaryModelCategory == "balanced"));
        Assert.IsTrue(options.Routes.Any(route =>
            route.Role == ModelRole.GeneralCompression
            && route.ThinkingMode == "deep"
            && route.PrimaryModelCategory == "deep"));
        Assert.IsTrue(options.Routes.Any(route =>
            route.Role == ModelRole.GeneralCompression
            && route.ThinkingMode == "audit"
            && route.PrimaryModelCategory == "audit"
            && route.HighRiskTask));
        Assert.IsTrue(options.ApiProviders.Any(provider => provider.Name == "deepseek"));
        Assert.IsTrue(options.ModelProfiles.Any(profile =>
            profile.Name == "deepseek-v4-pro"
            && profile.SupportsJsonResponseFormat == false));
        Assert.IsTrue(options.Routes
            .SelectMany(route => new[] { route.PrimaryModelName, route.FallbackModelName })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .All(name => modelNames.Contains(name!)));
    }

    [TestMethod]
    public void ModelRouteResolver_ShouldPreviewDefaultThinkingModeRoutes()
    {
        var options = ModelGatewayDefaults.CreateDefaultOptions();

        var fast = ModelRouteResolver.Resolve(options, CreateModelRoutePreviewRequest(
            thinkingMode: "fast",
            taskKind: "Summarize"));
        var balanced = ModelRouteResolver.Resolve(options, CreateModelRoutePreviewRequest(
            thinkingMode: "balanced",
            taskKind: "ExtractKeyPoints"));
        var deep = ModelRouteResolver.Resolve(options, CreateModelRoutePreviewRequest(
            thinkingMode: "deep",
            taskKind: "Summarize"));
        var audit = ModelRouteResolver.Resolve(options, CreateModelRoutePreviewRequest(
            thinkingMode: "audit",
            taskKind: "Validate"));
        var validator = ModelRouteResolver.Resolve(options, new ModelRequest
        {
            Role = ModelRole.Validator,
            Prompt = "校验压缩结果。",
            Metadata = new Dictionary<string, string>
            {
                ["compressionTask"] = "Validate",
                ["thinkingMode"] = "audit"
            }
        });

        Assert.AreEqual("deepseek-v4-flash", fast.Primary.ModelName);
        Assert.AreEqual("deepseek-v4-pro", balanced.Primary.ModelName);
        Assert.AreEqual("pinai-gpt-5.4", deep.Primary.ModelName);
        Assert.AreEqual("pinai-gpt-5.5", audit.Primary.ModelName);
        Assert.AreEqual("pinai-gpt-5.5", validator.Primary.ModelName);
        Assert.IsTrue(audit.Route!.HighRiskTask);
        CollectionAssert.Contains(balanced.Primary.Capabilities.ToArray(), "compression");
        CollectionAssert.Contains(deep.Primary.Capabilities.ToArray(), "reasoning");
        CollectionAssert.Contains(validator.Primary.Capabilities.ToArray(), "validation");
    }

    [TestMethod]
    public void ModelRouteResolver_ShouldNotExposeEndpointOrApiKey()
    {
        const string secret = "inline-secret-for-test";
        const string endpoint = "https://example.test/private/v1";
        var options = new ModelGatewayOptions
        {
            ApiProviders =
            [
                new ModelApiProviderOptions
                {
                    Name = "private-api",
                    Provider = "openai-compatible",
                    Endpoint = endpoint,
                    ApiKey = secret,
                    Enabled = true
                }
            ],
            ModelProfiles =
            [
                new ModelProfileOptions
                {
                    Name = "private-fast",
                    ApiProviderName = "private-api",
                    Model = "provider-fast",
                    Category = "fast",
                    Capabilities = ["compression"],
                    Roles = ["GeneralCompression"]
                }
            ],
            Routes =
            [
                new ModelRoleRoute
                {
                    Role = ModelRole.GeneralCompression,
                    PrimaryModelCategory = "fast",
                    RequiredCapabilities = ["compression"]
                }
            ]
        };

        var resolution = ModelRouteResolver.Resolve(options, CreateModelRoutePreviewRequest(
            thinkingMode: null,
            taskKind: "Summarize"));
        var serialized = JsonSerializer.Serialize(resolution);

        Assert.AreEqual("private-fast", resolution.Primary.ModelName);
        Assert.IsFalse(serialized.Contains(secret, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(serialized.Contains(endpoint, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ModelGatewayConfigurationValidator_ShouldRejectEnabledRemoteModelWithoutApiKey()
    {
        var previous = Environment.GetEnvironmentVariable("CONTEXTCORE_TEST_MISSING_API_KEY");

        try
        {
            Environment.SetEnvironmentVariable("CONTEXTCORE_TEST_MISSING_API_KEY", null);
            var options = new ModelGatewayOptions
            {
                Models =
                [
                    new ModelEndpointOptions
                    {
                        Name = "remote-model",
                        Provider = "openai-compatible",
                        Endpoint = "https://example.test/v1",
                        ApiKey = "env:CONTEXTCORE_TEST_MISSING_API_KEY",
                        Enabled = true
                    }
                ]
            };

            var ex = Assert.ThrowsException<InvalidOperationException>(() =>
                ModelGatewayConfigurationValidator.ThrowIfInvalid(options, new ApiKeyResolver()));

            StringAssert.Contains(ex.Message, "ApiKeyRequired");
            StringAssert.Contains(ex.Message, "CONTEXTCORE_TEST_MISSING_API_KEY");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONTEXTCORE_TEST_MISSING_API_KEY", previous);
        }
    }

    [TestMethod]
    public void ModelGatewayConfigurationInspector_ShouldNotLeakPlainApiKey()
    {
        var status = ModelGatewayConfigurationInspector.Inspect(new ModelEndpointOptions
        {
            Name = "private-model",
            Provider = "openai-compatible",
            Endpoint = "https://example.test/v1",
            ApiKey = "top-secret",
            Enabled = true
        });

        Assert.AreEqual("private-model", status.Name);
        Assert.AreEqual("inline", status.ApiKeySource);
        Assert.IsTrue(status.ApiKeyConfigured);
        Assert.IsFalse((status.ConfigurationError ?? string.Empty).Contains("top-secret", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ControlRoomModelStatus_ShouldExposeSafeConfigurationState()
    {
        var service = new ControlRoomService(CreateControlRoomState(CreateTestRootPath()));

        var status = await service.GetModelStatusAsync();

        Assert.AreEqual(1, status.Configuration.Count);
        var model = status.Configuration[0];
        Assert.AreEqual("test-model", model.Name);
        Assert.AreEqual("mock", model.Provider);
        Assert.IsTrue(model.Enabled);
        Assert.IsTrue(model.EndpointConfigured);
        Assert.IsFalse(model.ApiKeyRequired);
        Assert.AreEqual("not-required", model.ApiKeySource);
        Assert.IsNull(model.ApiKeyEnvironmentVariable);
        Assert.IsNull(model.ConfigurationError);
    }

    [TestMethod]
    public async Task ControlRoomModelStatus_ShouldExposeMaterializedModelProfiles()
    {
        var service = new ControlRoomService(CreateControlRoomState(
            CreateTestRootPath(),
            CreateProfileGatewayOptions()));

        var status = await service.GetModelStatusAsync();

        Assert.AreEqual(1, status.Options.ApiProviders.Count);
        Assert.AreEqual(2, status.Options.ModelProfiles.Count);
        Assert.AreEqual(2, status.Options.Models.Count);
        Assert.AreEqual(2, status.Configuration.Count);
        Assert.IsTrue(status.Options.Models.Any(model =>
            model.Name == "audit-model"
            && model.Metadata.TryGetValue("category", out var category)
            && category == "audit"));
        Assert.IsTrue(status.Options.Routes.Any(route =>
            route.PrimaryModelCategory == "audit"
            && route.RequiredCapabilities.Contains("validation")));
    }

    [TestMethod]
    public void EmptyInput_ShouldRefreshDashboard_NotShowUnknownOption()
    {
        var action = ControlRoomInteraction.InterpretDashboardInput("");

        Assert.AreEqual(ControlRoomActionKind.Refresh, action.Kind);
        Assert.AreNotEqual(ControlRoomActionKind.Unknown, action.Kind);
    }

    [TestMethod]
    public void ZeroInShowPrompt_ShouldReturnToDashboard()
    {
        var action = ControlRoomInteraction.InterpretDetailInput("0");

        Assert.AreEqual(ControlRoomActionKind.Back, action.Kind);
    }

    [TestMethod]
    public void Dashboard_ShouldShowCompactRootPath()
    {
        var rootPath = Path.GetFullPath(CreateTestRootPath());
        var dashboard = CreateDashboardSnapshot(rootPath);

        var rendered = DashboardRenderer.RenderToString(dashboard, autoRefresh: false, refreshSeconds: 2, width: 120);
        var compactPath = PathDisplayHelper.Compact(rootPath, 48);

        StringAssert.Contains(rendered, compactPath);
        Assert.IsTrue(rendered.Contains('╔'));
    }

    [TestMethod]
    public void FileStorageOptions_ShouldResolveDefaultRootInsideProjectDataDirectory()
    {
        var rootPath = FileStorageOptions.ResolveRootPath(null);

        Assert.AreEqual(FileStorageOptions.DefaultRootPath, rootPath);
        Assert.AreEqual(FileStorageOptions.DefaultDataDirectoryName, Path.GetFileName(rootPath));
        Assert.IsFalse(rootPath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ControlRoomState_ShouldUseResolvedRootPath()
    {
        var rootPath = Path.Combine(".", "context-core-data-test");

        var state = ControlRoomService.CreateState(
            "filesystem",
            rootPath,
            "workspace-test",
            "collection-test");

        Assert.AreEqual(FileStorageOptions.ResolveRootPath(rootPath), state.RootPath);
    }

    [TestMethod]
    public async Task Dashboard_ShouldShowNoWorkspaceAlert_WhenRootEmpty()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            Directory.CreateDirectory(rootPath);
            var service = new ControlRoomService(CreateControlRoomState(rootPath));

            var dashboard = await service.GetDashboardAsync();
            var rendered = DashboardRenderer.RenderToString(dashboard, autoRefresh: false, refreshSeconds: 2);

            CollectionAssert.Contains(dashboard.Alerts.ToArray(), "当前根目录下没有工作区数据");
            StringAssert.Contains(rendered, "当前根目录下没有工作区数据");
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task Dashboard_ShouldShowRecentCompressionQuality()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            var state = CreateControlRoomState(rootPath);
            var compressor = new MockContextCompressor();
            var input = CreateItem(
                id: "quality-source",
                type: "note",
                content: "Source content for quality dashboard.",
                tags: new[] { "quality" });
            var response = await compressor.CompressAsync(new CompressionRequest
            {
                OperationId = "operation-quality",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Inputs = new[] { input },
                Options = new CompressionOptions
                {
                    GenerateIndexHints = true,
                    PreserveSourceRefs = true
                }
            });
            await state.ContextStore.SaveAsync(response.GeneratedItems.Single());
            var service = new ControlRoomService(state);

            var dashboard = await service.GetDashboardAsync();
            var rendered = DashboardRenderer.RenderToString(dashboard, autoRefresh: false, refreshSeconds: 2, width: 120);
            var report = dashboard.RecentCompressionQuality.Single();

            Assert.AreEqual("operation-quality", report.OperationId);
            Assert.AreEqual(response.QualityReport!.GeneratedItemId, report.GeneratedItemId);
            Assert.IsTrue(report.CompletenessScore > 0);
            StringAssert.Contains(rendered, "压缩质量");
            StringAssert.Contains(rendered, PathDisplayHelper.CompactId("operation-quality-summary", 14));
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task Dashboard_ShouldReadRecentOperations_WhenLogsExist()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            Directory.CreateDirectory(rootPath);
            var eventSink = new FileContextEventSink(Path.Combine(rootPath, "logs"));
            await eventSink.EmitAsync(new ContextOperationEvent
            {
                EventId = "event-1",
                OperationId = "operation-1",
                OperationName = "test.operation",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Level = ContextEventLevel.Information,
                Message = "Operation from test log.",
                Duration = TimeSpan.FromMilliseconds(12),
                CreatedAt = DateTimeOffset.UtcNow
            });
            var service = new ControlRoomService(CreateControlRoomState(rootPath));

            var dashboard = await service.GetDashboardAsync();

            Assert.IsTrue(dashboard.RecentOperations.Any(operation =>
                operation.OperationName == "test.operation"
                && operation.Message == "Operation from test log."));
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    private static ContextItem CreateItem(
        string id,
        string type,
        string content,
        ContextContentFormat format = ContextContentFormat.PlainText,
        IReadOnlyList<string>? tags = null,
        double importance = 0.5,
        Dictionary<string, string>? metadata = null,
        IReadOnlyList<string>? refs = null,
        IReadOnlyList<string>? sourceRefs = null)
    {
        var now = DateTimeOffset.UtcNow;

        return new ContextItem
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = type,
            Title = id,
            Content = content,
            ContentFormat = format,
            Tags = tags ?? Array.Empty<string>(),
            Refs = refs ?? Array.Empty<string>(),
            SourceRefs = sourceRefs ?? new[] { $"source:{id}" },
            Metadata = metadata ?? new Dictionary<string, string>(),
            Importance = importance,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static ContextGlobalItem CreateGlobalItem(
        string id,
        ContextScope scope,
        string? collectionId = "collection-test",
        IReadOnlyList<string>? tags = null)
    {
        var now = DateTimeOffset.UtcNow;

        return new ContextGlobalItem
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = collectionId,
            Scope = scope,
            Type = "global",
            Content = $"Global content for {id}.",
            ContentFormat = ContextContentFormat.PlainText,
            Tags = tags ?? Array.Empty<string>(),
            SourceRefs = new[] { $"source:{id}" },
            Importance = 0.7,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static ModelGatewayOptions CreateModelGatewayOptions(
        bool highRisk,
        bool enableFallback,
        int maxRetryCount = 0,
        bool fallbackOnTimeout = true,
        bool fallbackOnRateLimit = true,
        bool fallbackOnServerError = true,
        bool fallbackOnInvalidJson = true,
        TimeSpan? primaryTimeout = null,
        ModelRole? routeRole = null)
    {
        var role = routeRole ?? (highRisk ? ModelRole.Validator : ModelRole.Router);

        return new ModelGatewayOptions
        {
            Models = new[]
            {
                new ModelEndpointOptions
                {
                    Name = "primary-model",
                    Provider = "mock",
                    Endpoint = "mock://primary",
                    Enabled = true,
                    Timeout = primaryTimeout ?? TimeSpan.FromSeconds(1),
                    Metadata = new Dictionary<string, string>
                    {
                        ["model"] = "primary-model"
                    }
                },
                new ModelEndpointOptions
                {
                    Name = "fallback-model",
                    Provider = "mock",
                    Endpoint = "mock://fallback",
                    Enabled = true,
                    Timeout = TimeSpan.FromSeconds(1),
                    Metadata = new Dictionary<string, string>
                    {
                        ["model"] = "fallback-model"
                    }
                }
            },
            Routes = new[]
            {
                new ModelRoleRoute
                {
                    Role = role,
                    PrimaryModelName = "primary-model",
                    FallbackModelName = "fallback-model",
                    MaxRetryCount = maxRetryCount,
                    EnableFallback = enableFallback,
                    FallbackOnTimeout = fallbackOnTimeout,
                    FallbackOnRateLimit = fallbackOnRateLimit,
                    FallbackOnServerError = fallbackOnServerError,
                    FallbackOnInvalidJson = fallbackOnInvalidJson,
                    HighRiskTask = highRisk
                }
            }
        };
    }

    private static ModelGatewayOptions CreateThinkingModeGatewayOptions()
    {
        return new ModelGatewayOptions
        {
            Models = new[]
            {
                new ModelEndpointOptions
                {
                    Name = "deepseek-chat",
                    Provider = "mock",
                    Endpoint = "mock://deepseek",
                    Enabled = true
                },
                new ModelEndpointOptions
                {
                    Name = "pinai-gpt",
                    Provider = "mock",
                    Endpoint = "mock://pinai",
                    Enabled = true
                }
            },
            Routes = new[]
            {
                new ModelRoleRoute
                {
                    Role = ModelRole.GeneralCompression,
                    ThinkingMode = "fast,balanced",
                    Priority = 10,
                    PrimaryModelName = "deepseek-chat"
                },
                new ModelRoleRoute
                {
                    Role = ModelRole.GeneralCompression,
                    TaskKind = "ExtractKeyPoints",
                    ThinkingMode = "balanced",
                    Priority = 20,
                    PrimaryModelName = "pinai-gpt"
                },
                new ModelRoleRoute
                {
                    Role = ModelRole.GeneralCompression,
                    ThinkingMode = "deep,audit",
                    Priority = 30,
                    PrimaryModelName = "pinai-gpt"
                },
                new ModelRoleRoute
                {
                    Role = ModelRole.Fallback,
                    PrimaryModelName = "deepseek-chat"
                }
            }
        };
    }

    private static ModelGatewayOptions CreateProfileGatewayOptions()
    {
        return new ModelGatewayOptions
        {
            ApiProviders = new[]
            {
                new ModelApiProviderOptions
                {
                    Name = "mock-api",
                    Provider = "mock",
                    Enabled = true
                }
            },
            ModelProfiles = new[]
            {
                new ModelProfileOptions
                {
                    Name = "fast-model",
                    ApiProviderName = "mock-api",
                    Model = "fast-model",
                    Category = "fast",
                    Capabilities = new[] { "compression", "routing" },
                    Roles = new[] { "GeneralCompression" },
                    ThinkingModes = new[] { "fast" },
                    Metadata = new Dictionary<string, string>
                    {
                        ["priority"] = "10"
                    }
                },
                new ModelProfileOptions
                {
                    Name = "audit-model",
                    ApiProviderName = "mock-api",
                    Model = "audit-model",
                    Category = "audit",
                    Capabilities = new[] { "compression", "audit", "validation" },
                    Roles = new[] { "GeneralCompression", "Validator" },
                    TaskKinds = new[] { "Validate" },
                    ThinkingModes = new[] { "audit" },
                    Metadata = new Dictionary<string, string>
                    {
                        ["priority"] = "50"
                    }
                }
            },
            Routes = new[]
            {
                new ModelRoleRoute
                {
                    Role = ModelRole.GeneralCompression,
                    ThinkingMode = "audit",
                    PrimaryModelCategory = "audit",
                    RequiredCapabilities = new[] { "audit", "validation" },
                    Priority = 100
                },
                new ModelRoleRoute
                {
                    Role = ModelRole.GeneralCompression,
                    PrimaryModelCategory = "fast",
                    RequiredCapabilities = new[] { "compression" },
                    Priority = 1
                }
            }
        };
    }

    private static ModelRequest CreateModelRoutePreviewRequest(
        string? thinkingMode,
        string taskKind,
        ModelRole role = ModelRole.GeneralCompression)
    {
        var metadata = new Dictionary<string, string>
        {
            ["compressionTask"] = taskKind
        };
        if (!string.IsNullOrWhiteSpace(thinkingMode))
        {
            metadata["thinkingMode"] = thinkingMode;
        }

        return new ModelRequest
        {
            Role = role,
            Prompt = "预览模型路由。",
            Metadata = metadata
        };
    }

    private static ControlRoomState CreateControlRoomState(
        string rootPath,
        ModelGatewayOptions? modelOptionsOverride = null)
    {
        var contextStore = new InMemoryContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var relationStore = new InMemoryRelationStore();
        var constraintStore = new InMemoryConstraintStore();
        var globalStore = new InMemoryGlobalContextStore();
        var jobQueue = new InMemoryJobQueue();
        var modelOptions = modelOptionsOverride ?? new ModelGatewayOptions
        {
            Models = new[]
            {
                new ModelEndpointOptions
                {
                    Name = "test-model",
                    Provider = "mock",
                    Endpoint = "mock://test",
                    Enabled = true
                }
            },
            Routes = Array.Empty<ModelRoleRoute>()
        };

        return new ControlRoomState
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            StorageKind = "filesystem",
            RootPath = Path.GetFullPath(rootPath),
            ContextStore = contextStore,
            Index = new InMemoryContextIndex(),
            MemoryStore = memoryStore,
            WorkingMemory = memoryStore,
            ConstraintStore = constraintStore,
            RelationStore = relationStore,
            GlobalContextStore = globalStore,
            JobQueue = jobQueue,
            JobQueryStore = jobQueue,
            PromotionService = new BasicMemoryPromotionService(memoryStore, memoryStore),
            PackageBuilder = new BasicContextPackageBuilder(
                contextStore,
                constraintStore,
                globalStore,
                memoryStore,
                relationStore),
            PackagePolicyStore = new InMemoryContextPackagePolicyStore(),
            ModelGatewayOptions = modelOptions,
            ModelHealthService = new TestModelHealthService(ModelAvailability.Available),
            ModelUsageLogStore = new InMemoryModelUsageLogStore()
        };
    }

    private static DashboardSnapshot CreateDashboardSnapshot(string rootPath)
    {
        return new DashboardSnapshot
        {
            CurrentTime = DateTimeOffset.UtcNow,
            StorageKind = "filesystem",
            RootPath = rootPath,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            WorkspaceDataFound = true,
            Health =
            [
                new SystemHealthItem
                {
                    Name = "storage",
                    Status = "ok",
                    Detail = rootPath
                }
            ],
            Memory = new MemoryLayerSummary(),
            Jobs = new JobsSummary(),
            Alerts = []
        };
    }

    private static ContextRelation CreateRelation(
        string id,
        string sourceId,
        string targetId,
        string relationType)
    {
        return new ContextRelation
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = sourceId,
            TargetId = targetId,
            RelationType = relationType,
            Weight = 1.0,
            Confidence = 0.9,
            SourceRefs = new[] { sourceId, targetId },
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static async Task AssertSaveManyAndQueryForItemAsync(IRelationStore store)
    {
        await store.SaveManyAsync(new[]
        {
            CreateRelation(
                id: "outgoing",
                sourceId: "source-1",
                targetId: "target-1",
                relationType: ContextRelationTypes.RelatedTo),
            CreateRelation(
                id: "incoming",
                sourceId: "source-2",
                targetId: "source-1",
                relationType: ContextRelationTypes.DependsOn),
            CreateRelation(
                id: "unrelated",
                sourceId: "source-3",
                targetId: "target-3",
                relationType: ContextRelationTypes.RelatedTo)
        });

        var results = await store.QueryForItemAsync("workspace-test", "collection-test", "source-1");

        Assert.AreEqual(2, results.Count);
        Assert.IsTrue(results.Any(item => item.Id == "outgoing"));
        Assert.IsTrue(results.Any(item => item.Id == "incoming"));
        Assert.IsFalse(results.Any(item => item.Id == "unrelated"));
    }

    private sealed class DuplicateContextStore : IContextStore
    {
        private readonly IReadOnlyList<ContextItem> _items;

        public DuplicateContextStore(IReadOnlyList<ContextItem> items)
        {
            _items = items;
        }

        public Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<ContextItem?> GetAsync(
            string workspaceId,
            string collectionId,
            string id,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(_items.FirstOrDefault(item =>
                string.Equals(item.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)));
        }

        public Task<IReadOnlyList<ContextItem>> QueryAsync(
            ContextQuery query,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var results = _items
                .Where(item => string.Equals(item.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase))
                .Where(item => string.IsNullOrWhiteSpace(query.CollectionId)
                    || string.Equals(item.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            return Task.FromResult<IReadOnlyList<ContextItem>>(results);
        }

        public Task DeleteAsync(
            string workspaceId,
            string collectionId,
            string id,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private static ContextConstraint CreateConstraint(
        string id,
        ConstraintLevel level,
        string content,
        string? collectionId = "collection-test",
        ContextScope scope = ContextScope.Collection,
        IReadOnlyList<string>? appliesToRefs = null,
        IReadOnlyList<string>? sourceRefs = null,
        ContextMemoryStatus status = ContextMemoryStatus.Verified,
        double confidence = 1.0)
    {
        var now = DateTimeOffset.UtcNow;

        return new ContextConstraint
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = collectionId,
            Scope = scope,
            Level = level,
            Content = content,
            AppliesToRefs = appliesToRefs ?? Array.Empty<string>(),
            SourceRefs = sourceRefs ?? new[] { $"source:{id}" },
            Status = status,
            Confidence = confidence,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static ContextMemoryItem CreateMemoryItem(
        string id,
        ContextMemoryLayer layer,
        DateTimeOffset updatedAt,
        ContextMemoryStatus status = ContextMemoryStatus.Verified,
        IReadOnlyList<string>? relationRefs = null)
    {
        return new ContextMemoryItem
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = layer,
            Status = status,
            Type = "memory",
            Content = $"Memory content for {id}.",
            ContentFormat = ContextContentFormat.PlainText,
            Tags = new[] { "memory" },
            SourceRefs = new[] { $"source:{id}" },
            RelationRefs = relationRefs ?? new[] { $"relation:{id}" },
            Importance = 0.5,
            Confidence = 0.8,
            Version = 1,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt
        };
    }

    private static WorkingMemoryItem CreateWorkingMemoryItem(
        string id,
        DateTimeOffset updatedAt,
        IReadOnlyList<string>? relationRefs = null)
    {
        return new WorkingMemoryItem
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "memory",
            Content = $"Working memory content for {id}.",
            ContentFormat = ContextContentFormat.PlainText,
            Tags = new[] { "working" },
            SourceRefs = new[] { $"source:{id}" },
            RelationRefs = relationRefs ?? new[] { $"relation:{id}" },
            Importance = 0.5,
            Confidence = 0.8,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt
        };
    }

    private static string CreateTestRootPath()
    {
        return Path.Combine(
            Directory.GetCurrentDirectory(),
            "context-core-test-data",
            Guid.NewGuid().ToString("N"));
    }

    private static void DeleteTestRoot(string rootPath)
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private sealed class CaptureHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        private CaptureHttpMessageHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        public int CallCount { get; private set; }

        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string RequestBody { get; private set; } = string.Empty;

        public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; private set; }

        public static CaptureHttpMessageHandler Json(
            string json,
            HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            return new CaptureHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
                ReasonPhrase = statusCode.ToString()
            }));
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return await _handler(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class RecordingModelGateway : IModelGateway
    {
        private readonly Func<ModelRequest, CancellationToken, Task<ModelResponse>> _handler;

        private RecordingModelGateway(
            Func<ModelRequest, CancellationToken, Task<ModelResponse>> handler)
        {
            _handler = handler;
        }

        public int CallCount { get; private set; }

        public ModelRequest? LastRequest { get; private set; }

        public static RecordingModelGateway Success(
            string content,
            int inputTokens = 0,
            int outputTokens = 0)
        {
            return new RecordingModelGateway((request, _) => Task.FromResult(new ModelResponse
            {
                OperationId = request.OperationId,
                Content = content,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                Succeeded = true,
                Metadata = new Dictionary<string, string>
                {
                    ["modelName"] = "test-compressor-model",
                    ["provider"] = "mock"
                }
            }));
        }

        public Task<ModelResponse> CompleteAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            return _handler(request, cancellationToken);
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

        public int CallCount { get; private set; }

        public Task<ModelResponse> CompleteAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return _handler(request, cancellationToken);
        }

        public static TestModelAdapter Success(string name, string content)
        {
            return new TestModelAdapter(name, (request, _) => Task.FromResult(new ModelResponse
            {
                OperationId = request.OperationId,
                Content = content,
                InputTokens = 3,
                OutputTokens = 2,
                Succeeded = true
            }));
        }

        public static TestModelAdapter Failure(string name, string errorMessage)
        {
            return new TestModelAdapter(name, (request, _) => Task.FromResult(new ModelResponse
            {
                OperationId = request.OperationId,
                Content = string.Empty,
                Succeeded = false,
                ErrorMessage = errorMessage,
                Metadata = new Dictionary<string, string>
                {
                    ["failureReason"] = "server_error"
                }
            }));
        }

        public static TestModelAdapter Sequence(
            string name,
            params Func<ModelRequest, ModelResponse>[] steps)
        {
            if (steps.Length == 0)
            {
                throw new ArgumentException("At least one response step is required.", nameof(steps));
            }

            var index = 0;
            return new TestModelAdapter(name, (request, _) =>
            {
                var step = steps[Math.Min(index, steps.Length - 1)];
                index++;

                return Task.FromResult(step(request));
            });
        }

        public static TestModelAdapter DelayedSuccess(
            string name,
            TimeSpan delay,
            string content)
        {
            return new TestModelAdapter(name, async (request, cancellationToken) =>
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                return new ModelResponse
                {
                    OperationId = request.OperationId,
                    Content = content,
                    InputTokens = 3,
                    OutputTokens = 2,
                    Succeeded = true
                };
            });
        }

        public static TestModelAdapter Throws(string name, Exception exception)
        {
            return new TestModelAdapter(name, (_, _) => Task.FromException<ModelResponse>(exception));
        }
    }

    private sealed class TestModelHealthService : IModelHealthService
    {
        private readonly ModelAvailability _availability;

        public TestModelHealthService(ModelAvailability availability)
        {
            _availability = availability;
        }

        public Task<ModelHealthResult> CheckAsync(
            string modelName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(new ModelHealthResult
            {
                ModelName = modelName,
                Availability = _availability,
                LatencyMs = 1,
                LastError = _availability == ModelAvailability.Available ? null : "unavailable",
                CheckedAt = DateTimeOffset.UtcNow
            });
        }
    }
}
