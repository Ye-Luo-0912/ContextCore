using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.MemoryEvolution;

/// <summary>
/// / Utility Ledger Materializer。异步批量物化 ContextDecisionResult
/// 中的 SelectedEnvelopes + DroppedEnvelopes 为 UtilityLedgerEntry 条目，
/// 并检测冲突候选生成 ConflictSet。
/// </summary>
/// <remarks>
/// 设计原则（对齐澄清 #4 + R29 学习闭环）：
///   1. Materializer 是写入边界：通过 <see cref="IUtilityLedger.AppendEntriesAsync"/> /
///      <see cref="IConflictSetLedger.AppendConflictSetsAsync"/> 异步批量写入。
///   2. Materializer 依赖 <see cref="IUtilityLedger"/> + <see cref="IConflictSetLedger"/> 抽象，
///      生产路径注入 Postgres 实现，开发 / 测试路径注入 InMemory 实现 — 无需修改 materializer 代码。
///   3. Store 的读 API 仍是 read-only；写入只通过 materializer 触发。
///   4. P8 硬边界：所有 candidate（selected/dropped）都写入 ledger，
///      避免"dropped 视为负样本"的简化。
///   5. ConflictSet 检测规则（对齐澄清 #7）：
///      - Duplicate：envelope.Safety.IsDuplicate = true 的候选
///      - SectionConflict：envelope.Safety.BlockReasonCode = SectionQuotaExceeded
///      - BudgetConflict：envelope.Safety.BlockReasonCode = TokenBudgetExceeded
///      - 同 DecisionId 内若多个 envelope 命中同一 kind，组成一个 ConflictSet
/// </remarks>
public sealed class UtilityLedgerMaterializer
{
    private readonly IUtilityLedger _ledgerStore;
    private readonly IConflictSetLedger _conflictSetStore;
    private readonly IWriteTransactionScopeFactory? _transactionScopeFactory;
    private readonly TimeProvider? _timeProvider;

    public UtilityLedgerMaterializer(
        IUtilityLedger ledgerStore,
        IConflictSetLedger conflictSetStore,
        TimeProvider? timeProvider = null)
        : this(ledgerStore, conflictSetStore, transactionScopeFactory: null, timeProvider)
    {
    }

