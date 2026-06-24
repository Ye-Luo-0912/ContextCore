using ContextCore.Abstractions.Models;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.ControlRoom.Services;

/// <summary>受控 selected workspace 的 relation governance canary；仍禁止全局默认启用。</summary>
public sealed class RelationGovernanceSelectedWorkspaceCanaryRunner
{
    private static readonly string[] ExtendedRelationIds =
    [
        "extended-rel-main",
        "extended-rel-delete",
        "extended-rel-old-to-new",
        "extended-rel-new-to-old",
        "extended-rel-conflict",
        "selected-api-roundtrip-relation"
    ];

    private readonly FileRelationStore _fileRelationStore;
    private readonly FileRelationReviewStore _fileReviewStore;
    private readonly FileRelationDiagnosticsStore _fileDiagnosticsStore;
    private readonly PostgresRelationStore _postgresRelationStore;
    private readonly PostgresRelationReviewStore _postgresReviewStore;
    private readonly PostgresRelationDiagnosticsStore _postgresDiagnosticsStore;
    private readonly RelationGovernanceSelectedWorkspaceCanaryOptions _options;
    private readonly bool _preflightGatePassed;
    private readonly Func<RelationGovernanceProviderSwitchTrace, CancellationToken, Task> _traceSink;

    public RelationGovernanceSelectedWorkspaceCanaryRunner(
        FileRelationStore fileRelationStore,
        FileRelationReviewStore fileReviewStore,
        FileRelationDiagnosticsStore fileDiagnosticsStore,
        PostgresRelationStore postgresRelationStore,
        PostgresRelationReviewStore postgresReviewStore,
        PostgresRelationDiagnosticsStore postgresDiagnosticsStore,
        RelationGovernanceSelectedWorkspaceCanaryOptions options,
        bool preflightGatePassed,
        Func<RelationGovernanceProviderSwitchTrace, CancellationToken, Task> traceSink)
    {
        _fileRelationStore = fileRelationStore;
        _fileReviewStore = fileReviewStore;
        _fileDiagnosticsStore = fileDiagnosticsStore;
        _postgresRelationStore = postgresRelationStore;
        _postgresReviewStore = postgresReviewStore;
        _postgresDiagnosticsStore = postgresDiagnosticsStore;
        _options = options;
        _preflightGatePassed = preflightGatePassed;
        _traceSink = traceSink;
    }

    public async Task<PostgresRelationSelectedWorkspaceCanaryReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var traces = new List<RelationGovernanceProviderSwitchTrace>();
        var blocked = new List<string>();
        var diagnostics = new List<string>();
        var mismatches = new List<string>();

        if (!_options.Enabled)
        {
            blocked.Add("SelectedWorkspaceCanaryDisabled");
        }

        if (string.IsNullOrWhiteSpace(_options.WorkspaceId) || string.IsNullOrWhiteSpace(_options.CollectionId))
        {
            blocked.Add("SelectedScopeMissing");
        }

        if (_options.RequireExtendedCanaryPassed && !_preflightGatePassed)
        {
            blocked.Add("ExtendedCanaryOrPreflightGateNotPassed");
        }

        if (blocked.Count > 0)
        {
            return BuildReport(
                gatePassed: false,
                traces,
                mismatches,
                diagnostics,
                blocked,
                graphParity: false,
                reviewParity: false,
                diagnosticsParity: false,
                replacementParity: false,
                controlRoomReadPathPassed: false,
                clientApiRoundtripPathPassed: false,
                nonSelectedScopeRemainsFileSystem: false,
                cleanupPerformed: false,
                recommendation: "GateNotPassed");
        }

        var extendedRunner = new RelationGovernanceExtendedCanaryRunner(
            _fileRelationStore,
            _fileReviewStore,
            _fileDiagnosticsStore,
            _postgresRelationStore,
            _postgresReviewStore,
            _postgresDiagnosticsStore,
            new RelationGovernanceExtendedCanaryOptions
            {
                Enabled = true,
                WorkspaceAllowlist = [_options.WorkspaceId],
                CollectionAllowlist = [_options.CollectionId],
                Mode = _options.Mode,
                FallbackToFileSystem = _options.FallbackToFileSystem,
                ContinueComparisonTrace = _options.ContinueComparisonTrace,
                FailClosedOnMismatch = _options.FailClosedOnMismatch,
                RequireScopedServiceModeGate = _options.RequireExtendedCanaryPassed
            },
            _preflightGatePassed,
            async (trace, token) =>
            {
                traces.Add(trace);
                await _traceSink(trace, token).ConfigureAwait(false);
            });

