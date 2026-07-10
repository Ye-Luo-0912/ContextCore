using ContextCore.Abstractions.Models;

namespace ContextCore.ControlRoom.Services;

/// <summary>
/// Shared trace-analysis helpers extracted from the relation governance canary
/// runners to eliminate triplicated <c>IsReadTrace</c>/<c>IsWriteTrace</c>/
/// <c>HasLatencyRisk</c>/<c>AverageDuration</c>/<c>PercentileDuration</c>
/// implementations (GRAPH-06 governance redundancy).
/// </summary>
internal static class RelationGovernanceCanaryTraceHelpers
{
    internal static bool IsReadTrace(RelationGovernanceProviderSwitchTrace trace)
    {
        return trace.OperationKind.Contains("Query", StringComparison.OrdinalIgnoreCase)
               || trace.OperationKind.Contains("Get", StringComparison.OrdinalIgnoreCase)
               || trace.OperationKind.Contains("Latest", StringComparison.OrdinalIgnoreCase)
               || trace.OperationKind.StartsWith("RelationDiagnosticsBy", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsWriteTrace(RelationGovernanceProviderSwitchTrace trace)
    {
        return trace.OperationKind.Contains("Write", StringComparison.OrdinalIgnoreCase)
               || trace.OperationKind.Contains("Delete", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasLatencyRisk(IReadOnlyList<RelationGovernanceProviderSwitchTrace> traces)
    {
        return traces.Count > 0 && traces.Average(static trace => trace.DurationMs) > 5000;
    }

    internal static double AverageDuration(IReadOnlyList<RelationGovernanceProviderSwitchTrace> traces)
    {
        return traces.Count == 0 ? 0 : traces.Average(static trace => trace.DurationMs);
    }

    internal static double PercentileDuration(IReadOnlyList<RelationGovernanceProviderSwitchTrace> traces, double percentile)
    {
        if (traces.Count == 0)
        {
            return 0;
        }

        var ordered = traces.Select(static trace => trace.DurationMs).OrderBy(static value => value).ToArray();
        var index = (int)Math.Ceiling(percentile * ordered.Length) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }
}
