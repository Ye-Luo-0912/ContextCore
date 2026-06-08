using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>聚合 stable review accept 生成对象的来源链，只读诊断，不做自动修复。</summary>
public sealed class ContextProvenanceService
{
    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private readonly IMemoryStore? _memoryStore;
    private readonly IConstraintStore? _constraintStore;
    private readonly IStableReviewCandidateStore? _stableReviewCandidateStore;
    private readonly IShortTermPromotionCandidateStore? _promotionCandidateStore;
    private readonly IContextLearningStore? _learningStore;
    private readonly IShortTermMemoryStore? _shortTermMemoryStore;

    public ContextProvenanceService(
        IMemoryStore? memoryStore,
        IConstraintStore? constraintStore,
        IStableReviewCandidateStore? stableReviewCandidateStore,
        IShortTermPromotionCandidateStore? promotionCandidateStore,
        IContextLearningStore? learningStore,
        IShortTermMemoryStore? shortTermMemoryStore)
    {
        _memoryStore = memoryStore;
        _constraintStore = constraintStore;
        _stableReviewCandidateStore = stableReviewCandidateStore;
        _promotionCandidateStore = promotionCandidateStore;
        _learningStore = learningStore;
        _shortTermMemoryStore = shortTermMemoryStore;
    }

    public async Task<ContextProvenanceResponse?> GetAsync(
        string itemId,
        string? workspaceId = null,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);

        var warnings = new List<string>();
        var missingLinks = new List<string>();
        var diagnostics = new List<StableDiagnosticWarning>();
        var stableReviewCandidateId = InferStableReviewCandidateId(itemId);
        var stableReviewCandidate = await ResolveStableReviewCandidateAsync(
            itemId,
            stableReviewCandidateId,
            cancellationToken).ConfigureAwait(false);
        var stableReviewHistory = await ResolveStableReviewHistoryAsync(
            stableReviewCandidate?.StableReviewCandidateId ?? stableReviewCandidateId,
            cancellationToken).ConfigureAwait(false);

        workspaceId = FirstNonEmpty(workspaceId, stableReviewCandidate?.WorkspaceId);
        collectionId = FirstNonEmpty(collectionId, stableReviewCandidate?.CollectionId);

        var targetItemId = ResolveTargetItemId(itemId, stableReviewCandidate, stableReviewHistory);
        var targetKind = ResolveTargetKind(itemId, stableReviewCandidate, stableReviewHistory);
        var targetMemory = await ResolveTargetMemoryAsync(
            workspaceId,
            collectionId,
            targetItemId,
            targetKind,
            cancellationToken).ConfigureAwait(false);
        var targetConstraint = await ResolveTargetConstraintAsync(
            workspaceId,
            collectionId,
            targetItemId,
            targetKind,
            cancellationToken).ConfigureAwait(false);

        if (stableReviewCandidate is null)
        {
            var targetMetadata = ResolveTargetMetadata(targetMemory, targetConstraint);
            stableReviewCandidateId = FirstNonEmpty(
                stableReviewCandidateId,
                ReadMetadata(targetMetadata, "sourceStableReviewCandidateId"));
            stableReviewCandidate = await ResolveStableReviewCandidateAsync(
                stableReviewCandidateId,
                stableReviewCandidateId,
                cancellationToken).ConfigureAwait(false);
            if (stableReviewCandidate is not null)
            {
                stableReviewHistory = await ResolveStableReviewHistoryAsync(
                    stableReviewCandidate.StableReviewCandidateId,
                    cancellationToken).ConfigureAwait(false);
                workspaceId = FirstNonEmpty(workspaceId, stableReviewCandidate.WorkspaceId);
                collectionId = FirstNonEmpty(collectionId, stableReviewCandidate.CollectionId);
            }
        }

        if (stableReviewCandidate is null && targetMemory is null && targetConstraint is null)
        {
            return null;
        }

        var metadata = ResolveTargetMetadata(targetMemory, targetConstraint);
        var sourcePromotionCandidateId = FirstNonEmpty(
            stableReviewCandidate?.SourceCandidateId,
            ReadMetadata(metadata, "sourcePromotionCandidateId", "sourceCandidateId", "stableReviewSourceCandidateId"));
        var promotionCandidate = await ResolvePromotionCandidateAsync(
            sourcePromotionCandidateId,
            cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(sourcePromotionCandidateId) && promotionCandidate is null)
        {
            AddMissing("promotionCandidate", sourcePromotionCandidateId, missingLinks, warnings, diagnostics, targetItemId);
        }

