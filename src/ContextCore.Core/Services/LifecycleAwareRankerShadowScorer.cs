using System.Globalization;
using System.Text.RegularExpressions;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Computes lifecycle-aware ranker scores for diagnostics only. It never mutates retrieval order or selected IDs.
/// </summary>
public sealed class LifecycleAwareRankerShadowScorer
{
    public const string DefaultProfile = "lifecycle-aware-v1";
    public const string PolicyVersion = "lifecycle-aware-ranker-shadow/v1";

    public LifecycleAwareRankerShadowTrace Score(
        IReadOnlyList<ContextEvalItemDiagnostic> selectedItems,
        IReadOnlyList<ContextEvalItemDiagnostic> droppedItems,
        LifecycleAwareRankerShadowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(selectedItems);
        ArgumentNullException.ThrowIfNull(droppedItems);

        var resolvedOptions = options ?? new LifecycleAwareRankerShadowOptions
        {
            Enabled = false,
            Profile = DefaultProfile
        };
        var profile = string.IsNullOrWhiteSpace(resolvedOptions.Profile)
            ? DefaultProfile
            : resolvedOptions.Profile;

        if (!resolvedOptions.Enabled)
        {
            return new LifecycleAwareRankerShadowTrace
            {
                RankerShadowEnabled = false,
                RankerShadowProfile = profile
            };
        }

        var candidates = BuildCandidates(selectedItems, droppedItems);
        var scored = candidates
            .Select(candidate => BuildCandidateScore(candidate, shadowRank: 0))
            .ToArray();
        var shadowRankById = scored
            .OrderByDescending(static item => item.LifecycleAwareScore)
            .ThenBy(static item => item.LegacyRank)
            .ThenBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
            .Select((item, index) => new { item.CandidateId, Rank = index + 1 })
            .GroupBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().Rank, StringComparer.OrdinalIgnoreCase);

