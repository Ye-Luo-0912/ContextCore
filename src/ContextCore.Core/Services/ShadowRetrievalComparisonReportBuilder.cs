using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public static class ShadowRetrievalComparisonReportBuilder
{
    private static readonly string[] RepairReasonCategories =
    [
        "FinalTopKClamped",
        "KeywordTopKClamped",
        "MemoryTopKClamped",
        "RelationTopKClamped",
        "VectorDisabled",
        "DeprecatedBlocked",
        "SupersededBlocked",
        "InvalidNormalLifecycle"
    ];

    public static ShadowRetrievalComparisonReport Build(
        string sampleSet,
        IReadOnlyList<ShadowRetrievalComparisonItem> samples)
    {
        var warningCounts = samples
            .SelectMany(sample => sample.Warnings)
            .GroupBy(warning => warning, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var validatorRepairReasons = samples
            .SelectMany(sample => sample.ValidatorRepairReasons)
            .GroupBy(reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var repairReasonCounts = BuildRepairReasonCounts(samples);
        var nativeValidPlanCount = samples.Count(sample => sample.NativeValidPlan);
        var fallbackPlanCount = samples.Count(sample => sample.FallbackToLegacySafePlan);

        return new ShadowRetrievalComparisonReport
        {
            ReportId = Guid.NewGuid().ToString("N"),
            SampleSet = sampleSet,
            GeneratedAt = DateTimeOffset.UtcNow,
            TotalSamples = samples.Count,
            SelectedSetDiffCount = samples.Count(sample => sample.SelectedSetDiff > 0),
            AddedItemCount = samples.Sum(sample => sample.AddedCount),
            DroppedItemCount = samples.Sum(sample => sample.DroppedCount),
            MustNotHitViolationCount = samples.Sum(sample => sample.MustNotHitViolationCount),
            LifecycleViolationCount = samples.Sum(sample => sample.LifecycleViolationCount),
            AvgBudgetPressureDelta = samples.Count == 0 ? 0 : samples.Average(sample => sample.BudgetPressureDelta),
            ValidPlanCount = samples.Count(sample => sample.ValidPlan),
            NativeValidPlanCount = nativeValidPlanCount,
            RepairedPlanCount = samples.Count(sample => sample.RepairedPlan),
            FallbackToLegacySafePlanCount = samples.Count(sample => sample.FallbackToLegacySafePlan),
            FallbackPlanCount = fallbackPlanCount,
            NativeValidRate = samples.Count == 0 ? 0 : (double)nativeValidPlanCount / samples.Count,
            FinalTopKClampCount = samples.Count(sample => sample.FinalTopKClamped),
            VectorDisabledCount = samples.Count(sample => sample.VectorDisabled),
            DeprecatedBlockedCount = samples.Sum(sample => sample.DeprecatedBlockedCount),
            ValidatorRepairReasons = validatorRepairReasons,
            RepairReasonCounts = repairReasonCounts,
            IntentRepairBreakdown = BuildBreakdown(
                samples,
                item => ResolveIntent(item.ProposalSummary)),
            ModeRepairBreakdown = BuildBreakdown(
                samples,
                item => string.IsNullOrWhiteSpace(item.Mode) ? "Unknown" : item.Mode),
            WarningCounts = warningCounts,
            Samples = samples
        };
    }

    public static ShadowRetrievalComparisonItem BuildSample(
        ContextEvalSample sample,
        ContextRetrievalResult legacy,
        ShadowRetrievalResult shadow)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(shadow);

        var legacySelected = legacy.SelectedItems;
        var shadowSelected = shadow.ShadowSelectedItems;
        var legacyIds = legacySelected.Select(item => item.SourceId).ToArray();
        var shadowIds = shadowSelected.Select(item => item.SourceId).ToArray();
        var legacySet = legacyIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shadowSet = shadowIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = shadowSet.Except(legacySet, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var dropped = legacySet.Except(shadowSet, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var legacyMustHitRefs = FindMatchedRefs(sample.MustHit, legacySelected);
        var shadowMustHitRefs = FindMatchedRefs(sample.MustHit, shadowSelected);
        var legacyMustHit = legacyMustHitRefs.Count;
        var shadowMustHit = shadowMustHitRefs.Count;
        var mustHitGained = shadowMustHitRefs
            .Except(legacyMustHitRefs, StringComparer.OrdinalIgnoreCase)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var mustHitDropped = legacyMustHitRefs
            .Except(shadowMustHitRefs, StringComparer.OrdinalIgnoreCase)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var disabledChannels = SplitDiagnosticList(shadow.Diagnostics, "disabledChannels");
        var legacyChannelSources = BuildChannelSources(legacySelected);
        var shadowChannelSources = BuildChannelSources(shadowSelected);
        var shadowCandidateChannelSources = BuildChannelSources(shadow.ShadowCandidates);
        var lostByTopKCap = mustHitDropped
            .Where(expected => ContainsRef(shadow.ShadowCandidates, expected))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var lostByDisabledChannel = mustHitDropped
            .Where(expected => IsLostByDisabledChannel(expected, legacySelected, shadow.ShadowCandidates, disabledChannels))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var lostByChannel = BuildLostByChannel(
            mustHitDropped,
            legacySelected,
            shadow.ShadowCandidates,
            disabledChannels,
            lostByTopKCap,
            lostByDisabledChannel);
        var legacyMustNotHitViolations = sample.MustNotHit
            .Where(expected => ContainsRef(legacySelected, expected))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var suppressRetainedLegacySafetyCounts = ShouldSuppressRetainedLegacySafetyCounts(shadow.Diagnostics);
        var mustNotHitViolations = suppressRetainedLegacySafetyCounts
            ? Array.Empty<string>()
            : sample.MustNotHit
                .Where(expected => ContainsRef(shadowSelected, expected))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        var lifecycleViolations = suppressRetainedLegacySafetyCounts
            ? 0
            : CountLifecycleViolations(shadowSelected, shadow.Diagnostics);
        var lifecycleRiskAdded = suppressRetainedLegacySafetyCounts
            ? Array.Empty<string>()
            : shadowSelected
                .Where(item => added.Contains(item.SourceId, StringComparer.OrdinalIgnoreCase))
                .Where(item => IsLifecycleViolation(item, shadow.Diagnostics))
                .Select(item => item.SourceId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        var legacyConstraintMatches = FindMatchedTexts(sample.ExpectedConstraints, legacySelected);
        var shadowConstraintMatches = FindMatchedTexts(sample.ExpectedConstraints, shadowSelected);
        var proposalSelected = ResolveProposalSelectedCandidates(shadow, legacySelected, shadowSelected);
        var proposalConstraintMatches = FindMatchedTexts(sample.ExpectedConstraints, proposalSelected);
        var proposalMissingConstraints = sample.ExpectedConstraints
            .Where(expected => !proposalConstraintMatches.Contains(expected, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var legacyEntityMatches = FindMatchedTexts(sample.ExpectedEntities, legacySelected);
        var shadowEntityMatches = FindMatchedTexts(sample.ExpectedEntities, shadowSelected);
        var legacyUncertaintyMatches = FindMatchedTexts(sample.ExpectedUncertainties, legacySelected);
        var shadowUncertaintyMatches = FindMatchedTexts(sample.ExpectedUncertainties, shadowSelected);
        var legacyConstraintHits = legacyConstraintMatches.Count;
        var shadowConstraintHits = shadowConstraintMatches.Count;
        var legacyEntityHits = legacyEntityMatches.Count;
        var shadowEntityHits = shadowEntityMatches.Count;
        var legacyUncertaintyHits = legacyUncertaintyMatches.Count;
        var shadowUncertaintyHits = shadowUncertaintyMatches.Count;
        var legacyTokens = legacySelected.Sum(item => item.EstimatedTokens);
        var shadowTokens = shadowSelected.Sum(item => item.EstimatedTokens);

        return new ShadowRetrievalComparisonItem
        {
            SampleId = sample.Id,
            Mode = sample.Mode,
            LegacyOperationId = legacy.OperationId,
            ShadowOperationId = shadow.OperationId,
            ProposalId = shadow.ProposalId,
            ProposalSummary = shadow.ProposalSummary,
            LegacySelected = legacyIds,
            ShadowSelected = shadowIds,
            SelectedSetDiff = added.Length + dropped.Length,
            AddedCount = added.Length,
            DroppedCount = dropped.Length,
            AddedItems = added,
            DroppedItems = dropped,
            MustHitDelta = shadowMustHit - legacyMustHit,
            MustHitCount = sample.MustHit.Count,
            LegacyMustHitCount = legacyMustHit,
            ShadowMustHitCount = shadowMustHit,
            LegacySelectedMustHit = legacyMustHitRefs,
            ShadowSelectedMustHit = shadowMustHitRefs,
            MustHitGained = mustHitGained,
            MustHitDropped = mustHitDropped,
            LegacyRecall3 = ResolveRecall(sample.MustHit, legacySelected, 3),
            ShadowRecall3 = ResolveRecall(sample.MustHit, shadowSelected, 3),
            LegacyRecall5 = ResolveRecall(sample.MustHit, legacySelected, 5),
            ShadowRecall5 = ResolveRecall(sample.MustHit, shadowSelected, 5),
            LegacyRecall10 = ResolveRecall(sample.MustHit, legacySelected, 10),
            ShadowRecall10 = ResolveRecall(sample.MustHit, shadowSelected, 10),
            LegacyMrr = ResolveMrr(sample.MustHit, legacySelected),
            ShadowMrr = ResolveMrr(sample.MustHit, shadowSelected),
            LegacyMustHitTokenShare = ResolveMustHitTokenShare(sample.MustHit, legacySelected),
            ShadowMustHitTokenShare = ResolveMustHitTokenShare(sample.MustHit, shadowSelected),
            LegacyMustNotHitViolationCount = legacyMustNotHitViolations.Length,
            MustNotHitViolation = mustNotHitViolations.Length > 0,
            MustNotHitViolationCount = mustNotHitViolations.Length,
            MustNotHitViolations = mustNotHitViolations,
            LifecycleViolation = lifecycleViolations > 0,
            LifecycleViolationCount = lifecycleViolations,
            LifecycleRiskAdded = lifecycleRiskAdded,
            ConstraintDelta = shadowConstraintHits - legacyConstraintHits,
            ExpectedHardConstraints = sample.ExpectedConstraints,
            LegacyConstraints = legacyConstraintMatches,
            ProposalConstraints = proposalConstraintMatches,
            MissingConstraints = proposalMissingConstraints,
            LegacyConstraintHitRate = ResolveHitRate(sample.ExpectedConstraints.Count, legacyConstraintHits),
            ShadowConstraintHitRate = ResolveHitRate(sample.ExpectedConstraints.Count, shadowConstraintHits),
            ConstraintGained = BuildGained(legacyConstraintMatches, shadowConstraintMatches),
            ConstraintLost = BuildLost(legacyConstraintMatches, shadowConstraintMatches),
            EntityDelta = shadowEntityHits - legacyEntityHits,
            LegacyEntityHitRate = ResolveHitRate(sample.ExpectedEntities.Count, legacyEntityHits),
            ShadowEntityHitRate = ResolveHitRate(sample.ExpectedEntities.Count, shadowEntityHits),
            EntityGained = BuildGained(legacyEntityMatches, shadowEntityMatches),
            EntityLost = BuildLost(legacyEntityMatches, shadowEntityMatches),
            UncertaintyDelta = shadowUncertaintyHits - legacyUncertaintyHits,
            LegacyUncertaintyHitRate = ResolveHitRate(sample.ExpectedUncertainties.Count, legacyUncertaintyHits),
            ShadowUncertaintyHitRate = ResolveHitRate(sample.ExpectedUncertainties.Count, shadowUncertaintyHits),
            UncertaintyGained = BuildGained(legacyUncertaintyMatches, shadowUncertaintyMatches),
            UncertaintyLost = BuildLost(legacyUncertaintyMatches, shadowUncertaintyMatches),
            BudgetPressureDelta = shadowTokens - legacyTokens,
            RankDeltas = BuildRankDeltas(legacySelected, shadowSelected),
            LegacyChannelSources = legacyChannelSources,
            ShadowChannelSources = shadowChannelSources,
            LostByChannel = lostByChannel,
            LostByTopKCap = lostByTopKCap,
            LostByDisabledChannel = lostByDisabledChannel,
            ValidatorApplied = ReadBool(shadow.Diagnostics, "validatorApplied"),
            ValidPlan = ReadBool(shadow.Diagnostics, "validPlan"),
            NativeValidPlan = ReadBool(shadow.Diagnostics, "validPlan")
                && !ReadBool(shadow.Diagnostics, "repairedPlan"),
            RepairedPlan = ReadBool(shadow.Diagnostics, "repairedPlan"),
            FallbackToLegacySafePlan = ReadBool(shadow.Diagnostics, "fallbackToLegacySafePlan"),
            RejectedPlanReasons = SplitDiagnosticList(shadow.Diagnostics, "rejectedPlanReasons"),
            ValidatorRepairReasons = SplitDiagnosticList(shadow.Diagnostics, "validatorRepairReasons"),
            FallbackRootCause = ReadString(shadow.Diagnostics, "fallbackRootCause"),
            AfterRepairPlanSummary = ReadString(shadow.Diagnostics, "afterRepairPlanSummary"),
            FinalTopKClamped = ReadBool(shadow.Diagnostics, "finalTopKClamped"),
            VectorDisabled = ReadBool(shadow.Diagnostics, "vectorDisabled"),
            DeprecatedBlockedCount = ReadInt(shadow.Diagnostics, "deprecatedBlockedCount", 0),
            MustNotHitAddedAfterValidation = ReadInt(
                shadow.Diagnostics,
                "mustNotHitAddedAfterValidation",
                mustNotHitViolations.Length),
            LifecycleViolationAfterValidation = ReadInt(
                shadow.Diagnostics,
                "lifecycleViolationAfterValidation",
                lifecycleViolations),
            Warnings = shadow.Warnings,
            Diagnostics = shadow.Diagnostics
        };
    }

    private static IReadOnlyDictionary<string, RetrievalPlanRepairBreakdown> BuildBreakdown(
        IReadOnlyList<ShadowRetrievalComparisonItem> samples,
        Func<ShadowRetrievalComparisonItem, string> keySelector)
    {
        return samples
            .GroupBy(sample =>
            {
                var key = keySelector(sample);
                return string.IsNullOrWhiteSpace(key) ? "Unknown" : key;
            }, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var groupSamples = group.ToArray();
                    var nativeValid = groupSamples.Count(sample => sample.NativeValidPlan);
                    var repaired = groupSamples.Count(sample => sample.RepairedPlan);
                    var fallback = groupSamples.Count(sample => sample.FallbackToLegacySafePlan);
                    return new RetrievalPlanRepairBreakdown
                    {
                        Key = group.Key,
                        TotalSamples = groupSamples.Length,
                        NativeValidPlanCount = nativeValid,
                        RepairedPlanCount = repaired,
                        FallbackPlanCount = fallback,
                        NativeValidRate = groupSamples.Length == 0 ? 0 : (double)nativeValid / groupSamples.Length,
                        RepairReasonCounts = BuildRepairReasonCounts(groupSamples)
                    };
                },
                StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, int> BuildRepairReasonCounts(
        IReadOnlyList<ShadowRetrievalComparisonItem> samples)
    {
        var counts = RepairReasonCategories.ToDictionary(
            category => category,
            _ => 0,
            StringComparer.OrdinalIgnoreCase);

        foreach (var sample in samples)
        {
            foreach (var reason in sample.ValidatorRepairReasons.Select(NormalizeRepairReason))
            {
                if (counts.ContainsKey(reason))
                {
                    counts[reason]++;
                }
            }

            if (sample.DeprecatedBlockedCount > 0)
            {
                counts["DeprecatedBlocked"] += sample.DeprecatedBlockedCount;
                counts["InvalidNormalLifecycle"] += sample.DeprecatedBlockedCount;
            }
        }

        return counts;
    }

    private static string NormalizeRepairReason(string reason)
    {
        if (reason.Contains("finalTopK", StringComparison.OrdinalIgnoreCase))
        {
            return "FinalTopKClamped";
        }

        if (reason.Contains("keywordTopK", StringComparison.OrdinalIgnoreCase))
        {
            return "KeywordTopKClamped";
        }

        if (reason.Contains("memoryTopK", StringComparison.OrdinalIgnoreCase))
        {
            return "MemoryTopKClamped";
        }

        if (reason.Contains("relationTopK", StringComparison.OrdinalIgnoreCase))
        {
            return "RelationTopKClamped";
        }

        if (reason.Contains("vector", StringComparison.OrdinalIgnoreCase))
        {
            return "VectorDisabled";
        }

        if (reason.Contains("superseded", StringComparison.OrdinalIgnoreCase))
        {
            return "SupersededBlocked";
        }

        if (reason.Contains("deprecated", StringComparison.OrdinalIgnoreCase))
        {
            return "DeprecatedBlocked";
        }

        if (reason.Contains("lifecycle", StringComparison.OrdinalIgnoreCase))
        {
            return "InvalidNormalLifecycle";
        }

        return reason;
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

    private static IReadOnlyList<ShadowRetrievalRankDelta> BuildRankDeltas(
        IReadOnlyList<ContextRetrievalCandidate> legacySelected,
        IReadOnlyList<ContextRetrievalCandidate> shadowSelected)
    {
        var legacyRanks = BuildRankMap(legacySelected);
        var shadowRanks = BuildRankMap(shadowSelected);
        return legacyRanks.Keys
            .Union(shadowRanks.Keys, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(sourceId =>
            {
                var hasLegacy = legacyRanks.TryGetValue(sourceId, out var legacyRank);
                var hasShadow = shadowRanks.TryGetValue(sourceId, out var shadowRank);
                return new ShadowRetrievalRankDelta
                {
                    SourceId = sourceId,
                    LegacyRank = hasLegacy ? legacyRank : null,
                    ShadowRank = hasShadow ? shadowRank : null,
                    Delta = hasLegacy && hasShadow ? legacyRank - shadowRank : null
                };
            })
            .ToArray();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildChannelSources(
        IReadOnlyList<ContextRetrievalCandidate> candidates)
    {
        return candidates
            .GroupBy(candidate => candidate.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group
                    .SelectMany(ReadChannelSources)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ReadChannelSources(ContextRetrievalCandidate candidate)
    {
        return candidate.Metadata.TryGetValue("channelSources", out var value)
            ? value
                .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();
    }

    private static bool IsLostByDisabledChannel(
        string expected,
        IReadOnlyList<ContextRetrievalCandidate> legacySelected,
        IReadOnlyList<ContextRetrievalCandidate> shadowCandidates,
        IReadOnlyList<string> disabledChannels)
    {
        if (disabledChannels.Count == 0)
        {
            return false;
        }

        var legacyChannels = FindChannelsForRef(legacySelected, expected);
        if (legacyChannels.Count == 0)
        {
            return false;
        }

        var shadowChannels = FindChannelsForRef(shadowCandidates, expected);
        return legacyChannels.Any(channel => disabledChannels.Contains(channel, StringComparer.OrdinalIgnoreCase))
            && shadowChannels.Count == 0;
    }

    private static IReadOnlyDictionary<string, string> BuildLostByChannel(
        IReadOnlyList<string> mustHitDropped,
        IReadOnlyList<ContextRetrievalCandidate> legacySelected,
        IReadOnlyList<ContextRetrievalCandidate> shadowCandidates,
        IReadOnlyList<string> disabledChannels,
        IReadOnlyList<string> lostByTopKCap,
        IReadOnlyList<string> lostByDisabledChannel)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var expected in mustHitDropped)
        {
            var legacyChannels = FindChannelsForRef(legacySelected, expected);
            var shadowChannels = FindChannelsForRef(shadowCandidates, expected);
            var reason = lostByDisabledChannel.Contains(expected, StringComparer.OrdinalIgnoreCase)
                ? "disabledChannel"
                : lostByTopKCap.Contains(expected, StringComparer.OrdinalIgnoreCase)
                    ? "topKCap"
                    : shadowChannels.Count == 0
                        ? "shadowCandidateMissing"
                        : "shadowOrdering";
            result[expected] = string.Join(
                ';',
                $"legacy={FormatList(legacyChannels)}",
                $"shadowCandidate={FormatList(shadowChannels)}",
                $"disabled={FormatList(disabledChannels)}",
                $"reason={reason}");
        }

        return result;
    }

    private static IReadOnlyList<string> FindChannelsForRef(
        IReadOnlyList<ContextRetrievalCandidate> candidates,
        string expected)
    {
        return candidates
            .Where(candidate => ContainsRef(candidate, expected))
            .SelectMany(ReadChannelSources)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FormatList(IReadOnlyList<string> values)
    {
        return values.Count == 0 ? "(none)" : string.Join(",", values);
    }

    private static Dictionary<string, int> BuildRankMap(IReadOnlyList<ContextRetrievalCandidate> items)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < items.Count; i++)
        {
            result.TryAdd(items[i].SourceId, i + 1);
        }

        return result;
    }

    private static int CountMatchedRefs(
        IReadOnlyList<string> expectedRefs,
        IReadOnlyList<ContextRetrievalCandidate> selected)
    {
        return expectedRefs.Count(expected => ContainsRef(selected, expected));
    }

    private static IReadOnlyList<string> FindMatchedRefs(
        IReadOnlyList<string> expectedRefs,
        IReadOnlyList<ContextRetrievalCandidate> selected)
    {
        return expectedRefs
            .Where(expected => ContainsRef(selected, expected))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static double ResolveRecall(
        IReadOnlyList<string> expectedRefs,
        IReadOnlyList<ContextRetrievalCandidate> selected,
        int topK)
    {
        if (expectedRefs.Count == 0)
        {
            return 1.0;
        }

        var topItems = selected.Take(topK).ToArray();
        return (double)CountMatchedRefs(expectedRefs, topItems) / expectedRefs.Count;
    }

    private static double ResolveMrr(
        IReadOnlyList<string> expectedRefs,
        IReadOnlyList<ContextRetrievalCandidate> selected)
    {
        if (expectedRefs.Count == 0)
        {
            return 1.0;
        }

        for (var i = 0; i < selected.Count; i++)
        {
            if (expectedRefs.Any(expected => ContainsRef(selected[i], expected)))
            {
                return 1.0 / (i + 1);
            }
        }

        return 0.0;
    }

    private static double ResolveMustHitTokenShare(
        IReadOnlyList<string> expectedRefs,
        IReadOnlyList<ContextRetrievalCandidate> selected)
    {
        var totalTokens = selected.Sum(item => item.EstimatedTokens);
        if (totalTokens <= 0)
        {
            return 0.0;
        }

        var mustHitTokens = selected
            .Where(item => expectedRefs.Any(expected => ContainsRef(item, expected)))
            .Sum(item => item.EstimatedTokens);
        return (double)mustHitTokens / totalTokens;
    }

    private static bool ContainsRef(
        IReadOnlyList<ContextRetrievalCandidate> selected,
        string expected)
    {
        return selected.Any(item =>
            ContainsRef(item, expected));
    }

    private static bool ContainsRef(
        ContextRetrievalCandidate item,
        string expected)
    {
        return string.Equals(item.SourceId, expected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.CandidateId, expected, StringComparison.OrdinalIgnoreCase)
            || item.SourceRefs.Contains(expected, StringComparer.OrdinalIgnoreCase)
            || item.Tags.Contains(expected, StringComparer.OrdinalIgnoreCase);
    }

    private static int CountMatchedText(
        IReadOnlyList<string> expectedTexts,
        IReadOnlyList<ContextRetrievalCandidate> selected)
    {
        if (expectedTexts.Count == 0)
        {
            return 0;
        }

        return expectedTexts.Count(expected => selected.Any(item =>
            Contains(item.SourceId, expected)
            || Contains(item.CandidateId, expected)
            || Contains(item.Type, expected)
            || Contains(item.Title, expected)
            || Contains(item.Content, expected)
            || item.Tags.Any(tag => Contains(tag, expected))
            || item.SourceRefs.Any(sourceRef => Contains(sourceRef, expected))));
    }

    private static IReadOnlyList<string> FindMatchedTexts(
        IReadOnlyList<string> expectedTexts,
        IReadOnlyList<ContextRetrievalCandidate> selected)
    {
        return expectedTexts
            .Where(expected => selected.Any(item =>
                Contains(item.SourceId, expected)
                || Contains(item.CandidateId, expected)
                || Contains(item.Type, expected)
                || Contains(item.Title, expected)
                || Contains(item.Content, expected)
                || item.Tags.Any(tag => Contains(tag, expected))
                || item.SourceRefs.Any(sourceRef => Contains(sourceRef, expected))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<ContextRetrievalCandidate> ResolveProposalSelectedCandidates(
        ShadowRetrievalResult shadow,
        IReadOnlyList<ContextRetrievalCandidate> legacySelected,
        IReadOnlyList<ContextRetrievalCandidate> shadowSelected)
    {
        var proposalSelectedIds = SplitCsvDiagnosticList(shadow.Diagnostics, "planningProposalSelected");
        if (proposalSelectedIds.Count == 0)
        {
            return shadowSelected;
        }

        var allCandidates = shadow.ShadowCandidates
            .Concat(shadowSelected)
            .Concat(legacySelected)
            .GroupBy(item => item.SourceId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        return proposalSelectedIds
            .Select(id => allCandidates.FirstOrDefault(candidate =>
                string.Equals(candidate.SourceId, id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.CandidateId, id, StringComparison.OrdinalIgnoreCase)))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .ToArray();
    }

    private static double ResolveHitRate(int expectedCount, int hitCount)
    {
        return expectedCount == 0 ? 1.0 : (double)hitCount / expectedCount;
    }

    private static IReadOnlyList<string> BuildGained(
        IReadOnlyList<string> legacyMatches,
        IReadOnlyList<string> shadowMatches)
    {
        return shadowMatches
            .Except(legacyMatches, StringComparer.OrdinalIgnoreCase)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> BuildLost(
        IReadOnlyList<string> legacyMatches,
        IReadOnlyList<string> shadowMatches)
    {
        return legacyMatches
            .Except(shadowMatches, StringComparer.OrdinalIgnoreCase)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int CountLifecycleViolations(
        IReadOnlyList<ContextRetrievalCandidate> selected,
        IReadOnlyDictionary<string, string> diagnostics)
        => selected.Count(item => IsLifecycleViolation(item, diagnostics));

    private static bool IsLifecycleViolation(
        ContextRetrievalCandidate item,
        IReadOnlyDictionary<string, string> diagnostics)
    {
        var auditMode = diagnostics.TryGetValue("auditMode", out var auditValue)
            && bool.TryParse(auditValue, out var audit)
            && audit;
        var conflictMode = diagnostics.TryGetValue("conflictMode", out var conflictValue)
            && bool.TryParse(conflictValue, out var conflict)
            && conflict;
        var lifecycleAllowed = auditMode || conflictMode;

        var status = item.Metadata.TryGetValue("lifecycleStatus", out var lifecycle)
            ? lifecycle
            : item.Metadata.TryGetValue("status", out var contextStatus)
                ? contextStatus
                : item.Metadata.TryGetValue("processState", out var processState)
                    ? processState
                    : string.Empty;

        if (status.Equals(ContextMemoryStatus.Rejected.ToString(), StringComparison.OrdinalIgnoreCase)
            || status.Equals("rejected", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !lifecycleAllowed
            && (status.Equals(ContextMemoryStatus.Deprecated.ToString(), StringComparison.OrdinalIgnoreCase)
                || status.Equals("deprecated", StringComparison.OrdinalIgnoreCase)
                || status.Equals("superseded", StringComparison.OrdinalIgnoreCase)
                || item.Metadata.ContainsKey("supersededBy"));
    }

    private static bool Contains(string? value, string expected)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !string.IsNullOrWhiteSpace(expected)
            && value.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ReadBool(
        IReadOnlyDictionary<string, string> diagnostics,
        string key)
    {
        return diagnostics.TryGetValue(key, out var value)
            && bool.TryParse(value, out var parsed)
            && parsed;
    }

    private static int ReadInt(
        IReadOnlyDictionary<string, string> diagnostics,
        string key,
        int fallback)
    {
        return diagnostics.TryGetValue(key, out var value)
            && int.TryParse(value, out var parsed)
            ? parsed
            : fallback;
    }

    private static string ReadString(
        IReadOnlyDictionary<string, string> diagnostics,
        string key)
    {
        return diagnostics.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static bool ShouldSuppressRetainedLegacySafetyCounts(
        IReadOnlyDictionary<string, string> diagnostics)
    {
        if (!diagnostics.TryGetValue("planningExecutionStatus", out var status)
            || string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        return !string.Equals(status, RetrievalPlanningOptions.ApplyGuardedMode, StringComparison.OrdinalIgnoreCase);
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
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> SplitCsvDiagnosticList(
        IReadOnlyDictionary<string, string> diagnostics,
        string key)
    {
        if (!diagnostics.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
