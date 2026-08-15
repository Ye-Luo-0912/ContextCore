using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.DecisionEngine;

/// <summary>
/// 两阶段排序的第二阶段：确定性 reranker。第一阶段（Provider 召回 + Scorer 评分）以低成本
/// 保召回；本阶段只对有限候选窗口做重排，之后才交给分配器。
/// 只改变候选顺序，不改 FinalScore；provenance 通过 ScoreBreakdown 记录重排分量。
/// </summary>
public interface ICandidateReranker
{
    /// <summary>对已评分候选做有限窗口确定性重排，返回新的候选顺序。</summary>
    Task<IReadOnlyList<ContextCandidateEnvelope>> RerankAsync(
        IReadOnlyList<ContextCandidateEnvelope> scored,
        EffectivePolicySnapshot snapshot,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 默认确定性 reranker。
/// 规则：先按第一阶段 FinalScore 降序取有限窗口（<see cref="RerankWindowSize"/>），
/// 窗口内重排分 = FinalScore + 唯一通道加成（窗口内只出现一次的通道 +3，来源多样性），
/// 同分按 CandidateId 升序（确定性 tie-break）；窗口外的候选保持第一阶段顺序。
/// 无随机性、无时钟依赖、不比较跨通道原始分；是「先建立 deterministic reranker」的可解释起点。
/// </summary>
public sealed class DefaultCandidateReranker : ICandidateReranker
{
    /// <summary>第二阶段只重排的有限候选窗口大小（超出部分保持第一阶段顺序）。</summary>
    public const int RerankWindowSize = 32;

    /// <summary>唯一通道加成：窗口内该通道只出现一次时的固定加分。</summary>
    private const double UniqueChannelBoost = 3.0;

    private const string RerankDiversityKey = "rerank_diversity";

    /// <inheritdoc />
    public Task<IReadOnlyList<ContextCandidateEnvelope>> RerankAsync(
        IReadOnlyList<ContextCandidateEnvelope> scored,
        EffectivePolicySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scored);
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        if (scored.Count == 0)
        {
            return Task.FromResult(scored);
        }

        if (scored.Count <= RerankWindowSize)
        {
            return Task.FromResult<IReadOnlyList<ContextCandidateEnvelope>>(RerankWindow(scored));
        }

        var window = scored.Take(RerankWindowSize).ToArray();
        var rest = scored.Skip(RerankWindowSize).ToArray();
        return Task.FromResult<IReadOnlyList<ContextCandidateEnvelope>>(
            RerankWindow(window).Concat(rest).ToArray());
    }

    private static IReadOnlyList<ContextCandidateEnvelope> RerankWindow(IReadOnlyList<ContextCandidateEnvelope> window)
    {
        // 窗口内通道出现次数：只出现一次的通道给多样性加成，避免单一通道挤占其他来源。
        var channelCounts = window
            .GroupBy(e => e.Source)
            .ToDictionary(g => g.Key, g => g.Count());

        return window
            .Select(e => new
            {
                Envelope = e,
                Diversity = channelCounts[e.Source] == 1 ? UniqueChannelBoost : 0.0
            })
            .OrderByDescending(x => x.Envelope.Utility.FinalScore + x.Diversity)
            .ThenBy(x => x.Envelope.CandidateId, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Envelope with
            {
                Features = x.Envelope.Features with
                {
                    ScoreBreakdown = new Dictionary<string, double>(
                        x.Envelope.Features.ScoreBreakdown, StringComparer.Ordinal)
                    {
                        [RerankDiversityKey] = x.Diversity
                    }
                }
            })
            .ToArray();
    }
}
