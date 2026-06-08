namespace ContextCore.Embedding;

/// <summary>Embedding 向量单位化工具。</summary>
public static class EmbeddingNormalization
{
    public static IReadOnlyList<float> Normalize(IReadOnlyList<float> vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        var norm = CalculateNorm(vector);
        if (norm <= 0)
        {
            return vector.ToArray();
        }

        return vector.Select(value => (float)(value / norm)).ToArray();
    }

    public static double CalculateNorm(IReadOnlyList<float> vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        var sum = 0.0;
        foreach (var value in vector)
        {
            sum += value * value;
        }

        return Math.Sqrt(sum);
    }
}
