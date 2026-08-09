using ContextCore.Abstractions;
using ContextCore.Service.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ContextCore.Tests;

/// <summary>
/// Runtime Diagnostics 端点测试（WP-N）：Learning 物化积压 / 后台负载预算诊断；
/// Schema 诊断依赖 PostgresMigrationRunner（未注册时保持 null，端点不失败）。
/// </summary>
[TestClass]
[TestCategory("Decision")]
public sealed class DiagnosticsEndpointsTests
{
    [TestMethod]
    public async Task RuntimeDiagnostics_ReportsLearningBacklogAndDrainBudget()
    {
        var outbox = new FakeLearningOutbox(pending: 3, processing: 1, deadLetter: 2);
        var result = await DiagnosticsEndpoints.GetRuntimeDiagnosticsAsync(
            migrationRunner: null, learningOutbox: outbox);

        var (status, report) = await ExecuteAsync<RuntimeDiagnosticsReport>(result);

        Assert.AreEqual(StatusCodes.Status200OK, status);
        Assert.IsNotNull(report);
        Assert.IsNull(report!.Schema, "未注册迁移 runner 时 Schema 诊断保持 null（不失败）。");
        Assert.AreEqual(3, report.Learning!.PendingEvents, "Learning 物化 pending 积压。");
        Assert.AreEqual(1, report.Learning.ProcessingEvents);
        Assert.AreEqual(2, report.Learning.DeadLetterEvents);
        Assert.IsNotNull(report.Background);
        Assert.AreEqual(8, report.Background!.DrainBudget!.MaxBatchesPerBurst, "后台负载预算配置可观测。");
        Assert.AreEqual(200, report.Background.DrainBudget.MaxBurstDurationMs);
    }

    [TestMethod]
    public async Task RuntimeDiagnostics_NoLearningOutbox_ReportsBackgroundOnly()
    {
        var result = await DiagnosticsEndpoints.GetRuntimeDiagnosticsAsync(
            migrationRunner: null, learningOutbox: null);

        var (status, report) = await ExecuteAsync<RuntimeDiagnosticsReport>(result);

        Assert.AreEqual(StatusCodes.Status200OK, status);
        Assert.IsNull(report!.Learning, "未注册 Learning outbox 时积压诊断为 null。");
        Assert.IsNotNull(report.Background, "后台预算诊断始终存在。");
    }

    // ── 辅助 ─────────────────────────────────────────────────────────────

    private static DefaultHttpContext Http()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.TraceIdentifier = "test-trace";
        httpContext.Response.Body = new MemoryStream();
        httpContext.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        return httpContext;
    }

    private static async Task<(int Status, T? Body)> ExecuteAsync<T>(IResult result) where T : class
    {
        var context = Http();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var body = await System.Text.Json.JsonSerializer.DeserializeAsync<T>(
            context.Response.Body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        return (context.Response.StatusCode, body);
    }

    private sealed class FakeLearningOutbox : ILearningEventOutboxStore
    {
        private readonly int _pending;
        private readonly int _processing;
        private readonly int _deadLetter;

        public FakeLearningOutbox(int pending, int processing, int deadLetter)
        {
            _pending = pending;
            _processing = processing;
            _deadLetter = deadLetter;
        }

        public Task EnqueueAsync(
            LearningEventOutboxRecord record, IWriteTransactionScope? scope = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<LearningEventOutboxRecord>> AcquirePendingAsync(
            int limit, string owner, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<LearningEventOutboxRecord>>(Array.Empty<LearningEventOutboxRecord>());

        public Task<bool> MarkAckedAsync(string eventId, string leaseToken, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> MarkFailedAsync(string eventId, string leaseToken, string errorMessage, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(
            string eventId, string leaseToken, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<IReadOnlyDictionary<string, DateTimeOffset>> RenewLeaseBatchAsync(
            IReadOnlyList<(string EventId, string LeaseToken)> leases, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>>(new Dictionary<string, DateTimeOffset>());

        public Task<IReadOnlyDictionary<string, int>> CountByStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>
            {
                [LearningEventOutboxStates.Pending] = _pending,
                [LearningEventOutboxStates.Processing] = _processing,
                [LearningEventOutboxStates.DeadLettered] = _deadLetter
            });

        public Task<DateTimeOffset?> GetLastSuccessAtAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow);
    }
}
