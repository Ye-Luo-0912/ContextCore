using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>构建中期候选记忆的只读治理快照、解释链和诊断。</summary>
public sealed class CandidateMemorySnapshotService
{
    private readonly IMemoryStore? _memoryStore;
    private readonly IConstraintStore? _constraintStore;
    private readonly IShortTermPromotionCandidateStore? _promotionStore;
    private readonly IStableReviewCandidateStore? _stableReviewStore;
    private readonly IConstraintGapCandidateStore? _constraintGapStore;
    private readonly IContextLearningStore? _learningStore;
    private readonly ICandidateConstraintReviewStore? _candidateConstraintReviewStore;
    private readonly ICandidateMemoryReviewStore? _candidateMemoryReviewStore;

    public CandidateMemorySnapshotService(
        IMemoryStore? memoryStore,
        IConstraintStore? constraintStore,
        IShortTermPromotionCandidateStore? promotionStore,
        IStableReviewCandidateStore? stableReviewStore,
        IConstraintGapCandidateStore? constraintGapStore,
        IContextLearningStore? learningStore,
        ICandidateConstraintReviewStore? candidateConstraintReviewStore,
        ICandidateMemoryReviewStore? candidateMemoryReviewStore)
    {
        _memoryStore = memoryStore;
        _constraintStore = constraintStore;
        _promotionStore = promotionStore;
        _stableReviewStore = stableReviewStore;
        _constraintGapStore = constraintGapStore;
        _learningStore = learningStore;
        _candidateConstraintReviewStore = candidateConstraintReviewStore;
        _candidateMemoryReviewStore = candidateMemoryReviewStore;
    }

