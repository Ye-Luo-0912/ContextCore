using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V5.15 output token / priority policy shadow gate。
/// 只构建 shadow package projection 并校验预算、优先级与 hard-constraint 覆盖；
/// 不写 formal package，不改变 formal selected set、PackingPolicy、package output 或 runtime binding。
/// </summary>
public sealed class OutputTokenPriorityShadowGateRunner
{
    private const double Epsilon = 1e-9;
    private const string DefaultProfileName = SourceAwareRankingProfileIds.CombinedSafe;

    public OutputTokenPriorityShadowGateReport BuildShadow(
        RetrievalDatasetV2GeneratedDataset? dataset,
        SourceAwareRankingRepairReport? sourceAwareGate,
        RetrievalEvalProtocolGateReport? protocolGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        RuntimeObservableFeatureContractSourceScan? sourceScan,
        OutputTokenPriorityShadowGateOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(dataset, sourceAwareGate, protocolGate, runtimeChangeGate, sourceScan, options, sourceReports, gateMode: false);

    public OutputTokenPriorityShadowGateReport BuildGate(
        RetrievalDatasetV2GeneratedDataset? dataset,
        SourceAwareRankingRepairReport? sourceAwareGate,
        RetrievalEvalProtocolGateReport? protocolGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        RuntimeObservableFeatureContractSourceScan? sourceScan,
        OutputTokenPriorityShadowGateOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(dataset, sourceAwareGate, protocolGate, runtimeChangeGate, sourceScan, options, sourceReports, gateMode: true);

    public static string BuildMarkdown(string title, OutputTokenPriorityShadowGateReport report)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"OperationId: `{report.OperationId}`");
        b.AppendLine($"CreatedAt: `{report.CreatedAt:O}`");
        b.AppendLine();
        b.AppendLine("## Summary");
        b.AppendLine($"- ShadowPassed: `{report.ShadowPassed}`");
        b.AppendLine($"- GatePassed: `{report.GatePassed}`");
        b.AppendLine($"- Recommendation: `{report.Recommendation}`");
        b.AppendLine($"- ProfileName: `{report.ProfileName}`");
        b.AppendLine($"- Protocol: `{report.Protocol.ProtocolVersion}` topK vector/merged/final=`{report.Protocol.VectorTopK}/{report.Protocol.MergedTopK}/{report.Protocol.FinalTopK}`");
        b.AppendLine($"- SampleCount: `{report.SampleCount}`");
        b.AppendLine($"- CorpusItemCount: `{report.CorpusItemCount}`");
        b.AppendLine($"- BlindHoldoutSampleCount: `{report.BlindHoldoutSampleCount}`");
        b.AppendLine();
        b.AppendLine("## Package Shadow Metrics");
        b.AppendLine($"- BaselinePackageCount: `{report.BaselinePackageCount}`");
        b.AppendLine($"- ShadowPackageCount: `{report.ShadowPackageCount}`");
        b.AppendLine($"- BaselineTokenTotal: `{report.BaselineTokenTotal}`");
        b.AppendLine($"- ShadowTokenTotal: `{report.ShadowTokenTotal}`");
        b.AppendLine($"- TokenDeltaTotal: `{report.TokenDeltaTotal}`");
        b.AppendLine($"- TokenDeltaMax: `{report.TokenDeltaMax}`");
        b.AppendLine($"- TokenDeltaP95: `{report.TokenDeltaP95}`");
        b.AppendLine($"- TokenBudgetExceededCount: `{report.TokenBudgetExceededCount}`");
        b.AppendLine($"- SectionBudgetExceededCount: `{report.SectionBudgetExceededCount}`");
        b.AppendLine($"- PriorityDeltaCount: `{report.PriorityDeltaCount}`");
        b.AppendLine($"- PriorityInversionCount: `{report.PriorityInversionCount}`");
        b.AppendLine($"- MandatoryCoverageBaseline: `{report.MandatoryCoverageBaseline:F4}`");
        b.AppendLine($"- MandatoryCoverageShadow: `{report.MandatoryCoverageShadow:F4}`");
        b.AppendLine($"- MandatoryCoverageDelta: `{report.MandatoryCoverageDelta:+0.0000;-0.0000;0.0000}`");
        b.AppendLine($"- DroppedRequiredCandidateCount: `{report.DroppedRequiredCandidateCount}`");
        b.AppendLine($"- SectionMismatchCount: `{report.SectionMismatchCount}`");
        b.AppendLine();
        b.AppendLine("## Safety Invariants");
        b.AppendLine($"- Risk/mustNot/lifecycle: `{report.RiskAfterPolicy}` / `{report.MustNotHitRiskAfterPolicy}` / `{report.LifecycleRiskAfterPolicy}`");
        b.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        b.AppendLine($"- FormalSelectedSetChanged: `{report.FormalSelectedSetChanged}`");
        b.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        b.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        b.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        b.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        b.AppendLine($"- VectorStoreBindingChanged: `{report.VectorStoreBindingChanged}`");
        b.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        b.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        b.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        b.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        b.AppendLine();
        AppendSectionSummary(b, "Baseline Section Occupancy", report.BaselineSectionSummaries);
        AppendSectionSummary(b, "Shadow Section Occupancy", report.ShadowSectionSummaries);
        AppendList(b, "Blocked Reasons", report.BlockedReasons);
        AppendMap(b, "Source Reports", report.SourceReports);
        b.AppendLine();
        b.AppendLine("V5.15 shadow only. The locked `combined-safe` preview profile and V5.11 eval protocol are evaluated without formal selected-set, package output, PackingPolicy, runtime, or vector binding mutation.");
        return b.ToString();
    }

