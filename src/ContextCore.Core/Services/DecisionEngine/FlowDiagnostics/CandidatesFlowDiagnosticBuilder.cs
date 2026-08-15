using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.DecisionEngine.FlowDiagnostics;

/// <summary>
/// 候选流诊断构建器：纯函数，从一次请求的执行结果 + 可选数据集期望
/// 产出漏失归因报告。无 I/O、无随机性，同一输入重复构建逐位一致。
/// 只消费 ID/通道/结局/分数/token，绝不读取正文。
/// </summary>
public static class CandidatesFlowDiagnosticBuilder
{
    private static readonly HashSet<CandidateDecisionReasonCode> GateReasons = new()
    {
        CandidateDecisionReasonCode.LifecycleBlocked,
        CandidateDecisionReasonCode.DeprecatedBlocked,
        CandidateDecisionReasonCode.RequiredTagMismatch,
        CandidateDecisionReasonCode.DuplicateSuppressed,
        CandidateDecisionReasonCode.SupersededByCurrentVersion,
        CandidateDecisionReasonCode.DeprecatedUsedByActiveChain,
        CandidateDecisionReasonCode.EvidenceMissing,
        CandidateDecisionReasonCode.DuplicateSectionReference
    };

    private static readonly HashSet<CandidateDecisionReasonCode> BudgetReasons = new()
    {
        CandidateDecisionReasonCode.TokenBudgetExceeded,
        CandidateDecisionReasonCode.SectionQuotaExceeded,
        CandidateDecisionReasonCode.PartiallyAcceptedDueToTruncation
    };

    /// <summary>
    /// 构建报告。
    /// </summary>
    /// <param name="request">运行时请求（提供 excluded/required/seed 语义与预算）。</param>
    /// <param name="result">执行结果（Provider 快照 + 工作集 + 决策）。</param>
    /// <param name="requiredEvidenceIds">可选的数据集期望 Required 证据 ID（漏失归因）。</param>
    /// <param name="forbiddenEvidenceIds">可选的数据集期望 Forbidden 证据 ID（禁止语义检查）。</param>
    public static CandidatesFlowDiagnostics Build(
        ContextDecisionRuntimeRequest request,
        ContextDecisionExecutionResult result,
        IReadOnlyList<string>? requiredEvidenceIds = null,
        IReadOnlyList<string>? forbiddenEvidenceIds = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);

