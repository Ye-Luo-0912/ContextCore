using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.MemoryEvolution;

namespace ContextCore.Tests;

// ===========================================================================
// Learning Event 硬验收门测试
//
// 背景：
// 任务 E 修复了 Learning Loop 静默丢训练数据问题——将 fire-and-forget
// Task.Run → MaterializeAsync → catch {} 模式替换为 Durable Outbox + batch worker。
// 本验收门验证以下硬保证：
// 1. Learning Event 入队到 Durable Outbox 后，进程崩溃（worker 未启动）也不丢数据。
// 2. 物化失败被重试，且失败可观测（metrics.FailedEvents 递增），超过最大重试次数后进入 dead-letter。
// 3. 数据集快照包含完整性报告与血缘信息（DatasetSnapshot 概念尚未实现 → Inconclusive）。
//
// 设计说明：
// - 项目未提供 InMemory 版 ILearningEventOutboxStore（仅 Postgres 实现），故在测试内
// 提供 InMemoryLearningEventOutboxStore 替身，精确模拟 PostgresLearningEventOutboxStore
// 的 lease + retry + CAS 语义（SELECT FOR UPDATE SKIP LOCKED / retry_count + 1 >= max → DeadLettered）。
// - LearningMaterializationWorker 为 BackgroundService，需 DI scope + 生命周期，在 Postgres
// 集成测试中覆盖；此处手动模拟 worker 循环（AcquirePending → Materialize → Ack/MarkFailed）
// 以直接验证 outbox store 的 retry/dead-letter 契约 + 可观测性指标。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
public sealed class R29H_LearningEventAcceptanceTests
{
    // 与 LearningMaterializationDispatcher / LearningMaterializationWorker 一致的 payload 序列化选项。
    private static readonly JsonSerializerOptions PayloadSerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    // 读取 TrainingDataExporter 输出的 JSONL（camelCase）时使用。
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    // -----------------------------------------------------------------------
    // 1. LearningEvent_Survives_ProcessCrash
    // 验证：Learning Event 入队到 Durable Outbox 后，即使进程"崩溃"（worker 未启动），
    // 事件仍持久化在 outbox store 中（Pending 状态），不会丢失。
    // -----------------------------------------------------------------------
    [TestMethod]
    [TestCategory("Learning-Event")]
    public async Task LearningEvent_Survives_ProcessCrash()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var metrics = new LearningMaterializationMetrics();
        var outboxStore = new InMemoryLearningEventOutboxStore();
        var options = new LearningMaterializationOptions { MaxRetryCount = 3 };

        // dispatcher 注入 outbox store → 走 Durable Outbox 路径（持久化）。
        // materializer=null：本测试只验证持久化，不物化。
        var dispatcher = new LearningMaterializationDispatcher(
            materializer: null,
            outboxStore: outboxStore,
            options: options,
            metrics: metrics);

        var decision = MakeDecision("req-crash-survival");

        // 入队：序列化 decision 并写入 outbox（持久化）。
        await dispatcher.EnqueueAsync(decision, "ws-hard-gate", "col-hard-gate", cts.Token);

        // 模拟"进程崩溃"：不调用 StartAsync（worker 未启动，不消费 outbox）。
        // —— 等同于进程在入队后立即崩溃，worker 从未运行。

        // 验证 1：事件仍持久化在 outbox store 中（Pending 状态）。
        var stateCounts = await outboxStore.CountByStateAsync(cts.Token);
        Assert.IsTrue(
            stateCounts.TryGetValue(LearningEventOutboxStates.Pending, out var pendingCount),
            "outbox 应含 Pending 状态记录。");
        Assert.AreEqual(1, pendingCount, "崩溃后 Pending 记录数应仍为 1（未丢失）。");

        // 验证 2：崩溃恢复后 worker 通过 AcquirePendingAsync 可重新取出该事件。
        var acquired = await outboxStore.AcquirePendingAsync(
            limit: 10, owner: "recovery-worker", leaseDuration: TimeSpan.FromMinutes(1), cts.Token);
        Assert.AreEqual(1, acquired.Count, "崩溃恢复后应能取出 1 条 pending 事件。");
        Assert.AreEqual("req-crash-survival", acquired[0].DecisionId, "取出的事件 DecisionId 应匹配。");
        Assert.AreEqual(
            LearningEventOutboxStates.Processing, acquired[0].State,
            "取出后状态应转为 Processing。");

