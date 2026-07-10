using ContextCore.Abstractions.Models;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.ControlRoom.Services;

/// <summary>selected normal scope 的延长观察窗口；只复用 canary 操作集，不改变全局 provider。</summary>
public sealed class RelationGovernanceLimitedNormalScopeObservationRunner
{
    private const string DefaultIdPrefix = "selected-normal";

    private readonly FileRelationStore _fileRelationStore;
    private readonly FileRelationReviewStore _fileReviewStore;
    private readonly FileRelationDiagnosticsStore _fileDiagnosticsStore;
    private readonly PostgresRelationStore _postgresRelationStore;
    private readonly PostgresRelationReviewStore _postgresReviewStore;
    private readonly PostgresRelationDiagnosticsStore _postgresDiagnosticsStore;
    private readonly RelationGovernanceLimitedNormalScopeObservationOptions _options;
    private readonly bool _selectedNormalCanaryPassed;
    private readonly Func<RelationGovernanceProviderSwitchTrace, CancellationToken, Task> _traceSink;
    private readonly string _idPrefix;

    public RelationGovernanceLimitedNormalScopeObservationRunner(
        FileRelationStore fileRelationStore,
        FileRelationReviewStore fileReviewStore,
        FileRelationDiagnosticsStore fileDiagnosticsStore,
        PostgresRelationStore postgresRelationStore,
        PostgresRelationReviewStore postgresReviewStore,
        PostgresRelationDiagnosticsStore postgresDiagnosticsStore,
        RelationGovernanceLimitedNormalScopeObservationOptions options,
        bool selectedNormalCanaryPassed,
        Func<RelationGovernanceProviderSwitchTrace, CancellationToken, Task> traceSink)
    {
        _fileRelationStore = fileRelationStore;
        _fileReviewStore = fileReviewStore;
        _fileDiagnosticsStore = fileDiagnosticsStore;
        _postgresRelationStore = postgresRelationStore;
        _postgresReviewStore = postgresReviewStore;
        _postgresDiagnosticsStore = postgresDiagnosticsStore;
        _options = options;
        _selectedNormalCanaryPassed = selectedNormalCanaryPassed;
        _traceSink = traceSink;
        _idPrefix = string.IsNullOrWhiteSpace(options.CanaryIdPrefix)
            ? DefaultIdPrefix
            : BuildCanaryIdPrefix(options.CanaryIdPrefix);
    }

    public async Task<PostgresRelationLimitedNormalScopeObservationReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var traces = new List<RelationGovernanceProviderSwitchTrace>();
        var diagnostics = new List<string>();
        var blocked = new List<string>();
        var reports = new List<PostgresRelationSelectedNormalWorkspaceCanaryReport>();

        if (!_options.Enabled)
        {
            blocked.Add("LimitedNormalScopeObservationDisabled");
        }

        if (string.IsNullOrWhiteSpace(_options.WorkspaceId) || string.IsNullOrWhiteSpace(_options.CollectionId))
        {
            blocked.Add("LimitedNormalScopeMissing");
        }

        if (_options.RequireSelectedNormalCanaryPassed && !_selectedNormalCanaryPassed)
        {
            blocked.Add("SelectedNormalWorkspaceCanaryNotPassed");
        }

        if (blocked.Count > 0)
        {
            return BuildReport(
                gatePassed: false,
                traces,
                reports,
                diagnostics,
                blocked,
                cleanupPerformed: false,
                recommendation: "GateNotPassed");
        }

