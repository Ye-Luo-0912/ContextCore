using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Service.Extensions;

// ===========================================================================
// R23-7：Agent Runtime DI 扩展方法。
//
// 提供 AddAgentRuntime / AddGenericToolAgentRuntime / AddCodexAgentRuntime /
// AddClaudeAgentRuntime / AddAgentContextDeltaCalculator / AddAgentCheckpointStore
// 等扩展，将 R23-1~R23-6 的实现注册到 DI 容器。
//
// 设计边界：
//   - 扩展方法为 public，允许外部 host 项目调用；
//   - 不强制注册所有组件；调用方按需选择；
//   - 默认实现均为 Singleton（Agent session 状态需跨请求共享）；
//   - 不与 AddContextCore 强耦合；可独立调用。
// ===========================================================================

/// <summary>
/// R23-7：Agent Runtime DI 扩展方法。
/// </summary>
public static class AgentRuntimeServiceCollectionExtensions
{
    /// <summary>
    /// 注册 <see cref="IAgentRuntimeRegistry"/> + 默认实现 <see cref="DefaultAgentRuntimeRegistry"/>。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <returns>当前容器（链式调用）。</returns>
    public static IServiceCollection AddAgentRuntimeRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IAgentRuntimeRegistry, DefaultAgentRuntimeRegistry>();
        return services;
    }

    /// <summary>
    /// 注册 <see cref="GenericToolAgentAdapter"/> 作为 <see cref="IAgentRuntime"/> 单例。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="timeProvider">可选时间提供者。</param>
    /// <returns>当前容器（链式调用）。</returns>
    public static IServiceCollection AddGenericToolAgentRuntime(
        this IServiceCollection services,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<GenericToolAgentAdapter>(sp =>
            new GenericToolAgentAdapter(timeProvider));
        services.AddSingleton<AgentRuntimeBase>(sp => sp.GetRequiredService<GenericToolAgentAdapter>());
        services.AddSingleton<IAgentRuntime>(sp => sp.GetRequiredService<GenericToolAgentAdapter>());
        return services;
    }

    /// <summary>
    /// 注册 <see cref="CodexAgentRuntimeAdapter"/> 作为 <see cref="IAgentRuntime"/> 单例。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="timeProvider">可选时间提供者。</param>
    /// <returns>当前容器（链式调用）。</returns>
    public static IServiceCollection AddCodexAgentRuntime(
        this IServiceCollection services,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<CodexAgentRuntimeAdapter>(sp =>
            new CodexAgentRuntimeAdapter(timeProvider));
        services.AddSingleton<AgentRuntimeBase>(sp => sp.GetRequiredService<CodexAgentRuntimeAdapter>());
        services.AddSingleton<IAgentRuntime>(sp => sp.GetRequiredService<CodexAgentRuntimeAdapter>());
        return services;
    }

    /// <summary>
    /// 注册 <see cref="ClaudeCodeAgentRuntimeAdapter"/> 作为 <see cref="IAgentRuntime"/> 单例。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="timeProvider">可选时间提供者。</param>
    /// <returns>当前容器（链式调用）。</returns>
    public static IServiceCollection AddClaudeCodeAgentRuntime(
        this IServiceCollection services,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ClaudeCodeAgentRuntimeAdapter>(sp =>
            new ClaudeCodeAgentRuntimeAdapter(timeProvider));
        services.AddSingleton<AgentRuntimeBase>(sp => sp.GetRequiredService<ClaudeCodeAgentRuntimeAdapter>());
        services.AddSingleton<IAgentRuntime>(sp => sp.GetRequiredService<ClaudeCodeAgentRuntimeAdapter>());
        return services;
    }

    /// <summary>
    /// 注册 <see cref="DefaultAgentWorkspaceContextProvider"/> 作为
    /// <see cref="IAgentWorkspaceContextProvider"/> 单例。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="timeProvider">可选时间提供者。</param>
    /// <returns>当前容器（链式调用）。</returns>
    /// <remarks>
    /// 自动解析已注册的 <see cref="IAgentRuntime"/>；若注册了多个 runtime，
    /// 将使用最后注册的一个作为 provider 的 backing adapter。
    /// </remarks>
    public static IServiceCollection AddAgentWorkspaceContextProvider(
        this IServiceCollection services,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IAgentWorkspaceContextProvider>(sp =>
        {
            var adapter = sp.GetService<AgentRuntimeBase>()
                ?? throw new InvalidOperationException(
                    "AddAgentWorkspaceContextProvider 需要先注册一个 AgentRuntimeBase 实现" +
                    "（如 AddGenericToolAgentRuntime / AddCodexAgentRuntime / AddClaudeCodeAgentRuntime）。");
            return new DefaultAgentWorkspaceContextProvider(adapter, timeProvider);
        });
        return services;
    }

    /// <summary>
    /// 注册 <see cref="DefaultAgentContextDeltaCalculator"/> 作为
    /// <see cref="IAgentContextDeltaCalculator"/> 单例。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <returns>当前容器（链式调用）。</returns>
    public static IServiceCollection AddAgentContextDeltaCalculator(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IAgentContextDeltaCalculator, DefaultAgentContextDeltaCalculator>();
        return services;
    }

    /// <summary>
    /// 注册 <see cref="InMemoryAgentCheckpointStore"/> 作为
    /// <see cref="IAgentCheckpointStore"/> 单例。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <returns>当前容器（链式调用）。</returns>
    /// <remarks>
    /// 注意：此实现为 in-memory；进程重启后丢失。
    /// 生产场景应替换为持久化实现（如 PostgresAgentCheckpointStore，后续阶段提供）。
    /// </remarks>
    public static IServiceCollection AddInMemoryAgentCheckpointStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IAgentCheckpointStore, InMemoryAgentCheckpointStore>();
        return services;
    }

    /// <summary>
    /// 一键注册全部 R23 默认实现（GenericTool runtime + provider + delta calculator + checkpoint store）。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="timeProvider">可选时间提供者。</param>
    /// <returns>当前容器（链式调用）。</returns>
    /// <remarks>
    /// 包含：
    ///   - <see cref="AddAgentRuntimeRegistry"/>
    ///   - <see cref="AddGenericToolAgentRuntime"/>
    ///   - <see cref="AddAgentWorkspaceContextProvider"/>
    ///   - <see cref="AddAgentContextDeltaCalculator"/>
    ///   - <see cref="AddInMemoryAgentCheckpointStore"/>
    /// </remarks>
    public static IServiceCollection AddAgentRuntimeDefaults(
        this IServiceCollection services,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services
            .AddAgentRuntimeRegistry()
            .AddGenericToolAgentRuntime(timeProvider)
            .AddAgentWorkspaceContextProvider(timeProvider)
            .AddAgentContextDeltaCalculator()
            .AddInMemoryAgentCheckpointStore();
    }

    // ===== R24 扩展 =====

    /// <summary>
    /// 注册 <see cref="DefaultAgentContextBridge"/> 作为 <see cref="IAgentContextBridge"/> 单例。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="timeProvider">可选时间提供者。</param>
    /// <returns>当前容器（链式调用）。</returns>
    /// <remarks>
    /// 自动解析已注册的 <see cref="IContextPackageBuilder"/>；
    /// 需要先注册 ContextCore 上下文构建管线（如 AddContextCore）。
    /// </remarks>
    public static IServiceCollection AddAgentContextBridge(
        this IServiceCollection services,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IAgentContextBridge>(sp =>
        {
            var packageBuilder = sp.GetService<IContextPackageBuilder>()
                ?? throw new InvalidOperationException(
                    "AddAgentContextBridge 需要先注册 IContextPackageBuilder" +
                    "（如通过 AddContextCore 注册 ContextCore 上下文构建管线）。");
            return new DefaultAgentContextBridge(packageBuilder, timeProvider);
        });
        return services;
    }

    /// <summary>
    /// 注册 <see cref="InMemoryAgentTaskStateStore"/> 作为
    /// <see cref="IAgentTaskStateStore"/> 单例。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <returns>当前容器（链式调用）。</returns>
    /// <remarks>
    /// 注意：此实现为 in-memory；进程重启后丢失。
    /// 生产场景应替换为持久化实现（如 PostgresAgentTaskStateStore，后续阶段提供）。
    /// </remarks>
    public static IServiceCollection AddInMemoryAgentTaskStateStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IAgentTaskStateStore, InMemoryAgentTaskStateStore>();
        return services;
    }

    /// <summary>
    /// 一键注册全部 R23 + R24 默认实现（在 AddAgentRuntimeDefaults 基础上追加 bridge + task state store）。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="timeProvider">可选时间提供者。</param>
    /// <returns>当前容器（链式调用）。</returns>
    /// <remarks>
    /// 注意：<see cref="AddAgentContextBridge"/> 需要先注册 <see cref="IContextPackageBuilder"/>；
    /// 若未注册则会在解析时抛异常。调用方应先调用 AddContextCore 注册 ContextCore 管线。
    /// </remarks>
    public static IServiceCollection AddAgentRuntimeAndBridgeDefaults(
        this IServiceCollection services,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services
            .AddAgentRuntimeDefaults(timeProvider)
            .AddAgentContextBridge(timeProvider)
            .AddInMemoryAgentTaskStateStore();
    }
}
