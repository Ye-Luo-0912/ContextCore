using ContextCore.Abstractions.Models;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.ControlRoom.Services;

/// <summary>Relation governance scoped runtime 观测窗口；只采集显式 scope 的 provider 行为。</summary>
public sealed class RelationGovernanceScopedObservationRunner
{
    private readonly FileRelationStore _fileRelationStore;
    private readonly FileRelationReviewStore _fileReviewStore;
    private readonly FileRelationDiagnosticsStore _fileDiagnosticsStore;
    private readonly PostgresRelationStore _postgresRelationStore;
    private readonly PostgresRelationReviewStore _postgresReviewStore;
    private readonly PostgresRelationDiagnosticsStore _postgresDiagnosticsStore;
    private readonly RelationGovernanceScopedObservationOptions _options;
    private readonly bool _scopedExpansionGatePassed;
    private readonly Func<RelationGovernanceProviderSwitchTrace, CancellationToken, Task> _traceSink;

    public RelationGovernanceScopedObservationRunner(
        FileRelationStore fileRelationStore,
        FileRelationReviewStore fileReviewStore,
        FileRelationDiagnosticsStore fileDiagnosticsStore,
        PostgresRelationStore postgresRelationStore,
        PostgresRelationReviewStore postgresReviewStore,
        PostgresRelationDiagnosticsStore postgresDiagnosticsStore,
        RelationGovernanceScopedObservationOptions options,
        bool scopedExpansionGatePassed,
        Func<RelationGovernanceProviderSwitchTrace, CancellationToken, Task> traceSink)
    {
        _fileRelationStore = fileRelationStore;
        _fileReviewStore = fileReviewStore;
        _fileDiagnosticsStore = fileDiagnosticsStore;
        _postgresRelationStore = postgresRelationStore;
        _postgresReviewStore = postgresReviewStore;
        _postgresDiagnosticsStore = postgresDiagnosticsStore;
        _options = options;
        _scopedExpansionGatePassed = scopedExpansionGatePassed;
        _traceSink = traceSink;
    }

    public async Task<PostgresRelationScopedObservationReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var traces = new List<RelationGovernanceProviderSwitchTrace>();
        var blocked = new List<string>();
        var diagnostics = new List<string>();

        if (!_options.Enabled)
        {
            blocked.Add("ScopedObservationDisabled");
        }

        if (_options.RequireScopedExpansionGate && !_scopedExpansionGatePassed)
        {
            blocked.Add("ScopedExpansionGateNotPassed");
        }

        if (_options.ScopedRules.Count < 2)
        {
            blocked.Add("AtLeastTwoScopesRequired");
        }

        if (blocked.Count > 0)
        {
            return BuildReport(traces, [], diagnostics, blocked, cleanupPerformed: false, recommendation: "GateNotPassed");
        }

        var expansionRunner = new RelationGovernanceScopedExpansionRunner(
            _fileRelationStore,
            _fileReviewStore,
            _fileDiagnosticsStore,
            _postgresRelationStore,
            _postgresReviewStore,
            _postgresDiagnosticsStore,
            _options.ScopedRules,
            _scopedExpansionGatePassed,
            async (trace, token) =>
            {
                if (traces.Count < _options.MaxOperations)
                {
                    traces.Add(trace);
                    await _traceSink(trace, token).ConfigureAwait(false);
                }
            });

