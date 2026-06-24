using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>CandidateConstraint 人工激活 / 拒绝服务。</summary>
public sealed class CandidateConstraintReviewService
{
    private readonly IConstraintStore _constraintStore;
    private readonly ICandidateConstraintReviewStore _reviewStore;

    public CandidateConstraintReviewService(
        IConstraintStore constraintStore,
        ICandidateConstraintReviewStore reviewStore)
    {
        _constraintStore = constraintStore;
        _reviewStore = reviewStore;
    }

    public async Task<IReadOnlyList<ContextConstraint>> QueryAsync(
        CandidateConstraintQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.WorkspaceId);

        var constraints = await _constraintStore.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = query.WorkspaceId,
            CollectionId = query.CollectionId,
            Status = query.Status,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);

        return constraints
            .Where(IsCandidateConstraint)
            .OrderByDescending(item => item.UpdatedAt == default ? item.CreatedAt : item.UpdatedAt)
            .Skip(Math.Max(0, query.Offset))
            .Take(query.Limit > 0 ? query.Limit : 20)
            .ToArray();
    }

    public async Task<ContextConstraint?> GetAsync(
        string constraintId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);
        var constraint = await _constraintStore.GetAsync(constraintId, cancellationToken).ConfigureAwait(false);
        return constraint is not null && IsCandidateConstraint(constraint)
            ? constraint
            : null;
    }

    public Task<IReadOnlyList<CandidateConstraintReviewRecord>> GetReviewsAsync(
        string constraintId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);
        return _reviewStore.QueryReviewsAsync(constraintId, cancellationToken);
    }

    public Task<CandidateConstraintReviewResult?> ActivateAsync(
        string constraintId,
        CandidateConstraintReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewAsync(constraintId, "activate", ContextMemoryStatus.Active, request, cancellationToken);
    }

    public Task<CandidateConstraintReviewResult?> RejectAsync(
        string constraintId,
        CandidateConstraintReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewAsync(constraintId, "reject", ContextMemoryStatus.Rejected, request, cancellationToken);
    }

    private async Task<CandidateConstraintReviewResult?> ReviewAsync(
        string constraintId,
        string action,
        ContextMemoryStatus targetStatus,
        CandidateConstraintReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);
        ArgumentNullException.ThrowIfNull(request);

        var candidate = await GetAsync(constraintId, cancellationToken).ConfigureAwait(false);
        if (candidate is null)
        {
            return null;
        }

        if (candidate.Status != ContextMemoryStatus.Candidate)
        {
            throw new ArgumentException(
                $"CandidateConstraint 当前状态为 {candidate.Status}，不能执行 {action}。",
                nameof(constraintId));
        }

        var operationId = string.IsNullOrWhiteSpace(request.OperationId)
            ? Guid.NewGuid().ToString("N")
            : request.OperationId.Trim();
        var reviewer = string.IsNullOrWhiteSpace(request.Reviewer) ? "manual" : request.Reviewer.Trim();
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? action : request.Reason.Trim();
        var now = DateTimeOffset.UtcNow;
        var warnings = new List<string>();
        var reviewId = $"ccr-{BuildShortHash($"{candidate.Id}\u001f{action}\u001f{now:O}")}";

        var source = ResolveSource(candidate);
        if (action.Equals("activate", StringComparison.OrdinalIgnoreCase))
        {
            ValidateSource(source);
            await EnsureNoDuplicateActiveConstraintAsync(candidate, cancellationToken).ConfigureAwait(false);
        }

        var updated = action.Equals("activate", StringComparison.OrdinalIgnoreCase)
            ? BuildActivatedConstraint(candidate, source, reviewer, reason, reviewId, now)
            : BuildRejectedConstraint(candidate, reviewer, reason, now);
        await _constraintStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);

        var review = new CandidateConstraintReviewRecord
        {
            ReviewId = reviewId,
            ConstraintId = candidate.Id,
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            Action = action,
            FromStatus = candidate.Status,
            ToStatus = targetStatus,
            Reviewer = reviewer,
            Reason = reason,
            ActivatedConstraintId = action.Equals("activate", StringComparison.OrdinalIgnoreCase) ? updated.Id : null,
            SourceConstraintGapId = source.SourceConstraintGapId,
            SourceSampleId = source.SourceSampleId,
            SourceOperationId = source.SourceOperationId,
            EvidenceRefs = source.EvidenceRefs,
            CreatedAt = now,
            ReviewedAt = now,
            Metadata = new Dictionary<string, string>(request.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["operationId"] = operationId,
                ["candidateConstraintId"] = candidate.Id,
                ["sourceConstraintGapId"] = source.SourceConstraintGapId,
                ["sourceSampleId"] = source.SourceSampleId,
                ["sourceOperationId"] = source.SourceOperationId,
                ["reviewedAt"] = now.ToString("O")
            },
            Warnings = warnings.ToArray(),
            Errors = Array.Empty<string>()
        };
        await _reviewStore.AppendReviewAsync(review, cancellationToken).ConfigureAwait(false);

        return new CandidateConstraintReviewResult
        {
            OperationId = operationId,
            ConstraintId = candidate.Id,
            Action = action,
            Status = updated.Status,
            ReviewId = review.ReviewId,
            Reviewer = reviewer,
            Reason = reason,
            ReviewedAt = now,
            ActivatedConstraintId = review.ActivatedConstraintId,
            TargetLayer = action.Equals("activate", StringComparison.OrdinalIgnoreCase) ? "ActiveHardConstraint" : "RejectedCandidateConstraint",
            Constraint = updated,
            Review = review,
            Warnings = warnings.ToArray(),
            Errors = Array.Empty<string>()
        };
    }

    private async Task EnsureNoDuplicateActiveConstraintAsync(
        ContextConstraint candidate,
        CancellationToken cancellationToken)
    {
        var active = await _constraintStore.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            Level = ConstraintLevel.Hard,
            Status = ContextMemoryStatus.Active,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);

        var normalized = NormalizeText(candidate.Content);
        var duplicate = active.FirstOrDefault(item =>
            !string.Equals(item.Id, candidate.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeText(item.Content), normalized, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"已存在内容重复的 Active hard constraint：{duplicate.Id}");
        }
    }

    private static ContextConstraint BuildActivatedConstraint(
        ContextConstraint candidate,
        CandidateConstraintSource source,
        string reviewer,
        string reason,
        string reviewId,
        DateTimeOffset now)
    {
        var metadata = new Dictionary<string, string>(candidate.Metadata, StringComparer.OrdinalIgnoreCase);
        if (metadata.TryGetValue("createdFrom", out var createdFrom)
            && !string.IsNullOrWhiteSpace(createdFrom))
        {
            metadata["candidateCreatedFrom"] = createdFrom;
        }

        metadata["createdFrom"] = "candidate_constraint_activate";
        metadata["sourceConstraintGapId"] = source.SourceConstraintGapId;
        metadata["sourceSampleId"] = source.SourceSampleId;
        metadata["sourceOperationId"] = source.SourceOperationId;
        metadata["evidenceRefs"] = string.Join(",", source.EvidenceRefs);
        metadata["reviewer"] = reviewer;
        metadata["reviewReason"] = reason;
        metadata["sourceCandidateConstraintReviewId"] = reviewId;
        metadata["activatedAt"] = now.ToString("O");

        return Clone(candidate, ContextMemoryStatus.Active, ConstraintLevel.Hard, metadata, now);
    }

    private static ContextConstraint BuildRejectedConstraint(
        ContextConstraint candidate,
        string reviewer,
        string reason,
        DateTimeOffset now)
    {
        var metadata = new Dictionary<string, string>(candidate.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["reviewer"] = reviewer,
            ["reviewReason"] = reason,
            ["reviewedAt"] = now.ToString("O")
        };

        return Clone(candidate, ContextMemoryStatus.Rejected, candidate.Level, metadata, now);
    }

    private static ContextConstraint Clone(
        ContextConstraint candidate,
        ContextMemoryStatus status,
        ConstraintLevel level,
        Dictionary<string, string> metadata,
        DateTimeOffset updatedAt)
    {
        return new ContextConstraint
        {
            Id = candidate.Id,
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            Scope = candidate.Scope,
            Level = level,
            Content = candidate.Content,
            AppliesToRefs = candidate.AppliesToRefs.ToArray(),
            SourceRefs = candidate.SourceRefs.ToArray(),
            Status = status,
            Confidence = candidate.Confidence,
            Metadata = metadata,
            CreatedAt = candidate.CreatedAt,
            UpdatedAt = updatedAt
        };
    }

    private static bool IsCandidateConstraint(ContextConstraint constraint)
    {
        return constraint.Metadata.ContainsKey("sourceConstraintGapId")
            || (constraint.Metadata.TryGetValue("createdFrom", out var createdFrom)
                && (string.Equals(createdFrom, "constraint_gap_accept", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(createdFrom, "candidate_constraint_activate", StringComparison.OrdinalIgnoreCase)));
    }

    private static CandidateConstraintSource ResolveSource(ContextConstraint candidate)
    {
        var metadata = candidate.Metadata;
        metadata.TryGetValue("sourceConstraintGapId", out var gapId);
        metadata.TryGetValue("sourceSampleId", out var sampleId);
        metadata.TryGetValue("sourceOperationId", out var operationId);
        metadata.TryGetValue("evidenceRefs", out var evidenceRefsText);
        var evidenceRefs = SplitRefs(evidenceRefsText)
            .Concat(candidate.SourceRefs.Where(IsEvidenceRef))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new CandidateConstraintSource(
            gapId ?? string.Empty,
            sampleId ?? string.Empty,
            operationId ?? string.Empty,
            evidenceRefs);
    }

    private static void ValidateSource(CandidateConstraintSource source)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(source.SourceConstraintGapId)) missing.Add("sourceConstraintGapId");
        if (string.IsNullOrWhiteSpace(source.SourceSampleId)) missing.Add("sourceSampleId");
        if (string.IsNullOrWhiteSpace(source.SourceOperationId)) missing.Add("sourceOperationId");
        if (source.EvidenceRefs.Count == 0) missing.Add("evidenceRefs");
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"CandidateConstraint 缺少来源字段：{string.Join(", ", missing)}");
        }
    }

    private static IReadOnlyList<string> SplitRefs(string? refs)
    {
        return string.IsNullOrWhiteSpace(refs)
            ? Array.Empty<string>()
            : refs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsEvidenceRef(string value)
    {
        return value.StartsWith("event", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("eval:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("evidence", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeText(string value)
    {
        var builder = new StringBuilder(value.Trim().Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (!char.IsWhiteSpace(ch) && !char.IsPunctuation(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static string BuildShortHash(string key)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private sealed record CandidateConstraintSource(
        string SourceConstraintGapId,
        string SourceSampleId,
        string SourceOperationId,
        IReadOnlyList<string> EvidenceRefs);
}
