using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>构建 StableMemory / StableConstraint / DecisionRecord 的只读治理快照、解释和诊断。</summary>
public sealed class StableMemoryGovernanceService
{
    private readonly IMemoryStore? _memoryStore;
    private readonly IConstraintStore? _constraintStore;
    private readonly IGlobalContextStore? _globalContextStore;
    private readonly IRelationStore? _relationStore;
    private readonly ContextProvenanceService? _provenanceService;

    public StableMemoryGovernanceService(
        IMemoryStore? memoryStore,
        IConstraintStore? constraintStore,
        IGlobalContextStore? globalContextStore,
        IRelationStore? relationStore,
        ContextProvenanceService? provenanceService)
    {
        _memoryStore = memoryStore;
        _constraintStore = constraintStore;
        _globalContextStore = globalContextStore;
        _relationStore = relationStore;
        _provenanceService = provenanceService;
    }

    public async Task<StableMemorySnapshot> GetSnapshotAsync(
        string workspaceId,
        string? collectionId = null,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var warnings = new List<string>();
        var records = await QueryRecordsAsync(workspaceId, collectionId, warnings, cancellationToken).ConfigureAwait(false);
        var diagnostics = await BuildDiagnosticsAsync(records, workspaceId, collectionId, warnings, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        return new StableMemorySnapshot
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            CreatedAt = now,
            StableMemoryCount = records.Count(item => item.StableKind == StableMemoryKinds.StableMemory),
            StableConstraintCount = records.Count(item => item.StableKind == StableMemoryKinds.StableConstraint),
            DecisionRecordCount = records.Count(item => item.StableKind == StableMemoryKinds.DecisionRecord),
            GlobalMemoryCount = records.Count(item => item.StableKind == StableMemoryKinds.GlobalMemory),
            ActiveCount = records.Count(IsActive),
            SupersededCount = records.Count(item => string.Equals(item.Lifecycle, StableMemoryLifecycle.Superseded, StringComparison.OrdinalIgnoreCase)),
            DeprecatedCount = records.Count(item => item.Status == ContextMemoryStatus.Deprecated
                || string.Equals(item.Lifecycle, StableMemoryLifecycle.Deprecated, StringComparison.OrdinalIgnoreCase)),
            RejectedCount = records.Count(item => item.Status == ContextMemoryStatus.Rejected
                || string.Equals(item.Lifecycle, StableMemoryLifecycle.Rejected, StringComparison.OrdinalIgnoreCase)),
            MissingProvenanceCount = diagnostics.Count(item => item.DiagnosticType == StableMemoryDiagnosticTypes.MissingProvenance),
            DuplicateCandidateCount = diagnostics.Count(item => item.DiagnosticType == StableMemoryDiagnosticTypes.DuplicateStableMemory),
            ConflictCandidateCount = diagnostics.Count(item => item.DiagnosticType == StableMemoryDiagnosticTypes.PossibleConflict),
            WeakEvidenceCount = diagnostics.Count(item => item.DiagnosticType == StableMemoryDiagnosticTypes.MissingEvidenceRefs),
            RecentStableItems = records
                .OrderByDescending(item => item.UpdatedAt == default ? item.CreatedAt : item.UpdatedAt)
                .Take(take > 0 ? take : 20)
                .ToArray(),
            Warnings = warnings.ToArray()
        };
    }

    public async Task<StableMemoryDiagnosticsReport> GetDiagnosticsAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var warnings = new List<string>();
        var records = await QueryRecordsAsync(workspaceId, collectionId, warnings, cancellationToken).ConfigureAwait(false);
        var diagnostics = await BuildDiagnosticsAsync(records, workspaceId, collectionId, warnings, cancellationToken).ConfigureAwait(false);