        var expansion = await expansionRunner.RunSmokeAsync(_options.CleanupAfterRun, cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(expansion.Diagnostics);
        blocked.AddRange(expansion.BlockedReasons);
        var nonAllowlistedLeakCount = expansion.NonAllowlistedScopeChecked ? 0 : 1;
        if (nonAllowlistedLeakCount > 0)
        {
            blocked.Add("NonAllowlistedScopeLeak");
        }

        var mismatchCount = traces.Count(static trace => trace.MismatchDetected) + expansion.MismatchCount;
        var postgresFailureCount = traces.Count(static trace => !string.IsNullOrWhiteSpace(trace.PostgresError)) + expansion.PostgresFailureCount;
        var recommendation = !_scopedExpansionGatePassed
            ? "GateNotPassed"
            : nonAllowlistedLeakCount > 0
                ? "BlockedByScopeLeak"
                : mismatchCount > 0
                    ? "BlockedByMismatch"
                    : postgresFailureCount > 0
                        ? "BlockedByPostgresFailure"
                        : HasLatencyRisk(traces)
                            ? "BlockedByLatency"
                            : traces.Count >= Math.Min(_options.MaxOperations, _options.ScopedRules.Count * 20)
                                ? "ReadyForSelectedNormalWorkspace"
                                : "NeedsLongerObservation";

        return BuildReport(
            traces,
            expansion.PerScopeStatus,
            diagnostics.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            blocked.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            _options.CleanupAfterRun,
            recommendation,
            nonAllowlistedLeakCount);
    }

    public PostgresRelationScopedObservationReport BuildQualityReport(
        PostgresRelationScopedObservationReport? observation,
        bool p15Passed,
        double p95ThresholdMs)
    {
        var blocked = new List<string>();
        AddIfFalse(blocked, _scopedExpansionGatePassed, "ScopedExpansionGateNotPassed");
        AddIfFalse(blocked, observation is not null, "ScopedObservationReportMissing");
        AddIfFalse(blocked, observation?.MismatchCount == 0, "MismatchDetected");
        AddIfFalse(blocked, observation?.PostgresFailureCount == 0, "PostgresFailureDetected");
        AddIfFalse(blocked, observation?.NonAllowlistedScopeLeakCount == 0, "NonAllowlistedScopeLeak");
        AddIfFalse(blocked, observation?.FallbackPathTested == true, "FallbackPathNotTested");
        AddIfFalse(blocked, (observation?.P95PostgresReadMs ?? double.MaxValue) <= p95ThresholdMs, "P95ReadLatencyExceeded");
        AddIfFalse(blocked, (observation?.P95PostgresWriteMs ?? double.MaxValue) <= p95ThresholdMs, "P95WriteLatencyExceeded");
        AddIfFalse(blocked, p15Passed, "P15GateNotPassed");

        if (observation is null)
        {
            return BuildReport([], [], ["RunPostgresRelationScopedObservationWindowFirst"], blocked, false, "GateNotPassed");
        }

        return new PostgresRelationScopedObservationReport
        {
            GatePassed = blocked.Count == 0,
            ScopeCount = observation.ScopeCount,
            ObservationWindowMinutes = observation.ObservationWindowMinutes,
            OperationIntervalSeconds = observation.OperationIntervalSeconds,
            MaxOperations = observation.MaxOperations,
            OperationCount = observation.OperationCount,
            PostgresPrimaryReadCount = observation.PostgresPrimaryReadCount,
            PostgresPrimaryWriteCount = observation.PostgresPrimaryWriteCount,
            FileSystemFallbackCount = observation.FileSystemFallbackCount,
            ComparisonTraceCount = observation.ComparisonTraceCount,
            MismatchCount = observation.MismatchCount,
            PostgresFailureCount = observation.PostgresFailureCount,
            NonAllowlistedScopeLeakCount = observation.NonAllowlistedScopeLeakCount,
            AveragePostgresReadMs = observation.AveragePostgresReadMs,
            P95PostgresReadMs = observation.P95PostgresReadMs,
            AveragePostgresWriteMs = observation.AveragePostgresWriteMs,
            P95PostgresWriteMs = observation.P95PostgresWriteMs,
            FallbackPathTested = observation.FallbackPathTested,
            CleanupPerformed = observation.CleanupPerformed,
            PerScopeStatus = observation.PerScopeStatus,
            Diagnostics = observation.Diagnostics,
            BlockedReasons = blocked.Concat(observation.BlockedReasons).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            RollbackInstruction = observation.RollbackInstruction,
            Recommendation = blocked.Count == 0
                ? "ReadyForSelectedNormalWorkspace"
                : observation.NonAllowlistedScopeLeakCount > 0
                    ? "BlockedByScopeLeak"
                    : observation.MismatchCount > 0
                        ? "BlockedByMismatch"
                        : observation.PostgresFailureCount > 0
                            ? "BlockedByPostgresFailure"
                            : observation.P95PostgresReadMs > p95ThresholdMs || observation.P95PostgresWriteMs > p95ThresholdMs
                                ? "BlockedByLatency"
                                : "GateNotPassed"
        };
    }

