using ContextCore.Abstractions;

namespace ContextCore.Core.Services.DecisionEngine;

/// <summary>
/// 候选分数校准器：把各通道的原始分数按分桶表映射到同一可比刻度（0–100）。
/// 语义不同的 lexical/vector/graph 原始分不可直接比较——校准是「先采用可解释的
/// 确定性方法」：桶边界与取值即策略，可审计、可确定性复现。
/// </summary>
public interface ICandidateScoreCalibrator
{
    /// <summary>把某通道候选的原始分映射到公共刻度；不认识的来源原样返回。</summary>
    double Calibrate(ContextCandidateSource source, double rawScore);
}

/// <summary>
/// 默认分桶校准器。桶为左闭右开 [min, max)；原始分小于首桶或超过末桶取最近档。
/// 初始取值反映各通道语义与当前 provider 原始分的含义：
/// - Lexical：关键词命中（基础 50，title 整句命中 +50，可读 ts_rank×100）——中强信号；
/// - Semantic：向量相似度 ×100——强信号，高相似度可信度高；
/// - Graph：关系邻居扩展（固定 30）——弱信号，只是「命中条目的邻居」；
/// - WorkingMemory：新鲜但未验证（固定 50）——中低；
/// - StableMemory：已验证长期有效（固定 80）——中高；
/// - Mandatory / Constraint / 未知：原样返回（不是召回通道，不参与校准）。
/// 桶取值是初始校准，后续用含 corpus 打分的评测数据细化。
/// </summary>
public sealed class DefaultCandidateScoreCalibrator : ICandidateScoreCalibrator
{
    private static readonly (double Min, double Max, double Calibrated)[] LexicalBuckets =
    {
        (double.NegativeInfinity, 40.0, 35.0),
        (40.0, 60.0, 50.0),
        (60.0, 80.0, 65.0),
        (80.0, double.PositiveInfinity, 85.0)
    };

    private static readonly (double Min, double Max, double Calibrated)[] SemanticBuckets =
    {
        (double.NegativeInfinity, 40.0, 30.0),
        (40.0, 60.0, 50.0),
        (60.0, 80.0, 70.0),
        (80.0, double.PositiveInfinity, 90.0)
    };

    private static readonly (double Min, double Max, double Calibrated)[] GraphBuckets =
    {
        (double.NegativeInfinity, 30.0, 25.0),
        (30.0, double.PositiveInfinity, 55.0)
    };

    /// <inheritdoc />
    public double Calibrate(ContextCandidateSource source, double rawScore)
        => source switch
        {
            ContextCandidateSource.Lexical => Lookup(LexicalBuckets, rawScore),
            ContextCandidateSource.Semantic => Lookup(SemanticBuckets, rawScore),
            ContextCandidateSource.Graph => Lookup(GraphBuckets, rawScore),
            ContextCandidateSource.WorkingMemory => 55.0,
            ContextCandidateSource.StableMemory => 75.0,
            _ => rawScore
        };

    private static double Lookup((double Min, double Max, double Calibrated)[] buckets, double rawScore)
    {
        foreach (var (min, max, calibrated) in buckets)
        {
            if (rawScore >= min && rawScore < max)
            {
                return calibrated;
            }
        }

        return buckets[^1].Calibrated;
    }
}
