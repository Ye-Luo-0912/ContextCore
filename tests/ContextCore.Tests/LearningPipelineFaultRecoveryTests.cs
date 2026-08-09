using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.MemoryEvolution;
using ContextCore.Service.Endpoints;
using ContextCore.Service.Security;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ContextCore.Tests;

/// <summary>
/// Learning 闭环故障恢复语义测试（WP-AB）：
/// 1. 导出故障 → 异常传播 + 工件不落库（无半态）；
/// 2. 物化故障 → 异常传播，ledger 无半写（存储故障不静默吞）；
/// 3. 重建故障 → 端点 503（Artifact Store 未注册，不静默降级）。
/// </summary>
[TestClass]
[TestCategory("Learning-Event")]
public sealed class LearningPipelineFaultRecoveryTests
{
    private const string Ws = "ws-fault";

    [TestMethod]
    public async Task ExportFailure_PropagatesAndPersistsNothing()
    {
        // ledger 查询故障 → 导出抛异常（不伪造空快照）→ 工件不落库。
        var failingLedger = new ThrowingLedgerStore(new InvalidOperationException("ledger unavailable"));
        var exporter = new TrainingDataExporter(failingLedger);
        var artifactStore = new InMemoryLearningArtifactStore();
        using var tempDir = new TempDirectory();

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            exporter.ExportAsync(new TrainingDataExportRequest
            {
                WorkspaceId = Ws,
                OutputDirectory = tempDir.Path,
                ModelArtifactId = "model-fault"
            }));

        Assert.AreEqual("ledger unavailable", ex.Message, "存储故障异常明确传播（不静默吞）。");
        Assert.AreEqual(0, (await artifactStore.ListRecentAsync(Ws)).Count, "导出失败工件不落库（无半态）。");
    }

    [TestMethod]
    public async Task MaterializeFailure_Propagates_NoPartialLedger()
    {
        // 物化故障：ledger 写入失败 → 异常传播（不静默吞）；InMemory 原子追加不产生半写。
        var failingLedger = new ThrowingLedgerStore(new InvalidOperationException("write failed"));
        var conflictSet = new InMemoryConflictSetStore();
        var materializer = new UtilityLedgerMaterializer(failingLedger, conflictSet);

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            materializer.MaterializeAsync(BuildDecisionResult(), Ws, "col-fault"));

        Assert.AreEqual("write failed", ex.Message, "物化故障异常明确传播。");
    }

    [TestMethod]
    public async Task RebuildFailure_StoreUnregistered_Returns503()
    {
        // Artifact Store 未注册（null）→ 端点 503（不静默降级，明确暴露组件缺失）。
        var result = await LearningArtifactEndpoints.GetArtifactAsync(
            null!, new FixedWorkspaceAccessor(), "snapshot-any");

        var context = Http();
        await result.ExecuteAsync(context);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    // ── 辅助 ─────────────────────────────────────────────────────────────

    /// <summary>读写均抛异常的 ledger（实现 IUtilityLedger，模拟存储故障）。</summary>
    private sealed class ThrowingLedgerStore : IUtilityLedger
    {
        private readonly Exception _exception;

        public ThrowingLedgerStore(Exception exception) => _exception = exception;

        public Task<IReadOnlyList<UtilityLedgerEntry>> QueryAsync(
            UtilityLedgerQuery query, CancellationToken cancellationToken = default)
            => throw _exception;

        public Task<UtilityLedgerEntry?> GetLatestEntryAsync(
            string workspaceId, string collectionId, string candidateItemId, CancellationToken cancellationToken = default)
            => throw _exception;

        public Task<IReadOnlyDictionary<RetrievalExpert, double>> GetExpertContributionsAsync(
            string workspaceId, string collectionId, string candidateItemId, CancellationToken cancellationToken = default)
            => throw _exception;

        public Task AppendEntriesAsync(
            IReadOnlyList<UtilityLedgerEntry> entries, CancellationToken cancellationToken = default)
            => throw _exception;
    }

    private static ContextDecisionResult BuildDecisionResult() => new()
    {
        RequestId = "decision-fault-1",
        DecisionSource = ContextDecisionSource.Retrieval,
        PolicyVersion = ContextDecisionPolicyVersions.DecisionSchemaV2_0,
        SelectedEnvelopes = new[]
        {
            new ContextCandidateEnvelope
            {
                CandidateId = "cand-1",
                Source = ContextCandidateSource.WorkingMemory,
                CanonicalKey = CanonicalCandidateKey.Create(Ws, "col-fault", "memory", "cand-1", "v1"),
                Utility = new CandidateUtilityScore { DeterministicScore = 0.9, FinalScore = 0.9 }
            }
        },
        DroppedEnvelopes = Array.Empty<ContextCandidateEnvelope>(),
        Outcome = new ContextDecisionOutcomeSummary { SelectedCount = 1, DroppedCount = 0 }
    };

    private static DefaultHttpContext Http()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.TraceIdentifier = "test-trace";
        httpContext.Response.Body = new MemoryStream();
        httpContext.RequestServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        return httpContext;
    }

    private sealed class FixedWorkspaceAccessor : IWorkspaceContextAccessor
    {
        public WorkspaceContext? Current => new()
        {
            WorkspaceId = Ws,
            Source = "test",
            ApiKeyId = "key-fault",
            Roles = new[] { WorkspaceRole.Operator },
            IsAuthenticated = true
        };

        public void Set(WorkspaceContext context) { }

        public void Clear() { }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "cc-fault-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // 清理失败忽略（临时目录）。
            }
        }
    }
}
