using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Graph;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.ControlRoom.Services;

/// <summary>Relation governance scoped provider 的扩展 canary；只在显式隔离 scope 中运行。</summary>
public sealed class RelationGovernanceExtendedCanaryRunner
{
    private static readonly string[] PreviewProfiles =
    [
        "audit-v1",
        "conflict-v1",
        "normal-v1",
        "current-task-v1"
    ];

    private readonly FileRelationStore _fileRelationStore;
    private readonly FileRelationReviewStore _fileReviewStore;
    private readonly FileRelationDiagnosticsStore _fileDiagnosticsStore;
    private readonly PostgresRelationStore _postgresRelationStore;
    private readonly PostgresRelationReviewStore _postgresReviewStore;
    private readonly PostgresRelationDiagnosticsStore _postgresDiagnosticsStore;
    private readonly RelationGovernanceExtendedCanaryOptions _options;
    private readonly bool _scopedServiceModeGatePassed;
    private readonly Func<RelationGovernanceProviderSwitchTrace, CancellationToken, Task> _traceSink;

    public RelationGovernanceExtendedCanaryRunner(
        FileRelationStore fileRelationStore,
        FileRelationReviewStore fileReviewStore,
        FileRelationDiagnosticsStore fileDiagnosticsStore,
        PostgresRelationStore postgresRelationStore,
        PostgresRelationReviewStore postgresReviewStore,
        PostgresRelationDiagnosticsStore postgresDiagnosticsStore,
        RelationGovernanceExtendedCanaryOptions options,
        bool scopedServiceModeGatePassed,
        Func<RelationGovernanceProviderSwitchTrace, CancellationToken, Task> traceSink)
    {
        _fileRelationStore = fileRelationStore;
        _fileReviewStore = fileReviewStore;
        _fileDiagnosticsStore = fileDiagnosticsStore;
        _postgresRelationStore = postgresRelationStore;
        _postgresReviewStore = postgresReviewStore;
        _postgresDiagnosticsStore = postgresDiagnosticsStore;
        _options = options;
        _scopedServiceModeGatePassed = scopedServiceModeGatePassed;
        _traceSink = traceSink;
    }

