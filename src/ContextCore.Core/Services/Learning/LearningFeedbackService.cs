using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>运行时学习反馈采集服务；只负责收集和离线导出，不改变任何正式策略。</summary>
public sealed class LearningFeedbackService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        LearningFeedbackKinds.Useful,
        LearningFeedbackKinds.NotUseful,
        LearningFeedbackKinds.WrongIntent,
        LearningFeedbackKinds.WrongCandidate,
        LearningFeedbackKinds.MissingContext,
        LearningFeedbackKinds.DeprecatedContext,
        LearningFeedbackKinds.ConstraintMissing,
        LearningFeedbackKinds.ConstraintIncorrect,
        LearningFeedbackKinds.RankingWrong,
        LearningFeedbackKinds.PromotionWrong,
        LearningFeedbackKinds.ShouldPromote,
        LearningFeedbackKinds.ShouldReject,
        LearningFeedbackKinds.NeedsMoreEvidence
    };

    private static readonly HashSet<string> AllowedCapabilities = new(StringComparer.OrdinalIgnoreCase)
    {
        ShadowCapabilityIds.GraphExpansion,
        ShadowCapabilityIds.VectorRetrieval,
        ShadowCapabilityIds.RouterIntentClassifier,
        ShadowCapabilityIds.CandidateReranker,
        ShadowCapabilityIds.AttentionRerank,
        ShadowCapabilityIds.PlanningProposal,
        ShadowCapabilityIds.PromotionJudge,
        ShadowCapabilityIds.ConstraintGapJudge
    };

    private readonly ILearningFeedbackStore _store;

    public LearningFeedbackService(ILearningFeedbackStore store)
    {
        _store = store;
    }

    public async Task<LearningFeedbackSubmitResult> SubmitAsync(
        LearningFeedbackEvent feedbackEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedbackEvent);
        var warnings = new List<string>();
        var normalized = Normalize(feedbackEvent, warnings);
        var existing = await _store.GetAsync(normalized.FeedbackId, cancellationToken)
            .ConfigureAwait(false);

        await _store.UpsertAsync(normalized, cancellationToken)
            .ConfigureAwait(false);

        return new LearningFeedbackSubmitResult
        {
            FeedbackId = normalized.FeedbackId,
            Created = existing is null,
            DuplicateReplaced = existing is not null,
            Event = normalized,
            Warnings = warnings
        };
    }

    public Task<LearningFeedbackSubmitResult> SubmitAsync(
        LearningFeedbackSubmitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SubmitAsync(ToEvent(request), cancellationToken);
    }

    public Task<IReadOnlyList<LearningFeedbackEvent>> ListAsync(
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return _store.QueryAsync(NormalizeQuery(query), cancellationToken);
    }

    public async Task<LearningFeedbackSummaryReport> BuildSummaryAsync(
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = NormalizeQuery(query);
        var rows = await _store.QueryAsync(
                new LearningFeedbackEventQuery
                {
                    WorkspaceId = normalizedQuery.WorkspaceId,
                    CollectionId = normalizedQuery.CollectionId,
                    Source = normalizedQuery.Source,
                    SourceOperationId = normalizedQuery.SourceOperationId,
                    CapabilityId = normalizedQuery.CapabilityId,
                    TargetId = normalizedQuery.TargetId,
                    TargetType = normalizedQuery.TargetType,
                    FeedbackKind = normalizedQuery.FeedbackKind,
                    Limit = int.MaxValue,
                    Offset = 0
                },
                cancellationToken)
            .ConfigureAwait(false);

        var recentLimit = normalizedQuery.Limit > 0 ? Math.Min(normalizedQuery.Limit, 20) : 20;
        return new LearningFeedbackSummaryReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            WorkspaceId = normalizedQuery.WorkspaceId,
            CollectionId = normalizedQuery.CollectionId,
            FeedbackCount = rows.Count,
            FeedbackByCapability = CountBy(rows, static item => item.CapabilityId),
            FeedbackByKind = CountBy(rows, static item => item.FeedbackKind),
            FeedbackByTargetType = CountBy(rows, static item => item.TargetType),
            MetadataOnlyCount = rows.Count(static item => item.MetadataOnly),
            TrainingUseDisabledCount = rows.Count(static item =>
                string.Equals(item.TrainingUse, "disabled_until_review", StringComparison.OrdinalIgnoreCase)),
            RecentFeedback = [.. rows
                .OrderByDescending(static item => item.CreatedAt)
                .Take(recentLimit)],
            ExportPath = "learning/feedback/learning-feedback-events.jsonl",
            Warnings = rows.Count == 0
                ? ["未找到运行时学习反馈事件。"]
                : Array.Empty<string>()
        };
    }

    public async Task<string> ExportJsonLinesAsync(
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken = default)
    {
        var exportQuery = new LearningFeedbackEventQuery
        {
            WorkspaceId = query.WorkspaceId,
            CollectionId = query.CollectionId,
            Source = query.Source,
            SourceOperationId = query.SourceOperationId,
            CapabilityId = query.CapabilityId,
            TargetId = query.TargetId,
            TargetType = query.TargetType,
            FeedbackKind = query.FeedbackKind,
            Limit = query.Limit > 0 ? query.Limit : int.MaxValue,
            Offset = query.Offset
        };
        var rows = await ListAsync(exportQuery, cancellationToken)
            .ConfigureAwait(false);
        return string.Join(Environment.NewLine, rows.Select(static item => JsonSerializer.Serialize(item, JsonOptions)));
    }

    public static string BuildMarkdownReport(LearningFeedbackSummaryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("# Runtime Learning Feedback Summary");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Generated: `{report.GeneratedAt:O}`");
        builder.AppendLine($"- WorkspaceId: `{report.WorkspaceId}`");
        builder.AppendLine($"- CollectionId: `{report.CollectionId}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- FeedbackCount: `{report.FeedbackCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- MetadataOnlyCount: `{report.MetadataOnlyCount}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- TrainingUseDisabledCount: `{report.TrainingUseDisabledCount}`");
        builder.AppendLine($"- ExportPath: `{report.ExportPath}`");
        builder.AppendLine();
        AppendCounts(builder, "Feedback By Capability", report.FeedbackByCapability);
        AppendCounts(builder, "Feedback By Kind", report.FeedbackByKind);
        AppendCounts(builder, "Feedback By Target Type", report.FeedbackByTargetType);

        builder.AppendLine("## Recent Feedback");
        builder.AppendLine();
        builder.AppendLine("| FeedbackId | Capability | Kind | Target | Source | CreatedAt |");
        builder.AppendLine("|---|---|---|---|---|---|");
        foreach (var item in report.RecentFeedback)
        {
            builder.AppendLine(
                $"| {Escape(item.FeedbackId)} | {Escape(item.CapabilityId)} | {Escape(item.FeedbackKind)} | {Escape(item.TargetType)}:{Escape(item.TargetId)} | {Escape(item.Source)} | {item.CreatedAt:O} |");
        }

        if (report.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Warnings");
            builder.AppendLine();
            foreach (var warning in report.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    private static LearningFeedbackEvent Normalize(
        LearningFeedbackEvent source,
        List<string> warnings)
    {
        var workspaceId = Require(source.WorkspaceId, nameof(source.WorkspaceId));
        var collectionId = Require(source.CollectionId, nameof(source.CollectionId));
        var capabilityId = Require(source.CapabilityId, nameof(source.CapabilityId));
        var feedbackKind = Require(source.FeedbackKind, nameof(source.FeedbackKind));
        var targetType = Require(source.TargetType, nameof(source.TargetType));

        if (!AllowedCapabilities.Contains(capabilityId))
        {
            throw new ArgumentException($"Invalid capabilityId '{capabilityId}'.", nameof(source));
        }

        if (!AllowedKinds.Contains(feedbackKind))
        {
            throw new ArgumentException($"Invalid feedbackKind '{feedbackKind}'.", nameof(source));
        }

        if (!Enum.TryParse<LearningFeedbackTargetType>(targetType, ignoreCase: true, out var parsedTargetType))
        {
            throw new ArgumentException($"Invalid targetType '{targetType}'.", nameof(source));
        }

        var metadata = new Dictionary<string, string>(source.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["storedFor"] = "runtime_feedback_collection",
            ["trainingUse"] = "disabled_until_review"
        };
        var requestedRedactionMode = string.IsNullOrWhiteSpace(source.RedactionMode)
            ? GetMetadata(metadata, "redactionMode") ?? string.Empty
            : source.RedactionMode.Trim();
        var metadataOnly = source.MetadataOnly
            || IsEnabled(metadata, "metadataOnly")
            || string.Equals(requestedRedactionMode, "metadata-only", StringComparison.OrdinalIgnoreCase);
        metadata["metadataOnly"] = metadataOnly ? "true" : "false";
        metadata["redactionMode"] = metadataOnly ? "metadata-only" : requestedRedactionMode;
        var reason = NormalizeSensitiveText(source.Reason, metadataOnly, "reason", metadata, warnings);
        var userCorrection = NormalizeSensitiveText(source.UserCorrection, metadataOnly, "userCorrection", metadata, warnings);
        var createdAt = source.CreatedAt == default ? DateTimeOffset.UtcNow : source.CreatedAt;
        var feedbackId = string.IsNullOrWhiteSpace(source.FeedbackId)
            ? BuildDeterministicFeedbackId(source, workspaceId, collectionId, capabilityId, feedbackKind)
            : source.FeedbackId.Trim();

        return new LearningFeedbackEvent
        {
            FeedbackId = feedbackId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Source = NormalizeOptional(source.Source),
            SourceOperationId = NormalizeOptional(source.SourceOperationId),
            CapabilityId = capabilityId,
            TargetId = NormalizeOptional(source.TargetId),
            TargetType = parsedTargetType.ToString(),
            FeedbackKind = feedbackKind,
            FeedbackValue = Clamp(source.FeedbackValue, -1.0, 1.0),
            Reason = reason,
            UserCorrection = userCorrection,
            RedactionMode = metadata["redactionMode"],
            MetadataOnly = metadataOnly,
            TrainingUse = "disabled_until_review",
            Confidence = Clamp(source.Confidence, 0.0, 1.0),
            CreatedAt = createdAt,
            Metadata = metadata
        };
    }

    private static LearningFeedbackEvent ToEvent(LearningFeedbackSubmitRequest request)
    {
        var metadata = new Dictionary<string, string>(request.Metadata, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(request.RedactionMode))
        {
            metadata["redactionMode"] = request.RedactionMode.Trim();
        }

        metadata["metadataOnly"] = request.MetadataOnly ? "true" : "false";
        metadata["trainingUse"] = string.IsNullOrWhiteSpace(request.TrainingUse)
            ? "disabled_until_review"
            : request.TrainingUse.Trim();

        return new LearningFeedbackEvent
        {
            FeedbackId = request.FeedbackId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            Source = request.Source,
            SourceOperationId = request.SourceOperationId,
            CapabilityId = request.CapabilityId,
            TargetId = request.TargetId,
            TargetType = request.TargetType.ToString(),
            FeedbackKind = request.FeedbackKind,
            FeedbackValue = request.FeedbackValue,
            Reason = request.Reason,
            UserCorrection = request.UserCorrection,
            RedactionMode = request.RedactionMode,
            MetadataOnly = request.MetadataOnly,
            TrainingUse = request.TrainingUse,
            Confidence = request.Confidence,
            CreatedAt = request.CreatedAt,
            Metadata = metadata
        };
    }

    private static LearningFeedbackEventQuery NormalizeQuery(LearningFeedbackEventQuery query)
    {
        return new LearningFeedbackEventQuery
        {
            WorkspaceId = NormalizeOptional(query.WorkspaceId),
            CollectionId = NormalizeOptional(query.CollectionId),
            Source = NormalizeOptional(query.Source),
            SourceOperationId = NormalizeOptional(query.SourceOperationId),
            CapabilityId = NormalizeOptional(query.CapabilityId),
            TargetId = NormalizeOptional(query.TargetId),
            TargetType = NormalizeOptional(query.TargetType),
            FeedbackKind = NormalizeOptional(query.FeedbackKind),
            Limit = query.Limit > 0 ? query.Limit : 100,
            Offset = Math.Max(0, query.Offset)
        };
    }

    private static string NormalizeSensitiveText(
        string value,
        bool metadataOnly,
        string fieldName,
        Dictionary<string, string> metadata,
        List<string> warnings)
    {
        if (metadataOnly)
        {
            metadata[$"{fieldName}Redacted"] = "true";
            metadata["redactionMode"] = "metadata-only";
            return string.Empty;
        }

        var normalized = value?.Trim() ?? string.Empty;
        const int maxLength = 512;
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        metadata[$"{fieldName}Truncated"] = "true";
        warnings.Add($"{fieldName} was truncated to {maxLength} characters.");
        return normalized[..maxLength];
    }

    private static string BuildDeterministicFeedbackId(
        LearningFeedbackEvent source,
        string workspaceId,
        string collectionId,
        string capabilityId,
        string feedbackKind)
    {
        var input = string.Join(
            "|",
            workspaceId,
            collectionId,
            NormalizeOptional(source.Source),
            NormalizeOptional(source.SourceOperationId),
            capabilityId,
            NormalizeOptional(source.TargetId),
            NormalizeOptional(source.TargetType),
            feedbackKind,
            source.FeedbackValue.ToString("R", CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return "lfb_" + Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    private static Dictionary<string, int> CountBy(
        IEnumerable<LearningFeedbackEvent> rows,
        Func<LearningFeedbackEvent, string> selector)
    {
        return rows
            .Select(selector)
            .Select(static value => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim())
            .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);
    }

    private static void AppendCounts(
        StringBuilder builder,
        string title,
        IReadOnlyDictionary<string, int> counts)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine("| Key | Count |");
        builder.AppendLine("|---|---:|");
        foreach (var pair in counts.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"| {Escape(pair.Key)} | {pair.Value} |");
        }

        builder.AppendLine();
    }

    private static string Require(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{fieldName} is required.", fieldName);
        }

        return value.Trim();
    }

    private static string NormalizeOptional(string? value) => value?.Trim() ?? string.Empty;

    private static string? GetMetadata(IReadOnlyDictionary<string, string> metadata, string key)
    {
        return metadata.TryGetValue(key, out var value) ? value : null;
    }

    private static bool IsEnabled(IReadOnlyDictionary<string, string> metadata, string key)
    {
        return metadata.TryGetValue(key, out var value)
            && (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase));
    }

    private static double Clamp(double value, double min, double max)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        return Math.Min(max, Math.Max(min, value));
    }

    private static string Escape(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "-"
            : value.Replace("|", "\\|", StringComparison.Ordinal);
    }
}