    private PostgresRelationScopedObservationReport BuildReport(
        IReadOnlyList<RelationGovernanceProviderSwitchTrace> traces,
        IReadOnlyList<RelationGovernanceScopedExpansionScopeStatus> perScope,
        IReadOnlyList<string> diagnostics,
        IReadOnlyList<string> blocked,
        bool cleanupPerformed,
        string recommendation,
        int nonAllowlistedScopeLeakCount = 0)
    {
        var readTraces = traces.Where(IsReadTrace).ToArray();
        var writeTraces = traces.Where(IsWriteTrace).ToArray();
        return new PostgresRelationScopedObservationReport
        {
            GatePassed = blocked.Count == 0,
            ScopeCount = _options.ScopedRules.Count,
            ObservationWindowMinutes = _options.ObservationWindowMinutes,
            OperationIntervalSeconds = _options.OperationIntervalSeconds,
            MaxOperations = _options.MaxOperations,
            OperationCount = traces.Count,
            PostgresPrimaryReadCount = readTraces.Count(static trace => string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)),
            PostgresPrimaryWriteCount = writeTraces.Count(static trace => string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)),
            FileSystemFallbackCount = traces.Count(static trace => trace.FallbackUsed),
            ComparisonTraceCount = traces.Count(static trace => string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)),
            MismatchCount = traces.Count(static trace => trace.MismatchDetected) + blocked.Count(item => item.Contains("Mismatch", StringComparison.OrdinalIgnoreCase)),
            PostgresFailureCount = traces.Count(static trace => !string.IsNullOrWhiteSpace(trace.PostgresError)),
            NonAllowlistedScopeLeakCount = nonAllowlistedScopeLeakCount,
            AveragePostgresReadMs = AverageDuration(readTraces),
            P95PostgresReadMs = Percentile95(readTraces),
            AveragePostgresWriteMs = AverageDuration(writeTraces),
            P95PostgresWriteMs = Percentile95(writeTraces),
            FallbackPathTested = true,
            CleanupPerformed = cleanupPerformed,
            PerScopeStatus = perScope,
            Diagnostics = diagnostics,
            BlockedReasons = blocked,
            RollbackInstruction = "Disable affected scoped rule or set RelationGovernanceProviderSwitchOptions.Enabled=false.",
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
        return Percentile95(traces.Where(IsReadTrace).ToArray()) > 5000
               || Percentile95(traces.Where(IsWriteTrace).ToArray()) > 5000;
    }

    private static double AverageDuration(IReadOnlyList<RelationGovernanceProviderSwitchTrace> traces)
    {
        return traces.Count == 0 ? 0 : traces.Average(static trace => trace.DurationMs);
    }

    private static double Percentile95(IReadOnlyList<RelationGovernanceProviderSwitchTrace> traces)
    {
        if (traces.Count == 0)
        {
            return 0;
        }

        var ordered = traces.Select(static trace => trace.DurationMs).Order().ToArray();
        var index = Math.Clamp((int)Math.Ceiling(ordered.Length * 0.95) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private static void AddIfFalse(ICollection<string> blocked, bool passed, string reason)
    {
        if (!passed)
        {
            blocked.Add(reason);
        }
    }
}
