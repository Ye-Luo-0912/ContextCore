using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Attention;

/// <summary>
/// 用于执行注意力配置文件实验的内部类。此类负责根据提供的上下文检索请求、候选列表以及当前打包结果，运行一系列预定义或传入的注意力配置文件实验，并生成实验报告。
/// </summary>
/// <remarks>
/// 实验过程旨在评估不同注意力配置文件在特定情境下的表现，从而为后续优化提供依据。通过<see cref="RunAsync"/>方法异步执行实验，最终返回包含所有参与实验配置文件及其对应结果的报告。
/// </remarks>
internal sealed class AttentionProfileExperimentRunner
{
    private readonly IContextLearningStore? _learningStore;
    private readonly IReadOnlyList<ContextAttentionProfile> _profiles;

    public AttentionProfileExperimentRunner(
        IEnumerable<ContextAttentionProfile>? profiles = null,
        IContextLearningStore? learningStore = null)
    {
        _profiles = (profiles ?? ContextAttentionProfile.CreateShadowExperimentProfiles()).ToArray();
        _learningStore = learningStore;
    }

    /// <summary>
    /// 异步执行注意力配置文件实验。
    /// </summary>
    /// <param name="operationId">操作ID，用于标识本次实验。</param>
    /// <param name="request">上下文检索请求，包含实验所需的初始信息。</param>
    /// <param name="rankedCandidates">已排序的候选列表，表示根据某些标准预先排序的检索候选人。</param>
    /// <param name="currentPackingResult">当前打包结果，包含已被选择和被丢弃的决策。</param>
    /// <param name="cancellationToken">取消令牌，用于支持取消正在进行的操作。</param>
    /// <returns>返回一个<see cref="AttentionProfileExperimentReport"/>对象，包含了所有参与实验的注意力配置文件及其对应的实验结果。</returns>
    /// <exception cref="ArgumentNullException">当<paramref name="request"/>, <paramref name="rankedCandidates"/>, 或<paramref name="currentPackingResult"/>为null时抛出。</exception>
    public async Task<AttentionProfileExperimentReport> RunAsync(
        string operationId,
        ContextRetrievalRequest request,
        IReadOnlyList<ContextRetrievalCandidate> rankedCandidates,
        RetrievalPackingResult currentPackingResult,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rankedCandidates);
        ArgumentNullException.ThrowIfNull(currentPackingResult);

        var results = new List<AttentionProfileExperimentResult>(_profiles.Count);
        foreach (var profile in _profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scorer = new RuleBasedContextAttentionScorer(profile, _learningStore);
            var scores = await scorer.ScoreAsync(request, rankedCandidates, cancellationToken).ConfigureAwait(false);
            var report = AttentionShadowReportBuilder.Build(
                operationId,
                request,
                rankedCandidates,
                currentPackingResult,
                scores);

            results.Add(new AttentionProfileExperimentResult
            {
                ProfileId = profile.ProfileId,
                PolicyVersion = profile.PolicyVersion,
                AttentionScores = scores,
                ShadowReport = report
            });
        }

        return new AttentionProfileExperimentReport
        {
            OperationId = operationId,
            Profiles = results
        };
    }
}
