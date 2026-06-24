using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Planning;

namespace ContextCore.Core.Services;

/// <summary>生成只读 learning feature dataset；不参与在线 retrieval / planning / package 决策。</summary>
public sealed class LearningFeatureDatasetService
{
    public const string PolicyVersion = "learning-feature-dataset/v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] DefaultEvalReportPaths =
    [
        "eval/eval-report-p15-a3.json",
        "eval/eval-report-p15-extended.json"
    ];

    private static readonly string[] DefaultPlanningShadowReportPaths =
    [
        "eval/planning-shadow-comparison-a3.json",
        "eval/planning-shadow-comparison-extended.json"
    ];

    private readonly PolicyFeedbackDatasetService? _policyFeedbackDatasetService;
    private readonly PlanningIntentDetector _intentDetector;

    public LearningFeatureDatasetService(
        PolicyFeedbackDatasetService? policyFeedbackDatasetService = null,
        PlanningIntentDetector? intentDetector = null)
    {
        _policyFeedbackDatasetService = policyFeedbackDatasetService;
        _intentDetector = intentDetector ?? new PlanningIntentDetector();
    }

    public async Task<LearningFeatureDataset> BuildAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        int limit = 500,
        int offset = 0,
        IReadOnlyList<string>? evalReportPaths = null,
        IReadOnlyList<string>? planningShadowReportPaths = null,
        string? latestExportPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var policyFeedback = _policyFeedbackDatasetService is null
            ? CreateEmptyPolicyFeedbackDataset(workspaceId, collectionId, sessionId)
            : await _policyFeedbackDatasetService
                .BuildAsync(workspaceId, collectionId, sessionId, int.MaxValue, 0, cancellationToken)
                .ConfigureAwait(false);

        var evalReports = await LoadEvalReportsAsync(evalReportPaths ?? DefaultEvalReportPaths, cancellationToken)
            .ConfigureAwait(false);
        var planningReports = await LoadPlanningShadowReportsAsync(
            planningShadowReportPaths ?? DefaultPlanningShadowReportPaths,
            cancellationToken).ConfigureAwait(false);

