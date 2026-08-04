using ContextCore.Inference.Onnx;
using Microsoft.Extensions.Configuration;

namespace ContextCore.Service.Infrastructure;

/// <summary>
/// 从 IConfiguration 解析 ONNX 推理默认配置（模型工件相关键，ModelArtifact:* 前缀）。
/// 集中维护默认 tensor 名与 Execution Provider / GPU 设备配置的读取，
/// 供模型控制面端点与 ModelStateReconcilerWorker 共用，避免散落的硬编码字面量。
/// </summary>
internal static class ModelArtifactOptionsReader
{
    /// <summary>解析默认输入张量名（ModelArtifact:DefaultInputTensorName，缺省 "input"）。</summary>
    public static string ResolveInputTensorName(IConfiguration configuration)
    {
        var value = configuration["ModelArtifact:DefaultInputTensorName"];
        return string.IsNullOrWhiteSpace(value) ? "input" : value;
    }

    /// <summary>解析默认主分数输出张量名（ModelArtifact:DefaultScoreOutputName，缺省 "score"）。</summary>
    public static string ResolveScoreOutputName(IConfiguration configuration)
    {
        var value = configuration["ModelArtifact:DefaultScoreOutputName"];
        return string.IsNullOrWhiteSpace(value) ? "score" : value;
    }

    /// <summary>
    /// 解析 Execution Provider（ModelArtifact:ExecutionProvider，缺省 CPU）。
    /// 未知配置值回退 CPU（fail-safe：错误配置不得导致模型激活失败）。
    /// </summary>
    public static OnnxExecutionProvider ResolveExecutionProvider(IConfiguration configuration)
    {
        var value = configuration["ModelArtifact:ExecutionProvider"];
        return Enum.TryParse<OnnxExecutionProvider>(value, ignoreCase: true, out var provider)
            ? provider
            : OnnxExecutionProvider.CPU;
    }

    /// <summary>解析 GPU 设备 ID（ModelArtifact:ExecutionProviderDeviceId，缺省 0；非法值回退 0）。</summary>
    public static int ResolveExecutionProviderDeviceId(IConfiguration configuration)
    {
        var value = configuration["ModelArtifact:ExecutionProviderDeviceId"];
        return int.TryParse(value, out var deviceId) && deviceId >= 0 ? deviceId : 0;
    }
}
