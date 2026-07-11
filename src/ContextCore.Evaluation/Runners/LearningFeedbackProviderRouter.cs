using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.ControlRoom.Services;

/// <summary>Learning feedback provider 路由器；默认 FileSystemPrimary，Postgres primary 只允许显式 scoped smoke。</summary>
public sealed class LearningFeedbackProviderRouter
{
    private readonly ILearningFeedbackStore _fileFeedbackStore;
    private readonly ILearningFeedbackReviewStore _fileReviewStore;
    private readonly ILearningFeatureCandidateStore _fileCandidateStore;
    private readonly ILearningFeedbackStore _postgresFeedbackStore;
    private readonly ILearningFeedbackReviewStore _postgresReviewStore;
    private readonly ILearningFeatureCandidateStore _postgresCandidateStore;
    private readonly LearningFeedbackProviderSwitchOptions _options;
    private readonly bool _providerQualityReady;
    private readonly Func<LearningFeedbackProviderSwitchTrace, CancellationToken, Task> _traceSink;

    public LearningFeedbackProviderRouter(
        ILearningFeedbackStore fileFeedbackStore,
        ILearningFeedbackReviewStore fileReviewStore,
        ILearningFeatureCandidateStore fileCandidateStore,
        ILearningFeedbackStore postgresFeedbackStore,
        ILearningFeedbackReviewStore postgresReviewStore,
        ILearningFeatureCandidateStore postgresCandidateStore,
        LearningFeedbackProviderSwitchOptions options,
        bool providerQualityReady,
        Func<LearningFeedbackProviderSwitchTrace, CancellationToken, Task> traceSink)
    {
        _fileFeedbackStore = fileFeedbackStore;
        _fileReviewStore = fileReviewStore;
        _fileCandidateStore = fileCandidateStore;
        _postgresFeedbackStore = postgresFeedbackStore;
        _postgresReviewStore = postgresReviewStore;
        _postgresCandidateStore = postgresCandidateStore;
        _options = options;
        _providerQualityReady = providerQualityReady;
        _traceSink = traceSink;
    }

    public Task UpsertFeedbackAsync(string operationId, LearningFeedbackEvent feedback, CancellationToken cancellationToken = default)
        => ExecuteWriteAsync(
            operationId,
            feedback.WorkspaceId,
            feedback.CollectionId,
            "FeedbackUpsert",
            feedback.TargetType,
            feedback.FeedbackId,
            token => _fileFeedbackStore.UpsertAsync(feedback, token),
            token => _postgresFeedbackStore.UpsertAsync(feedback, token),
            cancellationToken);

    public Task UpsertReviewAsync(string operationId, LearningFeedbackReviewRecord review, CancellationToken cancellationToken = default)
        => ExecuteWriteAsync(
            operationId,
            ResolveMetadata(review.Metadata, "workspaceId"),
            ResolveMetadata(review.Metadata, "collectionId"),
            "ReviewUpsert",
            "LearningFeedbackReview",
            review.FeedbackId,
            token => _fileReviewStore.UpsertAsync(review, token),
            token => _postgresReviewStore.UpsertAsync(review, token),
            cancellationToken);

    public Task UpsertFeatureCandidateAsync(string operationId, FeedbackFeatureCandidate candidate, CancellationToken cancellationToken = default)
        => ExecuteWriteAsync(
            operationId,
            ResolveMetadata(candidate.Metadata, "workspaceId"),
            ResolveMetadata(candidate.Metadata, "collectionId"),
            "FeatureCandidateUpsert",
            candidate.TargetType,
            candidate.CandidateId,
            token => _fileCandidateStore.UpsertAsync(candidate, token),
            token => _postgresCandidateStore.UpsertAsync(candidate, token),
            cancellationToken);

