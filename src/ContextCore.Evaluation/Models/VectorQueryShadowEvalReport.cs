using ContextCore.Abstractions.Models;

namespace ContextCore.Evaluation.Models;


/// <summary>vector query shadow eval 汇总报告；不改变正式 retrieval/package 输出。</summary>
public sealed class VectorQueryShadowEvalReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string ProviderType { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }

    public int Dimension { get; init; }

    public bool UseForRuntime { get; init; }

    public int Samples { get; init; }

    public double IndexedCoverage { get; init; }

    public int QueryCount { get; init; }

    public int CandidateCount { get; init; }

    public int RawCandidateCount { get; init; }

    public int EligibleCandidateCount { get; init; }

    public int BlockedCandidateCount { get; init; }

    public int RiskBeforePolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public double MustHitRecallBeforePolicy { get; init; }

    public double MustHitRecallAfterPolicy { get; init; }

    public double MustNotHitRiskBeforePolicy { get; init; }

    public double MustNotHitRiskAfterPolicy { get; init; }

    public double LifecycleRiskBeforePolicy { get; init; }

    public double LifecycleRiskAfterPolicy { get; init; }

    public double MustHitRecallAtK { get; init; }

    public double MustNotHitRiskAtK { get; init; }

    public double LifecycleRiskAtK { get; init; }

    public int DeprecatedHitCount { get; init; }

    public int DuplicateHitCount { get; init; }

    public double AverageTopSimilarity { get; init; }

    public int NoCandidateCount { get; init; }

    public int LowConfidenceCount { get; init; }

    public IReadOnlyDictionary<string, int> TopNoiseClusters { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> BlockedByReason { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.NeedsMoreIndexedData;

    public int FormalOutputChanged { get; init; }

    public IReadOnlyList<VectorQueryShadowEvalSample> SampleResults { get; init; } =
        Array.Empty<VectorQueryShadowEvalSample>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