        var finalScores = scored
            .Select(item => WithShadowRank(
                item,
                shadowRankById.TryGetValue(item.CandidateId, out var rank) ? rank : item.LegacyRank))
            .OrderBy(static item => item.LegacyRank)
            .ThenBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new LifecycleAwareRankerShadowTrace
        {
            RankerShadowEnabled = true,
            RankerShadowProfile = profile,
            CandidateShadowScores = finalScores,
            DeprecatedDemotions = finalScores
                .Where(IsDeprecatedDemotion)
                .OrderBy(static item => item.ShadowRank)
                .ToArray(),
            VersionConflictFixes = BuildVersionConflictFixes(finalScores),
            MustHitDemotions = finalScores
                .Where(static item => item.IsMustHit && (item.ScoreDelta < 0 || item.ShadowRank > item.LegacyRank))
                .OrderBy(static item => item.LegacyRank)
                .ToArray(),
            MustNotHitPromotions = finalScores
                .Where(static item => item.IsMustNotHit && (item.ScoreDelta > 0 || item.ShadowRank < item.LegacyRank))
                .OrderBy(static item => item.ShadowRank)
                .ToArray()
        };
    }

    private static IReadOnlyList<CandidateSnapshot> BuildCandidates(
        IReadOnlyList<ContextEvalItemDiagnostic> selectedItems,
        IReadOnlyList<ContextEvalItemDiagnostic> droppedItems)
    {
        var candidates = new List<CandidateSnapshot>(selectedItems.Count + droppedItems.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < selectedItems.Count; index++)
        {
            var item = selectedItems[index];
            if (!seen.Add(item.ItemId))
            {
                continue;
            }

            candidates.Add(new CandidateSnapshot(
                item,
                Selected: true,
                LegacyRank: item.Rank > 0 ? item.Rank : index + 1));
        }

        for (var index = 0; index < droppedItems.Count; index++)
        {
            var item = droppedItems[index];
            if (!seen.Add(item.ItemId))
            {
                continue;
            }

            candidates.Add(new CandidateSnapshot(
                item,
                Selected: false,
                LegacyRank: item.Rank > 0 ? selectedItems.Count + item.Rank : selectedItems.Count + index + 1));
        }

        return candidates
            .OrderBy(static item => item.LegacyRank)
            .ThenBy(static item => item.Diagnostic.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static LifecycleAwareRankerShadowCandidateScore BuildCandidateScore(
        CandidateSnapshot candidate,
        int shadowRank)
    {
        var diagnostic = candidate.Diagnostic;
        var features = ExtractLifecycleAwareFeatures(diagnostic);
        var (adjustment, reason) = ScoreAdjustment(features);
        var lifecycleAwareScore = diagnostic.Score + adjustment;

        return new LifecycleAwareRankerShadowCandidateScore
        {
            CandidateId = diagnostic.ItemId,
            Kind = diagnostic.Kind,
            Type = diagnostic.Type,
            SectionName = diagnostic.SectionName,
            Selected = candidate.Selected,
            IsMustHit = diagnostic.IsMustHit,
            IsMustNotHit = diagnostic.IsMustNotHit,
            LegacyRank = candidate.LegacyRank,
            ShadowRank = shadowRank,
            RankDelta = shadowRank == 0 ? 0 : candidate.LegacyRank - shadowRank,
            LegacyScore = diagnostic.Score,
            LifecycleAwareScore = lifecycleAwareScore,
            ScoreDelta = adjustment,
            Reason = reason,
            DemotionReasons = SplitReasons(reason)
                .Where(IsDemotionReason)
                .ToArray(),
            PromotionReasons = SplitReasons(reason)
                .Where(IsPromotionReason)
                .ToArray(),
            LifecycleFeatures = features
        };
    }

    private static LifecycleAwareRankerShadowCandidateScore WithShadowRank(
        LifecycleAwareRankerShadowCandidateScore item,
        int shadowRank)
    {
        return new LifecycleAwareRankerShadowCandidateScore
        {
            CandidateId = item.CandidateId,
            Kind = item.Kind,
            Type = item.Type,
            SectionName = item.SectionName,
            Selected = item.Selected,
            IsMustHit = item.IsMustHit,
            IsMustNotHit = item.IsMustNotHit,
            LegacyRank = item.LegacyRank,
            ShadowRank = shadowRank,
            RankDelta = item.LegacyRank - shadowRank,
            LegacyScore = item.LegacyScore,
            LifecycleAwareScore = item.LifecycleAwareScore,
            ScoreDelta = item.ScoreDelta,
            Reason = item.Reason,
            DemotionReasons = item.DemotionReasons,
            PromotionReasons = item.PromotionReasons,
            LifecycleFeatures = item.LifecycleFeatures
        };
    }

    private static (double Adjustment, string Reason) ScoreAdjustment(LifecycleAwareFeatureSet features)
    {
        var adjustment = 0.0;
        var reasons = new List<string>();

        if (features.IsRejected)
        {
            adjustment -= 40;
            reasons.Add("rejected_demotion");
        }

        if (features.IsDeprecated)
        {
            adjustment -= 22;
            reasons.Add("deprecated_demotion");
        }

        if (features.IsSuperseded)
        {
            adjustment -= 22;
            reasons.Add("superseded_demotion");
        }

        if (features.IsHistorical)
        {
            adjustment -= 16;
            reasons.Add("historical_demotion");
        }

        if (features.HistoricalSectionOnly)
        {
            adjustment -= 6;
            reasons.Add("historical_section_demotion");
        }

        if (features.HasReplacement && !features.IsCurrentVersion)
        {
            adjustment -= 8;
            reasons.Add("replacement_available_demotion");
        }

        if (features.IsCurrentVersion)
        {
            adjustment += 12;
            reasons.Add("current_version_boost");
        }

        if (features.HasSupersedesRelation && features.IsCurrentVersion)
        {
            adjustment += 4;
            reasons.Add("supersedes_relation_boost");
        }

        return (adjustment, reasons.Count == 0 ? "no_lifecycle_adjustment" : string.Join(';', reasons));
    }

    private static IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> BuildVersionConflictFixes(
        IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> scores)
    {
        var hasCurrentAlternative = scores.Any(static item =>
            item.ScoreDelta > 0
            && (item.LifecycleFeatures.IsCurrentVersion
                || item.LifecycleFeatures.HasReplacement
                || item.LifecycleFeatures.HasSupersedesRelation));
        if (!hasCurrentAlternative)
        {
            return Array.Empty<LifecycleAwareRankerShadowCandidateScore>();
        }

        return scores
            .Where(static item => item.ScoreDelta < 0
                && (item.LifecycleFeatures.IsDeprecated
                    || item.LifecycleFeatures.IsSuperseded
                    || item.LifecycleFeatures.IsHistorical
                    || item.LifecycleFeatures.VersionDistance > 0))
            .OrderBy(static item => item.ShadowRank)
            .ToArray();
    }

    private static bool IsDeprecatedDemotion(LifecycleAwareRankerShadowCandidateScore item)
    {
        return item.ScoreDelta < 0
            && (item.LifecycleFeatures.IsDeprecated
                || item.LifecycleFeatures.IsSuperseded
                || item.LifecycleFeatures.IsHistorical
                || item.LifecycleFeatures.HistoricalSectionOnly);
    }

    private static LifecycleAwareFeatureSet ExtractLifecycleAwareFeatures(ContextEvalItemDiagnostic diagnostic)
    {
        var candidateText = string.Join(' ', new[]
        {
            diagnostic.ItemId,
            diagnostic.Kind,
            diagnostic.Type,
            diagnostic.SectionName,
            diagnostic.Reason,
            string.Join(' ', diagnostic.SourceRefs)
        });
        var isDeprecated = ContainsAny(candidateText, "deprecated", "obsolete", "废弃", "作废", "过期");
        var isSuperseded = ContainsAny(candidateText, "superseded", "supersede", "覆盖", "被替代");
        var isHistorical = ContainsAny(candidateText, "historical", "history", "old", "legacy", "v1", "旧", "历史")
            || diagnostic.SectionName.Contains("historical", StringComparison.OrdinalIgnoreCase)
            || diagnostic.Kind.Contains("historical", StringComparison.OrdinalIgnoreCase);
        var isRejected = ContainsAny(candidateText, "rejected", "invalid", "blocked", "inactive", "否决", "拒绝", "无效");
        var hasReplacement = ContainsAny(candidateText, "replacement", "replaced", "replaces", "替代", "新版", "latest", "current", "v2");
        var hasSupersedesRelation = ContainsAny(candidateText, "supersedes", "superseded", "replacement", "replaced", "覆盖", "替代");
        var versionDistance = ResolveVersionDistance(candidateText);
        var isCurrentVersion = ContainsAny(candidateText, "v2", "latest", "current", "new", "active", "confirmed", "当前", "最新", "新版", "确认");
        var riskSignals = new[]
        {
            isDeprecated,
            isSuperseded,
            isHistorical,
            isRejected,
            hasReplacement && !isCurrentVersion
        }.Count(static value => value);
        var lifecycleConfidence = isCurrentVersion && riskSignals == 0
            ? 0.7
            : Math.Min(1, 0.25 + riskSignals * 0.22 + (versionDistance > 0 ? 0.2 : 0));
        var historicalSectionOnly = diagnostic.SectionName.Contains("historical", StringComparison.OrdinalIgnoreCase)
            && !isDeprecated
            && !isSuperseded
            && !isRejected;

        return new LifecycleAwareFeatureSet
        {
            IsDeprecated = isDeprecated,
            IsSuperseded = isSuperseded,
            IsHistorical = isHistorical,
            IsRejected = isRejected,
            HasReplacement = hasReplacement,
            HasSupersedesRelation = hasSupersedesRelation,
            VersionDistance = versionDistance,
            IsCurrentVersion = isCurrentVersion,
            LifecycleConfidence = lifecycleConfidence,
            HistoricalSectionOnly = historicalSectionOnly
        };
    }

    private static double ResolveVersionDistance(string text)
    {
        if (ContainsAny(text, "latest", "current", "new", "最新", "当前", "新版"))
        {
            return 0;
        }

        var matches = Regex.Matches(text, @"\bv(?<version>\d+)\b", RegexOptions.IgnoreCase);
        if (matches.Count == 0)
        {
            return ContainsAny(text, "old", "legacy", "旧", "历史") ? 1 : 0;
        }

        var maxVersion = matches
            .Select(static match => int.TryParse(
                match.Groups["version"].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var version)
                    ? version
                    : 0)
            .DefaultIfEmpty(0)
            .Max();
        return maxVersion <= 1 ? 1 : 0;
    }

    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> SplitReasons(string reason)
    {
        return string.IsNullOrWhiteSpace(reason)
            ? Array.Empty<string>()
            : reason.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsDemotionReason(string reason)
    {
        return reason.Contains("demotion", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("penalty", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("rejected", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPromotionReason(string reason)
    {
        return reason.Contains("boost", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("current", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("supersedes", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CandidateSnapshot(
        ContextEvalItemDiagnostic Diagnostic,
        bool Selected,
        int LegacyRank);
}