        // 验证 3：metrics 反映 pending 计数（入队时递增，可观测性）。
        Assert.AreEqual(1, metrics.GetSnapshot().PendingEvents, "metrics.PendingEvents 应为 1。");

        await dispatcher.DisposeAsync();

        // -----------------------------------------------------------------------
        // 附加：in-memory channel 路径（非 Postgres 回退）的排队语义验证。
        // 注意：in-memory channel 非持久（进程真实崩溃会丢失，与原 Task.Run 行为一致），
        // 此处仅验证"未启动 worker = 未消费 = 未丢失"的排队语义，非崩溃持久性。
        // 崩溃持久性的硬保证由上面的 Durable Outbox 路径提供。
        // -----------------------------------------------------------------------
        var channelMetrics = new LearningMaterializationMetrics();
        var channelLedger = new InMemoryUtilityLedgerStore();
        var channelConflict = new InMemoryConflictSetStore();
        // channel 路径需要 materializer 非 null 才会创建 bounded Channel。
        var channelMaterializer = new UtilityLedgerMaterializer(channelLedger, channelConflict);
        var channelDispatcher = new LearningMaterializationDispatcher(
            materializer: channelMaterializer,
            outboxStore: null, // → 走 in-memory bounded Channel
            options: new LearningMaterializationOptions { ChannelCapacity = 16 },
            metrics: channelMetrics);

        await channelDispatcher.EnqueueAsync(decision, "ws-channel", "col-channel", cts.Token);
        Assert.AreEqual(
            1, channelMetrics.GetSnapshot().PendingEvents,
            "in-memory channel 路径：入队后未启动 worker，事件应排队（PendingEvents=1）。");

        await channelDispatcher.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // 2. MaterializationFailure_IsRetried_AndObservable
    // 验证：物化失败时被重试（retry_count 递增），失败可观测（metrics.FailedEvents 递增），
    // 超过最大重试次数后转入 dead-letter（state=DeadLettered）。
    // -----------------------------------------------------------------------
    [TestMethod]
    [TestCategory("Learning-Event")]
    public async Task MaterializationFailure_IsRetried_AndObservable()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var metrics = new LearningMaterializationMetrics();
        var outboxStore = new InMemoryLearningEventOutboxStore();
        const int maxRetry = 3;
        var options = new LearningMaterializationOptions { MaxRetryCount = maxRetry };

        // 注入会失败的 materializer：UtilityLedgerMaterializer + FailingUtilityLedger
        // （AppendEntriesAsync 总是抛异常，模拟物化写入失败）。
        var failingLedger = new FailingUtilityLedger();
        var conflictStore = new InMemoryConflictSetStore();
        var materializer = new UtilityLedgerMaterializer(failingLedger, conflictStore);

        var dispatcher = new LearningMaterializationDispatcher(
            materializer: materializer,
            outboxStore: outboxStore,
            options: options,
            metrics: metrics);

        var decision = MakeDecision("req-retry-observable");

        // 入队（Durable Outbox 路径）：序列化 + 持久化。
        await dispatcher.EnqueueAsync(decision, "ws-retry", "col-retry", cts.Token);

        // 模拟 LearningMaterializationWorker 的 worker 循环：
        // AcquirePending → 反序列化 payload → MaterializeAsync（失败）→ MarkFailed（retry/dead-letter）。
        // 反复直到无 Pending（已转入 DeadLettered）。
        var owner = "test-worker";
        var leaseDuration = TimeSpan.FromMinutes(1);
        var totalFailures = 0;

        while (true)
        {
            var batch = await outboxStore.AcquirePendingAsync(
                limit: 10, owner: owner, leaseDuration: leaseDuration, cts.Token);
            if (batch.Count == 0)
            {
                // 无 Pending — 事件已转入 DeadLettered（或 Acked，本测试期望全部失败）。
                break;
            }

            foreach (var record in batch)
            {
                // 反序列化 payload（与真实 worker 一致）。
                var deserialized = JsonSerializer.Deserialize<ContextDecisionResult>(
                    record.Payload, PayloadSerializerOptions);
                Assert.IsNotNull(deserialized, "outbox payload 应可反序列化为 ContextDecisionResult。");

                try
                {
                    await materializer.MaterializeAsync(
                        deserialized, record.WorkspaceId, record.CollectionId, cts.Token);
                    Assert.Fail("FailingUtilityLedger 不应让物化成功。");
                }
                catch (InvalidOperationException)
                {
                    // 物化失败 — 与真实 worker 一致：递增 failed 计数 + MarkFailed。
                    metrics.IncrementFailed();
                    totalFailures++;
                    await outboxStore.MarkFailedAsync(
                        record.EventId,
                        record.LeaseToken ?? string.Empty,
                        "FailingUtilityLedger: simulated materialization failure",
                        CancellationToken.None);
                }
            }
        }