        var extended = await extendedRunner.RunAsync(_options.WorkspaceId, _options.CollectionId, cleanupConfirm: false, cancellationToken)
            .ConfigureAwait(false);
        mismatches.AddRange(extended.BlockedReasons);
        diagnostics.AddRange(extended.Diagnostics);

        var router = CreateRouter(traces);
        var controlRoomReadPathPassed = await ValidateControlRoomReadPathAsync(router, mismatches, cancellationToken)
            .ConfigureAwait(false);
        var clientApiRoundtripPathPassed = await ValidateClientApiRoundtripAsync(router, mismatches, cancellationToken)
            .ConfigureAwait(false);
        var nonSelectedScopeRemainsFileSystem = await ValidateNonSelectedScopeAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!nonSelectedScopeRemainsFileSystem)
        {
            mismatches.Add("NonSelectedScopeDidNotRemainFileSystem");
        }

        var cleanupPerformed = await CleanupAsync(cancellationToken).ConfigureAwait(false);
        var mismatchCount = traces.Count(static trace => trace.MismatchDetected) + mismatches.Count;
        var postgresFailureCount = traces.Count(static trace => !string.IsNullOrWhiteSpace(trace.PostgresError));
        var recommendation = !_preflightGatePassed
            ? "GateNotPassed"
            : mismatchCount > 0
                ? "BlockedByMismatch"
                : postgresFailureCount > 0
                    ? "BlockedByPostgresFailure"
                    : HasLatencyRisk(traces)
                        ? "BlockedByLatency"
                        : traces.Count >= Math.Min(_options.MaxOperations, 20)
                          && extended.GraphExpansionPreviewParityPassed
                          && extended.ReviewLifecycleParityPassed
                          && extended.DiagnosticsParityPassed
                          && extended.ReplacementChainParityPassed
                          && controlRoomReadPathPassed
                          && clientApiRoundtripPathPassed
                          && nonSelectedScopeRemainsFileSystem
                            ? "ReadyForScopedServiceModeExpansion"
                            : "NeedsMoreCanaryRuns";

        return BuildReport(
            gatePassed: _preflightGatePassed,
            traces,
            mismatches,
            diagnostics.Count == 0 ? ["SelectedWorkspaceCanaryScopedOnly"] : diagnostics.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            blocked,
            extended.GraphExpansionPreviewParityPassed,
            extended.ReviewLifecycleParityPassed,
            extended.DiagnosticsParityPassed,
            extended.ReplacementChainParityPassed,
            controlRoomReadPathPassed,
            clientApiRoundtripPathPassed,
            nonSelectedScopeRemainsFileSystem,
            cleanupPerformed,
            recommendation);
    }

    private RelationGovernanceProviderRouter CreateRouter(List<RelationGovernanceProviderSwitchTrace> traces)
    {
        return new RelationGovernanceProviderRouter(
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
                AllowedWorkspaces = [_options.WorkspaceId],
                AllowedCollections = [_options.CollectionId],
                FallbackToFileSystem = _options.FallbackToFileSystem,
                ContinueComparisonTrace = _options.ContinueComparisonTrace,
                FailClosedOnMismatch = _options.FailClosedOnMismatch,
                RequireReadinessGate = _options.RequireExtendedCanaryPassed
            },
            _preflightGatePassed,
            _preflightGatePassed,
            async (trace, token) =>
            {
                traces.Add(trace);
                await _traceSink(trace, token).ConfigureAwait(false);
            });
    }

    private async Task<bool> ValidateControlRoomReadPathAsync(
        RelationGovernanceProviderRouter router,
        ICollection<string> mismatches,
        CancellationToken cancellationToken)
    {
        var relations = await router.QueryRelationsAsync(
            "selected-controlroom-read-relations",
            _options.WorkspaceId,
            _options.CollectionId,
            cancellationToken).ConfigureAwait(false);
        var explainRelation = await router.GetRelationAsync(
            "selected-controlroom-read-explain",
            _options.WorkspaceId,
            _options.CollectionId,
            "extended-rel-main",
            cancellationToken).ConfigureAwait(false);
        var passed = relations.Count > 0
                     && string.Equals(explainRelation?.Id, "extended-rel-main", StringComparison.OrdinalIgnoreCase);
        if (!passed)
        {
            mismatches.Add("ControlRoomRelationReadPathMismatch");
        }

        return passed;
    }

    private async Task<bool> ValidateClientApiRoundtripAsync(
        RelationGovernanceProviderRouter router,
        ICollection<string> mismatches,
        CancellationToken cancellationToken)
    {
        var relation = new ContextRelation
        {
            Id = "selected-api-roundtrip-relation",
            WorkspaceId = _options.WorkspaceId,
            CollectionId = _options.CollectionId,
            SourceId = "selected-api-source",
            TargetId = "selected-api-target",
            RelationType = ContextRelationTypes.References,
            Weight = 0.7,
            Confidence = 1.0,
            SourceRefs = ["selected-canary:api-roundtrip"],
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lifecycle"] = "Active",
                ["reviewStatus"] = "Reviewed",
                ["targetLifecycle"] = "Active",
                ["evidenceRefs"] = "selected-canary-evidence:api-roundtrip",
                ["source"] = "relation_governance_selected_workspace_canary"
            },
            CreatedAt = DateTimeOffset.UtcNow
        };

        await router.SaveRelationAsync("selected-client-api-write", relation, cancellationToken).ConfigureAwait(false);
        var loaded = await router.GetRelationAsync("selected-client-api-read", _options.WorkspaceId, _options.CollectionId, relation.Id, cancellationToken)
            .ConfigureAwait(false);
        await router.DeleteRelationAsync("selected-client-api-delete", _options.WorkspaceId, _options.CollectionId, relation.Id, cancellationToken)
            .ConfigureAwait(false);
        var deleted = await router.GetRelationAsync("selected-client-api-read-after-delete", _options.WorkspaceId, _options.CollectionId, relation.Id, cancellationToken)
            .ConfigureAwait(false);

        var passed = string.Equals(loaded?.Id, relation.Id, StringComparison.OrdinalIgnoreCase) && deleted is null;
        if (!passed)
        {
            mismatches.Add("ClientApiRoundtripPathMismatch");
        }

        return passed;
    }

    private async Task<bool> ValidateNonSelectedScopeAsync(CancellationToken cancellationToken)
    {
        var relation = new ContextRelation
        {
            Id = "selected-non-selected-scope-relation",
            WorkspaceId = $"{_options.WorkspaceId}-outside",
            CollectionId = _options.CollectionId,
            SourceId = "selected-outside-source",
            TargetId = "selected-outside-target",
            RelationType = ContextRelationTypes.References,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _fileRelationStore.SaveAsync(relation, cancellationToken).ConfigureAwait(false);
        var fileRelation = await _fileRelationStore.GetAsync(relation.WorkspaceId, relation.CollectionId, relation.Id, cancellationToken)
            .ConfigureAwait(false);
        var postgresRelation = await _postgresRelationStore.GetAsync(relation.WorkspaceId, relation.CollectionId, relation.Id, cancellationToken)
            .ConfigureAwait(false);
        return string.Equals(fileRelation?.Id, relation.Id, StringComparison.OrdinalIgnoreCase) && postgresRelation is null;
    }

    private async Task<bool> CleanupAsync(CancellationToken cancellationToken)
    {
        await _postgresReviewStore.DeleteByScopeAsync(_options.WorkspaceId, _options.CollectionId, cancellationToken).ConfigureAwait(false);
        await _postgresDiagnosticsStore.DeleteByScopeAsync(_options.WorkspaceId, _options.CollectionId, cancellationToken).ConfigureAwait(false);
        foreach (var relationId in ExtendedRelationIds)
        {
            await _postgresRelationStore.DeleteAsync(_options.WorkspaceId, _options.CollectionId, relationId, cancellationToken)
                .ConfigureAwait(false);
        }

        return true;
    }

    private PostgresRelationSelectedWorkspaceCanaryReport BuildReport(
        bool gatePassed,
        IReadOnlyList<RelationGovernanceProviderSwitchTrace> traces,
        IReadOnlyList<string> mismatches,
        IReadOnlyList<string> diagnostics,
        IReadOnlyList<string> blocked,
        bool graphParity,
        bool reviewParity,
        bool diagnosticsParity,
        bool replacementParity,
        bool controlRoomReadPathPassed,
        bool clientApiRoundtripPathPassed,
        bool nonSelectedScopeRemainsFileSystem,
        bool cleanupPerformed,
        string recommendation)
    {
        var readTraces = traces.Where(IsReadTrace).ToArray();
        var writeTraces = traces.Where(IsWriteTrace).ToArray();
        var fallbackTraces = traces.Where(static trace => trace.FallbackUsed).ToArray();
        return new PostgresRelationSelectedWorkspaceCanaryReport
        {
            GatePassed = gatePassed,
            WorkspaceId = _options.WorkspaceId,
            CollectionId = _options.CollectionId,
            ProviderMode = _options.Mode.ToString(),
            OperationCount = traces.Count,
            PostgresPrimaryReadCount = readTraces.Count(static trace => string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)),
            PostgresPrimaryWriteCount = writeTraces.Count(static trace => string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)),
            FileSystemFallbackCount = fallbackTraces.Length,
            ComparisonTraceCount = traces.Count(static trace => string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)),
            MismatchCount = traces.Count(static trace => trace.MismatchDetected) + mismatches.Count,
            PostgresFailureCount = traces.Count(static trace => !string.IsNullOrWhiteSpace(trace.PostgresError)),
            AveragePostgresReadMs = AverageDuration(readTraces),
            AveragePostgresWriteMs = AverageDuration(writeTraces),
            AverageFileSystemFallbackMs = AverageDuration(fallbackTraces),
            GraphExpansionPreviewParityPassed = graphParity,
            ReviewLifecycleParityPassed = reviewParity,
            DiagnosticsParityPassed = diagnosticsParity,
            ReplacementChainParityPassed = replacementParity,
            ControlRoomReadPathPassed = controlRoomReadPathPassed,
            ClientApiRoundtripPathPassed = clientApiRoundtripPathPassed,
            NonSelectedScopeRemainsFileSystem = nonSelectedScopeRemainsFileSystem,
            CleanupPerformed = cleanupPerformed,
            RollbackInstruction = "Set RelationGovernanceProviderSwitchOptions.Enabled=false or remove the selected workspace/collection from allowlist.",
            Diagnostics = diagnostics,
            BlockedReasons = blocked.Concat(mismatches).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Recommendation = recommendation
        };
    }

    private static bool IsReadTrace(RelationGovernanceProviderSwitchTrace trace)
    {
        return trace.OperationKind.Contains("Query", StringComparison.OrdinalIgnoreCase)
               || trace.OperationKind.Contains("Get", StringComparison.OrdinalIgnoreCase)
               || trace.OperationKind.Contains("Latest", StringComparison.OrdinalIgnoreCase)
               || trace.OperationKind.StartsWith("RelationDiagnosticsBy", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWriteTrace(RelationGovernanceProviderSwitchTrace trace)
    {
        return trace.OperationKind.Contains("Write", StringComparison.OrdinalIgnoreCase)
               || trace.OperationKind.Contains("Delete", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasLatencyRisk(IReadOnlyList<RelationGovernanceProviderSwitchTrace> traces)
    {
        return traces.Count > 0 && traces.Average(static trace => trace.DurationMs) > 5000;
    }

    private static double AverageDuration(IReadOnlyList<RelationGovernanceProviderSwitchTrace> traces)
    {
        return traces.Count == 0 ? 0 : traces.Average(static trace => trace.DurationMs);
    }
}
