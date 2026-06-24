using ContextCore.Abstractions.Models;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.ControlRoom.Services;

/// <summary>多个 normal scope 的受控 canary；只扩大显式 scope，不启用全局默认 provider。</summary>
public sealed class RelationGovernanceMultiNormalScopeCanaryRunner
{
    private readonly FileRelationStore _fileRelationStore;
    private readonly FileRelationReviewStore _fileReviewStore;
    private readonly FileRelationDiagnosticsStore _fileDiagnosticsStore;
    private readonly PostgresRelationStore _postgresRelationStore;
    private readonly PostgresRelationReviewStore _postgresReviewStore;
    private readonly PostgresRelationDiagnosticsStore _postgresDiagnosticsStore;
    private readonly RelationGovernanceMultiNormalScopeCanaryOptions _options;
    private readonly bool _limitedNormalObservationPassed;
    private readonly Func<RelationGovernanceProviderSwitchTrace, CancellationToken, Task> _traceSink;

    public RelationGovernanceMultiNormalScopeCanaryRunner(
        FileRelationStore fileRelationStore,
        FileRelationReviewStore fileReviewStore,
        FileRelationDiagnosticsStore fileDiagnosticsStore,
        PostgresRelationStore postgresRelationStore,
        PostgresRelationReviewStore postgresReviewStore,
        PostgresRelationDiagnosticsStore postgresDiagnosticsStore,
        RelationGovernanceMultiNormalScopeCanaryOptions options,
        bool limitedNormalObservationPassed,
        Func<RelationGovernanceProviderSwitchTrace, CancellationToken, Task> traceSink)
    {
        _fileRelationStore = fileRelationStore;
        _fileReviewStore = fileReviewStore;
        _fileDiagnosticsStore = fileDiagnosticsStore;
        _postgresRelationStore = postgresRelationStore;
        _postgresReviewStore = postgresReviewStore;
        _postgresDiagnosticsStore = postgresDiagnosticsStore;
        _options = options;
        _limitedNormalObservationPassed = limitedNormalObservationPassed;
        _traceSink = traceSink;
    }

    public async Task<PostgresRelationMultiNormalScopeCanaryReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var traces = new List<RelationGovernanceProviderSwitchTrace>();
        var reports = new List<(RelationGovernanceNormalScopeRule Scope, PostgresRelationLimitedNormalScopeObservationReport Report)>();
        var diagnostics = new List<string>();
        var blocked = new List<string>();
        var enabledScopes = _options.Scopes.Where(static scope => scope.Enabled).ToArray();

        if (!_options.Enabled)
        {
            blocked.Add("MultiNormalScopeCanaryDisabled");
        }

        if (_options.RequireLimitedNormalScopeObservationPassed && !_limitedNormalObservationPassed)
        {
            blocked.Add("LimitedNormalScopeObservationNotPassed");
        }

        if (enabledScopes.Length < 2)
        {
            blocked.Add("AtLeastTwoNormalScopesRequired");
        }

        if (enabledScopes.Any(static scope => string.IsNullOrWhiteSpace(scope.WorkspaceId) || string.IsNullOrWhiteSpace(scope.CollectionId)))
        {
            blocked.Add("NormalScopeMissingWorkspaceOrCollection");
        }

        if (blocked.Count > 0)
        {
            return BuildReport(
                gatePassed: false,
                traces,
                reports,
                nonAllowlistedScopeChecked: false,
                cleanupPerformed: false,
                crossScopeLeakCount: 0,
                diagnostics,
                blocked,
                recommendation: "GateNotPassed");
        }