    public Task<IReadOnlyList<LearningFeedbackEvent>> QueryFeedbackAsync(
        string operationId,
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken = default)
    {
        var workspaceId = query.WorkspaceId ?? string.Empty;
        var collectionId = query.CollectionId ?? string.Empty;
        return ExecuteReadAsync(
            operationId,
            workspaceId,
            collectionId,
            "FeedbackQuery",
            "LearningFeedback",
            query.TargetId ?? query.CapabilityId ?? workspaceId,
            token => _fileFeedbackStore.QueryAsync(query, token),
            token => _postgresFeedbackStore.QueryAsync(query, token),
            cancellationToken);
    }

    public Task<LearningFeedbackSummaryReport> BuildSummaryAsync(
        string operationId,
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken = default)
    {
        var workspaceId = query.WorkspaceId ?? string.Empty;
        var collectionId = query.CollectionId ?? string.Empty;
        return ExecuteReadAsync(
            operationId,
            workspaceId,
            collectionId,
            "FeedbackSummary",
            "LearningFeedbackSummary",
            workspaceId,
            token => BuildSummaryCoreAsync(_fileFeedbackStore, query, token),
            token => BuildSummaryCoreAsync(_postgresFeedbackStore, query, token),
            cancellationToken);
    }

    public Task<string> ExportFeedbackJsonLinesAsync(
        string operationId,
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken = default)
    {
        var workspaceId = query.WorkspaceId ?? string.Empty;
        var collectionId = query.CollectionId ?? string.Empty;
        return ExecuteReadAsync(
            operationId,
            workspaceId,
            collectionId,
            "FeedbackExportProjection",
            "LearningFeedbackExport",
            workspaceId,
            token => ExportFeedbackCoreAsync(_fileFeedbackStore, query, token),
            token => ExportFeedbackCoreAsync(_postgresFeedbackStore, query, token),
            cancellationToken);
    }

    public Task<IReadOnlyList<LearningFeedbackReviewRecord>> QueryReviewsAsync(
        string operationId,
        string workspaceId,
        string collectionId,
        LearningFeedbackReviewQuery query,
        CancellationToken cancellationToken = default)
        => ExecuteReadAsync(
            operationId,
            workspaceId,
            collectionId,
            "ReviewQuery",
            "LearningFeedbackReview",
            query.FeedbackId ?? workspaceId,
            token => _fileReviewStore.QueryAsync(query, token),
            token => _postgresReviewStore.QueryAsync(query, token),
            cancellationToken);

    public Task<LearningFeedbackReviewRecord?> GetLatestReviewAsync(
        string operationId,
        string workspaceId,
        string collectionId,
        string feedbackId,
        CancellationToken cancellationToken = default)
        => ExecuteReadAsync(
            operationId,
            workspaceId,
            collectionId,
            "ReviewLatest",
            "LearningFeedbackReview",
            feedbackId,
            token => GetLatestReviewCoreAsync(_fileReviewStore, feedbackId, token),
            token => GetLatestReviewCoreAsync(_postgresReviewStore, feedbackId, token),
            cancellationToken);

    public Task<LearningFeedbackReviewSummaryReport> BuildReviewSummaryAsync(
        string operationId,
        string workspaceId,
        string collectionId,
        LearningFeedbackReviewQuery query,
        CancellationToken cancellationToken = default)
        => ExecuteReadAsync(
            operationId,
            workspaceId,
            collectionId,
            "ReviewSummary",
            "LearningFeedbackReviewSummary",
            workspaceId,
            token => BuildReviewSummaryCoreAsync(_fileReviewStore, query, token),
            token => BuildReviewSummaryCoreAsync(_postgresReviewStore, query, token),
            cancellationToken);

