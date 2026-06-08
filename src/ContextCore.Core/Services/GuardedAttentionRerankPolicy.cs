using ContextCore.Abstractions;

namespace ContextCore.Core.Services;

/// <summary>
/// Applies guarded attention rerank after packing. This policy may only reorder
/// already selected candidates and must never add or remove selected items.
/// </summary>
internal sealed class GuardedAttentionRerankPolicy
{
    private readonly RetrievalAttentionRerankOptions _options;

    public GuardedAttentionRerankPolicy(RetrievalAttentionRerankOptions? options = null)
    {
        _options = options ?? new RetrievalAttentionRerankOptions();
    }

    public GuardedAttentionRerankResult Apply(
        string operationId,
        ContextRetrievalRequest request,
        RetrievalPackingResult packingResult,
        IReadOnlyList<ContextAttentionScore> attentionScores)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(packingResult);
        ArgumentNullException.ThrowIfNull(attentionScores);

        var mode = _options.EffectiveMode;
        if (string.Equals(mode, RetrievalAttentionRerankOptions.OffMode, StringComparison.OrdinalIgnoreCase))
        {
            return Skipped(operationId, "off", packingResult, request);
        }

        var isShadow = string.Equals(mode, RetrievalAttentionRerankOptions.ShadowMode, StringComparison.OrdinalIgnoreCase);
        var isApplyGuarded = string.Equals(mode, RetrievalAttentionRerankOptions.ApplyGuardedMode, StringComparison.OrdinalIgnoreCase);
        if (!isShadow && !isApplyGuarded)
        {
            return Skipped(operationId, $"unsupported_mode:{mode}", packingResult, request);
        }

        if (!_options.PreserveSelectedSet || _options.AllowSelectedSetMutation)
        {
            return Skipped(operationId, "selected_set_mutation_not_allowed", packingResult, request);
        }

        if (packingResult.SelectedCandidates.Count <= 1)
        {
            return Skipped(operationId, "not_enough_selected_items", packingResult, request);
        }

