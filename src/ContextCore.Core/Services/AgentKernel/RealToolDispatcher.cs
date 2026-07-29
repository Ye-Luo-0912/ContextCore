using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using ContextCore.Abstractions;
using Microsoft.Extensions.Logging;

namespace ContextCore.Core.Services.AgentKernel;

// ===========================================================================
// RealToolDispatcher — IToolDispatcher 的真实分派实现
//
// 目标：
//   替代 EchoToolDispatcher 作为生产环境的 IToolDispatcher 实现。
//   通过 Tool 处理器注册表（IToolHandler）将 Tool 调用分派到真实实现，
//   而非简单 echo payload。
//
// 设计原则：
//   1. 注册表模式：维护 ConcurrentDictionary<string, IToolHandler>，按 ToolName 分派。
//      调用方通过 AddHandler / 构造函数注入注册真实 Tool 处理器。
//   2. 优雅降级：未注册的 Tool 返回 Succeeded=false + 错误信息（不抛异常），
//      让 Agent 循环能观察错误并继续/终止。
//   3. 无副作用默认：新构造的 RealToolDispatcher 默认无注册 Handler，
//      所有 Tool 调用返回 "tool not registered" 错误。
//      生产部署应通过 DI 工厂注册所需 Handler（如 search / read_file / calculator）。
//   4. 线程安全：ConcurrentDictionary + 不可变 ToolHandler 注册后不替换。
//
// P0-4 修复：
//   - IToolHandler.HandleAsync 改为接收 ToolExecutionContext（携带 WorkspaceId/RunId/
//     RequestId/IdempotencyKey/Payload/DeadlineAt/LeaseFence），而非仅裸 JSON Payload。
//   - 注册表冻结：Freeze() 后禁止 AddHandler；DI 工厂在注册完所有 Handler 后调用 Freeze()。
//   - fail-fast 重复注册：构造函数与 AddHandler 遇到重复 ToolName 抛
//     InvalidOperationException，不再静默覆盖（避免注册顺序依赖导致的隐蔽 bug）。
//   - 缓存 SupportedTools：首次读取后缓存为不可变 FrozenSet，避免每次读取创建新 HashSet。
// ===========================================================================

/// <summary>
/// Tool 处理器接口：由真实 Tool 实现此接口并注册到 RealToolDispatcher。
/// </summary>
/// <remarks>
/// 每个 IToolHandler 实现对应一个 Tool 名称，处理 Tool 调用并返回结果。
/// 生产部署应注册所需 Tool Handler（如 search / read_file / calculator 等）。
/// </remarks>
public interface IToolHandler
{
    /// <summary>Tool 名称（唯一标识，用于分派）。</summary>
    string ToolName { get; }

    /// <summary>Tool 描述（向模型说明何时调用此 Tool）；可选，用于原生 function calling 声明。</summary>
    string? Description { get; }

    /// <summary>
    /// Tool 参数的 JSON Schema 字符串（OpenAI / Anthropic function calling 兼容）；可选。
    /// 缺省时使用 <c>"{}"</c>（无参数约束）。
    /// </summary>
    string? ParametersJsonSchema { get; }

