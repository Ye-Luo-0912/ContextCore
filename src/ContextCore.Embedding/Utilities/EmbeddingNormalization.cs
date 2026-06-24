namespace ContextCore.Embedding.Utilities;

/// <summary>Embedding 向量单位化工具。</summary>
public static class EmbeddingNormalization
{
    public static IReadOnlyList<float> Normalize(IReadOnlyList<float> vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        var norm = CalculateNorm(vector);
        return norm <= 0 ? vector.ToArray() : vector.Select(value => (float)(value / norm)).ToArray();
    }

    public static double CalculateNorm(IReadOnlyList<float> vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        var sum = vector.Aggregate(0.0, (current, value) => current + value * value);

        return Math.Sqrt(sum);
    }
}
