using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Evaluation.Contracts;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.ControlRoom.Services;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Planning;
using ContextCore.Core.Services.Storage;
using ContextCore.Embedding;
using ContextCore.Embedding.Providers;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.Evaluation.Commands;

public static partial class EvalCommand
{
private static async Task ExecuteStorageBoundaryReportAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("storage", "storage-boundary-report.json");
        var markdownPath = CommandHelpers.GetOption(args, "--md-out")
            ?? Path.Combine("storage", "storage-boundary-report.md");
        var report = StorageResponsibilityRegistry.BuildReport();

        await WriteTextAsync(JsonSerializer.Serialize(report, JsonOptions), outputPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(StorageResponsibilityRegistry.BuildMarkdownReport(report), markdownPath, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[StorageBoundary] JSON: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[StorageBoundary] Markdown: {Path.GetFullPath(markdownPath)}");
        Console.WriteLine($"[StorageBoundary] DatabaseRecommended={report.DatabaseRecommendedCount}, MigrationCandidates={report.MigrationCandidates.Count}");
    }

private static async Task ExecuteStorageCheckAsync(
        IEvalHost service,
        CancellationToken cancellationToken)
    {
        var state = service.State;
        const string ProbeWs = "__readiness_probe__";
        const string ProbeColl = "__probe__";
        var probeId = $"probe-{DateTimeOffset.UtcNow.Ticks}";

        Console.WriteLine("\n========================================================");
        Console.WriteLine("          A0 §2.4  存储可读写深度检查");
        Console.WriteLine("========================================================");
        Console.WriteLine($"  存储类型 : {state.StorageKind}");
        Console.WriteLine($"  探针 ID  : {probeId}");
        Console.WriteLine();

        var now = DateTimeOffset.UtcNow;
        var results = new List<StorageCheckResult>
        {
            // 1. IContextStore
            await RunStorageCheckAsync("context-store", cancellationToken, async token =>
            {
                var item = new ContextItem
                {
                    Id = probeId,
                    WorkspaceId = ProbeWs,
                    CollectionId = ProbeColl,
                    Type = "readiness-probe",
                    Content = "readiness probe — safe to delete",
                    CreatedAt = now
                };
                await state.ContextStore.SaveAsync(item, token);
                var readBack = await state.ContextStore.GetAsync(ProbeWs, ProbeColl, probeId, token);
                await state.ContextStore.DeleteAsync(ProbeWs, ProbeColl, probeId, token);
                if (readBack is null || readBack.Id != probeId)
                    throw new InvalidOperationException($"读回 ID 不匹配：expected={probeId}");
                return "写入→读取→删除 成功";
            }),

            // 2. IMemoryStore
            await RunStorageCheckAsync("memory-store", cancellationToken, async token =>
            {
                var item = new ContextMemoryItem
                {
                    Id = probeId,
                    WorkspaceId = ProbeWs,
                    CollectionId = ProbeColl,
                    Type = "readiness-probe",
                    Content = "readiness probe — safe to delete",
                    Layer = ContextMemoryLayer.Working,
                    Status = ContextMemoryStatus.Candidate,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                await state.MemoryStore.SaveAsync(item, token);
                var readBack = await state.MemoryStore.GetAsync(ProbeWs, ProbeColl, probeId, token);
                if (readBack is null || readBack.Id != probeId)
                    throw new InvalidOperationException($"读回 ID 不匹配：expected={probeId}");
                return "写入→读取 成功（接口无 DeleteAsync）";
            }),

            // 3. IRelationStore
            await RunStorageCheckAsync("relation-store", cancellationToken, async token =>
            {
                var relation = new ContextRelation
                {
                    Id = probeId,
                    WorkspaceId = ProbeWs,
                    CollectionId = ProbeColl,
                    SourceId = probeId,
                    TargetId = probeId,
                    RelationType = "readiness-probe",
                    CreatedAt = now
                };
                await state.RelationStore.SaveAsync(relation, token);
                var readBack = await state.RelationStore.QueryAsync(new ContextRelationQuery { WorkspaceId = ProbeWs, CollectionId = ProbeColl, SourceId = probeId, Take = int.MaxValue }, token);
                if (!readBack.Any(r => r.Id == probeId))
                    throw new InvalidOperationException("写入成功但 QueryAsync 找不到探针关系");
                return "写入→QueryBySource 成功（接口无 DeleteAsync）";
            }),

            // 4. IConstraintStore
            await RunStorageCheckAsync("constraint-store", cancellationToken, async token =>
            {
                var constraint = new ContextConstraint
                {
                    Id = probeId,
                    WorkspaceId = ProbeWs,
                    CollectionId = ProbeColl,
                    Content = "readiness probe — safe to delete",
                    Level = ConstraintLevel.Soft,
                    Scope = ContextScope.Collection,
                    Status = ContextMemoryStatus.Candidate,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                await state.ConstraintStore.SaveAsync(constraint, token);
                var readBack = await state.ConstraintStore.QueryAsync(new ContextConstraintQuery
                {
                    WorkspaceId = ProbeWs,
                    CollectionId = ProbeColl,
                    Take = 100
                }, token);
                if (!readBack.Any(c => c.Id == probeId))
                    throw new InvalidOperationException("写入成功但 QueryAsync 找不到探针约束");
                return "写入→QueryAsync 成功（接口无 DeleteAsync）";
            }),

            // 5. IContextJobQueue
            await RunStorageCheckAsync("job-queue", cancellationToken, async token =>
            {
                var job = new ContextJob
                {
                    JobId = probeId,
                    WorkspaceId = ProbeWs,
                    CollectionId = ProbeColl,
                    Kind = ContextJobKind.Custom,
                    PayloadJson = "{}",
                    State = ContextJobState.Queued,
                    CreatedAt = now
                };
                await state.JobQueue.EnqueueAsync(job, token);
                var queued = await state.JobQueryStore.QueryAsync(new ContextJobQuery
                {
                    WorkspaceId = ProbeWs,
                    State = ContextJobState.Queued,
                    Take = 100
                }, token);
                if (!queued.Any(j => j.JobId == probeId))
                    throw new InvalidOperationException("入队成功但 QueryAsync 找不到探针作业");
                return "入队→QueryAsync 成功（探针作业将由处理器 Nack 或手动清理）";
            }),

            // 6. IRetrievalTraceStore
            await RunStorageCheckAsync("retrieval-trace", cancellationToken, async token =>
            {
                var trace = new ContextRetrievalTrace
                {
                    RetrievalId = probeId,
                    WorkspaceId = ProbeWs,
                    CollectionId = ProbeColl,
                    QueryText = "readiness probe",
                    CreatedAt = now
                };
                await state.RetrievalTraceStore.SaveAsync(trace, token);
                var readBack = await state.RetrievalTraceStore.QueryRecentAsync(ProbeWs, ProbeColl, 100, token);
                if (!readBack.Any(t => t.RetrievalId == probeId))
                    throw new InvalidOperationException("写入成功但 QueryRecentAsync 找不到探针 trace");
                return "写入→QueryRecent 成功（接口无 DeleteAsync）";
            })
        };

        // 打印结果表格
        int passed = 0, failed = 0;
        Console.WriteLine($"  {"存储",-22} {"状态",-8} {"耗时",7}  说明");
        Console.WriteLine($"  {new string('-', 72)}");
        foreach (var r in results)
        {
            var icon = r.Ok ? "✅" : "❌";
            Console.WriteLine($"  {icon} {r.Name,-20} {r.Status,-8} {r.ElapsedMs,5} ms  {r.Message}");
            if (r.Ok) passed++; else failed++;
        }

        Console.WriteLine();
        Console.WriteLine($"  结论: {passed}/{results.Count} 通过 — {(failed == 0 ? "所有存储可读写 ✅" : $"{failed} 项失败 ❌")}");
        Console.WriteLine("========================================================");
    }
}
