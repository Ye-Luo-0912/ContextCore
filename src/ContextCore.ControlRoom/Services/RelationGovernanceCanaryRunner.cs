using ContextCore.Abstractions.Models;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.ControlRoom.Services;

/// <summary>Relation governance 运行时 canary；只在显式 allowlist scope 中验证 guarded provider switch。</summary>
public sealed class RelationGovernanceCanaryRunner
{
    private readonly FileRelationStore _fileRelationStore;
    private readonly FileRelationReviewStore _fileReviewStore;
    private readonly FileRelationDiagnosticsStore _fileDiagnosticsStore;
    private readonly PostgresRelationStore _postgresRelationStore;
    private readonly PostgresRelationReviewStore _postgresReviewStore;
    private readonly PostgresRelationDiagnosticsStore _postgresDiagnosticsStore;
    private readonly RelationGovernanceCanaryOptions _options;
    private readonly bool _providerSwitchGatePassed;
    private readonly Func<RelationGovernanceProviderSwitchTrace, CancellationToken, Task> _traceSink;

    public RelationGovernanceCanaryRunner(
        FileRelationStore fileRelationStore,
        FileRelationReviewStore fileReviewStore,
        FileRelationDiagnosticsStore fileDiagnosticsStore,
        PostgresRelationStore postgresRelationStore,
        PostgresRelationReviewStore postgresReviewStore,
        PostgresRelationDiagnosticsStore postgresDiagnosticsStore,
        RelationGovernanceCanaryOptions options,
        bool providerSwitchGatePassed,
        Func<RelationGovernanceProviderSwitchTrace, CancellationToken, Task> traceSink)
    {
        _fileRelationStore = fileRelationStore;
        _fileReviewStore = fileReviewStore;
        _fileDiagnosticsStore = fileDiagnosticsStore;
        _postgresRelationStore = postgresRelationStore;
        _postgresReviewStore = postgresReviewStore;
        _postgresDiagnosticsStore = postgresDiagnosticsStore;
        _options = options;
        _providerSwitchGatePassed = providerSwitchGatePassed;
        _traceSink = traceSink;
    }

