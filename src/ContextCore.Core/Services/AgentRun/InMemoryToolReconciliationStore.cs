using System.Collections.Concurrent;
using System.Text.Json;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// InMemoryToolReconciliationStore — Tool 对账记录存储（进程内默认实现）
//
// 支撑 Run 级"未裁决不完成"约束与 ToolReconciliationWorker 轮询：
// - CreateAsync 按 (WorkspaceId, RunId, RequestId) 幂等（P0-5 完整租户键）；
// - HasUnresolvedForRunAsync 供 CompleteAsync 门禁查询（Pending/Running 存在 → 禁止 Completed）；
// - TryBeginAsync 领取裁决租约（Pending → Running + lease/fencing，含过期 Running 接管，
//   P0-4 崩溃恢复；所有 Resolve/Fail/Renew 校验 lease_token + 未过期）；
// - RenewLeaseAsync 心跳续租；TryResetToPendingAsync 失败回退（携带 last_error + 退避）；
// - ResolveReconciliationAtomicallyAsync 锁等价原子裁决：进程内单门串行化
//   journal 推进（状态分支：Prepared/缺失 → Corrupted；Committed/ResultDelivered →
//   指纹幂等判定）+ 结果 UPSERT + 记录终态 + Run 推进（停车且无未决 → Queued）
//   + 审计事件（P0-3）。
// ===========================================================================

/// <summary>
/// Tool 对账记录存储（进程内实现）。原子裁决通过进程内信号量提供"锁等价"，
/// journal / 结果 / 记录 / Run / 事件等存储经可选构造参数注入后在同一临界区内更新。
/// </summary>
public sealed class InMemoryToolReconciliationStore : IToolReconciliationStore
{
    private readonly ConcurrentDictionary<string, ToolReconciliationRecord> _records = new(StringComparer.Ordinal);
    private readonly IToolDispatchJournal? _journal;
    private readonly IDurableToolResultStore? _resultStore;
    private readonly IAgentRunStore? _runStore;
    private readonly IAgentRunEventStore? _eventStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 初始化进程内对账存储。
    /// </summary>
    /// <param name="journal">可选 Tool Dispatch Journal：提供后原子裁决会同步推进 journal 状态。</param>
    /// <param name="resultStore">可选 Durable Tool Result 存储：提供后原子裁决会同步 UPSERT 结果。</param>
    /// <param name="runStore">可选 Run 存储：提供后原子裁决支持 Run 状态推进。</param>
    /// <param name="eventStore">可选事件存储：提供后原子裁决会追加 ToolReconciliationResolved 审计事件。</param>
    public InMemoryToolReconciliationStore(
        IToolDispatchJournal? journal = null,
        IDurableToolResultStore? resultStore = null,
        IAgentRunStore? runStore = null,
        IAgentRunEventStore? eventStore = null)
    {
        _journal = journal;
        _resultStore = resultStore;
        _runStore = runStore;
        _eventStore = eventStore;
    }