        var runId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x");
        foreach (var scope in enabledScopes)
        {
            var scopeTraces = new List<RelationGovernanceProviderSwitchTrace>();
            var runner = new RelationGovernanceLimitedNormalScopeObservationRunner(
                _fileRelationStore,
                _fileReviewStore,
                _fileDiagnosticsStore,
                _postgresRelationStore,
                _postgresReviewStore,
                _postgresDiagnosticsStore,
                new RelationGovernanceLimitedNormalScopeObservationOptions
                {
                    Enabled = true,
                    WorkspaceId = scope.WorkspaceId,
                    CollectionId = scope.CollectionId,
                    ObservationWindowMinutes = _options.ObservationWindowMinutes,
                    OperationIntervalSeconds = 0,
                    MaxOperations = _options.MaxOperationsPerScope,
                    Mode = _options.Mode,
                    FallbackToFileSystem = _options.FallbackToFileSystem,
                    ContinueComparisonTrace = _options.ContinueComparisonTrace,
                    FailClosedOnMismatch = _options.FailClosedOnMismatch,
                    RequireSelectedNormalCanaryPassed = true,
                    CanaryIdPrefix = $"multi-{scope.ScopeName}-{runId}",
                    CleanupMode = scope.CleanupMode == RelationGovernanceSelectedNormalWorkspaceCleanupMode.None
                        ? _options.CleanupMode
                        : scope.CleanupMode
                },
                _limitedNormalObservationPassed,
                async (trace, token) =>
                {
                    scopeTraces.Add(trace);
                    traces.Add(trace);
                    await _traceSink(trace, token).ConfigureAwait(false);
                });

            var report = await runner.RunAsync(cancellationToken).ConfigureAwait(false);
            reports.Add((scope, report));
            diagnostics.AddRange(report.Diagnostics.Select(item => $"{scope.ScopeName}:{item}"));
            blocked.AddRange(report.BlockedReasons.Select(item => $"{scope.ScopeName}:{item}"));
        }

        var crossScopeLeakCount = await CountCrossScopeLeaksAsync(enabledScopes, cancellationToken).ConfigureAwait(false);
        if (crossScopeLeakCount > 0)
        {
            blocked.Add("CrossScopeLeakDetected");
        }

        var nonAllowlistedScopeChecked = await ValidateNonAllowlistedScopeAsync(cancellationToken).ConfigureAwait(false);
        if (!nonAllowlistedScopeChecked)
        {
            blocked.Add("NonAllowlistedScopeLeak");
        }

        var cleanupPerformed = reports.Any(static item => item.Report.CleanupPerformed);
        var mismatchCount = reports.Sum(static item => item.Report.MismatchCount);
        var postgresFailureCount = reports.Sum(static item => item.Report.PostgresFailureCount);
        var scopeLeakCount = reports.Sum(static item => item.Report.ScopeLeakCount) + crossScopeLeakCount + (nonAllowlistedScopeChecked ? 0 : 1);
        var recommendation = !_limitedNormalObservationPassed
            ? "GateNotPassed"
            : scopeLeakCount > 0
                ? "BlockedByScopeLeak"
                : mismatchCount > 0
                    ? "BlockedByMismatch"
                    : postgresFailureCount > 0
                        ? "BlockedByPostgresFailure"
                        : HasLatencyRisk(traces)
                            ? "BlockedByLatency"
                            : traces.Count >= Math.Min(_options.MaxOperationsPerScope * enabledScopes.Length, 120)
                                ? "ReadyForLimitedScopeExpansion"
                                : "NeedsMoreObservation";

