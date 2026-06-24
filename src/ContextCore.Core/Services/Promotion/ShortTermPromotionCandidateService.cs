using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Promotion;

/// <summary>根据短期工作记忆生成只读晋升候选项，不直接写入稳定记忆。</summary>
public sealed class ShortTermPromotionCandidateService
{
    private const double MinimumImportance = 0.70;
    private const string GeneratedBy = "short-term-promotion-candidate-service/rule-based";
    private const string PolicyVersion = "short-term-promotion-candidate-policy/v1";
    private const string RuleVersion = "short-term-promotion-rules/v1";
    private readonly IShortTermMemoryStore _shortTermMemoryStore;
    private readonly IShortTermPromotionCandidateStore _candidateStore;
    private readonly IMemoryStore? _memoryStore;
    private readonly IConstraintStore? _constraintStore;
    private readonly IRelationStore? _relationStore;
    private readonly IContextLearningStore? _learningStore;
    private readonly IContextLearningCaseGenerator? _learningCaseGenerator;

    public ShortTermPromotionCandidateService(
        IShortTermMemoryStore shortTermMemoryStore,
        IShortTermPromotionCandidateStore candidateStore)
        : this(shortTermMemoryStore, candidateStore, null, null, null, null)
    {
    }

    public ShortTermPromotionCandidateService(
        IShortTermMemoryStore shortTermMemoryStore,
        IShortTermPromotionCandidateStore candidateStore,
        IMemoryStore? memoryStore,
        IConstraintStore? constraintStore,
        IRelationStore? relationStore)
        : this(shortTermMemoryStore, candidateStore, memoryStore, constraintStore, relationStore, null)
    {
    }

    public ShortTermPromotionCandidateService(
        IShortTermMemoryStore shortTermMemoryStore,
        IShortTermPromotionCandidateStore candidateStore,
        IMemoryStore? memoryStore,
        IConstraintStore? constraintStore,
        IRelationStore? relationStore,
        IContextLearningStore? learningStore)
        : this(shortTermMemoryStore, candidateStore, memoryStore, constraintStore, relationStore, learningStore, null)
    {
    }

    public ShortTermPromotionCandidateService(
        IShortTermMemoryStore shortTermMemoryStore,
        IShortTermPromotionCandidateStore candidateStore,
        IMemoryStore? memoryStore,
        IConstraintStore? constraintStore,
        IRelationStore? relationStore,
        IContextLearningStore? learningStore,
        IContextLearningCaseGenerator? learningCaseGenerator)
    {
        _shortTermMemoryStore = shortTermMemoryStore;
        _candidateStore = candidateStore;
        _memoryStore = memoryStore;
        _constraintStore = constraintStore;
        _relationStore = relationStore;
        _learningStore = learningStore;
        _learningCaseGenerator = learningCaseGenerator;
    }

    public async Task<IReadOnlyList<ShortTermPromotionCandidate>> GenerateAsync(
        ShortTermPromotionCandidateGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);

        var workingItems = await _shortTermMemoryStore.QueryWorkingItemsAsync(new ShortTermWorkingItemQuery
        {
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            SessionId = request.SessionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);

        var results = new List<ShortTermPromotionCandidate>();
        foreach (var item in workingItems.OrderByDescending(static item => item.UpdatedAt))
        {
            var candidate = CreateCandidate(item);
            if (candidate is null)
            {
                continue;
            }

            var existing = await _candidateStore.GetAsync(candidate.CandidateId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                results.Add(existing);
                continue;
            }

            await _candidateStore.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
            results.Add(candidate);
        }

        return results
            .OrderByDescending(static item => item.CreatedAt)
            .ToArray();
    }

    public Task<IReadOnlyList<ShortTermPromotionCandidate>> QueryAsync(
        ShortTermPromotionCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return _candidateStore.QueryAsync(query, cancellationToken);
    }

