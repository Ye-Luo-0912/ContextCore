using ContextCore.Abstractions.Models;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.ControlRoom.Services;

/// <summary>Relation governance scoped service mode 扩容 canary；只面向显式 scope rule。</summary>
public sealed class RelationGovernanceScopedExpansionRunner
{
    private readonly FileRelationStore _fileRelationStore;
    private readonly FileRelationReviewStore _fileReviewStore;
    private readonly FileRelationDiagnosticsStore _fileDiagnosticsStore;
    private readonly PostgresRelationStore _postgresRelationStore;
    private readonly PostgresRelationReviewStore _postgresReviewStore;
    private readonly PostgresRelationDiagnosticsStore _postgresDiagnosticsStore;
    private readonly IReadOnlyList<RelationGovernanceScopedRule> _scopes;
    private readonly bool _preflightGatePassed;
    private readonly Func<RelationGovernanceProviderSwitchTrace, CancellationToken, Task> _traceSink;

    public RelationGovernanceScopedExpansionRunner(
        FileRelationStore fileRelationStore,
        FileRelationReviewStore fileReviewStore,
        FileRelationDiagnosticsStore fileDiagnosticsStore,
        PostgresRelationStore postgresRelationStore,
        PostgresRelationReviewStore postgresReviewStore,
        PostgresRelationDiagnosticsStore postgresDiagnosticsStore,
        IReadOnlyList<RelationGovernanceScopedRule> scopes,
        bool preflightGatePassed,
        Func<RelationGovernanceProviderSwitchTrace, CancellationToken, Task> traceSink)
    {
        _fileRelationStore = fileRelationStore;
        _fileReviewStore = fileReviewStore;
        _fileDiagnosticsStore = fileDiagnosticsStore;
        _postgresRelationStore = postgresRelationStore;
        _postgresReviewStore = postgresReviewStore;
        _postgresDiagnosticsStore = postgresDiagnosticsStore;
        _scopes = scopes;
        _preflightGatePassed = preflightGatePassed;
        _traceSink = traceSink;
    }

    public IReadOnlyList<RelationGovernanceScopedExpansionPlan> BuildPlans()
    {
        return
        [
            .. _scopes.Select(scope => new RelationGovernanceScopedExpansionPlan
            {
                ScopeName = scope.ScopeName,
                WorkspaceId = scope.WorkspaceId,
                CollectionId = scope.CollectionId,
                Mode = scope.Mode.ToString(),
                GateStatus = _preflightGatePassed ? "Passed" : "Blocked",
                LastCanaryStatus = _preflightGatePassed ? "ReadyForScopedServiceModeExpansion" : "NotReady",
                AllowedOperations =
                [
                    "relation-edge-read-write",
                    "relation-review-read-write",
                    "diagnostics-read-write",
                    "replacement-chain-lookup",
                    "graph-expansion-preview"
                ],
                FallbackEnabled = true,
                ComparisonTraceEnabled = true,
                RollbackInstruction = $"Disable scope `{scope.ScopeName}` or set RelationGovernanceProviderSwitchOptions.Enabled=false."
            })
        ];
    }

