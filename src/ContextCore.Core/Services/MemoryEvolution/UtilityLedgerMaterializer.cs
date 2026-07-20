using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.MemoryEvolution;

/// <summary>
/// R21-3：Utility Ledger Materializer。异步批量物化 ContextDecisionResult
/// 中的 SelectedEnvelopes + DroppedEnvelopes 为 UtilityLedgerEntry 条目，
/// 并检测冲突候选生成 ConflictSet。
/// </summary>
/// <remarks>
/// 设计原则（对齐澄清 #4）：
///   1. Materializer 是写入边界：通过 InMemoryUtilityLedgerStore / InMemoryConflictSetStore
///      的 internal write API 异步批量写入。
///   2. Store 的公共 API 仍是 read-only；写入只通过 materializer 触发。
///   3. P8 硬边界：所有 candidate（selected/dropped）都写入 ledger，
///      避免"dropped 视为负样本"的简化。
///   4. ConflictSet 检测规则（对齐澄清 #7）：
///      - Duplicate：envelope.Safety.IsDuplicate = true 的候选
///      - SectionConflict：envelope.Safety.BlockReasonCode = SectionQuotaExceeded
///      - BudgetConflict：envelope.Safety.BlockReasonCode = TokenBudgetExceeded
///      - 同 DecisionId 内若多个 envelope 命中同一 kind，组成一个 ConflictSet
/// </remarks>
public sealed class UtilityLedgerMaterializer
{
    private readonly InMemoryUtilityLedgerStore _ledgerStore;
    private readonly InMemoryConflictSetStore _conflictSetStore;
    private readonly TimeProvider? _timeProvider;

    public UtilityLedgerMaterializer(
        InMemoryUtilityLedgerStore ledgerStore,
        InMemoryConflictSetStore conflictSetStore,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(ledgerStore);
        ArgumentNullException.ThrowIfNull(conflictSetStore);
        _ledgerStore = ledgerStore;
        _conflictSetStore = conflictSetStore;
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
    public Task<UtilityLedgerMaterializationResult> MaterializeAsync(
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

        _ledgerStore.AppendEntries(entries);

        // 检测 ConflictSet
        var conflictSets = DetectConflictSets(envelopes, decisionId, workspaceId, collectionId, now, batchId);
        _conflictSetStore.AppendConflictSets(conflictSets);

        return Task.FromResult(new UtilityLedgerMaterializationResult(
            LedgerEntryCount: entries.Count,
            ConflictSetCount: conflictSets.Count));
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

        return new UtilityLedgerEntry
        {
            EntryId = "ledger-" + Guid.NewGuid().ToString("N"),
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

        return new ConflictSet
        {
            ConflictSetId = "conflict-" + Guid.NewGuid().ToString("N"),
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Kind = kind,
            Entries = entries,
            DecisionId = decisionId,
            ResolvedItemId = resolvedItemId,
            MaterializedAt = materializedAt,
            MaterializationBatchId = batchId
        };
    }

    private DateTimeOffset Now() => _timeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow;
}

/// <summary>
/// R21-3：UtilityLedgerMaterializer 物化结果。
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
