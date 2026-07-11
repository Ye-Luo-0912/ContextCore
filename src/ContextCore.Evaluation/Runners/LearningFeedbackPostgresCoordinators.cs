using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.ControlRoom.Services;

/// <summary>Learning feedback dual-write 旁路协调器；正式写入仍以 FileSystem 为准。</summary>
public sealed class LearningFeedbackDualWriteCoordinator
{
    private readonly ILearningFeedbackStore _fileFeedbackStore;
    private readonly ILearningFeedbackReviewStore _fileReviewStore;
    private readonly ILearningFeatureCandidateStore _fileCandidateStore;
    private readonly ILearningFeedbackStore _postgresFeedbackStore;
    private readonly ILearningFeedbackReviewStore _postgresReviewStore;
    private readonly ILearningFeatureCandidateStore _postgresCandidateStore;
    private readonly LearningFeedbackDualWriteOptions _options;
    private readonly Func<LearningFeedbackDualWriteTrace, CancellationToken, Task> _traceSink;

    public LearningFeedbackDualWriteCoordinator(
        ILearningFeedbackStore fileFeedbackStore,
        ILearningFeedbackReviewStore fileReviewStore,
        ILearningFeatureCandidateStore fileCandidateStore,
        ILearningFeedbackStore postgresFeedbackStore,
        ILearningFeedbackReviewStore postgresReviewStore,
        ILearningFeatureCandidateStore postgresCandidateStore,
        LearningFeedbackDualWriteOptions options,
        Func<LearningFeedbackDualWriteTrace, CancellationToken, Task> traceSink)
    {
        _fileFeedbackStore = fileFeedbackStore;
        _fileReviewStore = fileReviewStore;
        _fileCandidateStore = fileCandidateStore;
        _postgresFeedbackStore = postgresFeedbackStore;
        _postgresReviewStore = postgresReviewStore;
        _postgresCandidateStore = postgresCandidateStore;
        _options = options;
        _traceSink = traceSink;
    }

    public Task UpsertFeedbackAsync(LearningFeedbackEvent feedback, CancellationToken cancellationToken)
        => WriteAsync(
            "feedback",
            feedback.FeedbackId,
            feedback.WorkspaceId,
            feedback.CollectionId,
            fileWrite: token => _fileFeedbackStore.UpsertAsync(feedback, token),
            postgresWrite: token => _postgresFeedbackStore.UpsertAsync(feedback, token),
            cancellationToken);

    public Task UpsertReviewAsync(LearningFeedbackReviewRecord review, CancellationToken cancellationToken)
        => WriteAsync(
            "review",
            review.FeedbackId,
            ResolveMetadata(review.Metadata, "workspaceId"),
            ResolveMetadata(review.Metadata, "collectionId"),
            fileWrite: token => _fileReviewStore.UpsertAsync(review, token),
            postgresWrite: token => _postgresReviewStore.UpsertAsync(review, token),
            cancellationToken);

    public Task UpsertCandidateAsync(FeedbackFeatureCandidate candidate, CancellationToken cancellationToken)
        => WriteAsync(
            "feature_candidate",
            candidate.CandidateId,
            ResolveMetadata(candidate.Metadata, "workspaceId"),
            ResolveMetadata(candidate.Metadata, "collectionId"),
            fileWrite: token => _fileCandidateStore.UpsertAsync(candidate, token),
            postgresWrite: token => _postgresCandidateStore.UpsertAsync(candidate, token),
            cancellationToken);

