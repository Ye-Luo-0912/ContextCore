using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 从已接受的短期晋升候选项生成 Stable review 候选项。
/// 该服务只创建人工复核候选项，不写入 StableMemory。
/// </summary>
public sealed class StableReviewCandidateService
{
    private const string GeneratedBy = "stable-review-candidate-service/rule-based";
    private const string PolicyVersion = "stable-review-readiness-policy/v1";
    private readonly IShortTermPromotionCandidateStore _promotionCandidateStore;
    private readonly IStableReviewCandidateStore _stableReviewCandidateStore;
    private readonly IMemoryStore? _memoryStore;
    private readonly IConstraintStore? _constraintStore;
    private readonly IContextLearningStore? _learningStore;

    public StableReviewCandidateService(
        IShortTermPromotionCandidateStore promotionCandidateStore,
        IStableReviewCandidateStore stableReviewCandidateStore,
        IMemoryStore? memoryStore,
        IConstraintStore? constraintStore,
        IContextLearningStore? learningStore)
    {
        _promotionCandidateStore = promotionCandidateStore;
        _stableReviewCandidateStore = stableReviewCandidateStore;
        _memoryStore = memoryStore;
        _constraintStore = constraintStore;
        _learningStore = learningStore;
    }

    public async Task<IReadOnlyList<StableReviewCandidate>> GenerateAsync(
        StableReviewCandidateGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);

        var acceptedCandidates = await _promotionCandidateStore.QueryAsync(new ShortTermPromotionCandidateQuery
        {
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            SessionId = request.SessionId,
            Status = PromotionCandidateStatus.Accepted,
            Limit = request.Limit > 0 ? request.Limit : 100,
            Offset = Math.Max(0, request.Offset)
        }, cancellationToken).ConfigureAwait(false);

        var results = new List<StableReviewCandidate>();
        foreach (var sourceCandidate in acceptedCandidates.OrderByDescending(static candidate => candidate.CreatedAt))
        {
            var suggestedStableTarget = ResolveSuggestedStableTarget(sourceCandidate);
            if (string.IsNullOrWhiteSpace(suggestedStableTarget))
            {
                continue;
            }

            var targetKind = ResolveAcceptedTargetKind(sourceCandidate, suggestedStableTarget);
            var targetItemId = ResolveAcceptedTargetItemId(sourceCandidate, targetKind);
            var stableReviewCandidateId = BuildStableReviewCandidateId(
                sourceCandidate,
                targetItemId,
                suggestedStableTarget);

            var existing = await _stableReviewCandidateStore.GetAsync(stableReviewCandidateId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                results.Add(existing);
                continue;
            }

            var sourceLearningCase = await ResolveSourceLearningCaseAsync(sourceCandidate, cancellationToken)
                .ConfigureAwait(false);
            var validation = await ValidateAsync(
                sourceCandidate,
                targetItemId,
                targetKind,
                suggestedStableTarget,
                cancellationToken).ConfigureAwait(false);

            var stableReviewCandidate = BuildStableReviewCandidate(
                sourceCandidate,
                targetItemId,
                targetKind,
                suggestedStableTarget,
                sourceLearningCase?.CaseId,
                ResolveFeedbackId(sourceLearningCase),
                validation);

            await _stableReviewCandidateStore.SaveAsync(stableReviewCandidate, cancellationToken).ConfigureAwait(false);
            results.Add(stableReviewCandidate);
        }

