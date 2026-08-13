using System.Collections.Concurrent;

namespace ContextCore.Core.Services.DecisionEngine;

// 每个 canary run 使用独立的 CutoverController，避免多个 run 改同一百分比。
// 没有活跃 canary run 时用默认控制器（百分比来自 CC_CUTOVER_PERCENTAGE，缺省 100）。
// 每个 canary run 首次创建时仍从 0 起步，由晋升服务再调到阶梯首档。

/// <summary>
/// Per-runId 的 <see cref="CutoverController"/> 注册表。
/// </summary>
/// <remarks>
/// 每个 canary run 使用独立控制器，避免多个 run 改同一百分比。
/// 没有活跃 canary run 时回退到默认控制器（百分比来自环境变量，缺省 100）。
/// </remarks>
public sealed class CutoverControllerRegistry
{
    private readonly ConcurrentDictionary<string, CutoverController> _controllers
        = new(StringComparer.Ordinal);
    private readonly CutoverController _defaultController;

    /// <summary>构造注册表。</summary>
    /// <param name="defaultController">默认控制器（无活跃 canary run 时使用；CutoverPercentage 从环境变量读取）。</param>
    public CutoverControllerRegistry(CutoverController defaultController)
    {
        _defaultController = defaultController ?? throw new ArgumentNullException(nameof(defaultController));
    }

    /// <summary>默认控制器（无活跃 canary run 时使用）。</summary>
    public CutoverController Default => _defaultController;

    /// <summary>
    /// 获取或创建指定 run 的控制器。首次访问时创建一个 CutoverPercentage=0 的新实例
    /// （由 <see cref="CanaryProgressionService.InitializeCanary"/> 后续设置为首档百分比）。
    /// </summary>
    /// <param name="runId">Canary run ID。</param>
    /// <returns>该 run 专用的 <see cref="CutoverController"/>。</returns>
    public CutoverController GetOrCreate(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return _controllers.GetOrAdd(runId, _ => new CutoverController(0));
    }

    /// <summary>
    /// 获取当前活跃的 canary run 的控制器。
    /// 仅当恰好只有一个活跃 run 时返回该控制器；零个或多个时返回 null
    /// （多 run 场景应由调用方通过 <see cref="GetOrCreate"/> 按 runId 显式解析）。
    /// </summary>
    public CutoverController? GetActive()
    {
        if (_controllers.Count != 1)
        {
            return null;
        }
        // Count==1 时安全枚举取唯一值
        foreach (var kv in _controllers)
        {
            return kv.Value;
        }
        return null;
    }

    /// <summary>注册指定 run 的控制器（覆盖已存在的同 runId 控制器）。</summary>
    /// <param name="runId">Canary run ID。</param>
    /// <param name="controller">该 run 专用的控制器。</param>
    public void Register(string runId, CutoverController controller)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(controller);
        _controllers[runId] = controller;
    }

    /// <summary>run 终态后注销控制器（释放 per-run 隔离状态）。</summary>
    /// <param name="runId">Canary run ID。</param>
    public void Unregister(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        _controllers.TryRemove(runId, out _);
    }

    /// <summary>当前已注册的活跃 run 数量（测试与诊断用）。</summary>
    public int ActiveCount => _controllers.Count;
}

/// <summary>
/// CutoverController 解析器接口。按 runId 解析到对应的 <see cref="CutoverController"/>。
/// </summary>
/// <remarks>
/// <see cref="Resolve"/> 在 runId 为 null/空时返回默认控制器（无活跃 canary run 路径）；
/// runId 非空时返回该 run 的专用控制器（按需创建）。
/// </remarks>
public interface ICutoverControllerResolver
{
    /// <summary>
    /// 解析指定 run 的 <see cref="CutoverController"/>。
    /// </summary>
    /// <param name="runId">Canary run ID；null 或空时返回默认控制器。</param>
    /// <returns>对应的 <see cref="CutoverController"/>（永不为 null）。</returns>
    CutoverController Resolve(string? runId = null);
}

/// <summary>
/// 默认的 <see cref="ICutoverControllerResolver"/> 实现，包装 <see cref="CutoverControllerRegistry"/>。
/// </summary>
public sealed class DefaultCutoverControllerResolver : ICutoverControllerResolver
{
    private readonly CutoverControllerRegistry _registry;

    /// <summary>构造解析器。</summary>
    /// <param name="registry">被包装的注册表（必填）。</param>
    public DefaultCutoverControllerResolver(CutoverControllerRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <inheritdoc />
    public CutoverController Resolve(string? runId = null)
    {
        if (string.IsNullOrEmpty(runId))
        {
            return _registry.Default;
        }
        return _registry.GetOrCreate(runId);
    }
}