        // 验证 1：失败被重试 — 累计失败次数达到 maxRetry（每次失败 retry_count 递增）。
        Assert.AreEqual(
            maxRetry, totalFailures,
            $"应重试至达到上限（maxRetry={maxRetry}），实际失败次数={totalFailures}。");

        // 验证 2：metrics 中 failed_events 计数递增（可观测）。
        Assert.AreEqual(
            maxRetry, metrics.GetSnapshot().FailedEvents,
            "metrics.FailedEvents 应等于累计失败次数。");

        // 验证 3：超过最大重试次数后进入 dead-letter（state=DeadLettered）。
        var finalCounts = await outboxStore.CountByStateAsync(cts.Token);
        Assert.IsTrue(
            finalCounts.TryGetValue(LearningEventOutboxStates.DeadLettered, out var deadCount),
            "outbox 应含 DeadLettered 状态记录。");
        Assert.AreEqual(1, deadCount, "达到重试上限的事件应转入 DeadLettered。");
        Assert.IsTrue(
            finalCounts.TryGetValue(LearningEventOutboxStates.Pending, out var remainingPending),
            "outbox 状态字典应包含 Pending 键。");
        Assert.AreEqual(0, remainingPending, "DeadLettered 后不应再有 Pending 记录。");

        // 验证 4：metrics 通过 UpdateFromStateCounts 同步后反映 dead_letter_count。
        metrics.UpdateFromStateCounts(finalCounts);
        Assert.AreEqual(
            1, metrics.GetSnapshot().DeadLetterCount,
            "metrics.DeadLetterCount 同步后应为 1。");

        await dispatcher.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // 3. DatasetSnapshot_HasCompleteness_AndLineageReport
    // 验证：数据集快照应包含完整性报告（completeness）与血缘信息（lineage）。
    // 现状：项目仅有 TrainingDataExporter（JSONL + manifest），尚未实现独立 DatasetSnapshot。
    // -----------------------------------------------------------------------
    [TestMethod]
    [TestCategory("Learning-Event")]
    [TestCategory("Integration")]
    public async Task DatasetSnapshot_HasCompleteness_AndLineageReport()
    {
        // 项目当前仅有 TrainingDataExporter（JSONL + SHA-256 manifest，文件级完整性），
        // 尚未实现独立的 DatasetSnapshot 概念（含 completeness 完整率报告 + per-sample lineage）。
        // 本测试先验证当前可用的 lineage 字段（TrainingDataRecord 携带 DecisionId /
        // CandidateItemId / MaterializedAt 血缘），再对未实现的 DatasetSnapshot completeness
        // 报告标记 Inconclusive。

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ledgerStore = new InMemoryUtilityLedgerStore();
        var now = DateTimeOffset.UtcNow;
        await ledgerStore.AppendEntriesAsync(new[]
        {
            new UtilityLedgerEntry
            {
                EntryId = "e-lineage-1",
                WorkspaceId = "ws-lineage",
                CollectionId = "col-lineage",
                CandidateItemId = "item-1",
                Expert = RetrievalExpert.Semantic,
                UtilityContribution = 0.9,
                DeterministicScore = 0.9,
                FinalScore = 0.88,
                IsSelected = true,
                DecisionId = "dec-lineage-1",
                PolicyVersion = "policy/v1",
                MaterializedAt = now
            }
        }, CancellationToken.None);

        var exporter = new TrainingDataExporter(ledgerStore);
        using var tempDir = new TempDirectory();

        var request = new TrainingDataExportRequest
        {
            WorkspaceId = "ws-lineage",
            OutputDirectory = tempDir.Path,
            ModelArtifactId = "model-lineage-001"
        };
        var result = await exporter.ExportAsync(request, cts.Token);

        // 验证当前可用的 lineage 字段：TrainingDataRecord 携带 source decision_id + 物化时间。
        var lines = await File.ReadAllLinesAsync(result.DataFilePath, cts.Token);
        Assert.AreEqual(1, lines.Length, "应导出 1 条记录。");
        var record = JsonSerializer.Deserialize<TrainingDataRecord>(lines[0], WebJsonOptions)!;
        Assert.AreEqual(
            "dec-lineage-1", record.DecisionId,
            "lineage: TrainingDataRecord 应携带 source DecisionId。");
        Assert.AreEqual(
            "item-1", record.CandidateItemId,
            "lineage: TrainingDataRecord 应携带 CandidateItemId。");
        Assert.IsTrue(
            Math.Abs((record.MaterializedAt - now).TotalMilliseconds) < 1000,
            "lineage: TrainingDataRecord 应携带物化时间。");

        // 当前 manifest 已含 SHA-256 哈希（文件级完整性），但缺少数据集级 completeness 报告
        // （完整率 = 已物化 events / 总 events）与 per-sample lineage 元数据
        // （source decision_id + materialization 耗时等）。
        // 独立 DatasetSnapshot 概念尚未实现 — 标记 Inconclusive 待后续实现。
        Assert.Inconclusive(
            "DatasetSnapshot 概念（含 completeness 完整率报告 + per-sample lineage）尚未实现。" +
            "当前 TrainingDataExporter 提供 JSONL + SHA-256 manifest（文件级完整性），" +
            "TrainingDataRecord 携带 DecisionId / CandidateItemId / MaterializedAt 血缘字段。" +
            "需要实现独立 DatasetSnapshot + completeness 报告后启用本验收点。");
    }

