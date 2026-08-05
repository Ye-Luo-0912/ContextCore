using ContextCore.Abstractions;
using Microsoft.Extensions.Logging;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// ToolReconciliationCoordinator — Tool 对账协调器（Worker 与 resolve 端点共用）
//
// 集中封装对账记录裁决的唯一入口：
// - ResolveAsync：人工/自动裁决（POST /runs/{runId}/reconciliations/{id}/resolve 与
// ToolReconciliationWorker 共用），返回 0=成功（含幂等重试）/ 1=不存在 / 2=已裁决 /
// 3=仲裁权被占用 / 4=决策冲突（相同 DecisionRequestId 但相反 outcome）；
// - ReconcileWithLeaseAsync：Worker 路径——调用方已领取裁决租约（P0-4/P0-5），
// 本方法执行 Handler 确认外部副作用真相 → 原子提交裁决；Handler 异常回退 Pending 重试。
// 心跳续租由 ToolReconciliationWorker 的共享批量心跳循环负责（单次往返续约整批记录）；
// - CommitOutcomeAsync：先原子取得裁决权（租约）再提交——调用
// IToolReconciliationStore.ResolveReconciliationAtomicallyAsync 单事务完成
// journal 推进 + 结果 UPSERT + 记录终态 + 可选 Run 推进 + 审计事件（P0-3）。
//
// 不变量：任何记录从 Pending/Running 变为 Resolved/Rejected 的唯一路径都经过本协调器，
// 且先持有有效租约（唯一裁决者），再经单事务原子提交——绝不出现
// "记录 Resolved 而 Journal 仍 DispatchingIntent" 的撕裂，也杜绝
// 人工裁决与自动 Handler 竞争写入相反结果（P0-5 仲裁权）。
// ===========================================================================

/// <summary>
/// Tool 对账协调器：对账记录裁决的唯一入口，供 <see cref="ToolReconciliationWorker"/> 轮询
/// 与 HTTP resolve 端点共用，保证 journal 提交与记录裁决原子一致。
/// </summary>
public sealed class ToolReconciliationCoordinator
{
    /// <summary>Worker / 端点领取的裁决租约时长（P0-4：过期后其他 Worker 可接管）。</summary>
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>Handler 失败后的回退退避时长。</summary>
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(30);

    private readonly IToolReconciliationStore _store;
    private readonly ILogger<ToolReconciliationCoordinator> _logger;

