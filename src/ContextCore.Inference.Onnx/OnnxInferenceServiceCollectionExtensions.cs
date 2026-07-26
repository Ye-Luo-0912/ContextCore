using ContextCore.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Inference.Onnx;

// ===========================================================================
// R29 WP-A-2：OnnxInferenceServiceCollectionExtensions
//
// 目标：
//   为 ContextCore.Inference.Onnx 提供 DI 注册扩展，让 Service 层能在启动时
//   把 OnnxInferenceEngine 注入到 IBatchInferenceEngine 接口位。
//
// 设计边界：
//   1. 不在 DI 容器中直接创建 InferenceSession（需要在启动时加载模型文件，
//      可能涉及 I/O 与校验失败，不应阻塞 DI 容器构建）。
//      OnnxInferenceEngine 通过工厂模式在构造时延迟加载模型。
//   2. 支持两种注册方式：
//      a) 直接注册：使用调用方提供的 IOnnxInferenceSession（通常用于测试）。
//      b) 工厂注册：注入 IOnnxInferenceSessionFactory + OnnxInferenceEngineOptions +
//         可选 ModelArtifactDescriptor，由工厂在首次解析时创建 session。
//   3. IBatchInferenceEngine 绑定到 OnnxInferenceEngine；不替代 DeterministicBatchInferenceEngine
//      （后者仍作为 fallback 注入到 IPerformanceMonitor 等路径）。
// ===========================================================================

/// <summary>
/// R29 WP-A-2：注册 ContextCore ONNX 推理引擎。
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
}
