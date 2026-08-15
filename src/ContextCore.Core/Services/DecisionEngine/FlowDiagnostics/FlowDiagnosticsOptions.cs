using System.Security.Cryptography;
using System.Text;

namespace ContextCore.Core.Services.DecisionEngine.FlowDiagnostics;

/// <summary>
/// 候选流诊断开关与采样。默认关闭；由环境变量启用。
/// 采样按请求 ID 稳定哈希决定：同一请求 ID 重复执行采样结论一致。
/// </summary>
public sealed class FlowDiagnosticsOptions
{
    /// <summary>诊断总开关（默认关闭，透传）。</summary>
    public bool Enabled { get; init; }

    /// <summary>采样率 [0,1]（默认 0.1）。</summary>
    public double SampleRate { get; init; } = 0.1;

    /// <summary>诊断输出目录（默认 artifacts/flow-diagnostics/）。</summary>
    public string OutputDirectory { get; init; } = "artifacts/flow-diagnostics";

    /// <summary>从环境变量读取：CC_FLOW_DIAGNOSTICS=1、CC_FLOW_DIAGNOSTICS_SAMPLE_RATE=0.05、CC_FLOW_DIAGNOSTICS_OUT=路径。</summary>
    public static FlowDiagnosticsOptions FromEnvironment()
    {
        var enabled = Environment.GetEnvironmentVariable("CC_FLOW_DIAGNOSTICS") == "1";
        var sampleRate = 0.1;
        var rawRate = Environment.GetEnvironmentVariable("CC_FLOW_DIAGNOSTICS_SAMPLE_RATE");
        if (double.TryParse(rawRate, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            sampleRate = Math.Clamp(parsed, 0, 1);
        }
        var output = Environment.GetEnvironmentVariable("CC_FLOW_DIAGNOSTICS_OUT");
        return new FlowDiagnosticsOptions
        {
            Enabled = enabled,
            SampleRate = sampleRate,
            OutputDirectory = string.IsNullOrWhiteSpace(output) ? "artifacts/flow-diagnostics" : output
        };
    }

    /// <summary>是否采样该请求（稳定哈希，不依赖执行顺序）。</summary>
    public bool ShouldSample(string requestId)
    {
        if (!Enabled || SampleRate <= 0)
        {
            return false;
        }
        if (SampleRate >= 1)
        {
            return true;
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(requestId ?? string.Empty));
        var bucket = BitConverter.ToUInt64(hash, 0) % 1000;
        return bucket < (ulong)(SampleRate * 1000);
    }
}
