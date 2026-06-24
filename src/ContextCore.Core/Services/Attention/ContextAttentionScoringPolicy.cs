using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Attention;

/// <summary>Attention scorer 的运行时策略快照，集中读取 profile 中的权重、惩罚和控制项。</summary>
internal sealed class ContextAttentionScoringPolicy
{
    private readonly ContextAttentionProfile _profile;

    private ContextAttentionScoringPolicy(ContextAttentionProfile profile)
    {
        _profile = profile;
    }

    public static ContextAttentionScoringPolicy From(ContextAttentionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ContextAttentionScoringPolicy(profile);
    }

    public double Weighted(string key, double value)
    {
        return _profile.Weights.TryGetValue(key, out var weight)
            ? value * weight
            : 0d;
    }

    public double Penalty(string key)
    {
        return _profile.Penalties.TryGetValue(key, out var penalty)
            ? penalty
            : 0d;
    }

    public double Control(string key, double defaultValue = 0d)
    {
        return _profile.Controls.TryGetValue(key, out var value)
            ? value
            : defaultValue;
    }
}