    /// <summary>
    /// 处理 Tool 调用。
    /// </summary>
    /// <param name="context">Tool 执行上下文（携带 WorkspaceId/RunId/RequestId/IdempotencyKey/Payload/DeadlineAt/LeaseFence）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Tool 调用结果（成功/失败 + 输出/错误）。</returns>
    ValueTask<ToolHandlerResult> HandleAsync(ToolExecutionContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Tool 处理器返回结果。
/// </summary>
public sealed record ToolHandlerResult
{
    /// <summary>是否成功。</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Tool 输出结果（成功时）。</summary>
    public string? Result { get; init; }

    /// <summary>错误信息（失败时）。</summary>
    public string? Error { get; init; }

    /// <summary>Tool 副作用类型（用于 durable 恢复时判断是否可安全重放）。</summary>
    public ToolSideEffect SideEffect { get; init; } = ToolSideEffect.None;
}

/// <summary>
/// IToolDispatcher 的真实分派实现，通过 IToolHandler 注册表分派 Tool 调用。
/// </summary>
/// <remarks>
/// 生产环境（Profile=ProductionHA 或 ToolMode=RealDispatch）应使用本实现替代
/// <see cref="EchoToolDispatcher"/>。本类通过 <see cref="IToolHandler"/> 注册表
/// 将 Tool 调用分派到真实实现。
/// </remarks>
public sealed class RealToolDispatcher : IToolDispatcher
{
    private readonly ConcurrentDictionary<string, IToolHandler> _handlers =
        new(StringComparer.Ordinal);
    private readonly ILogger<RealToolDispatcher>? _logger;
    private readonly object _freezeGate = new();
    // P0-4：冻结标志。Freeze() 后 AddHandler 抛 InvalidOperationException，防止运行时变更注册表。
    private bool _isFrozen;
    // P0-4：SupportedTools 缓存。首次读取后冻结为不可变集合，避免每次读取创建新 HashSet。
    private IReadOnlySet<string>? _supportedToolsCache;

    /// <summary>
    /// 构造 RealToolDispatcher。
    /// </summary>
    /// <param name="handlers">初始 Tool 处理器集合（可选）。</param>
    /// <param name="logger">日志记录器（可选）。</param>
    /// <exception cref="InvalidOperationException">
    /// P0-4：handlers 中存在重复 ToolName（fail-fast，不再静默覆盖）。
    /// </exception>
    public RealToolDispatcher(
        IEnumerable<IToolHandler>? handlers = null,
        ILogger<RealToolDispatcher>? logger = null)
    {
        _logger = logger;
        if (handlers is not null)
        {
            foreach (var handler in handlers)
            {
                ArgumentNullException.ThrowIfNull(handler);
                if (!_handlers.TryAdd(handler.ToolName, handler))
                {
                    throw new InvalidOperationException(
                        $"重复注册 Tool Handler：ToolName='{handler.ToolName}' 已存在。" +
                        "RealToolDispatcher 禁止静默覆盖 Handler（P0-4 fail-fast）。");
                }
            }
        }
    }

    /// <summary>
    /// 注册一个 Tool 处理器（线程安全）。
    /// </summary>
    /// <param name="handler">Tool 处理器。</param>
    /// <exception cref="InvalidOperationException">
    /// P0-4：ToolName 已存在（fail-fast 重复注册）或注册表已冻结（<see cref="Freeze"/>）。
    /// </exception>
    public void AddHandler(IToolHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_freezeGate)
        {
            if (_isFrozen)
            {
                throw new InvalidOperationException(
                    "RealToolDispatcher 注册表已冻结，禁止 AddHandler。" +
                    "DI 工厂应在注册完所有 Handler 后调用 Freeze()（P0-4）。");
            }
            if (!_handlers.TryAdd(handler.ToolName, handler))
            {
                throw new InvalidOperationException(
                    $"重复注册 Tool Handler：ToolName='{handler.ToolName}' 已存在。" +
                    "RealToolDispatcher 禁止静默覆盖 Handler（P0-4 fail-fast）。");
            }
            // 注册表变更后使缓存失效
            _supportedToolsCache = null;
        }
    }

    /// <summary>
    /// P0-4：冻结注册表。调用后 <see cref="AddHandler"/> 抛异常，<see cref="SupportedTools"/> 缓存为不可变集合。
    /// DI 工厂应在注册完所有 Handler 后调用此方法，防止运行时变更注册表。
    /// </summary>
    public void Freeze()
    {
        lock (_freezeGate)
        {
            _isFrozen = true;
            // 冻结时立即物化缓存
            _supportedToolsCache ??= BuildSupportedTools();
        }
    }

    /// <inheritdoc />
    public IReadOnlySet<string> SupportedTools
    {
        get
        {
            // P0-4：缓存 SupportedTools，避免每次读取创建新 HashSet。
            // 未冻结时延迟物化并缓存；冻结后缓存永不变更。
            if (_supportedToolsCache is { } cached)
            {
                return cached;
            }
            lock (_freezeGate)
            {
                if (_supportedToolsCache is { } cached2)
                {
                    return cached2;
                }
                _supportedToolsCache = BuildSupportedTools();
                return _supportedToolsCache;
            }
        }
    }

    /// <summary>构建当前注册表快照为不可变集合。</summary>
    private IReadOnlySet<string> BuildSupportedTools()
    {
        // 冻结后用 FrozenSet 提供真正的不可变性；未冻结时用 HashSet 快照（值可能随后续注册变更）。
        if (_isFrozen)
        {
            return _handlers.Keys.ToFrozenSet(StringComparer.Ordinal);
        }
        return new HashSet<string>(_handlers.Keys, StringComparer.Ordinal);
    }

    /// <summary>
    /// P0-1：从已注册的 IToolHandler 集合构建 AgentToolDefinition 列表，
    /// 用于向模型声明可调用的 Tool 及其参数 schema（原生 function calling）。
    /// </summary>
    /// <returns>不可变的 AgentToolDefinition 列表（按注册表当前快照构建）。</returns>
    public IReadOnlyList<AgentToolDefinition> GetToolDefinitions()
    {
        return _handlers.Values
            .Select(h => new AgentToolDefinition
            {
                Name = h.ToolName,
                Description = h.Description,
                ParametersJsonSchema = h.ParametersJsonSchema ?? "{}"
            })
            .ToList();
    }

    /// <inheritdoc />
    public async ValueTask<ToolDispatchResult> DispatchAsync(
        ToolDispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // 未注册的 Tool → 返回错误（不抛异常）
        if (!_handlers.TryGetValue(request.ToolName, out var handler))
        {
            _logger?.LogWarning(
                "Tool '{ToolName}' 未注册到 RealToolDispatcher。RequestId={RequestId}",
                request.ToolName, request.RequestId);
            return new ToolDispatchResult
            {
                Succeeded = false,
                Result = $"[Error] Tool '{request.ToolName}' 未注册。已注册: {string.Join(", ", _handlers.Keys)}",
                Duration = TimeSpan.Zero,
                SideEffect = ToolSideEffect.None
            };
        }

        // P0-4：从 ToolDispatchRequest 构造 ToolExecutionContext，让 Handler 能访问
        // WorkspaceId/RunId/RequestId/IdempotencyKey/Payload/DeadlineAt/LeaseFence。
        var context = new ToolExecutionContext
        {
            WorkspaceId = request.WorkspaceId ?? string.Empty,
            RunId = request.RunId ?? string.Empty,
            RequestId = request.RequestId,
            IdempotencyKey = request.IdempotencyKey,
            Payload = request.Payload,
            DeadlineAt = request.DeadlineAt,
            LeaseFence = request.LeaseFence
        };

        var sw = Stopwatch.StartNew();
        try
        {
            var handlerResult = await handler.HandleAsync(context, cancellationToken).ConfigureAwait(false);
            sw.Stop();

            return new ToolDispatchResult
            {
                Succeeded = handlerResult.Succeeded,
                Result = handlerResult.Succeeded
                    ? handlerResult.Result ?? string.Empty
                    : handlerResult.Error ?? "Tool 处理失败（无错误信息）。",
                Duration = sw.Elapsed,
                SideEffect = handlerResult.SideEffect
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.LogError(ex,
                "Tool '{ToolName}' 处理异常。RequestId={RequestId}",
                request.ToolName, request.RequestId);
            return new ToolDispatchResult
            {
                Succeeded = false,
                Result = $"[Exception] Tool '{request.ToolName}' 处理异常：{ex.Message}",
                Duration = sw.Elapsed,
                SideEffect = ToolSideEffect.None
            };
        }
    }
}
