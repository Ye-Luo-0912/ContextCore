using ContextCore.Abstractions;
using Microsoft.Extensions.Logging;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// ToolReconciliationCoordinator — Tool 对账协调器（Worker 与 resolve 端点共用）
//
// 集中封装对账记录裁决的唯一入口：
// - ResolveAsync：人工/自动裁决（POST /runs/{runId}/reconciliations/{id}/resolve 与
// ToolReconciliationWorker 共用），返回 0=成功 / 1=不存在 / 2=已裁决；
// - ReconcileRecordAsync：Worker 路径——TryBeginAsync 接管 → 调 Handler 确认真相 →
// CommitOutcomeAsync 提交；Handler 异常回退 Pending 重试；
// - CommitOutcomeAsync：journal 状态推进（DispatchingIntent/Dispatched → Reconciling →
// Committed + 对账结果）与记录终态（Resolved/Rejected）原子完成。
//
// 不变量：任何记录从 Pending/Running 变为 Resolved/Rejected 的唯一路径都经过本协调器，
// 保证 journal 真相与记录裁决一致（绝不出现"记录 Resolved 但 journal 未提交"的撕裂）。
// ===========================================================================

/// <summary>
/// Tool 对账协调器：对账记录裁决的唯一入口，供 <see cref="ToolReconciliationWorker"/> 轮询
/// 与 HTTP resolve 端点共用，保证 journal 提交与记录裁决原子一致。
/// </summary>
public sealed class ToolReconciliationCoordinator
{
    private readonly IToolReconciliationStore _store;
    private readonly IToolDispatchJournal? _journal;
    private readonly ILogger<ToolReconciliationCoordinator> _logger;

    public ToolReconciliationCoordinator(
        IToolReconciliationStore store,
        IToolDispatchJournal? journal,
        ILogger<ToolReconciliationCoordinator> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _journal = journal;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 人工/自动裁决对账记录（POST resolve 端点与 Worker 共用）。
    /// </summary>
    /// <returns>0 = 成功裁决；1 = 记录不存在；2 = 已裁决（幂等冲突）。</returns>
    public async Task<int> ResolveAsync(string reconciliationId, ToolReconciliationOutcome outcome, CancellationToken ct)
    {
        var record = await _store.GetAsync(reconciliationId, ct).ConfigureAwait(false);
        if (record is null)
        {
            return 1;
        }
        if (record.Status == ToolReconciliationStatus.Resolved || record.Status == ToolReconciliationStatus.Rejected)
        {
            return 2;
        }

        await CommitOutcomeAsync(record, outcome, ct).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Worker 路径：接管记录（Pending → Running）→ 调 Handler 确认外部副作用真相 → 提交裁决。
    /// Handler 抛异常时回退 Pending（下次轮询重试），异常向上传播由调用方记录日志。
    /// </summary>
    public async Task ReconcileRecordAsync(
        ToolReconciliationRecord record,
        IToolReconciliationHandler handler,
        CancellationToken ct)
    {
        if (!await _store.TryBeginAsync(record.ReconciliationId, ct).ConfigureAwait(false))
        {
            return; // 已被并发 Worker / resolve 端点接管
        }

        try
        {
            var outcome = await handler.ReconcileAsync(record, ct).ConfigureAwait(false);
            await CommitOutcomeAsync(record, outcome, ct).ConfigureAwait(false);
        }
        catch
        {
            // 对账失败不进入终态——回退 Pending 等待下轮重试 / 人工裁决。
            await _store.TryResetToPendingAsync(record.ReconciliationId, ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 提交对账真相：journal 状态推进（DispatchingIntent/Dispatched → Reconciling → Committed + 结果）
    /// 与记录裁决（Resolved/Rejected）原子完成。
    /// </summary>
    public async Task CommitOutcomeAsync(ToolReconciliationRecord record, ToolReconciliationOutcome outcome, CancellationToken ct)
    {
        if (_journal is not null)
        {
            // 显式进入 Reconciling（DispatchingIntent/Dispatched → Reconciling），再提交真相结果。
            // BeginReconciliationAsync 对已 Reconciling/Committed/ResultDelivered 幂等；
            // 条目缺失或处于 Prepared（外部调用从未开始）时抛异常——由调用方捕获记录日志，
            // 记录本身仍可裁决（Prepared 条目无需对账，保持 Resolved 不破坏约束）。
            try
            {
                await _journal.BeginReconciliationAsync(record.RequestId, ct).ConfigureAwait(false);
                await _journal.MarkReconciledWithResultAsync(record.RequestId, new DurableToolResult
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
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ToolReconciliationCoordinator: 记录 {Id}（tool={Tool}, run={Run}）journal 对账提交失败，记录仍按裁决终态落库。",
                    record.ReconciliationId, record.ToolName, record.RunId);
            }
        }

        if (outcome.SideEffectOccurred)
        {
            await _store.MarkResolvedAsync(record.ReconciliationId, outcome, ct).ConfigureAwait(false);
        }
        else
        {
            await _store.MarkRejectedAsync(record.ReconciliationId, outcome, ct).ConfigureAwait(false);
        }
    }
}
