using ContextCore.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ContextCore.Inference.Onnx;

// ===========================================================================
// OnnxInferenceServiceCollectionExtensions
//
// 目标：
// 为 ContextCore.Inference.Onnx 提供 DI 注册扩展，让 Service 层能在启动时
// 把 OnnxInferenceEngine 注入到 IBatchInferenceEngine 接口位。
//
// 设计边界：
// 1. 不在 DI 容器中直接创建 InferenceSession（需要在启动时加载模型文件，
// 可能涉及 I/O 与校验失败，不应阻塞 DI 容器构建）。
// OnnxInferenceEngine 通过工厂模式在构造时延迟加载模型。
// 2. 支持两种注册方式：
// a) 直接注册：使用调用方提供的 IOnnxInferenceSession（通常用于测试）。
// b) 工厂注册：注入 IOnnxInferenceSessionFactory + OnnxInferenceEngineOptions +
// 可选 ModelArtifactDescriptor，由工厂在首次解析时创建 session。
// 3. IBatchInferenceEngine 绑定到 OnnxInferenceEngine；不替代 DeterministicBatchInferenceEngine
// （后者仍作为 fallback 注入到 IPerformanceMonitor 等路径）。
//
// 新增：
// AddModelActivationManager 方法注册 IModelActivationManager 作为 IBatchInferenceEngine 的实现，
// 以 DeterministicBatchInferenceEngine 为 fallback，运行时通过 ActivateAsync 切换到 OnnxInferenceEngine。
// 消费方注入 IBatchInferenceEngine 无需感知激活状态。
// ===========================================================================

/// <summary>
/// 注册 ContextCore ONNX 推理引擎。
/// </summary>
public static class OnnxInferenceServiceCollectionExtensions
{
    /// <summary>
    /// 注册 OnnxInferenceEngine 为 <see cref="IBatchInferenceEngine"/> 的实现。
    /// 调用方需提供已构造的 <see cref="OnnxInferenceEngine"/> 实例（通常用于测试或本地预览）。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="engine">已构造的 ONNX 推理引擎实例。</param>
    /// <returns>DI 容器（链式调用）。</returns>
    public static IServiceCollection AddOnnxInferenceEngine(
        this IServiceCollection services,
        OnnxInferenceEngine engine)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(engine);

