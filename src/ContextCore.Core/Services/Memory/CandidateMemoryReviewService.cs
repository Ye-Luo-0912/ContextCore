using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>CandidateMemory 人工 review / cleanup 服务，不执行 stable promotion。</summary>
public sealed class CandidateMemoryReviewService
{
    private readonly IMemoryStore? _memoryStore;
    private readonly IConstraintStore? _constraintStore;
    private readonly ICandidateMemoryReviewStore? _reviewStore;

    public CandidateMemoryReviewService(
        IMemoryStore? memoryStore,
        IConstraintStore? constraintStore,
        ICandidateMemoryReviewStore? reviewStore)
    {
        _memoryStore = memoryStore;
        _constraintStore = constraintStore;
        _reviewStore = reviewStore;
    }

    public Task<IReadOnlyList<CandidateMemoryReviewRecord>> GetReviewsAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        EnsureReviewStore();
        return _reviewStore!.QueryReviewsAsync(candidateId, cancellationToken);
    }

    public Task<CandidateMemoryReviewResult?> MarkReadyForStableReviewAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewAsync(candidateId, CandidateMemoryReviewActions.MarkReadyForStableReview, request, cancellationToken);
    }

    public Task<CandidateMemoryReviewResult?> NeedsMoreEvidenceAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewAsync(candidateId, CandidateMemoryReviewActions.NeedsMoreEvidence, request, cancellationToken);
    }

    public Task<CandidateMemoryReviewResult?> RejectAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewAsync(candidateId, CandidateMemoryReviewActions.Reject, request, cancellationToken);
    }

    public Task<CandidateMemoryReviewResult?> ExpireAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewAsync(candidateId, CandidateMemoryReviewActions.Expire, request, cancellationToken);
    }

    public Task<CandidateMemoryReviewResult?> SupersedeAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewAsync(candidateId, CandidateMemoryReviewActions.Supersede, request, cancellationToken);
    }

    private async Task<CandidateMemoryReviewResult?> ReviewAsync(
        string candidateId,
        string action,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        EnsureReviewStore();

        var candidate = await FindCandidateAsync(candidateId, request.WorkspaceId, request.CollectionId, cancellationToken)
            .ConfigureAwait(false);
        if (candidate is null)
        {
            return null;
        }

        ValidateCandidateCanTransition(candidate, action);
        CandidateSource? supersedeTarget = null;
        if (string.Equals(action, CandidateMemoryReviewActions.Supersede, StringComparison.OrdinalIgnoreCase))
        {
            supersedeTarget = await ResolveSupersedeTargetAsync(candidate, request, cancellationToken).ConfigureAwait(false);
        }

        var now = DateTimeOffset.UtcNow;
        var operationId = string.IsNullOrWhiteSpace(request.OperationId)
            ? Guid.NewGuid().ToString("N")
            : request.OperationId.Trim();
        var reviewer = string.IsNullOrWhiteSpace(request.Reviewer) ? "manual" : request.Reviewer.Trim();
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? action : request.Reason.Trim();
        var reviewId = $"cmr-{BuildShortHash($"{candidate.Id}\u001f{action}\u001f{now:O}")}";
        var warnings = BuildWarnings(candidate).ToList();
        var toStatus = ResolveTargetStatus(action);
        var state = ResolveReviewState(action);

        var updated = await SaveUpdatedCandidateAsync(
            candidate,
            action,
            state,
            toStatus,
            reviewer,
            reason,
            reviewId,
            operationId,
            supersedeTarget?.Id,
            request.Metadata,
            now,
            cancellationToken).ConfigureAwait(false);

        var review = new CandidateMemoryReviewRecord
        {
            ReviewId = reviewId,
            CandidateId = candidate.Id,
            CandidateKind = candidate.Kind,
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            Action = action,
            FromStatus = candidate.Status,
            ToStatus = toStatus,
            Reviewer = reviewer,
            Reason = reason,
            SupersedeTargetCandidateId = supersedeTarget?.Id,
            EvidenceRefs = candidate.EvidenceRefs,
            SourceRefs = candidate.SourceRefs,
            CreatedAt = now,
            ReviewedAt = now,
            Metadata = new Dictionary<string, string>(request.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["operationId"] = operationId,
                ["candidateReviewState"] = state,
                ["reviewedAt"] = now.ToString("O")
            },
            Warnings = warnings.ToArray(),
            Errors = Array.Empty<string>()
        };
        await _reviewStore!.AppendReviewAsync(review, cancellationToken).ConfigureAwait(false);

        return new CandidateMemoryReviewResult
        {
            OperationId = operationId,
            CandidateId = candidate.Id,
            CandidateKind = candidate.Kind,
            Action = action,
            FromStatus = candidate.Status,
            ToStatus = toStatus,
            ReviewId = reviewId,
            Reviewer = reviewer,
            Reason = reason,
            ReviewedAt = now,
            SupersedeTargetCandidateId = supersedeTarget?.Id,
            Candidate = updated,
            Review = review,
            Warnings = warnings.ToArray(),
            Errors = Array.Empty<string>()
        };
    }

    private async Task<CandidateSource?> FindCandidateAsync(
        string candidateId,
        string workspaceId,
        string? collectionId,
        CancellationToken cancellationToken)
    {
        if (_memoryStore is not null)
        {
            var memoryItems = await _memoryStore.QueryAsync(new ContextMemoryQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Take = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            var memory = memoryItems.FirstOrDefault(item => string.Equals(item.Id, candidateId, StringComparison.OrdinalIgnoreCase));
            if (memory is not null)
            {
                return CandidateSource.FromMemory(memory);
            }
        }

        if (_constraintStore is not null)
        {
            var constraints = await _constraintStore.QueryAsync(new ContextConstraintQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Take = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            var constraint = constraints.FirstOrDefault(item => string.Equals(item.Id, candidateId, StringComparison.OrdinalIgnoreCase));
            if (constraint is not null)
            {
                return CandidateSource.FromConstraint(constraint);
            }
        }

        return null;
    }

    private async Task<CandidateSource> ResolveSupersedeTargetAsync(
        CandidateSource candidate,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SupersedeTargetCandidateId))
        {
            throw new ArgumentException("Supersede 需要 SupersedeTargetCandidateId。", nameof(request));
        }

        if (string.Equals(request.SupersedeTargetCandidateId, candidate.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Supersede target 不能是当前 candidate。", nameof(request));
        }

        var target = await FindCandidateAsync(
            request.SupersedeTargetCandidateId,
            request.WorkspaceId,
            request.CollectionId,
            cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            throw new ArgumentException($"Supersede target 不存在：{request.SupersedeTargetCandidateId}", nameof(request));
        }

        if (target.Status != ContextMemoryStatus.Candidate)
        {
            throw new ArgumentException($"Supersede target 当前状态为 {target.Status}，不是 Candidate。", nameof(request));
        }

        if (IsStableOrActiveTarget(target))
        {
            throw new ArgumentException("Supersede target 不能是 stable / active item。", nameof(request));
        }

        return target;
    }

    private async Task<CandidateMemoryRecord> SaveUpdatedCandidateAsync(
        CandidateSource candidate,
        string action,
        string reviewState,
        ContextMemoryStatus toStatus,
        string reviewer,
        string reason,
        string reviewId,
        string operationId,
        string? supersedeTargetId,
        IReadOnlyDictionary<string, string> requestMetadata,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, string>(candidate.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["candidateReviewState"] = reviewState,
            ["candidateReviewAction"] = action,
            ["lastCandidateMemoryReviewId"] = reviewId,
            ["reviewer"] = reviewer,
            ["reviewReason"] = reason,
            ["reviewedAt"] = now.ToString("O"),
            ["operationId"] = operationId
        };
        foreach (var pair in requestMetadata)
        {
            metadata[$"reviewMetadata.{pair.Key}"] = pair.Value;
        }

        if (string.Equals(action, CandidateMemoryReviewActions.Expire, StringComparison.OrdinalIgnoreCase))
        {
            metadata["lifecycle"] = CandidateMemoryLifecycle.Stale;
            metadata["expiresAt"] = now.ToString("O");
        }
        else if (string.Equals(action, CandidateMemoryReviewActions.Reject, StringComparison.OrdinalIgnoreCase))
        {
            metadata["lifecycle"] = CandidateMemoryLifecycle.Rejected;
        }
        else if (string.Equals(action, CandidateMemoryReviewActions.Supersede, StringComparison.OrdinalIgnoreCase))
        {
            metadata["lifecycle"] = CandidateMemoryLifecycle.Superseded;
            metadata["supersededByCandidateId"] = supersedeTargetId ?? string.Empty;
        }
        else
        {
            metadata["lifecycle"] = CandidateMemoryLifecycle.Current;
        }

        if (candidate.MemoryItem is not null)
        {
            var updated = new ContextMemoryItem
            {
                Id = candidate.MemoryItem.Id,
                WorkspaceId = candidate.MemoryItem.WorkspaceId,
                CollectionId = candidate.MemoryItem.CollectionId,
                Layer = candidate.MemoryItem.Layer,
                Status = toStatus,
                Type = candidate.MemoryItem.Type,
                Content = candidate.MemoryItem.Content,
                ContentFormat = candidate.MemoryItem.ContentFormat,
                Tags = candidate.MemoryItem.Tags.ToArray(),
                SourceRefs = candidate.MemoryItem.SourceRefs.ToArray(),
                RelationRefs = candidate.MemoryItem.RelationRefs.ToArray(),
                Importance = candidate.MemoryItem.Importance,
                Confidence = candidate.MemoryItem.Confidence,
                Version = candidate.MemoryItem.Version + 1,
                Metadata = metadata,
                CreatedAt = candidate.MemoryItem.CreatedAt,
                UpdatedAt = now
            };
            await _memoryStore!.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            return ToRecord(updated);
        }

        var constraint = candidate.Constraint!;
        var updatedConstraint = new ContextConstraint
        {
            Id = constraint.Id,
            WorkspaceId = constraint.WorkspaceId,
            CollectionId = constraint.CollectionId,
            Scope = constraint.Scope,
            Level = constraint.Level,
            Content = constraint.Content,
            AppliesToRefs = constraint.AppliesToRefs.ToArray(),
            SourceRefs = constraint.SourceRefs.ToArray(),
            Status = toStatus,
            Confidence = constraint.Confidence,
            Metadata = metadata,
            CreatedAt = constraint.CreatedAt,
            UpdatedAt = now
        };
        await _constraintStore!.SaveAsync(updatedConstraint, cancellationToken).ConfigureAwait(false);
        return ToRecord(updatedConstraint);
    }

    private static void ValidateCandidateCanTransition(CandidateSource candidate, string action)
    {
        if (IsStableOrActiveTarget(candidate))
        {
            throw new ArgumentException("Stable / active item 不允许通过 candidate endpoint 修改。", nameof(candidate));
        }

        if (candidate.Status != ContextMemoryStatus.Candidate)
        {
            throw new ArgumentException($"Candidate 当前状态为 {candidate.Status}，不能执行 {action}。", nameof(candidate));
        }
    }

    private static bool IsStableOrActiveTarget(CandidateSource candidate)
    {
        return candidate.Status is ContextMemoryStatus.Stable or ContextMemoryStatus.Active or ContextMemoryStatus.Verified
            || candidate.MemoryItem?.Layer is ContextMemoryLayer.Stable or ContextMemoryLayer.Global;
    }

    private static ContextMemoryStatus ResolveTargetStatus(string action)
    {
        return action switch
        {
            CandidateMemoryReviewActions.Reject => ContextMemoryStatus.Rejected,
            CandidateMemoryReviewActions.Expire => ContextMemoryStatus.Deprecated,
            CandidateMemoryReviewActions.Supersede => ContextMemoryStatus.Deprecated,
            _ => ContextMemoryStatus.Candidate
        };
    }

    private static string ResolveReviewState(string action)
    {
        return action switch
        {
            CandidateMemoryReviewActions.MarkReadyForStableReview => CandidateMemoryReviewStates.ReadyForStableReview,
            CandidateMemoryReviewActions.NeedsMoreEvidence => CandidateMemoryReviewStates.NeedsMoreEvidence,
            CandidateMemoryReviewActions.Reject => CandidateMemoryReviewStates.Rejected,
            CandidateMemoryReviewActions.Expire => CandidateMemoryReviewStates.Expired,
            CandidateMemoryReviewActions.Supersede => CandidateMemoryReviewStates.Superseded,
            _ => action
        };
    }

    private static IEnumerable<string> BuildWarnings(CandidateSource candidate)
    {
        if (candidate.EvidenceRefs.Count == 0)
        {
            yield return "candidate has no evidence refs.";
        }

        if (candidate.SourceRefs.Count == 0
            && !candidate.Metadata.ContainsKey("sourcePromotionCandidateId")
            && !candidate.Metadata.ContainsKey("sourceCandidateId")
            && !candidate.Metadata.ContainsKey("sourceStableReviewCandidateId")
            && !candidate.Metadata.ContainsKey("sourceConstraintGapId")
            && !candidate.Metadata.ContainsKey("sourceFeedbackId")
            && !candidate.Metadata.ContainsKey("sourceLearningCaseId"))
        {
            yield return "candidate provenance links are missing.";
        }
    }

    private void EnsureReviewStore()
    {
        if (_reviewStore is null)
        {
            throw new InvalidOperationException("当前 provider 未注册 CandidateMemory review 存储。");
        }
    }

    private static CandidateMemoryRecord ToRecord(ContextMemoryItem item)
    {
        var evidenceRefs = ResolveEvidenceRefs(item.SourceRefs, item.Metadata);
        return new CandidateMemoryRecord
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = GetMetadata(item.Metadata, "sessionId"),
            CandidateKind = IsDecision(item.Type, item.Metadata) ? CandidateMemoryKinds.Decision : CandidateMemoryKinds.Memory,
            Type = item.Type,
            Title = ResolveTitle(item.Content),
            Summary = ResolveSummary(item.Content),
            Content = item.Content,
            Status = item.Status,
            Lifecycle = ResolveLifecycle(item.Status, item.Metadata),
            Importance = item.Importance,
            Confidence = item.Confidence,
            SourceRefs = item.SourceRefs,
            EvidenceRefs = evidenceRefs,
            PromotionCandidateId = ResolvePromotionCandidateId(item.SourceRefs, item.Metadata),
            StableReviewCandidateId = GetMetadata(item.Metadata, "sourceStableReviewCandidateId"),
            ConstraintGapId = null,
            FeedbackId = GetMetadata(item.Metadata, "sourceFeedbackId"),
            LearningCaseId = GetMetadata(item.Metadata, "sourceLearningCaseId"),
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
            ExpiresAt = ParseDateTime(GetMetadata(item.Metadata, "expiresAt")),
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static CandidateMemoryRecord ToRecord(ContextConstraint item)
    {
        var evidenceRefs = ResolveEvidenceRefs(item.SourceRefs, item.Metadata);
        return new CandidateMemoryRecord
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId ?? string.Empty,
            SessionId = GetMetadata(item.Metadata, "sessionId"),
            CandidateKind = CandidateMemoryKinds.Constraint,
            Type = item.Level.ToString(),
            Title = ResolveTitle(item.Content),
            Summary = ResolveSummary(item.Content),
            Content = item.Content,
            Status = item.Status,
            Lifecycle = ResolveLifecycle(item.Status, item.Metadata),
            Importance = item.Level == ConstraintLevel.Hard ? 1.0 : 0.8,
            Confidence = item.Confidence,
            SourceRefs = item.SourceRefs,
            EvidenceRefs = evidenceRefs,
            PromotionCandidateId = ResolvePromotionCandidateId(item.SourceRefs, item.Metadata),
            StableReviewCandidateId = GetMetadata(item.Metadata, "sourceStableReviewCandidateId"),
            ConstraintGapId = GetMetadata(item.Metadata, "sourceConstraintGapId"),
            FeedbackId = GetMetadata(item.Metadata, "sourceFeedbackId"),
            LearningCaseId = GetMetadata(item.Metadata, "sourceLearningCaseId"),
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
            ExpiresAt = ParseDateTime(GetMetadata(item.Metadata, "expiresAt")),
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static bool IsDecision(string type, IReadOnlyDictionary<string, string> metadata)
    {
        return string.Equals(type, "decision", StringComparison.OrdinalIgnoreCase)
            || string.Equals(GetMetadata(metadata, "suggestedTargetLayer"), "DecisionRecord", StringComparison.OrdinalIgnoreCase)
            || string.Equals(GetMetadata(metadata, "targetLayer"), "DecisionRecord", StringComparison.OrdinalIgnoreCase)
            || string.Equals(GetMetadata(metadata, "sourcePromotionKind"), "RecentDecision", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ResolveEvidenceRefs(
        IReadOnlyList<string> sourceRefs,
        IReadOnlyDictionary<string, string> metadata)
    {
        var refs = new List<string>();
        if (metadata.TryGetValue("evidenceRefs", out var value))
        {
            refs.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        refs.AddRange(sourceRefs.Where(reference =>
            !reference.StartsWith("stpc-", StringComparison.OrdinalIgnoreCase)
            && !reference.StartsWith("src-", StringComparison.OrdinalIgnoreCase)
            && !reference.StartsWith("clc-", StringComparison.OrdinalIgnoreCase)));
        return refs
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? ResolvePromotionCandidateId(
        IReadOnlyList<string> sourceRefs,
        IReadOnlyDictionary<string, string> metadata)
    {
        return GetMetadata(metadata, "sourcePromotionCandidateId")
            ?? GetMetadata(metadata, "sourceCandidateId")
            ?? sourceRefs.FirstOrDefault(reference => reference.StartsWith("stpc-", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveLifecycle(
        ContextMemoryStatus status,
        IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata.TryGetValue("lifecycle", out var lifecycle) && !string.IsNullOrWhiteSpace(lifecycle))
        {
            return lifecycle;
        }

        return status switch
        {
            ContextMemoryStatus.Deprecated => CandidateMemoryLifecycle.Stale,
            ContextMemoryStatus.Rejected => CandidateMemoryLifecycle.Rejected,
            _ => CandidateMemoryLifecycle.Current
        };
    }

    private static DateTimeOffset? ParseDateTime(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string? GetMetadata(IReadOnlyDictionary<string, string> metadata, string key)
    {
        return metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    }

    private static string ResolveTitle(string content)
    {
        var first = (content ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first))
        {
            return "(untitled candidate)";
        }

        return first.Length <= 120 ? first : first[..120];
    }

    private static string ResolveSummary(string content)
    {
        var normalized = (content ?? string.Empty).Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240];
    }

    private static string BuildShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private sealed class CandidateSource
    {
        public string Id { get; init; } = string.Empty;

        public string WorkspaceId { get; init; } = string.Empty;

        public string? CollectionId { get; init; }

        public string Kind { get; init; } = string.Empty;

        public ContextMemoryStatus Status { get; init; }

        public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

        public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public ContextMemoryItem? MemoryItem { get; init; }

        public ContextConstraint? Constraint { get; init; }

        public static CandidateSource FromMemory(ContextMemoryItem item)
        {
            return new CandidateSource
            {
                Id = item.Id,
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                Kind = IsDecision(item.Type, item.Metadata) ? CandidateMemoryKinds.Decision : CandidateMemoryKinds.Memory,
                Status = item.Status,
                EvidenceRefs = ResolveEvidenceRefs(item.SourceRefs, item.Metadata),
                SourceRefs = item.SourceRefs,
                Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase),
                MemoryItem = item
            };
        }

        public static CandidateSource FromConstraint(ContextConstraint item)
        {
            return new CandidateSource
            {
                Id = item.Id,
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                Kind = CandidateMemoryKinds.Constraint,
                Status = item.Status,
                EvidenceRefs = ResolveEvidenceRefs(item.SourceRefs, item.Metadata),
                SourceRefs = item.SourceRefs,
                Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase),
                Constraint = item
            };
        }
    }
}
