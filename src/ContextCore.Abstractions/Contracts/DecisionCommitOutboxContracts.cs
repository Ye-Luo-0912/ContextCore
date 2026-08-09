using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

/// <summary>
/// 决策提交类型：Decision Commit 的物化意图。
/// </summary>
public enum DecisionCommitType : byte
{
    /// <summary>仅归档决策记录（Record durable，不触发 Learning 物化）。</summary>
    RecordOnly = 0,

    /// <summary>归档决策记录 + 触发 Learning 物化意图（Materialize）。</summary>
    RecordAndMaterialize = 1
}

/// <summary>
/// Decision Commit Outbox 记录：把"决策提交"（Decision Record + Evidence Manifest 引用 +
/// Learning Materialization Intent）作为一条 durable 消息入队，经 outbox 连成可靠链——
/// 决策记录落库失败 / 进程崩溃后由消费方重放，不丢决策、不丢物化意图。
/// </summary>
public sealed record DecisionCommitOutboxRecord
{
    /// <summary>outbox 行 ID。</summary>
    public long OutboxId { get; init; }

    /// <summary>决策 ID（= ContextDecisionResult.RequestId；稳定主键，幂等入队）。</summary>
    public required string DecisionId { get; init; }

    /// <summary>Workspace ID（隔离边界）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>Collection ID。</summary>
    public required string CollectionId { get; init; }

    /// <summary>提交类型（记录归档 + 物化意图）。</summary>
    public DecisionCommitType CommitType { get; init; } = DecisionCommitType.RecordAndMaterialize;

    /// <summary>决策记录（完整 payload；决策 Evidence Plane 的 durable 归档本体）。</summary>
    public required ContextDecisionRecord Record { get; init; }

    /// <summary>
    /// 证据引用（可选）：关联的检索计划签名 / 数据集快照 ID——Evidence Manifest
    /// 通过稳定键关联（决策 → 证据 → Learning 工件可重建）。
    /// </summary>
    public string? EvidenceRef { get; init; }

    /// <summary>入队时间（UTC）。</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>处理状态：0=待处理，1=已处理，2=处理中（租约），3=死信。</summary>
    public short State { get; init; }

    /// <summary>已尝试次数（达上限转死信）。</summary>
    public int Attempts { get; init; }

    /// <summary>租约 token（处理中 CAS 用）。</summary>
    public string? LeaseToken { get; init; }

    /// <summary>租约过期时间。</summary>
    public DateTimeOffset? LeaseExpiresAt { get; init; }

    /// <summary>最近一次失败原因（审计用）。</summary>
    public string? LastError { get; init; }
}

/// <summary>
/// Decision Commit Outbox 存储：决策提交消息的 durable 队列（outbox 模式）。
/// 入队幂等（同 (workspace_id, decision_id) 只保留一条）；消费方领取后
/// 执行"决策记录落库 + 物化意图"并 Ack——崩溃后未 Ack 的条目可重放。
/// </summary>
public interface IDecisionCommitOutbox
{
    /// <summary>入队决策提交（同 (workspace_id, decision_id) 幂等覆盖为待处理）。</summary>
    ValueTask EnqueueAsync(
        DecisionCommitOutboxRecord commit,
        CancellationToken cancellationToken = default);

    /// <summary>领取一批待处理条目（FOR UPDATE SKIP LOCKED + 租约；崩溃恢复可重放）。</summary>
    ValueTask<IReadOnlyList<DecisionCommitOutboxRecord>> AcquirePendingAsync(
        int limit,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>确认处理完成（CAS：租约匹配且未过期）。返回 false = 租约被抢占。</summary>
    ValueTask<bool> AckAsync(
        long outboxId,
        string leaseToken,
        CancellationToken cancellationToken = default);

    /// <summary>标记失败（租约匹配；未达上限保持待重试，达上限转死信）。</summary>
    ValueTask MarkFailedAsync(
        long outboxId,
        string leaseToken,
        string errorMessage,
        CancellationToken cancellationToken = default);
}