        var promotionReviewHistory = await ResolvePromotionReviewHistoryAsync(
            promotionCandidate?.CandidateId ?? sourcePromotionCandidateId,
            cancellationToken).ConfigureAwait(false);

        var sourceLearningCaseId = FirstNonEmpty(
            stableReviewCandidate?.SourceLearningCaseId,
            ReadMetadata(metadata, "sourceLearningCaseId"),
            ReadMetadata(promotionCandidate?.Metadata, "sourceLearningCaseId"));
        var learningCase = await ResolveLearningCaseAsync(sourceLearningCaseId, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(sourceLearningCaseId) && learningCase is null)
        {
            AddMissing("learningCase", sourceLearningCaseId, missingLinks, warnings, diagnostics, targetItemId);
        }

        var sourceFeedbackId = FirstNonEmpty(
            ReadMetadata(metadata, "sourceFeedbackId"),
            ReadMetadata(stableReviewCandidate?.Metadata, "sourceFeedbackId", "feedbackId"),
            ReadMetadata(promotionCandidate?.Metadata, "sourceFeedbackId", "feedbackId"),
            ReadMetadata(learningCase?.Metadata, "sourceFeedbackId", "feedbackId"),
            IsFeedbackSource(learningCase) ? learningCase?.SourceId : null);
        var feedback = await ResolveFeedbackAsync(
            sourceFeedbackId,
            promotionCandidate,
            stableReviewCandidate,
            learningCase,
            workspaceId,
            collectionId,
            cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(sourceFeedbackId) && feedback is null)
        {
            AddMissing("feedbackSignal", sourceFeedbackId, missingLinks, warnings, diagnostics, targetItemId);
        }

        var sourceWorkingItemId = FirstNonEmpty(
            ReadMetadata(metadata, "sourceWorkingItemId"),
            stableReviewCandidate?.Metadata.GetValueOrDefault("sourceWorkingItemId"),
            promotionCandidate?.SourceWorkingItemId,
            feedback?.SourceWorkingItemId);
        var sourceWorkingItem = await ResolveSourceWorkingItemAsync(
            workspaceId,
            collectionId,
            promotionCandidate?.SessionId ?? stableReviewCandidate?.SessionId,
            sourceWorkingItemId,
            cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(sourceWorkingItemId) && sourceWorkingItem is null)
        {
            AddMissing("sourceWorkingItem", sourceWorkingItemId, missingLinks, warnings, diagnostics, targetItemId);
        }

        AddRequiredMetadataDiagnostics(metadata, targetItemId, missingLinks, warnings, diagnostics);
        await AddStableDiagnosticsAsync(
            targetItemId,
            targetMemory,
            targetConstraint,
            stableReviewCandidate,
            promotionCandidate,
            workspaceId,
            collectionId,
            diagnostics,
            warnings,
            cancellationToken).ConfigureAwait(false);

        var evidenceRefs = ResolveEvidenceRefs(
            metadata,
            targetMemory,
            targetConstraint,
            stableReviewCandidate,
            promotionCandidate,
            feedback,
            learningCase);

