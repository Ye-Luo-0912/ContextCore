using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.ModelExecution;

// ===========================================================================
// R28-D / R28-F P3-3：Platt Calibration Service
//
// 目标：
//   提供 ICalibrationService 的默认 Platt scaling 实现，把任意 raw score
//   映射为 [0, 1] 概率：calibrated = 1 / (1 + exp(-(A * raw + B)))。
//
// R28-F P3-3 重构：
//   - 旧版默认 A=1, B=0 被称为 "identity-like"，但实际执行 sigmoid，导致 raw=0 → 0.5 而非 0。
//   - 现在默认参数 Kind=Identity，Calibrate 直接返回 raw（真正恒等）。
//   - 调用 RegisterPlattParameters / RegisterTemperatureParameters / RegisterIsotonicParameters
//     可切换到对应策略。
//   - CalibrationParameters 完整暴露 A / B / T / IsotonicPoints；旧 Parameter 字段同步 A。
//
// 设计原则：
//   1. 默认 Identity：未配置参数时 calibrated = raw（不调用 Math.Exp）。
//   2. 每模型独立参数：通过 RegisterXxxParameters 注册 (modelName, parameters)。
//      null modelName 表示全局默认参数。
//   3. 查找策略：先按 modelName 精确查找，未命中时回退到全局默认。
//   4. 线程安全：ConcurrentDictionary 保护参数注册与读取。
//   5. 数值稳定：sigmoid 通过 Math.Exp 计算，对极值（raw > 35）饱和到 1.0
//      或 0.0，不抛 OverflowException。
//   6. 向后兼容：RegisterParameters(a, b, modelName, fittedAt) 保留，等价 RegisterPlattParameters。
//      Calibrate/CalibrateBatch 旧调用方无需修改；默认行为从 sigmoid(raw) 改为 raw。
// ===========================================================================

/// <summary>
/// R28-D / R28-F P3-3：默认校准服务（支持 Identity/Platt/Temperature/Isotonic）。
/// </summary>
/// <remarks>
/// 默认 Identity：未配置参数时 calibrated = rawScore。
/// 通过 RegisterXxxParameters 切换到对应策略。
/// </remarks>
public sealed class PlattCalibrationService : ICalibrationService
{
    internal const double DefaultA = 1.0;
    internal const double DefaultB = 0.0;
    private const string DefaultModelKey = "__default__";

    private readonly ConcurrentDictionary<string, CalibrationParameters> _parameters = new(StringComparer.Ordinal);
    private readonly IdentityCalibration _identity = new();
    private readonly PlattCalibration _platt = new();
    private readonly TemperatureCalibration _temperature = new();
    private readonly IsotonicCalibration _isotonic = new();

    /// <summary>
    /// 构造默认校准服务，注册全局默认 Identity 参数。
    /// </summary>
    public PlattCalibrationService()
    {
        var now = DateTimeOffset.UtcNow;
        var defaultParams = new CalibrationParameters
        {
            Method = "identity",
            Kind = CalibrationMethodKind.Identity,
            ParameterA = DefaultA,
            ParameterB = DefaultB,
            Parameter = DefaultA, // 兼容别名
            FittedAt = now
        };
        _parameters[DefaultModelKey] = defaultParams;
    }

    /// <inheritdoc />
    public double Calibrate(double rawScore, string? modelName = null)
    {
        var parameters = ResolveParameters(modelName);
        return CalibrateWithStrategy(rawScore, parameters);
    }

    /// <inheritdoc />
    public IReadOnlyList<double> CalibrateBatch(IReadOnlyList<double> rawScores, string? modelName = null)
    {
        ArgumentNullException.ThrowIfNull(rawScores);

        if (rawScores.Count == 0)
        {
            return Array.Empty<double>();
        }

        var parameters = ResolveParameters(modelName);
        var result = new double[rawScores.Count];
        for (var i = 0; i < rawScores.Count; i++)
        {
            result[i] = CalibrateWithStrategy(rawScores[i], parameters);
        }
        return result;
    }

    /// <inheritdoc />
    public CalibrationParameters? GetParameters(string? modelName = null)
    {
        var key = NormalizeKey(modelName);
        return _parameters.TryGetValue(key, out var entry) ? entry : null;
    }