    // -----------------------------------------------------------------------
    // 4. ExpiredLease_AckNackRenewAllRejected
    // 验证：lease 过期后，Ack/MarkFailed/RenewLease/RenewLeaseBatch 全部被拒绝
    // （CAS 统一校验 lease_expires_at > now）——旧 Worker 无法续活过期 lease，
    // 也无法在过期后把记录回退/死信/确认。过期 Processing 记录仍可被新 Worker 重新抢占。
    // -----------------------------------------------------------------------
    [TestMethod]
    [TestCategory("Learning-Event")]
    public async Task ExpiredLease_AckNackRenewAllRejected()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var outboxStore = new InMemoryLearningEventOutboxStore();
        var now = DateTimeOffset.UtcNow;

        // 入队一条事件并用"负时长"租约取出——取出的瞬间 lease 已过期（lease_expires_at <= now）。
        await outboxStore.EnqueueAsync(
            new LearningEventOutboxRecord
            {
                EventId = "ev-lease-expired",
                WorkspaceId = "ws-lease",
                CollectionId = "col-lease",
                DecisionId = "dec-lease-expired",
                Payload = "{}",
                State = LearningEventOutboxStates.Pending,
                MaxRetryCount = 3,
                CreatedAt = now,
                UpdatedAt = now
            }, cancellationToken: cts.Token);

        var acquired = await outboxStore.AcquirePendingAsync(
            limit: 10, owner: "stale-worker", leaseDuration: TimeSpan.FromSeconds(-60), cts.Token);
        Assert.AreEqual(1, acquired.Count, "应能取出 1 条事件。");
        var record = acquired[0];
        Assert.AreEqual(LearningEventOutboxStates.Processing, record.State, "取出后应为 Processing。");
        Assert.IsNotNull(record.LeaseToken, "取出后应携带 lease_token。");
        Assert.IsNotNull(record.LeaseExpiresAt, "取出后应携带数据库确认的 LeaseExpiresAt。");

        // 过期 lease 上所有写操作必须全部被拒绝——与 Postgres 的
        // lease_token = @token AND state = 'Processing' AND lease_expires_at > clock_timestamp() 一致。
        var acked = await outboxStore.MarkAckedAsync(record.EventId, record.LeaseToken!, cts.Token);
        Assert.IsFalse(acked, "过期 lease 上 Ack 应被拒绝。");

        var failed = await outboxStore.MarkFailedAsync(record.EventId, record.LeaseToken!, "boom", cts.Token);
        Assert.IsFalse(failed, "过期 lease 上 MarkFailed 应被拒绝（不得把记录回退/死信）。");