        // 1. 通道 → 候选 ID 映射（含失败通道与每通道分数）。
        var channelCandidates = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var failedChannels = new HashSet<string>(StringComparer.Ordinal);
        var scoresById = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        var producedById = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var snapshot in result.ProviderOutputSnapshots)
        {
            var channel = snapshot.Kind.ToString();
            channelCandidates[channel] = snapshot.Envelopes
                .Select(e => e.CandidateId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var envelope in snapshot.Envelopes)
            {
                if (!producedById.TryGetValue(envelope.CandidateId, out var producedChannels))
                {
                    producedChannels = new HashSet<string>(StringComparer.Ordinal);
                    producedById[envelope.CandidateId] = producedChannels;
                }
                producedChannels.Add(channel);
                if (!scoresById.TryGetValue(envelope.CandidateId, out var scores))
                {
                    scores = new List<double>();
                    scoresById[envelope.CandidateId] = scores;
                }
                scores.Add(envelope.Utility.DeterministicScore);
            }
        }
        foreach (var report in result.ProviderReports)
        {
            if (!report.Succeeded)
            {
                failedChannels.Add(report.Kind.ToString());
            }
        }

        // 2. 决策分类。
        var selectedIds = result.Decision.SelectedEnvelopes
            .Select(e => e.CandidateId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var finalScores = result.Decision.SelectedEnvelopes
            .ToDictionary(e => e.CandidateId, e => e.Utility.FinalScore, StringComparer.OrdinalIgnoreCase);
        var droppedById = result.Decision.DroppedEnvelopes
            .ToDictionary(e => e.CandidateId, e => e.Safety.BlockReasonCode, StringComparer.OrdinalIgnoreCase);
        var tokenById = result.Decision.SelectedEnvelopes
            .Concat(result.Decision.DroppedEnvelopes)
            .Where(e => e.TokenCost is not null)
            .GroupBy(e => e.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().TokenCost!.ContentTokens, StringComparer.OrdinalIgnoreCase);

        var candidateIds = producedById.Keys
            .Concat(selectedIds)
            .Concat(droppedById.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var candidates = candidateIds
            .Select(id => new CandidateOutcomeDiagnostic
            {
                CandidateId = id,
                Channels = producedById.TryGetValue(id, out var ch) ? ch.OrderBy(c => c, StringComparer.Ordinal).ToArray() : Array.Empty<string>(),
                Outcome = Classify(id, selectedIds, droppedById),
                ReasonCode = droppedById.TryGetValue(id, out var reason) && reason != CandidateDecisionReasonCode.Unknown
                    ? reason.ToString()
                    : null,
                FinalScore = finalScores.TryGetValue(id, out var score) ? score : null,
                TokenCost = tokenById.TryGetValue(id, out var tokens) ? tokens : null
            })
            .ToArray();

        // 3. 通道命中摘要。
        var allProduced = producedById.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var channels = result.ProviderOutputSnapshots
            .Select(s => s.Kind.ToString())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .Select(channel =>
            {
                var produced = channelCandidates.TryGetValue(channel, out var set) ? set : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var unique = produced.Count(id => producedById[id].Count == 1);
                var selected = produced.Count(id => selectedIds.Contains(id));
                return new ChannelHitSummary { Channel = channel, Produced = produced.Count, Unique = unique, Selected = selected };
            })
            .ToArray();

        // 4. 重复候选与跨通道分数。
        var duplicates = producedById
            .Where(kv => kv.Value.Count > 1)
            .Select(kv =>
            {
                var scores = scoresById[kv.Key];
                return new DuplicateCandidateDiagnostic
                {
                    CandidateId = kv.Key,
                    Channels = kv.Value.OrderBy(c => c, StringComparer.Ordinal).ToArray(),
                    ScoreMin = scores.Min(),
                    ScoreMax = scores.Max()
                };
            })
            .OrderBy(d => d.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // 5. held / excluded / required / forbidden 语义检查。
        var violations = new List<SemanticsViolation>();
        var excludedIds = request.RetrievalInput?.ExcludedIds ?? Array.Empty<string>();
        var requiredIds = request.RetrievalInput?.RequiredIds ?? Array.Empty<string>();
        var excludedSet = new HashSet<string>(excludedIds, StringComparer.OrdinalIgnoreCase);
        var requiredSet = new HashSet<string>(requiredIds, StringComparer.OrdinalIgnoreCase);

        foreach (var id in allProduced.Intersect(excludedSet, StringComparer.OrdinalIgnoreCase))
        {
            violations.Add(new SemanticsViolation
            {
                Kind = "excluded-in-candidates",
                EvidenceId = id,
                Detail = "排除 ID 仍出现在候选流中（排除语义被破坏）。"
            });
        }
        foreach (var id in requiredSet.Intersect(excludedSet, StringComparer.OrdinalIgnoreCase))
        {
            violations.Add(new SemanticsViolation
            {
                Kind = "required-excluded",
                EvidenceId = id,
                Detail = "必需证据同时出现在排除列表（矛盾语义）。"
            });
        }

        var heldIds = request.SeedCandidates
            .Select(e => e.CandidateId)
            .Concat(request.SeedWorkingSet?.Envelopes.Select(e => e.CandidateId) ?? Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var workingSetIds = result.WorkingSet.Envelopes
            .Select(e => e.CandidateId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in heldIds)
        {
            if (!workingSetIds.Contains(id))
            {
                violations.Add(new SemanticsViolation
                {
                    Kind = "held-missing",
                    EvidenceId = id,
                    Detail = "种子/持有证据不在工作集中（持有语义被破坏）。"
                });
            }
            else if (droppedById.TryGetValue(id, out var reason))
            {
                violations.Add(new SemanticsViolation
                {
                    Kind = "held-dropped",
                    EvidenceId = id,
                    Detail = $"种子/持有证据被丢弃：{reason}。"
                });
            }
        }

        // 6. 期望证据漏失归因（数据集样本提供时；未提供则用请求级 RequiredIds）。
        var requiredEvidence = Array.Empty<EvidenceAttributionDiagnostic>();
        var effectiveRequiredIds = requiredEvidenceIds ?? (request.RetrievalInput?.RequiredIds ?? Array.Empty<string>());
        if (effectiveRequiredIds.Count > 0)
        {
            requiredEvidence = effectiveRequiredIds
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .Select(id => new EvidenceAttributionDiagnostic
                {
                    EvidenceId = id,
                    Role = "required",
                    Outcome = ClassifyEvidence(id, excludedSet, selectedIds, droppedById, producedById, failedChannels),
                    Channels = producedById.TryGetValue(id, out var ch) ? ch.OrderBy(c => c, StringComparer.Ordinal).ToArray() : Array.Empty<string>(),
                    ReasonCode = droppedById.TryGetValue(id, out var reason) && reason != CandidateDecisionReasonCode.Unknown
                        ? reason.ToString()
                        : null
                })
                .ToArray();

            if (forbiddenEvidenceIds is not null)
            {
                foreach (var forbiddenId in forbiddenEvidenceIds
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .Where(selectedIds.Contains))
                {
                    violations.Add(new SemanticsViolation
                    {
                        Kind = "forbidden-selected",
                        EvidenceId = forbiddenId,
                        Detail = "禁止证据被选入结果（禁止语义被破坏）。"
                    });
                }
            }
        }

        // 7. selected hydration 成本。
        var selected = result.Decision.SelectedEnvelopes.ToArray();
        var hydrationTokens = selected
            .Where(e => e.TokenCost is not null)
            .Sum(e => e.TokenCost!.ContentTokens);
        var finalCost = result.FinalTokenCost;
        var hydration = new SelectedHydrationCost
        {
            SelectedCount = selected.Length,
            EstimatedTokens = hydrationTokens,
            FinalTotalTokens = finalCost?.TotalTokens,
            WithinBudget = finalCost?.WithinBudget ?? true,
            BudgetLimit = finalCost?.BudgetLimit
        };

        return new CandidatesFlowDiagnostics
        {
            RequestId = request.RequestId,
            QueryText = request.QueryText,
            Purpose = request.Purpose.ToString(),
            TokenBudget = request.TokenBudget,
            TopK = request.TopK,
            IsDegraded = result.IsDegraded,
            CandidateCount = candidateIds.Length,
            SelectedCount = selected.Length,
            DroppedCount = result.Decision.DroppedEnvelopes.Count,
            Channels = channels,
            Candidates = candidates,
            RequiredEvidence = requiredEvidence,
            Duplicates = duplicates,
            Violations = violations,
            Hydration = hydration,
            CreatedAt = result.Decision.DecidedAt
        };
    }

    private static CandidateFlowOutcome Classify(
        string candidateId,
        HashSet<string> selectedIds,
        IReadOnlyDictionary<string, CandidateDecisionReasonCode> droppedById)
    {
        if (selectedIds.Contains(candidateId))
        {
            return CandidateFlowOutcome.Selected;
        }
        if (droppedById.TryGetValue(candidateId, out var reason))
        {
            return ClassifyReason(reason);
        }
        return CandidateFlowOutcome.Unknown;
    }

    private static CandidateFlowOutcome ClassifyEvidence(
        string evidenceId,
        HashSet<string> excludedSet,
        HashSet<string> selectedIds,
        IReadOnlyDictionary<string, CandidateDecisionReasonCode> droppedById,
        IReadOnlyDictionary<string, HashSet<string>> producedById,
        HashSet<string> failedChannels)
    {
        if (excludedSet.Contains(evidenceId))
        {
            return CandidateFlowOutcome.ExcludedContradiction;
        }
        if (selectedIds.Contains(evidenceId))
        {
            return CandidateFlowOutcome.Selected;
        }
        if (droppedById.TryGetValue(evidenceId, out var reason))
        {
            return ClassifyReason(reason);
        }
        if (producedById.ContainsKey(evidenceId))
        {
            return CandidateFlowOutcome.Unknown;
        }
        // 未产出：区分「未生成」（应产出的通道失败）与「未召回」（通道正常但没召回）。
        if (failedChannels.Count > 0)
        {
            return CandidateFlowOutcome.NotGenerated;
        }
        return CandidateFlowOutcome.NotRecalled;
    }

    private static CandidateFlowOutcome ClassifyReason(CandidateDecisionReasonCode reason)
    {
        if (GateReasons.Contains(reason))
        {
            return CandidateFlowOutcome.GateDropped;
        }
        if (BudgetReasons.Contains(reason))
        {
            return CandidateFlowOutcome.BudgetCut;
        }
        if (reason == CandidateDecisionReasonCode.ScoreBelowThreshold)
        {
            return CandidateFlowOutcome.RankedTooLow;
        }
        return CandidateFlowOutcome.GateDropped;
    }
}