        return new ContextProvenanceResponse
        {
            ItemId = itemId,
            TargetItemKind = targetMemory is not null
                ? "memory"
                : targetConstraint is not null
                    ? "constraint"
                    : string.Empty,
            TargetMemoryItem = targetMemory,
            TargetConstraint = targetConstraint,
            StableReviewCandidate = stableReviewCandidate,
            PromotionCandidate = promotionCandidate,
            FeedbackSignal = feedback,
            LearningCase = learningCase,
            SourceWorkingItem = sourceWorkingItem,
            EvidenceRefs = evidenceRefs,
            StableReviewHistory = stableReviewHistory,
            PromotionReviewHistory = promotionReviewHistory,
            Diagnostics = diagnostics
                .DistinctBy(item => $"{item.Code}\u001f{item.TargetItemId}\u001f{item.Message}", StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            MissingLinks = missingLinks
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Warnings = warnings
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private async Task<StableReviewCandidate?> ResolveStableReviewCandidateAsync(
        string? itemId,
        string? stableReviewCandidateId,
        CancellationToken cancellationToken)
    {
        if (_stableReviewCandidateStore is null)
        {
            return null;
        }

        foreach (var id in new[] { stableReviewCandidateId, itemId }
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = await _stableReviewCandidateStore.GetAsync(id!, cancellationToken)
                .ConfigureAwait(false);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<StableReviewRecord>> ResolveStableReviewHistoryAsync(
        string? stableReviewCandidateId,
        CancellationToken cancellationToken)
    {
        if (_stableReviewCandidateStore is null || string.IsNullOrWhiteSpace(stableReviewCandidateId))
        {
            return Array.Empty<StableReviewRecord>();
        }

        return await _stableReviewCandidateStore.QueryReviewsAsync(stableReviewCandidateId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ContextMemoryItem?> ResolveTargetMemoryAsync(
        string? workspaceId,
        string? collectionId,
        string targetItemId,
        string targetKind,
        CancellationToken cancellationToken)
    {
        if (_memoryStore is null
            || string.IsNullOrWhiteSpace(workspaceId)
            || string.IsNullOrWhiteSpace(targetItemId)
            || string.Equals(targetKind, "constraint", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            var direct = await _memoryStore.GetAsync(
                workspaceId,
                collectionId,
                targetItemId,
                cancellationToken).ConfigureAwait(false);
            if (direct is not null)
            {
                return direct;
            }
        }

        var results = await _memoryStore.QueryAsync(new ContextMemoryQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);
        return results.FirstOrDefault(item => string.Equals(item.Id, targetItemId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ContextConstraint?> ResolveTargetConstraintAsync(
        string? workspaceId,
        string? collectionId,
        string targetItemId,
        string targetKind,
        CancellationToken cancellationToken)
    {
        if (_constraintStore is null
            || string.IsNullOrWhiteSpace(workspaceId)
            || string.IsNullOrWhiteSpace(targetItemId))
        {
            return null;
        }

        if (!string.Equals(targetKind, "constraint", StringComparison.OrdinalIgnoreCase)
            && !targetItemId.StartsWith("stable:constraint:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var constraints = await _constraintStore.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);
        return constraints.FirstOrDefault(item => string.Equals(item.Id, targetItemId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ShortTermPromotionCandidate?> ResolvePromotionCandidateAsync(
        string? sourcePromotionCandidateId,
        CancellationToken cancellationToken)
    {
        if (_promotionCandidateStore is null || string.IsNullOrWhiteSpace(sourcePromotionCandidateId))
        {
            return null;
        }

        return await _promotionCandidateStore.GetAsync(sourcePromotionCandidateId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<PromotionCandidateReviewRecord>> ResolvePromotionReviewHistoryAsync(
        string? sourcePromotionCandidateId,
        CancellationToken cancellationToken)
    {
        if (_promotionCandidateStore is null || string.IsNullOrWhiteSpace(sourcePromotionCandidateId))
        {
            return Array.Empty<PromotionCandidateReviewRecord>();
        }

        return await _promotionCandidateStore.QueryReviewsAsync(sourcePromotionCandidateId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ContextLearningCase?> ResolveLearningCaseAsync(
        string? sourceLearningCaseId,
        CancellationToken cancellationToken)
    {
        if (_learningStore is null || string.IsNullOrWhiteSpace(sourceLearningCaseId))
        {
            return null;
        }

        return await _learningStore.GetCaseAsync(sourceLearningCaseId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<PromotionFeedbackSignal?> ResolveFeedbackAsync(
        string? sourceFeedbackId,
        ShortTermPromotionCandidate? promotionCandidate,
        StableReviewCandidate? stableReviewCandidate,
        ContextLearningCase? learningCase,
        string? workspaceId,
        string? collectionId,
        CancellationToken cancellationToken)
    {
        if (_learningStore is null)
        {
            return null;
        }

        var query = new PromotionFeedbackSignalQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            CandidateId = promotionCandidate?.CandidateId ?? stableReviewCandidate?.SourceCandidateId,
            Limit = int.MaxValue
        };
        var feedback = await _learningStore.QueryFeedbackAsync(query, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(sourceFeedbackId))
        {
            var match = feedback.FirstOrDefault(item => string.Equals(item.FeedbackId, sourceFeedbackId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }

            var allScoped = await _learningStore.QueryFeedbackAsync(new PromotionFeedbackSignalQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Limit = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            return allScoped.FirstOrDefault(item => string.Equals(item.FeedbackId, sourceFeedbackId, StringComparison.OrdinalIgnoreCase));
        }

        return feedback.FirstOrDefault(item => string.Equals(item.CreatedTargetItemId, stableReviewCandidate?.SourceTargetItemId, StringComparison.OrdinalIgnoreCase))
            ?? feedback.FirstOrDefault(item => IsFeedbackSource(learningCase) && string.Equals(item.FeedbackId, learningCase!.SourceId, StringComparison.OrdinalIgnoreCase))
            ?? feedback.FirstOrDefault(item => string.Equals(item.Action, "Accepted", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ShortTermWorkingItem?> ResolveSourceWorkingItemAsync(
        string? workspaceId,
        string? collectionId,
        string? sessionId,
        string? sourceWorkingItemId,
        CancellationToken cancellationToken)
    {
        if (_shortTermMemoryStore is null
            || string.IsNullOrWhiteSpace(workspaceId)
            || string.IsNullOrWhiteSpace(collectionId)
            || string.IsNullOrWhiteSpace(sourceWorkingItemId))
        {
            return null;
        }

        var active = await _shortTermMemoryStore.GetWorkingItemAsync(
            workspaceId,
            collectionId,
            sourceWorkingItemId,
            cancellationToken).ConfigureAwait(false);
        if (active is not null)
        {
            return active;
        }

        var archived = await _shortTermMemoryStore.QueryArchivedWorkingItemsAsync(new ShortTermWorkingItemQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SessionId = sessionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);
        return archived.FirstOrDefault(item => string.Equals(item.ItemId, sourceWorkingItemId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task AddStableDiagnosticsAsync(
        string targetItemId,
        ContextMemoryItem? targetMemory,
        ContextConstraint? targetConstraint,
        StableReviewCandidate? stableReviewCandidate,
        ShortTermPromotionCandidate? promotionCandidate,
        string? workspaceId,
        string? collectionId,
        ICollection<StableDiagnosticWarning> diagnostics,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        if (stableReviewCandidate is null || promotionCandidate is null || string.IsNullOrWhiteSpace(workspaceId))
        {
            return;
        }

        var duplicates = await ResolveDuplicateStableIdsAsync(
            targetItemId,
            targetConstraint is not null || string.Equals(stableReviewCandidate.SuggestedStableTarget, "StableConstraint", StringComparison.OrdinalIgnoreCase),
            stableReviewCandidate,
            promotionCandidate,
            workspaceId,
            collectionId,
            cancellationToken).ConfigureAwait(false);
        if (duplicates.Count > 0)
        {
            diagnostics.Add(new StableDiagnosticWarning
            {
                Code = "DuplicateStable",
                Message = $"Found duplicate stable targets: {string.Join(", ", duplicates)}.",
                TargetItemId = targetItemId,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["duplicateIds"] = string.Join(",", duplicates)
                }
            });
            warnings.Add("duplicate stable warning.");
        }

        var metadata = ResolveTargetMetadata(targetMemory, targetConstraint);
        if (ContainsConflictSignal(metadata)
            || stableReviewCandidate.RiskFlags.Any(flag => flag.Contains("conflict", StringComparison.OrdinalIgnoreCase)
                || flag.Contains("scope_mismatch", StringComparison.OrdinalIgnoreCase)))
        {
            diagnostics.Add(new StableDiagnosticWarning
            {
                Code = "PossibleConflict",
                Message = "Target or stable review candidate carries possible conflict signals.",
                TargetItemId = targetItemId
            });
            warnings.Add("possible conflict warning.");
        }
    }

    private async Task<IReadOnlyList<string>> ResolveDuplicateStableIdsAsync(
        string targetItemId,
        bool constraintTarget,
        StableReviewCandidate stableReviewCandidate,
        ShortTermPromotionCandidate promotionCandidate,
        string workspaceId,
        string? collectionId,
        CancellationToken cancellationToken)
    {
        if (constraintTarget)
        {
            if (_constraintStore is null)
            {
                return Array.Empty<string>();
            }

            var constraints = await _constraintStore.QueryAsync(new ContextConstraintQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Status = ContextMemoryStatus.Stable,
                Take = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            return constraints
                .Where(item => !string.Equals(item.Id, targetItemId, StringComparison.OrdinalIgnoreCase))
                .Where(item => ReferencesSource(item.SourceRefs, item.Metadata, stableReviewCandidate, promotionCandidate))
                .Select(item => item.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        if (_memoryStore is null)
        {
            return Array.Empty<string>();
        }

        var memory = await _memoryStore.QueryAsync(new ContextMemoryQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);
        return memory
            .Where(item => !string.Equals(item.Id, targetItemId, StringComparison.OrdinalIgnoreCase))
            .Where(item => ReferencesSource(item.SourceRefs, item.Metadata, stableReviewCandidate, promotionCandidate))
            .Select(item => item.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddRequiredMetadataDiagnostics(
        IReadOnlyDictionary<string, string> metadata,
        string targetItemId,
        ICollection<string> missingLinks,
        ICollection<string> warnings,
        ICollection<StableDiagnosticWarning> diagnostics)
    {
        foreach (var key in new[]
        {
            "sourceStableReviewCandidateId",
            "sourcePromotionCandidateId",
            "sourceLearningCaseId",
            "sourceWorkingItemId",
            "sourceFeedbackId",
            "evidenceRefs",
            "reviewer",
            "reviewReason",
            "policyVersion",
            "createdFrom"
        })
        {
            if (!metadata.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                AddMissing(key, string.Empty, missingLinks, warnings, diagnostics, targetItemId);
            }
        }

        if (metadata.TryGetValue("createdFrom", out var createdFrom)
            && !string.Equals(createdFrom, "stable_review_accept", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new StableDiagnosticWarning
            {
                Code = "UnexpectedCreatedFrom",
                Message = $"createdFrom is '{createdFrom}', expected stable_review_accept.",
                TargetItemId = targetItemId
            });
        }
    }

    private static void AddMissing(
        string linkName,
        string? linkId,
        ICollection<string> missingLinks,
        ICollection<string> warnings,
        ICollection<StableDiagnosticWarning> diagnostics,
        string? targetItemId)
    {
        var missing = string.IsNullOrWhiteSpace(linkId) ? linkName : $"{linkName}:{linkId}";
        missingLinks.Add(missing);
        warnings.Add($"missing source link: {missing}");
        diagnostics.Add(new StableDiagnosticWarning
        {
            Code = "MissingSourceLink",
            Message = $"Missing source link: {missing}.",
            TargetItemId = targetItemId
        });
    }

    private static IReadOnlyList<string> ResolveEvidenceRefs(
        IReadOnlyDictionary<string, string> metadata,
        ContextMemoryItem? targetMemory,
        ContextConstraint? targetConstraint,
        StableReviewCandidate? stableReviewCandidate,
        ShortTermPromotionCandidate? promotionCandidate,
        PromotionFeedbackSignal? feedback,
        ContextLearningCase? learningCase)
    {
        return ReadMetadataList(metadata, "evidenceRefs")
            .Concat(targetMemory?.SourceRefs ?? Array.Empty<string>())
            .Concat(targetConstraint?.SourceRefs ?? Array.Empty<string>())
            .Concat(stableReviewCandidate?.EvidenceRefs ?? Array.Empty<string>())
            .Concat(promotionCandidate?.EvidenceRefs ?? Array.Empty<string>())
            .Concat(feedback?.EvidenceRefs ?? Array.Empty<string>())
            .Concat(learningCase?.EvidenceRefs ?? Array.Empty<string>())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool ReferencesSource(
        IReadOnlyList<string> sourceRefs,
        IReadOnlyDictionary<string, string> metadata,
        StableReviewCandidate stableReviewCandidate,
        ShortTermPromotionCandidate promotionCandidate)
    {
        return sourceRefs.Contains(stableReviewCandidate.StableReviewCandidateId, StringComparer.OrdinalIgnoreCase)
            || sourceRefs.Contains(stableReviewCandidate.SourceCandidateId, StringComparer.OrdinalIgnoreCase)
            || sourceRefs.Contains(stableReviewCandidate.SourceTargetItemId, StringComparer.OrdinalIgnoreCase)
            || sourceRefs.Contains(promotionCandidate.CandidateId, StringComparer.OrdinalIgnoreCase)
            || MatchesMetadata(metadata, "sourceStableReviewCandidateId", stableReviewCandidate.StableReviewCandidateId)
            || MatchesMetadata(metadata, "sourcePromotionCandidateId", promotionCandidate.CandidateId)
            || MatchesMetadata(metadata, "sourceCandidateId", promotionCandidate.CandidateId)
            || MatchesMetadata(metadata, "sourceTargetItemId", stableReviewCandidate.SourceTargetItemId)
            || MatchesMetadata(metadata, "stableReviewSourceTargetItemId", stableReviewCandidate.SourceTargetItemId);
    }

    private static bool ContainsConflictSignal(IReadOnlyDictionary<string, string> metadata)
    {
        foreach (var key in new[] { "conflict", "possibleConflict", "lifecycleConflict", "riskFlags" })
        {
            if (metadata.TryGetValue(key, out var value)
                && !string.IsNullOrWhiteSpace(value)
                && (IsTruthy(value) || value.Contains("conflict", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTruthy(string value)
    {
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string> ResolveTargetMetadata(
        ContextMemoryItem? targetMemory,
        ContextConstraint? targetConstraint)
    {
        return targetMemory?.Metadata ?? targetConstraint?.Metadata ?? EmptyMetadata;
    }

    private static string ResolveTargetItemId(
        string itemId,
        StableReviewCandidate? stableReviewCandidate,
        IReadOnlyList<StableReviewRecord> stableReviewHistory)
    {
        if (stableReviewCandidate is null
            || !string.Equals(itemId, stableReviewCandidate.StableReviewCandidateId, StringComparison.OrdinalIgnoreCase))
        {
            return itemId;
        }

        return FirstNonEmpty(
            ReadMetadata(stableReviewCandidate.Metadata, "createdStableTargetItemId"),
            stableReviewHistory.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.StableTargetItemId))?.StableTargetItemId,
            BuildStableTargetItemId(stableReviewCandidate)) ?? itemId;
    }

    private static string ResolveTargetKind(
        string itemId,
        StableReviewCandidate? stableReviewCandidate,
        IReadOnlyList<StableReviewRecord> stableReviewHistory)
    {
        if (itemId.StartsWith("stable:constraint:", StringComparison.OrdinalIgnoreCase))
        {
            return "constraint";
        }

        var reviewKind = stableReviewHistory.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.StableTargetItemKind))?.StableTargetItemKind;
        if (!string.IsNullOrWhiteSpace(reviewKind))
        {
            return reviewKind;
        }

        if (string.Equals(stableReviewCandidate?.SuggestedStableTarget, "StableConstraint", StringComparison.OrdinalIgnoreCase))
        {
            return "constraint";
        }

        return "memory";
    }

    private static string? InferStableReviewCandidateId(string itemId)
    {
        foreach (var prefix in new[] { "stable:mem:", "stable:decision:", "stable:constraint:" })
        {
            if (itemId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return itemId[prefix.Length..];
            }
        }

        return itemId.StartsWith("src-", StringComparison.OrdinalIgnoreCase) ? itemId : null;
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

    private static bool IsFeedbackSource(ContextLearningCase? learningCase)
    {
        return learningCase is not null
            && (string.Equals(learningCase.SourceType, "PromotionFeedbackSignal", StringComparison.OrdinalIgnoreCase)
                || string.Equals(learningCase.SourceKind, "PromotionFeedbackSignal", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadMetadata(
        IReadOnlyDictionary<string, string>? metadata,
        params string[] keys)
    {
        if (metadata is null)
        {
            return null;
        }

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

    private static IReadOnlyList<string> ReadMetadataList(
        IReadOnlyDictionary<string, string> metadata,
        params string[] keys)
    {
        var value = ReadMetadata(metadata, keys);
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
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

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