        return BuildReport(
            _limitedNormalObservationPassed,
            traces,
            reports,
            nonAllowlistedScopeChecked,
            cleanupPerformed,
            crossScopeLeakCount,
            diagnostics.Count == 0 ? ["MultiNormalScopeCanary"] : diagnostics.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            blocked.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            recommendation);
    }

    public PostgresRelationMultiNormalScopeCanaryReport BuildQualityReport(
        PostgresRelationMultiNormalScopeCanaryReport? canary,
        bool p15Passed,
        double p95LatencyThresholdMs)
    {
        var blocked = new List<string>();
        AddIfFalse(blocked, _limitedNormalObservationPassed, "LimitedNormalScopeObservationNotPassed");
        AddIfFalse(blocked, canary is not null, "MultiNormalScopeCanaryMissing");
        AddIfFalse(blocked, p15Passed, "P15GateNotPassed");
        if (canary is null)
        {
            return BuildReport(false, [], [], false, false, 0, ["RunPostgresRelationMultiNormalScopeCanaryFirst"], blocked, "GateNotPassed");
        }

        AddIfFalse(blocked, canary.EnabledScopeCount >= 2, "AtLeastTwoNormalScopesRequired");
        AddIfFalse(blocked, canary.MismatchCount == 0, "MismatchDetected");
        AddIfFalse(blocked, canary.PostgresFailureCount == 0, "PostgresFailureDetected");
        AddIfFalse(blocked, canary.ScopeLeakCount == 0, "ScopeLeakDetected");
        AddIfFalse(blocked, canary.NonAllowlistedScopeChecked, "NonAllowlistedScopeNotChecked");
        AddIfFalse(blocked, canary.P95PostgresReadMs <= p95LatencyThresholdMs, "P95PostgresReadLatencyExceeded");
        AddIfFalse(blocked, canary.P95PostgresWriteMs <= p95LatencyThresholdMs, "P95PostgresWriteLatencyExceeded");
        AddIfFalse(blocked, canary.GraphExpansionPreviewParityPassed, "GraphExpansionPreviewParityFailed");
        AddIfFalse(blocked, canary.ReviewLifecycleParityPassed, "ReviewLifecycleParityFailed");
        AddIfFalse(blocked, canary.DiagnosticsParityPassed, "DiagnosticsParityFailed");
        AddIfFalse(blocked, canary.ReplacementChainParityPassed, "ReplacementChainParityFailed");

        var passed = blocked.Count == 0;
        return new PostgresRelationMultiNormalScopeCanaryReport
        {
            GatePassed = passed,
            ScopeCount = canary.ScopeCount,
            EnabledScopeCount = canary.EnabledScopeCount,
            OperationCount = canary.OperationCount,
            OperationCountByScope = canary.OperationCountByScope,
            PostgresPrimaryReadCount = canary.PostgresPrimaryReadCount,
            PostgresPrimaryWriteCount = canary.PostgresPrimaryWriteCount,
            FileSystemFallbackCount = canary.FileSystemFallbackCount,
            ComparisonTraceCount = canary.ComparisonTraceCount,
            MismatchCount = canary.MismatchCount,
            PostgresFailureCount = canary.PostgresFailureCount,
            ScopeLeakCount = canary.ScopeLeakCount,
            NonAllowlistedScopeChecked = canary.NonAllowlistedScopeChecked,
            AveragePostgresReadMs = canary.AveragePostgresReadMs,
            P95PostgresReadMs = canary.P95PostgresReadMs,
            AveragePostgresWriteMs = canary.AveragePostgresWriteMs,
            P95PostgresWriteMs = canary.P95PostgresWriteMs,
            PerScopeStatus = canary.PerScopeStatus,
            GraphExpansionPreviewParityPassed = canary.GraphExpansionPreviewParityPassed,
            ReviewLifecycleParityPassed = canary.ReviewLifecycleParityPassed,
            DiagnosticsParityPassed = canary.DiagnosticsParityPassed,
            ReplacementChainParityPassed = canary.ReplacementChainParityPassed,
            CleanupPerformed = canary.CleanupPerformed,
            RollbackInstruction = canary.RollbackInstruction,
            Diagnostics = canary.Diagnostics,
            BlockedReasons = blocked,
            Recommendation = passed
                ? "ReadyForLimitedScopeExpansion"
                : canary.ScopeLeakCount > 0
                    ? "BlockedByScopeLeak"
                    : canary.MismatchCount > 0
                        ? "BlockedByMismatch"
                        : canary.PostgresFailureCount > 0
                            ? "BlockedByPostgresFailure"
                            : canary.P95PostgresReadMs > p95LatencyThresholdMs || canary.P95PostgresWriteMs > p95LatencyThresholdMs
                                ? "BlockedByLatency"
                                : "GateNotPassed"
        };
    }

    private async Task<int> CountCrossScopeLeaksAsync(
        IReadOnlyList<RelationGovernanceNormalScopeRule> scopes,
        CancellationToken cancellationToken)
    {
        var leaks = 0;
        foreach (var scope in scopes)
        {
            var relations = await _postgresRelationStore.QueryAsync(
                    new ContextRelationQuery
                    {
                        WorkspaceId = scope.WorkspaceId,
                        CollectionId = scope.CollectionId
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            leaks += relations.Count(relation =>
                !string.Equals(relation.WorkspaceId, scope.WorkspaceId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(relation.CollectionId, scope.CollectionId, StringComparison.OrdinalIgnoreCase));
        }

        return leaks;
    }

    private async Task<bool> ValidateNonAllowlistedScopeAsync(CancellationToken cancellationToken)
    {
        var relation = new ContextRelation
        {
            Id = "multi-normal-non-allowlisted-relation",
            WorkspaceId = "contextcore_multi_normal_outside",
            CollectionId = "relation-governance-multi-normal-outside",
            SourceId = "multi-normal-outside-source",
            TargetId = "multi-normal-outside-target",
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

    private PostgresRelationMultiNormalScopeCanaryReport BuildReport(
        bool gatePassed,
        IReadOnlyList<RelationGovernanceProviderSwitchTrace> traces,
        IReadOnlyList<(RelationGovernanceNormalScopeRule Scope, PostgresRelationLimitedNormalScopeObservationReport Report)> reports,
        bool nonAllowlistedScopeChecked,
        bool cleanupPerformed,
        int crossScopeLeakCount,
        IReadOnlyList<string> diagnostics,
        IReadOnlyList<string> blocked,
        string recommendation)
    {
        var readTraces = traces.Where(IsReadTrace).ToArray();
        var writeTraces = traces.Where(IsWriteTrace).ToArray();
        return new PostgresRelationMultiNormalScopeCanaryReport
        {
            GatePassed = gatePassed,
            ScopeCount = _options.Scopes.Count,
            EnabledScopeCount = _options.Scopes.Count(static scope => scope.Enabled),
            OperationCount = traces.Count,
            OperationCountByScope = reports.ToDictionary(
                static item => item.Scope.ScopeName,
                static item => item.Report.OperationCount,
                StringComparer.OrdinalIgnoreCase),
            PostgresPrimaryReadCount = readTraces.Count(static trace => string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)),
            PostgresPrimaryWriteCount = writeTraces.Count(static trace => string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)),
            FileSystemFallbackCount = traces.Count(static trace => trace.FallbackUsed),
            ComparisonTraceCount = traces.Count(static trace => string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)),
            MismatchCount = reports.Sum(static item => item.Report.MismatchCount),
            PostgresFailureCount = reports.Sum(static item => item.Report.PostgresFailureCount),
            ScopeLeakCount = reports.Sum(static item => item.Report.ScopeLeakCount) + crossScopeLeakCount + (nonAllowlistedScopeChecked ? 0 : 1),
            NonAllowlistedScopeChecked = nonAllowlistedScopeChecked,
            AveragePostgresReadMs = AverageDuration(readTraces),
            P95PostgresReadMs = PercentileDuration(readTraces, 0.95),
            AveragePostgresWriteMs = AverageDuration(writeTraces),
            P95PostgresWriteMs = PercentileDuration(writeTraces, 0.95),
            PerScopeStatus =
            [
                .. reports.Select(static item => new RelationGovernanceMultiNormalScopeStatus
                {
                    ScopeName = item.Scope.ScopeName,
                    WorkspaceId = item.Scope.WorkspaceId,
                    CollectionId = item.Scope.CollectionId,
                    RolloutStage = item.Scope.RolloutStage,
                    OperationCount = item.Report.OperationCount,
                    PostgresPrimaryReadCount = item.Report.PostgresPrimaryReadCount,
                    PostgresPrimaryWriteCount = item.Report.PostgresPrimaryWriteCount,
                    FileSystemFallbackCount = item.Report.FileSystemFallbackCount,
                    MismatchCount = item.Report.MismatchCount,
                    PostgresFailureCount = item.Report.PostgresFailureCount,
                    ScopeLeakCount = item.Report.ScopeLeakCount,
                    Recommendation = item.Report.Recommendation
                })
            ],
            GraphExpansionPreviewParityPassed = reports.Count > 0 && reports.All(static item => item.Report.GraphExpansionPreviewParityPassed),
            ReviewLifecycleParityPassed = reports.Count > 0 && reports.All(static item => item.Report.ReviewLifecycleParityPassed),
            DiagnosticsParityPassed = reports.Count > 0 && reports.All(static item => item.Report.DiagnosticsParityPassed),
            ReplacementChainParityPassed = reports.Count > 0 && reports.All(static item => item.Report.ReplacementChainParityPassed),
            CleanupPerformed = cleanupPerformed,
            RollbackInstruction = "Disable affected normal scope rule or set RelationGovernanceProviderSwitchOptions.Enabled=false.",
            Diagnostics = diagnostics,
            BlockedReasons = blocked,
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

    private static void AddIfFalse(ICollection<string> blocked, bool condition, string reason)
    {
        if (!condition)
        {
            blocked.Add(reason);
        }
    }
}