    /// <summary>
    /// 构造 Materializer，可选注入 <paramref name="transactionScopeFactory"/>。
    /// 当 factory 非 null 且两个 store 均实现事务能力接口时，<see cref="MaterializeAsync"/>
    /// 会在同一事务内提交 ledger + ConflictSet，避免一边成功、一边失败。
    /// </summary>
    /// <param name="ledgerStore">Utility Ledger 写入边界。</param>
    /// <param name="conflictSetStore">ConflictSet 写入边界。</param>
    /// <param name="transactionScopeFactory">跨 store 事务作用域工厂（null = 非事务回退路径）。</param>
    /// <param name="timeProvider">时间提供者（测试可注入；null = <see cref="DateTimeOffset.UtcNow"/>）。</param>
    public UtilityLedgerMaterializer(
        IUtilityLedger ledgerStore,
        IConflictSetLedger conflictSetStore,
        IWriteTransactionScopeFactory? transactionScopeFactory,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(ledgerStore);
        ArgumentNullException.ThrowIfNull(conflictSetStore);
        _ledgerStore = ledgerStore;
        _conflictSetStore = conflictSetStore;
        _transactionScopeFactory = transactionScopeFactory;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// 物化 ContextDecisionResult：写入 ledger 条目 + ConflictSet。
    /// </summary>
    /// <param name="result">决策结果（必填 SelectedEnvelopes + DroppedEnvelopes）。</param>
    /// <param name="workspaceId">workspace 作用域（默认从 envelope 提取）。</param>
    /// <param name="collectionId">collection 作用域（默认从 envelope 提取）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入的 ledger 条目数 + ConflictSet 数。</returns>
    public async Task<UtilityLedgerMaterializationResult> MaterializeAsync(
        ContextDecisionResult result,
        string? workspaceId = null,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();

        var now = Now();
        var batchId = "batch-" + Guid.NewGuid().ToString("N");
        var decisionId = result.RequestId;
        var policyVersion = result.PolicyVersion;
        // result 不直接携带 RouterId；materializer 不强制填充，留 null
        string? routerId = null;

        var entries = new List<UtilityLedgerEntry>();
        var envelopes = new List<(ContextCandidateEnvelope Envelope, bool IsSelected)>();

        foreach (var envelope in result.SelectedEnvelopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(BuildEntry(
                envelope, isSelected: true, decisionId, policyVersion, routerId, workspaceId, collectionId, now, batchId));
            envelopes.Add((envelope, true));
        }

        foreach (var envelope in result.DroppedEnvelopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(BuildEntry(
                envelope, isSelected: false, decisionId, policyVersion, routerId, workspaceId, collectionId, now, batchId));
            envelopes.Add((envelope, false));
        }

        // 检测 ConflictSet（先构建，便于事务路径一并提交）
        var conflictSets = DetectConflictSets(envelopes, decisionId, workspaceId, collectionId, now, batchId);

        // 事务路径：当两个 store 均实现 ITransactionalUtilityLedger / ITransactionalConflictSetLedger
        // 且注入了 IWriteTransactionScopeFactory 时，在同一事务内提交 ledger + ConflictSet，
        // 避免一边成功、一边失败导致的数据不一致。
        if (_transactionScopeFactory is not null
            && _ledgerStore is ITransactionalUtilityLedger txLedger
            && _conflictSetStore is ITransactionalConflictSetLedger txConflict)
        {
            await using var scope = await _transactionScopeFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await txLedger.AppendEntriesAsync(entries, scope, cancellationToken).ConfigureAwait(false);
                await txConflict.AppendConflictSetsAsync(conflictSets, scope, cancellationToken).ConfigureAwait(false);
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await scope.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        else
        {
            // 回退路径：非事务（InMemory/FileSystem 或未注入工厂）—— 保持原语义。
            await _ledgerStore.AppendEntriesAsync(entries, cancellationToken).ConfigureAwait(false);
            await _conflictSetStore.AppendConflictSetsAsync(conflictSets, cancellationToken).ConfigureAwait(false);
        }

        return new UtilityLedgerMaterializationResult(
            LedgerEntryCount: entries.Count,
            ConflictSetCount: conflictSets.Count);
    }

    private static UtilityLedgerEntry BuildEntry(
        ContextCandidateEnvelope envelope,
        bool isSelected,
        string decisionId,
        string policyVersion,
        string? routerId,
        string? workspaceId,
        string? collectionId,
        DateTimeOffset materializedAt,
        string batchId)
    {
        var expert = CandidateSourceExpertMapper.MapToExpert(envelope.Source);
        var utility = envelope.Utility;
        var dropReasonCode = isSelected
            ? null
            : (envelope.Safety.BlockReasonCode != CandidateDecisionReasonCode.Unknown
                ? envelope.Safety.BlockReasonCode.ToString()
                : null);

        // 稳定幂等键：hash(decision_id + candidate_id + expert + materialization_version)。
        // materialization_version = PolicyVersion（每次策略升级产生新条目，同版本内重试幂等）。
        // ON CONFLICT(entry_id) DO UPDATE 在重试 / 重复物化时覆盖而非插入重复行。
        var entryId = BuildStableEntryId(decisionId, envelope.CandidateId, expert, policyVersion);

        return new UtilityLedgerEntry
        {
            EntryId = entryId,
            WorkspaceId = workspaceId ?? envelope.WorkspaceId,
            CollectionId = collectionId ?? envelope.CollectionId,
            CandidateItemId = envelope.CandidateId,
            Expert = expert,
            UtilityContribution = ComputeUtilityContribution(envelope),
            DeterministicScore = utility.DeterministicScore,
            ModelScore = utility.ModelScore,
            FinalScore = utility.FinalScore,
            IsSelected = isSelected,
            DropReasonCode = dropReasonCode,
            DecisionId = decisionId,
            PolicyVersion = policyVersion,
            RouterId = routerId,
            MaterializedAt = materializedAt,
            MaterializationBatchId = batchId
        };
    }

    /// <summary>
    /// 计算稳定的 ledger EntryId：基于 (decisionId, candidateId, expert, policyVersion) 的 SHA-256 哈希。
    /// 相同 Decision 重试 / 重复物化时产生相同 EntryId，配合 ON CONFLICT(entry_id) 实现业务幂等。
    /// </summary>
    private static string BuildStableEntryId(
        string decisionId,
        string candidateId,
        RetrievalExpert expert,
        string policyVersion)
    {
        // 使用 '|' 分隔避免字段拼接歧义（与 PackageRequestFingerprintBuilder 一致）。
        var canonical = $"{decisionId}|{candidateId}|{expert}|{policyVersion}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "ledger-" + Convert.ToHexString(bytes);
    }

    /// <summary>
    /// 计算稳定的 ConflictSetId：基于 (decisionId, kind, 排序后的 candidateIds) 的 SHA-256 哈希。
    /// 同一 Decision 内同 kind 的候选组重试时产生相同 ConflictSetId，配合 ON CONFLICT(conflict_set_id) 实现幂等。
    /// </summary>
    private static string BuildStableConflictSetId(
        string decisionId,
        ConflictSetKind kind,
        IReadOnlyList<string> candidateItemIds)
    {
        // 排序后拼接，避免候选顺序差异导致不同 ID。
        var sortedIds = candidateItemIds.OrderBy(id => id, StringComparer.Ordinal);
        var canonical = $"{decisionId}|{kind}|{string.Join(",", sortedIds)}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "conflict-" + Convert.ToHexString(bytes);
    }

    /// <summary>
    /// 计算 Expert 对该 candidate 的 utility 贡献比例（0.0-1.0）。
    /// 若 envelope 的 ScoreBreakdown 含本 Expert 维度，使用该维度 / sum；
    /// 否则贡献 = 1.0（单一来源）。
    /// </summary>
    private static double ComputeUtilityContribution(ContextCandidateEnvelope envelope)
    {
        var expert = CandidateSourceExpertMapper.MapToExpert(envelope.Source);
        if (expert == RetrievalExpert.Unknown)
        {
            return 1.0; // GlobalContext / RelatedContext / Unknown：单一来源
        }

        var breakdown = envelope.Features.ScoreBreakdown;
        if (breakdown.Count == 0)
        {
            return 1.0; // 无 breakdown 数据：单一来源
        }

        var expertKey = expert.ToString().ToLowerInvariant();
        var sum = breakdown.Values.Sum();
        if (sum <= 0)
        {
            return 0.0;
        }

        return breakdown.TryGetValue(expertKey, out var expertScore)
            ? expertScore / sum
            : 0.0; // Expert 未贡献分数（仍写入条目，便于 ablation 分析）
    }

    private static List<ConflictSet> DetectConflictSets(
        List<(ContextCandidateEnvelope Envelope, bool IsSelected)> envelopes,
        string decisionId,
        string? workspaceId,
        string? collectionId,
        DateTimeOffset materializedAt,
        string batchId)
    {
        var sets = new List<ConflictSet>();
        var ws = workspaceId ?? envelopes.FirstOrDefault().Envelope.WorkspaceId;
        var col = collectionId ?? envelopes.FirstOrDefault().Envelope.CollectionId;

        // Duplicate：envelope.Safety.IsDuplicate = true
        var duplicates = envelopes.Where(t => t.Envelope.Safety.IsDuplicate).ToList();
        if (duplicates.Count >= 2)
        {
            sets.Add(BuildConflictSet(
                ConflictSetKind.Duplicate, duplicates, decisionId, ws, col, materializedAt, batchId));
        }

        // SectionConflict：BlockReasonCode = SectionQuotaExceeded
        var sectionConflicts = envelopes
            .Where(t => t.Envelope.Safety.BlockReasonCode == CandidateDecisionReasonCode.SectionQuotaExceeded)
            .ToList();
        if (sectionConflicts.Count >= 2)
        {
            sets.Add(BuildConflictSet(
                ConflictSetKind.SectionConflict, sectionConflicts, decisionId, ws, col, materializedAt, batchId));
        }

        // BudgetConflict：BlockReasonCode = TokenBudgetExceeded
        var budgetConflicts = envelopes
            .Where(t => t.Envelope.Safety.BlockReasonCode == CandidateDecisionReasonCode.TokenBudgetExceeded)
            .ToList();
        if (budgetConflicts.Count >= 2)
        {
            sets.Add(BuildConflictSet(
                ConflictSetKind.BudgetConflict, budgetConflicts, decisionId, ws, col, materializedAt, batchId));
        }

        // SameItemMultipleSources：同 CandidateId 来自多个不同 Source
        var byCandidateId = envelopes
            .GroupBy(t => t.Envelope.CandidateId)
            .Where(g => g.Select(t => t.Envelope.Source).Distinct().Count() >= 2)
            .ToList();
        foreach (var group in byCandidateId)
        {
            sets.Add(BuildConflictSet(
                ConflictSetKind.SameItemMultipleSources, group.ToList(), decisionId, ws, col, materializedAt, batchId));
        }

        return sets;
    }

    private static ConflictSet BuildConflictSet(
        ConflictSetKind kind,
        List<(ContextCandidateEnvelope Envelope, bool IsSelected)> envelopes,
        string decisionId,
        string workspaceId,
        string collectionId,
        DateTimeOffset materializedAt,
        string batchId)
    {
        var entries = envelopes.Select(t => new ConflictSetEntry
        {
            CandidateItemId = t.Envelope.CandidateId,
            Expert = CandidateSourceExpertMapper.MapToExpert(t.Envelope.Source),
            Score = t.Envelope.Utility.FinalScore,
            IsSelected = t.IsSelected,
            DropReasonCode = t.IsSelected
                ? null
                : (t.Envelope.Safety.BlockReasonCode != CandidateDecisionReasonCode.Unknown
                    ? t.Envelope.Safety.BlockReasonCode.ToString()
                    : null),
            ReasonDetail = t.Envelope.Safety.BlockReasonDetail
        }).ToList();

        var resolvedItemId = entries.FirstOrDefault(e => e.IsSelected)?.CandidateItemId;
        var resolutionStatus = resolvedItemId is not null
            ? ConflictResolutionStatus.AutoResolved
            : ConflictResolutionStatus.Unresolved;
        var chosenAuthority = resolvedItemId is not null
            ? "highest-score"
            : null;

        // 稳定幂等键：hash(decision_id + kind + 排序后的 candidate_ids)。
        // 相同 Decision 重试 / 重复物化时产生相同 ConflictSetId，配合 ON CONFLICT(conflict_set_id) 实现业务幂等。
        var candidateItemIds = entries.Select(e => e.CandidateItemId).ToList();
        var conflictSetId = BuildStableConflictSetId(decisionId, kind, candidateItemIds);

        return new ConflictSet
        {
            ConflictSetId = conflictSetId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Kind = kind,
            Entries = entries,
            DecisionId = decisionId,
            ResolvedItemId = resolvedItemId,
            ResolutionStatus = resolutionStatus,
            ChosenAuthority = chosenAuthority,
            ResolvedAt = resolvedItemId is not null ? materializedAt : null,
            MaterializedAt = materializedAt,
            MaterializationBatchId = batchId
        };
    }

    private DateTimeOffset Now() => _timeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow;
}

/// <summary>
/// UtilityLedgerMaterializer 物化结果。
/// </summary>
public sealed record UtilityLedgerMaterializationResult
{
    public int LedgerEntryCount { get; init; }
    public int ConflictSetCount { get; init; }

    public UtilityLedgerMaterializationResult(int LedgerEntryCount, int ConflictSetCount)
    {
        this.LedgerEntryCount = LedgerEntryCount;
        this.ConflictSetCount = ConflictSetCount;
    }
}