    public async Task<PostgresRelationScopedExpansionReport> RunSmokeAsync(
        bool cleanupConfirm,
        CancellationToken cancellationToken = default)
    {
        var traces = new List<RelationGovernanceProviderSwitchTrace>();
        var blocked = new List<string>();
        var diagnostics = new List<string>();
        var perScope = new List<RelationGovernanceScopedExpansionScopeStatus>();

        if (!_preflightGatePassed)
        {
            blocked.Add("PreflightGateNotPassed");
        }

        if (_scopes.Count < 2)
        {
            blocked.Add("AtLeastTwoScopesRequired");
        }

        foreach (var scope in _scopes)
        {
            if (string.IsNullOrWhiteSpace(scope.ScopeName)
                || string.IsNullOrWhiteSpace(scope.WorkspaceId)
                || string.IsNullOrWhiteSpace(scope.CollectionId))
            {
                blocked.Add("ScopeConfigurationIncomplete");
                break;
            }
        }

        if (blocked.Count > 0)
        {
            return BuildReport(false, traces, perScope, false, 0, diagnostics, blocked, "GateNotPassed");
        }

        foreach (var scope in _scopes)
        {
            var start = traces.Count;
            var scopeFileRoot = Path.Combine(Path.GetTempPath(), "contextcore-relation-scoped-expansion-scope", Guid.NewGuid().ToString("N"));
            var scopeFileOptions = new FileStorageOptions { RootPath = scopeFileRoot };
            var scopeFilePaths = new FilePathResolver(scopeFileOptions);
            var scopeFileSerializer = new FileFormatSerializer();
            try
            {
                var runner = new RelationGovernanceExtendedCanaryRunner(
                    new FileRelationStore(scopeFileOptions),
                    new FileRelationReviewStore(scopeFilePaths, scopeFileSerializer),
                    new FileRelationDiagnosticsStore(scopeFilePaths, scopeFileSerializer),
                    _postgresRelationStore,
                    _postgresReviewStore,
                    _postgresDiagnosticsStore,
                    new RelationGovernanceExtendedCanaryOptions
                    {
                        Enabled = true,
                        WorkspaceAllowlist = [scope.WorkspaceId],
                        CollectionAllowlist = [scope.CollectionId],
                        Mode = scope.Mode,
                        FallbackToFileSystem = true,
                        ContinueComparisonTrace = true,
                        FailClosedOnMismatch = true,
                        RequireScopedServiceModeGate = true
                    },
                    _preflightGatePassed,
                    async (trace, token) =>
                    {
                        traces.Add(trace);
                        await _traceSink(trace, token).ConfigureAwait(false);
                    });

                var scopeReport = await runner.RunAsync(scope.WorkspaceId, scope.CollectionId, cleanupConfirm, cancellationToken, scope.ScopeName)
                    .ConfigureAwait(false);
                diagnostics.AddRange(scopeReport.Diagnostics.Select(item => $"{scope.ScopeName}:{item}"));
                blocked.AddRange(scopeReport.BlockedReasons.Select(item => $"{scope.ScopeName}:{item}"));
                perScope.Add(BuildScopeStatus(scope, traces.Skip(start).ToArray(), scopeReport));
            }
            finally
            {
                if (Directory.Exists(scopeFileRoot))
                {
                    Directory.Delete(scopeFileRoot, recursive: true);
                }
            }
        }

        var nonAllowlistedChecked = await ValidateNonAllowlistedScopeAsync(traces, cancellationToken).ConfigureAwait(false);
        if (!nonAllowlistedChecked)
        {
            blocked.Add("NonAllowlistedScopeLeak");
        }

        var cleanupPerformed = cleanupConfirm && await CleanupAsync(cancellationToken).ConfigureAwait(false);
        var mismatchCount = traces.Count(static trace => trace.MismatchDetected) + blocked.Count(item => item.Contains("Mismatch", StringComparison.OrdinalIgnoreCase));
        var failureCount = traces.Count(static trace => !string.IsNullOrWhiteSpace(trace.PostgresError));
        var recommendation = !_preflightGatePassed
            ? "GateNotPassed"
            : !nonAllowlistedChecked
                ? "BlockedByNonAllowlistedScopeLeak"
                : mismatchCount > 0
                    ? "BlockedByMismatch"
                    : failureCount > 0
                        ? "BlockedByPostgresFailure"
                        : traces.Count >= _scopes.Count * 20 && cleanupPerformed
                            ? "ReadyForScopedExpansion"
                            : "NeedsMoreCanaryRuns";

        return BuildReport(
            _preflightGatePassed,
            traces,
            perScope,
            nonAllowlistedChecked,
            fileSystemScopeReadCount: nonAllowlistedChecked ? 1 : 0,
            diagnostics.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            blocked.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            recommendation);
    }