    public Task<IReadOnlyList<FeedbackFeatureCandidate>> QueryFeatureCandidatesAsync(
        string operationId,
        string workspaceId,
        string collectionId,
        LearningFeatureCandidateQuery query,
        CancellationToken cancellationToken = default)
        => ExecuteReadAsync(
            operationId,
            workspaceId,
            collectionId,
            "FeatureCandidateQuery",
            "LearningFeatureCandidate",
            query.CandidateId ?? workspaceId,
            token => QueryCandidatesInScopeAsync(_fileCandidateStore, query, workspaceId, collectionId, token),
            token => QueryCandidatesInScopeAsync(_postgresCandidateStore, query, workspaceId, collectionId, token),
            cancellationToken);

    public Task<string> ExportFeatureCandidatesJsonLinesAsync(
        string operationId,
        string workspaceId,
        string collectionId,
        LearningFeatureCandidateQuery query,
        CancellationToken cancellationToken = default)
        => ExecuteReadAsync(
            operationId,
            workspaceId,
            collectionId,
            "FeatureCandidateExportProjection",
            "LearningFeatureCandidateExport",
            workspaceId,
            token => ExportCandidatesCoreAsync(_fileCandidateStore, query, workspaceId, collectionId, token),
            token => ExportCandidatesCoreAsync(_postgresCandidateStore, query, workspaceId, collectionId, token),
            cancellationToken);

    private Task ExecuteWriteAsync(
        string operationId,
        string workspaceId,
        string collectionId,
        string operationKind,
        string targetType,
        string targetId,
        Func<CancellationToken, Task> fileWrite,
        Func<CancellationToken, Task> postgresWrite,
        CancellationToken cancellationToken)
        => ExecuteAsync<object?>(
            operationId,
            workspaceId,
            collectionId,
            operationKind,
            targetType,
            targetId,
            async token =>
            {
                await fileWrite(token).ConfigureAwait(false);
                return null;
            },
            async token =>
            {
                await postgresWrite(token).ConfigureAwait(false);
                return null;
            },
            isWrite: true,
            cancellationToken);

    private Task<T> ExecuteReadAsync<T>(
        string operationId,
        string workspaceId,
        string collectionId,
        string operationKind,
        string targetType,
        string targetId,
        Func<CancellationToken, Task<T>> fileRead,
        Func<CancellationToken, Task<T>> postgresRead,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            operationId,
            workspaceId,
            collectionId,
            operationKind,
            targetType,
            targetId,
            fileRead,
            postgresRead,
            isWrite: false,
            cancellationToken);

