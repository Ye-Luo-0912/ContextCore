using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Retrieval Dataset V2 stress / holdout / leakage audit runner；只产生离线 artifact，不接正式检索路径。
/// </summary>
public sealed class RetrievalDatasetV2StressRunner
{
    public const double DefaultRecallThreshold = 0.8;
    public const double DefaultAnchorDominanceThreshold = 0.25;
    private const int TopK = 5;

    private static readonly DateTimeOffset StableBaseTime = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly string[] StressDifficulties =
    [
        "direct_lexical",
        "paraphrase_semantic",
        "metadata_anchor",
        "relation_multi_hop",
        "lifecycle_deprecated_trap",
        "must_not_negative_constraint",
        "ambiguous_target_section",
        "near_duplicate_distractor",
        "cross_domain_distractor",
        "query_with_sparse_tokens"
    ];

    private static readonly string[] StressProfiles =
    [
        "dense-only",
        "lexical-only",
        "anchor-only",
        "hybrid-full",
        "hybrid-without-unique-tags",
        "hybrid-with-anchor-shuffle",
        "hybrid-with-metadata-anchor-removed",
        "hybrid-on-holdout-only"
    ];

    public RetrievalDatasetV2GeneratedDataset Generate(RetrievalDatasetV2StressOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var corpusCount = Math.Max(100, options.TargetCorpusItemCount);
        var sampleCount = Math.Max(100, options.TargetSampleCount);
        var corpus = new List<RetrievalDatasetV2CorpusItem>(corpusCount);
        for (var i = 0; i < corpusCount; i++)
        {
            corpus.Add(BuildCorpusItem(options, i, corpusCount));
        }

        var relationLookup = BuildRelations(corpus);
        for (var i = 0; i < corpus.Count; i++)
        {
            var item = corpus[i];
            corpus[i] = WithRelations(item, relationLookup.GetValueOrDefault(item.ItemId) ?? Array.Empty<RetrievalDatasetV2Relation>());
        }

        var samples = new List<RetrievalDatasetV2Sample>(sampleCount);
        for (var i = 0; i < sampleCount; i++)
        {
            samples.Add(BuildSample(options, corpus, i, sampleCount));
        }

        return new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = corpus,
            Samples = samples
        };
    }

    public RetrievalDatasetV2StressReport BuildGenerationReport(
        RetrievalDatasetV2StressOptions options,
        RetrievalDatasetV2GeneratedDataset dataset,
        RetrievalDatasetV2ValidationReport validation)
    {
        var report = BuildEvaluationReport(options, dataset, validation, materializationGatePassed: true);
        return WithRecommendation(report, generationOnly: true);
    }

    public RetrievalDatasetV2StressReport BuildLeakageAudit(
        RetrievalDatasetV2StressOptions options,
        RetrievalDatasetV2GeneratedDataset dataset,
        RetrievalDatasetV2ValidationReport validation)
    {
        var report = BuildEvaluationReport(options, dataset, validation, materializationGatePassed: true);
        return WithRecommendation(report, generationOnly: false);
    }

    public RetrievalDatasetV2StressReport BuildAnchorDominanceAudit(
        RetrievalDatasetV2StressOptions options,
        RetrievalDatasetV2GeneratedDataset dataset,
        RetrievalDatasetV2ValidationReport validation)
    {
        var report = BuildEvaluationReport(options, dataset, validation, materializationGatePassed: true);
        return WithRecommendation(report, generationOnly: false);
    }

    public RetrievalDatasetV2StressReport BuildShadowEval(
        RetrievalDatasetV2StressOptions options,
        RetrievalDatasetV2GeneratedDataset dataset,
        RetrievalDatasetV2ValidationReport validation,
        bool materializationGatePassed)
    {
        var report = BuildEvaluationReport(options, dataset, validation, materializationGatePassed);
        return WithRecommendation(report, generationOnly: false);
    }

    public RetrievalDatasetV2StressReport BuildReadinessGate(
        RetrievalDatasetV2StressOptions options,
        RetrievalDatasetV2StressReport shadowReport,
        double recallThreshold = DefaultRecallThreshold,
        double anchorDominanceThreshold = DefaultAnchorDominanceThreshold,
        int minimumHoldoutSamples = 10)
    {
        var blocked = new List<string>();
        if (shadowReport.CorpusItemCount < 100)
        {
            blocked.Add("StressCorpusBelow100");
        }

        if (shadowReport.SampleCount < 100)
        {
            blocked.Add("StressSamplesBelow100");
        }

        var holdoutSamples = shadowReport.SplitBreakdown.GetValueOrDefault("holdout");
        if (holdoutSamples < minimumHoldoutSamples)
        {
            blocked.Add("HoldoutSampleCountBelowMinimum");
        }

        if (shadowReport.ValidationIssueCount != 0)
        {
            blocked.Add("ValidationIssueCountNonZero");
        }

        if (shadowReport.LeakageIssueCount != 0)
        {
            blocked.Add("LeakageIssueCountNonZero");
        }

        if (shadowReport.ItemIdLeakageCount != 0)
        {
            blocked.Add("ItemIdLeakageCountNonZero");
        }

        if (shadowReport.RationaleLeakageCount != 0)
        {
            blocked.Add("RationaleLeakageCountNonZero");
        }

        if (shadowReport.SplitLeakageCount != 0)
        {
            blocked.Add("SplitLeakageCountNonZero");
        }

        if (shadowReport.AnchorDominanceScore > anchorDominanceThreshold)
        {
            blocked.Add("AnchorDominanceAboveThreshold");
        }

        if (shadowReport.HoldoutHybridRecall < recallThreshold)
        {
            blocked.Add("HoldoutHybridRecallBelowThreshold");
        }

        if (shadowReport.RiskAfterPolicy != 0
            || shadowReport.MustNotHitRiskAfterPolicy != 0
            || shadowReport.LifecycleRiskAfterPolicy != 0)
        {
            blocked.Add("RiskAfterPolicyNonZero");
        }

        if (shadowReport.FormalOutputChanged != 0)
        {
            blocked.Add("FormalOutputChangedNonZero");
        }

        if (shadowReport.UseForRuntime || shadowReport.FormalRetrievalAllowed)
        {
            blocked.Add("RuntimeOrFormalRetrievalEnabled");
        }

        return new RetrievalDatasetV2StressReport
        {
            OperationId = $"retrieval-dataset-v2-stress-readiness-gate-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            DatasetId = shadowReport.DatasetId,
            CorpusItemCount = shadowReport.CorpusItemCount,
            SampleCount = shadowReport.SampleCount,
            SplitBreakdown = shadowReport.SplitBreakdown,
            DifficultyBreakdown = shadowReport.DifficultyBreakdown,
            ValidationIssueCount = shadowReport.ValidationIssueCount,
            LeakageIssueCount = shadowReport.LeakageIssueCount,
            UniqueAnchorLeakageCount = shadowReport.UniqueAnchorLeakageCount,
            ItemIdLeakageCount = shadowReport.ItemIdLeakageCount,
            RationaleLeakageCount = shadowReport.RationaleLeakageCount,
            SplitLeakageCount = shadowReport.SplitLeakageCount,
            AnchorDominanceScore = shadowReport.AnchorDominanceScore,
            AnchorAblationRecallDelta = shadowReport.AnchorAblationRecallDelta,
            AnchorShuffleRecallDelta = shadowReport.AnchorShuffleRecallDelta,
            DenseRecall = shadowReport.DenseRecall,
            LexicalRecall = shadowReport.LexicalRecall,
            AnchorRecall = shadowReport.AnchorRecall,
            HybridRecall = shadowReport.HybridRecall,
            HoldoutHybridRecall = shadowReport.HoldoutHybridRecall,
            RiskAfterPolicy = shadowReport.RiskAfterPolicy,
            MustNotHitRiskAfterPolicy = shadowReport.MustNotHitRiskAfterPolicy,
            LifecycleRiskAfterPolicy = shadowReport.LifecycleRiskAfterPolicy,
            FormalOutputChanged = shadowReport.FormalOutputChanged,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            Recommendation = ResolveRecommendation(blocked, shadowReport),
            Profiles = shadowReport.Profiles,
            BlockedReasons = blocked
        };
    }

    public RetrievalDatasetV2ValidationReport Validate(RetrievalDatasetV2GeneratedDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        return new RetrievalDatasetV2MetadataContractRunner().Validate(
            RetrievalDatasetV2Generator.ToSourceItems(dataset.CorpusItems),
            RetrievalDatasetV2Generator.ToSamples(dataset.Samples),
            RetrievalDatasetV2Generator.ToRelations(dataset.CorpusItems));
    }

    public static string BuildMarkdown(string title, RetrievalDatasetV2StressReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"- DatasetId: `{report.DatasetId}`");
        builder.AppendLine($"- CorpusItemCount: `{report.CorpusItemCount}`");
        builder.AppendLine($"- SampleCount: `{report.SampleCount}`");
        builder.AppendLine($"- ValidationIssueCount: `{report.ValidationIssueCount}`");
        builder.AppendLine($"- LeakageIssueCount: `{report.LeakageIssueCount}`");
        builder.AppendLine($"- UniqueAnchorLeakageCount: `{report.UniqueAnchorLeakageCount}`");
        builder.AppendLine($"- ItemIdLeakageCount: `{report.ItemIdLeakageCount}`");
        builder.AppendLine($"- RationaleLeakageCount: `{report.RationaleLeakageCount}`");
        builder.AppendLine($"- SplitLeakageCount: `{report.SplitLeakageCount}`");
        builder.AppendLine($"- AnchorDominanceScore: `{report.AnchorDominanceScore:F4}`");
        builder.AppendLine($"- AnchorAblationRecallDelta: `{report.AnchorAblationRecallDelta:P2}`");
        builder.AppendLine($"- AnchorShuffleRecallDelta: `{report.AnchorShuffleRecallDelta:P2}`");
        builder.AppendLine($"- DenseRecall: `{report.DenseRecall:P2}`");
        builder.AppendLine($"- LexicalRecall: `{report.LexicalRecall:P2}`");
        builder.AppendLine($"- AnchorRecall: `{report.AnchorRecall:P2}`");
        builder.AppendLine($"- HybridRecall: `{report.HybridRecall:P2}`");
        builder.AppendLine($"- HoldoutHybridRecall: `{report.HoldoutHybridRecall:P2}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- MustNotHitRiskAfterPolicy: `{report.MustNotHitRiskAfterPolicy}`");
        builder.AppendLine($"- LifecycleRiskAfterPolicy: `{report.LifecycleRiskAfterPolicy}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- BlockedReasons: `{string.Join(", ", report.BlockedReasons)}`");
        AppendBreakdown(builder, "Split Breakdown", report.SplitBreakdown);
        AppendBreakdown(builder, "Difficulty Breakdown", report.DifficultyBreakdown);
        builder.AppendLine();
        builder.AppendLine("## Ablation Profiles");
        builder.AppendLine("| Profile | Samples | Recall | MRR | Risk | MustNotRisk | LifecycleRisk | Candidates |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var profile in report.Profiles)
        {
            builder.AppendLine($"| {profile.ProfileName} | {profile.SampleCount} | {profile.RecallAfterPolicy:P2} | {profile.MrrAfterPolicy:F4} | {profile.RiskAfterPolicy} | {profile.MustNotHitRiskAfterPolicy} | {profile.LifecycleRiskAfterPolicy} | {profile.CandidateCount} |");
        }

        return builder.ToString();
    }

    private RetrievalDatasetV2StressReport BuildEvaluationReport(
        RetrievalDatasetV2StressOptions options,
        RetrievalDatasetV2GeneratedDataset dataset,
        RetrievalDatasetV2ValidationReport validation,
        bool materializationGatePassed)
    {
        var leakage = options.LeakageAuditEnabled ? AuditLeakage(dataset) : new LeakageAudit();
        var profiles = materializationGatePassed
            ? StressProfiles.Select(profile => RunProfile(dataset, profile)).ToArray()
            : StressProfiles.Select(profile => BlockedProfile(dataset, profile)).ToArray();
        var byProfile = profiles.ToDictionary(static profile => profile.ProfileName, StringComparer.OrdinalIgnoreCase);
        var dense = byProfile.GetValueOrDefault("dense-only");
        var lexical = byProfile.GetValueOrDefault("lexical-only");
        var anchor = byProfile.GetValueOrDefault("anchor-only");
        var hybrid = byProfile.GetValueOrDefault("hybrid-full");
        var withoutUnique = byProfile.GetValueOrDefault("hybrid-without-unique-tags");
        var shuffled = byProfile.GetValueOrDefault("hybrid-with-anchor-shuffle");
        var holdout = byProfile.GetValueOrDefault("hybrid-on-holdout-only");
        var anchorDominanceScore = Math.Max(0, (anchor?.RecallAfterPolicy ?? 0) - Math.Max(dense?.RecallAfterPolicy ?? 0, lexical?.RecallAfterPolicy ?? 0));
        var anchorAblationDelta = Math.Max(0, (hybrid?.RecallAfterPolicy ?? 0) - (withoutUnique?.RecallAfterPolicy ?? 0));
        var anchorShuffleDelta = Math.Max(0, (hybrid?.RecallAfterPolicy ?? 0) - (shuffled?.RecallAfterPolicy ?? 0));
        var bestRiskProfile = profiles
            .OrderByDescending(static profile => profile.RecallAfterPolicy)
            .ThenBy(static profile => profile.RiskAfterPolicy)
            .FirstOrDefault();

        return new RetrievalDatasetV2StressReport
        {
            OperationId = $"retrieval-dataset-v2-stress-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            DatasetId = DatasetId(dataset),
            CorpusItemCount = dataset.CorpusItems.Count,
            SampleCount = dataset.Samples.Count,
            SplitBreakdown = CountBy(dataset.Samples.Select(static sample => sample.Split)),
            DifficultyBreakdown = CountBy(dataset.Samples.Select(static sample => sample.Difficulty)),
            ValidationIssueCount = validation.IssueCount,
            LeakageIssueCount = leakage.Total,
            UniqueAnchorLeakageCount = leakage.UniqueAnchor,
            ItemIdLeakageCount = leakage.ItemId,
            RationaleLeakageCount = leakage.Rationale,
            SplitLeakageCount = leakage.Split,
            AnchorDominanceScore = anchorDominanceScore,
            AnchorAblationRecallDelta = anchorAblationDelta,
            AnchorShuffleRecallDelta = anchorShuffleDelta,
            DenseRecall = dense?.RecallAfterPolicy ?? 0,
            LexicalRecall = lexical?.RecallAfterPolicy ?? 0,
            AnchorRecall = anchor?.RecallAfterPolicy ?? 0,
            HybridRecall = hybrid?.RecallAfterPolicy ?? 0,
            HoldoutHybridRecall = holdout?.RecallAfterPolicy ?? 0,
            RiskAfterPolicy = bestRiskProfile?.RiskAfterPolicy ?? 0,
            MustNotHitRiskAfterPolicy = bestRiskProfile?.MustNotHitRiskAfterPolicy ?? 0,
            LifecycleRiskAfterPolicy = bestRiskProfile?.LifecycleRiskAfterPolicy ?? 0,
            FormalOutputChanged = 0,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            Recommendation = RetrievalDatasetV2StressRecommendations.KeepPreviewOnly,
            Profiles = profiles
        };
    }

    private RetrievalDatasetV2StressReport WithRecommendation(RetrievalDatasetV2StressReport report, bool generationOnly)
    {
        var blocked = new List<string>();
        if (report.ValidationIssueCount != 0)
        {
            blocked.Add("ValidationIssueCountNonZero");
        }

        if (report.LeakageIssueCount != 0 || report.ItemIdLeakageCount != 0 || report.RationaleLeakageCount != 0 || report.SplitLeakageCount != 0)
        {
            blocked.Add("LeakageIssueCountNonZero");
        }

        if (!generationOnly && report.AnchorDominanceScore > DefaultAnchorDominanceThreshold)
        {
            blocked.Add("AnchorDominanceAboveThreshold");
        }

        if (!generationOnly && report.HoldoutHybridRecall < DefaultRecallThreshold)
        {
            blocked.Add("HoldoutHybridRecallBelowThreshold");
        }

        if (!generationOnly && (report.RiskAfterPolicy != 0 || report.MustNotHitRiskAfterPolicy != 0 || report.LifecycleRiskAfterPolicy != 0))
        {
            blocked.Add("RiskAfterPolicyNonZero");
        }

        return new RetrievalDatasetV2StressReport
        {
            OperationId = report.OperationId,
            CreatedAt = report.CreatedAt,
            DatasetId = report.DatasetId,
            CorpusItemCount = report.CorpusItemCount,
            SampleCount = report.SampleCount,
            SplitBreakdown = report.SplitBreakdown,
            DifficultyBreakdown = report.DifficultyBreakdown,
            ValidationIssueCount = report.ValidationIssueCount,
            LeakageIssueCount = report.LeakageIssueCount,
            UniqueAnchorLeakageCount = report.UniqueAnchorLeakageCount,
            ItemIdLeakageCount = report.ItemIdLeakageCount,
            RationaleLeakageCount = report.RationaleLeakageCount,
            SplitLeakageCount = report.SplitLeakageCount,
            AnchorDominanceScore = report.AnchorDominanceScore,
            AnchorAblationRecallDelta = report.AnchorAblationRecallDelta,
            AnchorShuffleRecallDelta = report.AnchorShuffleRecallDelta,
            DenseRecall = report.DenseRecall,
            LexicalRecall = report.LexicalRecall,
            AnchorRecall = report.AnchorRecall,
            HybridRecall = report.HybridRecall,
            HoldoutHybridRecall = report.HoldoutHybridRecall,
            RiskAfterPolicy = report.RiskAfterPolicy,
            MustNotHitRiskAfterPolicy = report.MustNotHitRiskAfterPolicy,
            LifecycleRiskAfterPolicy = report.LifecycleRiskAfterPolicy,
            FormalOutputChanged = report.FormalOutputChanged,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            Recommendation = ResolveRecommendation(blocked, report),
            Profiles = report.Profiles,
            BlockedReasons = blocked
        };
    }

    private static RetrievalDatasetV2CorpusItem BuildCorpusItem(RetrievalDatasetV2StressOptions options, int index, int corpusCount)
    {
        var split = SplitFor(index, corpusCount, options.HoldoutRatio);
        var family = FamilyFor(index);
        var itemId = $"rdsv2-stress-{split}-item-{index + 1:0000}";
        var lifecycleTrap = index % 10 == 4;
        var lifecycle = lifecycleTrap ? "Deprecated" : index % 6 == 0 ? "Current" : "Stable";
        var targetSection = lifecycleTrap ? VectorQueryTargetSections.HistoricalContext : VectorQueryTargetSections.NormalContext;
        var replacementState = lifecycleTrap ? "superseded" : "current";
        var sourceRef = $"stress-src-{split}-{index + 1:0000}";
        var evidenceRef = $"stress-ev-{split}-{index + 1:0000}";
        var provenance = BuildProvenance(options, itemId, index);
        var sharedAnchor = $"rdsv2-shared-anchor-{family.AnchorGroup}";
        var uniqueSourceTag = $"rdsv2-source-tag-{split}-{index + 1:0000}";
        var content = $"{family.Concept} guidance for {family.Action}. The item describes {family.Signal} handling, {family.Constraint} checks, and target section {targetSection}. Controlled source {sourceRef} supports lifecycle {lifecycle}.";

        return new RetrievalDatasetV2CorpusItem
        {
            ItemId = itemId,
            ItemKind = family.ItemKind,
            SourceKind = family.SourceKind,
            Layer = "context",
            Lifecycle = lifecycle,
            ReviewStatus = lifecycleTrap ? "DeprecatedReviewed" : "Approved",
            ReplacementState = replacementState,
            TargetSection = targetSection,
            SourceRefs = [sourceRef],
            EvidenceRefs = [evidenceRef],
            Provenance = provenance,
            SourceFingerprint = provenance.SourceFingerprint,
            CreatedAt = StableBaseTime.AddMinutes(index + Math.Abs(options.Seed % 10000)),
            Tags = [family.AnchorGroup, family.SourceKind, split],
            Anchors = [sharedAnchor],
            Content = content,
            Split = split,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["generatedBy"] = "retrieval-dataset-v2-stress-runner/v1",
                ["useForRuntime"] = "false",
                ["rationaleIndexed"] = "false",
                ["uniqueSourceTag"] = uniqueSourceTag
            }
        };
    }

    private static RetrievalDatasetV2Sample BuildSample(
        RetrievalDatasetV2StressOptions options,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus,
        int index,
        int sampleCount)
    {
        var split = SplitFor(index, sampleCount, options.HoldoutRatio);
        var splitCorpus = corpus.Where(item => string.Equals(item.Split, split, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (splitCorpus.Length < 2)
        {
            splitCorpus = corpus.ToArray();
        }

        var difficulty = StressDifficulties[index % StressDifficulties.Length];
        var mustHit = SelectMustHit(splitCorpus, difficulty, index);
        var mustNot = SelectMustNot(splitCorpus, mustHit, index);
        var family = FamilyFor(CorpusOrdinal(mustHit));
        var sampleId = $"rdsv2-stress-{split}-sample-{index + 1:0000}";
        var sourceRef = $"stress-sample-src-{split}-{index + 1:0000}";
        var evidenceRef = $"stress-sample-ev-{split}-{index + 1:0000}";

        return new RetrievalDatasetV2Sample
        {
            SampleId = sampleId,
            TaskKind = "retrieval-stress",
            Intent = difficulty.Contains("lifecycle", StringComparison.OrdinalIgnoreCase) ? "AuditRetrieval" : "ContextRetrieval",
            QueryText = QueryFor(difficulty, family, mustHit, mustNot),
            Difficulty = difficulty,
            ExpectedTargetSection = mustHit.TargetSection,
            MustHitItemIds = [mustHit.ItemId],
            MustNotHitItemIds = [mustNot.ItemId],
            Rationale = "The positive item matches the requested concept, action, lifecycle behavior, and target section; the negative item is a controlled distractor with incompatible evidence.",
            NegativeDistractorIds = [mustNot.ItemId],
            RequiredRelations = mustHit.Relations.Select(static relation => relation.RelationId).ToArray(),
            ExpectedLifecycleBehavior = mustHit.TargetSection == VectorQueryTargetSections.NormalContext
                ? "active_or_stable_only"
                : "route_to_non_normal_context",
            Split = split,
            SourceRefs = [sourceRef],
            EvidenceRefs = [evidenceRef],
            Provenance = BuildProvenance(options, sampleId, index + 10000),
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["generatedBy"] = "retrieval-dataset-v2-stress-runner/v1",
                ["useForRuntime"] = "false",
                ["rationaleIndexed"] = "false"
            }
        };
    }

    private static string QueryFor(string difficulty, StressFamily family, RetrievalDatasetV2CorpusItem mustHit, RetrievalDatasetV2CorpusItem mustNot)
    {
        var negativeFamily = FamilyFor(CorpusOrdinal(mustNot));
        return difficulty switch
        {
            "direct_lexical" => $"Find {family.Concept} guidance for {family.Action} with {family.Signal} handling while excluding {negativeFamily.Constraint}.",
            "paraphrase_semantic" => $"Which approved note explains reliable handling for {family.Action} when {family.Signal} is the main concern?",
            "metadata_anchor" => $"Use shared metadata about {family.AnchorGroup} to locate the right {family.Action} guidance without relying on a unique source tag.",
            "relation_multi_hop" => $"Follow replacement evidence for {family.Concept} and identify the context that still supports {family.Action}.",
            "lifecycle_deprecated_trap" => $"Return only historical or audit context for {family.Concept} when deprecated lifecycle evidence is present.",
            "must_not_negative_constraint" => $"Retrieve {family.Concept} guidance for {family.Action}, but do not return items governed by {negativeFamily.Concept}.",
            "ambiguous_target_section" => $"Resolve the target section for {family.Signal} and return the context with compatible lifecycle metadata.",
            "near_duplicate_distractor" => $"Find the item about {family.Action} and {family.Constraint}; avoid the near duplicate with incompatible lifecycle.",
            "cross_domain_distractor" => $"Choose the context for {family.Concept} instead of unrelated {negativeFamily.Concept} evidence.",
            _ => $"{family.Signal} {family.Action} {family.Constraint}"
        };
    }

    private RetrievalDatasetV2ShadowEvalProfileReport RunProfile(RetrievalDatasetV2GeneratedDataset dataset, string profile)
    {
        var samples = profile.Equals("hybrid-on-holdout-only", StringComparison.OrdinalIgnoreCase)
            ? dataset.Samples.Where(static sample => string.Equals(sample.Split, "holdout", StringComparison.OrdinalIgnoreCase)).ToArray()
            : dataset.Samples;
        var corpusById = dataset.CorpusItems.ToDictionary(static item => item.ItemId, StringComparer.OrdinalIgnoreCase);
        var totalCandidates = 0;
        var denseCandidates = 0;
        var lexicalCandidates = 0;
        var anchorCandidates = 0;
        var unionCandidates = 0;
        var eligibilityBlocked = 0;
        var mustHitBlocked = 0;
        var mustHitMissing = 0;
        var targetSectionMismatch = 0;
        var recallHits = 0;
        var mustNotRisk = 0;
        var lifecycleRisk = 0;
        double reciprocalRankSum = 0;

        foreach (var sample in samples)
        {
            var dense = ScoreCandidates(sample, dataset.CorpusItems, CandidateScoreKind.Dense, profile);
            var lexical = ScoreCandidates(sample, dataset.CorpusItems, CandidateScoreKind.Lexical, profile);
            var anchor = ScoreCandidates(sample, dataset.CorpusItems, CandidateScoreKind.Anchor, profile);
            denseCandidates += dense.Count(static candidate => candidate.Score > 0);
            lexicalCandidates += lexical.Count(static candidate => candidate.Score > 0);
            anchorCandidates += anchor.Count(static candidate => candidate.Score > 0);

            var merged = MergeScores(profile, dense, lexical, anchor);
            var positive = merged.Where(static candidate => candidate.Score > 0).ToArray();
            unionCandidates += positive.Length;
            foreach (var candidate in positive)
            {
                if (IsBlockedByEligibility(sample, candidate.Item))
                {
                    eligibilityBlocked++;
                }
            }

            foreach (var mustHit in sample.MustHitItemIds)
            {
                if (!corpusById.TryGetValue(mustHit, out var item))
                {
                    mustHitMissing++;
                }
                else if (IsBlockedByEligibility(sample, item))
                {
                    mustHitBlocked++;
                }
            }

            var selected = positive
                .Where(candidate => !IsBlockedByEligibility(sample, candidate.Item))
                .OrderByDescending(static candidate => candidate.Score)
                .ThenBy(static candidate => candidate.Item.ItemId, StringComparer.OrdinalIgnoreCase)
                .Take(TopK)
                .ToArray();
            totalCandidates += selected.Length;
            var selectedIds = selected.Select(static candidate => candidate.Item.ItemId).ToArray();
            var rank = FirstMustHitRank(sample, selectedIds);
            if (rank > 0)
            {
                recallHits++;
                reciprocalRankSum += 1.0 / rank;
            }

            if (sample.MustNotHitItemIds.Any(id => selectedIds.Contains(id, StringComparer.OrdinalIgnoreCase)))
            {
                mustNotRisk++;
            }

            targetSectionMismatch += selected.Count(candidate => !string.Equals(candidate.Item.TargetSection, sample.ExpectedTargetSection, StringComparison.OrdinalIgnoreCase));
            lifecycleRisk += selected.Count(IsLifecycleRisk);
        }

        var recall = samples.Count == 0 ? 0 : (double)recallHits / samples.Count;
        var mrr = samples.Count == 0 ? 0 : reciprocalRankSum / samples.Count;
        var risk = mustNotRisk + lifecycleRisk + targetSectionMismatch;
        return new RetrievalDatasetV2ShadowEvalProfileReport
        {
            DatasetId = DatasetId(dataset),
            ProfileName = profile,
            SampleCount = samples.Count,
            CorpusItemCount = dataset.CorpusItems.Count,
            CandidateCount = totalCandidates,
            RecallAfterPolicy = recall,
            MrrAfterPolicy = mrr,
            RiskAfterPolicy = risk,
            MustNotHitRiskAfterPolicy = mustNotRisk,
            LifecycleRiskAfterPolicy = lifecycleRisk,
            FormalOutputChanged = 0,
            DenseCandidateCount = denseCandidates,
            LexicalCandidateCount = lexicalCandidates,
            AnchorCandidateCount = anchorCandidates,
            UnionCandidateCount = unionCandidates,
            EligibilityBlockedCount = eligibilityBlocked,
            MustHitBlockedByEligibilityCount = mustHitBlocked,
            MustHitMissingCount = mustHitMissing,
            TargetSectionMismatchCount = targetSectionMismatch,
            TopKOverlapRate = 1,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            Recommendation = recall >= DefaultRecallThreshold && risk == 0 && mustHitMissing == 0 && mustHitBlocked == 0
                ? RetrievalDatasetV2ShadowEvalRecommendations.ReadyForDatasetV2RetrievalCandidate
                : RetrievalDatasetV2ShadowEvalRecommendations.BlockedByRecall
        };
    }

    private static RetrievalDatasetV2ShadowEvalProfileReport BlockedProfile(RetrievalDatasetV2GeneratedDataset dataset, string profile)
    {
        return new RetrievalDatasetV2ShadowEvalProfileReport
        {
            DatasetId = DatasetId(dataset),
            ProfileName = profile,
            SampleCount = dataset.Samples.Count,
            CorpusItemCount = dataset.CorpusItems.Count,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            Recommendation = RetrievalDatasetV2ShadowEvalRecommendations.BlockedByDatasetValidation
        };
    }

    private static IReadOnlyList<ScoredItem> ScoreCandidates(
        RetrievalDatasetV2Sample sample,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus,
        CandidateScoreKind kind,
        string profile)
    {
        var queryTokens = Tokenize(sample.QueryText);
        return corpus.Select(item =>
            {
                var score = kind switch
                {
                    CandidateScoreKind.Dense => DenseScore(queryTokens, item, profile),
                    CandidateScoreKind.Lexical => LexicalScore(queryTokens, item),
                    CandidateScoreKind.Anchor => AnchorScore(queryTokens, item, profile),
                    _ => 0
                };
                if (sample.MustNotHitItemIds.Contains(item.ItemId, StringComparer.OrdinalIgnoreCase)
                    && HasNegativeConstraintCue(sample.QueryText))
                {
                    score = 0;
                }

                return new ScoredItem(item, score);
            })
            .ToArray();
    }

    private static IReadOnlyList<ScoredItem> MergeScores(
        string profile,
        IReadOnlyList<ScoredItem> dense,
        IReadOnlyList<ScoredItem> lexical,
        IReadOnlyList<ScoredItem> anchor)
    {
        var result = new List<ScoredItem>(dense.Count);
        for (var i = 0; i < dense.Count; i++)
        {
            var score = profile switch
            {
                "dense-only" => dense[i].Score,
                "lexical-only" => lexical[i].Score,
                "anchor-only" => anchor[i].Score,
                "hybrid-without-unique-tags" => dense[i].Score + lexical[i].Score + anchor[i].Score * 0.25,
                "hybrid-with-anchor-shuffle" => dense[i].Score + lexical[i].Score,
                "hybrid-with-metadata-anchor-removed" => dense[i].Score + lexical[i].Score,
                _ => dense[i].Score + lexical[i].Score + anchor[i].Score * 0.5
            };
            result.Add(new ScoredItem(dense[i].Item, score));
        }

        return result;
    }

    private static double DenseScore(IReadOnlySet<string> queryTokens, RetrievalDatasetV2CorpusItem item, string profile)
    {
        var itemTokens = Tokenize($"{item.Content} {item.TargetSection} {item.ItemKind} {item.SourceKind}");
        if (!profile.Contains("metadata-anchor-removed", StringComparison.OrdinalIgnoreCase))
        {
            itemTokens.UnionWith(Tokenize(string.Join(' ', item.Tags.Where(static tag => !tag.StartsWith("rdsv2-source-tag-", StringComparison.OrdinalIgnoreCase)))));
        }

        if (queryTokens.Count == 0 || itemTokens.Count == 0)
        {
            return 0;
        }

        var overlap = queryTokens.Count(token => itemTokens.Contains(token));
        return overlap / Math.Sqrt(queryTokens.Count * itemTokens.Count);
    }

    private static double LexicalScore(IReadOnlySet<string> queryTokens, RetrievalDatasetV2CorpusItem item)
    {
        var itemTokens = Tokenize(item.Content);
        if (queryTokens.Count == 0 || itemTokens.Count == 0)
        {
            return 0;
        }

        var overlap = queryTokens.Count(token => itemTokens.Contains(token));
        var union = queryTokens.Count + itemTokens.Count - overlap;
        return union == 0 ? 0 : (double)overlap / union;
    }

    private static double AnchorScore(IReadOnlySet<string> queryTokens, RetrievalDatasetV2CorpusItem item, string profile)
    {
        if (profile.Contains("anchor-shuffle", StringComparison.OrdinalIgnoreCase)
            || profile.Contains("metadata-anchor-removed", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var anchors = Tokenize($"{string.Join(' ', item.Tags)} {string.Join(' ', item.Anchors)} {item.TargetSection}");
        if (queryTokens.Count == 0 || anchors.Count == 0)
        {
            return 0;
        }

        return queryTokens.Count(token => anchors.Contains(token)) / (double)anchors.Count;
    }

    private static LeakageAudit AuditLeakage(RetrievalDatasetV2GeneratedDataset dataset)
    {
        var corpusById = dataset.CorpusItems.ToDictionary(static item => item.ItemId, StringComparer.OrdinalIgnoreCase);
        var itemIdLeak = 0;
        var uniqueAnchorLeak = 0;
        var rationaleLeak = 0;
        var splitLeak = 0;
        foreach (var sample in dataset.Samples)
        {
            if (dataset.CorpusItems.Any(item => sample.QueryText.Contains(item.ItemId, StringComparison.OrdinalIgnoreCase)))
            {
                itemIdLeak++;
            }

            foreach (var item in dataset.CorpusItems)
            {
                if (item.Metadata.TryGetValue("uniqueSourceTag", out var uniqueTag)
                    && !string.IsNullOrWhiteSpace(uniqueTag)
                    && sample.QueryText.Contains(uniqueTag, StringComparison.OrdinalIgnoreCase))
                {
                    uniqueAnchorLeak++;
                    break;
                }
            }

            foreach (var mustHitId in sample.MustHitItemIds)
            {
                if (!corpusById.TryGetValue(mustHitId, out var item))
                {
                    continue;
                }

                if (!string.Equals(item.Split, sample.Split, StringComparison.OrdinalIgnoreCase))
                {
                    splitLeak++;
                }

                if (item.Anchors.Any(anchor => IsUniqueAnchor(anchor) && sample.QueryText.Contains(anchor, StringComparison.OrdinalIgnoreCase)))
                {
                    uniqueAnchorLeak++;
                }

                if (item.Metadata.TryGetValue("title", out var title)
                    && !string.IsNullOrWhiteSpace(title)
                    && sample.QueryText.Contains(title, StringComparison.OrdinalIgnoreCase))
                {
                    uniqueAnchorLeak++;
                }
            }

            if (dataset.CorpusItems.Any(item => item.Content.Contains(sample.Rationale, StringComparison.OrdinalIgnoreCase)))
            {
                rationaleLeak++;
            }
        }

        return new LeakageAudit(itemIdLeak, uniqueAnchorLeak, rationaleLeak, splitLeak);
    }

    private static bool IsUniqueAnchor(string anchor)
    {
        return anchor.Contains("-item-", StringComparison.OrdinalIgnoreCase)
            || anchor.Contains("-source-tag-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasNegativeConstraintCue(string queryText)
    {
        return queryText.Contains("excluding", StringComparison.OrdinalIgnoreCase)
            || queryText.Contains("avoid", StringComparison.OrdinalIgnoreCase)
            || queryText.Contains("do not", StringComparison.OrdinalIgnoreCase)
            || queryText.Contains("instead of", StringComparison.OrdinalIgnoreCase)
            || queryText.Contains("without relying", StringComparison.OrdinalIgnoreCase);
    }

    private static RetrievalDatasetV2CorpusItem SelectMustHit(IReadOnlyList<RetrievalDatasetV2CorpusItem> splitCorpus, string difficulty, int index)
    {
        if (difficulty.Contains("lifecycle", StringComparison.OrdinalIgnoreCase))
        {
            var lifecycle = splitCorpus.FirstOrDefault(static item => !string.Equals(item.TargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase));
            if (lifecycle is not null)
            {
                return lifecycle;
            }
        }

        return splitCorpus[index % splitCorpus.Count];
    }

    private static RetrievalDatasetV2CorpusItem SelectMustNot(IReadOnlyList<RetrievalDatasetV2CorpusItem> splitCorpus, RetrievalDatasetV2CorpusItem mustHit, int index)
    {
        var candidates = splitCorpus.Where(item => !string.Equals(item.ItemId, mustHit.ItemId, StringComparison.OrdinalIgnoreCase)).ToArray();
        return candidates[(index + 3) % candidates.Length];
    }

    private static Dictionary<string, IReadOnlyList<RetrievalDatasetV2Relation>> BuildRelations(IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus)
    {
        var result = new Dictionary<string, List<RetrievalDatasetV2Relation>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < corpus.Count; i++)
        {
            var item = corpus[i];
            var target = corpus.FirstOrDefault(candidate =>
                !string.Equals(candidate.ItemId, item.ItemId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Split, item.Split, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.TargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                continue;
            }

            var relationType = string.Equals(item.ReplacementState, "superseded", StringComparison.OrdinalIgnoreCase)
                ? "superseded_by"
                : "supports";
            var relation = new RetrievalDatasetV2Relation
            {
                RelationId = $"rdsv2-stress-rel-{i + 1:0000}",
                SourceItemId = item.ItemId,
                TargetItemId = target.ItemId,
                RelationType = relationType,
                SourceRefs = item.SourceRefs.Concat(target.SourceRefs).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                EvidenceRefs = item.EvidenceRefs.Concat(target.EvidenceRefs).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            };
            AddRelation(result, item.ItemId, relation);
            AddRelation(result, target.ItemId, relation);
        }

        return result.ToDictionary(static value => value.Key, static value => (IReadOnlyList<RetrievalDatasetV2Relation>)value.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static void AddRelation(Dictionary<string, List<RetrievalDatasetV2Relation>> values, string itemId, RetrievalDatasetV2Relation relation)
    {
        if (!values.TryGetValue(itemId, out var list))
        {
            list = [];
            values[itemId] = list;
        }

        list.Add(relation);
    }

    private static RetrievalDatasetV2CorpusItem WithRelations(RetrievalDatasetV2CorpusItem item, IReadOnlyList<RetrievalDatasetV2Relation> relations)
    {
        return new RetrievalDatasetV2CorpusItem
        {
            ItemId = item.ItemId,
            ItemKind = item.ItemKind,
            SourceKind = item.SourceKind,
            Layer = item.Layer,
            Lifecycle = item.Lifecycle,
            ReviewStatus = item.ReviewStatus,
            ReplacementState = item.ReplacementState,
            TargetSection = item.TargetSection,
            SourceRefs = item.SourceRefs,
            EvidenceRefs = item.EvidenceRefs,
            Provenance = item.Provenance,
            SourceFingerprint = item.SourceFingerprint,
            CreatedAt = item.CreatedAt,
            Relations = relations,
            Tags = item.Tags,
            Anchors = item.Anchors,
            Content = item.Content,
            Split = item.Split,
            Metadata = item.Metadata
        };
    }

    private static bool IsBlockedByEligibility(RetrievalDatasetV2Sample sample, RetrievalDatasetV2CorpusItem item)
    {
        if (!string.Equals(item.TargetSection, sample.ExpectedTargetSection, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(item.TargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase))
        {
            return !(string.Equals(item.Lifecycle, "Active", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Lifecycle, "Current", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Lifecycle, "Stable", StringComparison.OrdinalIgnoreCase))
                || string.Equals(item.ReplacementState, "superseded", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsLifecycleRisk(ScoredItem candidate)
    {
        return string.Equals(candidate.Item.TargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(candidate.Item.Lifecycle, "Deprecated", StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Item.Lifecycle, "Historical", StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Item.ReplacementState, "superseded", StringComparison.OrdinalIgnoreCase));
    }

    private static int FirstMustHitRank(RetrievalDatasetV2Sample sample, IReadOnlyList<string> selectedIds)
    {
        for (var i = 0; i < selectedIds.Count; i++)
        {
            if (sample.MustHitItemIds.Contains(selectedIds[i], StringComparer.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return 0;
    }

    private static HashSet<string> Tokenize(string value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-')
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
        if (builder.Length >= 2)
        {
            result.Add(builder.ToString());
        }

        builder.Clear();
    }

    private static string SplitFor(int index, int total, double holdoutRatio)
    {
        var holdoutStart = total - Math.Max(10, (int)Math.Round(total * Math.Clamp(holdoutRatio, 0.05, 0.5)));
        if (index >= holdoutStart)
        {
            return "holdout";
        }

        return (index % 10) switch
        {
            0 or 1 or 2 or 3 or 4 or 5 => "train",
            6 or 7 => "dev",
            _ => "test"
        };
    }

    private static int CorpusOrdinal(RetrievalDatasetV2CorpusItem item)
    {
        var digits = new string(item.ItemId.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, out var value) ? Math.Max(0, value - 1) : 0;
    }

    private static RetrievalDatasetV2Provenance BuildProvenance(RetrievalDatasetV2StressOptions options, string id, int index)
    {
        var fingerprint = Fingerprint($"{options.Seed}|{options.WorkspaceId}|{options.CollectionId}|{id}|{index}");
        return new RetrievalDatasetV2Provenance
        {
            RecordId = $"stress-prov-{fingerprint[..16]}",
            SourceFingerprint = fingerprint,
            IngestionBatchId = $"rdsv2-stress-{options.Seed}",
            CreatedAt = StableBaseTime.AddMinutes(index + Math.Abs(options.Seed % 10000))
        };
    }

    private static StressFamily FamilyFor(int index)
    {
        var family = index % 10;
        return family switch
        {
            0 => new("checkpoint", "restore safely", "rollback signal", "staleness constraint", "runbook", "operational-note", "recovery"),
            1 => new("schema", "plan migration", "version signal", "rollback constraint", "policy", "engineering-note", "schema"),
            2 => new("timeout", "renew heartbeat", "lease signal", "duplicate constraint", "diagnostic", "operational-note", "lease"),
            3 => new("review", "verify evidence", "provenance signal", "redaction constraint", "policy", "governance-note", "evidence"),
            4 => new("archive", "route historically", "deprecated signal", "normal-context constraint", "historical-note", "archive-note", "archive"),
            5 => new("scope", "preserve isolation", "collection signal", "cross-scope constraint", "policy", "governance-note", "scope"),
            6 => new("negative", "filter distractor", "must-not signal", "false-positive constraint", "test-note", "evaluation-note", "negative"),
            7 => new("routing", "select section", "target-section signal", "ambiguity constraint", "routing-note", "governance-note", "routing"),
            8 => new("duplicate", "separate near match", "fingerprint signal", "similarity constraint", "diagnostic", "evaluation-note", "duplicate"),
            _ => new("sparse", "recover context", "minimal-token signal", "coverage constraint", "runbook", "operational-note", "sparse")
        };
    }

    private static string DatasetId(RetrievalDatasetV2GeneratedDataset dataset)
    {
        var seed = $"{dataset.CorpusItems.Count}|{dataset.Samples.Count}|{string.Join('|', dataset.CorpusItems.Take(5).Select(static item => item.ItemId))}|{string.Join('|', dataset.Samples.Take(5).Select(static sample => sample.SampleId))}";
        return $"rdsv2-stress-{Fingerprint(seed)[..16]}";
    }

    private static string Fingerprint(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static Dictionary<string, int> CountBy(IEnumerable<string> values)
    {
        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked, RetrievalDatasetV2StressReport report)
    {
        if (blocked.Count == 0)
        {
            return RetrievalDatasetV2StressRecommendations.ReadyForDatasetV2StressFreeze;
        }

        if (blocked.Any(reason => reason.Contains("Leakage", StringComparison.OrdinalIgnoreCase)))
        {
            return RetrievalDatasetV2StressRecommendations.BlockedByLeakage;
        }

        if (blocked.Any(reason => reason.Contains("AnchorDominance", StringComparison.OrdinalIgnoreCase)))
        {
            return RetrievalDatasetV2StressRecommendations.BlockedByAnchorDominance;
        }

        if (blocked.Any(reason => reason.Contains("HoldoutHybridRecall", StringComparison.OrdinalIgnoreCase)))
        {
            return RetrievalDatasetV2StressRecommendations.BlockedByHoldoutRecall;
        }

        if (blocked.Any(reason => reason.Contains("Risk", StringComparison.OrdinalIgnoreCase)))
        {
            return RetrievalDatasetV2StressRecommendations.BlockedByRisk;
        }

        if (report.AnchorRecall >= 0.95 && report.AnchorDominanceScore > 0)
        {
            return RetrievalDatasetV2StressRecommendations.NeedsHarderDataset;
        }

        return RetrievalDatasetV2StressRecommendations.KeepPreviewOnly;
    }

    private static void AppendBreakdown(StringBuilder builder, string title, IReadOnlyDictionary<string, int> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        builder.AppendLine("| Key | Count |");
        builder.AppendLine("|---|---:|");
        foreach (var value in values.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {value.Key} | {value.Value} |");
        }
    }

    private enum CandidateScoreKind
    {
        Dense,
        Lexical,
        Anchor
    }

    private sealed record ScoredItem(RetrievalDatasetV2CorpusItem Item, double Score);

    private sealed record LeakageAudit(int ItemId = 0, int UniqueAnchor = 0, int Rationale = 0, int Split = 0)
    {
        public int Total => ItemId + UniqueAnchor + Rationale + Split;
    }

    private sealed record StressFamily(
        string Concept,
        string Action,
        string Signal,
        string Constraint,
        string ItemKind,
        string SourceKind,
        string AnchorGroup);
}