    public PostgresRelationScopedExpansionReport BuildGateReport(
        PostgresRelationScopedExpansionReport? smokeReport,
        bool p15Passed)
    {
        var blocked = new List<string>();
        AddIfFalse(blocked, _preflightGatePassed, "PreflightGateNotPassed");
        AddIfFalse(blocked, _scopes.Count >= 2, "AtLeastTwoScopesRequired");
        AddIfFalse(blocked, smokeReport is not null, "ScopedExpansionSmokeMissing");
        AddIfFalse(blocked, smokeReport?.NonAllowlistedScopeChecked == true, "NonAllowlistedScopeNotChecked");
        AddIfFalse(blocked, smokeReport?.MismatchCount == 0, "MismatchDetected");
        AddIfFalse(blocked, smokeReport?.PostgresFailureCount == 0, "PostgresFailureDetected");
        AddIfFalse(blocked, p15Passed, "P15GateNotPassed");

        if (smokeReport is null)
        {
            return BuildReport(
                _preflightGatePassed,
                [],
                [],
                false,
                0,
                ["RunPostgresRelationScopedExpansionSmokeFirst"],
                blocked,
                "GateNotPassed");
        }

        return new PostgresRelationScopedExpansionReport
        {
            GatePassed = blocked.Count == 0,
            ScopeCount = smokeReport.ScopeCount,
            AllowlistedScopeCount = smokeReport.AllowlistedScopeCount,
            NonAllowlistedScopeChecked = smokeReport.NonAllowlistedScopeChecked,
            OperationCount = smokeReport.OperationCount,
            PostgresPrimaryReadCount = smokeReport.PostgresPrimaryReadCount,
            PostgresPrimaryWriteCount = smokeReport.PostgresPrimaryWriteCount,
            FileSystemScopeReadCount = smokeReport.FileSystemScopeReadCount,
            FallbackCount = smokeReport.FallbackCount,
            ComparisonTraceCount = smokeReport.ComparisonTraceCount,
            MismatchCount = smokeReport.MismatchCount,
            PostgresFailureCount = smokeReport.PostgresFailureCount,
            AveragePostgresReadMs = smokeReport.AveragePostgresReadMs,
            AveragePostgresWriteMs = smokeReport.AveragePostgresWriteMs,
            Plans = BuildPlans(),
            PerScopeStatus = smokeReport.PerScopeStatus,
            Diagnostics = smokeReport.Diagnostics,
            BlockedReasons = blocked.Concat(smokeReport.BlockedReasons).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Recommendation = blocked.Count == 0
                ? "ReadyForScopedExpansion"
                : smokeReport.MismatchCount > 0
                    ? "BlockedByMismatch"
                    : smokeReport.PostgresFailureCount > 0
                        ? "BlockedByPostgresFailure"
                        : smokeReport.NonAllowlistedScopeChecked
                            ? "GateNotPassed"
                            : "BlockedByNonAllowlistedScopeLeak"
        };
    }

