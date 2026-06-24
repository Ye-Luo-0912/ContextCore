using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public static class PlanningShadowRecallLossReportBuilder
{
    private const double Epsilon = 0.0001;

    public static PlanningShadowRecallLossReport Build(ShadowRetrievalComparisonReport comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        var samples = comparison.Samples
            .Where(IsDegraded)
            .Select(BuildSample)
            .ToArray();

        return new PlanningShadowRecallLossReport
        {
            ReportId = Guid.NewGuid().ToString("N"),
            SampleSet = comparison.SampleSet,
            GeneratedAt = DateTimeOffset.UtcNow,
            TotalSamples = comparison.TotalSamples,
            DegradedSampleCount = samples.Length,
            MustHitLostCount = samples.Sum(sample => sample.MustHitLost.Count),
            SuspectedLossReasonCounts = samples
                .GroupBy(sample => sample.SuspectedLossReason, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            Samples = samples
        };
    }

    private static PlanningShadowRecallLossSample BuildSample(ShadowRetrievalComparisonItem item)
    {
        var relevantMustHits = item.LegacySelectedMustHit
            .Union(item.ShadowSelectedMustHit, StringComparer.OrdinalIgnoreCase)
            .Union(item.MustHitDropped, StringComparer.OrdinalIgnoreCase)
            .Union(item.MustHitGained, StringComparer.OrdinalIgnoreCase)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var legacyRanks = BuildRankLookup(item, legacy: true, relevantMustHits);
        var shadowRanks = BuildRankLookup(item, legacy: false, relevantMustHits);
        var reason = ResolveSuspectedLossReason(item);

        return new PlanningShadowRecallLossSample
        {
            SampleId = item.SampleId,
            Mode = string.IsNullOrWhiteSpace(item.Mode) ? "Unknown" : item.Mode,
            Intent = ResolveIntent(item.ProposalSummary),
            LegacySelectedMustHit = item.LegacySelectedMustHit,
            ShadowSelectedMustHit = item.ShadowSelectedMustHit,
            MustHitLost = item.MustHitDropped,
            MustHitRankLegacy = legacyRanks,
            MustHitRankShadow = shadowRanks,
            LegacyChannelSources = FilterChannelSources(item.LegacyChannelSources, relevantMustHits),
            ShadowChannelSources = FilterChannelSources(item.ShadowChannelSources, relevantMustHits),
            DisabledChannels = SplitDiagnosticList(item.Diagnostics, "disabledChannels"),
            TopKCaps = ParseTopKCaps(item.Diagnostics),
            SuspectedLossReason = reason,
            SuggestedFix = ResolveSuggestedFix(reason)
        };
    }

    private static bool IsDegraded(ShadowRetrievalComparisonItem item)
    {
        return item.MustHitDropped.Count > 0
            || item.ShadowRecall10 + Epsilon < item.LegacyRecall10
            || item.ShadowMrr + Epsilon < item.LegacyMrr;
    }

    private static IReadOnlyDictionary<string, int?> BuildRankLookup(
        ShadowRetrievalComparisonItem item,
        bool legacy,
        IReadOnlyList<string> relevantMustHits)
    {
        var rankBySource = item.RankDeltas.ToDictionary(
            rank => rank.SourceId,
            rank => legacy ? rank.LegacyRank : rank.ShadowRank,
            StringComparer.OrdinalIgnoreCase);
        return relevantMustHits.ToDictionary(
            mustHit => mustHit,
            mustHit => rankBySource.TryGetValue(mustHit, out var rank) ? rank : null,
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> FilterChannelSources(
        IReadOnlyDictionary<string, IReadOnlyList<string>> sources,
        IReadOnlyList<string> relevantMustHits)
    {
        if (relevantMustHits.Count == 0)
        {
            return sources;
        }

        return sources
            .Where(pair => relevantMustHits.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveSuspectedLossReason(ShadowRetrievalComparisonItem item)
    {
        if (item.MustHitDropped.Count > 0 && item.LostByDisabledChannel.Count > 0)
        {
            return "LostByDisabledChannel";
        }

        if (item.MustHitDropped.Count > 0 && item.LostByTopKCap.Count > 0)
        {
            return "LostByTopKCap";
        }

        if (item.MustHitDropped.Count > 0)
        {
            return item.LostByChannel.Values.Any(value =>
                value.Contains("shadowCandidateMissing", StringComparison.OrdinalIgnoreCase))
                ? "LostByShadowCandidateMissing"
                : "MustHitLostByShadowOrder";
        }

        if (item.ShadowMrr + Epsilon < item.LegacyMrr)
        {
            return "MustHitRankedLower";
        }

        if (item.ShadowSelected.Count < item.LegacySelected.Count)
        {
            return "ShadowSelectedFewerItems";
        }

        return "QualityRegression";
    }

    private static string ResolveSuggestedFix(string reason)
    {
        return reason switch
        {
            "LostByDisabledChannel" => "keep vector disabled; add non-vector recall reserve or relation evidence reserve",
            "LostByTopKCap" => "apply coverage floor before final TopK selection",
            "LostByShadowCandidateMissing" => "add intent-specific keyword/memory/relation reserve without changing scoring",
            "MustHitLostByShadowOrder" => "promote mustHit reserve in shadow packing order",
            "MustHitRankedLower" => "apply mustHit and exact-match reserve ahead of generic high-score candidates",
            "ShadowSelectedFewerItems" => "backfill safe candidates after mustNotHit/lifecycle filtering",
            _ => "inspect sample-level channels and reserve coverage"
        };
    }

    private static string ResolveIntent(string proposalSummary)
    {
        if (string.IsNullOrWhiteSpace(proposalSummary))
        {
            return "Unknown";
        }

        var slash = proposalSummary.IndexOf('/');
        return slash > 0 ? proposalSummary[..slash] : proposalSummary;
    }

    private static IReadOnlyDictionary<string, int> ParseTopKCaps(
        IReadOnlyDictionary<string, string> diagnostics)
    {
        if (!diagnostics.TryGetValue("topKCaps", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return new Dictionary<string, int>();
        }

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = part[..separator].Trim();
            var raw = part[(separator + 1)..].Trim();
            if (int.TryParse(raw, out var parsed))
            {
                result[key] = parsed;
            }
        }

        return result;
    }

    private static IReadOnlyList<string> SplitDiagnosticList(
        IReadOnlyDictionary<string, string> diagnostics,
        string key)
    {
        if (!diagnostics.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value
            .Split(['|', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
