using ContextCore.Abstractions.Models;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.ControlRoom.Services;

/// <summary>受控 normal workspace 的 relation governance canary；只验证显式 scope，不升级全局默认 provider。</summary>
public sealed class RelationGovernanceSelectedNormalWorkspaceRunner
{
    private const string DefaultIdPrefix = "selected-normal";

    private readonly FileRelationStore _fileRelationStore;
    private readonly FileRelationReviewStore _fileReviewStore;
    private readonly FileRelationDiagnosticsStore _fileDiagnosticsStore;
    private readonly PostgresRelationStore _postgresRelationStore;
    private readonly PostgresRelationReviewStore _postgresReviewStore;
    private readonly PostgresRelationDiagnosticsStore _postgresDiagnosticsStore;
    private readonly RelationGovernanceSelectedNormalWorkspaceOptions _options;
    private readonly bool _scopedObservationGatePassed;
    private readonly Func<RelationGovernanceProviderSwitchTrace, CancellationToken, Task> _traceSink;
    private readonly string _idPrefix;

    public RelationGovernanceSelectedNormalWorkspaceRunner(
        FileRelationStore fileRelationStore,
        FileRelationReviewStore fileReviewStore,
        FileRelationDiagnosticsStore fileDiagnosticsStore,
        PostgresRelationStore postgresRelationStore,
        PostgresRelationReviewStore postgresReviewStore,
        PostgresRelationDiagnosticsStore postgresDiagnosticsStore,
        RelationGovernanceSelectedNormalWorkspaceOptions options,
        bool scopedObservationGatePassed,
        Func<RelationGovernanceProviderSwitchTrace, CancellationToken, Task> traceSink)
    {
        _fileRelationStore = fileRelationStore;
        _fileReviewStore = fileReviewStore;
        _fileDiagnosticsStore = fileDiagnosticsStore;
        _postgresRelationStore = postgresRelationStore;
        _postgresReviewStore = postgresReviewStore;
        _postgresDiagnosticsStore = postgresDiagnosticsStore;
        _options = options;
        _scopedObservationGatePassed = scopedObservationGatePassed;
        _traceSink = traceSink;
        _idPrefix = string.IsNullOrWhiteSpace(options.CanaryIdPrefix)
            ? DefaultIdPrefix
            : BuildCanaryIdPrefix(options.CanaryIdPrefix);
    }

    public async Task<PostgresRelationSelectedNormalWorkspaceCanaryReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var traces = new List<RelationGovernanceProviderSwitchTrace>();
        var blocked = new List<string>();
        var diagnostics = new List<string>();
        var mismatches = new List<string>();

        if (!_options.Enabled)
        {
            blocked.Add("SelectedNormalCanaryDisabled");
        }

        if (string.IsNullOrWhiteSpace(_options.WorkspaceId) || string.IsNullOrWhiteSpace(_options.CollectionId))
        {
            blocked.Add("SelectedNormalScopeMissing");
        }