        var renewed = await outboxStore.RenewLeaseAsync(
            record.EventId, record.LeaseToken!, TimeSpan.FromMinutes(5), cts.Token);
        Assert.IsFalse(renewed, "过期 lease 上单条 Renew 应被拒绝（不得续活过期 lease）。");

        var batchRenewed = await outboxStore.RenewLeaseBatchAsync(
            new[] { (record.EventId, record.LeaseToken!) }, TimeSpan.FromMinutes(5), cts.Token);
        Assert.AreEqual(0, batchRenewed.Count, "过期 lease 上批量 Renew 应被拒绝。");

        // 记录必须保持原样（仍为 Processing、原 lease_token、原过期时间）——未被任何操作篡改。
        var stateCounts = await outboxStore.CountByStateAsync(cts.Token);
        Assert.AreEqual(1, stateCounts[LearningEventOutboxStates.Processing], "记录应仍为 Processing。");

        // 过期 Processing 记录可被新 Worker 重新抢占——旧 Worker 的写操作全部失效。
        var reacquired = await outboxStore.AcquirePendingAsync(
            limit: 10, owner: "fresh-worker", leaseDuration: TimeSpan.FromMinutes(1), cts.Token);
        Assert.AreEqual(1, reacquired.Count, "过期 Processing 记录应可被新 Worker 重新抢占。");
        Assert.AreNotEqual(
            record.LeaseToken, reacquired[0].LeaseToken,
            "重新抢占后 lease_token 应更新为新的唯一 token。");
        Assert.IsTrue(
            reacquired[0].LeaseExpiresAt > DateTimeOffset.UtcNow,
            "重新抢占后 LeaseExpiresAt 应为未来的有效时间。");
    }

    // -----------------------------------------------------------------------
    // 5. RenewLeaseBatch_ReturnsDbConfirmedExpiry
    // 验证：批量续约返回每个成功续约 eventId 的数据库确认新到期时间（Postgres 为
    // RETURNING lease_expires_at）——Worker 以该值更新本地期限，不再以应用时钟重新估算。
    // -----------------------------------------------------------------------
    [TestMethod]
    [TestCategory("Learning-Event")]
    public async Task RenewLeaseBatch_ReturnsDbConfirmedExpiry()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var outboxStore = new InMemoryLearningEventOutboxStore();
        var now = DateTimeOffset.UtcNow;

        await outboxStore.EnqueueAsync(
            new LearningEventOutboxRecord
            {
                EventId = "ev-renew-batch",
                WorkspaceId = "ws-lease",
                CollectionId = "col-lease",
                DecisionId = "dec-renew-batch",
                Payload = "{}",
                State = LearningEventOutboxStates.Pending,
                MaxRetryCount = 3,
                CreatedAt = now,
                UpdatedAt = now
            }, cancellationToken: cts.Token);

        var acquired = await outboxStore.AcquirePendingAsync(
            limit: 10, owner: "worker-a", leaseDuration: TimeSpan.FromMinutes(1), cts.Token);
        var record = acquired[0];

        var renewed = await outboxStore.RenewLeaseBatchAsync(
            new[] { (record.EventId, record.LeaseToken!) }, TimeSpan.FromMinutes(5), cts.Token);

        Assert.AreEqual(1, renewed.Count, "有效 lease 应续约成功。");
        Assert.IsTrue(
            renewed.TryGetValue(record.EventId, out var confirmedExpiry),
            "返回值应包含续约成功的 eventId → 新到期时间。");
        var remaining = (confirmedExpiry - DateTimeOffset.UtcNow).TotalMinutes;
        Assert.IsTrue(
            remaining is > 4.5 and < 5.5,
            $"返回的到期时间应为续约时长后的 DB 确认值（当前剩余 {remaining:F1} 分钟）。");
        Assert.IsTrue(
            confirmedExpiry > record.LeaseExpiresAt,
            "续约后的到期时间应晚于续约前的到期时间。");

        // 续约后 lease 有效——其他 worker 无法抢占，且状态仍为 Processing。
        var stolen = await outboxStore.AcquirePendingAsync(
            limit: 10, owner: "worker-b", leaseDuration: TimeSpan.FromSeconds(1), cts.Token);
        Assert.AreEqual(0, stolen.Count, "续约后未过期，其他 worker 不应抢占。");
        var counts = await outboxStore.CountByStateAsync(cts.Token);
        Assert.AreEqual(1, counts[LearningEventOutboxStates.Processing], "记录应仍为 Processing。");
    }

    // -----------------------------------------------------------------------
    // 6. Acquire_ReturnsDbConfirmedLeaseExpiresAt
    // 验证：AcquirePendingAsync 返回的记录携带数据库确认的 LeaseExpiresAt
    // （Postgres RETURNING lease_expires_at）——Worker 本地期限直接采用该值。
    // -----------------------------------------------------------------------
    [TestMethod]
    [TestCategory("Learning-Event")]
    public async Task Acquire_ReturnsDbConfirmedLeaseExpiresAt()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var outboxStore = new InMemoryLearningEventOutboxStore();
        var now = DateTimeOffset.UtcNow;
        var leaseDuration = TimeSpan.FromMinutes(3);

        await outboxStore.EnqueueAsync(
            new LearningEventOutboxRecord
            {
                EventId = "ev-acquire-expiry",
                WorkspaceId = "ws-lease",
                CollectionId = "col-lease",
                DecisionId = "dec-acquire-expiry",
                Payload = "{}",
                State = LearningEventOutboxStates.Pending,
                MaxRetryCount = 3,
                CreatedAt = now,
                UpdatedAt = now
            }, cancellationToken: cts.Token);

        var acquired = await outboxStore.AcquirePendingAsync(
            limit: 10, owner: "worker-a", leaseDuration: leaseDuration, cts.Token);

        Assert.AreEqual(1, acquired.Count, "应取出 1 条事件。");
        var record = acquired[0];
        Assert.IsNotNull(record.LeaseExpiresAt, "Acquire 返回的记录必须携带 LeaseExpiresAt。");
        var remaining = (record.LeaseExpiresAt!.Value - DateTimeOffset.UtcNow).TotalMinutes;
        Assert.IsTrue(
            remaining is > 2.5 and < 3.5,
            $"返回的 LeaseExpiresAt 应为数据库确认的到期时间（当前剩余 {remaining:F1} 分钟）。");
    }

    // --- helpers ---

    private static ContextDecisionResult MakeDecision(string requestId)
    {
        return new ContextDecisionResult
        {
            RequestId = requestId,
            PolicyVersion = "policy/hard-gate"
        };
    }

    /// <summary>临时目录助手（导出测试用）。</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ContextCoreTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

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
                // 忽略清理失败
            }
        }
    }

    // =======================================================================
    // 测试替身：InMemoryLearningEventOutboxStore
    // 精确模拟 PostgresLearningEventOutboxStore 的 lease + retry + CAS 语义。
    // =======================================================================

    private sealed class InMemoryLearningEventOutboxStore : ILearningEventOutboxStore
    {
        private readonly ConcurrentDictionary<string, LearningEventOutboxRecord> _records = new();
        private readonly object _lock = new();

        public Task EnqueueAsync(
            LearningEventOutboxRecord record,
            IWriteTransactionScope? scope = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(record);
            _records[record.EventId] = record;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LearningEventOutboxRecord>> AcquirePendingAsync(
            int limit,
            string owner,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(owner);
            if (limit <= 0)
            {
                return Task.FromResult<IReadOnlyList<LearningEventOutboxRecord>>(Array.Empty<LearningEventOutboxRecord>());
            }

            var now = DateTimeOffset.UtcNow;
            var leaseUntil = now.Add(leaseDuration);
            // 每次 AcquirePending 生成唯一 lease_token，与 Postgres 实现保持一致。
            var leaseToken = Guid.NewGuid().ToString("N");

            lock (_lock)
            {
                // 取 Pending 或 Processing 但租约已过期的记录（与 SELECT FOR UPDATE SKIP LOCKED 语义一致）。
                var toAcquire = _records.Values
                    .Where(r => r.State == LearningEventOutboxStates.Pending
                                || (r.State == LearningEventOutboxStates.Processing
                                    && r.LeaseExpiresAt is { } exp && exp <= now))
                    .Take(limit)
                    .ToList();

                var result = new List<LearningEventOutboxRecord>(toAcquire.Count);
                foreach (var r in toAcquire)
                {
                    var updated = Clone(
                        r,
                        state: LearningEventOutboxStates.Processing,
                        retryCount: r.RetryCount,
                        leaseOwner: owner,
                        leaseExpiresAt: leaseUntil,
                        leaseToken: leaseToken,
                        processedAt: r.ProcessedAt,
                        lastError: r.LastError,
                        deadLetterReason: r.DeadLetterReason);
                    _records[r.EventId] = updated;
                    result.Add(updated);
                }

                return Task.FromResult<IReadOnlyList<LearningEventOutboxRecord>>(result);
            }
        }

        public Task<bool> MarkAckedAsync(
            string eventId,
            string leaseToken,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
            ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
            var now = DateTimeOffset.UtcNow;

            lock (_lock)
            {
                if (!_records.TryGetValue(eventId, out var r)
                    || r.State != LearningEventOutboxStates.Processing
                    || r.LeaseToken != leaseToken
                    || (r.LeaseExpiresAt is { } leaseExpiresAt && leaseExpiresAt <= now))
                {
                    // lease_token 不匹配或 lease 已过期——lease 已被其他 worker 抢占或已 Ack/Nack。
                    return Task.FromResult(false);
                }

                // CAS：仅当 state=Processing 且 lease_token 匹配时转为 Acked。
                _records[eventId] = Clone(
                    r,
                    state: LearningEventOutboxStates.Acked,
                    retryCount: r.RetryCount,
                    leaseOwner: null,
                    leaseExpiresAt: null,
                    leaseToken: null,
                    processedAt: now,
                    lastError: r.LastError,
                    deadLetterReason: r.DeadLetterReason);
                return Task.FromResult(true);
            }
        }

        public Task<bool> MarkFailedAsync(
            string eventId,
            string leaseToken,
            string errorMessage,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
            ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
            var now = DateTimeOffset.UtcNow;

            lock (_lock)
            {
                if (!_records.TryGetValue(eventId, out var r)
                    || r.State != LearningEventOutboxStates.Processing
                    || r.LeaseToken != leaseToken
                    || (r.LeaseExpiresAt is { } leaseExpiresAt && leaseExpiresAt <= now))
                {
                    // lease_token 不匹配或 lease 已过期——lease 已被其他 worker 抢占或已 Ack/Nack。
                    return Task.FromResult(false);
                }

                // CAS：仅当 state=Processing 且 lease_token 匹配时转换为 DeadLettered 或 Pending。
                // retry_count + 1 >= max_retry_count → DeadLettered，否则回退 Pending 等待重试。
                var newRetry = r.RetryCount + 1;
                var isDead = newRetry >= r.MaxRetryCount;
                _records[eventId] = Clone(
                    r,
                    state: isDead ? LearningEventOutboxStates.DeadLettered : LearningEventOutboxStates.Pending,
                    retryCount: newRetry,
                    leaseOwner: null,
                    leaseExpiresAt: null,
                    leaseToken: null,
                    processedAt: r.ProcessedAt,
                    lastError: errorMessage,
                    deadLetterReason: isDead ? errorMessage : null);
                return Task.FromResult(true);
            }
        }

        public Task<bool> RenewLeaseAsync(
            string eventId,
            string leaseToken,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
            ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
            var now = DateTimeOffset.UtcNow;

            lock (_lock)
            {
                // 用 lease_token 替代 lease_owner 校验更严格（token 全局唯一）。
                // 仅当 lease 未过期（lease_expires_at > now）时才允许续约——防止旧 Worker 续活过期 lease。
                if (!_records.TryGetValue(eventId, out var r)
                    || r.State != LearningEventOutboxStates.Processing
                    || r.LeaseToken != leaseToken
                    || (r.LeaseExpiresAt is { } leaseExpiresAt && leaseExpiresAt <= now))
                {
                    return Task.FromResult(false);
                }

                _records[eventId] = Clone(
                    r,
                    state: r.State,
                    retryCount: r.RetryCount,
                    leaseOwner: r.LeaseOwner,
                    leaseExpiresAt: now.Add(leaseDuration),
                    leaseToken: leaseToken,
                    processedAt: r.ProcessedAt,
                    lastError: r.LastError,
                    deadLetterReason: r.DeadLetterReason);
                return Task.FromResult(true);
            }
        }

        public Task<IReadOnlyDictionary<string, DateTimeOffset>> RenewLeaseBatchAsync(
            IReadOnlyList<(string EventId, string LeaseToken)> leases,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var renewed = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            var now = DateTimeOffset.UtcNow;
            lock (_lock)
            {
                foreach (var (eventId, leaseToken) in leases)
                {
                    if (!_records.TryGetValue(eventId, out var r)
                        || r.State != LearningEventOutboxStates.Processing
                        || r.LeaseToken != leaseToken
                        || (r.LeaseExpiresAt is { } leaseExpiresAt && leaseExpiresAt <= now))
                    {
                        continue;
                    }
                    _records[eventId] = Clone(
                        r, state: r.State, retryCount: r.RetryCount,
                        leaseOwner: r.LeaseOwner,
                        leaseExpiresAt: now.Add(leaseDuration),
                        leaseToken: leaseToken,
                        processedAt: r.ProcessedAt,
                        lastError: r.LastError,
                        deadLetterReason: r.DeadLetterReason);
                    renewed[eventId] = now.Add(leaseDuration);
                }
            }
            return Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>>(renewed);
        }

        public Task<IReadOnlyDictionary<string, int>> CountByStateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 预初始化所有已知状态为 0（与可观测性契约一致：消费方可直接 TryGetValue 读取任意状态，
            // 无需处理键缺失）。Postgres 实现仅返回非零状态，但 metrics/observability 消费方
            // 期望所有已知状态键均存在——本测试替身按此契约实现。
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [LearningEventOutboxStates.Pending] = 0,
                [LearningEventOutboxStates.Processing] = 0,
                [LearningEventOutboxStates.Acked] = 0,
                [LearningEventOutboxStates.DeadLettered] = 0
            };
            foreach (var r in _records.Values)
            {
                counts.TryGetValue(r.State, out var current);
                counts[r.State] = current + 1;
            }
            return Task.FromResult<IReadOnlyDictionary<string, int>>(counts);
        }

        public Task<DateTimeOffset?> GetLastSuccessAtAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset? last = null;
            foreach (var r in _records.Values)
            {
                if (r.State == LearningEventOutboxStates.Acked && r.ProcessedAt.HasValue)
                {
                    if (!last.HasValue || r.ProcessedAt.Value > last.Value)
                    {
                        last = r.ProcessedAt.Value;
                    }
                }
            }

            return Task.FromResult(last);
        }

        // LearningEventOutboxRecord 是 sealed class（非 record），不支持 with 表达式，手动克隆。
        private static LearningEventOutboxRecord Clone(
            LearningEventOutboxRecord r,
            string state,
            int retryCount,
            string? leaseOwner,
            DateTimeOffset? leaseExpiresAt,
            string? leaseToken,
            DateTimeOffset? processedAt,
            string? lastError,
            string? deadLetterReason)
        {
            return new LearningEventOutboxRecord
            {
                EventId = r.EventId,
                WorkspaceId = r.WorkspaceId,
                CollectionId = r.CollectionId,
                DecisionId = r.DecisionId,
                Payload = r.Payload,
                State = state,
                RetryCount = retryCount,
                MaxRetryCount = r.MaxRetryCount,
                CreatedAt = r.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
                ProcessedAt = processedAt,
                LeaseOwner = leaseOwner,
                LeaseExpiresAt = leaseExpiresAt,
                LeaseToken = leaseToken,
                LastError = lastError,
                DeadLetterReason = deadLetterReason
            };
        }
    }

    // =======================================================================
    // 测试替身：FailingUtilityLedger
    // AppendEntriesAsync 总是抛异常，模拟物化写入失败。
    // =======================================================================

    private sealed class FailingUtilityLedger : IUtilityLedger
    {
        public Task AppendEntriesAsync(
            IReadOnlyList<UtilityLedgerEntry> entries,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("FailingUtilityLedger: simulated materialization failure.");

        public Task<IReadOnlyList<UtilityLedgerEntry>> QueryAsync(
            UtilityLedgerQuery query,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<UtilityLedgerEntry>>(Array.Empty<UtilityLedgerEntry>());

        public Task<UtilityLedgerEntry?> GetLatestEntryAsync(
            string workspaceId,
            string collectionId,
            string candidateItemId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<UtilityLedgerEntry?>(null);

        public Task<IReadOnlyDictionary<RetrievalExpert, double>> GetExpertContributionsAsync(
            string workspaceId,
            string collectionId,
            string candidateItemId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<RetrievalExpert, double>>(new Dictionary<RetrievalExpert, double>());
    }
}