    public Task<ShortTermPromotionCandidate?> GetAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        return _candidateStore.GetAsync(candidateId, cancellationToken);
    }

    public async Task<ShortTermPromotionCandidateExplanation?> ExplainAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        var candidate = await GetAsync(candidateId, cancellationToken).ConfigureAwait(false);
        if (candidate is null)
        {
            return null;
        }

        var sourceWorkingItem = await _shortTermMemoryStore.GetWorkingItemAsync(
            candidate.WorkspaceId,
            candidate.CollectionId,
            candidate.SourceWorkingItemId,
            cancellationToken).ConfigureAwait(false);
        if (sourceWorkingItem is null)
        {
            return null;
        }

        var rawEvents = await _shortTermMemoryStore.QueryRawEventsAsync(new ShortTermRawEventQuery
        {
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            SessionId = candidate.SessionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);
        var sourceRawEvents = rawEvents
            .Where(item => MatchesEvidence(item, candidate.EvidenceRefs))
            .OrderByDescending(static item => item.CreatedAt)
            .ToArray();

        var warnings = new List<string>();
        if (sourceRawEvents.Length == 0)
        {
            warnings.Add("未匹配到对应 raw events，当前仅保留 source working item 和 evidence refs。");
        }

        return new ShortTermPromotionCandidateExplanation
        {
            CandidateId = candidate.CandidateId,
            Candidate = candidate,
            SourceWorkingItem = sourceWorkingItem,
            SourceRawEvents = sourceRawEvents,
            EvidenceRefs = candidate.EvidenceRefs,
            Reason = candidate.Reason,
            RuleName = candidate.RuleName,
            RuleVersion = candidate.RuleVersion,
            PolicyVersion = candidate.PolicyVersion,
            Confidence = candidate.Confidence,
            Importance = candidate.Importance,
            SuggestedTargetLayer = candidate.SuggestedTargetLayer,
            DedupeKey = candidate.DedupeKey,
            SourceFingerprint = candidate.SourceFingerprint,
            GeneratedBy = candidate.GeneratedBy,
            Warnings = warnings
        };
    }

    public Task<ReviewPromotionCandidateResponse?> AcceptAsync(
        string candidateId,
        ReviewPromotionCandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewAsync(candidateId, "accept", PromotionCandidateStatus.Accepted, request, cancellationToken);
    }

    public async Task<PromotionCandidateReviewResult?> AcceptAsync(
        string candidateId,
        PromotionCandidateReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return await ReviewAsync(candidateId, "accept", PromotionCandidateStatus.Accepted, request, cancellationToken).ConfigureAwait(false);
    }

    public Task<ReviewPromotionCandidateResponse?> RejectAsync(
        string candidateId,
        ReviewPromotionCandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewAsync(candidateId, "reject", PromotionCandidateStatus.Rejected, request, cancellationToken);
    }

    public async Task<PromotionCandidateReviewResult?> RejectAsync(
        string candidateId,
        PromotionCandidateReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return await ReviewAsync(candidateId, "reject", PromotionCandidateStatus.Rejected, request, cancellationToken).ConfigureAwait(false);
    }

    public Task<ReviewPromotionCandidateResponse?> ExpireAsync(
        string candidateId,
        ReviewPromotionCandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewAsync(candidateId, "expire", PromotionCandidateStatus.Expired, request, cancellationToken);
    }

    public Task<IReadOnlyList<PromotionCandidateReviewRecord>> GetReviewsAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        return _candidateStore.QueryReviewsAsync(candidateId, cancellationToken);
    }

    private async Task<ReviewPromotionCandidateResponse?> ReviewAsync(
        string candidateId,
        string action,
        PromotionCandidateStatus targetStatus,
        PromotionCandidateReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentNullException.ThrowIfNull(request);

        var candidate = await _candidateStore.GetAsync(candidateId, cancellationToken).ConfigureAwait(false);
        if (candidate is null)
        {
            return null;
        }

        if (candidate.Status != PromotionCandidateStatus.Candidate
            && candidate.Status != PromotionCandidateStatus.NeedsReview)
        {
            throw new ArgumentException(
                $"短期晋升候选项当前状态为 {candidate.Status}，不能再次执行 {action}。",
                nameof(candidateId));
        }

        var operationId = string.IsNullOrWhiteSpace(request.OperationId)
            ? Guid.NewGuid().ToString("N")
            : request.OperationId;
        var reviewer = string.IsNullOrWhiteSpace(request.Reviewer) ? "manual" : request.Reviewer.Trim();
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? action : request.Reason.Trim();
        var now = DateTimeOffset.UtcNow;
        var warnings = new List<string>();
        var target = action.Equals("accept", StringComparison.OrdinalIgnoreCase)
            ? await CreateAcceptedTargetAsync(candidate, reviewer, reason, now, warnings, cancellationToken).ConfigureAwait(false)
            : null;
        if (target is not null)
        {
            await ValidateAcceptedTargetAsync(candidate, target, cancellationToken).ConfigureAwait(false);
        }

        var updatedCandidate = CloneCandidate(candidate, targetStatus, reviewer, reason, target, now);
        await _candidateStore.SaveAsync(updatedCandidate, cancellationToken).ConfigureAwait(false);

        var review = new PromotionCandidateReviewRecord
        {
            ReviewId = $"stpr-{BuildShortHash($"{candidate.CandidateId}\u001f{action}\u001f{now:O}")}",
            CandidateId = candidate.CandidateId,
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            SessionId = candidate.SessionId,
            Action = action,
            FromStatus = candidate.Status,
            ToStatus = targetStatus,
            Reviewer = reviewer,
            Reason = reason,
            TargetItemId = target?.TargetItemId,
            TargetItemKind = target?.TargetItemKind,
            TargetLayer = target?.TargetLayer,
            EvidenceRefs = candidate.EvidenceRefs.ToArray(),
            CreatedAt = now,
            ReviewedAt = now,
            Metadata = new Dictionary<string, string>(request.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["operationId"] = operationId,
                ["sourceCandidateId"] = candidate.CandidateId,
                ["sourceWorkingItemId"] = candidate.SourceWorkingItemId,
                ["reviewedAt"] = now.ToString("O")
            },
            Warnings = warnings.ToArray(),
            Errors = Array.Empty<string>()
        };

        await _candidateStore.AppendReviewAsync(review, cancellationToken).ConfigureAwait(false);
        await AppendLearningAsync(updatedCandidate, review, cancellationToken).ConfigureAwait(false);

        return new ReviewPromotionCandidateResponse
        {
            OperationId = operationId,
            CandidateId = candidate.CandidateId,
            Action = action,
            Status = updatedCandidate.Status,
            ReviewId = review.ReviewId,
            Reviewer = reviewer,
            Reason = reason,
            ReviewedAt = now,
            TargetItemId = target?.TargetItemId,
            CreatedTargetItemId = target?.TargetItemId,
            TargetItemKind = target?.TargetItemKind,
            TargetLayer = target?.TargetLayer,
            Candidate = updatedCandidate,
            Review = review,
            Warnings = warnings.ToArray(),
            Errors = Array.Empty<string>()
        };
    }

    private async Task AppendLearningAsync(
        ShortTermPromotionCandidate candidate,
        PromotionCandidateReviewRecord review,
        CancellationToken cancellationToken)
    {
        if (_learningStore is null)
        {
            return;
        }

        var (eventKind, signal, failureType) = ResolveLearningClassification(review.Action);
        if (signal is ContextFeedbackSignal.Positive or ContextFeedbackSignal.Negative)
        {
            await _learningStore.AddFeedbackAsync(BuildFeedbackSignal(candidate, review), cancellationToken).ConfigureAwait(false);
        }

        var record = new ContextLearningRecord
        {
            RecordId = $"clr-{BuildShortHash($"{candidate.CandidateId}\u001f{review.ReviewId}\u001f{review.Action}")}",
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            SessionId = candidate.SessionId,
            SourceKind = "ShortTermPromotionCandidate",
            SourceId = candidate.CandidateId,
            CandidateId = candidate.CandidateId,
            ReviewId = review.ReviewId,
            EventKind = eventKind,
            Signal = signal,
            FailureType = failureType,
            Reason = review.Reason,
            Confidence = candidate.Confidence,
            Importance = candidate.Importance,
            EvidenceRefs = candidate.EvidenceRefs.ToArray(),
            CreatedAt = review.CreatedAt,
            Metadata = BuildLearningMetadata(candidate, review)
        };

        await _learningStore.AddRecordAsync(record, cancellationToken).ConfigureAwait(false);
        var learningCase = _learningCaseGenerator?.Generate(record)
            ?? BuildLearningCase(candidate, review, record, signal, failureType);
        if (learningCase is not null)
        {
            await _learningStore.AddCaseAsync(learningCase, cancellationToken).ConfigureAwait(false);
        }
    }

    private static PromotionFeedbackSignal BuildFeedbackSignal(
        ShortTermPromotionCandidate candidate,
        PromotionCandidateReviewRecord review)
    {
        var action = ResolveFeedbackAction(review.Action);
        var metadata = new Dictionary<string, string>(candidate.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["sourceCandidateId"] = candidate.CandidateId,
            ["reviewId"] = review.ReviewId,
            ["reviewAction"] = review.Action,
            ["sourceWorkingItemId"] = candidate.SourceWorkingItemId,
            ["suggestedTargetLayer"] = candidate.SuggestedTargetLayer
        };
        foreach (var pair in review.Metadata)
        {
            metadata[pair.Key] = pair.Value;
        }

        return new PromotionFeedbackSignal
        {
            FeedbackId = BuildFeedbackId(candidate.CandidateId, review.ReviewId, review.Action),
            CandidateId = candidate.CandidateId,
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            SessionId = candidate.SessionId,
            Action = action,
            Reviewer = review.Reviewer,
            Reason = review.Reason,
            SourceWorkingItemId = candidate.SourceWorkingItemId,
            CreatedTargetItemId = review.TargetItemId,
            SuggestedTargetLayer = candidate.SuggestedTargetLayer,
            ActualTargetLayer = review.TargetLayer,
            Confidence = candidate.Confidence,
            Importance = candidate.Importance,
            EvidenceRefs = candidate.EvidenceRefs.ToArray(),
            CreatedAt = review.ReviewedAt == default ? review.CreatedAt : review.ReviewedAt,
            Metadata = metadata
        };
    }

    private static string ResolveFeedbackAction(string reviewAction)
    {
        return string.Equals(reviewAction, "accept", StringComparison.OrdinalIgnoreCase)
            ? "Accepted"
            : string.Equals(reviewAction, "reject", StringComparison.OrdinalIgnoreCase)
                ? "Rejected"
                : reviewAction;
    }

    private static string BuildFeedbackId(string candidateId, string reviewId, string reviewAction)
    {
        return $"pfs-{BuildShortHash($"{candidateId}\u001f{reviewId}\u001f{ResolveFeedbackAction(reviewAction)}")}";
    }

    private static (string EventKind, ContextFeedbackSignal Signal, ContextFailureType FailureType) ResolveLearningClassification(string action)
    {
        if (string.Equals(action, "accept", StringComparison.OrdinalIgnoreCase))
        {
            return ("PromotionAccepted", ContextFeedbackSignal.Positive, ContextFailureType.None);
        }

        if (string.Equals(action, "reject", StringComparison.OrdinalIgnoreCase))
        {
            return ("PromotionRejected", ContextFeedbackSignal.Negative, ContextFailureType.PromotionFalsePositive);
        }

        if (string.Equals(action, "expire", StringComparison.OrdinalIgnoreCase))
        {
            return ("PromotionExpired", ContextFeedbackSignal.Stale, ContextFailureType.StaleCandidate);
        }

        return ($"Promotion{action}", ContextFeedbackSignal.Negative, ContextFailureType.Unknown);
    }

    private static ContextLearningCase? BuildLearningCase(
        ShortTermPromotionCandidate candidate,
        PromotionCandidateReviewRecord review,
        ContextLearningRecord record,
        ContextFeedbackSignal signal,
        ContextFailureType failureType)
    {
        var caseKind = signal switch
        {
            ContextFeedbackSignal.Positive => "PositivePromotionSample",
            ContextFeedbackSignal.Negative => "PromotionFalsePositive",
            ContextFeedbackSignal.Stale => "StaleContextSample",
            _ => null
        };
        if (caseKind is null)
        {
            return null;
        }

        return new ContextLearningCase
        {
            CaseId = $"clc-{BuildShortHash($"{record.RecordId}\u001f{caseKind}")}",
            SourceType = "PromotionFeedbackSignal",
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            SessionId = candidate.SessionId,
            SourceRecordId = record.RecordId,
            SourceKind = "PromotionFeedbackSignal",
            SourceId = BuildFeedbackId(candidate.CandidateId, review.ReviewId, review.Action),
            CaseKind = caseKind,
            Title = candidate.Title,
            Summary = candidate.Summary,
            InputSummary = string.IsNullOrWhiteSpace(candidate.Summary) ? candidate.Title : candidate.Summary,
            ExpectedBehavior = signal == ContextFeedbackSignal.Positive
                ? $"Keep similar candidates as {candidate.SuggestedTargetLayer} review targets."
                : "Reject similar promotion candidates unless later evidence changes the decision.",
            Signal = signal,
            FailureType = failureType,
            CorrectionReason = review.Reason,
            Status = ContextLearningCaseStatus.Draft,
            EvidenceRefs = candidate.EvidenceRefs.ToArray(),
            PositiveRefs = signal == ContextFeedbackSignal.Positive ? candidate.EvidenceRefs.ToArray() : Array.Empty<string>(),
            NegativeRefs = signal == ContextFeedbackSignal.Negative ? candidate.EvidenceRefs.ToArray() : Array.Empty<string>(),
            CreatedAt = review.CreatedAt,
            Metadata = BuildLearningMetadata(candidate, review)
        };
    }

    private static Dictionary<string, string> BuildLearningMetadata(
        ShortTermPromotionCandidate candidate,
        PromotionCandidateReviewRecord review)
    {
        var metadata = new Dictionary<string, string>(candidate.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["sourceCandidateId"] = candidate.CandidateId,
            ["sourceWorkingItemId"] = candidate.SourceWorkingItemId,
            ["candidateTitle"] = candidate.Title,
            ["candidateSummary"] = candidate.Summary,
            ["reviewId"] = review.ReviewId,
            ["reviewAction"] = review.Action,
            ["reviewer"] = review.Reviewer,
            ["reviewReason"] = review.Reason,
            ["suggestedTargetLayer"] = candidate.SuggestedTargetLayer,
            ["promotionKind"] = candidate.Kind,
            ["learningFlow"] = "context-learning-loop/v1"
        };
        if (string.Equals(review.Action, "accept", StringComparison.OrdinalIgnoreCase)
            || string.Equals(review.Action, "reject", StringComparison.OrdinalIgnoreCase))
        {
            metadata["feedbackId"] = BuildFeedbackId(candidate.CandidateId, review.ReviewId, review.Action);
            metadata["feedbackSourceType"] = "PromotionFeedbackSignal";
        }

        if (!string.IsNullOrWhiteSpace(review.TargetItemId))
        {
            metadata["targetItemId"] = review.TargetItemId;
        }

        if (!string.IsNullOrWhiteSpace(review.TargetItemKind))
        {
            metadata["targetItemKind"] = review.TargetItemKind;
        }

        if (!string.IsNullOrWhiteSpace(review.TargetLayer))
        {
            metadata["targetLayer"] = review.TargetLayer;
        }

        if (review.Metadata.TryGetValue("operationId", out var operationId) && !string.IsNullOrWhiteSpace(operationId))
        {
            metadata["operationId"] = operationId;
        }

        return metadata;
    }

    private async Task<AcceptedTarget> CreateAcceptedTargetAsync(
        ShortTermPromotionCandidate candidate,
        string reviewer,
        string reason,
        DateTimeOffset now,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (string.Equals(candidate.SuggestedTargetLayer, "ConstraintCandidate", StringComparison.OrdinalIgnoreCase))
        {
            if (_constraintStore is null)
            {
                throw new InvalidOperationException("当前 provider 未注册约束存储，不能接受 ConstraintCandidate。");
            }

            var constraint = BuildConstraintCandidate(candidate, reviewer, reason, now);
            await _constraintStore.SaveAsync(constraint, cancellationToken).ConfigureAwait(false);
            await WriteEvidenceRelationsAsync(candidate, constraint.Id, "constraint", now, warnings, cancellationToken).ConfigureAwait(false);
            return new AcceptedTarget(constraint.Id, "constraint", "ConstraintCandidate");
        }

        if (_memoryStore is null)
        {
            throw new InvalidOperationException("当前 provider 未注册记忆存储，不能接受短期晋升候选项。");
        }

        var memoryItem = BuildCandidateMemory(candidate, reviewer, reason, now);
        await _memoryStore.SaveAsync(memoryItem, cancellationToken).ConfigureAwait(false);
        await WriteEvidenceRelationsAsync(candidate, memoryItem.Id, "memory", now, warnings, cancellationToken).ConfigureAwait(false);
        return new AcceptedTarget(memoryItem.Id, "memory", "CandidateMemory");
    }

    private async Task ValidateAcceptedTargetAsync(
        ShortTermPromotionCandidate candidate,
        AcceptedTarget target,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target.TargetItemId))
        {
            throw new InvalidOperationException("接受短期晋升候选项后未返回 CreatedTargetItemId。");
        }

        if (string.Equals(target.TargetItemKind, "memory", StringComparison.OrdinalIgnoreCase))
        {
            if (_memoryStore is null)
            {
                throw new InvalidOperationException("当前 provider 未注册记忆存储，不能验证 accepted target。");
            }

            var memoryItem = await _memoryStore.GetAsync(
                candidate.WorkspaceId,
                candidate.CollectionId,
                target.TargetItemId,
                cancellationToken).ConfigureAwait(false);
            if (memoryItem is null)
            {
                throw new InvalidOperationException($"accepted target 不存在：{target.TargetItemId}");
            }

            if (memoryItem.Status != ContextMemoryStatus.Candidate)
            {
                throw new InvalidOperationException($"accepted target 状态必须是 Candidate，实际为 {memoryItem.Status}。");
            }

            if (memoryItem.Layer == ContextMemoryLayer.Stable)
            {
                throw new InvalidOperationException("accepted target 不能直接写入 StableMemory。");
            }

            if (!HasSourceCandidateRef(memoryItem.SourceRefs, memoryItem.Metadata, candidate.CandidateId))
            {
                throw new InvalidOperationException("accepted target 缺少 source candidate ref。");
            }

            return;
        }

        if (string.Equals(target.TargetItemKind, "constraint", StringComparison.OrdinalIgnoreCase))
        {
            if (_constraintStore is null)
            {
                throw new InvalidOperationException("当前 provider 未注册约束存储，不能验证 accepted constraint target。");
            }

            var constraints = await _constraintStore.QueryAsync(new ContextConstraintQuery
            {
                WorkspaceId = candidate.WorkspaceId,
                CollectionId = candidate.CollectionId,
                Status = ContextMemoryStatus.Candidate,
                Take = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            var constraint = constraints.FirstOrDefault(item => string.Equals(item.Id, target.TargetItemId, StringComparison.OrdinalIgnoreCase));
            if (constraint is null)
            {
                throw new InvalidOperationException($"accepted constraint target 不存在或状态不是 Candidate：{target.TargetItemId}");
            }

            if (!HasSourceCandidateRef(constraint.SourceRefs, constraint.Metadata, candidate.CandidateId))
            {
                throw new InvalidOperationException("accepted constraint target 缺少 source candidate ref。");
            }
        }
    }

    private static bool HasSourceCandidateRef(
        IReadOnlyList<string> sourceRefs,
        IReadOnlyDictionary<string, string> metadata,
        string candidateId)
    {
        return sourceRefs.Contains(candidateId, StringComparer.OrdinalIgnoreCase)
            || (metadata.TryGetValue("sourceCandidateId", out var metadataCandidateId)
                && string.Equals(metadataCandidateId, candidateId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task WriteEvidenceRelationsAsync(
        ShortTermPromotionCandidate candidate,
        string targetItemId,
        string targetKind,
        DateTimeOffset now,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (_relationStore is null)
        {
            warnings.Add("当前 provider 未注册 relation store，未写入 evidence chain 关系。");
            return;
        }

        var relations = new List<ContextRelation>
        {
            BuildRelation(candidate, targetItemId, candidate.CandidateId, ContextRelationTypes.PromotedFrom, targetKind, now),
            BuildRelation(candidate, targetItemId, candidate.SourceWorkingItemId, ContextRelationTypes.DerivedFrom, targetKind, now)
        };

        relations.AddRange(candidate.EvidenceRefs
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(evidenceRef => BuildRelation(candidate, evidenceRef, targetItemId, ContextRelationTypes.EvidenceFor, targetKind, now)));

        await _relationStore.SaveManyAsync(relations, cancellationToken).ConfigureAwait(false);
    }

    private static ContextMemoryItem BuildCandidateMemory(
        ShortTermPromotionCandidate candidate,
        string reviewer,
        string reason,
        DateTimeOffset now)
    {
        var targetId = $"mem:stp:{candidate.CandidateId}";
        return new ContextMemoryItem
        {
            Id = targetId,
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            Layer = ContextMemoryLayer.Structured,
            Status = ContextMemoryStatus.Candidate,
            Type = ResolveAcceptedMemoryType(candidate),
            Content = BuildAcceptedContent(candidate),
            ContentFormat = ContextContentFormat.Markdown,
            Tags = candidate.Tags
                .Append(candidate.Kind)
                .Append("short-term-promotion")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            SourceRefs = candidate.EvidenceRefs
                .Append(candidate.CandidateId)
                .Append(candidate.SourceWorkingItemId)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            RelationRefs = Array.Empty<string>(),
            Importance = candidate.Importance,
            Confidence = candidate.Confidence,
            Version = 1,
            Metadata = BuildTargetMetadata(candidate, reviewer, reason, "memory"),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static ContextConstraint BuildConstraintCandidate(
        ShortTermPromotionCandidate candidate,
        string reviewer,
        string reason,
        DateTimeOffset now)
    {
        return new ContextConstraint
        {
            Id = $"constraint:stp:{candidate.CandidateId}",
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            Scope = ContextScope.Collection,
            Level = ConstraintLevel.Soft,
            Content = BuildAcceptedContent(candidate),
            AppliesToRefs = candidate.EvidenceRefs.ToArray(),
            SourceRefs = candidate.EvidenceRefs
                .Append(candidate.CandidateId)
                .Append(candidate.SourceWorkingItemId)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Status = ContextMemoryStatus.Candidate,
            Confidence = candidate.Confidence,
            Metadata = BuildTargetMetadata(candidate, reviewer, reason, "constraint"),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Dictionary<string, string> BuildTargetMetadata(
        ShortTermPromotionCandidate candidate,
        string reviewer,
        string reason,
        string targetKind)
    {
        var metadata = new Dictionary<string, string>(candidate.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["sourceCandidateId"] = candidate.CandidateId,
            ["sourceWorkingItemId"] = candidate.SourceWorkingItemId,
            ["sourcePromotionKind"] = candidate.Kind,
            ["suggestedTargetLayer"] = candidate.SuggestedTargetLayer,
            ["evidenceRefs"] = string.Join(",", candidate.EvidenceRefs),
            ["acceptedBy"] = reviewer,
            ["acceptReason"] = reason,
            ["promotionTargetKind"] = targetKind,
            ["promotionFlow"] = "short-term-promotion-review/v1"
        };
        return metadata;
    }

    private static ContextRelation BuildRelation(
        ShortTermPromotionCandidate candidate,
        string sourceId,
        string targetId,
        string relationType,
        string targetKind,
        DateTimeOffset now)
    {
        return new ContextRelation
        {
            Id = $"rel:stp:{BuildShortHash($"{candidate.CandidateId}\u001f{sourceId}\u001f{targetId}\u001f{relationType}")}",
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            SourceId = sourceId,
            TargetId = targetId,
            RelationType = relationType,
            Weight = 1.0,
            Confidence = candidate.Confidence,
            SourceRefs = candidate.EvidenceRefs.ToArray(),
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sourceCandidateId"] = candidate.CandidateId,
                ["sourceWorkingItemId"] = candidate.SourceWorkingItemId,
                ["targetKind"] = targetKind,
                ["promotionFlow"] = "short-term-promotion-review/v1"
            },
            CreatedAt = now
        };
    }

    private static string BuildAcceptedContent(ShortTermPromotionCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Summary)
            && !string.Equals(candidate.Title, candidate.Summary, StringComparison.OrdinalIgnoreCase))
        {
            return $"{candidate.Title}\n\n{candidate.Summary}";
        }

        return string.IsNullOrWhiteSpace(candidate.Summary) ? candidate.Title : candidate.Summary;
    }

    private static string ResolveAcceptedMemoryType(ShortTermPromotionCandidate candidate)
    {
        if (string.Equals(candidate.SuggestedTargetLayer, "DecisionRecord", StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.Kind, "RecentDecision", StringComparison.OrdinalIgnoreCase))
        {
            return "decision";
        }

        if (string.Equals(candidate.Kind, "KnownIssue", StringComparison.OrdinalIgnoreCase))
        {
            return "known_issue";
        }

        if (string.Equals(candidate.SuggestedTargetLayer, "OpenIssue", StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.Kind, "OpenQuestion", StringComparison.OrdinalIgnoreCase))
        {
            return "open_issue";
        }

        return NormalizeType(candidate.Kind);
    }

    private static string NormalizeType(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "candidate_memory";
        }

        var builder = new StringBuilder();
        foreach (var ch in value.Trim())
        {
            if (char.IsUpper(ch) && builder.Length > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_');
        }

        return builder.ToString();
    }

    private static ShortTermPromotionCandidate CloneCandidate(
        ShortTermPromotionCandidate candidate,
        PromotionCandidateStatus status,
        string reviewer,
        string reason,
        AcceptedTarget? target,
        DateTimeOffset updatedAt)
    {
        var metadata = new Dictionary<string, string>(candidate.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["lastReviewer"] = reviewer,
            ["lastReviewReason"] = reason,
            ["lastReviewedAt"] = updatedAt.ToString("O")
        };
        
        if (target is null)
            return new ShortTermPromotionCandidate
            {
                CandidateId = candidate.CandidateId,
                WorkspaceId = candidate.WorkspaceId,
                CollectionId = candidate.CollectionId,
                SessionId = candidate.SessionId,
                SourceWorkingItemId = candidate.SourceWorkingItemId,
                Kind = candidate.Kind,
                Title = candidate.Title,
                Summary = candidate.Summary,
                SuggestedTargetLayer = candidate.SuggestedTargetLayer,
                Reason = candidate.Reason,
                Confidence = candidate.Confidence,
                Importance = candidate.Importance,
                EvidenceRefs = candidate.EvidenceRefs.ToArray(),
                Tags = candidate.Tags.ToArray(),
                CreatedAt = candidate.CreatedAt,
                Status = status,
                DedupeKey = candidate.DedupeKey,
                SourceFingerprint = candidate.SourceFingerprint,
                GeneratedBy = candidate.GeneratedBy,
                PolicyVersion = candidate.PolicyVersion,
                RuleName = candidate.RuleName,
                RuleVersion = candidate.RuleVersion,
                Metadata = metadata
            };
        
        metadata["acceptedTargetItemId"] = target.TargetItemId;
        metadata["acceptedTargetItemKind"] = target.TargetItemKind;
        metadata["acceptedTargetLayer"] = target.TargetLayer;

        return new ShortTermPromotionCandidate
        {
            CandidateId = candidate.CandidateId,
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            SessionId = candidate.SessionId,
            SourceWorkingItemId = candidate.SourceWorkingItemId,
            Kind = candidate.Kind,
            Title = candidate.Title,
            Summary = candidate.Summary,
            SuggestedTargetLayer = candidate.SuggestedTargetLayer,
            Reason = candidate.Reason,
            Confidence = candidate.Confidence,
            Importance = candidate.Importance,
            EvidenceRefs = candidate.EvidenceRefs.ToArray(),
            Tags = candidate.Tags.ToArray(),
            CreatedAt = candidate.CreatedAt,
            Status = status,
            DedupeKey = candidate.DedupeKey,
            SourceFingerprint = candidate.SourceFingerprint,
            GeneratedBy = candidate.GeneratedBy,
            PolicyVersion = candidate.PolicyVersion,
            RuleName = candidate.RuleName,
            RuleVersion = candidate.RuleVersion,
            Metadata = metadata
        };
    }

    private static ShortTermPromotionCandidate? CreateCandidate(ShortTermWorkingItem item)
    {
        if (item.Importance < MinimumImportance)
        {
            return null;
        }

        var target = ResolveTarget(item);
        if (target is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var title = string.IsNullOrWhiteSpace(item.Title) ? item.Summary : item.Title;
        var evidenceRefs = ResolveEvidenceRefs(item);
        var dedupeKey = BuildDedupeKey(item, target.Value.SuggestedTargetLayer);
        var sourceFingerprint = BuildSourceFingerprint(item, evidenceRefs);
        var candidateId = BuildCandidateId(dedupeKey);

        return new ShortTermPromotionCandidate
        {
            CandidateId = candidateId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            SourceWorkingItemId = item.ItemId,
            Kind = item.Kind,
            Title = title,
            Summary = item.Summary,
            SuggestedTargetLayer = target.Value.SuggestedTargetLayer,
            Reason = target.Value.Reason,
            Confidence = Math.Clamp(item.Importance, 0d, 1d),
            Importance = item.Importance,
            EvidenceRefs = evidenceRefs,
            Tags = item.Tags.ToArray(),
            CreatedAt = now,
            Status = PromotionCandidateStatus.Candidate,
            DedupeKey = dedupeKey,
            SourceFingerprint = sourceFingerprint,
            GeneratedBy = GeneratedBy,
            PolicyVersion = PolicyVersion,
            RuleName = target.Value.GenerationRule,
            RuleVersion = RuleVersion,
            Metadata = BuildMetadata(item, target.Value.GenerationRule)
        };
    }

    private static (string SuggestedTargetLayer, string Reason, string GenerationRule)? ResolveTarget(ShortTermWorkingItem item)
    {
        if (string.Equals(item.Kind, "RecentDecision", StringComparison.OrdinalIgnoreCase))
        {
            return ("CandidateMemory", "RecentDecision 具备复用价值，建议进入候选记忆层。", "recent-decision-to-candidate-memory");
        }

        if (string.Equals(item.Kind, "KnownIssue", StringComparison.OrdinalIgnoreCase))
        {
            return ("CandidateMemory", "KnownIssue 需要跨轮次保留，建议进入候选记忆层。", "known-issue-to-candidate-memory");
        }

        if (string.Equals(item.Kind, "TemporaryConstraint", StringComparison.OrdinalIgnoreCase))
        {
            return ("ConstraintCandidate", "TemporaryConstraint 应进入约束候选流等待后续确认。", "temporary-constraint-to-constraint-candidate");
        }

        if (string.Equals(item.Kind, "ActiveTask", StringComparison.OrdinalIgnoreCase) && IsResolvedTask(item.Status))
        {
            return ("DecisionRecord", "已解决的 ActiveTask 可以沉淀为决策记录候选项。", "resolved-active-task-to-decision-record");
        }

        if (string.Equals(item.Kind, "OpenQuestion", StringComparison.OrdinalIgnoreCase))
        {
            return ("OpenIssue", "OpenQuestion 仍待后续跟进，建议进入开放问题候选流。", "open-question-to-open-issue");
        }

        return null;
    }

    private static bool IsResolvedTask(string status)
    {
        return status.Contains("resolved", StringComparison.OrdinalIgnoreCase)
            || status.Contains("closed", StringComparison.OrdinalIgnoreCase)
            || status.Contains("done", StringComparison.OrdinalIgnoreCase)
            || status.Contains("completed", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ResolveEvidenceRefs(ShortTermWorkingItem item)
    {
        var refs = item.Refs
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Concat(item.SourceRefs.Where(static value => !string.IsNullOrWhiteSpace(value)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return refs.Length > 0 ? refs : [$"working:{item.ItemId}"];
    }

    private static Dictionary<string, string> BuildMetadata(ShortTermWorkingItem item, string generationRule)
    {
        var metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["sourceWorkingStatus"] = item.Status,
            ["sourceWorkingLifecycle"] = item.Lifecycle,
            ["generationRule"] = generationRule,
            ["generatedBy"] = GeneratedBy,
            ["policyVersion"] = PolicyVersion,
            ["ruleVersion"] = RuleVersion
        };
        return metadata;
    }

    private static string BuildDedupeKey(ShortTermWorkingItem item, string suggestedTargetLayer)
    {
        return string.Join('\u001f',
            item.WorkspaceId,
            item.CollectionId,
            item.SessionId ?? string.Empty,
            item.ItemId,
            item.Kind,
            suggestedTargetLayer);
    }

    private static string BuildCandidateId(string dedupeKey)
    {
        return $"stpc-{BuildShortHash(dedupeKey)}";
    }

    private static string BuildSourceFingerprint(ShortTermWorkingItem item, IReadOnlyList<string> evidenceRefs)
    {
        var input = string.Join('\u001f',
            item.Kind,
            item.Title,
            item.Summary,
            item.Status,
            string.Join('|', evidenceRefs.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return $"stpf-{Convert.ToHexString(hash)[..20].ToLowerInvariant()}";
    }

    private static bool MatchesEvidence(ShortTermRawEvent item, IReadOnlyList<string> evidenceRefs)
    {
        if (evidenceRefs.Contains(item.EventId, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (item.Metadata.TryGetValue("sourceRefs", out var sourceRefs) && !string.IsNullOrWhiteSpace(sourceRefs))
        {
            var refs = sourceRefs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return refs.Any(reference => evidenceRefs.Contains(reference, StringComparer.OrdinalIgnoreCase));
        }

        return false;
    }

    private static string BuildShortHash(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..20].ToLowerInvariant();
    }

    private sealed record AcceptedTarget(string TargetItemId, string TargetItemKind, string TargetLayer);
}