        var attentionById = attentionScores
            .GroupBy(score => score.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (attentionById.Count == 0)
        {
            return Skipped(operationId, "attention_scores_unavailable", packingResult, request, attentionById);
        }

        var selected = packingResult.SelectedCandidates.ToArray();
        var selectedIds = selected
            .Select(candidate => candidate.CandidateId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedAttentionById = selected
            .Where(candidate => attentionById.ContainsKey(candidate.CandidateId))
            .ToDictionary(candidate => candidate.CandidateId, candidate => attentionById[candidate.CandidateId], StringComparer.OrdinalIgnoreCase);
        if (selectedAttentionById.Count == 0)
        {
            return Skipped(operationId, "selected_attention_scores_unavailable", packingResult, request, attentionById);
        }

        var proposed = selected
            .OrderByDescending(IsMandatory)
            .ThenBy(candidate => selectedAttentionById.TryGetValue(candidate.CandidateId, out var score)
                ? score.AttentionRank
                : ResolveCurrentRank(selected, candidate))
            .ThenByDescending(candidate => selectedAttentionById.TryGetValue(candidate.CandidateId, out var score)
                ? score.FinalAttentionScore
                : 0d)
            .ThenByDescending(candidate => candidate.Score)
            .ToArray();

        var proposedIds = proposed
            .Select(candidate => candidate.CandidateId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedSetChangeCount = selectedIds
            .Union(proposedIds, StringComparer.OrdinalIgnoreCase)
            .Count(candidateId => selectedIds.Contains(candidateId) != proposedIds.Contains(candidateId));
        var selectedSetPreserved = selectedSetChangeCount == 0
            && selectedIds.SetEquals(proposedIds);

        if (!selectedSetPreserved)
        {
            return Blocked(
                operationId,
                "selected_set_changed",
                packingResult,
                selected,
                proposed,
                request,
                selectedSetChangeCount,
                attentionById);
        }

        var mustHitLabels = BuildLabelSet(request, isMustHit: true);
        var mustNotHitLabels = BuildLabelSet(request, isMustHit: false);
        var guardViolation = FindGuardViolation(selected, proposed, mustNotHitLabels);
        if (!string.IsNullOrEmpty(guardViolation))
        {
            return Blocked(
                operationId,
                guardViolation,
                packingResult,
                selected,
                proposed,
                request,
                selectedSetChangeCount,
                attentionById);
        }

        var orderChanges = BuildOrderChanges(selected, proposed, request, blockedReason: string.Empty, attentionById);
        if (orderChanges.Count == 0)
        {
            return Skipped(operationId, "order_unchanged", packingResult, request, attentionById);
        }

        if (isShadow)
        {
            var shadowReport = BuildReport(
                operationId,
                enabled: true,
                applied: false,
                skipped: true,
                blocked: false,
                skippedReason: "shadow",
                blockedReason: string.Empty,
                selected,
                proposed,
                request,
                selectedSetChangeCount,
                attentionById);

            return new GuardedAttentionRerankResult(packingResult, shadowReport);
        }

        var selectedDecisionsById = packingResult.SelectedDecisions
            .GroupBy(decision => decision.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var rerankedDecisions = proposed
            .Select(candidate => selectedDecisionsById.GetValueOrDefault(candidate.CandidateId))
            .Where(static decision => decision is not null)
            .Cast<ContextRetrievalDecision>()
            .ToArray();
        var rerankedPacking = new RetrievalPackingResult(
            proposed,
            rerankedDecisions,
            packingResult.DroppedDecisions);

        var report = BuildReport(
            operationId,
            enabled: true,
            applied: true,
            skipped: false,
            blocked: false,
            skippedReason: string.Empty,
            blockedReason: string.Empty,
            selected,
            proposed,
            request,
            selectedSetChangeCount,
            attentionById);

        return new GuardedAttentionRerankResult(rerankedPacking, report);
    }

    private GuardedAttentionRerankResult Skipped(
        string operationId,
        string reason,
        RetrievalPackingResult packingResult,
        ContextRetrievalRequest? request = null,
        IReadOnlyDictionary<string, ContextAttentionScore>? attentionById = null)
    {
        var selected = packingResult.SelectedCandidates.ToArray();
        var scores = attentionById ?? new Dictionary<string, ContextAttentionScore>(StringComparer.OrdinalIgnoreCase);
        return new GuardedAttentionRerankResult(
            packingResult,
            new AttentionRerankComparisonReport
            {
                OperationId = operationId,
                Enabled = _options.ShouldAnalyze,
                Mode = _options.EffectiveMode,
                ProfileId = _options.EffectiveProfile,
                AttentionRerankMode = _options.EffectiveMode,
                AttentionProfile = _options.EffectiveProfile,
                Applied = false,
                AttentionApplied = false,
                Skipped = true,
                Blocked = false,
                SkippedReason = reason,
                SelectedSetPreserved = true,
                OrderChangedCount = 0,
                OldOrder = selected.Select(candidate => candidate.SourceId).ToArray(),
                NewOrder = selected.Select(candidate => candidate.SourceId).ToArray(),
                OldSelectedOrder = BuildSelectedOrder(selected, selected, scores, request, Array.Empty<string>()),
                NewSelectedOrder = BuildSelectedOrder(selected, selected, scores, request, Array.Empty<string>()),
                Warnings = string.IsNullOrWhiteSpace(reason) ? Array.Empty<string>() : [reason]
            });
    }

    private GuardedAttentionRerankResult Blocked(
        string operationId,
        string reason,
        RetrievalPackingResult packingResult,
        IReadOnlyList<ContextRetrievalCandidate> original,
        IReadOnlyList<ContextRetrievalCandidate> proposed,
        ContextRetrievalRequest request,
        int selectedSetChangeCount,
        IReadOnlyDictionary<string, ContextAttentionScore> attentionById)
    {
        var report = BuildReport(
            operationId,
            enabled: true,
            applied: false,
            skipped: false,
            blocked: true,
            skippedReason: string.Empty,
            blockedReason: reason,
            original,
            proposed,
            request,
            selectedSetChangeCount,
            attentionById);

        return new GuardedAttentionRerankResult(packingResult, report);
    }

    private AttentionRerankComparisonReport BuildReport(
        string operationId,
        bool enabled,
        bool applied,
        bool skipped,
        bool blocked,
        string skippedReason,
        string blockedReason,
        IReadOnlyList<ContextRetrievalCandidate> original,
        IReadOnlyList<ContextRetrievalCandidate> proposed,
        ContextRetrievalRequest request,
        int selectedSetChangeCount,
        IReadOnlyDictionary<string, ContextAttentionScore> attentionById)
    {
        var originalIds = original
            .Select(candidate => candidate.CandidateId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var proposedIds = proposed
            .Select(candidate => candidate.CandidateId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = proposed
            .Where(candidate => !originalIds.Contains(candidate.CandidateId))
            .Select(candidate => ToChange(candidate, original, proposed, request, blockedReason, attentionById))
            .ToArray();
        var dropped = original
            .Where(candidate => !proposedIds.Contains(candidate.CandidateId))
            .Select(candidate => ToChange(candidate, original, proposed, request, blockedReason, attentionById))
            .ToArray();
        var orderChanges = BuildOrderChanges(original, proposed, request, blockedReason, attentionById);
        var sectionChanges = BuildSectionChanges(original, proposed);
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(skippedReason))
        {
            warnings.Add(skippedReason);
        }

        if (!string.IsNullOrWhiteSpace(blockedReason))
        {
            warnings.Add(blockedReason);
        }

        if (selectedSetChangeCount > 0)
        {
            warnings.Add("selected_set_changed");
        }
        var guardViolation = !string.IsNullOrWhiteSpace(blockedReason)
            ? blockedReason
            : selectedSetChangeCount > 0
                ? "selected_set_changed"
                : string.Empty;
        var selectedSetPreserved = selectedSetChangeCount == 0
            && added.Length == 0
            && dropped.Length == 0;
        var effectiveNewOrder = !string.IsNullOrWhiteSpace(guardViolation)
            ? original
            : proposed;

        return new AttentionRerankComparisonReport
        {
            OperationId = operationId,
            Enabled = enabled,
            Mode = _options.EffectiveMode,
            ProfileId = _options.EffectiveProfile,
            AttentionRerankMode = _options.EffectiveMode,
            AttentionProfile = _options.EffectiveProfile,
            Applied = applied,
            AttentionApplied = applied,
            Skipped = skipped,
            Blocked = blocked,
            SkippedReason = skippedReason,
            BlockedReason = blockedReason,
            SelectedSetPreserved = selectedSetPreserved,
            OrderChangedCount = orderChanges.Count,
            OldOrder = original.Select(candidate => candidate.SourceId).ToArray(),
            NewOrder = effectiveNewOrder.Select(candidate => candidate.SourceId).ToArray(),
            GuardViolation = guardViolation,
            AddedItems = added,
            DroppedItems = dropped,
            OldSelectedOrder = BuildSelectedOrder(original, original, attentionById, request, Array.Empty<string>()),
            NewSelectedOrder = BuildSelectedOrder(proposed, original, attentionById, request, [.. warnings]),
            OrderChanges = orderChanges,
            MovedUpItems = orderChanges.Where(change => change.RankDelta > 0).ToArray(),
            MovedDownItems = orderChanges.Where(change => change.RankDelta < 0).ToArray(),
            SectionChanges = sectionChanges,
            MustHitRankDeltas = orderChanges.Where(change => change.IsMustHit).ToArray(),
            MustNotHitRankDeltas = orderChanges.Where(change => change.IsMustNotHit).ToArray(),
            SelectedSetChangeCount = selectedSetChangeCount,
            SelectedSetChangeRatio = original.Count == 0 ? 0d : (double)selectedSetChangeCount / original.Count,
            Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private static IReadOnlyList<AttentionRerankItemChange> BuildOrderChanges(
        IReadOnlyList<ContextRetrievalCandidate> original,
        IReadOnlyList<ContextRetrievalCandidate> proposed,
        ContextRetrievalRequest request,
        string blockedReason,
        IReadOnlyDictionary<string, ContextAttentionScore> attentionById)
    {
        return proposed
            .Select(candidate => ToChange(candidate, original, proposed, request, blockedReason, attentionById))
            .Where(change => change.CurrentRank > 0 && change.RerankedRank > 0 && change.RankDelta != 0)
            .OrderByDescending(change => Math.Abs(change.RankDelta))
            .ThenBy(change => change.RerankedRank)
            .ToArray();
    }

    private static IReadOnlyList<AttentionRerankSectionChange> BuildSectionChanges(
        IReadOnlyList<ContextRetrievalCandidate> original,
        IReadOnlyList<ContextRetrievalCandidate> proposed)
    {
        var originalById = original.ToDictionary(candidate => candidate.CandidateId, StringComparer.OrdinalIgnoreCase);
        return proposed
            .Where(candidate => originalById.ContainsKey(candidate.CandidateId))
            .Select(candidate =>
            {
                var fromSection = originalById[candidate.CandidateId].Metadata.GetValueOrDefault("sectionName") ?? string.Empty;
                var toSection = candidate.Metadata.GetValueOrDefault("sectionName") ?? string.Empty;
                return new AttentionRerankSectionChange
                {
                    CandidateId = candidate.CandidateId,
                    SourceId = candidate.SourceId,
                    FromSection = fromSection,
                    ToSection = toSection
                };
            })
            .Where(change => !string.Equals(change.FromSection, change.ToSection, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static AttentionRerankItemChange ToChange(
        ContextRetrievalCandidate candidate,
        IReadOnlyList<ContextRetrievalCandidate> original,
        IReadOnlyList<ContextRetrievalCandidate> proposed,
        ContextRetrievalRequest request,
        string blockedReason,
        IReadOnlyDictionary<string, ContextAttentionScore> attentionById)
    {
        var currentRank = ResolveCurrentRank(original, candidate);
        var rerankedRank = ResolveCurrentRank(proposed, candidate);
        attentionById.TryGetValue(candidate.CandidateId, out var attentionScore);
        var reasons = new List<string>();
        if (!string.IsNullOrWhiteSpace(blockedReason))
        {
            reasons.Add(blockedReason);
        }

        if (IsMandatory(candidate))
        {
            reasons.Add("mandatory_protected");
        }

        if (IsHardConstraint(candidate))
        {
            reasons.Add("hard_constraint_protected");
        }

        if (IsLifecycleRisk(candidate))
        {
            reasons.Add("lifecycle_risk_protected");
        }

        if (attentionScore is not null)
        {
            reasons.AddRange(attentionScore.Reasons);
        }

        var rankDelta = currentRank - rerankedRank;
        return new AttentionRerankItemChange
        {
            CandidateId = candidate.CandidateId,
            SourceId = candidate.SourceId,
            CurrentRank = currentRank,
            RerankedRank = rerankedRank,
            RankDelta = rankDelta,
            OldScore = candidate.Score,
            AttentionScore = attentionScore?.FinalAttentionScore ?? 0d,
            FinalScore = attentionScore?.FinalAttentionScore ?? candidate.Score,
            MoveReason = ResolveMoveReason(rankDelta, blockedReason),
            Lifecycle = ResolveLifecycle(candidate),
            IsMustHit = MatchesAnyLabel(candidate, BuildLabelSet(request, isMustHit: true)),
            IsMustNotHit = MatchesAnyLabel(candidate, BuildLabelSet(request, isMustHit: false)),
            IsConstraint = IsConstraint(candidate),
            IsHardConstraint = IsHardConstraint(candidate),
            IsLifecycleRisk = IsLifecycleRisk(candidate),
            AttentionScoreBreakdown = attentionScore is null
                ? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                : BuildAttentionScoreBreakdown(attentionScore),
            Reasons = reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private static IReadOnlyList<AttentionRerankOrderItem> BuildSelectedOrder(
        IReadOnlyList<ContextRetrievalCandidate> order,
        IReadOnlyList<ContextRetrievalCandidate> original,
        IReadOnlyDictionary<string, ContextAttentionScore> attentionById,
        ContextRetrievalRequest? request,
        IReadOnlyList<string> extraReasons)
    {
        var mustHitLabels = request is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : BuildLabelSet(request, isMustHit: true);
        var mustNotHitLabels = request is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : BuildLabelSet(request, isMustHit: false);

        return order
            .Select((candidate, index) =>
            {
                attentionById.TryGetValue(candidate.CandidateId, out var attentionScore);
                var reasons = new List<string>(candidate.Reasons);
                reasons.AddRange(extraReasons);
                if (attentionScore is not null)
                {
                    reasons.AddRange(attentionScore.Reasons);
                }

                return new AttentionRerankOrderItem
                {
                    CandidateId = candidate.CandidateId,
                    SourceId = candidate.SourceId,
                    CandidateKind = candidate.Kind,
                    Type = candidate.Type,
                    Rank = index + 1,
                    OldScore = candidate.Score,
                    AttentionScore = attentionScore?.FinalAttentionScore ?? 0d,
                    FinalScore = attentionScore?.FinalAttentionScore ?? candidate.Score,
                    Lifecycle = ResolveLifecycle(candidate),
                    IsMustHit = MatchesAnyLabel(candidate, mustHitLabels),
                    IsMustNotHit = MatchesAnyLabel(candidate, mustNotHitLabels),
                    IsConstraint = IsConstraint(candidate),
                    IsHardConstraint = IsHardConstraint(candidate),
                    IsLifecycleRisk = IsLifecycleRisk(candidate),
                    AttentionScoreBreakdown = attentionScore is null
                        ? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                        : BuildAttentionScoreBreakdown(attentionScore),
                    Reasons = reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                };
            })
            .ToArray();
    }

    private static string FindGuardViolation(
        IReadOnlyList<ContextRetrievalCandidate> original,
        IReadOnlyList<ContextRetrievalCandidate> proposed,
        IReadOnlySet<string> mustNotHitLabels)
    {
        foreach (var candidate in original)
        {
            var currentRank = ResolveCurrentRank(original, candidate);
            var proposedRank = ResolveCurrentRank(proposed, candidate);
            if (proposedRank <= 0)
            {
                return "selected_set_changed";
            }

            if (MatchesAnyLabel(candidate, mustNotHitLabels) && proposedRank < currentRank)
            {
                return "must_not_hit_promotion_blocked";
            }

            if (IsHardConstraint(candidate) && proposedRank > currentRank)
            {
                return "hard_constraint_demotion_blocked";
            }

            if (IsLifecycleRisk(candidate) && proposedRank < currentRank)
            {
                return "lifecycle_risk_promotion_blocked";
            }
        }

        return string.Empty;
    }

    private static int ResolveCurrentRank(
        IReadOnlyList<ContextRetrievalCandidate> candidates,
        ContextRetrievalCandidate candidate)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            if (string.Equals(candidates[i].CandidateId, candidate.CandidateId, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return 0;
    }

    private static bool IsMandatory(ContextRetrievalCandidate candidate)
    {
        return candidate.Metadata.TryGetValue("mandatory", out var value)
            && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHardConstraint(ContextRetrievalCandidate candidate)
    {
        if (IsMandatory(candidate))
        {
            return true;
        }

        if (!IsConstraint(candidate))
        {
            return false;
        }

        return candidate.Metadata.Any(pair =>
            pair.Value.Contains("hard", StringComparison.OrdinalIgnoreCase)
            || pair.Value.Contains("required", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsConstraint(ContextRetrievalCandidate candidate)
    {
        return candidate.Type.Contains("constraint", StringComparison.OrdinalIgnoreCase)
            || candidate.Tags.Any(tag => tag.Contains("constraint", StringComparison.OrdinalIgnoreCase))
            || candidate.Metadata.Any(pair =>
                pair.Key.Contains("constraint", StringComparison.OrdinalIgnoreCase)
                || pair.Value.Contains("constraint", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLifecycleRisk(ContextRetrievalCandidate candidate)
    {
        var lifecycle = ResolveLifecycle(candidate);
        return lifecycle.Contains("rejected", StringComparison.OrdinalIgnoreCase)
            || lifecycle.Contains("deprecated", StringComparison.OrdinalIgnoreCase)
            || lifecycle.Contains("superseded", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveLifecycle(ContextRetrievalCandidate candidate)
    {
        if (candidate.Metadata.TryGetValue("lifecycleStatus", out var lifecycleStatus)
            && !string.IsNullOrWhiteSpace(lifecycleStatus))
        {
            return lifecycleStatus;
        }

        if (candidate.Metadata.TryGetValue("lifecycle", out var lifecycle)
            && !string.IsNullOrWhiteSpace(lifecycle))
        {
            return lifecycle;
        }

        return "Active";
    }

    private static string ResolveMoveReason(int rankDelta, string blockedReason)
    {
        if (!string.IsNullOrWhiteSpace(blockedReason))
        {
            return blockedReason;
        }

        return rankDelta switch
        {
            > 0 => "attention_rank_promoted",
            < 0 => "attention_rank_demoted",
            _ => "order_unchanged"
        };
    }

    private static Dictionary<string, double> BuildAttentionScoreBreakdown(ContextAttentionScore score)
    {
        return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["queryMatch"] = score.QueryMatchScore,
            ["shortTermMatch"] = score.ShortTermMatchScore,
            ["relation"] = score.RelationScore,
            ["recency"] = score.RecencyScore,
            ["importance"] = score.ImportanceScore,
            ["channel"] = score.ChannelScore,
            ["learningFeedback"] = score.LearningFeedbackScore,
            ["lifecyclePenalty"] = score.LifecyclePenalty,
            ["scopePenalty"] = score.ScopePenalty,
            ["noiseRisk"] = score.NoiseRiskScore,
            ["final"] = score.FinalAttentionScore
        };
    }

    private static HashSet<string> BuildLabelSet(ContextRetrievalRequest request, bool isMustHit)
    {
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (isMustHit)
        {
            foreach (var id in request.RequiredIds)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    labels.Add(id.Trim());
                }
            }
        }

        var keys = isMustHit
            ? new[] { "attention.mustHit", "eval.mustHit", "mustHit" }
            : new[] { "attention.mustNotHit", "eval.mustNotHit", "mustNotHit" };

        foreach (var pair in request.Metadata)
        {
            if (!keys.Any(key => string.Equals(key, pair.Key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (var value in SplitLabels(pair.Value))
            {
                labels.Add(value);
            }
        }

        return labels;
    }

    private static IEnumerable<string> SplitLabels(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (var item in value.Split([',', ';', '|', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(item))
            {
                yield return item.Trim();
            }
        }
    }

    private static bool MatchesAnyLabel(ContextRetrievalCandidate candidate, IReadOnlySet<string> labels)
    {
        return labels.Count > 0 && labels.Any(label => MatchesLabel(candidate, label));
    }

    private static bool MatchesLabel(ContextRetrievalCandidate candidate, string label)
    {
        if (string.Equals(candidate.CandidateId, label, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.SourceId, label, StringComparison.OrdinalIgnoreCase)
            || candidate.SourceRefs.Contains(label, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidate.Metadata.Any(pair =>
            (string.Equals(pair.Key, "itemId", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.Key, "sourceId", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.Key, "memoryId", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.Key, "contextId", StringComparison.OrdinalIgnoreCase))
            && string.Equals(pair.Value, label, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed record GuardedAttentionRerankResult(
    RetrievalPackingResult PackingResult,
    AttentionRerankComparisonReport Report);
