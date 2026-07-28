using System.Collections.Concurrent;
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

    /// <summary>处理 Tool 调用。</summary>
    /// <param name="payload">Tool 调用负载（自由文本；语义由 Tool 实现约定）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Tool 调用结果（成功/失败 + 输出/错误）。</returns>
    ValueTask<ToolHandlerResult> HandleAsync(string payload, CancellationToken cancellationToken = default);
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

    /// <summary>
    /// 构造 RealToolDispatcher。
    /// </summary>
    /// <param name="handlers">初始 Tool 处理器集合（可选）。</param>
    /// <param name="logger">日志记录器（可选）。</param>
    public RealToolDispatcher(
        IEnumerable<IToolHandler>? handlers = null,
        ILogger<RealToolDispatcher>? logger = null)
    {
        _logger = logger;
        if (handlers is not null)
        {
            foreach (var handler in handlers)
            {
                _handlers[handler.ToolName] = handler;
            }
        }
    }

    /// <summary>
    /// 注册一个 Tool 处理器（线程安全）。
    /// </summary>
    /// <param name="handler">Tool 处理器。</param>
    public void AddHandler(IToolHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[handler.ToolName] = handler;
    }

    /// <inheritdoc />
    public IReadOnlySet<string> SupportedTools =>
        new HashSet<string>(_handlers.Keys, StringComparer.Ordinal);

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

        var sw = Stopwatch.StartNew();
        try
        {
            var handlerResult = await handler.HandleAsync(request.Payload, cancellationToken).ConfigureAwait(false);
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