        return new StableMemoryDiagnosticsReport
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            CreatedAt = DateTimeOffset.UtcNow,
            DiagnosticCount = diagnostics.Count,
            DuplicateStableMemoryCount = diagnostics.Count(item => item.DiagnosticType == StableMemoryDiagnosticTypes.DuplicateStableMemory),
            PossibleConflictCount = diagnostics.Count(item => item.DiagnosticType == StableMemoryDiagnosticTypes.PossibleConflict),
            MissingProvenanceCount = diagnostics.Count(item => item.DiagnosticType == StableMemoryDiagnosticTypes.MissingProvenance),
            MissingEvidenceRefsCount = diagnostics.Count(item => item.DiagnosticType == StableMemoryDiagnosticTypes.MissingEvidenceRefs),
            StableWithoutReviewSourceCount = diagnostics.Count(item => item.DiagnosticType == StableMemoryDiagnosticTypes.StableWithoutReviewSource),
            StableConstraintWithoutScopeCount = diagnostics.Count(item => item.DiagnosticType == StableMemoryDiagnosticTypes.StableConstraintWithoutScope),
            DecisionRecordWithoutSourceCount = diagnostics.Count(item => item.DiagnosticType == StableMemoryDiagnosticTypes.DecisionRecordWithoutSource),
            DeprecatedStillActiveCount = diagnostics.Count(item => item.DiagnosticType == StableMemoryDiagnosticTypes.DeprecatedStillActive),
            SupersededWithoutReplacementCount = diagnostics.Count(item => item.DiagnosticType == StableMemoryDiagnosticTypes.SupersededWithoutReplacement),
            GlobalMemoryScopeRiskCount = diagnostics.Count(item => item.DiagnosticType == StableMemoryDiagnosticTypes.GlobalMemoryScopeRisk),
            SupersededWithoutRelationCount = diagnostics.Count(item => item.DiagnosticType == StableMemoryDiagnosticTypes.SupersededWithoutRelation),
            MetadataRelationMismatchCount = diagnostics.Count(item => item.DiagnosticType == StableMemoryDiagnosticTypes.MetadataRelationMismatch),
            BrokenReplacementLinkCount = diagnostics.Count(item => item.DiagnosticType == StableMemoryDiagnosticTypes.BrokenReplacementLink),
            ReplacementTargetMissingCount = diagnostics.Count(item => item.DiagnosticType == StableMemoryDiagnosticTypes.ReplacementTargetMissing),
            ReplacementTargetInactiveCount = diagnostics.Count(item => item.DiagnosticType == StableMemoryDiagnosticTypes.ReplacementTargetInactive),
            ReplacementCycleCount = diagnostics.Count(item => item.DiagnosticType == StableMemoryDiagnosticTypes.ReplacementCycle),
            MultipleActiveReplacementsCount = diagnostics.Count(item => item.DiagnosticType == StableMemoryDiagnosticTypes.MultipleActiveReplacements),
            ScopeMismatchInReplacementCount = diagnostics.Count(item => item.DiagnosticType == StableMemoryDiagnosticTypes.ScopeMismatchInReplacement),
            Diagnostics = diagnostics
        };
    }

    public async Task<StableMemoryExplanation?> ExplainAsync(
        string itemId,
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var warnings = new List<string>();
        var records = await QueryRecordsAsync(workspaceId, collectionId, warnings, cancellationToken).ConfigureAwait(false);
        var item = records.FirstOrDefault(record => string.Equals(record.Id, itemId, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return null;
        }

        ContextProvenanceResponse? provenance = null;
        if (_provenanceService is not null && item.StableKind != StableMemoryKinds.GlobalMemory)
        {
            provenance = await _provenanceService.GetAsync(item.Id, workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
            if (provenance is null)
            {
                warnings.Add("provenance chain is missing or target is not linked to stable review accept.");
            }
        }
        else if (item.StableKind == StableMemoryKinds.GlobalMemory)
        {
            warnings.Add("global memory currently has metadata-only provenance.");
        }

        var diagnostics = (await BuildDiagnosticsAsync(records, workspaceId, collectionId, warnings, cancellationToken).ConfigureAwait(false))
            .Where(diagnostic => string.Equals(diagnostic.StableItemId, item.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return new StableMemoryExplanation
        {
            StableItemId = item.Id,
            StableItem = item,
            Provenance = provenance,
            EvidenceRefs = item.EvidenceRefs,
            Diagnostics = diagnostics,
            Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    public async Task<StableReplacementChainResponse?> GetReplacementChainAsync(
        string itemId,
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var warnings = new List<string>();
        var records = await QueryRecordsAsync(workspaceId, collectionId, warnings, cancellationToken).ConfigureAwait(false);
        var current = records.FirstOrDefault(record => string.Equals(record.Id, itemId, StringComparison.OrdinalIgnoreCase));
        if (current is null)
        {
            return null;
        }

        var relations = await QueryReplacementRelationsAsync(workspaceId, collectionId, warnings, cancellationToken).ConfigureAwait(false);
        var edges = BuildReplacementEdges(relations);
        var previousIds = TraverseReplacementGraph(itemId, edges, forward: false, warnings);
        var nextIds = TraverseReplacementGraph(itemId, edges, forward: true, warnings);
        var chainIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { itemId };
        foreach (var id in previousIds.Concat(nextIds))
        {
            chainIds.Add(id);
        }

        var recordById = records.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var id in chainIds)
        {
            if (!recordById.ContainsKey(id))
            {
                warnings.Add($"replacement chain references missing stable item: {id}");
            }
        }

        var previousItems = previousIds
            .Reverse()
            .Where(recordById.ContainsKey)
            .Select(id => recordById[id])
            .ToArray();
        var nextItems = nextIds
            .Where(recordById.ContainsKey)
            .Select(id => recordById[id])
            .ToArray();
        var rootId = previousIds.LastOrDefault() ?? itemId;
        var latestId = ResolveLatestItemId(itemId, nextIds, recordById);
        var chainRelations = relations
            .Where(relation => chainIds.Contains(relation.SourceId) || chainIds.Contains(relation.TargetId))
            .OrderBy(relation => relation.SourceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(relation => relation.RelationType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(relation => relation.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new StableReplacementChainResponse
        {
            ItemId = itemId,
            CurrentItem = current,
            PreviousItems = previousItems,
            NextItems = nextItems,
            RootItem = recordById.TryGetValue(rootId, out var root) ? root : current,
            LatestItem = recordById.TryGetValue(latestId, out var latest) ? latest : current,
            Relations = chainRelations,
            Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private async Task<IReadOnlyList<StableMemoryRecord>> QueryRecordsAsync(
        string workspaceId,
        string? collectionId,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var records = new List<StableMemoryRecord>();

        if (_memoryStore is not null)
        {
            var memory = await _memoryStore.QueryAsync(new ContextMemoryQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Layer = ContextMemoryLayer.Stable,
                Take = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            records.AddRange(memory.Select(ToRecord));
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
                Take = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            records.AddRange(constraints
                .Where(static item => item.Status != ContextMemoryStatus.Candidate)
                .Select(ToRecord));
        }
        else
        {
            warnings.Add("constraint store is not registered.");
        }

        if (_globalContextStore is not null)
        {
            var global = await _globalContextStore.QueryAsync(new ContextGlobalQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Take = int.MaxValue
            }, cancellationToken).ConfigureAwait(false);
            records.AddRange(global.Select(ToRecord));
        }
        else
        {
            warnings.Add("global context store is not registered.");
        }

        return records
            .OrderByDescending(item => item.UpdatedAt == default ? item.CreatedAt : item.UpdatedAt)
            .ToArray();
    }

    private static StableMemoryRecord ToRecord(ContextMemoryItem item)
    {
        var stableKind = IsDecision(item.Type, item.Metadata)
            ? StableMemoryKinds.DecisionRecord
            : StableMemoryKinds.StableMemory;
        return new StableMemoryRecord
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = ReadMetadata(item.Metadata, "sessionId"),
            StableKind = stableKind,
            Type = item.Type,
            Title = ResolveTitle(item.Content),
            Summary = ResolveSummary(item.Content),
            Content = item.Content,
            Status = item.Status,
            Lifecycle = ResolveLifecycle(item.Status, item.Metadata),
            Importance = item.Importance,
            Confidence = item.Confidence,
            SourceRefs = item.SourceRefs,
            EvidenceRefs = ResolveEvidenceRefs(item.SourceRefs, item.Metadata),
            StableReviewCandidateId = ReadMetadata(item.Metadata, "sourceStableReviewCandidateId", "stableReviewCandidateId"),
            PromotionCandidateId = ReadMetadata(item.Metadata, "sourcePromotionCandidateId", "sourceCandidateId"),
            LearningCaseId = ReadMetadata(item.Metadata, "sourceLearningCaseId"),
            FeedbackId = ReadMetadata(item.Metadata, "sourceFeedbackId", "feedbackId"),
            WorkingItemId = ReadMetadata(item.Metadata, "sourceWorkingItemId"),
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static StableMemoryRecord ToRecord(ContextConstraint item)
    {
        return new StableMemoryRecord
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = ReadMetadata(item.Metadata, "sessionId"),
            StableKind = StableMemoryKinds.StableConstraint,
            Type = item.Level.ToString(),
            Title = ResolveTitle(item.Content),
            Summary = ResolveSummary(item.Content),
            Content = item.Content,
            Status = item.Status,
            Lifecycle = ResolveLifecycle(item.Status, item.Metadata),
            Importance = item.Level == ConstraintLevel.Hard ? 1.0 : 0.8,
            Confidence = item.Confidence,
            Scope = item.Scope,
            ConstraintLevel = item.Level,
            SourceRefs = item.SourceRefs,
            EvidenceRefs = ResolveEvidenceRefs(item.SourceRefs, item.Metadata),
            StableReviewCandidateId = ReadMetadata(item.Metadata, "sourceStableReviewCandidateId", "stableReviewCandidateId"),
            PromotionCandidateId = ReadMetadata(item.Metadata, "sourcePromotionCandidateId", "sourceCandidateId"),
            LearningCaseId = ReadMetadata(item.Metadata, "sourceLearningCaseId"),
            FeedbackId = ReadMetadata(item.Metadata, "sourceFeedbackId", "feedbackId"),
            WorkingItemId = ReadMetadata(item.Metadata, "sourceWorkingItemId"),
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static StableMemoryRecord ToRecord(ContextGlobalItem item)
    {
        var status = Enum.TryParse<ContextMemoryStatus>(ReadMetadata(item.Metadata, "status"), ignoreCase: true, out var parsedStatus)
            ? parsedStatus
            : ContextMemoryStatus.Stable;
        return new StableMemoryRecord
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            StableKind = StableMemoryKinds.GlobalMemory,
            Type = item.Type,
            Title = ResolveTitle(item.Content),
            Summary = ResolveSummary(item.Content),
            Content = item.Content,
            Status = status,
            Lifecycle = ResolveLifecycle(status, item.Metadata),
            Importance = item.Importance,
            Confidence = ParseDouble(ReadMetadata(item.Metadata, "confidence")),
            Scope = item.Scope,
            SourceRefs = item.SourceRefs,
            EvidenceRefs = ResolveEvidenceRefs(item.SourceRefs, item.Metadata),
            StableReviewCandidateId = ReadMetadata(item.Metadata, "sourceStableReviewCandidateId", "stableReviewCandidateId"),
            PromotionCandidateId = ReadMetadata(item.Metadata, "sourcePromotionCandidateId", "sourceCandidateId"),
            LearningCaseId = ReadMetadata(item.Metadata, "sourceLearningCaseId"),
            FeedbackId = ReadMetadata(item.Metadata, "sourceFeedbackId", "feedbackId"),
            WorkingItemId = ReadMetadata(item.Metadata, "sourceWorkingItemId"),
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private async Task<IReadOnlyList<StableMemoryDiagnostic>> BuildDiagnosticsAsync(
        IReadOnlyList<StableMemoryRecord> records,
        string workspaceId,
        string? collectionId,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var relations = await QueryReplacementRelationsAsync(workspaceId, collectionId, warnings, cancellationToken)
            .ConfigureAwait(false);
        return BuildDiagnostics(records, relations);
    }

    private async Task<IReadOnlyList<ContextRelation>> QueryReplacementRelationsAsync(
        string workspaceId,
        string? collectionId,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (_relationStore is null)
        {
            warnings.Add("relation store is not registered; replacement relation diagnostics are incomplete.");
            return Array.Empty<ContextRelation>();
        }

        var relations = await _relationStore.QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);

        return relations
            .Where(IsReplacementRelation)
            .ToArray();
    }

    private static IReadOnlyList<StableMemoryDiagnostic> BuildDiagnostics(
        IReadOnlyList<StableMemoryRecord> records,
        IReadOnlyList<ContextRelation> replacementRelations)
    {
        var diagnostics = new List<StableMemoryDiagnostic>();
        var recordById = records.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var edges = BuildReplacementEdges(replacementRelations);
        foreach (var item in records)
        {
            if (!HasProvenance(item))
            {
                diagnostics.Add(BuildDiagnostic(
                    item,
                    StableMemoryDiagnosticTypes.MissingProvenance,
                    "High",
                    "Stable item has no stable review, promotion, feedback, learning, working item, or source refs."));
            }

            if (item.EvidenceRefs.Count == 0)
            {
                diagnostics.Add(BuildDiagnostic(
                    item,
                    StableMemoryDiagnosticTypes.MissingEvidenceRefs,
                    "High",
                    "Stable item has no evidence refs."));
            }

            if (item.StableKind is StableMemoryKinds.StableMemory or StableMemoryKinds.StableConstraint or StableMemoryKinds.DecisionRecord
                && string.IsNullOrWhiteSpace(item.StableReviewCandidateId)
                && IsCreatedFromStableReview(item))
            {
                diagnostics.Add(BuildDiagnostic(
                    item,
                    StableMemoryDiagnosticTypes.StableWithoutReviewSource,
                    "High",
                    "Stable review accept metadata is present but sourceStableReviewCandidateId is missing."));
            }

            if (item.StableKind == StableMemoryKinds.StableConstraint
                && (item.Scope is null
                    || (item.Scope == ContextScope.Collection && string.IsNullOrWhiteSpace(item.CollectionId))
                    || string.Equals(ReadMetadata(item.Metadata, "scopeMissing"), "true", StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Add(BuildDiagnostic(
                    item,
                    StableMemoryDiagnosticTypes.StableConstraintWithoutScope,
                    "High",
                    "Stable constraint is missing an explicit usable scope."));
            }

            if (item.StableKind == StableMemoryKinds.DecisionRecord
                && string.IsNullOrWhiteSpace(item.PromotionCandidateId)
                && string.IsNullOrWhiteSpace(item.WorkingItemId)
                && item.SourceRefs.Count == 0)
            {
                diagnostics.Add(BuildDiagnostic(
                    item,
                    StableMemoryDiagnosticTypes.DecisionRecordWithoutSource,
                    "High",
                    "Decision record does not preserve a source promotion, working item, or source refs."));
            }

            if (IsDeprecatedStillActive(item))
            {
                diagnostics.Add(BuildDiagnostic(
                    item,
                    StableMemoryDiagnosticTypes.DeprecatedStillActive,
                    "High",
                    "Stable item is marked deprecated while still carrying active/current status metadata."));
            }

            if (string.Equals(item.Lifecycle, StableMemoryLifecycle.Superseded, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(ReadMetadata(item.Metadata, "supersededBy", "replacementId", "replacementStableItemId")))
            {
                diagnostics.Add(BuildDiagnostic(
                    item,
                    StableMemoryDiagnosticTypes.SupersededWithoutReplacement,
                    "Medium",
                    "Stable item is superseded but no replacement is recorded."));
            }

            var metadataSupersededBy = ReadMetadataList(item.Metadata, "supersededBy", "replacementId", "replacementStableItemId");
            var relationSupersededBy = edges
                .Where(edge => string.Equals(edge.OldItemId, item.Id, StringComparison.OrdinalIgnoreCase))
                .Select(edge => edge.NewItemId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (string.Equals(item.Lifecycle, StableMemoryLifecycle.Superseded, StringComparison.OrdinalIgnoreCase)
                && metadataSupersededBy.Count > 0
                && relationSupersededBy.Length == 0)
            {
                diagnostics.Add(BuildDiagnostic(
                    item,
                    StableMemoryDiagnosticTypes.SupersededWithoutRelation,
                    "High",
                    "Stable item is superseded in metadata but has no superseded_by / replaces relation.",
                    metadataSupersededBy));
            }

            if (metadataSupersededBy.Count > 0)
            {
                foreach (var replacementId in metadataSupersededBy)
                {
                    if (!recordById.TryGetValue(replacementId, out var replacement))
                    {
                        diagnostics.Add(BuildDiagnostic(
                            item,
                            StableMemoryDiagnosticTypes.ReplacementTargetMissing,
                            "High",
                            $"Replacement target is missing: {replacementId}",
                            [replacementId]));
                        continue;
                    }

                    if (!IsActive(replacement))
                    {
                        diagnostics.Add(BuildDiagnostic(
                            item,
                            StableMemoryDiagnosticTypes.ReplacementTargetInactive,
                            "High",
                            $"Replacement target is not active/current: {replacementId}",
                            [replacementId]));
                    }
                }

                if (relationSupersededBy.Length > 0
                    && !metadataSupersededBy.Intersect(relationSupersededBy, StringComparer.OrdinalIgnoreCase).Any())
                {
                    diagnostics.Add(BuildDiagnostic(
                        item,
                        StableMemoryDiagnosticTypes.MetadataRelationMismatch,
                        "High",
                        "supersededBy metadata does not match replacement relations.",
                        metadataSupersededBy.Concat(relationSupersededBy).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
                }
            }

            var metadataReplaces = ReadMetadataList(item.Metadata, "replaces", "replacesItemId", "replacedItemId");
            var relationReplaces = edges
                .Where(edge => string.Equals(edge.NewItemId, item.Id, StringComparison.OrdinalIgnoreCase))
                .Select(edge => edge.OldItemId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (metadataReplaces.Count > 0
                && relationReplaces.Length > 0
                && !metadataReplaces.Intersect(relationReplaces, StringComparer.OrdinalIgnoreCase).Any())
            {
                diagnostics.Add(BuildDiagnostic(
                    item,
                    StableMemoryDiagnosticTypes.MetadataRelationMismatch,
                    "High",
                    "replaces metadata does not match replacement relations.",
                    metadataReplaces.Concat(relationReplaces).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
            }

            var activeReplacementTargets = relationSupersededBy
                .Where(id => recordById.TryGetValue(id, out var target) && IsActive(target))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (activeReplacementTargets.Length > 1)
            {
                diagnostics.Add(BuildDiagnostic(
                    item,
                    StableMemoryDiagnosticTypes.MultipleActiveReplacements,
                    "High",
                    "Stable item has multiple active replacement targets.",
                    activeReplacementTargets));
            }

            if (item.StableKind == StableMemoryKinds.GlobalMemory
                && (item.Scope is null
                    || item.Scope != ContextScope.Workspace
                    || !string.IsNullOrWhiteSpace(item.CollectionId)
                    || string.Equals(ReadMetadata(item.Metadata, "scopeRisk"), "true", StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Add(BuildDiagnostic(
                    item,
                    StableMemoryDiagnosticTypes.GlobalMemoryScopeRisk,
                    "Medium",
                    "Global memory has collection-specific or unclear scope."));
            }

            if (HasConflictSignal(item))
            {
                diagnostics.Add(BuildDiagnostic(
                    item,
                    StableMemoryDiagnosticTypes.PossibleConflict,
                    "High",
                    "Stable item metadata carries possible conflict signals."));
            }
        }

        foreach (var group in records.GroupBy(BuildDuplicateKey, StringComparer.OrdinalIgnoreCase)
                     .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1))
        {
            var ids = group.Select(item => item.Id).ToArray();
            diagnostics.AddRange(group.Select(item => BuildDiagnostic(
                item,
                StableMemoryDiagnosticTypes.DuplicateStableMemory,
                "Medium",
                "Stable item duplicates another stable item.",
                ids.Where(id => !string.Equals(id, item.Id, StringComparison.OrdinalIgnoreCase)).ToArray())));
        }

        foreach (var edge in edges)
        {
            if (!recordById.TryGetValue(edge.OldItemId, out var oldItem))
            {
                if (recordById.TryGetValue(edge.NewItemId, out var onlyNew))
                {
                    diagnostics.Add(BuildDiagnostic(
                        onlyNew,
                        StableMemoryDiagnosticTypes.ReplacementTargetMissing,
                        "High",
                        $"Replacement relation source is missing: {edge.OldItemId}",
                        [edge.OldItemId]));
                }

                continue;
            }

            if (!recordById.TryGetValue(edge.NewItemId, out var newItem))
            {
                diagnostics.Add(BuildDiagnostic(
                    oldItem,
                    StableMemoryDiagnosticTypes.ReplacementTargetMissing,
                    "High",
                    $"Replacement relation target is missing: {edge.NewItemId}",
                    [edge.NewItemId]));
                continue;
            }

            if (!HasForwardAndReverseReplacementRelations(edge.OldItemId, edge.NewItemId, replacementRelations))
            {
                diagnostics.Add(BuildDiagnostic(
                    oldItem,
                    StableMemoryDiagnosticTypes.BrokenReplacementLink,
                    "High",
                    "Replacement relation is missing the inverse superseded_by / replaces relation.",
                    [edge.NewItemId]));
            }

            if (!string.Equals(oldItem.WorkspaceId, newItem.WorkspaceId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(oldItem.CollectionId ?? string.Empty, newItem.CollectionId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(BuildDiagnostic(
                    oldItem,
                    StableMemoryDiagnosticTypes.ScopeMismatchInReplacement,
                    "High",
                    "Replacement relation crosses workspace or collection scope.",
                    [newItem.Id]));
            }
        }

        foreach (var cycle in FindReplacementCycles(records, edges))
        {
            foreach (var id in cycle)
            {
                if (recordById.TryGetValue(id, out var item))
                {
                    diagnostics.Add(BuildDiagnostic(
                        item,
                        StableMemoryDiagnosticTypes.ReplacementCycle,
                        "High",
                        "Replacement chain contains a cycle.",
                        cycle.Where(other => !string.Equals(other, id, StringComparison.OrdinalIgnoreCase)).ToArray()));
                }
            }
        }

        return diagnostics
            .GroupBy(item => item.DiagnosticId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(item => item.Severity == "High")
            .ThenBy(item => item.StableItemId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DiagnosticType, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static StableMemoryDiagnostic BuildDiagnostic(
        StableMemoryRecord item,
        string type,
        string severity,
        string reason,
        IReadOnlyList<string>? related = null)
    {
        return new StableMemoryDiagnostic
        {
            DiagnosticId = $"smd-{BuildShortHash($"{item.Id}\u001f{type}\u001f{string.Join(',', related ?? Array.Empty<string>())}")}",
            StableItemId = item.Id,
            StableKind = item.StableKind,
            DiagnosticType = type,
            Severity = severity,
            Reason = reason,
            RelatedStableItemIds = related ?? Array.Empty<string>(),
            EvidenceRefs = item.EvidenceRefs,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lifecycle"] = item.Lifecycle,
                ["status"] = item.Status.ToString()
            }
        };
    }

    private static bool IsDecision(string type, IReadOnlyDictionary<string, string> metadata)
    {
        return string.Equals(type, "decision", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ReadMetadata(metadata, "suggestedTargetLayer", "targetLayer"), "DecisionRecord", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ReadMetadata(metadata, "stableTargetKind"), "DecisionRecord", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsActive(StableMemoryRecord item)
    {
        if (item.Status is ContextMemoryStatus.Deprecated or ContextMemoryStatus.Rejected
            || string.Equals(item.Lifecycle, StableMemoryLifecycle.Deprecated, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Lifecycle, StableMemoryLifecycle.Superseded, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Lifecycle, StableMemoryLifecycle.Rejected, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return item.Status is ContextMemoryStatus.Active or ContextMemoryStatus.Verified or ContextMemoryStatus.Stable
            || string.Equals(item.Lifecycle, StableMemoryLifecycle.Active, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Lifecycle, StableMemoryLifecycle.Current, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasProvenance(StableMemoryRecord item)
    {
        return !string.IsNullOrWhiteSpace(item.StableReviewCandidateId)
            || !string.IsNullOrWhiteSpace(item.PromotionCandidateId)
            || !string.IsNullOrWhiteSpace(item.LearningCaseId)
            || !string.IsNullOrWhiteSpace(item.FeedbackId)
            || !string.IsNullOrWhiteSpace(item.WorkingItemId)
            || item.SourceRefs.Count > 0
            || item.Metadata.ContainsKey("createdFrom");
    }

    private static bool IsCreatedFromStableReview(StableMemoryRecord item)
    {
        return string.Equals(ReadMetadata(item.Metadata, "createdFrom"), "stable_review_accept", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeprecatedStillActive(StableMemoryRecord item)
    {
        var activeMetadata = string.Equals(ReadMetadata(item.Metadata, "active"), "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ReadMetadata(item.Metadata, "status"), "Active", StringComparison.OrdinalIgnoreCase);
        return (item.Status == ContextMemoryStatus.Active || activeMetadata)
            && (string.Equals(item.Lifecycle, StableMemoryLifecycle.Deprecated, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ReadMetadata(item.Metadata, "lifecycle"), "Deprecated", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasConflictSignal(StableMemoryRecord item)
    {
        foreach (var key in new[] { "conflict", "possibleConflict", "lifecycleConflict", "conflictWithStableId", "riskFlags" })
        {
            if (item.Metadata.TryGetValue(key, out var value)
                && !string.IsNullOrWhiteSpace(value)
                && (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("conflict", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildDuplicateKey(StableMemoryRecord item)
    {
        var explicitKey = ReadMetadata(item.Metadata, "dedupeKey", "sourceFingerprint");
        if (!string.IsNullOrWhiteSpace(explicitKey))
        {
            return $"{item.StableKind}\u001f{explicitKey}";
        }

        var text = NormalizeText(string.IsNullOrWhiteSpace(item.Content) ? item.Summary : item.Content);
        return string.IsNullOrWhiteSpace(text) ? string.Empty : $"{item.StableKind}\u001f{item.Type}\u001f{text}";
    }

    private static bool IsReplacementRelation(ContextRelation relation)
    {
        return string.Equals(relation.RelationType, ContextRelationTypes.SupersededBy, StringComparison.OrdinalIgnoreCase)
            || string.Equals(relation.RelationType, ContextRelationTypes.Replaces, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ReplacementEdge> BuildReplacementEdges(IReadOnlyList<ContextRelation> relations)
    {
        return relations
            .Where(IsReplacementRelation)
            .Select(relation => string.Equals(relation.RelationType, ContextRelationTypes.SupersededBy, StringComparison.OrdinalIgnoreCase)
                ? new ReplacementEdge(relation.SourceId, relation.TargetId)
                : new ReplacementEdge(relation.TargetId, relation.SourceId))
            .Where(edge => !string.IsNullOrWhiteSpace(edge.OldItemId) && !string.IsNullOrWhiteSpace(edge.NewItemId))
            .GroupBy(edge => $"{edge.OldItemId}\u001f{edge.NewItemId}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static bool HasForwardAndReverseReplacementRelations(
        string oldItemId,
        string newItemId,
        IReadOnlyList<ContextRelation> relations)
    {
        var hasSupersededBy = relations.Any(relation =>
            string.Equals(relation.RelationType, ContextRelationTypes.SupersededBy, StringComparison.OrdinalIgnoreCase)
            && string.Equals(relation.SourceId, oldItemId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(relation.TargetId, newItemId, StringComparison.OrdinalIgnoreCase));
        var hasReplaces = relations.Any(relation =>
            string.Equals(relation.RelationType, ContextRelationTypes.Replaces, StringComparison.OrdinalIgnoreCase)
            && string.Equals(relation.SourceId, newItemId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(relation.TargetId, oldItemId, StringComparison.OrdinalIgnoreCase));
        return hasSupersededBy && hasReplaces;
    }

    private static IReadOnlyList<string> TraverseReplacementGraph(
        string itemId,
        IReadOnlyList<ReplacementEdge> edges,
        bool forward,
        List<string> warnings)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { itemId };
        var current = itemId;

        while (true)
        {
            var nextIds = forward
                ? edges.Where(edge => string.Equals(edge.OldItemId, current, StringComparison.OrdinalIgnoreCase)).Select(edge => edge.NewItemId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : edges.Where(edge => string.Equals(edge.NewItemId, current, StringComparison.OrdinalIgnoreCase)).Select(edge => edge.OldItemId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (nextIds.Length == 0)
            {
                break;
            }

            if (nextIds.Length > 1)
            {
                warnings.Add($"replacement chain branches at {current}: {string.Join(",", nextIds)}");
            }

            var next = nextIds[0];
            if (!seen.Add(next))
            {
                warnings.Add($"replacement chain cycle detected at {next}.");
                break;
            }

            result.Add(next);
            current = next;
        }

        return result;
    }

    private static string ResolveLatestItemId(
        string currentItemId,
        IReadOnlyList<string> nextIds,
        IReadOnlyDictionary<string, StableMemoryRecord> recordById)
    {
        if (nextIds.Count == 0)
        {
            return currentItemId;
        }

        foreach (var id in nextIds.Reverse())
        {
            if (recordById.TryGetValue(id, out var item) && IsActive(item))
            {
                return id;
            }
        }

        return nextIds.LastOrDefault() ?? currentItemId;
    }

    private static IReadOnlyList<IReadOnlyList<string>> FindReplacementCycles(
        IReadOnlyList<StableMemoryRecord> records,
        IReadOnlyList<ReplacementEdge> edges)
    {
        var cycles = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in records)
        {
            var path = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = item.Id;
            while (seen.Add(current))
            {
                path.Add(current);
                var next = edges
                    .Where(edge => string.Equals(edge.OldItemId, current, StringComparison.OrdinalIgnoreCase))
                    .Select(edge => edge.NewItemId)
                    .FirstOrDefault();
                if (string.IsNullOrWhiteSpace(next))
                {
                    break;
                }

                var existingIndex = path.FindIndex(id => string.Equals(id, next, StringComparison.OrdinalIgnoreCase));
                if (existingIndex >= 0)
                {
                    var cycle = path.Skip(existingIndex).ToArray();
                    var key = string.Join("|", cycle.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
                    cycles.TryAdd(key, cycle);
                    break;
                }

                current = next;
            }
        }

        return cycles.Values.ToArray();
    }

    private static IReadOnlyList<string> ReadMetadataList(
        IReadOnlyDictionary<string, string> metadata,
        params string[] keys)
    {
        var values = new List<string>();
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                values.AddRange(value.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveLifecycle(ContextMemoryStatus status, IReadOnlyDictionary<string, string> metadata)
    {
        var metadataLifecycle = ReadMetadata(metadata, "lifecycle", "processState");
        if (!string.IsNullOrWhiteSpace(metadataLifecycle))
        {
            return metadataLifecycle;
        }

        return status switch
        {
            ContextMemoryStatus.Active => StableMemoryLifecycle.Active,
            ContextMemoryStatus.Deprecated => StableMemoryLifecycle.Deprecated,
            ContextMemoryStatus.Rejected => StableMemoryLifecycle.Rejected,
            _ => StableMemoryLifecycle.Current
        };
    }

    private static IReadOnlyList<string> ResolveEvidenceRefs(
        IReadOnlyList<string> sourceRefs,
        IReadOnlyDictionary<string, string> metadata)
    {
        var refs = new List<string>();
        var value = ReadMetadata(metadata, "evidenceRefs");
        if (!string.IsNullOrWhiteSpace(value))
        {
            refs.AddRange(value.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        refs.AddRange(sourceRefs.Where(static reference =>
            !reference.StartsWith("src-", StringComparison.OrdinalIgnoreCase)
            && !reference.StartsWith("stpc-", StringComparison.OrdinalIgnoreCase)
            && !reference.StartsWith("clc-", StringComparison.OrdinalIgnoreCase)));
        return refs
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveTitle(string content)
    {
        var first = (content ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first))
        {
            return "(untitled stable item)";
        }

        return first.Length <= 120 ? first : first[..120];
    }

    private static string ResolveSummary(string content)
    {
        var normalized = (content ?? string.Empty).Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240];
    }

    private static double ParseDouble(string? value)
    {
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0.0;
    }

    private static string? ReadMetadata(
        IReadOnlyDictionary<string, string> metadata,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
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

    private sealed record ReplacementEdge(string OldItemId, string NewItemId);
}