        return Build(
            policyFeedback,
            evalReports,
            planningReports,
            latestExportPath ?? ResolveLatestExportPath("learning/features"),
            limit,
            offset);
    }

    public LearningFeatureDataset Build(
        PolicyFeedbackDataset policyFeedback,
        IReadOnlyList<ContextEvalReport>? evalReports = null,
        IReadOnlyList<ShadowRetrievalComparisonReport>? planningShadowReports = null,
        string? latestExportPath = null,
        int limit = 500,
        int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(policyFeedback);

        var policyExamples = GeneratePolicyFeedbackFeatureExamples(policyFeedback).ToArray();
        var rankingPairs = (evalReports ?? Array.Empty<ContextEvalReport>())
            .SelectMany(GenerateRankingPairsFromEvalReport)
            .ToArray();
        var routerExamples = (planningShadowReports ?? Array.Empty<ShadowRetrievalComparisonReport>())
            .SelectMany(GenerateRouterIntentExamples)
            .ToArray();

        var pagedPolicyExamples = policyExamples
            .Skip(Math.Max(0, offset))
            .Take(limit > 0 ? limit : 500)
            .ToArray();

        var labelDistribution = policyExamples
            .Concat(routerExamples)
            .GroupBy(example => string.IsNullOrWhiteSpace(example.Label) ? "Unknown" : example.Label, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var sourceTypeDistribution = policyExamples
            .Concat(routerExamples)
            .GroupBy(example => string.IsNullOrWhiteSpace(example.SourceType) ? "Unknown" : example.SourceType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return new LearningFeatureDataset
        {
            DatasetId = BuildId("learning-feature-dataset", $"{policyFeedback.DatasetId}\u001f{policyExamples.Length}\u001f{rankingPairs.Length}\u001f{routerExamples.Length}"),
            CreatedAt = DateTimeOffset.UtcNow,
            FeatureExamples = pagedPolicyExamples,
            RankingPairs = rankingPairs,
            RouterIntentExamples = routerExamples,
            FeatureCount = policyExamples.Length,
            RankingPairCount = rankingPairs.Length,
            RouterIntentExampleCount = routerExamples.Length,
            LabelDistribution = labelDistribution,
            SourceTypeDistribution = sourceTypeDistribution,
            LatestExportPath = latestExportPath ?? string.Empty,
            PolicyVersion = PolicyVersion
        };
    }

    public IReadOnlyList<ContextPolicyFeatureExample> GeneratePolicyFeedbackFeatureExamples(
        PolicyFeedbackDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        return dataset.Records
            .Select(record => BuildPolicyFeedbackFeatureExample(record))
            .OrderByDescending(example => example.CreatedAt)
            .ThenBy(example => example.ExampleId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<RankingPairExample> GenerateRankingPairsFromEvalReport(ContextEvalReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var pairs = new List<RankingPairExample>();
        foreach (var result in report.Results)
        {
            var positives = ResolveMustHitCandidates(result).ToArray();
            var negatives = ResolveMustNotHitCandidates(result).ToArray();
            if (positives.Length == 0 || negatives.Length == 0)
            {
                continue;
            }

            var intent = DetectIntent(result.Query, result.Mode);
            foreach (var positive in positives)
            {
                foreach (var negative in negatives)
                {
                    var positiveDiagnostic = FindDiagnostic(result, positive);
                    var negativeDiagnostic = FindDiagnostic(result, negative);
                    pairs.Add(new RankingPairExample
                    {
                        Query = result.Query,
                        Mode = result.Mode,
                        Intent = intent,
                        PositiveCandidateId = positive,
                        NegativeCandidateId = negative,
                        Reason = "eval mustHit should rank above mustNotHit",
                        EvalSampleId = result.SampleId,
                        FeatureSnapshot = BuildRankingFeatureSnapshot(result, positive, negative, positiveDiagnostic, negativeDiagnostic)
                    });
                }
            }
        }

        return pairs
            .OrderBy(pair => pair.EvalSampleId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.PositiveCandidateId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.NegativeCandidateId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<ContextPolicyFeatureExample> GenerateRouterIntentExamples(
        ShadowRetrievalComparisonReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Samples
            .Select(sample => BuildRouterIntentExample(report, sample))
            .OrderBy(example => example.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<LearningFeatureExportResult> ExportAsync(
        LearningFeatureDataset dataset,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var resolvedDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(resolvedDirectory);

        var policyPath = Path.Combine(resolvedDirectory, "policy-feedback-features.jsonl");
        var rankingPath = Path.Combine(resolvedDirectory, "ranking-pairs.jsonl");
        var routerPath = Path.Combine(resolvedDirectory, "router-intent-examples.jsonl");

        await WriteJsonLinesAsync(policyPath, dataset.FeatureExamples, cancellationToken).ConfigureAwait(false);
        await WriteJsonLinesAsync(rankingPath, dataset.RankingPairs, cancellationToken).ConfigureAwait(false);
        await WriteJsonLinesAsync(routerPath, dataset.RouterIntentExamples, cancellationToken).ConfigureAwait(false);

        return new LearningFeatureExportResult
        {
            ExportedAt = DateTimeOffset.UtcNow,
            OutputDirectory = resolvedDirectory,
            PolicyFeedbackFeaturesPath = policyPath,
            RankingPairsPath = rankingPath,
            RouterIntentExamplesPath = routerPath,
            FeatureCount = dataset.FeatureCount,
            RankingPairCount = dataset.RankingPairCount,
            RouterIntentExampleCount = dataset.RouterIntentExampleCount,
            PolicyVersion = PolicyVersion
        };
    }

    public async Task<LearningFeatureExportResult> ExportAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        string outputDirectory = "learning/features",
        IReadOnlyList<string>? evalReportPaths = null,
        IReadOnlyList<string>? planningShadowReportPaths = null,
        CancellationToken cancellationToken = default)
    {
        var dataset = await BuildAsync(
            workspaceId,
            collectionId,
            sessionId,
            int.MaxValue,
            0,
            evalReportPaths,
            planningShadowReportPaths,
            outputDirectory,
            cancellationToken).ConfigureAwait(false);
        return await ExportAsync(dataset, outputDirectory, cancellationToken).ConfigureAwait(false);
    }

    private ContextPolicyFeatureExample BuildPolicyFeedbackFeatureExample(PolicyFeedbackRecord record)
    {
        var accepted = IsPositive(record.Label) || IsAcceptAction(record.Action);
        var rejected = IsNegative(record.Label) || IsRejectAction(record.Action);
        var metadata = new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["sourcePolicyFeedbackRecordId"] = record.FeedbackRecordId,
            ["policyFeedbackSourceType"] = record.SourceType
        };

        var candidateId = FirstMetadataValue(
            metadata,
            "candidateId",
            "stableReviewCandidateId",
            "gapId",
            "constraintId",
            "sourcePromotionCandidateId",
            "sourceConstraintGapId") ?? record.SourceId;
        var candidateKind = FirstMetadataValue(metadata, "candidateKind", "suggestedStableTarget") ?? record.SourceType;
        var candidateStatus = FirstMetadataValue(metadata, "candidateStatus", "validationStatus") ?? record.Label;

        return new ContextPolicyFeatureExample
        {
            ExampleId = BuildId("feature", $"{record.FeedbackRecordId}\u001f{record.SourceType}\u001f{record.Label}"),
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            SourceType = record.SourceType,
            SourceId = record.SourceId,
            TaskKind = "PolicyFeedback",
            Mode = GetMetadataValue(metadata, "mode") ?? string.Empty,
            Intent = GetMetadataValue(metadata, "intent") ?? string.Empty,
            Label = record.Label,
            InputSummary = record.Reason,
            CandidateId = candidateId,
            CandidateKind = candidateKind,
            CandidateLayer = record.TargetLayer,
            CandidateStatus = candidateStatus,
            CandidateImportance = ParseDouble(metadata, "candidateImportance", "importance"),
            CandidateRecency = CalculateRecency(record.CreatedAt),
            ChannelSources = [record.SourceType],
            RelationPathCount = ParseInt(metadata, "relationPathCount"),
            KeywordMatchScore = ParseDouble(metadata, "keywordMatchScore"),
            SemanticAnchorMatchScore = ParseDouble(metadata, "semanticAnchorMatchScore"),
            ShortTermMatchScore = ParseDouble(metadata, "shortTermMatchScore"),
            StableMatchScore = ParseDouble(metadata, "stableMatchScore"),
            ConstraintMatchScore = ParseDouble(metadata, "constraintMatchScore"),
            LifecycleRisk = rejected ? 1 : 0,
            Selected = true,
            Accepted = accepted,
            Rejected = rejected,
            EvidenceRefs = record.EvidenceRefs.ToArray(),
            PolicyVersion = PolicyVersion,
            CreatedAt = record.CreatedAt == default ? DateTimeOffset.UtcNow : record.CreatedAt,
            Metadata = metadata
        };
    }

    private ContextPolicyFeatureExample BuildRouterIntentExample(
        ShadowRetrievalComparisonReport report,
        ShadowRetrievalComparisonItem sample)
    {
        var intent = ResolveIntent(sample);
        var channelSources = sample.ShadowChannelSources.Keys
            .Concat(sample.LegacyChannelSources.Keys)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["reportId"] = report.ReportId,
            ["sampleSet"] = report.SampleSet,
            ["proposalSummary"] = sample.ProposalSummary,
            ["legacyOperationId"] = sample.LegacyOperationId,
            ["shadowOperationId"] = sample.ShadowOperationId,
            ["validPlan"] = sample.ValidPlan.ToString(CultureInfo.InvariantCulture),
            ["nativeValidPlan"] = sample.NativeValidPlan.ToString(CultureInfo.InvariantCulture),
            ["repairedPlan"] = sample.RepairedPlan.ToString(CultureInfo.InvariantCulture),
            ["fallbackToLegacySafePlan"] = sample.FallbackToLegacySafePlan.ToString(CultureInfo.InvariantCulture),
            ["mustHitDelta"] = sample.MustHitDelta.ToString(CultureInfo.InvariantCulture),
            ["constraintDelta"] = sample.ConstraintDelta.ToString(CultureInfo.InvariantCulture),
            ["entityDelta"] = sample.EntityDelta.ToString(CultureInfo.InvariantCulture),
            ["uncertaintyDelta"] = sample.UncertaintyDelta.ToString(CultureInfo.InvariantCulture)
        };

        return new ContextPolicyFeatureExample
        {
            ExampleId = BuildId("router", $"{report.ReportId}\u001f{sample.SampleId}\u001f{intent}"),
            WorkspaceId = string.Empty,
            CollectionId = string.Empty,
            SourceType = "PlanningShadowComparison",
            SourceId = sample.SampleId,
            TaskKind = "RouterIntent",
            Mode = sample.Mode,
            Intent = intent,
            Label = intent,
            InputSummary = sample.ProposalSummary,
            CandidateId = sample.ProposalId,
            CandidateKind = "RetrievalPlanProposal",
            CandidateLayer = "Planning",
            CandidateStatus = sample.FallbackToLegacySafePlan ? "Fallback" : sample.RepairedPlan ? "Repaired" : "NativeValid",
            CandidateImportance = Math.Max(sample.ShadowRecall10, sample.LegacyRecall10),
            CandidateRecency = CalculateRecency(report.GeneratedAt),
            ChannelSources = channelSources,
            RelationPathCount = CountChannelSources(sample.ShadowChannelSources, "relation"),
            KeywordMatchScore = CountChannelSources(sample.ShadowChannelSources, "keyword"),
            SemanticAnchorMatchScore = sample.ShadowMrr,
            ShortTermMatchScore = CountChannelSources(sample.ShadowChannelSources, "short"),
            StableMatchScore = CountChannelSources(sample.ShadowChannelSources, "stable"),
            ConstraintMatchScore = sample.ShadowConstraintHitRate,
            LifecycleRisk = sample.LifecycleViolation ? 1 : 0,
            Selected = sample.ValidPlan && !sample.FallbackToLegacySafePlan,
            Accepted = sample.ValidPlan && !sample.FallbackToLegacySafePlan,
            Rejected = sample.FallbackToLegacySafePlan || sample.LifecycleViolation || sample.MustNotHitViolation,
            EvidenceRefs = sample.ShadowSelectedMustHit.Concat(sample.MustHitGained).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            PolicyVersion = PolicyVersion,
            CreatedAt = report.GeneratedAt == default ? DateTimeOffset.UtcNow : report.GeneratedAt,
            Metadata = metadata
        };
    }

    private Dictionary<string, string> BuildRankingFeatureSnapshot(
        ContextEvalResult result,
        string positive,
        string negative,
        ContextEvalItemDiagnostic? positiveDiagnostic,
        ContextEvalItemDiagnostic? negativeDiagnostic)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["status"] = result.Status,
            ["recall3"] = Format(result.RetrievalRecall3),
            ["recall5"] = Format(result.RetrievalRecall5),
            ["recall10"] = Format(result.RetrievalRecall10),
            ["mrr"] = Format(result.RetrievalMrrAnyMustHit),
            ["selectedCount"] = result.SelectedCount.ToString(CultureInfo.InvariantCulture),
            ["tokenBudget"] = result.TokenBudget.ToString(CultureInfo.InvariantCulture),
            ["positiveSelected"] = ContainsId(result.SelectedIds, positive).ToString(CultureInfo.InvariantCulture),
            ["negativeSelected"] = ContainsId(result.SelectedIds, negative).ToString(CultureInfo.InvariantCulture),
            ["positiveRank"] = (positiveDiagnostic?.Rank ?? FindRank(result.SelectedIds, positive)).ToString(CultureInfo.InvariantCulture),
            ["negativeRank"] = (negativeDiagnostic?.Rank ?? FindRank(result.SelectedIds, negative)).ToString(CultureInfo.InvariantCulture),
            ["positiveScore"] = Format(positiveDiagnostic?.Score ?? 0),
            ["negativeScore"] = Format(negativeDiagnostic?.Score ?? 0),
            ["positiveKind"] = positiveDiagnostic?.Kind ?? string.Empty,
            ["negativeKind"] = negativeDiagnostic?.Kind ?? string.Empty,
            ["positiveSection"] = positiveDiagnostic?.SectionName ?? string.Empty,
            ["negativeSection"] = negativeDiagnostic?.SectionName ?? string.Empty,
            ["packageHasAllConstraints"] = result.PackageHasAllConstraints.ToString(CultureInfo.InvariantCulture),
            ["packageHasAllEntities"] = result.PackageHasAllEntities.ToString(CultureInfo.InvariantCulture),
            ["packageHasAllUncertainties"] = result.PackageHasAllUncertainties.ToString(CultureInfo.InvariantCulture)
        };
    }

    private async Task<IReadOnlyList<ContextEvalReport>> LoadEvalReportsAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var reports = new List<ContextEvalReport>();
        foreach (var path in paths.Select(ResolvePath).Where(File.Exists))
        {
            await using var stream = File.OpenRead(path);
            var report = await JsonSerializer.DeserializeAsync<ContextEvalReport>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (report is not null)
            {
                reports.Add(report);
            }
        }

        return reports;
    }

    private async Task<IReadOnlyList<ShadowRetrievalComparisonReport>> LoadPlanningShadowReportsAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var reports = new List<ShadowRetrievalComparisonReport>();
        foreach (var path in paths.Select(ResolvePath).Where(File.Exists))
        {
            await using var stream = File.OpenRead(path);
            var report = await JsonSerializer.DeserializeAsync<ShadowRetrievalComparisonReport>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (report is not null)
            {
                reports.Add(report);
            }
        }

        return reports;
    }

    private static async Task WriteJsonLinesAsync<T>(
        string path,
        IReadOnlyList<T> records,
        CancellationToken cancellationToken)
    {
        var lines = records.Select(record => JsonSerializer.Serialize(record, JsonOptions));
        await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, lines), cancellationToken)
            .ConfigureAwait(false);
    }

    private string DetectIntent(string query, string mode)
    {
        var detection = _intentDetector.Detect(
            new ContextPlanningSnapshot(),
            query,
            mode);
        return detection.Intent;
    }

    private static IEnumerable<string> ResolveMustHitCandidates(ContextEvalResult result)
    {
        return result.MustHit
            .Concat(result.SelectedItemDiagnostics.Where(item => item.IsMustHit).Select(item => item.ItemId))
            .Concat(result.DroppedItemDiagnostics.Where(item => item.IsMustHit).Select(item => item.ItemId))
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ResolveMustNotHitCandidates(ContextEvalResult result)
    {
        return result.MustNotHit
            .Concat(result.SelectedItemDiagnostics.Where(item => item.IsMustNotHit).Select(item => item.ItemId))
            .Concat(result.DroppedItemDiagnostics.Where(item => item.IsMustNotHit).Select(item => item.ItemId))
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static ContextEvalItemDiagnostic? FindDiagnostic(ContextEvalResult result, string id)
    {
        return result.SelectedItemDiagnostics
            .Concat(result.DroppedItemDiagnostics)
            .FirstOrDefault(item => MatchesId(item.ItemId, id)
                || item.SourceRefs.Any(source => MatchesId(source, id)));
    }

    private static string ResolveIntent(ShadowRetrievalComparisonItem sample)
    {
        if (!string.IsNullOrWhiteSpace(sample.ProposalSummary))
        {
            var separator = sample.ProposalSummary.IndexOf('/');
            return separator > 0
                ? sample.ProposalSummary[..separator]
                : sample.ProposalSummary;
        }

        return sample.Diagnostics.TryGetValue("intent", out var intent) && !string.IsNullOrWhiteSpace(intent)
            ? intent
            : PlanningIntentDetector.FuzzyQuestion;
    }

    private static int CountChannelSources(
        IReadOnlyDictionary<string, IReadOnlyList<string>> channelSources,
        string channel)
    {
        return channelSources
            .Where(pair => pair.Key.Contains(channel, StringComparison.OrdinalIgnoreCase))
            .Sum(pair => pair.Value.Count);
    }

    private static string ResolvePath(string path)
    {
        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(Directory.GetCurrentDirectory(), path);
    }

    private static string ResolveLatestExportPath(string outputDirectory)
    {
        var resolved = ResolvePath(outputDirectory);
        var files = new[]
            {
                Path.Combine(resolved, "policy-feedback-features.jsonl"),
                Path.Combine(resolved, "ranking-pairs.jsonl"),
                Path.Combine(resolved, "router-intent-examples.jsonl")
            }
            .Where(File.Exists)
            .ToArray();
        return files.Length == 0 ? string.Empty : resolved;
    }

    private static PolicyFeedbackDataset CreateEmptyPolicyFeedbackDataset(
        string workspaceId,
        string? collectionId,
        string? sessionId)
    {
        return new PolicyFeedbackDataset
        {
            DatasetId = BuildId("policy-feedback-empty", $"{workspaceId}\u001f{collectionId}\u001f{sessionId}"),
            Name = "Policy Feedback Dataset",
            Scope = $"workspace:{workspaceId}",
            CreatedAt = DateTimeOffset.UtcNow,
            PolicyVersion = PolicyFeedbackDatasetService.PolicyVersion,
            EvalBaselineRef = PolicyFeedbackDatasetService.EvalBaselineRef
        };
    }

    private static bool IsPositive(string label)
        => string.Equals(label, PolicyFeedbackLabels.Positive, StringComparison.OrdinalIgnoreCase);

    private static bool IsNegative(string label)
        => string.Equals(label, PolicyFeedbackLabels.Negative, StringComparison.OrdinalIgnoreCase);

    private static bool IsAcceptAction(string action)
        => string.Equals(action, "accept", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "accepted", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "activate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "activated", StringComparison.OrdinalIgnoreCase);

    private static bool IsRejectAction(string action)
        => string.Equals(action, "reject", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "rejected", StringComparison.OrdinalIgnoreCase);

    private static string? FirstMetadataValue(
        IReadOnlyDictionary<string, string> metadata,
        params string[] keys)
    {
        return keys.Select(key => GetMetadataValue(metadata, key)).FirstOrDefault(value => value is not null);
    }

    private static string? GetMetadataValue(IReadOnlyDictionary<string, string> metadata, string key)
    {
        return metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static double ParseDouble(IReadOnlyDictionary<string, string> metadata, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value)
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }

    private static int ParseInt(IReadOnlyDictionary<string, string> metadata, string key)
    {
        return metadata.TryGetValue(key, out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
    }

    private static double CalculateRecency(DateTimeOffset createdAt)
    {
        if (createdAt == default)
        {
            return 0;
        }

        var days = Math.Max(0, (DateTimeOffset.UtcNow - createdAt.ToUniversalTime()).TotalDays);
        return 1 / (1 + days);
    }

    private static bool ContainsId(IEnumerable<string> ids, string id)
        => ids.Any(item => MatchesId(item, id));

    private static bool MatchesId(string value, string expected)
        => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static int FindRank(IReadOnlyList<string> ids, string id)
    {
        for (var i = 0; i < ids.Count; i++)
        {
            if (MatchesId(ids[i], id))
            {
                return i + 1;
            }
        }

        return 0;
    }

    private static string Format(double value)
        => value.ToString("0.########", CultureInfo.InvariantCulture);

    private static string BuildId(string prefix, string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"{prefix}-{Convert.ToHexString(bytes)[..16].ToLowerInvariant()}";
    }
}
