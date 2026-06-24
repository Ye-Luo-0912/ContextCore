using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.ControlRoom.Commands;
using ContextCore.ControlRoom.Services;
using ContextCore.Storage.InMemory;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

[TestClass]
public sealed class ContextCoreFallbackReportTests
{
    [TestMethod]
    public void QualityEvaluator_ShouldAddFallbackUsedSignal_WhenFallbackUsed()
    {
        var request = new CompressionRequest
        {
            OperationId = "op-test-fallback",
            WorkspaceId = "ws-1",
            CollectionId = "col-1",
            Inputs = new[]
            {
                new ContextItem { Id = "item-1", Content = "Test input content for fallback.", Version = 1 }
            }
        };

        var responseWithFallback = new CompressionResponse
        {
            OperationId = "op-test-fallback",
            Status = CompressionStatus.RequiresReview,
            GeneratedItems = new[]
            {
                new ContextItem { Id = "op-test-fallback-summary", Content = "Test summary.", Metadata = new Dictionary<string, string>() }
            },
            Warnings = new[]
            {
                new ContextWarning { Code = "FallbackUsedWarning", Message = "Fallback model used." }
            },
            Usage = new ContextOperationUsage { InputTokens = 10, OutputTokens = 2 }
        };

        var evaluator = new CompressionQualityEvaluator();
        var report = evaluator.Evaluate(request, responseWithFallback);

        Assert.IsNotNull(report);
        Assert.IsTrue(report.Signals.Contains("fallback-used"));
    }

    [TestMethod]
    public async Task ModelCommand_FallbackReport_ShouldExecuteSuccessfully()
    {
        // 1. Create in-memory dependencies
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

        var promotionService = new BasicMemoryPromotionService(memoryStore, memoryStore);

        // 2. Initialize ControlRoomState
        var state = new ControlRoomState
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            StorageKind = "memory",
            RootPath = "memory://",
            ContextStore = contextStore,
            MemoryStore = memoryStore,
            RelationStore = relationStore,
            ConstraintStore = constraintStore,
            GlobalContextStore = globalStore,
            PromotionCandidateStore = memoryStore,
            WorkingMemory = memoryStore,
            JobQueryStore = new InMemoryJobQueue(),
            PromotionService = promotionService,
            ModelGatewayOptions = new ModelGatewayOptions(),
            ModelHealthService = new InMemoryModelHealthService(),
            ModelUsageLogStore = new InMemoryModelUsageLogStore()
        };

        var service = new ControlRoomService(state);

        // 3. Ingest some summary items, one with fallback-used signal and one without
        var primarySummary = new ContextItem
        {
            Id = "summary-primary",
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            Type = "summary",
            Content = "Primary summary content.",
            Metadata = new Dictionary<string, string>
            {
                ["quality.completenessScore"] = "0.9",
                ["quality.consistencyScore"] = "0.95",
                ["quality.usabilityScore"] = "0.85",
                ["quality.compressionRatio"] = "0.2",
                ["quality.riskScore"] = "0.1",
                ["quality.requiresReview"] = "false",
                ["quality.inputTokens"] = "100",
                ["quality.outputTokens"] = "20",
                ["quality.status"] = "Succeeded",
                ["quality.signals"] = "status:Succeeded,source-coverage:1,key-term-coverage:0.8",
                ["quality.createdAt"] = DateTimeOffset.UtcNow.ToString("O")
            }
        };

        var fallbackSummary = new ContextItem
        {
            Id = "summary-fallback",
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            Type = "summary",
            Content = "Fallback summary content.",
            Metadata = new Dictionary<string, string>
            {
                ["quality.completenessScore"] = "0.7",
                ["quality.consistencyScore"] = "0.6",
                ["quality.usabilityScore"] = "0.65",
                ["quality.compressionRatio"] = "0.25",
                ["quality.riskScore"] = "0.55",
                ["quality.requiresReview"] = "true",
                ["quality.inputTokens"] = "100",
                ["quality.outputTokens"] = "25",
                ["quality.status"] = "RequiresReview",
                ["quality.signals"] = "status:RequiresReview,source-coverage:0.8,fallback-used,warnings",
                ["quality.createdAt"] = DateTimeOffset.UtcNow.ToString("O")
            }
        };

        await contextStore.SaveAsync(primarySummary);
        await contextStore.SaveAsync(fallbackSummary);

        // 4. Capture console output
        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);

        try
        {
            // 5. Execute subcommand
            await ModelCommand.ExecuteAsync(service, new[] { "fallback-report" });
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = sw.ToString();

        // 6. Assertions
        StringAssert.Contains(output, "压缩源数据分类统计");
        StringAssert.Contains(output, "回退兜底与主模型压缩质量对比");
        StringAssert.Contains(output, "主模型 (无回退)");
        StringAssert.Contains(output, "回退兜底模型");
        StringAssert.Contains(output, "质量分析与建议");
    }

    private sealed class InMemoryModelHealthService : IModelHealthService
    {
        public Task<ModelHealthResult> CheckAsync(string modelName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ModelHealthResult
            {
                ModelName = modelName,
                Availability = ModelAvailability.Available,
                LatencyMs = 15,
                CheckedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private sealed class InMemoryModelUsageLogStore : IModelUsageLogStore
    {
        private readonly List<ModelUsageLog> _logs = new();
        public Task SaveAsync(ModelUsageLog log, CancellationToken cancellationToken = default)
        {
            _logs.Add(log);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ModelUsageLog>> QueryRecentAsync(int take, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ModelUsageLog>>(_logs);
        }
    }
}