        services.AddSingleton(engine);
        services.AddSingleton<IBatchInferenceEngine>(sp => sp.GetRequiredService<OnnxInferenceEngine>());
        return services;
    }

    /// <summary>
    /// 注册 OnnxInferenceEngine 为 <see cref="IBatchInferenceEngine"/> 的实现，
    /// 并通过 <see cref="IOnnxInferenceSessionFactory"/> 在首次解析时延迟创建 session。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="options">ONNX 推理配置（张量映射、线程数、超时）。</param>
    /// <param name="calibrationVersion">校准版本号（默认 "default-v1"）。</param>
    /// <param name="descriptor">可选的模型工件描述符（生产路径应通过 IModelArtifactRegistry 解析后传入）。</param>
    /// <returns>DI 容器（链式调用）。</returns>
    public static IServiceCollection AddOnnxInferenceEngine(
        this IServiceCollection services,
        OnnxInferenceEngineOptions options,
        string? calibrationVersion = null,
        ModelArtifactDescriptor? descriptor = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        // 注册 options 与 descriptor（即使为 null 也注册，让工厂内部解析）
        services.AddSingleton(options);
        if (descriptor is not null)
        {
            services.AddSingleton(descriptor);
        }

        // 默认注册 OnnxRuntimeInferenceSessionFactory；调用方可覆盖
        services.AddSingleton<IOnnxInferenceSessionFactory, OnnxRuntimeInferenceSessionFactory>();

        // OnnxInferenceEngine + IOnnxInferenceSession 在首次解析时由工厂创建。
        services.AddSingleton<IOnnxInferenceSession>(sp =>
        {
            var factory = sp.GetRequiredService<IOnnxInferenceSessionFactory>();
            var opts = sp.GetRequiredService<OnnxInferenceEngineOptions>();
            var desc = sp.GetService<ModelArtifactDescriptor>();
            return factory.CreateAsync(opts, desc).GetAwaiter().GetResult();
        });

        services.AddSingleton<OnnxInferenceEngine>(sp =>
        {
            var session = sp.GetRequiredService<IOnnxInferenceSession>();
            var opts = sp.GetRequiredService<OnnxInferenceEngineOptions>();
            return new OnnxInferenceEngine(session, opts, calibrationVersion);
        });

        services.AddSingleton<IBatchInferenceEngine>(sp => sp.GetRequiredService<OnnxInferenceEngine>());
        return services;
    }

    /// <summary>
    /// 注册 <see cref="IModelActivationManager"/> 为 <see cref="IBatchInferenceEngine"/> 的实现。
    /// 未激活时委托给 fallback（由 fallbackEngineFactory 提供），激活后委托给 OnnxInferenceEngine。
    /// </summary>
    /// <remarks>
    /// 此注册将 <see cref="ICalibrationValidator"/> 纳入模型激活（加载）路径，
    /// 在 <see cref="IModelActivationManager.ActivateAsync"/> 调用时验证校准参数统计有效性。
    /// <see cref="IFeatureSchemaValidator"/> 应由上游消费方在推理前调用（生产推理路径）。
    /// 子问题1：fallback 引擎注册为 <see cref="IFallbackInferenceEngine"/>（而非 IBatchInferenceEngine），
    /// 避免与 ModelActivationManager 自身注册为 IBatchInferenceEngine 冲突导致循环依赖。
    /// </remarks>
    /// <param name="services">DI 容器。</param>
    /// <param name="fallbackEngine">降级引擎（未激活时使用，通常为 DeterministicBatchInferenceEngine）。
    /// 必须实现 <see cref="IFallbackInferenceEngine"/> 标记接口。</param>
    /// <returns>DI 容器（链式调用）。</returns>
    public static IServiceCollection AddModelActivationManager(
        this IServiceCollection services,
        IFallbackInferenceEngine fallbackEngine)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(fallbackEngine);

        // 子问题1：注册 fallback 引擎为 IFallbackInferenceEngine（而非 IBatchInferenceEngine）。
        // 这样 ModelActivationManager 构造函数注入 IFallbackInferenceEngine 时，
        // DI 容器解析到的是 fallback 实例，而非 ModelActivationManager 自身（避免循环依赖）。
        services.AddSingleton(fallbackEngine);

        // 默认注册 OnnxRuntimeInferenceSessionFactory；调用方可覆盖
        services.TryAddSingleton<IOnnxInferenceSessionFactory, OnnxRuntimeInferenceSessionFactory>();

        // 注册 ModelActivationManager 为 IModelActivationManager 和 IBatchInferenceEngine
        // 子问题1：ModelActivationManager 构造函数注入 IFallbackInferenceEngine（fallback），
        // 消费方通过 IBatchInferenceEngine 获取 ModelActivationManager 代理。
        services.AddSingleton<ModelActivationManager>();
        services.AddSingleton<IModelActivationManager>(sp => sp.GetRequiredService<ModelActivationManager>());
        services.AddSingleton<IBatchInferenceEngine>(sp => sp.GetRequiredService<ModelActivationManager>());

        return services;
    }

    /// <summary>
    /// 在已注册的 <see cref="IBatchInferenceEngine"/> 之上叠加 <see cref="InferenceScheduler"/>，
    /// 提供有界队列、最大并发治理与动态批处理。
    /// </summary>
    /// <remarks>
    /// <b>调用顺序约束</b>：必须在 <see cref="AddOnnxInferenceEngine(OnnxInferenceEngineOptions, string?, ModelArtifactDescriptor?)"/>
    /// 或 <see cref="AddModelActivationManager"/> 之后调用，确保 IBatchInferenceEngine 已注册。
    /// 本方法会把 IBatchInferenceEngine 的注册替换为 InferenceScheduler 包裹的版本，
    /// 内部引擎通过 <c>GetService&lt;IBatchInferenceEngine&gt;()</c> 在首次解析时获取。
    /// <para>
    /// <b>默认行为</b>：当 <see cref="InferenceSchedulerOptions.EnableDynamicBatching"/>=false（默认）
    /// 时，InferenceScheduler 直接转发请求到内部引擎，行为与未引入本方法完全一致。
    /// 仅在显式设置 EnableDynamicBatching=true 时才走 channel + 微批路径。
    /// </para>
    /// <para>
    /// <b>启用前评估</b>：动态批处理在低 QPS 单条请求场景下只会增加 BatchWaitWindow 量级延迟
    /// 而不增加吞吐。建议先通过真实 profile 验证 QPS ≥ 100 且单次推理耗时 ≥ 1ms 后再开启。
    /// </para>
    /// </remarks>
    /// <param name="services">DI 容器。</param>
    /// <param name="configure">调度器配置回调。</param>
    /// <returns>DI 容器（链式调用）。</returns>
    public static IServiceCollection UseInferenceScheduler(
        this IServiceCollection services,
        Action<InferenceSchedulerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new InferenceSchedulerOptions();
        configure(options);
        services.AddSingleton(options);

        // 把已有的 IBatchInferenceEngine 注册移到内部位（InnerBatchInferenceEngine），
        // 让 InferenceScheduler 通过 InnerBatchInferenceEngine 获取内部引擎，
        // 避免与 InferenceScheduler 自身注册为 IBatchInferenceEngine 冲突导致循环解析。
        var existingRegistration = services.FirstOrDefault(
            d => d.ServiceType == typeof(IBatchInferenceEngine));
        if (existingRegistration is null)
        {
            throw new InvalidOperationException(
                "UseInferenceScheduler 必须在 AddOnnxInferenceEngine / AddModelActivationManager 之后调用：" +
                "DI 容器中尚未注册 IBatchInferenceEngine，InferenceScheduler 没有可包裹的内部引擎。");
        }

        services.Remove(existingRegistration);
        services.AddSingleton<InnerBatchInferenceEngine>(
            sp => new InnerBatchInferenceEngine(BuildInnerEngine(sp, existingRegistration)));
        services.AddSingleton<InferenceScheduler>(sp =>
        {
            var inner = sp.GetRequiredService<InnerBatchInferenceEngine>();
            var opts = sp.GetRequiredService<InferenceSchedulerOptions>();
            return new InferenceScheduler(inner.Engine, opts);
        });
        services.AddSingleton<IBatchInferenceEngine>(sp => sp.GetRequiredService<InferenceScheduler>());

        return services;
    }

    /// <summary>
    /// 从原始注册描述符解析内部 IBatchInferenceEngine 实例。
    /// 支持 Instance / Factory / Object 三种注册方式。
    /// </summary>
    private static IBatchInferenceEngine BuildInnerEngine(
        IServiceProvider sp,
        ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IBatchInferenceEngine instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is { } factory)
        {
            return (IBatchInferenceEngine)factory(sp);
        }

        if (descriptor.ImplementationType is { } implType)
        {
            return (IBatchInferenceEngine)sp.GetRequiredService(implType);
        }

        throw new InvalidOperationException(
            "无法解析原始 IBatchInferenceEngine 注册（既非 Instance 也非 Factory 也非 Type）。");
    }
}

/// <summary>
/// 内部 IBatchInferenceEngine 包装器：用于在 DI 容器中把原始引擎注册"隐藏"起来，
/// 让 InferenceScheduler 通过本类型获取内部引擎，避免与自身注册为 IBatchInferenceEngine 冲突。
/// </summary>
internal sealed class InnerBatchInferenceEngine
{
    public InnerBatchInferenceEngine(IBatchInferenceEngine engine)
    {
        Engine = engine;
    }

    public IBatchInferenceEngine Engine { get; }
}