    private async Task<T> ExecuteAsync<T>(
        string operationId,
        string workspaceId,
        string collectionId,
        string operationKind,
        string targetType,
        string targetId,
        Func<CancellationToken, Task<T>> fileOperation,
        Func<CancellationToken, Task<T>> postgresOperation,
        bool isWrite,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var mode = ResolveMode(workspaceId, collectionId);
        var primaryProvider = mode == LearningFeedbackProviderMode.GuardedPostgresPrimary ? "Postgres" : "FileSystem";
        var fallbackUsed = false;
        var mismatchDetected = false;
        var postgresError = string.Empty;
        T result;

        if (mode == LearningFeedbackProviderMode.DualWriteOnly)
        {
            result = await fileOperation(cancellationToken).ConfigureAwait(false);
            if (isWrite)
            {
                try
                {
                    await postgresOperation(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (_options.FallbackToFileSystem)
                {
                    fallbackUsed = true;
                    postgresError = ex.GetType().Name;
                }
            }
        }
        else if (mode == LearningFeedbackProviderMode.ShadowRead)
        {
            result = await fileOperation(cancellationToken).ConfigureAwait(false);
            if (!isWrite && _options.ContinueComparisonTrace)
            {
                try
                {
                    var postgresResult = await postgresOperation(cancellationToken).ConfigureAwait(false);
                    mismatchDetected = !SameHash(result, postgresResult);
                    if (mismatchDetected && _options.FailClosedOnMismatch)
                    {
                        await EmitTraceAsync(operationId, workspaceId, collectionId, mode, operationKind, targetType, targetId, primaryProvider, fallbackUsed, mismatchDetected, postgresError, stopwatch, cancellationToken).ConfigureAwait(false);
                        throw new InvalidOperationException("Learning feedback provider switch mismatch detected.");
                    }
                }
                catch (Exception ex) when (_options.FallbackToFileSystem && ex is not InvalidOperationException)
                {
                    fallbackUsed = true;
                    postgresError = ex.GetType().Name;
                }
            }
        }
        else if (mode == LearningFeedbackProviderMode.GuardedPostgresPrimary)
        {
            try
            {
                result = await postgresOperation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (_options.FallbackToFileSystem)
            {
                fallbackUsed = true;
                postgresError = ex.GetType().Name;
                result = await fileOperation(cancellationToken).ConfigureAwait(false);
                await EmitTraceAsync(operationId, workspaceId, collectionId, mode, operationKind, targetType, targetId, primaryProvider, fallbackUsed, mismatchDetected, postgresError, stopwatch, cancellationToken).ConfigureAwait(false);
                return result;
            }

            if (_options.ContinueComparisonTrace)
            {
                var fileResult = await fileOperation(cancellationToken).ConfigureAwait(false);
                mismatchDetected = !SameHash(result, fileResult);
                if (mismatchDetected)
                {
                    if (_options.FailClosedOnMismatch)
                    {
                        await EmitTraceAsync(operationId, workspaceId, collectionId, mode, operationKind, targetType, targetId, primaryProvider, fallbackUsed, mismatchDetected, postgresError, stopwatch, cancellationToken).ConfigureAwait(false);
                        throw new InvalidOperationException("Learning feedback provider switch mismatch detected.");
                    }

                    fallbackUsed = _options.FallbackToFileSystem;
                    if (fallbackUsed)
                    {
                        result = fileResult;
                    }
                }
            }
        }
        else
        {
            result = await fileOperation(cancellationToken).ConfigureAwait(false);
        }

        await EmitTraceAsync(operationId, workspaceId, collectionId, mode, operationKind, targetType, targetId, primaryProvider, fallbackUsed, mismatchDetected, postgresError, stopwatch, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private LearningFeedbackProviderMode ResolveMode(string workspaceId, string collectionId)
    {
        if (!_options.Enabled)
        {
            return LearningFeedbackProviderMode.FileSystemPrimary;
        }

        var scopedRule = ResolveScopedRule(workspaceId, collectionId);
        if (scopedRule is not null)
        {
            EnsureScopedProviderAllowed(workspaceId, collectionId, scopedRule);
            return scopedRule.Mode;
        }

        if (_options.Mode == LearningFeedbackProviderMode.FileSystemPrimary)
        {
            return LearningFeedbackProviderMode.FileSystemPrimary;
        }

        EnsureScopedProviderAllowed(workspaceId, collectionId, scopedRule: null);
        return _options.Mode;
    }

    private LearningFeedbackScopedRule? ResolveScopedRule(string workspaceId, string collectionId)
    {
        foreach (var rule in _options.ScopedRules)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            if (string.Equals(rule.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(rule.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
            {
                return rule;
            }
        }

        return null;
    }

    private void EnsureScopedProviderAllowed(
        string workspaceId,
        string collectionId,
        LearningFeedbackScopedRule? scopedRule)
    {
        if (_options.RequireProviderQualityReady && !_providerQualityReady)
        {
            throw new InvalidOperationException("Learning feedback provider quality gate is not satisfied.");
        }

        if (scopedRule is not null)
        {
            return;
        }

        if (_options.WorkspaceAllowlist.Count == 0 || _options.CollectionAllowlist.Count == 0)
        {
            throw new InvalidOperationException("Learning feedback provider switch requires explicit workspace and collection allowlist.");
        }

        if (!_options.WorkspaceAllowlist.Contains(workspaceId, StringComparer.OrdinalIgnoreCase)
            || !_options.CollectionAllowlist.Contains(collectionId, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Learning feedback provider switch scope is not allowlisted.");
        }
    }

    private async Task EmitTraceAsync(
        string operationId,
        string workspaceId,
        string collectionId,
        LearningFeedbackProviderMode mode,
        string operationKind,
        string targetType,
        string targetId,
        string primaryProvider,
        bool fallbackUsed,
        bool mismatchDetected,
        string postgresError,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        stopwatch.Stop();
        await _traceSink(new LearningFeedbackProviderSwitchTrace
        {
            OperationId = operationId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Mode = mode.ToString(),
            OperationKind = operationKind,
            TargetType = targetType,
            TargetId = targetId,
            PrimaryProvider = primaryProvider,
            FallbackUsed = fallbackUsed,
            MismatchDetected = mismatchDetected,
            PostgresError = postgresError,
            DurationMs = stopwatch.Elapsed.TotalMilliseconds,
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool SameHash<T>(T left, T right)
        => string.Equals(ComputeCanonicalHash(left), ComputeCanonicalHash(right), StringComparison.Ordinal);

    private static string ComputeCanonicalHash<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var canonical = ToCanonicalJson(value);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes);
    }

    private static string ToCanonicalJson<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Canonicalize(node)?.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? "null";
    }

    private static JsonNode? Canonicalize(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonArray array)
        {
            var normalized = new JsonArray();
            foreach (var item in array)
            {
                normalized.Add(Canonicalize(item));
            }

            return normalized;
        }

        if (node is JsonObject obj)
        {
            var normalized = new JsonObject();
            foreach (var property in obj.OrderBy(static item => item.Key, StringComparer.Ordinal))
            {
                normalized[property.Key] = Canonicalize(property.Value);
            }

            return normalized;
        }

        return node.DeepClone();
    }

    private static async Task<LearningFeedbackReviewRecord?> GetLatestReviewCoreAsync(
        ILearningFeedbackReviewStore store,
        string feedbackId,
        CancellationToken cancellationToken)
    {
        var rows = await store.QueryAsync(new LearningFeedbackReviewQuery { FeedbackId = feedbackId, Limit = 1 }, cancellationToken)
            .ConfigureAwait(false);
        return rows.FirstOrDefault();
    }

    private static async Task<LearningFeedbackSummaryReport> BuildSummaryCoreAsync(
        ILearningFeedbackStore store,
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken)
    {
        var rows = await store.QueryAsync(Clone(query, int.MaxValue, 0), cancellationToken)
            .ConfigureAwait(false);
        return new LearningFeedbackSummaryReport
        {
            GeneratedAt = DateTimeOffset.UnixEpoch,
            WorkspaceId = query.WorkspaceId,
            CollectionId = query.CollectionId,
            FeedbackCount = rows.Count,
            FeedbackByCapability = rows.GroupBy(static item => item.CapabilityId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase),
            FeedbackByKind = rows.GroupBy(static item => item.FeedbackKind, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase),
            FeedbackByTargetType = rows.GroupBy(static item => item.TargetType, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase),
            MetadataOnlyCount = rows.Count(static item => item.MetadataOnly),
            TrainingUseDisabledCount = rows.Count(static item => string.Equals(item.TrainingUse, "disabled_until_review", StringComparison.OrdinalIgnoreCase)),
            RecentFeedback = [.. rows.OrderByDescending(static item => item.CreatedAt).Take(10)]
        };
    }

    private static async Task<LearningFeedbackReviewSummaryReport> BuildReviewSummaryCoreAsync(
        ILearningFeedbackReviewStore store,
        LearningFeedbackReviewQuery query,
        CancellationToken cancellationToken)
    {
        var rows = await store.QueryAsync(Clone(query, int.MaxValue, 0), cancellationToken)
            .ConfigureAwait(false);
        return new LearningFeedbackReviewSummaryReport
        {
            GeneratedAt = DateTimeOffset.UnixEpoch,
            FeedbackCount = rows.Select(static item => item.FeedbackId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            PendingReviewCount = rows.Count(static item => item.ReviewStatus == FeedbackReviewStatus.PendingReview),
            ApprovedCount = rows.Count(static item => item.ReviewStatus == FeedbackReviewStatus.ApprovedForDataset),
            RejectedCount = rows.Count(static item => item.ReviewStatus == FeedbackReviewStatus.Rejected),
            NeedsRedactionCount = rows.Count(static item => item.ReviewStatus == FeedbackReviewStatus.NeedsRedaction),
            NeedsMoreEvidenceCount = rows.Count(static item => item.ReviewStatus == FeedbackReviewStatus.NeedsMoreEvidence),
            ReviewsByStatus = rows.GroupBy(static item => item.ReviewStatus.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase),
            RecentReviews = [.. rows.OrderByDescending(static item => item.ReviewedAt).Take(10)]
        };
    }

    private static async Task<string> ExportFeedbackCoreAsync(
        ILearningFeedbackStore store,
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken)
    {
        var rows = await store.QueryAsync(Clone(query, int.MaxValue, 0), cancellationToken)
            .ConfigureAwait(false);
        return string.Join(Environment.NewLine, rows.OrderBy(static item => item.FeedbackId, StringComparer.OrdinalIgnoreCase)
            .Select(static item => ToCanonicalJson(item)));
    }

    private static async Task<IReadOnlyList<FeedbackFeatureCandidate>> QueryCandidatesInScopeAsync(
        ILearningFeatureCandidateStore store,
        LearningFeatureCandidateQuery query,
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var rows = await store.QueryAsync(Clone(query, int.MaxValue, 0), cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Where(item =>
            MetadataMatches(item.Metadata, "workspaceId", workspaceId)
            && MetadataMatches(item.Metadata, "collectionId", collectionId))
            .OrderBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)];
    }

    private static async Task<string> ExportCandidatesCoreAsync(
        ILearningFeatureCandidateStore store,
        LearningFeatureCandidateQuery query,
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var rows = await QueryCandidatesInScopeAsync(store, query, workspaceId, collectionId, cancellationToken)
            .ConfigureAwait(false);
        return string.Join(Environment.NewLine, rows.Select(static item => ToCanonicalJson(item)));
    }

    private static bool MetadataMatches(IReadOnlyDictionary<string, string> metadata, string key, string expected)
        => metadata.TryGetValue(key, out var actual)
           && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static string ResolveMetadata(IReadOnlyDictionary<string, string> metadata, string key)
        => metadata.TryGetValue(key, out var value) ? value : string.Empty;

    private static LearningFeedbackEventQuery Clone(LearningFeedbackEventQuery query, int limit, int offset)
        => new()
        {
            WorkspaceId = query.WorkspaceId,
            CollectionId = query.CollectionId,
            Source = query.Source,
            SourceOperationId = query.SourceOperationId,
            CapabilityId = query.CapabilityId,
            TargetId = query.TargetId,
            TargetType = query.TargetType,
            FeedbackKind = query.FeedbackKind,
            Limit = limit,
            Offset = offset
        };

    private static LearningFeedbackReviewQuery Clone(LearningFeedbackReviewQuery query, int limit, int offset)
        => new()
        {
            FeedbackId = query.FeedbackId,
            ReviewStatus = query.ReviewStatus,
            Reviewer = query.Reviewer,
            Limit = limit,
            Offset = offset
        };

    private static LearningFeatureCandidateQuery Clone(LearningFeatureCandidateQuery query, int limit, int offset)
        => new()
        {
            CandidateId = query.CandidateId,
            SourceFeedbackId = query.SourceFeedbackId,
            CapabilityId = query.CapabilityId,
            TargetType = query.TargetType,
            LabelKind = query.LabelKind,
            TrainingUse = query.TrainingUse,
            Limit = limit,
            Offset = offset
        };
}