        if (_options.RequireScopedObservationPassed && !_scopedObservationGatePassed)
        {
            blocked.Add("ScopedObservationQualityNotPassed");
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
                RequireScopedServiceModeGate = _options.RequireScopedObservationPassed
            },
            _scopedObservationGatePassed,
            async (trace, token) =>
            {
                traces.Add(trace);
                await _traceSink(trace, token).ConfigureAwait(false);
            });

        var extended = await extendedRunner.RunAsync(
                _options.WorkspaceId,
                _options.CollectionId,
                cleanupConfirm: false,
                cancellationToken,
                _idPrefix)
            .ConfigureAwait(false);
        mismatches.AddRange(extended.BlockedReasons);
        diagnostics.AddRange(extended.Diagnostics);

        var router = CreateRouter(traces);
        var controlRoomReadPathPassed = await ValidateControlRoomReadPathAsync(router, mismatches, cancellationToken)
            .ConfigureAwait(false);
        var clientApiRoundtripPathPassed = await ValidateClientApiRoundtripAsync(router, mismatches, cancellationToken)
            .ConfigureAwait(false);
        var nonSelectedScopeRemainsFileSystem = await ValidateNonSelectedNormalScopeAsync(mismatches, cancellationToken)
            .ConfigureAwait(false);
        var cleanupPerformed = await CleanupCanaryRelationsAsync(cancellationToken).ConfigureAwait(false);
        if (_options.CleanupMode != RelationGovernanceSelectedNormalWorkspaceCleanupMode.None)
        {
            diagnostics.Add("ReviewDiagnosticsCleanupSkippedToAvoidScopeDelete");
        }

        var mismatchCount = traces.Count(static trace => trace.MismatchDetected) + mismatches.Count;
        var postgresFailureCount = traces.Count(static trace => !string.IsNullOrWhiteSpace(trace.PostgresError));
        var scopeLeakCount = nonSelectedScopeRemainsFileSystem ? 0 : 1;
        var recommendation = !_scopedObservationGatePassed
            ? "GateNotPassed"
            : scopeLeakCount > 0
                ? "BlockedByScopeLeak"
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
                                ? "ReadyForLimitedNormalScope"
                                : "NeedsMoreObservation";

        return BuildReport(
            gatePassed: _scopedObservationGatePassed,
            traces,
            mismatches,
            diagnostics.Count == 0 ? ["SelectedNormalWorkspaceCanaryScopedOnly"] : diagnostics.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
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
                RequireReadinessGate = _options.RequireScopedObservationPassed
            },
            _scopedObservationGatePassed,
            _scopedObservationGatePassed,
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
        var mainRelationId = $"{_idPrefix}-extended-rel-main";
        var relations = await router.QueryRelationsAsync(
            "selected-normal-controlroom-read-relations",
            _options.WorkspaceId,
            _options.CollectionId,
            cancellationToken).ConfigureAwait(false);
        var explainRelation = await router.GetRelationAsync(
            "selected-normal-controlroom-read-explain",
            _options.WorkspaceId,
            _options.CollectionId,
            mainRelationId,
            cancellationToken).ConfigureAwait(false);
        var passed = relations.Count > 0
                     && string.Equals(explainRelation?.Id, mainRelationId, StringComparison.OrdinalIgnoreCase);
        if (!passed)
        {
            mismatches.Add("SelectedNormalControlRoomReadPathMismatch");
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
            Id = $"{_idPrefix}-api-roundtrip-relation",
            WorkspaceId = _options.WorkspaceId,
            CollectionId = _options.CollectionId,
            SourceId = $"{_idPrefix}-api-source",
            TargetId = $"{_idPrefix}-api-target",
            RelationType = ContextRelationTypes.References,
            Weight = 0.7,
            Confidence = 1.0,
            SourceRefs = ["selected-normal-canary:api-roundtrip"],
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lifecycle"] = "Active",
                ["reviewStatus"] = "Reviewed",
                ["targetLifecycle"] = "Active",
                ["evidenceRefs"] = "selected-normal-canary-evidence:api-roundtrip",
                ["source"] = "relation_governance_selected_normal_workspace_canary"
            },
            CreatedAt = DateTimeOffset.UtcNow
        };

        await router.SaveRelationAsync("selected-normal-client-api-write", relation, cancellationToken).ConfigureAwait(false);
        var loaded = await router.GetRelationAsync("selected-normal-client-api-read", _options.WorkspaceId, _options.CollectionId, relation.Id, cancellationToken)
            .ConfigureAwait(false);
        await router.DeleteRelationAsync("selected-normal-client-api-delete", _options.WorkspaceId, _options.CollectionId, relation.Id, cancellationToken)
            .ConfigureAwait(false);
        var deleted = await router.GetRelationAsync("selected-normal-client-api-read-after-delete", _options.WorkspaceId, _options.CollectionId, relation.Id, cancellationToken)
            .ConfigureAwait(false);

        var passed = string.Equals(loaded?.Id, relation.Id, StringComparison.OrdinalIgnoreCase) && deleted is null;
        if (!passed)
        {
            mismatches.Add("SelectedNormalClientApiRoundtripMismatch");
        }

        return passed;
    }

    private async Task<bool> ValidateNonSelectedNormalScopeAsync(
        ICollection<string> mismatches,
        CancellationToken cancellationToken)
    {
        var relation = new ContextRelation
        {
            Id = $"{_idPrefix}-outside-scope-relation",
            WorkspaceId = $"{_options.WorkspaceId}-outside",
            CollectionId = _options.CollectionId,
            SourceId = $"{_idPrefix}-outside-source",
            TargetId = $"{_idPrefix}-outside-target",
            RelationType = ContextRelationTypes.References,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _fileRelationStore.SaveAsync(relation, cancellationToken).ConfigureAwait(false);
        var fileRelation = await _fileRelationStore.GetAsync(relation.WorkspaceId, relation.CollectionId, relation.Id, cancellationToken)
            .ConfigureAwait(false);
        var postgresRelation = await _postgresRelationStore.GetAsync(relation.WorkspaceId, relation.CollectionId, relation.Id, cancellationToken)
            .ConfigureAwait(false);
        var passed = string.Equals(fileRelation?.Id, relation.Id, StringComparison.OrdinalIgnoreCase) && postgresRelation is null;
        if (!passed)
        {
            mismatches.Add("SelectedNormalNonSelectedScopeLeak");
        }

        return passed;
    }

    private async Task<bool> CleanupCanaryRelationsAsync(CancellationToken cancellationToken)
    {
        if (_options.CleanupMode == RelationGovernanceSelectedNormalWorkspaceCleanupMode.None)
        {
            return false;
        }

        foreach (var relationId in BuildCanaryRelationIds(_idPrefix))
        {
            await _postgresRelationStore.DeleteAsync(_options.WorkspaceId, _options.CollectionId, relationId, cancellationToken)
                .ConfigureAwait(false);
        }

        return true;
    }

    private PostgresRelationSelectedNormalWorkspaceCanaryReport BuildReport(
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
        return new PostgresRelationSelectedNormalWorkspaceCanaryReport
        {
            GatePassed = gatePassed,
            WorkspaceId = _options.WorkspaceId,
            CollectionId = _options.CollectionId,
            ProviderMode = _options.Mode.ToString(),
            OperationCount = traces.Count,
            PostgresPrimaryReadCount = readTraces.Count(static trace => string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)),
            PostgresPrimaryWriteCount = writeTraces.Count(static trace => string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)),
            FileSystemFallbackCount = traces.Count(static trace => trace.FallbackUsed),
            ComparisonTraceCount = traces.Count(static trace => string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)),
            MismatchCount = traces.Count(static trace => trace.MismatchDetected) + mismatches.Count,
            PostgresFailureCount = traces.Count(static trace => !string.IsNullOrWhiteSpace(trace.PostgresError)),
            ScopeLeakCount = nonSelectedScopeRemainsFileSystem ? 0 : 1,
            AveragePostgresReadMs = AverageDuration(readTraces),
            P95PostgresReadMs = PercentileDuration(readTraces, 0.95),
            AveragePostgresWriteMs = AverageDuration(writeTraces),
            P95PostgresWriteMs = PercentileDuration(writeTraces, 0.95),
            GraphExpansionPreviewParityPassed = graphParity,
            ReviewLifecycleParityPassed = reviewParity,
            DiagnosticsParityPassed = diagnosticsParity,
            ReplacementChainParityPassed = replacementParity,
            ControlRoomReadPathPassed = controlRoomReadPathPassed,
            ClientApiRoundtripPathPassed = clientApiRoundtripPathPassed,
            NonSelectedNormalScopeRemainsFileSystem = nonSelectedScopeRemainsFileSystem,
            CleanupPerformed = cleanupPerformed,
            RollbackInstruction = "Remove the selected normal workspace/collection from allowlist or set RelationGovernanceProviderSwitchOptions.Enabled=false.",
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

    private static double PercentileDuration(IReadOnlyList<RelationGovernanceProviderSwitchTrace> traces, double percentile)
    {
        if (traces.Count == 0)
        {
            return 0;
        }

        var ordered = traces.Select(static trace => trace.DurationMs).OrderBy(static value => value).ToArray();
        var index = (int)Math.Ceiling(percentile * ordered.Length) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    private static IReadOnlyList<string> BuildCanaryRelationIds(string idPrefix)
    {
        return
        [
            $"{idPrefix}-extended-rel-main",
            $"{idPrefix}-extended-rel-delete",
            $"{idPrefix}-extended-rel-old-to-new",
            $"{idPrefix}-extended-rel-new-to-old",
            $"{idPrefix}-extended-rel-conflict",
            $"{idPrefix}-api-roundtrip-relation"
        ];
    }

    private static string BuildCanaryIdPrefix(string value)
    {
        Span<char> buffer = stackalloc char[Math.Min(value.Length, 64)];
        var index = 0;
        foreach (var ch in value)
        {
            if (index >= buffer.Length)
            {
                break;
            }

            buffer[index++] = char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-';
        }

        var normalized = new string(buffer[..index]).Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? DefaultIdPrefix : normalized;
    }
}