    /// <summary>
    /// R28-F P3-3：注册 Platt (A, B) 参数。null modelName 表示更新全局默认。
    /// 已存在同名模型参数时覆盖（与 FeatureRegistry 不同——校准参数允许 refit）。
    /// </summary>
    public void RegisterPlattParameters(double a, double b, string? modelName = null, DateTimeOffset? fittedAt = null)
    {
        var key = NormalizeKey(modelName);
        var parameters = new CalibrationParameters
        {
            Method = "platt",
            Kind = CalibrationMethodKind.Platt,
            ParameterA = a,
            ParameterB = b,
            Parameter = a, // 兼容别名
            FittedAt = fittedAt ?? DateTimeOffset.UtcNow
        };
        _parameters[key] = parameters;
    }

    /// <summary>
    /// R28-F P3-3：注册 Temperature T 参数。T 必须 > 0。
    /// </summary>
    public void RegisterTemperatureParameters(double t, string? modelName = null, DateTimeOffset? fittedAt = null)
    {
        if (t <= 0.0 || double.IsNaN(t) || double.IsInfinity(t))
        {
            throw new ArgumentException($"Temperature 必须 > 0 且有限；实际为 {t}。", nameof(t));
        }
        var key = NormalizeKey(modelName);
        var parameters = new CalibrationParameters
        {
            Method = "temperature",
            Kind = CalibrationMethodKind.Temperature,
            Temperature = t,
            Parameter = t, // 兼容别名（旧字段语义不严格，存 T 以保留信息）
            FittedAt = fittedAt ?? DateTimeOffset.UtcNow
        };
        _parameters[key] = parameters;
    }

    /// <summary>
    /// R28-F P3-3：注册 Isotonic 回归点。points 必须按 Input 升序，否则抛 ArgumentException。
    /// </summary>
    public void RegisterIsotonicParameters(IReadOnlyList<IsotonicPoint> points, string? modelName = null, DateTimeOffset? fittedAt = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count > 1)
        {
            for (var i = 1; i < points.Count; i++)
            {
                if (points[i].Input < points[i - 1].Input)
                {
                    throw new ArgumentException(
                        $"IsotonicPoints 必须按 Input 升序；第 {i} 项 Input={points[i].Input} < 前项 {points[i - 1].Input}。",
                        nameof(points));
                }
            }
        }
        var key = NormalizeKey(modelName);
        var parameters = new CalibrationParameters
        {
            Method = "isotonic",
            Kind = CalibrationMethodKind.Isotonic,
            IsotonicPoints = points,
            FittedAt = fittedAt ?? DateTimeOffset.UtcNow
        };
        _parameters[key] = parameters;
    }

    /// <summary>
    /// 注册 Identity 参数（重置某模型为恒等变换）。
    /// </summary>
    public void RegisterIdentityParameters(string? modelName = null, DateTimeOffset? fittedAt = null)
    {
        var key = NormalizeKey(modelName);
        var parameters = new CalibrationParameters
        {
            Method = "identity",
            Kind = CalibrationMethodKind.Identity,
            FittedAt = fittedAt ?? DateTimeOffset.UtcNow
        };
        _parameters[key] = parameters;
    }

    /// <summary>
    /// R28-D 兼容入口：等价于 RegisterPlattParameters(a, b, modelName, fittedAt)。
    /// </summary>
    public void RegisterParameters(double a, double b, string? modelName = null, DateTimeOffset? fittedAt = null)
        => RegisterPlattParameters(a, b, modelName, fittedAt);

    private CalibrationParameters ResolveParameters(string? modelName)
    {
        var key = NormalizeKey(modelName);
        return _parameters.TryGetValue(key, out var entry)
            ? entry
            : _parameters[DefaultModelKey];
    }

    private double CalibrateWithStrategy(double rawScore, CalibrationParameters parameters)
    {
        return parameters.Kind switch
        {
            CalibrationMethodKind.Identity => _identity.Calibrate(rawScore, parameters),
            CalibrationMethodKind.Platt => _platt.Calibrate(rawScore, parameters),
            CalibrationMethodKind.Temperature => _temperature.Calibrate(rawScore, parameters),
            CalibrationMethodKind.Isotonic => _isotonic.Calibrate(rawScore, parameters),
            _ => rawScore // 防御性 fallback
        };
    }

    private static string NormalizeKey(string? modelName)
        => string.IsNullOrEmpty(modelName) ? DefaultModelKey : modelName!;
}