    public async Task<PostgresRelationScopedExtendedCanaryReport> RunAsync(
        string workspaceId,
        string collectionId,
        bool cleanupConfirm,
        CancellationToken cancellationToken = default,
        string? idScopePrefix = null)
    {
        var traces = new List<RelationGovernanceProviderSwitchTrace>();
        var diagnostics = new List<string>();
        var blocked = new List<string>();
        var semanticMismatches = new List<string>();
        var scope = $"{workspaceId}/{collectionId}";

        if (!_options.Enabled)
        {
            blocked.Add("ExtendedCanaryDisabled");
        }

        if (_options.RequireScopedServiceModeGate && !_scopedServiceModeGatePassed)
        {
            blocked.Add("ScopedServiceModeGateNotPassed");
        }

        if (!_options.WorkspaceAllowlist.Contains(workspaceId, StringComparer.OrdinalIgnoreCase)
            || !_options.CollectionAllowlist.Contains(collectionId, StringComparer.OrdinalIgnoreCase))
        {
            blocked.Add("ExtendedCanaryScopeNotAllowlisted");
        }

        if (blocked.Count > 0)
        {
            return new PostgresRelationScopedExtendedCanaryReport
            {
                GatePassed = false,
                ProviderMode = _options.Mode.ToString(),
                CanaryScope = scope,
                CleanupPerformed = cleanupConfirm,
                Diagnostics = diagnostics,
                BlockedReasons = blocked,
                Recommendation = "GateNotPassed"
            };
        }

        var router = new RelationGovernanceProviderRouter(
            _fileRelationStore,
            _fileReviewStore,
            _fileDiagnosticsStore,
            _postgresRelationStore,
            _postgresReviewStore,
            _postgresDiagnosticsStore,
            new RelationGovernanceProviderSwitchOptions
            {
                Enabled = true,
                Mode = _options.Mode,
                AllowedWorkspaces = [workspaceId],
                AllowedCollections = [collectionId],
                FallbackToFileSystem = _options.FallbackToFileSystem,
                ContinueComparisonTrace = _options.ContinueComparisonTrace,
                FailClosedOnMismatch = _options.FailClosedOnMismatch,
                RequireReadinessGate = _options.RequireScopedServiceModeGate
            },
            _scopedServiceModeGatePassed,
            _scopedServiceModeGatePassed,
            async (trace, token) =>
            {
                traces.Add(trace);
                await _traceSink(trace, token).ConfigureAwait(false);
            });

        var now = DateTimeOffset.UtcNow;
        var idPrefix = BuildScopedIdPrefix(idScopePrefix);
        string Id(string value) => string.IsNullOrWhiteSpace(idPrefix) ? value : $"{idPrefix}-{value}";
        var main = CreateRelation(Id("extended-rel-main"), workspaceId, collectionId, Id("extended-source-main"), Id("extended-target-main"), ContextRelationTypes.References, now, "Active", "Reviewed", "Active");
        var updatedMain = CloneRelation(main, weight: 0.74, confidence: 0.96, source: "relation_governance_extended_canary_update");
        var deleted = CreateRelation(Id("extended-rel-delete"), workspaceId, collectionId, Id("extended-source-delete"), Id("extended-target-delete"), "references", now.AddMilliseconds(1), "Active", "Reviewed", "Active");
        var oldToNew = CreateRelation(Id("extended-rel-old-to-new"), workspaceId, collectionId, Id("extended-item-old"), Id("extended-item-new"), ContextRelationTypes.SupersededBy, now.AddMilliseconds(2), "Active", "Reviewed", "Active");
        var newToOld = CreateRelation(Id("extended-rel-new-to-old"), workspaceId, collectionId, Id("extended-item-new"), Id("extended-item-old"), ContextRelationTypes.Replaces, now.AddMilliseconds(3), "Active", "Reviewed", StableMemoryLifecycle.Deprecated);
        var conflict = CreateRelation(Id("extended-rel-conflict"), workspaceId, collectionId, Id("extended-item-new"), Id("extended-item-conflict"), "conflicts_with", now.AddMilliseconds(4), "Active", "Reviewed", "Active");

        var reviews = new[]
        {
            CreateReview(Id("extended-review-main-review"), main, RelationReviewActions.Review, RelationReviewStatuses.Reviewed, now.AddMilliseconds(5)),
            CreateReview(Id("extended-review-main-reject"), main, RelationReviewActions.Reject, RelationReviewStatuses.Rejected, now.AddMilliseconds(6)),
            CreateReview(Id("extended-review-main-deprecate"), main, RelationReviewActions.Deprecate, "Deprecated", now.AddMilliseconds(7)),
            CreateReview(Id("extended-review-main-needs-evidence"), main, RelationReviewActions.MarkNeedsEvidence, RelationReviewStatuses.NeedsEvidence, now.AddMilliseconds(8))
        };
        var diagnosticsSnapshots = new[]
        {
            CreateDiagnostics(Id("extended-diagnostic-main"), workspaceId, collectionId, main.Id, main.TargetId, "ExtendedCanaryInfo", "Info", now.AddMilliseconds(9)),
            CreateDiagnostics(Id("extended-diagnostic-chain"), workspaceId, collectionId, oldToNew.Id, oldToNew.SourceId, "ReplacementChainCheck", "Warning", now.AddMilliseconds(10))
        };

        if (cleanupConfirm)
        {
            await CleanupAsync(workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            await router.SaveRelationAsync("extended-create-main", main, cancellationToken).ConfigureAwait(false);
            await router.SaveRelationAsync("extended-update-main", updatedMain, cancellationToken).ConfigureAwait(false);
            await router.SaveRelationAsync("extended-create-delete", deleted, cancellationToken).ConfigureAwait(false);
            await router.DeleteRelationAsync("extended-delete-relation", workspaceId, collectionId, deleted.Id, cancellationToken).ConfigureAwait(false);
            await router.SaveRelationAsync("extended-create-old-to-new", oldToNew, cancellationToken).ConfigureAwait(false);
            await router.SaveRelationAsync("extended-create-new-to-old", newToOld, cancellationToken).ConfigureAwait(false);
            await router.SaveRelationAsync("extended-create-conflict", conflict, cancellationToken).ConfigureAwait(false);

            foreach (var review in reviews)
            {
                await router.AppendReviewAsync($"extended-write-review-{review.Action}", review, cancellationToken).ConfigureAwait(false);
            }

            foreach (var snapshot in diagnosticsSnapshots)
            {
                await router.WriteDiagnosticsAsync($"extended-write-diagnostics-{snapshot.DiagnosticKind}", snapshot, cancellationToken).ConfigureAwait(false);
            }

            await ValidateRelationReadsAsync(router, workspaceId, collectionId, updatedMain, deleted, oldToNew, newToOld, conflict, semanticMismatches, cancellationToken).ConfigureAwait(false);
            await ValidateReviewReadsAsync(router, workspaceId, collectionId, main.Id, reviews, semanticMismatches, cancellationToken).ConfigureAwait(false);
            await ValidateDiagnosticsReadsAsync(router, workspaceId, collectionId, diagnosticsSnapshots, semanticMismatches, cancellationToken).ConfigureAwait(false);
            await ValidateReplacementChainAsync(router, workspaceId, collectionId, oldToNew, newToOld, semanticMismatches, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Npgsql.PostgresException or TimeoutException or IOException)
        {
            diagnostics.Add($"ExtendedCanaryFailed:{ex.GetType().Name}");
        }

        var graphParityPassed = await ValidateGraphPreviewParityAsync(
            workspaceId,
            collectionId,
            oldToNew.SourceId,
            semanticMismatches,
            cancellationToken).ConfigureAwait(false);
        var reviewParityPassed = !semanticMismatches.Any(static item => item.StartsWith("Review", StringComparison.OrdinalIgnoreCase));
        var diagnosticsParityPassed = !semanticMismatches.Any(static item => item.StartsWith("Diagnostics", StringComparison.OrdinalIgnoreCase));
        var replacementParityPassed = !semanticMismatches.Any(static item => item.StartsWith("Replacement", StringComparison.OrdinalIgnoreCase));

        if (cleanupConfirm)
        {
            await CleanupAsync(workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
        }

        var traceMismatchCount = traces.Count(static item => item.MismatchDetected);
        var postgresFailureCount = traces.Count(static item => !string.IsNullOrWhiteSpace(item.PostgresError));
        var mismatchCount = traceMismatchCount + semanticMismatches.Count;
        var primaryReadCount = traces.Count(static item =>
            string.Equals(item.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)
            && (item.OperationKind.Contains("Query", StringComparison.OrdinalIgnoreCase)
                || item.OperationKind.Contains("Get", StringComparison.OrdinalIgnoreCase)
                || item.OperationKind.Contains("Latest", StringComparison.OrdinalIgnoreCase)
                || item.OperationKind.StartsWith("RelationDiagnosticsBy", StringComparison.OrdinalIgnoreCase)));
        var primaryWriteCount = traces.Count(static item =>
            string.Equals(item.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)
            && (item.OperationKind.Contains("Write", StringComparison.OrdinalIgnoreCase)
                || item.OperationKind.Contains("Delete", StringComparison.OrdinalIgnoreCase)));
        var comparisonTraceCount = traces.Count(static item =>
            string.Equals(item.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase));

        var recommendation = !_scopedServiceModeGatePassed
            ? "GateNotPassed"
            : mismatchCount > 0
                ? graphParityPassed ? "BlockedByMismatch" : "BlockedByGraphPreviewMismatch"
                : postgresFailureCount > 0
                    ? "BlockedByPostgresFailure"
                    : primaryReadCount > 0
                      && primaryWriteCount > 0
                      && graphParityPassed
                      && reviewParityPassed
                      && diagnosticsParityPassed
                      && replacementParityPassed
                      && cleanupConfirm
                        ? "ReadyForSelectedWorkspaceCanary"
                        : "NeedsMoreCanaryRuns";

        return new PostgresRelationScopedExtendedCanaryReport
        {
            GatePassed = _scopedServiceModeGatePassed,
            ProviderMode = _options.Mode.ToString(),
            CanaryScope = scope,
            OperationCount = traces.Count,
            PostgresPrimaryReadCount = primaryReadCount,
            PostgresPrimaryWriteCount = primaryWriteCount,
            FileSystemFallbackCount = traces.Count(static item => item.FallbackUsed),
            ComparisonTraceCount = comparisonTraceCount,
            MismatchCount = mismatchCount,
            PostgresFailureCount = postgresFailureCount,
            GraphExpansionPreviewParityPassed = graphParityPassed,
            ReviewLifecycleParityPassed = reviewParityPassed,
            DiagnosticsParityPassed = diagnosticsParityPassed,
            ReplacementChainParityPassed = replacementParityPassed,
            CleanupPerformed = cleanupConfirm,
            Diagnostics = diagnostics.Count == 0 ? ["ExtendedCanaryScopedOnly"] : diagnostics,
            BlockedReasons = semanticMismatches,
            Recommendation = recommendation
        };
    }

    private async Task ValidateRelationReadsAsync(
        RelationGovernanceProviderRouter router,
        string workspaceId,
        string collectionId,
        ContextRelation main,
        ContextRelation deleted,
        ContextRelation oldToNew,
        ContextRelation newToOld,
        ContextRelation conflict,
        ICollection<string> mismatches,
        CancellationToken cancellationToken)
    {
        await ExpectRelationAsync("RelationGet", mismatches, router.GetRelationAsync("extended-read-main", workspaceId, collectionId, main.Id, cancellationToken), main.Id).ConfigureAwait(false);
        var deletedResult = await router.GetRelationAsync("extended-read-deleted", workspaceId, collectionId, deleted.Id, cancellationToken).ConfigureAwait(false);
        AddIfFalse(mismatches, deletedResult is null, "RelationDeleteMismatch");
        await ExpectRelationListAsync("RelationList", mismatches, router.QueryRelationsAsync("extended-read-list", workspaceId, collectionId, cancellationToken), main.Id, oldToNew.Id, newToOld.Id, conflict.Id).ConfigureAwait(false);
        await ExpectRelationListAsync("RelationSourceQuery", mismatches, router.QueryBySourceAsync("extended-read-source", workspaceId, collectionId, main.SourceId, cancellationToken), main.Id).ConfigureAwait(false);
        await ExpectRelationListAsync("RelationTargetQuery", mismatches, router.QueryByTargetAsync("extended-read-target", workspaceId, collectionId, main.TargetId, cancellationToken), main.Id).ConfigureAwait(false);
        await ExpectRelationListAsync("RelationTypeQuery", mismatches, router.QueryByTypeAsync("extended-read-type", workspaceId, collectionId, ContextRelationTypes.SupersededBy, cancellationToken), oldToNew.Id).ConfigureAwait(false);
        await ExpectRelationListAsync("RelationLifecycleQuery", mismatches, router.QueryByLifecycleAsync("extended-read-lifecycle", workspaceId, collectionId, "Active", cancellationToken), main.Id, oldToNew.Id, newToOld.Id, conflict.Id).ConfigureAwait(false);
        await ExpectRelationListAsync("RelationReviewStatusQuery", mismatches, router.QueryByReviewStatusAsync("extended-read-review-status", workspaceId, collectionId, "Reviewed", cancellationToken), main.Id, oldToNew.Id, newToOld.Id, conflict.Id).ConfigureAwait(false);
    }

    private static async Task ValidateReviewReadsAsync(
        RelationGovernanceProviderRouter router,
        string workspaceId,
        string collectionId,
        string relationId,
        IReadOnlyList<RelationReviewRecord> reviews,
        ICollection<string> mismatches,
        CancellationToken cancellationToken)
    {
        await ExpectReviewListAsync("ReviewHistoryList", mismatches, router.QueryReviewsAsync("extended-read-review-history", workspaceId, collectionId, relationId, cancellationToken), reviews.Select(static item => item.ReviewId).ToArray()).ConfigureAwait(false);
        await ExpectReviewAsync("ReviewLatest", mismatches, router.GetLatestReviewAsync("extended-read-review-latest", workspaceId, collectionId, relationId, cancellationToken), reviews[^1].ReviewId).ConfigureAwait(false);
    }

    private static async Task ValidateDiagnosticsReadsAsync(
        RelationGovernanceProviderRouter router,
        string workspaceId,
        string collectionId,
        IReadOnlyList<RelationDiagnosticsSnapshot> snapshots,
        ICollection<string> mismatches,
        CancellationToken cancellationToken)
    {
        var first = snapshots[0];
        var second = snapshots[1];
        await ExpectDiagnosticsAsync("DiagnosticsByRelation", mismatches, router.QueryDiagnosticsByRelationAsync("extended-read-diagnostics-relation", workspaceId, collectionId, first.RelationId, cancellationToken), first.DiagnosticId).ConfigureAwait(false);
        await ExpectDiagnosticsAsync("DiagnosticsByItem", mismatches, router.QueryDiagnosticsByItemAsync("extended-read-diagnostics-item", workspaceId, collectionId, first.ItemId, cancellationToken), first.DiagnosticId).ConfigureAwait(false);
        await ExpectDiagnosticsAsync("DiagnosticsByKind", mismatches, router.QueryDiagnosticsByKindAsync("extended-read-diagnostics-kind", workspaceId, collectionId, second.DiagnosticKind, cancellationToken), second.DiagnosticId).ConfigureAwait(false);
        await ExpectDiagnosticsAsync("DiagnosticsBySeverity", mismatches, router.QueryDiagnosticsBySeverityAsync("extended-read-diagnostics-severity", workspaceId, collectionId, second.Severity, cancellationToken), second.DiagnosticId).ConfigureAwait(false);
    }

    private static async Task ValidateReplacementChainAsync(
        RelationGovernanceProviderRouter router,
        string workspaceId,
        string collectionId,
        ContextRelation oldToNew,
        ContextRelation newToOld,
        ICollection<string> mismatches,
        CancellationToken cancellationToken)
    {
        await ExpectRelationListAsync("ReplacementChainOld", mismatches, router.QueryReplacementChainRelationsAsync("extended-read-chain-old", workspaceId, collectionId, oldToNew.SourceId, cancellationToken), oldToNew.Id).ConfigureAwait(false);
        await ExpectRelationListAsync("ReplacementChainNew", mismatches, router.QueryReplacementChainRelationsAsync("extended-read-chain-new", workspaceId, collectionId, newToOld.SourceId, cancellationToken), newToOld.Id).ConfigureAwait(false);
    }

    private async Task<bool> ValidateGraphPreviewParityAsync(
        string workspaceId,
        string collectionId,
        string itemId,
        ICollection<string> mismatches,
        CancellationToken cancellationToken)
    {
        var registry = new RelationExpansionProfileRegistry();
        var validator = new RelationExpansionPolicyValidator(new RelationTypeRegistry());
        var filePreview = new RelationExpansionPreviewService(_fileRelationStore, registry, validator);
        var postgresPreview = new RelationExpansionPreviewService(_postgresRelationStore, registry, validator);
        var passed = true;

        foreach (var profileId in PreviewProfiles)
        {
            var request = new RelationExpansionPreviewRequest
            {
                OperationId = $"extended-preview-{profileId}",
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ItemId = itemId,
                ProfileId = profileId
            };
            var fileResult = await filePreview.PreviewAsync(request, cancellationToken).ConfigureAwait(false);
            var postgresResult = await postgresPreview.PreviewAsync(request, cancellationToken).ConfigureAwait(false);
            var fileSignature = BuildPreviewSignature(fileResult);
            var postgresSignature = BuildPreviewSignature(postgresResult);
            if (!string.Equals(fileSignature, postgresSignature, StringComparison.Ordinal))
            {
                passed = false;
                mismatches.Add($"GraphPreviewMismatch:{profileId}");
            }
        }

        return passed;
    }

    private async Task CleanupAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        await _postgresReviewStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
        await _postgresDiagnosticsStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
        await _postgresRelationStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildPreviewSignature(RelationExpansionPreviewResponse response)
    {
        static IEnumerable<string> Lines(IEnumerable<RelationExpansionPreviewRelation> relations, string prefix)
        {
            return relations
                .OrderBy(item => item.RelationId, StringComparer.OrdinalIgnoreCase)
                .Select(item => string.Join(
                    '|',
                    prefix,
                    item.RelationId,
                    item.RelationType,
                    item.TargetSection,
                    item.RiskIfNormalSelected,
                    item.RiskAfterSectionRouting,
                    string.Join(',', item.Reasons.Order(StringComparer.OrdinalIgnoreCase))));
        }

        return string.Join('\n', Lines(response.AcceptedRelations, "A").Concat(Lines(response.BlockedRelations, "B")));
    }

    private static ContextRelation CreateRelation(
        string id,
        string workspaceId,
        string collectionId,
        string sourceId,
        string targetId,
        string relationType,
        DateTimeOffset createdAt,
        string lifecycle,
        string reviewStatus,
        string targetLifecycle)
    {
        return new ContextRelation
        {
            Id = id,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SourceId = sourceId,
            TargetId = targetId,
            RelationType = relationType,
            Weight = 0.8,
            Confidence = 1.0,
            SourceRefs = [$"extended-canary:{id}"],
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lifecycle"] = lifecycle,
                ["reviewStatus"] = reviewStatus,
                ["targetLifecycle"] = targetLifecycle,
                ["evidenceRefs"] = $"extended-canary-evidence:{id}",
                ["sourceRefs"] = $"extended-canary-source:{id}",
                ["source"] = "relation_governance_extended_canary",
                ["policyVersion"] = "db2.8"
            },
            CreatedAt = createdAt
        };
    }

    private static ContextRelation CloneRelation(ContextRelation relation, double weight, double confidence, string source)
    {
        var metadata = new Dictionary<string, string>(relation.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = source
        };

        return new ContextRelation
        {
            Id = relation.Id,
            WorkspaceId = relation.WorkspaceId,
            CollectionId = relation.CollectionId,
            SourceId = relation.SourceId,
            TargetId = relation.TargetId,
            RelationType = relation.RelationType,
            Weight = weight,
            Confidence = confidence,
            SourceRefs = relation.SourceRefs,
            Metadata = metadata,
            CreatedAt = relation.CreatedAt
        };
    }

    private static RelationReviewRecord CreateReview(
        string reviewId,
        ContextRelation relation,
        string action,
        string reviewStatus,
        DateTimeOffset createdAt)
    {
        return new RelationReviewRecord
        {
            ReviewId = reviewId,
            RelationId = relation.Id,
            WorkspaceId = relation.WorkspaceId,
            CollectionId = relation.CollectionId,
            Action = action,
            FromLifecycle = "Active",
            ToLifecycle = string.Equals(action, RelationReviewActions.Deprecate, StringComparison.OrdinalIgnoreCase)
                ? "Deprecated"
                : "Active",
            FromReviewStatus = "Pending",
            ToReviewStatus = reviewStatus,
            Reviewer = "relation-governance-extended-canary",
            Reason = "scoped extended canary review lifecycle check",
            RelationType = relation.RelationType,
            SourceId = relation.SourceId,
            TargetId = relation.TargetId,
            EvidenceRefs = [$"extended-canary-evidence:{reviewId}"],
            SourceRefs = [$"extended-canary-source:{reviewId}"],
            CreatedAt = createdAt,
            ReviewedAt = createdAt,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["operationId"] = $"extended-canary-review-{action}",
                ["source"] = "relation_governance_extended_canary"
            }
        };
    }

    private static RelationDiagnosticsSnapshot CreateDiagnostics(
        string diagnosticId,
        string workspaceId,
        string collectionId,
        string relationId,
        string itemId,
        string kind,
        string severity,
        DateTimeOffset createdAt)
    {
        return new RelationDiagnosticsSnapshot
        {
            DiagnosticId = diagnosticId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            RelationId = relationId,
            ItemId = itemId,
            DiagnosticKind = kind,
            Severity = severity,
            Message = $"{kind} scoped extended canary diagnostic",
            CreatedAt = createdAt,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["source"] = "relation_governance_extended_canary"
            }
        };
    }