    /// <inheritdoc />
    public ValueTask<ToolReconciliationRecord> CreateAsync(ToolReconciliationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        // 按 (WorkspaceId, RunId, RequestId) 幂等（P0-5 完整租户键）：同一 Tool 调用只保留一条对账记录。
        var existing = _records.Values.FirstOrDefault(r =>
            string.Equals(r.WorkspaceId, record.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(r.RunId, record.RunId, StringComparison.Ordinal)
            && string.Equals(r.RequestId, record.RequestId, StringComparison.Ordinal));
        if (existing is not null)
        {
            return ValueTask.FromResult(existing);
        }

        var created = record with { CreatedAt = record.CreatedAt == default ? DateTimeOffset.UtcNow : record.CreatedAt };
        _records[record.ReconciliationId] = created;
        return ValueTask.FromResult(created);
    }

    /// <inheritdoc />
    public ValueTask<ToolReconciliationRecord?> GetAsync(string reconciliationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _records.TryGetValue(reconciliationId, out var record);
        return ValueTask.FromResult(record);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ToolReconciliationRecord>> ListByRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var records = _records.Values
            .Where(r => string.Equals(r.RunId, runId, StringComparison.Ordinal))
            .OrderBy(r => r.CreatedAt)
            .ToList();
        return ValueTask.FromResult<IReadOnlyList<ToolReconciliationRecord>>(records);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ToolReconciliationRecord>> QueryByExternalOperationIdAsync(string externalOperationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(externalOperationId);
        var records = _records.Values
            .Where(r => string.Equals(r.ExternalOperationId, externalOperationId, StringComparison.Ordinal))
            .OrderByDescending(r => r.CreatedAt)
            .ToList();
        return ValueTask.FromResult<IReadOnlyList<ToolReconciliationRecord>>(records);
    }

    /// <inheritdoc />
    public ValueTask<ReconciliationListResult> ListAsync(ReconciliationQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(query);
        var now = DateTimeOffset.UtcNow;
        var filtered = _records.Values
            .Where(r => query.WorkspaceId is null || string.Equals(r.WorkspaceId, query.WorkspaceId, StringComparison.Ordinal))
            .Where(r => query.RunId is null || string.Equals(r.RunId, query.RunId, StringComparison.Ordinal))
            .Where(r => query.Status is null || r.Status == query.Status)
            .Where(r => !query.OverdueOnly || IsOverdue(r, now))
            .OrderByDescending(r => r.CreatedAt)
            .ToList();

        var overdueCount = filtered.Count(r => IsOverdue(r, now));
        var page = filtered
            .Skip(Math.Max(0, query.Offset))
            .Take(Math.Clamp(query.Limit > 0 ? query.Limit : 50, 1, 200))
            .ToList();

        return ValueTask.FromResult(new ReconciliationListResult
        {
            Items = page,
            Total = filtered.Count,
            OverdueCount = overdueCount
        });
    }

    /// <inheritdoc />
    public ValueTask<bool> HasUnresolvedForRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hasUnresolved = _records.Values.Any(r =>
            string.Equals(r.RunId, runId, StringComparison.Ordinal)
            && (r.Status == ToolReconciliationStatus.Pending || r.Status == ToolReconciliationStatus.Running));
        return ValueTask.FromResult(hasUnresolved);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ToolReconciliationRecord>> ListPendingAsync(int take, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var records = _records.Values
            .Where(r =>
                // Pending 或租约已过期的 Running（P0-4：Worker 崩溃后重新领取）
                (r.Status == ToolReconciliationStatus.Pending
                 || (r.Status == ToolReconciliationStatus.Running
                     && (!r.LeaseExpiresAt.HasValue || r.LeaseExpiresAt.Value <= now)))
                // 退避未到期跳过
                && (!r.NextAttemptAt.HasValue || r.NextAttemptAt.Value <= now))
            .OrderBy(r => r.CreatedAt)
            .Take(take)
            .ToList();
        return ValueTask.FromResult<IReadOnlyList<ToolReconciliationRecord>>(records);
    }

    /// <inheritdoc />
    public ValueTask<ToolReconciliationLease?> TryBeginAsync(
        string reconciliationId,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        ToolReconciliationLease? lease = null;
        _records.AddOrUpdate(
            reconciliationId,
            _ => throw new InvalidOperationException($"对账记录不存在：{reconciliationId}"),
            (_, existing) =>
            {
                // 可领取：Pending；或 Running 且租约已过期（P0-4 崩溃恢复接管）。
                // 终态 / 有效租约持有中 / 退避未到期 → 不领取（返回 null）。
                var runningLeaseExpired = existing.Status == ToolReconciliationStatus.Running
                    && (!existing.LeaseExpiresAt.HasValue || existing.LeaseExpiresAt.Value <= now);
                if (existing.Status != ToolReconciliationStatus.Pending && !runningLeaseExpired)
                {
                    return existing;
                }
                if (existing.NextAttemptAt.HasValue && existing.NextAttemptAt.Value > now)
                {
                    return existing;
                }

                lease = new ToolReconciliationLease
                {
                    LeaseToken = Guid.NewGuid().ToString("N"),
                    FencingToken = existing.FencingToken + 1,
                    ExpiresAt = now + leaseDuration
                };
                return existing with
                {
                    Status = ToolReconciliationStatus.Running,
                    LeaseOwner = leaseOwner,
                    LeaseToken = lease.LeaseToken,
                    LeaseExpiresAt = lease.ExpiresAt,
                    FencingToken = lease.FencingToken,
                    AttemptCount = existing.AttemptCount + 1,
                    NextAttemptAt = null,
                    LastError = null,
                    UpdatedAt = now
                };
            });
        return ValueTask.FromResult(lease);
    }

    /// <inheritdoc />
    public ValueTask<bool> RenewLeaseAsync(
        string reconciliationId,
        string leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var renewed = false;
        _records.AddOrUpdate(
            reconciliationId,
            _ => throw new InvalidOperationException($"对账记录不存在：{reconciliationId}"),
            (_, existing) =>
            {
                // P0-4：续租必须校验 lease_token 匹配且未过期。
                if (existing.Status != ToolReconciliationStatus.Running
                    || !string.Equals(existing.LeaseToken, leaseToken, StringComparison.Ordinal)
                    || !existing.LeaseExpiresAt.HasValue || existing.LeaseExpiresAt.Value <= now)
                {
                    return existing;
                }
                renewed = true;
                return existing with
                {
                    LeaseExpiresAt = now + leaseDuration,
                    UpdatedAt = now
                };
            });
        return ValueTask.FromResult(renewed);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> RenewHeartbeatBatchAsync(
        IReadOnlyList<ToolReconciliationHeartbeat> heartbeats,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (heartbeats.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        // 与单条 RenewLeaseAsync 同语义：token 匹配 + Running + 未过期才续约。
        var now = DateTimeOffset.UtcNow;
        var failed = new List<string>(heartbeats.Count);
        foreach (var heartbeat in heartbeats)
        {
            var renewed = false;
            _records.AddOrUpdate(
                heartbeat.ReconciliationId,
                _ => throw new InvalidOperationException($"对账记录不存在：{heartbeat.ReconciliationId}"),
                (_, existing) =>
                {
                    if (existing.Status != ToolReconciliationStatus.Running
                        || !string.Equals(existing.LeaseToken, heartbeat.LeaseToken, StringComparison.Ordinal)
                        || !existing.LeaseExpiresAt.HasValue || existing.LeaseExpiresAt.Value <= now)
                    {
                        return existing;
                    }
                    renewed = true;
                    return existing with
                    {
                        LeaseExpiresAt = now + leaseDuration,
                        UpdatedAt = now
                    };
                });
            if (!renewed)
            {
                failed.Add(heartbeat.ReconciliationId);
            }
        }
        return ValueTask.FromResult<IReadOnlyList<string>>(failed);
    }

    /// <inheritdoc />
    public ValueTask<bool> TryResetToPendingAsync(
        string reconciliationId,
        string leaseToken,
        string? lastError,
        TimeSpan? retryDelay,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var updated = false;
        _records.AddOrUpdate(
            reconciliationId,
            _ => throw new InvalidOperationException($"对账记录不存在：{reconciliationId}"),
            (_, existing) =>
            {
                // P0-4：仅持有有效租约的 Running 记录可回退。
                if (existing.Status != ToolReconciliationStatus.Running
                    || !string.Equals(existing.LeaseToken, leaseToken, StringComparison.Ordinal)
                    || !existing.LeaseExpiresAt.HasValue || existing.LeaseExpiresAt.Value <= now)
                {
                    return existing;
                }
                updated = true;
                return existing with
                {
                    Status = ToolReconciliationStatus.Pending,
                    LeaseOwner = null,
                    LeaseToken = null,
                    LeaseExpiresAt = null,
                    NextAttemptAt = retryDelay.HasValue ? now + retryDelay.Value : null,
                    LastError = lastError,
                    UpdatedAt = now
                };
            });
        return ValueTask.FromResult(updated);
    }

    /// <inheritdoc />
    public ValueTask<bool> MarkResolvedAsync(
        string reconciliationId,
        string leaseToken,
        ToolReconciliationOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(MarkTerminal(reconciliationId, leaseToken, ToolReconciliationStatus.Resolved, outcome));
    }

    /// <inheritdoc />
    public ValueTask<bool> MarkRejectedAsync(
        string reconciliationId,
        string leaseToken,
        ToolReconciliationOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(MarkTerminal(reconciliationId, leaseToken, ToolReconciliationStatus.Rejected, outcome));
    }

    /// <inheritdoc />
    public async ValueTask<ToolReconciliationResolution> ResolveReconciliationAtomicallyAsync(
        string workspaceId,
        string runId,
        string requestId,
        string leaseToken,
        long expectedReconciliationVersion,
        ToolReconciliationOutcome outcome,
        DurableToolResult durableResult,
        CancellationToken cancellationToken = default,
        string? decisionRequestId = null)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(durableResult);
        cancellationToken.ThrowIfCancellationRequested();

        // 锁等价：进程内单门串行化所有对账原子裁决——journal 推进、结果 UPSERT、
        // 记录终态、Run 推进、审计事件追加在同一临界区内完成（P0-3 不撕裂）。
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = _records.Values.FirstOrDefault(r =>
                string.Equals(r.WorkspaceId, workspaceId, StringComparison.Ordinal)
                && string.Equals(r.RunId, runId, StringComparison.Ordinal)
                && string.Equals(r.RequestId, requestId, StringComparison.Ordinal));
            if (record is null)
            {
                return new ToolReconciliationResolution { Status = ToolReconciliationResolutionStatus.NotFound };
            }
            if (record.Status is ToolReconciliationStatus.Resolved or ToolReconciliationStatus.Rejected)
            {
                // 客户端决策幂等——相同 DecisionRequestId 重试：outcome 一致 → 幂等成功；
                // 相反 outcome → 决策冲突；无/不同决策身份 → AlreadyTerminal。
                if (!string.IsNullOrWhiteSpace(decisionRequestId)
                    && string.Equals(record.DecisionRequestId, decisionRequestId, StringComparison.Ordinal))
                {
                    var resolutionStatus = record.SideEffectOccurred == outcome.SideEffectOccurred
                        ? ToolReconciliationResolutionStatus.Resolved
                        : ToolReconciliationResolutionStatus.DecisionConflict;
                    return new ToolReconciliationResolution { Status = resolutionStatus, Record = record };
                }
                return new ToolReconciliationResolution { Status = ToolReconciliationResolutionStatus.AlreadyTerminal };
            }

            // 验证唯一裁决者（P0-5）：租约匹配 + 未过期 + fencing 版本一致。
            var now = DateTimeOffset.UtcNow;
            if (!string.Equals(record.LeaseToken, leaseToken, StringComparison.Ordinal)
                || !record.LeaseExpiresAt.HasValue || record.LeaseExpiresAt.Value <= now)
            {
                return new ToolReconciliationResolution { Status = ToolReconciliationResolutionStatus.ArbitrationLost };
            }
            if (record.FencingToken != expectedReconciliationVersion)
            {
                return new ToolReconciliationResolution { Status = ToolReconciliationResolutionStatus.VersionMismatch };
            }

            // 1. Journal 状态分支：
            //    DispatchingIntent/Dispatched/Reconciling → 推进 Committed 并 UPSERT 结果；
            //    Committed/ResultDelivered → 幂等判定（指纹一致才允许，绝不覆盖）；
            //    Prepared/行缺失（已注入 journal 时）→ 记录标记损坏（Corrupted），不写结果、不终态化、不推进 Run。
            //    未注入 journal（测试简化配置）→ 跳过 journal 约束，保持既有行为。
            ToolDispatchState? journalState = null;
            var journalParticipates = _journal is not null;
            if (journalParticipates)
            {
                journalState = await _journal!.GetStateAsync(workspaceId, runId, requestId, cancellationToken).ConfigureAwait(false);
            }

            var incomingFingerprint = durableResult.ComputeFingerprint();
            bool writeResult = false;
            switch (journalState)
            {
                case ToolDispatchState.DispatchingIntent:
                case ToolDispatchState.Dispatched:
                    await _journal!.BeginReconciliationAsync(requestId, cancellationToken).ConfigureAwait(false);
                    await _journal.MarkReconciledWithResultAsync(requestId, durableResult, cancellationToken).ConfigureAwait(false);
                    writeResult = true;
                    break;

                case ToolDispatchState.Reconciling:
                    await _journal!.MarkReconciledWithResultAsync(requestId, durableResult, cancellationToken).ConfigureAwait(false);
                    writeResult = true;
                    break;

                case ToolDispatchState.Committed:
                case ToolDispatchState.ResultDelivered:
                {
                    var existing = _resultStore is not null
                        ? await _resultStore.GetByRequestIdAsync(requestId, cancellationToken).ConfigureAwait(false)
                        : null;
                    if (existing is null
                        || !string.Equals(existing.ComputeFingerprint(), incomingFingerprint, StringComparison.Ordinal))
                    {
                        var reason = existing is null
                            ? "Journal 已提交但 Durable Result 缺失，无法幂等确认，记录标记损坏。"
                            : "Journal 已提交且既有结果指纹与本次裁决不一致，拒绝覆盖，记录标记损坏。";
                        return MarkCorrupted(record, reason, now);
                    }
                    // 指纹一致 → 幂等成功（复用既有已交付结果，不覆盖）。
                    break;
                }

                case null when journalParticipates:
                case ToolDispatchState.Prepared:
                {
                    var reason = journalState is null
                        ? "Journal 行缺失，无法确认裁决前提，记录标记损坏。"
                        : "Journal 状态为 Prepared（外部副作用从未分派），无法裁决，记录标记损坏。";
                    return MarkCorrupted(record, reason, now);
                }

                case null:
                    // 未注入 journal（测试简化配置）：跳过 journal 约束，走结果写入与终态化。
                    break;
            }

            // 2. Durable Result UPSERT（愉快/重试路径）。
            if (writeResult && _resultStore is not null)
            {
                await _resultStore.SaveByRequestIdAsync(durableResult, cancellationToken).ConfigureAwait(false);
            }

            // 3. 记录终态 + 清除租约。
            var terminal = record with
            {
                Status = outcome.SideEffectOccurred ? ToolReconciliationStatus.Resolved : ToolReconciliationStatus.Rejected,
                SideEffectOccurred = outcome.SideEffectOccurred,
                Result = outcome.Result,
                Reason = outcome.Error,
                DecisionRequestId = decisionRequestId,
                UpdatedAt = now,
                ResolvedAt = now,
                LeaseOwner = null,
                LeaseToken = null,
                LeaseExpiresAt = null,
                NextAttemptAt = null,
                LastError = null
            };
            _records[record.ReconciliationId] = terminal;

            // 4. Run 状态推进（P0-3）：Run 处于停车状态且无其他未决对账记录 → Queued（同一临界区，原子）。
            AgentRunState? auditState = null;
            if (_runStore is not null)
            {
                var run = await _runStore.GetAsync(workspaceId, runId, cancellationToken).ConfigureAwait(false);
                if (run is not null)
                {
                    auditState = run.State;
                    var hasOtherUnresolved = _records.Values.Any(r =>
                        string.Equals(r.WorkspaceId, workspaceId, StringComparison.Ordinal)
                        && string.Equals(r.RunId, runId, StringComparison.Ordinal)
                        && !string.Equals(r.RequestId, requestId, StringComparison.Ordinal)
                        && (r.Status == ToolReconciliationStatus.Pending || r.Status == ToolReconciliationStatus.Running));
                    if (run.State is AgentRunState.AwaitingReconciliation or AgentRunState.ReconciliationRunning
                        && !hasOtherUnresolved)
                    {
                        try
                        {
                            await _runStore.TransitionStateAsync(workspaceId, runId, run.State, AgentRunState.Queued, cancellationToken).ConfigureAwait(false);
                        }
                        catch (InvalidOperationException)
                        {
                            // CAS 失败（Run 状态被并发推进）→ best-effort，不阻断裁决。
                        }
                    }
                }
            }

            // 5. 审计事件追加（ToolReconciliationResolved，与记录终态同一临界区）。
            if (_eventStore is not null)
            {
                await AppendReconciliationAuditEventAsync(
                    record, terminal, outcome, auditState, now, cancellationToken).ConfigureAwait(false);
            }

            return new ToolReconciliationResolution
            {
                Status = ToolReconciliationResolutionStatus.Resolved,
                Record = terminal
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 把记录标记为损坏（Corrupted）：清除租约与重试指针，写入损坏原因，不写结果、不终态化。
    /// </summary>
    private ToolReconciliationResolution MarkCorrupted(ToolReconciliationRecord record, string reason, DateTimeOffset now)
    {
        var corrupted = record with
        {
            Status = ToolReconciliationStatus.Corrupted,
            LastError = reason,
            UpdatedAt = now,
            LeaseOwner = null,
            LeaseToken = null,
            LeaseExpiresAt = null,
            NextAttemptAt = null
        };
        _records[record.ReconciliationId] = corrupted;
        return new ToolReconciliationResolution
        {
            Status = ToolReconciliationResolutionStatus.Corrupted,
            Record = corrupted
        };
    }

    /// <inheritdoc />
    public async ValueTask<int> RecoverParkedRunsAsync(int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit <= 0 || _runStore is null)
        {
            return 0;
        }

        // 进程内没有"全量枚举 Run"能力，从对账记录出发收集出现过记录的 Run
        // （Run 只有因存在对账记录才会进入停车状态，覆盖现实中的停车窗口）。
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var recovered = 0;
            var groups = _records.Values
                .GroupBy(r => (r.WorkspaceId, r.RunId))
                .Take(limit);
            foreach (var group in groups)
            {
                var ws = group.Key.WorkspaceId;
                var runId = group.Key.RunId;
                var run = await _runStore.GetAsync(ws, runId, cancellationToken).ConfigureAwait(false);
                if (run is null
                    || (run.State != AgentRunState.AwaitingReconciliation && run.State != AgentRunState.ReconciliationRunning))
                {
                    continue;
                }
                var hasUnresolved = _records.Values.Any(r =>
                    string.Equals(r.WorkspaceId, ws, StringComparison.Ordinal)
                    && string.Equals(r.RunId, runId, StringComparison.Ordinal)
                    && (r.Status == ToolReconciliationStatus.Pending || r.Status == ToolReconciliationStatus.Running));
                if (hasUnresolved)
                {
                    continue;
                }
                try
                {
                    await _runStore.TransitionStateAsync(ws, runId, run.State, AgentRunState.Queued, cancellationToken).ConfigureAwait(false);
                    recovered++;
                }
                catch (InvalidOperationException)
                {
                    // CAS 失败（Run 状态被并发推进）→ 跳过，不阻断扫描。
                }
            }
            return recovered;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>过期判定：设置了截止且未裁决（Pending/Running）且已超期。</summary>
    private static bool IsOverdue(ToolReconciliationRecord record, DateTimeOffset now)
        => record.DeadlineUtc.HasValue
           && (record.Status == ToolReconciliationStatus.Pending || record.Status == ToolReconciliationStatus.Running)
           && record.DeadlineUtc.Value < now;

    private bool MarkTerminal(string reconciliationId, string leaseToken, ToolReconciliationStatus target, ToolReconciliationOutcome outcome)
    {
        var now = DateTimeOffset.UtcNow;
        var updated = false;
        _records.AddOrUpdate(
            reconciliationId,
            _ => throw new InvalidOperationException($"对账记录不存在：{reconciliationId}"),
            (_, existing) =>
            {
                if (existing.Status == ToolReconciliationStatus.Resolved || existing.Status == ToolReconciliationStatus.Rejected)
                {
                    return existing; // 已裁决 → 幂等
                }
                // P0-4：裁决必须持有有效租约。
                if (!string.Equals(existing.LeaseToken, leaseToken, StringComparison.Ordinal)
                    || !existing.LeaseExpiresAt.HasValue || existing.LeaseExpiresAt.Value <= now)
                {
                    return existing;
                }
                updated = true;
                return existing with
                {
                    Status = target,
                    SideEffectOccurred = outcome.SideEffectOccurred,
                    Result = outcome.Result,
                    Reason = outcome.Error,
                    UpdatedAt = now,
                    ResolvedAt = now,
                    LeaseOwner = null,
                    LeaseToken = null,
                    LeaseExpiresAt = null,
                    NextAttemptAt = null,
                    LastError = null
                };
            });
        return updated;
    }

    /// <summary>
    /// 追加 ToolReconciliationResolved 审计事件：沿用事件哈希链契约
    /// （Sequence = 最后事件 + 1，PrevChainHash = 最后事件 ContentHash）。
    /// </summary>
    private async Task AppendReconciliationAuditEventAsync(
        ToolReconciliationRecord record,
        ToolReconciliationRecord terminal,
        ToolReconciliationOutcome outcome,
        AgentRunState? auditState,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var lastSequence = await _eventStore!.GetLastSequenceAsync(record.WorkspaceId, record.RunId, cancellationToken).ConfigureAwait(false);
        AgentRunEvent? last = null;
        if (lastSequence >= 0)
        {
            var tail = await _eventStore.ReadAsync(record.WorkspaceId, record.RunId, lastSequence, 1, cancellationToken).ConfigureAwait(false);
            last = tail.Count > 0 ? tail[0] : null;
        }

        var payload = JsonSerializer.Serialize(new
        {
            ReconciliationId = record.ReconciliationId,
            RequestId = record.RequestId,
            SideEffectOccurred = outcome.SideEffectOccurred,
            Result = outcome.Result,
            Error = outcome.Error
        });

        var auditEvent = AgentRunEventChain.BuildEvent(
            record.RunId,
            record.WorkspaceId,
            (last?.Sequence ?? -1) + 1,
            AgentRunEventType.ToolReconciliationResolved,
            auditState ?? AgentRunState.AwaitingReconciliation,
            payload,
            last?.ContentHash);

        await _eventStore.AppendAsync(auditEvent, cancellationToken).ConfigureAwait(false);
    }
}
