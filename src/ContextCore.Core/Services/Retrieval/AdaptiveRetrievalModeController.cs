using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Retrieval;

/// <summary>
/// 模式切换审计记录。
/// </summary>
public sealed record AdaptiveModeTransition
{
    public required AdaptiveRetrievalMode From { get; init; }

    public required AdaptiveRetrievalMode To { get; init; }

    public required string Actor { get; init; }

    public required DateTimeOffset TransitionedAt { get; init; }

    public string? Reason { get; init; }
}

/// <summary>
/// 自适应检索运行模式控制器（WP-X）：支持生产运行时动态切换
/// Disabled / Shadow / Active，并记录切换审计。
/// 生产启用流程：Disabled（默认，fail-closed）→ Shadow（观察期，计算不应用）
/// → 审核 → Active（应用）；一键回退 = 切换回 Disabled。
/// 变更可见性：控制器为单例，planner 每次规划读取当前模式（volatile 读，无需锁）。
/// </summary>
public sealed class AdaptiveRetrievalModeController
{
    private readonly List<AdaptiveModeTransition> _history = new();
    private readonly object _gate = new();
    private volatile AdaptiveRetrievalMode _currentMode;

    /// <summary>历史保留上限。</summary>
    private const int MaxHistory = 100;

    /// <summary>初始化控制器。</summary>
    /// <param name="initialMode">初始模式（默认 Disabled，fail-closed）。</param>
    /// <param name="actor">初始模式归属者（审计用）。</param>
    public AdaptiveRetrievalModeController(
        AdaptiveRetrievalMode initialMode = AdaptiveRetrievalMode.Disabled,
        string actor = "startup")
    {
        _currentMode = initialMode;
        _history.Add(new AdaptiveModeTransition
        {
            From = initialMode,
            To = initialMode,
            Actor = actor,
            TransitionedAt = DateTimeOffset.UtcNow,
            Reason = "初始化"
        });
    }

    /// <summary>当前模式（volatile 读，planner 每轮规划直接读取）。</summary>
    public AdaptiveRetrievalMode CurrentMode => _currentMode;

    /// <summary>切换运行模式（记录审计）。同模式切换幂等（记录但无副作用）。</summary>
    public AdaptiveModeTransition Transition(
        AdaptiveRetrievalMode targetMode,
        string actor,
        string? reason = null)
    {
        var transition = new AdaptiveModeTransition
        {
            From = _currentMode,
            To = targetMode,
            Actor = actor,
            TransitionedAt = DateTimeOffset.UtcNow,
            Reason = reason
        };

        lock (_gate)
        {
            _currentMode = targetMode;
            _history.Add(transition);
            if (_history.Count > MaxHistory)
            {
                _history.RemoveRange(0, _history.Count - MaxHistory);
            }
        }

        return transition;
    }

    /// <summary>最近 N 条切换审计（按时间正序；最近在尾部）。</summary>
    public IReadOnlyList<AdaptiveModeTransition> GetHistory(int take = 20)
    {
        lock (_gate)
        {
            var count = take > 0 ? take : 20;
            var start = Math.Max(0, _history.Count - count);
            return _history.Skip(start).Take(count).ToArray();
        }
    }
}