    private async Task<bool> ValidateNonAllowlistedScopeAsync(
        List<RelationGovernanceProviderSwitchTrace> traces,
        CancellationToken cancellationToken)
    {
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
                Mode = RelationGovernanceProviderMode.FileSystemPrimary,
                ScopedRules = _scopes,
                FallbackToFileSystem = true,
                ContinueComparisonTrace = true,
                FailClosedOnMismatch = true,
                RequireReadinessGate = true,
                ScopeName = "db2.10-scoped-expansion",
                ScopeDescription = "explicit multi-scope expansion canary",
                RolloutStage = "selected-workspace-expansion"
            },
            _preflightGatePassed,
            _preflightGatePassed,
            async (trace, token) =>
            {
                traces.Add(trace);
                await _traceSink(trace, token).ConfigureAwait(false);
            });

        var relation = new ContextRelation
        {
            Id = "scoped-expansion-outside-relation",
            WorkspaceId = "contextcore_scoped_expansion_outside",
            CollectionId = "relation-governance-scoped-expansion-outside",
            SourceId = "outside-source",
            TargetId = "outside-target",
            RelationType = ContextRelationTypes.References,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await router.SaveRelationAsync("scoped-expansion-outside-write", relation, cancellationToken).ConfigureAwait(false);
        var fileRelation = await _fileRelationStore.GetAsync(relation.WorkspaceId, relation.CollectionId, relation.Id, cancellationToken)
            .ConfigureAwait(false);
        var postgresRelation = await _postgresRelationStore.GetAsync(relation.WorkspaceId, relation.CollectionId, relation.Id, cancellationToken)
            .ConfigureAwait(false);
        return string.Equals(fileRelation?.Id, relation.Id, StringComparison.OrdinalIgnoreCase) && postgresRelation is null;
    }

    private async Task<bool> CleanupAsync(CancellationToken cancellationToken)
    {
        foreach (var scope in _scopes)
        {
            await _postgresReviewStore.DeleteByScopeAsync(scope.WorkspaceId, scope.CollectionId, cancellationToken).ConfigureAwait(false);
            await _postgresDiagnosticsStore.DeleteByScopeAsync(scope.WorkspaceId, scope.CollectionId, cancellationToken).ConfigureAwait(false);
            foreach (var relationId in ExtendedRelationIds())
            {
                await _postgresRelationStore.DeleteAsync(scope.WorkspaceId, scope.CollectionId, relationId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return true;
    }

    private PostgresRelationScopedExpansionReport BuildReport(
        bool gatePassed,
        IReadOnlyList<RelationGovernanceProviderSwitchTrace> traces,
        IReadOnlyList<RelationGovernanceScopedExpansionScopeStatus> perScope,
        bool nonAllowlistedChecked,
        int fileSystemScopeReadCount,
        IReadOnlyList<string> diagnostics,
        IReadOnlyList<string> blocked,
        string recommendation)
    {
        var readTraces = traces.Where(IsReadTrace).ToArray();
        var writeTraces = traces.Where(IsWriteTrace).ToArray();
        return new PostgresRelationScopedExpansionReport
        {
            GatePassed = gatePassed,
            ScopeCount = _scopes.Count,
            AllowlistedScopeCount = _scopes.Count(static scope => scope.Enabled),
            NonAllowlistedScopeChecked = nonAllowlistedChecked,
            OperationCount = traces.Count,
            PostgresPrimaryReadCount = readTraces.Count(static trace => string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)),
            PostgresPrimaryWriteCount = writeTraces.Count(static trace => string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)),
            FileSystemScopeReadCount = fileSystemScopeReadCount,
            FallbackCount = traces.Count(static trace => trace.FallbackUsed),
            ComparisonTraceCount = traces.Count(static trace => string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)),
            MismatchCount = traces.Count(static trace => trace.MismatchDetected) + blocked.Count(item => item.Contains("Mismatch", StringComparison.OrdinalIgnoreCase)),
            PostgresFailureCount = traces.Count(static trace => !string.IsNullOrWhiteSpace(trace.PostgresError)),
            AveragePostgresReadMs = AverageDuration(readTraces),
            AveragePostgresWriteMs = AverageDuration(writeTraces),
            Plans = BuildPlans(),
            PerScopeStatus = perScope,
            Diagnostics = diagnostics,
            BlockedReasons = blocked,
            Recommendation = recommendation
        };
    }

    private static RelationGovernanceScopedExpansionScopeStatus BuildScopeStatus(
        RelationGovernanceScopedRule scope,
        IReadOnlyList<RelationGovernanceProviderSwitchTrace> traces,
        PostgresRelationScopedExtendedCanaryReport report)
    {
        return new RelationGovernanceScopedExpansionScopeStatus
        {
            ScopeName = scope.ScopeName,
            WorkspaceId = scope.WorkspaceId,
            CollectionId = scope.CollectionId,
            Mode = scope.Mode.ToString(),
            RolloutStage = scope.RolloutStage,
            OperationCount = traces.Count,
            PostgresPrimaryReadCount = report.PostgresPrimaryReadCount,
            PostgresPrimaryWriteCount = report.PostgresPrimaryWriteCount,
            FallbackCount = report.FileSystemFallbackCount,
            MismatchCount = report.MismatchCount,
            PostgresFailureCount = report.PostgresFailureCount,
            Recommendation = report.Recommendation
        };
    }

    private static IReadOnlyList<string> ExtendedRelationIds()
    {
        return
        [
            "extended-rel-main",
            "extended-rel-delete",
            "extended-rel-old-to-new",
            "extended-rel-new-to-old",
            "extended-rel-conflict"
        ];
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

    private static double AverageDuration(IReadOnlyList<RelationGovernanceProviderSwitchTrace> traces)
    {
        return traces.Count == 0 ? 0 : traces.Average(static trace => trace.DurationMs);
    }

    private static void AddIfFalse(ICollection<string> blocked, bool passed, string reason)
    {
        if (!passed)
        {
            blocked.Add(reason);
        }
    }
}