    public async Task<CandidateMemorySnapshot> GetSnapshotAsync(
        string workspaceId,
        string? collectionId = null,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var now = DateTimeOffset.UtcNow;
        var warnings = new List<string>();
        var records = await QueryRecordsAsync(workspaceId, collectionId, int.MaxValue, 0, warnings, cancellationToken)
            .ConfigureAwait(false);
        var diagnostics = BuildDiagnostics(records, now);

        return new CandidateMemorySnapshot
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            CreatedAt = now,
            CandidateMemoryCount = records.Count(item => item.CandidateKind == CandidateMemoryKinds.Memory),
            CandidateConstraintCount = records.Count(item => item.CandidateKind == CandidateMemoryKinds.Constraint),
            CandidateDecisionCount = records.Count(item => item.CandidateKind == CandidateMemoryKinds.Decision),
            PendingReviewCount = records.Count(item => item.Status == ContextMemoryStatus.Candidate),
            AcceptedFromPromotionCount = records.Count(IsAcceptedFromPromotion),
            ExpiredCandidateCount = diagnostics.Count(item => item.DiagnosticType == CandidateMemoryDiagnosticTypes.StaleCandidate),
            DuplicateCandidateCount = diagnostics.Count(item => item.DiagnosticType == CandidateMemoryDiagnosticTypes.DuplicateCandidate),
            ConflictCandidateCount = diagnostics.Count(item => item.DiagnosticType == CandidateMemoryDiagnosticTypes.StableConflict),
            RecentCandidates = records
                .OrderByDescending(item => item.UpdatedAt == default ? item.CreatedAt : item.UpdatedAt)
                .Take(take > 0 ? take : 20)
                .ToArray(),
            Warnings = warnings.ToArray()
        };
    }

    public async Task<CandidateMemoryRecord?> GetAsync(
        string candidateId,
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var warnings = new List<string>();
        var records = await QueryRecordsAsync(workspaceId, collectionId, int.MaxValue, 0, warnings, cancellationToken)
            .ConfigureAwait(false);
        return records.FirstOrDefault(item => string.Equals(item.Id, candidateId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<CandidateMemoryExplanation?> ExplainAsync(
        string candidateId,
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        var candidate = await GetAsync(candidateId, workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
        if (candidate is null)
        {
            return null;
        }

        var warnings = new List<string>();
        var sourcePromotion = await ResolvePromotionCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
        var sourceStable = await ResolveStableReviewCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
        var sourceGap = await ResolveConstraintGapAsync(candidate, cancellationToken).ConfigureAwait(false);
        var feedback = await ResolveFeedbackAsync(candidate, cancellationToken).ConfigureAwait(false);
        var learningCase = await ResolveLearningCaseAsync(candidate, sourceStable, cancellationToken).ConfigureAwait(false);
        var promotionReviews = sourcePromotion is null || _promotionStore is null
            ? Array.Empty<PromotionCandidateReviewRecord>()
            : await _promotionStore.QueryReviewsAsync(sourcePromotion.CandidateId, cancellationToken).ConfigureAwait(false);
        var stableReviews = sourceStable is null || _stableReviewStore is null
            ? Array.Empty<StableReviewRecord>()
            : await _stableReviewStore.QueryReviewsAsync(sourceStable.StableReviewCandidateId, cancellationToken).ConfigureAwait(false);
        var gapReviews = sourceGap is null || _constraintGapStore is null
            ? Array.Empty<ConstraintGapReviewRecord>()
            : await _constraintGapStore.QueryReviewsAsync(sourceGap.GapId, cancellationToken).ConfigureAwait(false);
        var constraintReviews = candidate.CandidateKind == CandidateMemoryKinds.Constraint && _candidateConstraintReviewStore is not null
            ? await _candidateConstraintReviewStore.QueryReviewsAsync(candidate.Id, cancellationToken).ConfigureAwait(false)
            : Array.Empty<CandidateConstraintReviewRecord>();
        var candidateMemoryReviews = _candidateMemoryReviewStore is null
            ? Array.Empty<CandidateMemoryReviewRecord>()
            : await _candidateMemoryReviewStore.QueryReviewsAsync(candidate.Id, cancellationToken).ConfigureAwait(false);

        if (sourcePromotion is null && !string.IsNullOrWhiteSpace(candidate.PromotionCandidateId))
        {
            warnings.Add("source promotion candidate link is missing.");
        }

        if (candidate.EvidenceRefs.Count == 0)
        {
            warnings.Add("candidate has no evidence refs.");
        }

        return new CandidateMemoryExplanation
        {
            CandidateId = candidate.Id,
            Candidate = candidate,
            SourcePromotionCandidate = sourcePromotion,
            SourceStableReviewCandidate = sourceStable,
            SourceConstraintGap = sourceGap,
            SourceFeedbackSignal = feedback,
            SourceLearningCase = learningCase,
            EvidenceRefs = candidate.EvidenceRefs,
            PromotionReviewHistory = promotionReviews,
            StableReviewHistory = stableReviews,
            ConstraintGapReviewHistory = gapReviews,
            CandidateConstraintReviewHistory = constraintReviews,
            CandidateMemoryReviewHistory = candidateMemoryReviews,
            ProvenanceChain = BuildProvenance(candidate, sourcePromotion, sourceStable, sourceGap, feedback, learningCase),
            RiskFlags = BuildRiskFlags(candidate, sourcePromotion, sourceStable).ToArray(),
            Warnings = warnings.ToArray()
        };
    }

    public async Task<CandidateMemoryDiagnosticsReport> GetDiagnosticsAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var warnings = new List<string>();
        var records = await QueryRecordsAsync(workspaceId, collectionId, int.MaxValue, 0, warnings, cancellationToken)
            .ConfigureAwait(false);
        var diagnostics = BuildDiagnostics(records, DateTimeOffset.UtcNow);

        return new CandidateMemoryDiagnosticsReport
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            CreatedAt = DateTimeOffset.UtcNow,
            DiagnosticCount = diagnostics.Count,
            DuplicateCandidateCount = diagnostics.Count(item => item.DiagnosticType == CandidateMemoryDiagnosticTypes.DuplicateCandidate),
            StaleCandidateCount = diagnostics.Count(item => item.DiagnosticType == CandidateMemoryDiagnosticTypes.StaleCandidate),
            CandidateWithoutEvidenceCount = diagnostics.Count(item => item.DiagnosticType == CandidateMemoryDiagnosticTypes.CandidateWithoutEvidence),
            CandidateWithRejectedSourceCount = diagnostics.Count(item => item.DiagnosticType == CandidateMemoryDiagnosticTypes.CandidateWithRejectedSource),
            StableConflictCount = diagnostics.Count(item => item.DiagnosticType == CandidateMemoryDiagnosticTypes.StableConflict),
            SupersededCandidateCount = diagnostics.Count(item => item.DiagnosticType == CandidateMemoryDiagnosticTypes.SupersededByNewerCandidate),
            Diagnostics = diagnostics
        };
    }

    private async Task<IReadOnlyList<CandidateMemoryRecord>> QueryRecordsAsync(
        string workspaceId,
        string? collectionId,
        int limit,
        int offset,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var records = new List<CandidateMemoryRecord>();
        if (_memoryStore is not null)
        {
            var memoryItems = await _memoryStore.QueryAsync(new ContextMemoryQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Status = ContextMemoryStatus.Candidate,
                Take = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            records.AddRange(memoryItems.Select(ToRecord));
        }
        else
        {
            warnings.Add("memory store is not registered.");
        }

        if (_constraintStore is not null)
        {
            var constraints = await _constraintStore.QueryAsync(new ContextConstraintQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Status = ContextMemoryStatus.Candidate,
                Take = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            records.AddRange(constraints.Select(ToRecord));
        }
        else
        {
            warnings.Add("constraint store is not registered.");
        }

        return records
            .OrderByDescending(item => item.UpdatedAt == default ? item.CreatedAt : item.UpdatedAt)
            .Skip(Math.Max(0, offset))
            .Take(limit > 0 ? limit : 20)
            .ToArray();
    }

    private static CandidateMemoryRecord ToRecord(ContextMemoryItem item)
    {
        var evidenceRefs = ResolveEvidenceRefs(item.SourceRefs, item.Metadata);
        var promotionCandidateId = ResolvePromotionCandidateId(item.SourceRefs, item.Metadata);
        var candidateKind = IsDecision(item.Type, item.Metadata)
            ? CandidateMemoryKinds.Decision
            : CandidateMemoryKinds.Memory;
        return new CandidateMemoryRecord
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = GetMetadata(item.Metadata, "sessionId"),
            CandidateKind = candidateKind,
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
            PromotionCandidateId = promotionCandidateId,
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

    private IReadOnlyList<CandidateMemoryDiagnostic> BuildDiagnostics(
        IReadOnlyList<CandidateMemoryRecord> records,
        DateTimeOffset now)
    {
        var diagnostics = new List<CandidateMemoryDiagnostic>();
        foreach (var item in records)
        {
            if (item.EvidenceRefs.Count == 0)
            {
                diagnostics.Add(BuildDiagnostic(
                    item,
                    CandidateMemoryDiagnosticTypes.CandidateWithoutEvidence,
                    "High",
                    "Candidate has no evidence refs.",
                    suggestedAction: CandidateMemoryReviewActions.NeedsMoreEvidence));
            }

            if (IsStale(item, now))
            {
                diagnostics.Add(BuildDiagnostic(
                    item,
                    CandidateMemoryDiagnosticTypes.StaleCandidate,
                    "Medium",
                    "Candidate is expired, deprecated, rejected, stale, or superseded.",
                    suggestedAction: CandidateMemoryReviewActions.Expire));
            }

            if (IsRejectedSource(item))
            {
                diagnostics.Add(BuildDiagnostic(
                    item,
                    CandidateMemoryDiagnosticTypes.CandidateWithRejectedSource,
                    "High",
                    "Candidate metadata or lifecycle marks a rejected source.",
                    suggestedAction: CandidateMemoryReviewActions.Reject));
            }

            if (HasStableConflict(item))
            {
                diagnostics.Add(BuildDiagnostic(
                    item,
                    CandidateMemoryDiagnosticTypes.StableConflict,
                    "High",
                    "Candidate metadata indicates conflict with active stable memory.",
                    suggestedAction: CandidateMemoryReviewActions.NeedsMoreEvidence));
            }
        }

        foreach (var group in records.GroupBy(BuildDuplicateKey, StringComparer.OrdinalIgnoreCase)
                     .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1))
        {
            var related = group.Select(item => item.Id).ToArray();
            diagnostics.AddRange(group.Select(item => BuildDiagnostic(
                item,
                CandidateMemoryDiagnosticTypes.DuplicateCandidate,
                "Medium",
                "Candidate content duplicates another candidate.",
                suggestedAction: CandidateMemoryReviewActions.Supersede,
                related.Where(id => !string.Equals(id, item.Id, StringComparison.OrdinalIgnoreCase)).ToArray())));
        }

        foreach (var group in records.GroupBy(item => $"{item.Type}\u001f{NormalizeText(item.Title)}", StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderByDescending(item => item.UpdatedAt == default ? item.CreatedAt : item.UpdatedAt).ToArray();
            foreach (var older in ordered.Skip(1).Where(item => item.Status == ContextMemoryStatus.Candidate))
            {
                diagnostics.Add(BuildDiagnostic(
                    older,
                    CandidateMemoryDiagnosticTypes.SupersededByNewerCandidate,
                    "Low",
                    "A newer candidate with the same type/title exists.",
                    suggestedAction: CandidateMemoryReviewActions.Supersede,
                    [ordered[0].Id]));
            }
        }

        return diagnostics
            .OrderByDescending(item => item.Severity == "High")
            .ThenBy(item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DiagnosticType, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<ShortTermPromotionCandidate?> ResolvePromotionCandidateAsync(
        CandidateMemoryRecord candidate,
        CancellationToken cancellationToken)
    {
        if (_promotionStore is null || string.IsNullOrWhiteSpace(candidate.PromotionCandidateId))
        {
            return null;
        }

        return await _promotionStore.GetAsync(candidate.PromotionCandidateId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StableReviewCandidate?> ResolveStableReviewCandidateAsync(
        CandidateMemoryRecord candidate,
        CancellationToken cancellationToken)
    {
        if (_stableReviewStore is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(candidate.StableReviewCandidateId))
        {
            var direct = await _stableReviewStore.GetAsync(candidate.StableReviewCandidateId, cancellationToken).ConfigureAwait(false);
            if (direct is not null)
            {
                return direct;
            }
        }

        var candidates = await _stableReviewStore.QueryAsync(new StableReviewCandidateQuery
        {
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            Limit = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);
        return candidates.FirstOrDefault(item =>
            string.Equals(item.SourceTargetItemId, candidate.Id, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(candidate.PromotionCandidateId)
                && string.Equals(item.SourceCandidateId, candidate.PromotionCandidateId, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<ConstraintGapCandidate?> ResolveConstraintGapAsync(
        CandidateMemoryRecord candidate,
        CancellationToken cancellationToken)
    {
        if (_constraintGapStore is null || string.IsNullOrWhiteSpace(candidate.ConstraintGapId))
        {
            return null;
        }

        return await _constraintGapStore.GetAsync(candidate.ConstraintGapId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PromotionFeedbackSignal?> ResolveFeedbackAsync(
        CandidateMemoryRecord candidate,
        CancellationToken cancellationToken)
    {
        if (_learningStore is null)
        {
            return null;
        }

        var feedback = await _learningStore.QueryFeedbackAsync(new PromotionFeedbackSignalQuery
        {
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            CandidateId = candidate.PromotionCandidateId,
            Limit = 20
        }, cancellationToken).ConfigureAwait(false);
        return feedback.FirstOrDefault(item =>
            string.IsNullOrWhiteSpace(candidate.FeedbackId)
            || string.Equals(item.FeedbackId, candidate.FeedbackId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ContextLearningCase?> ResolveLearningCaseAsync(
        CandidateMemoryRecord candidate,
        StableReviewCandidate? stableReview,
        CancellationToken cancellationToken)
    {
        if (_learningStore is null)
        {
            return null;
        }

        var learningCaseId = candidate.LearningCaseId ?? stableReview?.SourceLearningCaseId;
        if (!string.IsNullOrWhiteSpace(learningCaseId))
        {
            var direct = await _learningStore.GetCaseAsync(learningCaseId, cancellationToken).ConfigureAwait(false);
            if (direct is not null)
            {
                return direct;
            }
        }

        var cases = await _learningStore.QueryCasesAsync(new ContextLearningCaseQuery
        {
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            Limit = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);
        return cases.FirstOrDefault(item =>
            (!string.IsNullOrWhiteSpace(candidate.PromotionCandidateId)
                && (string.Equals(item.SourceId, candidate.PromotionCandidateId, StringComparison.OrdinalIgnoreCase)
                    || (item.Metadata.TryGetValue("sourceCandidateId", out var sourceCandidateId)
                        && string.Equals(sourceCandidateId, candidate.PromotionCandidateId, StringComparison.OrdinalIgnoreCase))))
            || item.EvidenceRefs.Any(reference => candidate.EvidenceRefs.Contains(reference, StringComparer.OrdinalIgnoreCase)));
    }

    private static IReadOnlyList<CandidateMemoryProvenanceLink> BuildProvenance(
        CandidateMemoryRecord candidate,
        ShortTermPromotionCandidate? promotion,
        StableReviewCandidate? stable,
        ConstraintGapCandidate? gap,
        PromotionFeedbackSignal? feedback,
        ContextLearningCase? learningCase)
    {
        var links = new List<CandidateMemoryProvenanceLink>
        {
            new()
            {
                SourceType = "CandidateMemoryRecord",
                SourceId = candidate.Id,
                Relation = "target",
                Status = candidate.Status.ToString()
            }
        };
        if (promotion is not null)
        {
            links.Add(new CandidateMemoryProvenanceLink
            {
                SourceType = "ShortTermPromotionCandidate",
                SourceId = promotion.CandidateId,
                Relation = "promoted-from",
                Status = promotion.Status.ToString()
            });
        }

        if (stable is not null)
        {
            links.Add(new CandidateMemoryProvenanceLink
            {
                SourceType = "StableReviewCandidate",
                SourceId = stable.StableReviewCandidateId,
                Relation = "stable-review",
                Status = stable.Status
            });
        }

        if (gap is not null)
        {
            links.Add(new CandidateMemoryProvenanceLink
            {
                SourceType = "ConstraintGapCandidate",
                SourceId = gap.GapId,
                Relation = "constraint-gap",
                Status = gap.Status
            });
        }

        if (feedback is not null)
        {
            links.Add(new CandidateMemoryProvenanceLink
            {
                SourceType = "PromotionFeedbackSignal",
                SourceId = feedback.FeedbackId,
                Relation = "feedback",
                Status = feedback.Action
            });
        }

        if (learningCase is not null)
        {
            links.Add(new CandidateMemoryProvenanceLink
            {
                SourceType = "ContextLearningCase",
                SourceId = learningCase.CaseId,
                Relation = "learning-case",
                Status = learningCase.Status.ToString()
            });
        }

        return links;
    }

    private static IEnumerable<string> BuildRiskFlags(
        CandidateMemoryRecord candidate,
        ShortTermPromotionCandidate? promotion,
        StableReviewCandidate? stable)
    {
        if (candidate.EvidenceRefs.Count == 0)
        {
            yield return CandidateMemoryDiagnosticTypes.CandidateWithoutEvidence;
        }

        if (promotion?.Status == PromotionCandidateStatus.Rejected)
        {
            yield return CandidateMemoryDiagnosticTypes.CandidateWithRejectedSource;
        }

        if (stable is not null)
        {
            foreach (var risk in stable.RiskFlags)
            {
                yield return risk;
            }
        }

        if (HasStableConflict(candidate))
        {
            yield return CandidateMemoryDiagnosticTypes.StableConflict;
        }
    }

    private static CandidateMemoryDiagnostic BuildDiagnostic(
        CandidateMemoryRecord item,
        string type,
        string severity,
        string reason,
        string suggestedAction,
        IReadOnlyList<string>? related = null)
    {
        return new CandidateMemoryDiagnostic
        {
            DiagnosticId = $"cmd-{BuildShortHash($"{item.Id}\u001f{type}\u001f{string.Join(',', related ?? Array.Empty<string>())}")}",
            CandidateId = item.Id,
            DiagnosticType = type,
            Severity = severity,
            Reason = reason,
            SuggestedAction = suggestedAction,
            RelatedCandidateIds = related ?? Array.Empty<string>(),
            EvidenceRefs = item.EvidenceRefs
        };
    }

    private static bool IsAcceptedFromPromotion(CandidateMemoryRecord item)
    {
        return !string.IsNullOrWhiteSpace(item.PromotionCandidateId)
            || item.Metadata.ContainsKey("sourceCandidateId")
            || item.Metadata.ContainsKey("sourcePromotionCandidateId")
            || item.SourceRefs.Any(reference => reference.StartsWith("stpc-", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDecision(string type, IReadOnlyDictionary<string, string> metadata)
    {
        return string.Equals(type, "decision", StringComparison.OrdinalIgnoreCase)
            || string.Equals(GetMetadata(metadata, "suggestedTargetLayer"), "DecisionRecord", StringComparison.OrdinalIgnoreCase)
            || string.Equals(GetMetadata(metadata, "targetLayer"), "DecisionRecord", StringComparison.OrdinalIgnoreCase)
            || string.Equals(GetMetadata(metadata, "sourcePromotionKind"), "RecentDecision", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStale(CandidateMemoryRecord item, DateTimeOffset now)
    {
        return item.Status is ContextMemoryStatus.Deprecated or ContextMemoryStatus.Rejected
            || string.Equals(item.Lifecycle, CandidateMemoryLifecycle.Stale, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Lifecycle, CandidateMemoryLifecycle.Superseded, StringComparison.OrdinalIgnoreCase)
            || (item.ExpiresAt is not null && item.ExpiresAt <= now);
    }

    private static bool IsRejectedSource(CandidateMemoryRecord item)
    {
        return item.Metadata.Any(pair =>
            pair.Key.Contains("source", StringComparison.OrdinalIgnoreCase)
            && pair.Value.Contains("Rejected", StringComparison.OrdinalIgnoreCase))
            || string.Equals(GetMetadata(item.Metadata, "sourceStatus"), "Rejected", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasStableConflict(CandidateMemoryRecord item)
    {
        return item.Metadata.ContainsKey("conflictWithStableId")
            || item.Metadata.ContainsKey("conflictWithActiveStableMemory")
            || string.Equals(GetMetadata(item.Metadata, "lifecycleConflict"), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildDuplicateKey(CandidateMemoryRecord item)
    {
        var explicitKey = GetMetadata(item.Metadata, "dedupeKey")
            ?? GetMetadata(item.Metadata, "sourceFingerprint");
        if (!string.IsNullOrWhiteSpace(explicitKey))
        {
            return $"{item.CandidateKind}\u001f{explicitKey}";
        }

        var text = NormalizeText(string.IsNullOrWhiteSpace(item.Content) ? item.Summary : item.Content);
        return string.IsNullOrWhiteSpace(text) ? string.Empty : $"{item.CandidateKind}\u001f{item.Type}\u001f{text}";
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

    private static string NormalizeText(string value)
    {
        var builder = new StringBuilder();
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
            else if (char.IsWhiteSpace(ch) && builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(' ');
            }
        }

        return builder.ToString().Trim();
    }

    private static string BuildShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