        return results
            .OrderByDescending(static candidate => candidate.CreatedAt)
            .ToArray();
    }

    public Task<IReadOnlyList<StableReviewCandidate>> QueryAsync(
        StableReviewCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.WorkspaceId);
        return _stableReviewCandidateStore.QueryAsync(query, cancellationToken);
    }

    public Task<StableReviewCandidate?> GetAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableReviewCandidateId);
        return _stableReviewCandidateStore.GetAsync(stableReviewCandidateId, cancellationToken);
    }

    public async Task<StableReviewCandidateExplanation?> ExplainAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
    {
        var candidate = await GetAsync(stableReviewCandidateId, cancellationToken).ConfigureAwait(false);
        if (candidate is null)
        {
            return null;
        }

        var warnings = new List<string>();
        var sourceCandidate = await _promotionCandidateStore.GetAsync(candidate.SourceCandidateId, cancellationToken)
            .ConfigureAwait(false);
        if (sourceCandidate is null)
        {
            warnings.Add("source promotion candidate not found.");
        }

        var sourceLearningCase = await ResolveLearningCaseByIdOrCandidateAsync(
            candidate.SourceLearningCaseId,
            candidate.SourceCandidateId,
            candidate.WorkspaceId,
            candidate.CollectionId,
            cancellationToken).ConfigureAwait(false);
        if (sourceLearningCase is null)
        {
            warnings.Add("source learning case not found.");
        }

        var targetKind = ResolveTargetKind(candidate);
        var target = sourceCandidate is null
            ? new ResolvedTarget(null, null, targetKind, candidate.SourceTargetItemId)
            : await ResolveSourceTargetAsync(
                sourceCandidate,
                candidate.SourceTargetItemId,
                targetKind,
                cancellationToken).ConfigureAwait(false);
        if (target.Memory is null && target.Constraint is null)
        {
            warnings.Add("source target item not found.");
        }

        warnings.AddRange(candidate.RiskFlags.Select(flag => $"risk:{flag}"));

        return new StableReviewCandidateExplanation
        {
            StableReviewCandidateId = candidate.StableReviewCandidateId,
            Candidate = candidate,
            SourceCandidate = sourceCandidate ?? new ShortTermPromotionCandidate
            {
                CandidateId = candidate.SourceCandidateId,
                WorkspaceId = candidate.WorkspaceId,
                CollectionId = candidate.CollectionId,
                SessionId = candidate.SessionId
            },
            SourceLearningCase = sourceLearningCase,
            SourceMemoryTarget = target.Memory,
            SourceConstraintTarget = target.Constraint,
            EvidenceRefs = candidate.EvidenceRefs.ToArray(),
            Reason = candidate.Reason,
            ValidationStatus = candidate.ValidationStatus,
            RiskFlags = candidate.RiskFlags.ToArray(),
            Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    public Task<IReadOnlyList<StableReviewRecord>> GetReviewsAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableReviewCandidateId);
        return _stableReviewCandidateStore.QueryReviewsAsync(stableReviewCandidateId, cancellationToken);
    }

    public Task<StableReviewDecisionResult?> AcceptAsync(
        string stableReviewCandidateId,
        StableReviewDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewAsync(
            stableReviewCandidateId,
            "accept",
            StableReviewCandidateStatuses.Accepted,
            request,
            cancellationToken);
    }

    public Task<StableReviewDecisionResult?> RejectAsync(
        string stableReviewCandidateId,
        StableReviewDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewAsync(
            stableReviewCandidateId,
            "reject",
            StableReviewCandidateStatuses.Rejected,
            request,
            cancellationToken);
    }

    private async Task<StableReviewDecisionResult?> ReviewAsync(
        string stableReviewCandidateId,
        string action,
        string targetStatus,
        StableReviewDecisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableReviewCandidateId);
        ArgumentNullException.ThrowIfNull(request);

        var candidate = await _stableReviewCandidateStore.GetAsync(stableReviewCandidateId, cancellationToken)
            .ConfigureAwait(false);
        if (candidate is null)
        {
            return null;
        }

        if (string.Equals(candidate.Status, StableReviewCandidateStatuses.Accepted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.Status, StableReviewCandidateStatuses.Rejected, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Stable Review 候选项当前状态为 {candidate.Status}，不能再次执行 {action}。",
                nameof(stableReviewCandidateId));
        }

        var operationId = string.IsNullOrWhiteSpace(request.OperationId)
            ? Guid.NewGuid().ToString("N")
            : request.OperationId.Trim();
        var reviewer = string.IsNullOrWhiteSpace(request.Reviewer) ? "manual" : request.Reviewer.Trim();
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? action : request.Reason.Trim();
        var now = DateTimeOffset.UtcNow;
        var warnings = new List<string>();
        StableTarget? stableTarget = null;
        var validation = await ValidateExistingCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);

        if (string.Equals(action, "accept", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(validation.ValidationStatus, StableReviewValidationStatuses.ReadyForReview, StringComparison.OrdinalIgnoreCase))
            {
                var details = validation.RiskFlags.Count == 0 ? validation.ValidationStatus : string.Join(", ", validation.RiskFlags);
                throw new ArgumentException(
                    $"Stable Review 候选项未通过 accept validation：{validation.ValidationStatus} ({details})。",
                    nameof(stableReviewCandidateId));
            }

            stableTarget = await CreateStableTargetAsync(
                candidate,
                validation.SourceCandidate ?? throw new ArgumentException("source promotion candidate missing.", nameof(stableReviewCandidateId)),
                validation.Target,
                reviewer,
                reason,
                now,
                cancellationToken).ConfigureAwait(false);
        }

        var updatedCandidate = CloneCandidate(
            candidate,
            targetStatus,
            validation.ValidationStatus,
            validation.RiskFlags,
            reviewer,
            reason,
            stableTarget,
            now);
        await _stableReviewCandidateStore.SaveAsync(updatedCandidate, cancellationToken).ConfigureAwait(false);

        var review = BuildReviewRecord(
            candidate,
            updatedCandidate,
            action,
            operationId,
            reviewer,
            reason,
            validation,
            stableTarget,
            warnings,
            now,
            request.Metadata);
        await _stableReviewCandidateStore.AppendReviewAsync(review, cancellationToken).ConfigureAwait(false);

        return new StableReviewDecisionResult
        {
            OperationId = operationId,
            StableReviewCandidateId = candidate.StableReviewCandidateId,
            Action = action,
            Status = updatedCandidate.Status,
            ReviewId = review.ReviewId,
            Reviewer = reviewer,
            Reason = reason,
            ReviewedAt = now,
            CreatedStableTargetItemId = stableTarget?.TargetItemId,
            CreatedTargetItemId = stableTarget?.TargetItemId,
            StableTargetItemKind = stableTarget?.TargetItemKind,
            TargetLayer = stableTarget?.TargetLayer,
            ValidationStatus = validation.ValidationStatus,
            Candidate = updatedCandidate,
            Review = review,
            Warnings = warnings.ToArray(),
            Errors = Array.Empty<string>()
        };
    }

    private async Task<ValidationResult> ValidateAsync(
        ShortTermPromotionCandidate sourceCandidate,
        string targetItemId,
        string targetKind,
        string suggestedStableTarget,
        CancellationToken cancellationToken)
    {
        var riskFlags = new List<string>();
        if (sourceCandidate.Status != PromotionCandidateStatus.Accepted)
        {
            riskFlags.Add("source_candidate_not_accepted");
        }

        if (sourceCandidate.EvidenceRefs.Count == 0)
        {
            riskFlags.Add("missing_evidence");
        }

        var target = await ResolveSourceTargetAsync(sourceCandidate, targetItemId, targetKind, cancellationToken)
            .ConfigureAwait(false);
        if (target.Memory is null && target.Constraint is null)
        {
            riskFlags.Add("source_target_missing");
        }
        else if (!IsSameScope(sourceCandidate, target))
        {
            riskFlags.Add("scope_mismatch");
        }
        else if (!IsTargetCandidate(target))
        {
            riskFlags.Add("target_not_candidate");
        }

        if (await HasDuplicateStableAsync(
                sourceCandidate,
                targetItemId,
                targetKind,
                suggestedStableTarget,
                cancellationToken).ConfigureAwait(false))
        {
            riskFlags.Add("duplicate_stable");
        }

        var validationStatus = ResolveValidationStatus(riskFlags);
        var status = validationStatus switch
        {
            StableReviewValidationStatuses.Ready => StableReviewCandidateStatuses.Candidate,
            StableReviewValidationStatuses.NeedsMoreEvidence => StableReviewCandidateStatuses.NeedsMoreEvidence,
            _ => StableReviewCandidateStatuses.Blocked
        };

        return new ValidationResult(validationStatus, status, riskFlags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private async Task<StableReviewValidationContext> ValidateExistingCandidateAsync(
        StableReviewCandidate candidate,
        CancellationToken cancellationToken)
    {
        var riskFlags = new List<string>();
        if (candidate.EvidenceRefs.Count == 0)
        {
            riskFlags.Add("missing_evidence");
        }

        var sourceCandidate = await _promotionCandidateStore.GetAsync(candidate.SourceCandidateId, cancellationToken)
            .ConfigureAwait(false);
        if (sourceCandidate is null)
        {
            riskFlags.Add("source_candidate_missing");
        }
        else if (sourceCandidate.Status != PromotionCandidateStatus.Accepted)
        {
            riskFlags.Add("source_candidate_not_accepted");
        }

        var targetKind = ResolveTargetKind(candidate);
        var target = sourceCandidate is null
            ? new ResolvedTarget(null, null, targetKind, candidate.SourceTargetItemId)
            : await ResolveSourceTargetAsync(
                sourceCandidate,
                candidate.SourceTargetItemId,
                targetKind,
                cancellationToken).ConfigureAwait(false);
        if (target.Memory is null && target.Constraint is null)
        {
            riskFlags.Add("source_target_missing");
        }
        else if (sourceCandidate is not null && !IsSameScope(sourceCandidate, target))
        {
            riskFlags.Add("scope_mismatch");
        }
        else if (!IsTargetCandidate(target))
        {
            riskFlags.Add("target_not_candidate");
        }

        if (sourceCandidate is not null
            && await HasDuplicateStableAsync(
                sourceCandidate,
                candidate.SourceTargetItemId,
                targetKind,
                candidate.SuggestedStableTarget,
                cancellationToken).ConfigureAwait(false))
        {
            riskFlags.Add("duplicate_stable");
        }

        var validationStatus = ResolveValidationStatus(riskFlags);
        var status = validationStatus switch
        {
            StableReviewValidationStatuses.Ready => StableReviewCandidateStatuses.Candidate,
            StableReviewValidationStatuses.NeedsMoreEvidence => StableReviewCandidateStatuses.NeedsMoreEvidence,
            _ => StableReviewCandidateStatuses.Blocked
        };

        return new StableReviewValidationContext(
            validationStatus,
            status,
            riskFlags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            sourceCandidate,
            target);
    }

    private async Task<StableTarget> CreateStableTargetAsync(
        StableReviewCandidate candidate,
        ShortTermPromotionCandidate sourceCandidate,
        ResolvedTarget target,
        string reviewer,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (IsStableConstraintTarget(candidate, target))
        {
            if (_constraintStore is null)
            {
                throw new InvalidOperationException("当前 provider 未注册约束存储，不能接受 StableConstraint。");
            }

            if (target.Constraint is null)
            {
                throw new ArgumentException("Stable Review source constraint target missing.", nameof(candidate));
            }

            var stableConstraint = BuildStableConstraint(candidate, sourceCandidate, target.Constraint, reviewer, reason, now);
            await _constraintStore.SaveAsync(stableConstraint, cancellationToken).ConfigureAwait(false);
            return new StableTarget(stableConstraint.Id, "constraint", "StableConstraint");
        }

        if (_memoryStore is null)
        {
            throw new InvalidOperationException("当前 provider 未注册记忆存储，不能接受 Stable Review 候选项。");
        }

        if (target.Memory is null)
        {
            throw new ArgumentException("Stable Review source memory target missing.", nameof(candidate));
        }

        var stableMemory = BuildStableMemory(candidate, sourceCandidate, target.Memory, reviewer, reason, now);
        await _memoryStore.SaveAsync(stableMemory, cancellationToken).ConfigureAwait(false);
        return new StableTarget(
            stableMemory.Id,
            "memory",
            string.Equals(candidate.SuggestedStableTarget, "DecisionRecord", StringComparison.OrdinalIgnoreCase)
                ? "DecisionRecord"
                : "StableMemory");
    }

    private static ContextMemoryItem BuildStableMemory(
        StableReviewCandidate candidate,
        ShortTermPromotionCandidate sourceCandidate,
        ContextMemoryItem sourceTarget,
        string reviewer,
        string reason,
        DateTimeOffset now)
    {
        var isDecisionRecord = string.Equals(candidate.SuggestedStableTarget, "DecisionRecord", StringComparison.OrdinalIgnoreCase);
        return new ContextMemoryItem
        {
            Id = BuildStableTargetItemId(candidate),
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Type = isDecisionRecord ? "decision" : sourceTarget.Type,
            Content = sourceTarget.Content,
            ContentFormat = sourceTarget.ContentFormat,
            Tags = sourceTarget.Tags
                .Append(isDecisionRecord ? "decision-record" : "stable-memory")
                .Append("stable-review")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            SourceRefs = BuildStableSourceRefs(candidate, sourceCandidate, sourceTarget.SourceRefs),
            RelationRefs = sourceTarget.RelationRefs.ToArray(),
            Importance = candidate.Importance,
            Confidence = candidate.Confidence,
            Version = sourceTarget.Version <= 0 ? 1 : sourceTarget.Version + 1,
            Metadata = BuildStableTargetMetadata(candidate, sourceCandidate, reviewer, reason, isDecisionRecord ? "memory:decision" : "memory"),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static ContextConstraint BuildStableConstraint(
        StableReviewCandidate candidate,
        ShortTermPromotionCandidate sourceCandidate,
        ContextConstraint sourceTarget,
        string reviewer,
        string reason,
        DateTimeOffset now)
    {
        return new ContextConstraint
        {
            Id = BuildStableTargetItemId(candidate),
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            Scope = sourceTarget.Scope,
            Level = sourceTarget.Level,
            Content = sourceTarget.Content,
            AppliesToRefs = sourceTarget.AppliesToRefs.ToArray(),
            SourceRefs = BuildStableSourceRefs(candidate, sourceCandidate, sourceTarget.SourceRefs),
            Status = ContextMemoryStatus.Stable,
            Confidence = candidate.Confidence,
            Metadata = BuildStableTargetMetadata(candidate, sourceCandidate, reviewer, reason, "constraint"),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static IReadOnlyList<string> BuildStableSourceRefs(
        StableReviewCandidate candidate,
        ShortTermPromotionCandidate sourceCandidate,
        IReadOnlyList<string> sourceTargetRefs)
    {
        return sourceTargetRefs
            .Concat(candidate.EvidenceRefs)
            .Append(candidate.StableReviewCandidateId)
            .Append(candidate.SourceCandidateId)
            .Append(candidate.SourceTargetItemId)
            .Append(sourceCandidate.SourceWorkingItemId)
            .Append(candidate.SourceLearningCaseId ?? string.Empty)
            .Append(ResolveSourceFeedbackId(candidate, sourceCandidate))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Dictionary<string, string> BuildStableTargetMetadata(
        StableReviewCandidate candidate,
        ShortTermPromotionCandidate sourceCandidate,
        string reviewer,
        string reason,
        string targetKind)
    {
        var metadata = new Dictionary<string, string>(candidate.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["sourceStableReviewCandidateId"] = candidate.StableReviewCandidateId,
            ["sourcePromotionCandidateId"] = candidate.SourceCandidateId,
            ["sourceCandidateId"] = candidate.SourceCandidateId,
            ["stableReviewSourceCandidateId"] = candidate.SourceCandidateId,
            ["sourceLearningCaseId"] = candidate.SourceLearningCaseId ?? string.Empty,
            ["sourceWorkingItemId"] = sourceCandidate.SourceWorkingItemId,
            ["sourceFeedbackId"] = ResolveSourceFeedbackId(candidate, sourceCandidate),
            ["sourceTargetItemId"] = candidate.SourceTargetItemId,
            ["stableReviewSourceTargetItemId"] = candidate.SourceTargetItemId,
            ["evidenceRefs"] = string.Join(",", candidate.EvidenceRefs),
            ["reviewer"] = reviewer,
            ["reviewReason"] = reason,
            ["policyVersion"] = candidate.Metadata.GetValueOrDefault("policyVersion") ?? PolicyVersion,
            ["createdFrom"] = "stable_review_accept",
            ["stableReviewTargetKind"] = targetKind,
            ["stableReviewFlow"] = "stable-review-accept/v1"
        };
        return metadata;
    }

    private static StableReviewRecord BuildReviewRecord(
        StableReviewCandidate original,
        StableReviewCandidate updated,
        string action,
        string operationId,
        string reviewer,
        string reason,
        StableReviewValidationContext validation,
        StableTarget? target,
        IReadOnlyList<string> warnings,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string> requestMetadata)
    {
        var metadata = new Dictionary<string, string>(requestMetadata, StringComparer.OrdinalIgnoreCase)
        {
            ["operationId"] = operationId,
            ["sourceStableReviewCandidateId"] = original.StableReviewCandidateId,
            ["sourcePromotionCandidateId"] = original.SourceCandidateId,
            ["sourceFeedbackId"] = ResolveSourceFeedbackId(original, validation.SourceCandidate),
            ["sourceTargetItemId"] = original.SourceTargetItemId,
            ["reviewedAt"] = now.ToString("O")
        };
        if (!string.IsNullOrWhiteSpace(original.SourceLearningCaseId))
        {
            metadata["sourceLearningCaseId"] = original.SourceLearningCaseId;
        }

        if (target is not null)
        {
            metadata["createdStableTargetItemId"] = target.TargetItemId;
            metadata["createdStableTargetItemKind"] = target.TargetItemKind;
            metadata["targetLayer"] = target.TargetLayer;
        }

        return new StableReviewRecord
        {
            ReviewId = $"strr-{BuildShortHash($"{original.StableReviewCandidateId}\u001f{action}\u001f{now:O}")}",
            StableReviewCandidateId = original.StableReviewCandidateId,
            WorkspaceId = original.WorkspaceId,
            CollectionId = original.CollectionId,
            SessionId = original.SessionId,
            Action = action,
            FromStatus = original.Status,
            ToStatus = updated.Status,
            Reviewer = reviewer,
            Reason = reason,
            StableTargetItemId = target?.TargetItemId,
            StableTargetItemKind = target?.TargetItemKind,
            TargetLayer = target?.TargetLayer,
            SourcePromotionCandidateId = original.SourceCandidateId,
            SourceTargetItemId = original.SourceTargetItemId,
            SourceLearningCaseId = original.SourceLearningCaseId,
            EvidenceRefs = original.EvidenceRefs.ToArray(),
            ValidationStatus = validation.ValidationStatus,
            RiskFlags = validation.RiskFlags.ToArray(),
            CreatedAt = now,
            ReviewedAt = now,
            Metadata = metadata,
            Warnings = warnings.ToArray(),
            Errors = Array.Empty<string>()
        };
    }

    private static StableReviewCandidate CloneCandidate(
        StableReviewCandidate candidate,
        string status,
        string validationStatus,
        IReadOnlyList<string> riskFlags,
        string reviewer,
        string reason,
        StableTarget? stableTarget,
        DateTimeOffset reviewedAt)
    {
        var metadata = new Dictionary<string, string>(candidate.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["lastReviewer"] = reviewer,
            ["lastReviewReason"] = reason,
            ["lastReviewedAt"] = reviewedAt.ToString("O"),
            ["validationStatus"] = validationStatus,
            ["riskFlags"] = string.Join(",", riskFlags)
        };
        if (stableTarget is not null)
        {
            metadata["createdStableTargetItemId"] = stableTarget.TargetItemId;
            metadata["createdStableTargetItemKind"] = stableTarget.TargetItemKind;
            metadata["stableTargetLayer"] = stableTarget.TargetLayer;
        }

        return new StableReviewCandidate
        {
            StableReviewCandidateId = candidate.StableReviewCandidateId,
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            SessionId = candidate.SessionId,
            SourceCandidateId = candidate.SourceCandidateId,
            SourceTargetItemId = candidate.SourceTargetItemId,
            SourceLearningCaseId = candidate.SourceLearningCaseId,
            Kind = candidate.Kind,
            Title = candidate.Title,
            Summary = candidate.Summary,
            SuggestedStableTarget = candidate.SuggestedStableTarget,
            Reason = candidate.Reason,
            Confidence = candidate.Confidence,
            Importance = candidate.Importance,
            EvidenceRefs = candidate.EvidenceRefs.ToArray(),
            RiskFlags = riskFlags.ToArray(),
            ValidationStatus = validationStatus,
            CreatedAt = candidate.CreatedAt,
            Status = status,
            Metadata = metadata
        };
    }

    private async Task<ResolvedTarget> ResolveSourceTargetAsync(
        ShortTermPromotionCandidate sourceCandidate,
        string targetItemId,
        string targetKind,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetItemId))
        {
            return new ResolvedTarget(null, null, targetKind, targetItemId);
        }

        if (string.Equals(targetKind, "constraint", StringComparison.OrdinalIgnoreCase))
        {
            if (_constraintStore is null)
            {
                return new ResolvedTarget(null, null, targetKind, targetItemId);
            }

            var constraints = await _constraintStore.QueryAsync(new ContextConstraintQuery
            {
                WorkspaceId = sourceCandidate.WorkspaceId,
                CollectionId = null,
                Take = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            var constraint = constraints.FirstOrDefault(item => string.Equals(item.Id, targetItemId, StringComparison.OrdinalIgnoreCase));
            return new ResolvedTarget(null, constraint, targetKind, targetItemId);
        }

        if (_memoryStore is null)
        {
            return new ResolvedTarget(null, null, targetKind, targetItemId);
        }

        var memory = await _memoryStore.GetAsync(
            sourceCandidate.WorkspaceId,
            sourceCandidate.CollectionId,
            targetItemId,
            cancellationToken).ConfigureAwait(false);
        if (memory is not null)
        {
            return new ResolvedTarget(memory, null, targetKind, targetItemId);
        }

        var allWorkspaceMemory = await _memoryStore.QueryAsync(new ContextMemoryQuery
        {
            WorkspaceId = sourceCandidate.WorkspaceId,
            CollectionId = null,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);
        memory = allWorkspaceMemory.FirstOrDefault(item => string.Equals(item.Id, targetItemId, StringComparison.OrdinalIgnoreCase));
        return new ResolvedTarget(memory, null, targetKind, targetItemId);
    }

    private async Task<bool> HasDuplicateStableAsync(
        ShortTermPromotionCandidate sourceCandidate,
        string targetItemId,
        string targetKind,
        string suggestedStableTarget,
        CancellationToken cancellationToken)
    {
        if (string.Equals(targetKind, "constraint", StringComparison.OrdinalIgnoreCase)
            || string.Equals(suggestedStableTarget, "StableConstraint", StringComparison.OrdinalIgnoreCase))
        {
            if (_constraintStore is null)
            {
                return false;
            }

            var stableConstraints = await _constraintStore.QueryAsync(new ContextConstraintQuery
            {
                WorkspaceId = sourceCandidate.WorkspaceId,
                CollectionId = sourceCandidate.CollectionId,
                Status = ContextMemoryStatus.Stable,
                Take = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            return stableConstraints.Any(item => ReferencesSource(item.SourceRefs, item.Metadata, sourceCandidate.CandidateId, targetItemId));
        }

        if (_memoryStore is null)
        {
            return false;
        }

        var stableMemory = await _memoryStore.QueryAsync(new ContextMemoryQuery
        {
            WorkspaceId = sourceCandidate.WorkspaceId,
            CollectionId = sourceCandidate.CollectionId,
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);
        return stableMemory.Any(item => ReferencesSource(item.SourceRefs, item.Metadata, sourceCandidate.CandidateId, targetItemId));
    }

    private async Task<ContextLearningCase?> ResolveSourceLearningCaseAsync(
        ShortTermPromotionCandidate sourceCandidate,
        CancellationToken cancellationToken)
    {
        if (_learningStore is null)
        {
            return null;
        }

        var cases = await _learningStore.QueryCasesAsync(new ContextLearningCaseQuery
        {
            WorkspaceId = sourceCandidate.WorkspaceId,
            CollectionId = sourceCandidate.CollectionId,
            SessionId = sourceCandidate.SessionId,
            Signal = ContextFeedbackSignal.Positive,
            Status = ContextLearningCaseStatus.Draft,
            Limit = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);

        return cases.FirstOrDefault(item => ReferencesSource(item.EvidenceRefs, item.Metadata, sourceCandidate.CandidateId, sourceCandidate.Metadata.GetValueOrDefault("acceptedTargetItemId") ?? string.Empty))
            ?? cases.FirstOrDefault(item => string.Equals(item.SourceId, sourceCandidate.Metadata.GetValueOrDefault("feedbackId"), StringComparison.OrdinalIgnoreCase))
            ?? cases.FirstOrDefault(item => item.Metadata.TryGetValue("sourceCandidateId", out var value)
                && string.Equals(value, sourceCandidate.CandidateId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ContextLearningCase?> ResolveLearningCaseByIdOrCandidateAsync(
        string? sourceLearningCaseId,
        string sourceCandidateId,
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        if (_learningStore is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(sourceLearningCaseId))
        {
            var learningCase = await _learningStore.GetCaseAsync(sourceLearningCaseId, cancellationToken).ConfigureAwait(false);
            if (learningCase is not null)
            {
                return learningCase;
            }
        }

        var cases = await _learningStore.QueryCasesAsync(new ContextLearningCaseQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Signal = ContextFeedbackSignal.Positive,
            Limit = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);
        return cases.FirstOrDefault(item => item.Metadata.TryGetValue("sourceCandidateId", out var value)
            && string.Equals(value, sourceCandidateId, StringComparison.OrdinalIgnoreCase));
    }

    private static StableReviewCandidate BuildStableReviewCandidate(
        ShortTermPromotionCandidate sourceCandidate,
        string targetItemId,
        string targetKind,
        string suggestedStableTarget,
        string? sourceLearningCaseId,
        string? sourceFeedbackId,
        ValidationResult validation)
    {
        var now = DateTimeOffset.UtcNow;
        var metadata = new Dictionary<string, string>(sourceCandidate.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["sourceCandidateId"] = sourceCandidate.CandidateId,
            ["sourceWorkingItemId"] = sourceCandidate.SourceWorkingItemId,
            ["sourceTargetItemId"] = targetItemId,
            ["sourceTargetItemKind"] = targetKind,
            ["suggestedStableTarget"] = suggestedStableTarget,
            ["validationStatus"] = validation.ValidationStatus,
            ["generatedBy"] = GeneratedBy,
            ["policyVersion"] = PolicyVersion,
            ["stableReviewFlow"] = "stable-review-readiness/v1"
        };
        if (!string.IsNullOrWhiteSpace(sourceLearningCaseId))
        {
            metadata["sourceLearningCaseId"] = sourceLearningCaseId;
        }
        if (!string.IsNullOrWhiteSpace(sourceFeedbackId))
        {
            metadata["sourceFeedbackId"] = sourceFeedbackId;
        }

        return new StableReviewCandidate
        {
            StableReviewCandidateId = BuildStableReviewCandidateId(sourceCandidate, targetItemId, suggestedStableTarget),
            WorkspaceId = sourceCandidate.WorkspaceId,
            CollectionId = sourceCandidate.CollectionId,
            SessionId = sourceCandidate.SessionId,
            SourceCandidateId = sourceCandidate.CandidateId,
            SourceTargetItemId = targetItemId,
            SourceLearningCaseId = sourceLearningCaseId,
            Kind = sourceCandidate.Kind,
            Title = sourceCandidate.Title,
            Summary = sourceCandidate.Summary,
            SuggestedStableTarget = suggestedStableTarget,
            Reason = BuildReason(sourceCandidate, suggestedStableTarget, validation.ValidationStatus),
            Confidence = sourceCandidate.Confidence,
            Importance = sourceCandidate.Importance,
            EvidenceRefs = sourceCandidate.EvidenceRefs.ToArray(),
            RiskFlags = validation.RiskFlags,
            ValidationStatus = validation.ValidationStatus,
            CreatedAt = now,
            Status = validation.Status,
            Metadata = metadata
        };
    }

    private static string ResolveSuggestedStableTarget(ShortTermPromotionCandidate sourceCandidate)
    {
        if (IsOpenOrKnownIssue(sourceCandidate) && !AllowsExplicitStableReview(sourceCandidate.Metadata))
        {
            return string.Empty;
        }

        if (sourceCandidate.Metadata.TryGetValue("suggestedStableTarget", out var explicitTarget)
            && !string.IsNullOrWhiteSpace(explicitTarget))
        {
            return explicitTarget.Trim();
        }

        if (string.Equals(sourceCandidate.SuggestedTargetLayer, "ConstraintCandidate", StringComparison.OrdinalIgnoreCase))
        {
            return "StableConstraint";
        }

        if (string.Equals(sourceCandidate.SuggestedTargetLayer, "DecisionRecord", StringComparison.OrdinalIgnoreCase))
        {
            return "DecisionRecord";
        }

        if (string.Equals(sourceCandidate.SuggestedTargetLayer, "CandidateMemory", StringComparison.OrdinalIgnoreCase))
        {
            return "StableMemory";
        }

        return string.Empty;
    }

    private static string? ResolveFeedbackId(ContextLearningCase? sourceLearningCase)
    {
        if (sourceLearningCase is null)
        {
            return null;
        }

        if (sourceLearningCase.Metadata.TryGetValue("feedbackId", out var feedbackId)
            && !string.IsNullOrWhiteSpace(feedbackId))
        {
            return feedbackId;
        }

        return string.Equals(sourceLearningCase.SourceType, "PromotionFeedbackSignal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sourceLearningCase.SourceKind, "PromotionFeedbackSignal", StringComparison.OrdinalIgnoreCase)
                ? sourceLearningCase.SourceId
                : null;
    }

    private static bool IsOpenOrKnownIssue(ShortTermPromotionCandidate sourceCandidate)
    {
        return string.Equals(sourceCandidate.Kind, "KnownIssue", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sourceCandidate.Kind, "OpenIssue", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sourceCandidate.Kind, "OpenQuestion", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sourceCandidate.SuggestedTargetLayer, "OpenIssue", StringComparison.OrdinalIgnoreCase);
    }

    private static bool AllowsExplicitStableReview(IReadOnlyDictionary<string, string> metadata)
    {
        return IsTruthy(metadata.GetValueOrDefault("stableReviewAllowed"))
            || IsTruthy(metadata.GetValueOrDefault("allowStableReview"))
            || IsTruthy(metadata.GetValueOrDefault("generateStableReview"))
            || IsTruthy(metadata.GetValueOrDefault("stableReview"));
    }

    private static bool IsTruthy(string? value)
    {
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveAcceptedTargetKind(
        ShortTermPromotionCandidate sourceCandidate,
        string suggestedStableTarget)
    {
        if (sourceCandidate.Metadata.TryGetValue("acceptedTargetItemKind", out var targetKind)
            && !string.IsNullOrWhiteSpace(targetKind))
        {
            return targetKind.Trim();
        }

        return string.Equals(suggestedStableTarget, "StableConstraint", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sourceCandidate.SuggestedTargetLayer, "ConstraintCandidate", StringComparison.OrdinalIgnoreCase)
                ? "constraint"
                : "memory";
    }

    private static string ResolveAcceptedTargetItemId(
        ShortTermPromotionCandidate sourceCandidate,
        string targetKind)
    {
        if (sourceCandidate.Metadata.TryGetValue("acceptedTargetItemId", out var targetItemId)
            && !string.IsNullOrWhiteSpace(targetItemId))
        {
            return targetItemId.Trim();
        }

        return string.Equals(targetKind, "constraint", StringComparison.OrdinalIgnoreCase)
            ? $"constraint:stp:{sourceCandidate.CandidateId}"
            : $"mem:stp:{sourceCandidate.CandidateId}";
    }

    private static string ResolveTargetKind(StableReviewCandidate candidate)
    {
        if (candidate.Metadata.TryGetValue("sourceTargetItemKind", out var targetKind)
            && !string.IsNullOrWhiteSpace(targetKind))
        {
            return targetKind;
        }

        return string.Equals(candidate.SuggestedStableTarget, "StableConstraint", StringComparison.OrdinalIgnoreCase)
            ? "constraint"
            : "memory";
    }

    private static bool IsSameScope(ShortTermPromotionCandidate sourceCandidate, ResolvedTarget target)
    {
        if (target.Memory is not null)
        {
            return string.Equals(target.Memory.WorkspaceId, sourceCandidate.WorkspaceId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(target.Memory.CollectionId, sourceCandidate.CollectionId, StringComparison.OrdinalIgnoreCase);
        }

        if (target.Constraint is not null)
        {
            return string.Equals(target.Constraint.WorkspaceId, sourceCandidate.WorkspaceId, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(target.Constraint.CollectionId)
                    || string.Equals(target.Constraint.CollectionId, sourceCandidate.CollectionId, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static bool IsTargetCandidate(ResolvedTarget target)
    {
        if (target.Memory is not null)
        {
            return target.Memory.Status == ContextMemoryStatus.Candidate
                && target.Memory.Layer != ContextMemoryLayer.Stable;
        }

        if (target.Constraint is not null)
        {
            return target.Constraint.Status == ContextMemoryStatus.Candidate;
        }

        return false;
    }

    private static bool IsStableConstraintTarget(StableReviewCandidate candidate, ResolvedTarget target)
    {
        return string.Equals(target.TargetKind, "constraint", StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.SuggestedStableTarget, "StableConstraint", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ReferencesSource(
        IReadOnlyList<string> sourceRefs,
        IReadOnlyDictionary<string, string> metadata,
        string sourceCandidateId,
        string sourceTargetItemId)
    {
        if (sourceRefs.Contains(sourceCandidateId, StringComparer.OrdinalIgnoreCase)
            || sourceRefs.Contains(sourceTargetItemId, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return MatchesMetadata(metadata, "sourceCandidateId", sourceCandidateId)
            || MatchesMetadata(metadata, "stableReviewSourceCandidateId", sourceCandidateId)
            || MatchesMetadata(metadata, "sourceTargetItemId", sourceTargetItemId)
            || MatchesMetadata(metadata, "stableReviewSourceTargetItemId", sourceTargetItemId)
            || MatchesMetadata(metadata, "acceptedTargetItemId", sourceTargetItemId);
    }

    private static bool MatchesMetadata(
        IReadOnlyDictionary<string, string> metadata,
        string key,
        string expected)
    {
        return !string.IsNullOrWhiteSpace(expected)
            && metadata.TryGetValue(key, out var value)
            && string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveSourceFeedbackId(
        StableReviewCandidate candidate,
        ShortTermPromotionCandidate? sourceCandidate)
    {
        return ReadMetadata(candidate.Metadata, "sourceFeedbackId", "feedbackId")
            ?? (sourceCandidate is null
                ? string.Empty
                : ReadMetadata(sourceCandidate.Metadata, "sourceFeedbackId", "feedbackId"))
            ?? string.Empty;
    }

    private static string? ReadMetadata(
        IReadOnlyDictionary<string, string> metadata,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value)
                && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string ResolveValidationStatus(IReadOnlyList<string> riskFlags)
    {
        if (riskFlags.Contains("source_candidate_missing", StringComparer.OrdinalIgnoreCase))
        {
            return StableReviewValidationStatuses.SourceCandidateMissing;
        }

        if (riskFlags.Contains("scope_mismatch", StringComparer.OrdinalIgnoreCase))
        {
            return StableReviewValidationStatuses.ScopeMismatch;
        }

        if (riskFlags.Contains("source_target_missing", StringComparer.OrdinalIgnoreCase))
        {
            return StableReviewValidationStatuses.SourceTargetMissing;
        }

        if (riskFlags.Contains("source_candidate_not_accepted", StringComparer.OrdinalIgnoreCase))
        {
            return StableReviewValidationStatuses.LifecycleConflict;
        }

        if (riskFlags.Contains("target_not_candidate", StringComparer.OrdinalIgnoreCase))
        {
            return StableReviewValidationStatuses.TargetNotCandidate;
        }

        if (riskFlags.Contains("duplicate_stable", StringComparer.OrdinalIgnoreCase))
        {
            return StableReviewValidationStatuses.DuplicateStableCandidate;
        }

        if (riskFlags.Contains("missing_evidence", StringComparer.OrdinalIgnoreCase))
        {
            return StableReviewValidationStatuses.NeedsMoreEvidence;
        }

        return StableReviewValidationStatuses.ReadyForReview;
    }

    private static string BuildReason(
        ShortTermPromotionCandidate sourceCandidate,
        string suggestedStableTarget,
        string validationStatus)
    {
        return validationStatus == StableReviewValidationStatuses.Ready
            ? $"{sourceCandidate.SuggestedTargetLayer} accepted candidate is ready for {suggestedStableTarget} review."
            : $"{sourceCandidate.SuggestedTargetLayer} accepted candidate requires stable review attention: {validationStatus}.";
    }

    private static string BuildStableReviewCandidateId(
        ShortTermPromotionCandidate sourceCandidate,
        string targetItemId,
        string suggestedStableTarget)
    {
        return $"src-{BuildShortHash(string.Join('\u001f',
            sourceCandidate.WorkspaceId,
            sourceCandidate.CollectionId,
            sourceCandidate.CandidateId,
            targetItemId,
            suggestedStableTarget))}";
    }

    private static string BuildStableTargetItemId(StableReviewCandidate candidate)
    {
        if (string.Equals(candidate.SuggestedStableTarget, "StableConstraint", StringComparison.OrdinalIgnoreCase))
        {
            return $"stable:constraint:{candidate.StableReviewCandidateId}";
        }

        if (string.Equals(candidate.SuggestedStableTarget, "DecisionRecord", StringComparison.OrdinalIgnoreCase))
        {
            return $"stable:decision:{candidate.StableReviewCandidateId}";
        }

        return $"stable:mem:{candidate.StableReviewCandidateId}";
    }

    private static string BuildShortHash(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..20].ToLowerInvariant();
    }

    private sealed record ValidationResult(
        string ValidationStatus,
        string Status,
        IReadOnlyList<string> RiskFlags);

    private sealed record StableReviewValidationContext(
        string ValidationStatus,
        string Status,
        IReadOnlyList<string> RiskFlags,
        ShortTermPromotionCandidate? SourceCandidate,
        ResolvedTarget Target);

    private sealed record StableTarget(
        string TargetItemId,
        string TargetItemKind,
        string TargetLayer);

    private sealed record ResolvedTarget(
        ContextMemoryItem? Memory,
        ContextConstraint? Constraint,
        string TargetKind,
        string TargetItemId);
}