    public ToolReconciliationCoordinator(
        IToolReconciliationStore store,
        ILogger<ToolReconciliationCoordinator> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 人工/自动裁决对账记录（POST resolve 端点与 Worker 共用）。
    /// </summary>
    /// <returns>0 = 成功裁决（含幂等重试）；1 = 记录不存在；2 = 已裁决（无决策身份或身份不同）；
    /// 3 = 仲裁权被占用/租约失效；4 = 决策冲突（相同 DecisionRequestId 但相反 outcome）。</returns>
    public async Task<int> ResolveAsync(
        string reconciliationId,
        ToolReconciliationOutcome outcome,
        CancellationToken ct,
        string? decisionRequestId = null)
    {
        var record = await _store.GetAsync(reconciliationId, ct).ConfigureAwait(false);
        if (record is null)
        {
            return 1;
        }
        if (record.Status is ToolReconciliationStatus.Resolved or ToolReconciliationStatus.Rejected)
        {
            // 客户端决策幂等——相同决策身份 + 相同 outcome → 幂等成功（0）；
            // 相同决策身份 + 相反 outcome → 决策冲突（4）；无/不同决策身份 → 2（重复提交被拒绝）。
            if (!string.IsNullOrWhiteSpace(decisionRequestId)
                && string.Equals(record.DecisionRequestId, decisionRequestId, StringComparison.Ordinal))
            {
                return record.SideEffectOccurred == outcome.SideEffectOccurred ? 0 : 4;
            }
            return 2;
        }

        // P0-5：先原子取得裁决权（租约），再执行 Journal 提交——人工裁决与自动 Handler
        // 竞争时只有一个赢家持有租约，输家无法再修改 Journal。
        var lease = await _store.TryBeginAsync(reconciliationId, "manual:endpoint", LeaseDuration, ct).ConfigureAwait(false);
        if (lease is null)
        {
            return 3; // 仲裁权被占用（有效租约持有中 / 退避未到期 / 终态竞态）
        }

        try
        {
            return await CommitOutcomeAsync(record, lease, outcome, ct, decisionRequestId).ConfigureAwait(false);
        }
        catch
        {
            // 原子裁决失败（整体回滚，记录未被终态化）→ 回退 Pending 等待重试 / 人工再裁决。
            await _store.TryResetToPendingAsync(
                reconciliationId, lease.LeaseToken, "resolve failed", RetryBackoff, ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Worker 路径：调用方已通过 <see cref="IToolReconciliationStore.TryBeginAsync"/> 领取裁决租约
    /// （Pending → Running + lease/fencing），此处调 Handler 确认外部副作用真相 → 原子提交裁决。
    /// 心跳续租由调用方负责（ToolReconciliationWorker 的共享批量心跳循环，单次往返续约整批记录）。
    /// Handler 抛异常时回退 Pending（携带 last_error + 退避，下次轮询重试），
    /// 异常向上传播由调用方记录日志。
    /// </summary>
    public async Task ReconcileWithLeaseAsync(
        ToolReconciliationRecord record,
        ToolReconciliationLease lease,
        IToolReconciliationHandler handler,
        CancellationToken ct)
    {
        ToolReconciliationOutcome outcome;
        try
        {
            outcome = await handler.ReconcileAsync(record, ct).ConfigureAwait(false);
        }
        catch
        {
            // 对账失败不进入终态——回退 Pending（last_error + 退避）等待下轮重试 / 人工裁决。
            // 若租约已被接管，TryResetToPendingAsync 返回 false（记录由新持有者继续处理），不阻断。
            await _store.TryResetToPendingAsync(
                record.ReconciliationId, lease.LeaseToken, "handler failed", RetryBackoff, ct).ConfigureAwait(false);
            throw;
        }

        await CommitOutcomeAsync(record, lease, outcome, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 原子提交对账真相（P0-3）：单事务完成 journal 状态推进（DispatchingIntent/Dispatched →
    /// Reconciling → Committed + 对账结果）、Durable Result UPSERT、记录终态（Resolved/Rejected）、
    /// Run 状态推进（Run 停车且无其他未决记录时 → Queued，同一事务，杜绝崩溃后永久停车）与
    /// 审计事件追加——任意一步失败整体回滚，绝不出现撕裂。
    /// 调用方必须先持有有效租约（P0-5 唯一裁决者）。
    /// </summary>
    public async Task<int> CommitOutcomeAsync(
        ToolReconciliationRecord record,
        ToolReconciliationLease lease,
        ToolReconciliationOutcome outcome,
        CancellationToken ct,
        string? decisionRequestId = null)
    {
        var durableResult = new DurableToolResult
        {
            ToolCallId = record.RequestId,
            RequestId = record.RequestId,
            WorkspaceId = record.WorkspaceId,
            RunId = record.RunId,
            InvocationId = record.RequestId,
            SideEffect = ToolSideEffect.Write,
            ExternalOperationId = record.ExternalOperationId,
            Result = outcome.SideEffectOccurred ? outcome.Result : null,
            Succeeded = outcome.SideEffectOccurred,
            Error = outcome.SideEffectOccurred
                ? null
                : (outcome.Error ?? "reconciled: 外部副作用确认未发生（void）"),
            DurationMs = 0
        };

        var resolution = await _store.ResolveReconciliationAtomicallyAsync(
            record.WorkspaceId,
            record.RunId,
            record.RequestId,
            lease.LeaseToken,
            lease.FencingToken,
            outcome,
            durableResult,
            ct,
            decisionRequestId).ConfigureAwait(false);

        return resolution.Status switch
        {
            ToolReconciliationResolutionStatus.Resolved => 0,
            ToolReconciliationResolutionStatus.NotFound => 1,
            ToolReconciliationResolutionStatus.AlreadyTerminal => 2,
            ToolReconciliationResolutionStatus.DecisionConflict => 4,
            // ArbitrationLost / VersionMismatch：租约在提交前被接管/失效，裁决权已不属于本次调用。
            _ => 3
        };
    }
}
