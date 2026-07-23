using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.ModelExecution;

// ===========================================================================
// R28-D：Platt Calibration Service
//
// 目标：
//   提供 ICalibrationService 的默认 Platt scaling 实现，把任意 raw score
//   映射为 [0, 1] 概率：calibrated = 1 / (1 + exp(-(A * raw + B)))。
//
// 设计原则：
//   1. 默认 A=1, B=0：未配置参数时为恒等变换的 logistic 形式，输入 score
//      经 sigmoid 后落入 [0, 1]，但语义上视为 identity。
//   2. 每模型独立参数：通过 RegisterParameters 注册 (modelName, parameters)。
//      null modelName 表示全局默认参数。
//   3. 查找策略：先按 modelName 精确查找，未命中时回退到全局默认。
//   4. 线程安全：ConcurrentDictionary 保护参数注册与读取。
//   5. 数值稳定：sigmoid 通过 Math.Exp 计算，对极值（raw > 35）饱和到 1.0
//      或 0.0，不抛 OverflowException。
// ===========================================================================

/// <summary>
/// R28-D：Platt scaling 校准服务默认实现。
/// </summary>
/// <remarks>
/// 公式：calibrated = 1 / (1 + exp(-(A * raw + B)))；默认 A=1, B=0。
/// 生产部署可替换为基于持久化参数表的实现；契约不变。
/// </remarks>
public sealed class PlattCalibrationService : ICalibrationService
{
    // 默认 Platt 参数：A=1, B=0（identity 的 sigmoid 形式）。
    // B 嵌入到 Parameter 字段中（Platt 仅暴露 A；B 固定为 0）。
    internal const double DefaultA = 1.0;
    internal const double DefaultB = 0.0;
    private const string DefaultModelKey = "__default__";

    // 主键：modelName（null 视为 DefaultModelKey）。
    private readonly ConcurrentDictionary<string, (double A, double B, CalibrationParameters Parameters)> _parameters = new(StringComparer.Ordinal);

    /// <summary>
    /// 构造默认 Platt 校准服务，注册全局默认参数（A=1, B=0）。
    /// </summary>
    public PlattCalibrationService()
    {
        var now = DateTimeOffset.UtcNow;
        var defaultParams = new CalibrationParameters
        {
            Method = "platt",
            Parameter = DefaultA,
            FittedAt = now
        };
        _parameters[DefaultModelKey] = (DefaultA, DefaultB, defaultParams);
    }

    /// <inheritdoc />
    public double Calibrate(double rawScore, string? modelName = null)
    {
        var (a, b, _) = ResolveParameters(modelName);
        return Sigmoid(a * rawScore + b);
    }

    /// <inheritdoc />
    public IReadOnlyList<double> CalibrateBatch(IReadOnlyList<double> rawScores, string? modelName = null)
    {
        ArgumentNullException.ThrowIfNull(rawScores);

        if (rawScores.Count == 0)
        {
            return Array.Empty<double>();
        }

        var (a, b, _) = ResolveParameters(modelName);
        var result = new double[rawScores.Count];
        for (var i = 0; i < rawScores.Count; i++)
        {
            result[i] = Sigmoid(a * rawScores[i] + b);
        }
        return result;
    }

    /// <inheritdoc />
    public CalibrationParameters? GetParameters(string? modelName = null)
    {
        var key = NormalizeKey(modelName);
        return _parameters.TryGetValue(key, out var entry) ? entry.Parameters : null;
    }

    /// <summary>
    /// 注册指定模型的校准参数。null modelName 表示更新全局默认。
    /// 已存在同名模型参数时覆盖（与 FeatureRegistry 不同——校准参数允许 refit）。
    /// </summary>
    public void RegisterParameters(double a, double b, string? modelName = null, DateTimeOffset? fittedAt = null)
    {
        var key = NormalizeKey(modelName);
        var parameters = new CalibrationParameters
        {
            Method = "platt",
            // Parameter 字段只暴露 A（与契约约定一致）；B 通过内部元组保留。
            Parameter = a,
            FittedAt = fittedAt ?? DateTimeOffset.UtcNow
        };
        _parameters[key] = (a, b, parameters);
    }

    private (double A, double B, CalibrationParameters Parameters) ResolveParameters(string? modelName)
    {
        var key = NormalizeKey(modelName);
        // 先按精确模型名查找；未命中时回退到全局默认（DefaultModelKey 始终存在）。
        return _parameters.TryGetValue(key, out var entry)
            ? entry
            : _parameters[DefaultModelKey];
    }

    private static string NormalizeKey(string? modelName)
        => string.IsNullOrEmpty(modelName) ? DefaultModelKey : modelName!;

    /// <summary>
    /// 数值稳定的 sigmoid：1 / (1 + exp(-x))。
    /// 对 |x| > 35 直接饱和，避免 Math.Exp 溢出。
    /// </summary>
    private static double Sigmoid(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        if (x >= 35.0) return 1.0;
        if (x <= -35.0) return 0.0;
        return 1.0 / (1.0 + Math.Exp(-x));
    }
}