        var targetOperations = Math.Max(1, _options.MaxOperations);
        do
        {
            var runner = new RelationGovernanceSelectedNormalWorkspaceRunner(
                _fileRelationStore,
                _fileReviewStore,
                _fileDiagnosticsStore,
                _postgresRelationStore,
                _postgresReviewStore,
                _postgresDiagnosticsStore,
                new RelationGovernanceSelectedNormalWorkspaceOptions
                {
                    Enabled = true,
                    WorkspaceId = _options.WorkspaceId,
                    CollectionId = _options.CollectionId,
                    Mode = _options.Mode,
                    FallbackToFileSystem = _options.FallbackToFileSystem,
                    ContinueComparisonTrace = _options.ContinueComparisonTrace,
                    FailClosedOnMismatch = _options.FailClosedOnMismatch,
                    RequireScopedObservationPassed = _options.RequireSelectedNormalCanaryPassed,
                    ObservationWindowMinutes = _options.ObservationWindowMinutes,
                    MaxOperations = _options.MaxOperations,
                    CanaryIdPrefix = _idPrefix,
                    CleanupMode = RelationGovernanceSelectedNormalWorkspaceCleanupMode.None
                },
                _selectedNormalCanaryPassed,
                async (trace, token) =>
                {
                    traces.Add(trace);
                    await _traceSink(trace, token).ConfigureAwait(false);
                });

            var report = await runner.RunAsync(cancellationToken).ConfigureAwait(false);
            reports.Add(report);
            diagnostics.AddRange(report.Diagnostics);
            blocked.AddRange(report.BlockedReasons);

            if (_options.OperationIntervalSeconds > 0 && traces.Count < targetOperations)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(_options.OperationIntervalSeconds, 5)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        while (traces.Count < targetOperations && reports.Count < 8);

        var cleanupPerformed = await CleanupCanaryRelationsAsync(cancellationToken).ConfigureAwait(false);
        if (_options.CleanupMode != RelationGovernanceSelectedNormalWorkspaceCleanupMode.None)
        {
            diagnostics.Add("ReviewDiagnosticsCleanupSkippedToAvoidScopeDelete");
        }

        var mismatchCount = traces.Count(static trace => trace.MismatchDetected)
                            + reports.Sum(static report => report.MismatchCount);
        var postgresFailureCount = traces.Count(static trace => !string.IsNullOrWhiteSpace(trace.PostgresError))
                                   + reports.Sum(static report => report.PostgresFailureCount);
        var scopeLeakCount = reports.Sum(static report => report.ScopeLeakCount);
        var recommendation = !_selectedNormalCanaryPassed
            ? "GateNotPassed"
            : scopeLeakCount > 0
                ? "BlockedByScopeLeak"
                : mismatchCount > 0
                    ? "BlockedByMismatch"
                    : postgresFailureCount > 0
                        ? "BlockedByPostgresFailure"
                        : HasLatencyRisk(traces)
                            ? "BlockedByLatency"
                            : traces.Count >= Math.Min(_options.MaxOperations, 60)
                                ? "ReadyForMultiNormalScopeCanary"
                                : "NeedsLongerObservation";

        return BuildReport(
            gatePassed: _selectedNormalCanaryPassed,
            traces,
            reports,
            diagnostics.Count == 0 ? ["LimitedNormalScopeObservation"] : diagnostics.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            blocked.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            cleanupPerformed,
            recommendation);
    }