    private OutputTokenPriorityShadowGateReport Build(
        RetrievalDatasetV2GeneratedDataset? dataset,
        SourceAwareRankingRepairReport? sourceAwareGate,
        RetrievalEvalProtocolGateReport? protocolGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        RuntimeObservableFeatureContractSourceScan? sourceScan,
        OutputTokenPriorityShadowGateOptions? options,
        IReadOnlyDictionary<string, string>? sourceReports,
        bool gateMode)
    {
        options ??= new OutputTokenPriorityShadowGateOptions();
        var blocked = new List<string>();
        if (dataset is null || dataset.CorpusItems.Count == 0 || dataset.Samples.Count == 0)
        {
            blocked.Add("MissingDataset");
            dataset = new RetrievalDatasetV2GeneratedDataset();
        }

        if (options.RequireV514GatePassed && (sourceAwareGate is null || !sourceAwareGate.GatePassed))
        {
            blocked.Add("V514SourceAwareRankingGateNotPassed");
        }

        var profileName = string.IsNullOrWhiteSpace(options.ProfileName) ? DefaultProfileName : options.ProfileName;
        if (!string.Equals(profileName, DefaultProfileName, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("ProfileNotCombinedSafe");
        }

        if (options.RequireV511ProtocolGatePassed && (protocolGate is null || !protocolGate.GatePassed))
        {
            blocked.Add("V511ProtocolGateNotPassed");
        }

        if (options.RequireRuntimeChangeGate && (runtimeChangeGate is null || !runtimeChangeGate.Passed))
        {
            blocked.Add("RuntimeChangeReadinessGateNotPassed");
        }

        if (options.RequireSourceScan && (sourceScan is null || !sourceScan.ScanPerformed))
        {
            blocked.Add("SourceScanMissing");
        }

        if (sourceScan is not null && sourceScan.FixtureTokenHitCount > 0)
        {
            blocked.Add("EvalLabelOrFixtureSpecialCasingDetected");
        }

        if (options.UseForRuntime
            || options.FormalRetrievalAllowed
            || options.RuntimeSwitchAllowed
            || options.ReadyForRuntimeSwitch
            || options.WriteFormalPackage
            || options.MutatePackingPolicy
            || options.MutatePackageOutput
            || options.MutateFormalSelectedSet)
        {
            blocked.Add("RuntimeOrFormalMutationAttempt");
        }

        var protocol = protocolGate?.Protocol ?? options.Protocol ?? sourceAwareGate?.Protocol ?? new RetrievalEvalProtocol();
        var enrichedDataset = dataset.CorpusItems.Count == 0
            ? dataset
            : InputMetadataEnrichmentPreviewRunner.BuildEnrichedProjection(dataset);
        var topK = Math.Max(1, protocol.FinalTopK);
        var itemProfiles = enrichedDataset.CorpusItems.Select(BuildItemProfile).ToArray();
        var itemMap = itemProfiles.ToDictionary(static profile => profile.Item.ItemId, StringComparer.OrdinalIgnoreCase);
        var totals = new ShadowTotals();
        var baselineSections = new Dictionary<string, SectionAccumulator>(StringComparer.OrdinalIgnoreCase);
        var shadowSections = new Dictionary<string, SectionAccumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var sample in enrichedDataset.Samples)
        {
            var query = BuildQueryTokens(sample, includeRuntimeMetadata: true);
            var baselinePackage = BuildPackage(RankCandidates(query, itemProfiles, protocol, useSourceSignals: false), sample, itemMap, topK, options, baselineSections);
            var shadowPackage = BuildPackage(RankCandidates(query, itemProfiles, protocol, useSourceSignals: true), sample, itemMap, topK, options, shadowSections);
            totals.Add(EvaluateSample(sample, baselinePackage, shadowPackage, options));
        }

        if (totals.TokenBudgetExceededCount > 0)
        {
            blocked.Add("TokenBudgetExceeded");
        }

        if (totals.SectionBudgetExceededCount > 0)
        {
            blocked.Add("SectionBudgetExceeded");
        }

        if (totals.PriorityInversionCount > 0)
        {
            blocked.Add("PriorityInversionDetected");
        }

        if (totals.MandatoryCoverageDelta < -options.MetricTolerance)
        {
            blocked.Add("MandatoryCoverageRegression");
        }

        if (totals.HardConstraintCoverageDelta < -options.MetricTolerance)
        {
            blocked.Add("HardConstraintCoverageRegression");
        }

        if (totals.DroppedRequiredCandidateCount > 0)
        {
            blocked.Add("DroppedRequiredCandidate");
        }

        if (totals.SectionMismatchCount > 0)
        {
            blocked.Add("SectionMismatchDetected");
        }

        if (totals.RiskAfterPolicy > 0 || totals.MustNotHitRiskAfterPolicy > 0 || totals.LifecycleRiskAfterPolicy > 0)
        {
            blocked.Add("RiskAfterPolicyNonZero");
        }

        if (totals.FormalOutputChanged != 0
            || totals.FormalSelectedSetChanged
            || totals.FormalPackageWritten
            || totals.PackageOutputChanged
            || totals.PackingPolicyChanged
            || totals.RuntimeMutated
            || totals.VectorStoreBindingChanged)
        {
            blocked.Add("RuntimeOrPackageInvariantChanged");
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var shadowPassed = distinctBlocked.Length == 0;
        var gatePassed = gateMode && shadowPassed;

        return new OutputTokenPriorityShadowGateReport
        {
            OperationId = (gateMode ? "vector-output-token-priority-shadow-gate-" : "vector-output-token-priority-shadow-") + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            ShadowPassed = shadowPassed,
            GatePassed = gatePassed,
            Recommendation = ResolveRecommendation(shadowPassed, distinctBlocked),
            ProfileName = profileName,
            Protocol = protocol,
            CorpusItemCount = enrichedDataset.CorpusItems.Count,
            SampleCount = enrichedDataset.Samples.Count,
            BlindHoldoutSampleCount = enrichedDataset.Samples.Count(static sample => string.Equals(sample.Split, "blind-holdout", StringComparison.OrdinalIgnoreCase)),
            BaselinePackageCount = totals.BaselinePackageCount,
            ShadowPackageCount = totals.ShadowPackageCount,
            BaselineTokenTotal = totals.BaselineTokenTotal,
            ShadowTokenTotal = totals.ShadowTokenTotal,
            TokenDeltaTotal = totals.TokenDeltaTotal,
            TokenDeltaMax = totals.TokenDeltaMax,
            TokenDeltaP95 = Percentile95(totals.TokenDeltas),
            TokenBudgetLimit = options.TotalTokenBudget,
            PerPackageTokenBudgetLimit = options.PerPackageTokenBudget,
            SectionTokenBudgetLimit = options.SectionTokenBudget,
            TokenBudgetExceededCount = totals.TokenBudgetExceededCount,
            SectionBudgetExceededCount = totals.SectionBudgetExceededCount,
            PriorityDeltaCount = totals.PriorityDeltaCount,
            PriorityInversionCount = totals.PriorityInversionCount,
            MandatoryCoverageBaseline = totals.MandatoryCoverageBaseline,
            MandatoryCoverageShadow = totals.MandatoryCoverageShadow,
            MandatoryCoverageDelta = totals.MandatoryCoverageDelta,
            HardConstraintCoverageBaseline = totals.HardConstraintCoverageBaseline,
            HardConstraintCoverageShadow = totals.HardConstraintCoverageShadow,
            HardConstraintCoverageDelta = totals.HardConstraintCoverageDelta,
            DroppedRequiredCandidateCount = totals.DroppedRequiredCandidateCount,
            SectionMismatchCount = totals.SectionMismatchCount,
            RiskAfterPolicy = totals.RiskAfterPolicy,
            MustNotHitRiskAfterPolicy = totals.MustNotHitRiskAfterPolicy,
            LifecycleRiskAfterPolicy = totals.LifecycleRiskAfterPolicy,
            FormalOutputChanged = 0,
            FormalSelectedSetChanged = false,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            V514GatePassed = sourceAwareGate?.GatePassed ?? false,
            V511ProtocolGatePassed = protocolGate?.GatePassed ?? false,
            RuntimeChangeGatePassed = runtimeChangeGate?.Passed ?? false,
            SourceScan = sourceScan ?? new RuntimeObservableFeatureContractSourceScan(),
            BaselineSectionSummaries = baselineSections
                .OrderBy(static pair => SectionPriority(pair.Key))
                .ThenBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => pair.Value.ToSummary(pair.Key))
                .ToArray(),
            ShadowSectionSummaries = shadowSections
                .OrderBy(static pair => SectionPriority(pair.Key))
                .ThenBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => pair.Value.ToSummary(pair.Key))
                .ToArray(),
            SourceReports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            BlockedReasons = distinctBlocked
        };
    }

    private static IReadOnlyList<RankedPackageCandidate> RankCandidates(
        HashSet<string> query,
        IReadOnlyList<PackageItemProfile> items,
        RetrievalEvalProtocol protocol,
        bool useSourceSignals)
    {
        var sourceMax = ComputeSourceMax(query, items);
        var rankLimit = Math.Max(Math.Max(1, protocol.FinalTopK), Math.Max(protocol.MergedTopK, protocol.FinalTopK * 4));
        var candidates = new List<RankedPackageCandidate>(items.Count);
        foreach (var item in items)
        {
            var raw = ScoreSources(query, item);
            var normalized = NormalizeSources(raw, sourceMax);
            var dense = normalized[RetrievalCandidateSourceIds.Dense];
            var source = useSourceSignals
                ? CombineSourceScore(normalized)
                : dense;
            var confidence = ComputeConfidence(normalized, item.Item);
            var score = useSourceSignals
                ? dense * 2.40 + source
                : dense;
            if (confidence < 0.56 && dense <= Epsilon)
            {
                score = 0;
            }

            if (IsRuntimeRisk(item.Item))
            {
                score = 0;
            }

            if (score <= protocol.ScoreThreshold + Epsilon)
            {
                continue;
            }

            var priority = ComputePriority(item.Item, dense, source, confidence);
            candidates.Add(new RankedPackageCandidate(item.Item.ItemId, score, dense, source, confidence, priority, item.Item));
        }

        return candidates
            .OrderByDescending(static candidate => candidate.Score)
            .ThenByDescending(static candidate => candidate.Priority)
            .ThenByDescending(static candidate => candidate.DenseScore)
            .ThenBy(static candidate => candidate.ItemId, StringComparer.OrdinalIgnoreCase)
            .Take(rankLimit)
            .ToArray();
    }

    private static IReadOnlyList<PackageEntry> BuildPackage(
        IReadOnlyList<RankedPackageCandidate> ranked,
        RetrievalDatasetV2Sample sample,
        IReadOnlyDictionary<string, PackageItemProfile> itemMap,
        int topK,
        OutputTokenPriorityShadowGateOptions options,
        IDictionary<string, SectionAccumulator> sectionAccumulators)
    {
        var mustNot = new HashSet<string>(sample.MustNotHitItemIds, StringComparer.OrdinalIgnoreCase);
        var result = new List<PackageEntry>(topK);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in ranked)
        {
            if (mustNot.Contains(candidate.ItemId) || IsRuntimeRisk(candidate.Item) || !seen.Add(candidate.ItemId))
            {
                continue;
            }

            var tokens = EstimateTokens(candidate.Item);
            var priority = ComputePriority(candidate.Item, candidate.DenseScore, candidate.SourceScore, candidate.Confidence);
            var entry = new PackageEntry(
                candidate.ItemId,
                candidate.Item.TargetSection,
                tokens,
                priority,
                candidate.Score,
                candidate.Item);
            result.Add(entry);
            AddSection(sectionAccumulators, entry);
            if (result.Count >= topK)
            {
                break;
            }
        }

        // priority projection is a shadow package policy check, not a formal package order mutation.
        return result
            .OrderBy(static entry => SectionPriority(entry.TargetSection))
            .ThenByDescending(static entry => entry.Priority)
            .ThenByDescending(static entry => entry.Score)
            .ThenBy(static entry => entry.ItemId, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, options.MaxPackageItemCount))
            .ToArray();

        static void AddSection(IDictionary<string, SectionAccumulator> sections, PackageEntry entry)
        {
            if (!sections.TryGetValue(entry.TargetSection, out var acc))
            {
                acc = new SectionAccumulator();
                sections[entry.TargetSection] = acc;
            }

            acc.Add(entry);
        }
    }

    private static ShadowSampleResult EvaluateSample(
        RetrievalDatasetV2Sample sample,
        IReadOnlyList<PackageEntry> baseline,
        IReadOnlyList<PackageEntry> shadow,
        OutputTokenPriorityShadowGateOptions options)
    {
        var mustHit = new HashSet<string>(sample.MustHitItemIds, StringComparer.OrdinalIgnoreCase);
        var mustNot = new HashSet<string>(sample.MustNotHitItemIds, StringComparer.OrdinalIgnoreCase);
        var baselineIds = baseline.Select(static entry => entry.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shadowIds = shadow.Select(static entry => entry.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baselineHits = mustHit.Count == 0 ? 0 : mustHit.Count(baselineIds.Contains);
        var shadowHits = mustHit.Count == 0 ? 0 : mustHit.Count(shadowIds.Contains);
        var baselineCoverage = mustHit.Count == 0 ? 1 : baselineHits / (double)mustHit.Count;
        var shadowCoverage = mustHit.Count == 0 ? 1 : shadowHits / (double)mustHit.Count;
        var baselineTokens = baseline.Sum(static entry => entry.TokenCount);
        var shadowTokens = shadow.Sum(static entry => entry.TokenCount);
        var tokenDelta = shadowTokens - baselineTokens;
        var hardCoverageBaseline = ComputeHardCoverage(sample, baseline);
        var hardCoverageShadow = ComputeHardCoverage(sample, shadow);
        var droppedRequired = mustHit.Count(id => baselineIds.Contains(id) && !shadowIds.Contains(id));
        var sectionMismatch = shadow.Count(static entry =>
            !string.Equals(entry.TargetSection, entry.Item.TargetSection, StringComparison.OrdinalIgnoreCase));
        var priorityDelta = CountOrderDelta(baseline, shadow);
        var priorityInversion = CountPriorityInversions(shadow);
        var sectionBudgetExceeded = shadow
            .GroupBy(static entry => entry.TargetSection, StringComparer.OrdinalIgnoreCase)
            .Count(group => group.Sum(static entry => entry.TokenCount) > options.SectionTokenBudget);
        var mustNotRisk = shadow.Count(entry => mustNot.Contains(entry.ItemId));
        var lifecycleRisk = shadow.Count(static entry => IsRuntimeRisk(entry.Item));

        return new ShadowSampleResult(
            baseline.Count,
            shadow.Count,
            baselineTokens,
            shadowTokens,
            tokenDelta,
            Math.Abs(tokenDelta) > options.PerPackageTokenBudget || shadowTokens > options.TotalTokenBudget ? 1 : 0,
            sectionBudgetExceeded,
            priorityDelta,
            priorityInversion,
            baselineCoverage,
            shadowCoverage,
            hardCoverageBaseline,
            hardCoverageShadow,
            droppedRequired,
            sectionMismatch,
            mustNotRisk + lifecycleRisk,
            mustNotRisk,
            lifecycleRisk);
    }

    private static double ComputeHardCoverage(RetrievalDatasetV2Sample sample, IReadOnlyList<PackageEntry> package)
    {
        var constraints = 0;
        var satisfied = 0;
        if (sample.EvidenceRefs.Count > 0)
        {
            constraints++;
            if (package.SelectMany(static entry => entry.Item.EvidenceRefs).Any(reference => sample.EvidenceRefs.Contains(reference, StringComparer.OrdinalIgnoreCase)))
            {
                satisfied++;
            }
        }

        if (sample.SourceRefs.Count > 0)
        {
            constraints++;
            if (package.SelectMany(static entry => entry.Item.SourceRefs).Any(reference => sample.SourceRefs.Contains(reference, StringComparer.OrdinalIgnoreCase)))
            {
                satisfied++;
            }
        }

        if (sample.RequiredRelations.Count > 0)
        {
            constraints++;
            if (package.SelectMany(static entry => entry.Item.Relations).Any(relation => sample.RequiredRelations.Contains(relation.RelationId, StringComparer.OrdinalIgnoreCase)))
            {
                satisfied++;
            }
        }

        return constraints == 0 ? 1 : satisfied / (double)constraints;
    }

    private static int CountOrderDelta(IReadOnlyList<PackageEntry> baseline, IReadOnlyList<PackageEntry> shadow)
    {
        var max = Math.Max(baseline.Count, shadow.Count);
        var delta = 0;
        for (var i = 0; i < max; i++)
        {
            var left = i < baseline.Count ? baseline[i].ItemId : string.Empty;
            var right = i < shadow.Count ? shadow[i].ItemId : string.Empty;
            if (!string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            {
                delta++;
            }
        }

        return delta;
    }

    private static int CountPriorityInversions(IReadOnlyList<PackageEntry> package)
    {
        var count = 0;
        for (var i = 1; i < package.Count; i++)
        {
            var previousRank = SectionPriority(package[i - 1].TargetSection);
            var currentRank = SectionPriority(package[i].TargetSection);
            if (currentRank < previousRank
                || (currentRank == previousRank && package[i].Priority > package[i - 1].Priority + Epsilon))
            {
                count++;
            }
        }

        return count;
    }

    private static PackageItemProfile BuildItemProfile(RetrievalDatasetV2CorpusItem item)
    {
        var metadataText = item.Metadata.Count == 0
            ? string.Empty
            : string.Join(' ', item.Metadata.Select(static pair => pair.Key + " " + pair.Value));
        return new PackageItemProfile(
            item,
            Tokenize($"{item.Content} {item.TargetSection} {item.ItemKind} {item.SourceKind} {item.Layer} {string.Join(' ', item.Tags)}"),
            Tokenize(item.Content),
            Tokenize($"{string.Join(' ', item.Tags)} {string.Join(' ', item.Anchors)} {item.TargetSection}"),
            Tokenize($"{string.Join(' ', item.SourceRefs)} {string.Join(' ', item.EvidenceRefs)} {item.Provenance.RecordId} {item.Provenance.SourceFingerprint} {item.SourceFingerprint}"),
            Tokenize($"{string.Join(' ', item.Relations.Select(static relation => relation.RelationId))} {string.Join(' ', item.Relations.Select(static relation => relation.RelationType))} {string.Join(' ', item.Relations.SelectMany(static relation => relation.SourceRefs))} {string.Join(' ', item.Relations.SelectMany(static relation => relation.EvidenceRefs))}"),
            Tokenize($"{item.Lifecycle} {item.ReviewStatus} {item.ReplacementState} {item.TargetSection} {item.ItemKind} {item.SourceKind} {item.Layer} {metadataText}"));
    }

    private static HashSet<string> BuildQueryTokens(RetrievalDatasetV2Sample sample, bool includeRuntimeMetadata)
    {
        if (!includeRuntimeMetadata)
        {
            return Tokenize(sample.QueryText);
        }

        var metadataText = sample.Metadata.Count == 0
            ? string.Empty
            : string.Join(' ', sample.Metadata.Where(static pair =>
                    !string.Equals(pair.Key, "rationaleIndexed", StringComparison.OrdinalIgnoreCase))
                .Select(static pair => pair.Key + " " + pair.Value));
        return Tokenize($"{sample.QueryText} {string.Join(' ', sample.SourceRefs)} {string.Join(' ', sample.EvidenceRefs)} {string.Join(' ', sample.RequiredRelations)} {sample.Provenance.RecordId} {sample.Provenance.SourceFingerprint} {metadataText}");
    }

    private static Dictionary<string, double> ComputeSourceMax(HashSet<string> query, IReadOnlyList<PackageItemProfile> items)
    {
        var max = SourceIds.ToDictionary(static source => source, static _ => 0.0, StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            foreach (var pair in ScoreSources(query, item))
            {
                if (pair.Value > max[pair.Key])
                {
                    max[pair.Key] = pair.Value;
                }
            }
        }

        return max;
    }

    private static Dictionary<string, double> ScoreSources(HashSet<string> query, PackageItemProfile item)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            [RetrievalCandidateSourceIds.Dense] = CosineOverlap(query, item.DenseTokens),
            [RetrievalCandidateSourceIds.Lexical] = Jaccard(query, item.LexicalTokens),
            [RetrievalCandidateSourceIds.Anchor] = Coverage(query, item.AnchorTokens),
            [RetrievalCandidateSourceIds.EvidenceSource] = Coverage(query, item.EvidenceSourceTokens),
            [RetrievalCandidateSourceIds.Relation] = Coverage(query, item.RelationTokens),
            [RetrievalCandidateSourceIds.Metadata] = Coverage(query, item.MetadataTokens)
        };

    private static Dictionary<string, double> NormalizeSources(
        IReadOnlyDictionary<string, double> raw,
        IReadOnlyDictionary<string, double> max)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in SourceIds)
        {
            var denominator = max.TryGetValue(source, out var value) ? value : 0;
            result[source] = denominator <= Epsilon || !raw.TryGetValue(source, out var score)
                ? 0
                : score / denominator;
        }

        return result;
    }

    private static double CombineSourceScore(IReadOnlyDictionary<string, double> normalized)
    {
        static double Cap(double value) => Math.Min(0.24, value);
        return Cap(normalized[RetrievalCandidateSourceIds.Lexical]) * 0.34
            + Cap(normalized[RetrievalCandidateSourceIds.Anchor]) * 0.26
            + Cap(normalized[RetrievalCandidateSourceIds.EvidenceSource]) * 0.20
            + Cap(normalized[RetrievalCandidateSourceIds.Relation]) * 0.20
            + Cap(normalized[RetrievalCandidateSourceIds.Metadata]) * 0.14;
    }

    private static double ComputeConfidence(IReadOnlyDictionary<string, double> normalized, RetrievalDatasetV2CorpusItem item)
    {
        var active = normalized.Count(static pair => pair.Value > 0.05);
        var evidenceCoverage = item.SourceRefs.Count > 0 && item.EvidenceRefs.Count > 0 ? 0.20 : 0;
        var provenance = !string.IsNullOrWhiteSpace(item.Provenance.RecordId) ? 0.15 : 0;
        var relation = item.Relations.Count > 0 ? 0.10 : 0;
        return Math.Min(1.0, active * 0.16 + evidenceCoverage + provenance + relation);
    }

    private static double ComputePriority(RetrievalDatasetV2CorpusItem item, double dense, double source, double confidence)
    {
        var section = SectionPriority(item.TargetSection);
        var sectionWeight = Math.Max(0, 8 - section) * 0.10;
        var lifecycle = IsActiveLifecycle(item.Lifecycle) ? 0.20 : 0;
        var evidence = item.SourceRefs.Count > 0 && item.EvidenceRefs.Count > 0 ? 0.16 : 0;
        var relation = item.Relations.Count > 0 ? 0.08 : 0;
        return sectionWeight + lifecycle + evidence + relation + confidence * 0.20 + dense * 0.18 + source * 0.12;
    }

    private static int EstimateTokens(RetrievalDatasetV2CorpusItem item)
    {
        var tokenCount = Tokenize($"{item.Content} {item.ItemKind} {item.SourceKind} {item.TargetSection} {string.Join(' ', item.Tags)} {string.Join(' ', item.Anchors)} {string.Join(' ', item.SourceRefs)} {string.Join(' ', item.EvidenceRefs)}").Count;
        return Math.Max(1, tokenCount);
    }

    private static bool IsRuntimeRisk(RetrievalDatasetV2CorpusItem item)
        => string.Equals(item.TargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase)
            && (!IsActiveLifecycle(item.Lifecycle)
                || string.Equals(item.ReplacementState, "superseded", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.ReviewStatus, "rejected", StringComparison.OrdinalIgnoreCase));

    private static bool IsActiveLifecycle(string value)
        => string.Equals(value, "Active", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Current", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Stable", StringComparison.OrdinalIgnoreCase);

    private static double CosineOverlap(IReadOnlySet<string> query, IReadOnlySet<string> item)
    {
        if (query.Count == 0 || item.Count == 0)
        {
            return 0;
        }

        var overlap = query.Count(item.Contains);
        return overlap == 0 ? 0 : overlap / Math.Sqrt(query.Count * item.Count);
    }

    private static double Jaccard(IReadOnlySet<string> query, IReadOnlySet<string> item)
    {
        if (query.Count == 0 || item.Count == 0)
        {
            return 0;
        }

        var overlap = query.Count(item.Contains);
        var union = query.Count + item.Count - overlap;
        return union <= 0 ? 0 : overlap / (double)union;
    }

    private static double Coverage(IReadOnlySet<string> query, IReadOnlySet<string> item)
    {
        if (query.Count == 0 || item.Count == 0)
        {
            return 0;
        }

        var overlap = query.Count(item.Contains);
        return overlap / (double)query.Count;
    }

    private static HashSet<string> Tokenize(string? value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
            {
                builder.Append(char.ToLowerInvariant(ch));
                continue;
            }

            FlushToken(builder, result);
        }

        FlushToken(builder, result);
        return result;
    }

    private static void FlushToken(StringBuilder builder, ISet<string> result)
    {
        if (builder.Length == 0)
        {
            return;
        }

        result.Add(builder.ToString());
        builder.Clear();
    }

    private static int SectionPriority(string? section)
        => section switch
        {
            "must_hit_context" => 0,
            var value when string.Equals(value, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase) => 1,
            var value when string.Equals(value, VectorQueryTargetSections.WorkingContext, StringComparison.OrdinalIgnoreCase) => 2,
            var value when string.Equals(value, VectorQueryTargetSections.StableContext, StringComparison.OrdinalIgnoreCase) => 3,
            var value when string.Equals(value, VectorQueryTargetSections.HistoricalContext, StringComparison.OrdinalIgnoreCase) => 4,
            var value when string.Equals(value, VectorQueryTargetSections.AuditContext, StringComparison.OrdinalIgnoreCase) => 5,
            var value when string.Equals(value, VectorQueryTargetSections.DiagnosticsOnly, StringComparison.OrdinalIgnoreCase) => 6,
            _ => 7
        };

    private static int Percentile95(IReadOnlyList<int> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var ordered = values.Order().ToArray();
        var index = Math.Clamp((int)Math.Ceiling(ordered.Length * 0.95) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private static string ResolveRecommendation(bool shadowPassed, IReadOnlyList<string> blocked)
    {
        if (shadowPassed)
        {
            return OutputTokenPriorityShadowGateRecommendations.ReadyForOutputPolicyShadowFreeze;
        }

        if (blocked.Any(static reason => reason.Contains("Token", StringComparison.OrdinalIgnoreCase)))
        {
            return OutputTokenPriorityShadowGateRecommendations.BlockedByTokenBudget;
        }

        if (blocked.Any(static reason => reason.Contains("Priority", StringComparison.OrdinalIgnoreCase)))
        {
            return OutputTokenPriorityShadowGateRecommendations.BlockedByPriorityInversion;
        }

        if (blocked.Any(static reason => reason.Contains("Coverage", StringComparison.OrdinalIgnoreCase)))
        {
            return OutputTokenPriorityShadowGateRecommendations.BlockedByCoverageRegression;
        }

        if (blocked.Any(static reason => reason.Contains("DroppedRequired", StringComparison.OrdinalIgnoreCase)))
        {
            return OutputTokenPriorityShadowGateRecommendations.BlockedByDroppedRequiredCandidate;
        }

        if (blocked.Any(static reason => reason.Contains("Section", StringComparison.OrdinalIgnoreCase)))
        {
            return OutputTokenPriorityShadowGateRecommendations.BlockedBySectionMismatch;
        }

        if (blocked.Any(static reason => reason.Contains("Risk", StringComparison.OrdinalIgnoreCase)))
        {
            return OutputTokenPriorityShadowGateRecommendations.BlockedByRisk;
        }

        if (blocked.Any(static reason => reason.Contains("Gate", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("Protocol", StringComparison.OrdinalIgnoreCase)))
        {
            return OutputTokenPriorityShadowGateRecommendations.BlockedByMissingV514Gate;
        }

        if (blocked.Any(static reason => reason.Contains("Runtime", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("Package", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("Formal", StringComparison.OrdinalIgnoreCase)))
        {
            return OutputTokenPriorityShadowGateRecommendations.BlockedByRuntimeInvariant;
        }

        return OutputTokenPriorityShadowGateRecommendations.KeepPreviewOnly;
    }

    private static void AppendSectionSummary(StringBuilder b, string title, IReadOnlyList<OutputTokenPrioritySectionSummary> sections)
    {
        b.AppendLine();
        b.AppendLine($"## {title}");
        if (sections.Count == 0)
        {
            b.AppendLine("- none");
            return;
        }

        b.AppendLine("| section | item count | token total | token max |");
        b.AppendLine("|---|---:|---:|---:|");
        foreach (var section in sections)
        {
            b.AppendLine($"| `{Escape(section.Section)}` | {section.ItemCount} | {section.TokenTotal} | {section.TokenMax} |");
        }
    }

    private static void AppendList(StringBuilder b, string title, IReadOnlyList<string> values)
    {
        b.AppendLine();
        b.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            b.AppendLine("- none");
            return;
        }

        foreach (var value in values)
        {
            b.AppendLine($"- {Escape(value)}");
        }
    }

    private static void AppendMap(StringBuilder b, string title, IReadOnlyDictionary<string, string> values)
    {
        b.AppendLine();
        b.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            b.AppendLine("- none");
            return;
        }

        foreach (var pair in values.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            b.AppendLine($"- `{Escape(pair.Key)}`: `{Escape(pair.Value)}`");
        }
    }

    private static string Escape(string? value)
        => (value ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal);

    private static readonly string[] SourceIds =
    [
        RetrievalCandidateSourceIds.Dense,
        RetrievalCandidateSourceIds.Lexical,
        RetrievalCandidateSourceIds.Anchor,
        RetrievalCandidateSourceIds.EvidenceSource,
        RetrievalCandidateSourceIds.Relation,
        RetrievalCandidateSourceIds.Metadata
    ];

    private sealed record PackageItemProfile(
        RetrievalDatasetV2CorpusItem Item,
        HashSet<string> DenseTokens,
        HashSet<string> LexicalTokens,
        HashSet<string> AnchorTokens,
        HashSet<string> EvidenceSourceTokens,
        HashSet<string> RelationTokens,
        HashSet<string> MetadataTokens);

    private sealed record RankedPackageCandidate(
        string ItemId,
        double Score,
        double DenseScore,
        double SourceScore,
        double Confidence,
        double Priority,
        RetrievalDatasetV2CorpusItem Item);

    private sealed record PackageEntry(
        string ItemId,
        string TargetSection,
        int TokenCount,
        double Priority,
        double Score,
        RetrievalDatasetV2CorpusItem Item);

    private sealed record ShadowSampleResult(
        int BaselinePackageCount,
        int ShadowPackageCount,
        int BaselineTokenTotal,
        int ShadowTokenTotal,
        int TokenDelta,
        int TokenBudgetExceeded,
        int SectionBudgetExceeded,
        int PriorityDelta,
        int PriorityInversion,
        double MandatoryCoverageBaseline,
        double MandatoryCoverageShadow,
        double HardConstraintCoverageBaseline,
        double HardConstraintCoverageShadow,
        int DroppedRequiredCandidateCount,
        int SectionMismatchCount,
        int RiskAfterPolicy,
        int MustNotHitRiskAfterPolicy,
        int LifecycleRiskAfterPolicy);

    private sealed class SectionAccumulator
    {
        public int ItemCount { get; private set; }
        public int TokenTotal { get; private set; }
        public int TokenMax { get; private set; }

        public void Add(PackageEntry entry)
        {
            ItemCount++;
            TokenTotal += entry.TokenCount;
            TokenMax = Math.Max(TokenMax, entry.TokenCount);
        }

        public OutputTokenPrioritySectionSummary ToSummary(string section)
            => new()
            {
                Section = section,
                ItemCount = ItemCount,
                TokenTotal = TokenTotal,
                TokenMax = TokenMax
            };
    }

    private sealed class ShadowTotals
    {
        private int _samples;
        private double _mandatoryCoverageBaseline;
        private double _mandatoryCoverageShadow;
        private double _hardCoverageBaseline;
        private double _hardCoverageShadow;

        public List<int> TokenDeltas { get; } = new();
        public int BaselinePackageCount { get; private set; }
        public int ShadowPackageCount { get; private set; }
        public int BaselineTokenTotal { get; private set; }
        public int ShadowTokenTotal { get; private set; }
        public int TokenDeltaTotal { get; private set; }
        public int TokenDeltaMax { get; private set; }
        public int TokenBudgetExceededCount { get; private set; }
        public int SectionBudgetExceededCount { get; private set; }
        public int PriorityDeltaCount { get; private set; }
        public int PriorityInversionCount { get; private set; }
        public double MandatoryCoverageBaseline => _samples == 0 ? 0 : _mandatoryCoverageBaseline / _samples;
        public double MandatoryCoverageShadow => _samples == 0 ? 0 : _mandatoryCoverageShadow / _samples;
        public double MandatoryCoverageDelta => MandatoryCoverageShadow - MandatoryCoverageBaseline;
        public double HardConstraintCoverageBaseline => _samples == 0 ? 0 : _hardCoverageBaseline / _samples;
        public double HardConstraintCoverageShadow => _samples == 0 ? 0 : _hardCoverageShadow / _samples;
        public double HardConstraintCoverageDelta => HardConstraintCoverageShadow - HardConstraintCoverageBaseline;
        public int DroppedRequiredCandidateCount { get; private set; }
        public int SectionMismatchCount { get; private set; }
        public int RiskAfterPolicy { get; private set; }
        public int MustNotHitRiskAfterPolicy { get; private set; }
        public int LifecycleRiskAfterPolicy { get; private set; }
        public int FormalOutputChanged { get; }
        public bool FormalSelectedSetChanged { get; }
        public bool FormalPackageWritten { get; }
        public bool PackageOutputChanged { get; }
        public bool PackingPolicyChanged { get; }
        public bool RuntimeMutated { get; }
        public bool VectorStoreBindingChanged { get; }

        public void Add(ShadowSampleResult result)
        {
            _samples++;
            BaselinePackageCount += result.BaselinePackageCount;
            ShadowPackageCount += result.ShadowPackageCount;
            BaselineTokenTotal += result.BaselineTokenTotal;
            ShadowTokenTotal += result.ShadowTokenTotal;
            TokenDeltaTotal += result.TokenDelta;
            TokenDeltaMax = Math.Max(TokenDeltaMax, Math.Abs(result.TokenDelta));
            TokenDeltas.Add(Math.Abs(result.TokenDelta));
            TokenBudgetExceededCount += result.TokenBudgetExceeded;
            SectionBudgetExceededCount += result.SectionBudgetExceeded;
            PriorityDeltaCount += result.PriorityDelta;
            PriorityInversionCount += result.PriorityInversion;
            _mandatoryCoverageBaseline += result.MandatoryCoverageBaseline;
            _mandatoryCoverageShadow += result.MandatoryCoverageShadow;
            _hardCoverageBaseline += result.HardConstraintCoverageBaseline;
            _hardCoverageShadow += result.HardConstraintCoverageShadow;
            DroppedRequiredCandidateCount += result.DroppedRequiredCandidateCount;
            SectionMismatchCount += result.SectionMismatchCount;
            RiskAfterPolicy += result.RiskAfterPolicy;
            MustNotHitRiskAfterPolicy += result.MustNotHitRiskAfterPolicy;
            LifecycleRiskAfterPolicy += result.LifecycleRiskAfterPolicy;
        }
    }
}

public sealed class OutputTokenPriorityShadowGateOptions
{
    public RetrievalEvalProtocol? Protocol { get; init; }
    public string ProfileName { get; init; } = SourceAwareRankingProfileIds.CombinedSafe;
    public bool RequireV514GatePassed { get; init; } = true;
    public bool RequireV511ProtocolGatePassed { get; init; } = true;
    public bool RequireRuntimeChangeGate { get; init; } = true;
    public bool RequireSourceScan { get; init; } = true;
    public int TotalTokenBudget { get; init; } = 4096;
    public int PerPackageTokenBudget { get; init; } = 128;
    public int SectionTokenBudget { get; init; } = 2048;
    public int MaxPackageItemCount { get; init; } = 16;
    public double MetricTolerance { get; init; } = 1e-9;
    public bool UseForRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public bool WriteFormalPackage { get; init; }
    public bool MutatePackingPolicy { get; init; }
    public bool MutatePackageOutput { get; init; }
    public bool MutateFormalSelectedSet { get; init; }
}

public sealed class OutputTokenPriorityShadowGateReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ShadowPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = OutputTokenPriorityShadowGateRecommendations.KeepPreviewOnly;
    public string ProfileName { get; init; } = SourceAwareRankingProfileIds.CombinedSafe;
    public RetrievalEvalProtocol Protocol { get; init; } = new();
    public int CorpusItemCount { get; init; }
    public int SampleCount { get; init; }
    public int BlindHoldoutSampleCount { get; init; }
    public int BaselinePackageCount { get; init; }
    public int ShadowPackageCount { get; init; }
    public int BaselineTokenTotal { get; init; }
    public int ShadowTokenTotal { get; init; }
    public int TokenDeltaTotal { get; init; }
    public int TokenDeltaMax { get; init; }
    public int TokenDeltaP95 { get; init; }
    public int TokenBudgetLimit { get; init; }
    public int PerPackageTokenBudgetLimit { get; init; }
    public int SectionTokenBudgetLimit { get; init; }
    public int TokenBudgetExceededCount { get; init; }
    public int SectionBudgetExceededCount { get; init; }
    public int PriorityDeltaCount { get; init; }
    public int PriorityInversionCount { get; init; }
    public double MandatoryCoverageBaseline { get; init; }
    public double MandatoryCoverageShadow { get; init; }
    public double MandatoryCoverageDelta { get; init; }
    public double HardConstraintCoverageBaseline { get; init; }
    public double HardConstraintCoverageShadow { get; init; }
    public double HardConstraintCoverageDelta { get; init; }
    public int DroppedRequiredCandidateCount { get; init; }
    public int SectionMismatchCount { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int FormalOutputChanged { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public bool UseForRuntime { get; init; }
    public bool V514GatePassed { get; init; }
    public bool V511ProtocolGatePassed { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public RuntimeObservableFeatureContractSourceScan SourceScan { get; init; } = new();
    public IReadOnlyList<OutputTokenPrioritySectionSummary> BaselineSectionSummaries { get; init; } = Array.Empty<OutputTokenPrioritySectionSummary>();
    public IReadOnlyList<OutputTokenPrioritySectionSummary> ShadowSectionSummaries { get; init; } = Array.Empty<OutputTokenPrioritySectionSummary>();
    public IReadOnlyDictionary<string, string> SourceReports { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed class OutputTokenPrioritySectionSummary
{
    public string Section { get; init; } = string.Empty;
    public int ItemCount { get; init; }
    public int TokenTotal { get; init; }
    public int TokenMax { get; init; }
}

public static class OutputTokenPriorityShadowGateRecommendations
{
    public const string ReadyForOutputPolicyShadowFreeze = nameof(ReadyForOutputPolicyShadowFreeze);
    public const string BlockedByMissingV514Gate = nameof(BlockedByMissingV514Gate);
    public const string BlockedByTokenBudget = nameof(BlockedByTokenBudget);
    public const string BlockedByPriorityInversion = nameof(BlockedByPriorityInversion);
    public const string BlockedByCoverageRegression = nameof(BlockedByCoverageRegression);
    public const string BlockedByDroppedRequiredCandidate = nameof(BlockedByDroppedRequiredCandidate);
    public const string BlockedBySectionMismatch = nameof(BlockedBySectionMismatch);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}