    public async Task<PostgresRelationRuntimeCanaryReport> RunAsync(
        string workspaceId,
        string collectionId,
        bool cleanupConfirm,
        CancellationToken cancellationToken = default)
    {
        var traces = new List<RelationGovernanceProviderSwitchTrace>();
        var diagnostics = new List<string>();
        var blocked = new List<string>();
        var semanticMismatches = new List<string>();
        var scope = $"{workspaceId}/{collectionId}";

        if (!_options.Enabled)
        {
            blocked.Add("CanaryDisabled");
        }

        if (_options.RequireProviderSwitchGate && !_providerSwitchGatePassed)
        {
            blocked.Add("ProviderSwitchGateNotPassed");
        }

        if (!_options.WorkspaceAllowlist.Contains(workspaceId, StringComparer.OrdinalIgnoreCase)
            || !_options.CollectionAllowlist.Contains(collectionId, StringComparer.OrdinalIgnoreCase))
        {
            blocked.Add("CanaryScopeNotAllowlisted");
        }

        if (blocked.Count > 0)
        {
            return new PostgresRelationRuntimeCanaryReport
            {
                CanaryScope = scope,
                ProviderMode = _options.Mode.ToString(),
                GatePassed = false,
                CleanupPerformed = cleanupConfirm,
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
                RequireReadinessGate = _options.RequireProviderSwitchGate
            },
            _providerSwitchGatePassed,
            _providerSwitchGatePassed,
            async (trace, token) =>
            {
                traces.Add(trace);
                await _traceSink(trace, token).ConfigureAwait(false);
            });

        var now = DateTimeOffset.UtcNow;
        var mainRelation = CreateRelation(
            "canary-rel-main",
            workspaceId,
            collectionId,
            "canary-source-main",
            "canary-target-main",
            ContextRelationTypes.References,
            now);
        var replacementRelation = CreateRelation(
            "canary-rel-replacement",
            workspaceId,
            collectionId,
            "canary-item-old",
            "canary-item-new",
            ContextRelationTypes.SupersededBy,
            now.AddMilliseconds(1));
        var review = CreateReview(mainRelation, now.AddMilliseconds(2));
        var snapshot = CreateDiagnostics(workspaceId, collectionId, mainRelation.Id, mainRelation.TargetId, now.AddMilliseconds(3));

        try
        {
            await router.SaveRelationAsync("canary-write-main-relation", mainRelation, cancellationToken).ConfigureAwait(false);
            await router.SaveRelationAsync("canary-write-replacement-relation", replacementRelation, cancellationToken).ConfigureAwait(false);
            await router.AppendReviewAsync("canary-write-review", review, cancellationToken).ConfigureAwait(false);
            await router.WriteDiagnosticsAsync("canary-write-diagnostics", snapshot, cancellationToken).ConfigureAwait(false);

            await ExpectRelationAsync(
                "RelationGet",
                semanticMismatches,
                router.GetRelationAsync("canary-read-get", workspaceId, collectionId, mainRelation.Id, cancellationToken),
                mainRelation.Id).ConfigureAwait(false);
            await ExpectRelationListAsync(
                "RelationList",
                semanticMismatches,
                router.QueryRelationsAsync("canary-read-list", workspaceId, collectionId, cancellationToken),
                mainRelation.Id,
                replacementRelation.Id).ConfigureAwait(false);
            await ExpectRelationListAsync(
                "RelationSourceQuery",
                semanticMismatches,
                router.QueryBySourceAsync("canary-read-source", workspaceId, collectionId, mainRelation.SourceId, cancellationToken),
                mainRelation.Id).ConfigureAwait(false);
            await ExpectRelationListAsync(
                "RelationTargetQuery",
                semanticMismatches,
                router.QueryByTargetAsync("canary-read-target", workspaceId, collectionId, mainRelation.TargetId, cancellationToken),
                mainRelation.Id).ConfigureAwait(false);
            await ExpectRelationListAsync(
                "RelationTypeQuery",
                semanticMismatches,
                router.QueryByTypeAsync("canary-read-type", workspaceId, collectionId, mainRelation.RelationType, cancellationToken),
                mainRelation.Id).ConfigureAwait(false);
            await ExpectRelationListAsync(
                "RelationLifecycleQuery",
                semanticMismatches,
                router.QueryByLifecycleAsync("canary-read-lifecycle", workspaceId, collectionId, "Active", cancellationToken),
                mainRelation.Id,
                replacementRelation.Id).ConfigureAwait(false);
            await ExpectRelationListAsync(
                "RelationReviewStatusQuery",
                semanticMismatches,
                router.QueryByReviewStatusAsync("canary-read-review-status", workspaceId, collectionId, "Reviewed", cancellationToken),
                mainRelation.Id,
                replacementRelation.Id).ConfigureAwait(false);
            await ExpectRelationListAsync(
                "RelationReplacementChainQuery",
                semanticMismatches,
                router.QueryReplacementChainRelationsAsync("canary-read-replacement-chain", workspaceId, collectionId, replacementRelation.SourceId, cancellationToken),
                replacementRelation.Id).ConfigureAwait(false);
            await ExpectReviewAsync(
                "RelationReviewLatest",
                semanticMismatches,
                router.GetLatestReviewAsync("canary-read-review-latest", workspaceId, collectionId, mainRelation.Id, cancellationToken),
                review.ReviewId).ConfigureAwait(false);
            await ExpectDiagnosticsAsync(
                "RelationDiagnosticsByRelation",
                semanticMismatches,
                router.QueryDiagnosticsByRelationAsync("canary-read-diagnostics-relation", workspaceId, collectionId, mainRelation.Id, cancellationToken),
                snapshot.DiagnosticId).ConfigureAwait(false);
            await ExpectDiagnosticsAsync(
                "RelationDiagnosticsByItem",
                semanticMismatches,
                router.QueryDiagnosticsByItemAsync("canary-read-diagnostics-item", workspaceId, collectionId, mainRelation.TargetId, cancellationToken),
                snapshot.DiagnosticId).ConfigureAwait(false);
            await ExpectDiagnosticsAsync(
                "RelationDiagnosticsByKind",
                semanticMismatches,
                router.QueryDiagnosticsByKindAsync("canary-read-diagnostics-kind", workspaceId, collectionId, snapshot.DiagnosticKind, cancellationToken),
                snapshot.DiagnosticId).ConfigureAwait(false);
            await ExpectDiagnosticsAsync(
                "RelationDiagnosticsBySeverity",
                semanticMismatches,
                router.QueryDiagnosticsBySeverityAsync("canary-read-diagnostics-severity", workspaceId, collectionId, snapshot.Severity, cancellationToken),
                snapshot.DiagnosticId).ConfigureAwait(false);

            var fallbackRelation = await _fileRelationStore.GetAsync(workspaceId, collectionId, mainRelation.Id, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(fallbackRelation?.Id, mainRelation.Id, StringComparison.OrdinalIgnoreCase))
            {
                semanticMismatches.Add("FileSystemFallbackRelationMissing");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Npgsql.PostgresException or TimeoutException or IOException)
        {
            diagnostics.Add($"RuntimeCanaryFailed:{ex.GetType().Name}");
        }

        if (cleanupConfirm)
        {
            await _postgresReviewStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
            await _postgresDiagnosticsStore.DeleteByScopeAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
            await _postgresRelationStore.DeleteAsync(workspaceId, collectionId, mainRelation.Id, cancellationToken).ConfigureAwait(false);
            await _postgresRelationStore.DeleteAsync(workspaceId, collectionId, replacementRelation.Id, cancellationToken).ConfigureAwait(false);
        }

        var traceMismatchCount = traces.Count(static item => item.MismatchDetected);
        var postgresFailureCount = traces.Count(static item => !string.IsNullOrWhiteSpace(item.PostgresError));
        var mismatchCount = traceMismatchCount + semanticMismatches.Count;
        var primaryReadCount = traces.Count(static item =>
            string.Equals(item.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)
            && (item.OperationKind.Contains("Query", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.OperationKind, "RelationGet", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.OperationKind, "RelationReviewLatest", StringComparison.OrdinalIgnoreCase)
                || item.OperationKind.StartsWith("RelationDiagnosticsBy", StringComparison.OrdinalIgnoreCase)));
        var primaryWriteCount = traces.Count(static item =>
            string.Equals(item.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)
            && item.OperationKind.Contains("Write", StringComparison.OrdinalIgnoreCase));
        var comparisonTraceCount = traces.Count(static item =>
            string.Equals(item.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase));

        var recommendation = !_providerSwitchGatePassed
            ? "GateNotPassed"
            : mismatchCount > 0
                ? "BlockedByMismatch"
                : postgresFailureCount > 0
                    ? "BlockedByPostgresFailure"
                    : primaryReadCount > 0 && primaryWriteCount > 0 && comparisonTraceCount > 0 && cleanupConfirm
                        ? "ReadyForScopedServiceMode"
                        : "NeedsMoreCanaryRuns";

        return new PostgresRelationRuntimeCanaryReport
        {
            CanaryScope = scope,
            ProviderMode = _options.Mode.ToString(),
            GatePassed = _providerSwitchGatePassed,
            PostgresPrimaryReadCount = primaryReadCount,
            PostgresPrimaryWriteCount = primaryWriteCount,
            FallbackCount = traces.Count(static item => item.FallbackUsed),
            MismatchCount = mismatchCount,
            PostgresFailureCount = postgresFailureCount,
            ComparisonTraceCount = comparisonTraceCount,
            CleanupPerformed = cleanupConfirm,
            Diagnostics = diagnostics.Count == 0 ? ["RuntimeProviderStillScopedCanaryOnly"] : diagnostics,
            BlockedReasons = semanticMismatches,
            Recommendation = recommendation
        };
    }

    private static ContextRelation CreateRelation(
        string id,
        string workspaceId,
        string collectionId,
        string sourceId,
        string targetId,
        string relationType,
        DateTimeOffset createdAt)
    {
        return new ContextRelation
        {
            Id = id,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SourceId = sourceId,
            TargetId = targetId,
            RelationType = relationType,
            Weight = 0.9,
            Confidence = 1.0,
            SourceRefs = [$"canary:{id}"],
            Metadata = new Dictionary<string, string>
            {
                ["lifecycle"] = "Active",
                ["reviewStatus"] = "Reviewed",
                ["source"] = "relation_governance_runtime_canary"
            },
            CreatedAt = createdAt
        };
    }

    private static RelationReviewRecord CreateReview(ContextRelation relation, DateTimeOffset createdAt)
    {
        return new RelationReviewRecord
        {
            ReviewId = "canary-review-main",
            RelationId = relation.Id,
            WorkspaceId = relation.WorkspaceId,
            CollectionId = relation.CollectionId,
            Action = RelationReviewActions.Review,
            FromLifecycle = "Active",
            ToLifecycle = "Active",
            FromReviewStatus = "Pending",
            ToReviewStatus = RelationReviewStatuses.Reviewed,
            Reviewer = "relation-governance-canary",
            Reason = "runtime canary scope verification",
            ReviewedAt = createdAt,
            CreatedAt = createdAt,
            Metadata = new Dictionary<string, string> { ["operationId"] = "canary-review-operation" }
        };
    }

    private static RelationDiagnosticsSnapshot CreateDiagnostics(
        string workspaceId,
        string collectionId,
        string relationId,
        string itemId,
        DateTimeOffset createdAt)
    {
        return new RelationDiagnosticsSnapshot
        {
            DiagnosticId = "canary-diagnostic-main",
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            RelationId = relationId,
            ItemId = itemId,
            DiagnosticKind = "CanaryVerification",
            Severity = "Info",
            Message = "relation governance runtime canary diagnostic",
            CreatedAt = createdAt,
            Metadata = new Dictionary<string, string> { ["source"] = "relation_governance_runtime_canary" }
        };
    }

    private static async Task ExpectRelationAsync(
        string label,
        List<string> mismatches,
        Task<ContextRelation?> task,
        string expectedId)
    {
        var relation = await task.ConfigureAwait(false);
        if (!string.Equals(relation?.Id, expectedId, StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add($"{label}Mismatch");
        }
    }

    private static async Task ExpectRelationListAsync(
        string label,
        List<string> mismatches,
        Task<IReadOnlyList<ContextRelation>> task,
        params string[] expectedIds)
    {
        var relations = await task.ConfigureAwait(false);
        foreach (var expectedId in expectedIds)
        {
            if (!relations.Any(item => string.Equals(item.Id, expectedId, StringComparison.OrdinalIgnoreCase)))
            {
                mismatches.Add($"{label}Missing:{expectedId}");
            }
        }
    }

    private static async Task ExpectReviewAsync(
        string label,
        List<string> mismatches,
        Task<RelationReviewRecord?> task,
        string expectedId)
    {
        var review = await task.ConfigureAwait(false);
        if (!string.Equals(review?.ReviewId, expectedId, StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add($"{label}Mismatch");
        }
    }

    private static async Task ExpectDiagnosticsAsync(
        string label,
        List<string> mismatches,
        Task<IReadOnlyList<RelationDiagnosticsSnapshot>> task,
        string expectedId)
    {
        var snapshots = await task.ConfigureAwait(false);
        if (!snapshots.Any(item => string.Equals(item.DiagnosticId, expectedId, StringComparison.OrdinalIgnoreCase)))
        {
            mismatches.Add($"{label}Missing:{expectedId}");
        }
    }
}