    public PostgresRelationLimitedNormalScopeObservationReport BuildQualityReport(
        PostgresRelationLimitedNormalScopeObservationReport? observation,
        bool p15Passed,
        double maxFallbackRate,
        double p95LatencyThresholdMs)
    {
        var blocked = new List<string>();
        AddIfFalse(blocked, _selectedNormalCanaryPassed, "SelectedNormalWorkspaceCanaryNotPassed");
        AddIfFalse(blocked, observation is not null, "LimitedNormalScopeObservationMissing");
        AddIfFalse(blocked, p15Passed, "P15GateNotPassed");
        if (observation is null)
        {
            return BuildReport(false, [], [], ["RunPostgresRelationLimitedNormalScopeObservationFirst"], blocked, false, "GateNotPassed");
        }

        AddIfFalse(blocked, observation.MismatchCount == 0, "MismatchDetected");
        AddIfFalse(blocked, observation.PostgresFailureCount == 0, "PostgresFailureDetected");
        AddIfFalse(blocked, observation.ScopeLeakCount == 0, "ScopeLeakDetected");
        AddIfFalse(blocked, observation.FallbackRate <= maxFallbackRate, "FallbackRateExceeded");
        AddIfFalse(blocked, observation.P95PostgresReadMs <= p95LatencyThresholdMs, "P95PostgresReadLatencyExceeded");
        AddIfFalse(blocked, observation.P95PostgresWriteMs <= p95LatencyThresholdMs, "P95PostgresWriteLatencyExceeded");
        AddIfFalse(blocked, observation.GraphExpansionPreviewParityPassed, "GraphExpansionPreviewParityFailed");
        AddIfFalse(blocked, observation.ReviewLifecycleParityPassed, "ReviewLifecycleParityFailed");
        AddIfFalse(blocked, observation.DiagnosticsParityPassed, "DiagnosticsParityFailed");
        AddIfFalse(blocked, observation.ReplacementChainParityPassed, "ReplacementChainParityFailed");

        var passed = blocked.Count == 0;
        return new PostgresRelationLimitedNormalScopeObservationReport
        {
            GatePassed = passed,
            WorkspaceId = observation.WorkspaceId,
            CollectionId = observation.CollectionId,
            ObservationWindowMinutes = observation.ObservationWindowMinutes,
            OperationIntervalSeconds = observation.OperationIntervalSeconds,
            MaxOperations = observation.MaxOperations,
            ProviderMode = observation.ProviderMode,
            OperationCount = observation.OperationCount,
            PostgresPrimaryReadCount = observation.PostgresPrimaryReadCount,
            PostgresPrimaryWriteCount = observation.PostgresPrimaryWriteCount,
            FileSystemFallbackCount = observation.FileSystemFallbackCount,
            ComparisonTraceCount = observation.ComparisonTraceCount,
            MismatchCount = observation.MismatchCount,
            PostgresFailureCount = observation.PostgresFailureCount,
            ScopeLeakCount = observation.ScopeLeakCount,
            AveragePostgresReadMs = observation.AveragePostgresReadMs,
            P95PostgresReadMs = observation.P95PostgresReadMs,
            AveragePostgresWriteMs = observation.AveragePostgresWriteMs,
            P95PostgresWriteMs = observation.P95PostgresWriteMs,
            ErrorRate = observation.ErrorRate,
            FallbackRate = observation.FallbackRate,
            GraphExpansionPreviewParityPassed = observation.GraphExpansionPreviewParityPassed,
            ReviewLifecycleParityPassed = observation.ReviewLifecycleParityPassed,
            DiagnosticsParityPassed = observation.DiagnosticsParityPassed,
            ReplacementChainParityPassed = observation.ReplacementChainParityPassed,
            ControlRoomReadPathPassed = observation.ControlRoomReadPathPassed,
            ClientApiRoundtripPathPassed = observation.ClientApiRoundtripPathPassed,
            NonSelectedNormalScopeRemainsFileSystem = observation.NonSelectedNormalScopeRemainsFileSystem,
            CleanupPerformed = observation.CleanupPerformed,
            RollbackInstruction = observation.RollbackInstruction,
            Diagnostics = observation.Diagnostics,
            BlockedReasons = blocked,
            Recommendation = passed
                ? "ReadyForMultiNormalScopeCanary"
                : observation.ScopeLeakCount > 0
                    ? "BlockedByScopeLeak"
                    : observation.MismatchCount > 0
                        ? "BlockedByMismatch"
                        : observation.PostgresFailureCount > 0
                            ? "BlockedByPostgresFailure"
                            : observation.P95PostgresReadMs > p95LatencyThresholdMs || observation.P95PostgresWriteMs > p95LatencyThresholdMs
                                ? "BlockedByLatency"
                                : "GateNotPassed"
        };
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

    private PostgresRelationLimitedNormalScopeObservationReport BuildReport(
        bool gatePassed,
        IReadOnlyList<RelationGovernanceProviderSwitchTrace> traces,
        IReadOnlyList<PostgresRelationSelectedNormalWorkspaceCanaryReport> reports,
        IReadOnlyList<string> diagnostics,
        IReadOnlyList<string> blocked,
        bool cleanupPerformed,
        string recommendation)
    {
        var readTraces = traces.Where(IsReadTrace).ToArray();
        var writeTraces = traces.Where(IsWriteTrace).ToArray();
        var operations = Math.Max(1, traces.Count);
        return new PostgresRelationLimitedNormalScopeObservationReport
        {
            GatePassed = gatePassed,
            WorkspaceId = _options.WorkspaceId,
            CollectionId = _options.CollectionId,
            ObservationWindowMinutes = _options.ObservationWindowMinutes,
            OperationIntervalSeconds = _options.OperationIntervalSeconds,
            MaxOperations = _options.MaxOperations,
            ProviderMode = _options.Mode.ToString(),
            OperationCount = traces.Count,
            PostgresPrimaryReadCount = readTraces.Count(static trace => string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)),
            PostgresPrimaryWriteCount = writeTraces.Count(static trace => string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)),
            FileSystemFallbackCount = traces.Count(static trace => trace.FallbackUsed),
            ComparisonTraceCount = traces.Count(static trace => string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)),
            MismatchCount = reports.Sum(static report => report.MismatchCount),
            PostgresFailureCount = reports.Sum(static report => report.PostgresFailureCount),
            ScopeLeakCount = reports.Sum(static report => report.ScopeLeakCount),
            AveragePostgresReadMs = AverageDuration(readTraces),
            P95PostgresReadMs = PercentileDuration(readTraces, 0.95),
            AveragePostgresWriteMs = AverageDuration(writeTraces),
            P95PostgresWriteMs = PercentileDuration(writeTraces, 0.95),
            ErrorRate = (double)reports.Sum(static report => report.MismatchCount + report.PostgresFailureCount) / operations,
            FallbackRate = (double)traces.Count(static trace => trace.FallbackUsed) / operations,
            GraphExpansionPreviewParityPassed = reports.Count > 0 && reports.All(static report => report.GraphExpansionPreviewParityPassed),
            ReviewLifecycleParityPassed = reports.Count > 0 && reports.All(static report => report.ReviewLifecycleParityPassed),
            DiagnosticsParityPassed = reports.Count > 0 && reports.All(static report => report.DiagnosticsParityPassed),
            ReplacementChainParityPassed = reports.Count > 0 && reports.All(static report => report.ReplacementChainParityPassed),
            ControlRoomReadPathPassed = reports.Count > 0 && reports.All(static report => report.ControlRoomReadPathPassed),
            ClientApiRoundtripPathPassed = reports.Count > 0 && reports.All(static report => report.ClientApiRoundtripPathPassed),
            NonSelectedNormalScopeRemainsFileSystem = reports.Count > 0 && reports.All(static report => report.NonSelectedNormalScopeRemainsFileSystem),
            CleanupPerformed = cleanupPerformed,
            RollbackInstruction = "Remove the selected normal workspace/collection from allowlist or set RelationGovernanceProviderSwitchOptions.Enabled=false.",
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
