using ContextCore.Abstractions.Models;

namespace ContextCore.Evaluation.Models;

public sealed class PostgresVectorShadowEvalReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetName { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }

    public int Dimension { get; init; }

    public bool Normalized { get; init; }

    public string ProfileId { get; init; } = string.Empty;

    public int TopK { get; init; }

    public string Recommendation { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public int QueryCount { get; init; }

    public int PgVectorCandidateCount { get; init; }

    public int FileSystemCandidateCount { get; init; }

    public double RecallAfterPolicy { get; init; }

    public double MrrAfterPolicy { get; init; }

    public double FileSystemRecallAfterPolicy { get; init; }

    public double RecallDelta { get; init; }

    public int RiskAfterPolicy { get; init; }

    public double MustNotHitRiskAfterPolicy { get; init; }

    public double LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public double TopKOverlapRate { get; init; }

    public int OrderingMismatchCount { get; init; }

    public double ScoreDeltaMax { get; init; }

    public int MetadataMismatchCount { get; init; }

    public int EligibilityMetadataMismatchCount { get; init; }

    public int RiskProjectionMismatchCount { get; init; }

    public bool UseForRuntime { get; init; }

    public IReadOnlyList<PostgresVectorQueryPreviewSample> Samples { get; init; } =
        Array.Empty<PostgresVectorQueryPreviewSample>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class PostgresVectorShadowEvalSummaryReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string Recommendation { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }

    public int Dimension { get; init; }

    public bool UseForRuntime { get; init; }

    public IReadOnlyList<PostgresVectorShadowEvalReport> Reports { get; init; } =
        Array.Empty<PostgresVectorShadowEvalReport>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}
