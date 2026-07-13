using ContextCore.Abstractions.Models;

namespace ContextCore.Evaluation.Models;

public sealed class PostgresRelationStoreParityReport
{
    public bool ProviderEnabled { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int FixtureRelationCount { get; init; }

    public bool GetPassed { get; init; }

    public bool ListPassed { get; init; }

    public bool SourceQueryPassed { get; init; }

    public bool TargetQueryPassed { get; init; }

    public bool TypeQueryPassed { get; init; }

    public bool LifecycleQueryPassed { get; init; }

    public bool ReviewStatusQueryPassed { get; init; }

    public bool ReplacementChainQueryPassed { get; init; }

    public bool DeletePassed { get; init; }

    public bool CleanupPerformed { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresRelationDualWriteSmokeReport
{
    public bool ProviderEnabled { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public bool RelationDualWritePassed { get; init; }

    public bool ReviewDualWritePassed { get; init; }

    public bool DiagnosticsDualWritePassed { get; init; }

    public bool CleanupPerformed { get; init; }

    public int TraceCount { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PostgresRelationShadowReadSmokeReport
{
    public bool ProviderEnabled { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public int TraceCount { get; init; }

    public bool CleanupPerformed { get; init; }

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}