    private async Task WriteAsync(
        string targetKind,
        string targetId,
        string workspaceId,
        string collectionId,
        Func<CancellationToken, Task> fileWrite,
        Func<CancellationToken, Task> postgresWrite,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var fileSucceeded = false;
        var postgresSucceeded = false;
        var fallbackUsed = false;
        var postgresError = string.Empty;
        var operationId = $"learning-feedback-dual-write-{targetKind}-{Guid.NewGuid():N}";

        await fileWrite(cancellationToken).ConfigureAwait(false);
        fileSucceeded = true;

        if (_options.Enabled && _options.WritePostgres)
        {
            try
            {
                await postgresWrite(cancellationToken).ConfigureAwait(false);
                postgresSucceeded = true;
            }
            catch (Exception ex) when (_options.FallbackOnPostgresFailure)
            {
                fallbackUsed = true;
                postgresError = ex.GetType().Name;
            }
        }

        stopwatch.Stop();
        if (_options.TraceEnabled)
        {
            await _traceSink(new LearningFeedbackDualWriteTrace
            {
                OperationId = operationId,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                TargetKind = targetKind,
                TargetId = targetId,
                FileSystemWriteSucceeded = fileSucceeded,
                PostgresWriteSucceeded = postgresSucceeded,
                PostgresError = postgresError,
                FallbackUsed = fallbackUsed,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds
            }, cancellationToken).ConfigureAwait(false);
        }

        if (!postgresSucceeded && _options.Enabled && _options.WritePostgres && !_options.FallbackOnPostgresFailure)
        {
            throw new InvalidOperationException($"Postgres learning feedback write failed for {targetKind}:{targetId}.");
        }
    }

    private static string ResolveMetadata(IReadOnlyDictionary<string, string> metadata, string key)
        => metadata.TryGetValue(key, out var value) ? value : string.Empty;
}

/// <summary>Learning feedback shadow-read 协调器；FileSystem 结果仍作为正式结果。</summary>
public sealed class LearningFeedbackShadowReadCoordinator
{
    private readonly LearningFeedbackShadowReadOptions _options;
    private readonly Func<LearningFeedbackShadowReadTrace, CancellationToken, Task> _traceSink;

    public LearningFeedbackShadowReadCoordinator(
        LearningFeedbackShadowReadOptions options,
        Func<LearningFeedbackShadowReadTrace, CancellationToken, Task> traceSink)
    {
        _options = options;
        _traceSink = traceSink;
    }

    public async Task<T> CompareAsync<T>(
        string readKind,
        string targetId,
        string workspaceId,
        string collectionId,
        Func<CancellationToken, Task<T>> fileRead,
        Func<CancellationToken, Task<T>> postgresRead,
        CancellationToken cancellationToken)
    {
        var fileStopwatch = Stopwatch.StartNew();
        var fileResult = await fileRead(cancellationToken).ConfigureAwait(false);
        fileStopwatch.Stop();

        var postgresStopwatch = new Stopwatch();
        var postgresSucceeded = false;
        var postgresError = string.Empty;
        var postgresHash = string.Empty;
        var mismatch = false;
        var mismatchReason = string.Empty;
        var fileHash = ComputeStableHash(fileResult);

        if (_options.Enabled && _options.ReadPostgres)
        {
            try
            {
                postgresStopwatch.Start();
                var postgresResult = await postgresRead(cancellationToken).ConfigureAwait(false);
                postgresStopwatch.Stop();
                postgresSucceeded = true;
                postgresHash = ComputeStableHash(postgresResult);
                mismatch = _options.CompareResults && !string.Equals(fileHash, postgresHash, StringComparison.Ordinal);
                mismatchReason = mismatch ? "ResultHashMismatch" : string.Empty;
            }
            catch (Exception ex)
            {
                postgresStopwatch.Stop();
                postgresError = ex.GetType().Name;
            }
        }

        if (_options.TraceEnabled)
        {
            await _traceSink(new LearningFeedbackShadowReadTrace
            {
                OperationId = $"learning-feedback-shadow-read-{readKind}-{Guid.NewGuid():N}",
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ReadKind = readKind,
                TargetId = targetId,
                FileSystemReadSucceeded = true,
                PostgresReadSucceeded = postgresSucceeded,
                FileSystemResultHash = fileHash,
                PostgresResultHash = postgresHash,
                MismatchDetected = mismatch,
                MismatchReason = mismatchReason,
                PostgresError = postgresError,
                FallbackUsed = !postgresSucceeded && _options.Enabled && _options.ReadPostgres,
                FileSystemDurationMs = fileStopwatch.Elapsed.TotalMilliseconds,
                PostgresDurationMs = postgresStopwatch.Elapsed.TotalMilliseconds
            }, cancellationToken).ConfigureAwait(false);
        }

        if (mismatch && _options.FailOnMismatch)
        {
            throw new InvalidOperationException($"Learning feedback shadow-read mismatch: {readKind}.");
        }

        return fileResult;
    }

    public static string ComputeStableHash<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes);
    }
}
