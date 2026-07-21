using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Evolution;

// ===========================================================================
// R27-2：InMemoryPipelineRunStore — IPipelineRunStore 的 in-memory 实现。
//
// 目标（对齐 R27 规格）：
//   1. 实现 IPipelineRunStore 的 9 个方法（runs / canary / rollback / baseline）。
//   2. 仅 in-memory；进程重启后丢失；生产实现应替换为 PostgresPipelineRunStore。
//   3. 线程安全：ConcurrentDictionary + 按 proposal/run 维度分组查询。
//   4. 与 InMemoryAgentCheckpointStore 设计模式对齐（R23-3）。
// ===========================================================================

/// <summary>
/// R27-2：<see cref="IPipelineRunStore"/> 的 in-memory 实现。
/// </summary>
/// <remarks>
/// 适用于测试 / 演示 / 单机开发场景。生产场景需替换为持久化实现。
/// </remarks>
public sealed class InMemoryPipelineRunStore : IPipelineRunStore
{
    private readonly ConcurrentDictionary<string, PipelineRunSnapshot> _runs
        = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CanaryAssignment> _canaryAssignments
        = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RollbackRecord> _rollbackRecords
        = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BaselineComparison> _baselineComparisons
        = new(StringComparer.Ordinal);

    // ---------- Pipeline runs ----------

    /// <inheritdoc />
    public Task SaveRunAsync(PipelineRunSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        _runs[snapshot.RunId] = snapshot;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<PipelineRunSnapshot?> GetRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();
        _runs.TryGetValue(runId, out var snapshot);
        return Task.FromResult(snapshot);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PipelineRunSnapshot>> ListRunsByProposalAsync(
        string proposalId,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);
        if (take < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "take must be >= 0");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var list = _runs.Values
            .Where(r => string.Equals(r.ProposalId, proposalId, StringComparison.Ordinal))
            .OrderByDescending(r => r.UpdatedAt)
            .ThenByDescending(r => r.RunId)
            .Take(take == 0 ? int.MaxValue : take)
            .ToList();
        return Task.FromResult<IReadOnlyList<PipelineRunSnapshot>>(list);
    }

    /// <inheritdoc />
    public Task<bool> DeleteRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_runs.TryRemove(runId, out _));
    }

    // ---------- Canary assignments ----------

    /// <inheritdoc />
    public Task SaveCanaryAssignmentAsync(CanaryAssignment assignment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        cancellationToken.ThrowIfCancellationRequested();
        _canaryAssignments[assignment.AssignmentId] = assignment;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CanaryAssignment>> ListCanaryAssignmentsByRunAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();
        var list = _canaryAssignments.Values
            .Where(a => string.Equals(a.RunId, runId, StringComparison.Ordinal))
            .OrderBy(a => a.AssignedAt)
            .ThenBy(a => a.AssignmentId)
            .ToList();
        return Task.FromResult<IReadOnlyList<CanaryAssignment>>(list);
    }

    // ---------- Rollback records ----------

    /// <inheritdoc />
    public Task SaveRollbackRecordAsync(RollbackRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        _rollbackRecords[record.RecordId] = record;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<RollbackRecord?> GetRollbackRecordByRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();
        var record = _rollbackRecords.Values
            .FirstOrDefault(r => string.Equals(r.RunId, runId, StringComparison.Ordinal));
        return Task.FromResult(record);
    }

    // ---------- Baseline comparisons ----------

    /// <inheritdoc />
    public Task SaveBaselineComparisonAsync(BaselineComparison comparison, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        cancellationToken.ThrowIfCancellationRequested();
        _baselineComparisons[comparison.ComparisonId] = comparison;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<BaselineComparison>> ListBaselineComparisonsByProposalAsync(
        string proposalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);
        cancellationToken.ThrowIfCancellationRequested();
        var list = _baselineComparisons.Values
            .Where(c => string.Equals(c.ProposalId, proposalId, StringComparison.Ordinal))
            .OrderByDescending(c => c.ComparedAt)
            .ThenByDescending(c => c.ComparisonId)
            .ToList();
        return Task.FromResult<IReadOnlyList<BaselineComparison>>(list);
    }

    // ---------- 测试与诊断用 ----------

    /// <summary>当前 run snapshot 总数（测试与诊断用）。</summary>
    public int RunCount => _runs.Count;

    /// <summary>当前 canary assignment 总数（测试与诊断用）。</summary>
    public int CanaryAssignmentCount => _canaryAssignments.Count;

    /// <summary>当前 rollback record 总数（测试与诊断用）。</summary>
    public int RollbackRecordCount => _rollbackRecords.Count;

    /// <summary>当前 baseline comparison 总数（测试与诊断用）。</summary>
    public int BaselineComparisonCount => _baselineComparisons.Count;
}
