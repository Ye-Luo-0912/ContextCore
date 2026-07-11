namespace ContextCore.Evaluation.Hosting;

/// <summary>
/// P3-02：统一的 Gate 评估器接口。
/// 替代 12 个 Gate Runner 各自实现的 BuildGate/RunGate/RunFreeze 方法。
/// Gate 协议：给定输入 report + 运行时标志，返回 Gate 决策（passed/blocked + 原因列表）。
/// </summary>
public interface IGateEvaluator<TInput, TDecision>
{
    string GateName { get; }

    TDecision Evaluate(TInput input, GateEvaluationContext context);
}

/// <summary>Gate 评估上下文，携带运行时门禁标志。</summary>
public sealed class GateEvaluationContext
{
    public bool RuntimeChangeGatePassed { get; init; }
    public bool P15GatePassed { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineRegistryPresent { get; init; }
}

/// <summary>Gate 评估决策基类。所有 Gate 决策应包含 Passed 和 BlockedReasons。</summary>
public abstract class GateDecision
{
    public abstract bool Passed { get; }
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}