    private static async Task ExpectRelationAsync(
        string label,
        ICollection<string> mismatches,
        Task<ContextRelation?> task,
        string expectedId)
    {
        var relation = await task.ConfigureAwait(false);
        AddIfFalse(mismatches, string.Equals(relation?.Id, expectedId, StringComparison.OrdinalIgnoreCase), $"{label}Mismatch");
    }

    private static async Task ExpectRelationListAsync(
        string label,
        ICollection<string> mismatches,
        Task<IReadOnlyList<ContextRelation>> task,
        params string[] expectedIds)
    {
        var relations = await task.ConfigureAwait(false);
        foreach (var expectedId in expectedIds)
        {
            AddIfFalse(
                mismatches,
                relations.Any(item => string.Equals(item.Id, expectedId, StringComparison.OrdinalIgnoreCase)),
                $"{label}Missing:{expectedId}");
        }
    }

    private static async Task ExpectReviewAsync(
        string label,
        ICollection<string> mismatches,
        Task<RelationReviewRecord?> task,
        string expectedId)
    {
        var review = await task.ConfigureAwait(false);
        AddIfFalse(mismatches, string.Equals(review?.ReviewId, expectedId, StringComparison.OrdinalIgnoreCase), $"{label}Mismatch");
    }

    private static async Task ExpectReviewListAsync(
        string label,
        ICollection<string> mismatches,
        Task<IReadOnlyList<RelationReviewRecord>> task,
        params string[] expectedIds)
    {
        var reviews = await task.ConfigureAwait(false);
        foreach (var expectedId in expectedIds)
        {
            AddIfFalse(
                mismatches,
                reviews.Any(item => string.Equals(item.ReviewId, expectedId, StringComparison.OrdinalIgnoreCase)),
                $"{label}Missing:{expectedId}");
        }
    }

    private static async Task ExpectDiagnosticsAsync(
        string label,
        ICollection<string> mismatches,
        Task<IReadOnlyList<RelationDiagnosticsSnapshot>> task,
        string expectedId)
    {
        var snapshots = await task.ConfigureAwait(false);
        AddIfFalse(
            mismatches,
            snapshots.Any(item => string.Equals(item.DiagnosticId, expectedId, StringComparison.OrdinalIgnoreCase)),
            $"{label}Missing:{expectedId}");
    }

    private static void AddIfFalse(ICollection<string> mismatches, bool passed, string mismatch)
    {
        if (!passed)
        {
            mismatches.Add(mismatch);
        }
    }

    private static string BuildScopedIdPrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[Math.Min(value.Length, 48)];
        var index = 0;
        foreach (var ch in value)
        {
            if (index >= buffer.Length)
            {
                break;
            }

            buffer[index++] = char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-';
        }

        return new string(buffer[..index]).Trim('-');
    }
}
