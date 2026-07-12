using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 默认的 <see cref="IContextEvolutionAgent"/> 空实现。
/// 不监测任何演化机会，返回空周期结果。
/// 用于未接入真实演化逻辑时的默认占位，确保 DI 可解析且不触发副作用。
/// </summary>
public sealed class NullContextEvolutionAgent : IContextEvolutionAgent
{
    public Task<EvolutionCycleResult> RunCycleAsync(
        EvolutionCycleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = DateTimeOffset.UtcNow;
        var result = new EvolutionCycleResult
        {
            CycleId = $"null-evolution-{now:yyyyMMddHHmmss}",
            StartedAt = now,
            CompletedAt = now,
            Goals = Array.Empty<EvolutionGoal>(),
            Steps = Array.Empty<EvolutionStep>(),
            ProposedCount = 0,
            AppliedCount = 0,
            SkippedCount = 0,
            FailedCount = 0
        };

        return Task.FromResult(result);
    }
}
