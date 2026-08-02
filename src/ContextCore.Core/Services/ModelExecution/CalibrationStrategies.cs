using ContextCore.Abstractions;

namespace ContextCore.Core.Services.ModelExecution;

// ===========================================================================
// Calibration 策略族
//
// 目标：把校准从单一 PlattCalibrationService（默认 A=1 B=0 但实际执行 sigmoid，
// 导致 raw=0 → 0.5 而非 0）重构为显式的多策略：
//   - IdentityCalibration        —— calibrated = raw（真正的恒等变换）
//   - PlattCalibration            —— calibrated = sigmoid(A*raw + B)
//   - TemperatureCalibration      —— calibrated = sigmoid(raw / T)
//   - IsotonicCalibration         —— 分段线性插值（points 必须按 Input 升序）
//
// 设计原则：
//   1. 每种策略独立类型，便于单元测试与审计。
//   2. ICalibrationStrategy 路由：DefaultCalibrationService 据 Kind 选择实现。
//   3. 数值稳定：sigmoid 对 |x| > 35 饱和；插值外推用边界值。
//   4. IdentityCalibration 不调用 Math.Exp，零开销。
// ===========================================================================

/// <summary>
/// 校准策略抽象。每种校准方法对应一个实现。
/// </summary>
public interface ICalibrationStrategy
{
    /// <summary>策略类型。</summary>
    CalibrationMethodKind Kind { get; }

    /// <summary>应用校准。</summary>
    /// <param name="rawScore">原始模型分数。</param>
    /// <param name="parameters">校准参数（与 Kind 对齐）。</param>
    /// <returns>校准后分数。</returns>
    double Calibrate(double rawScore, CalibrationParameters parameters);
}

/// <summary>
/// 恒等校准。
/// calibrated = rawScore；不改变输入。
/// 替代旧版 PlattCalibrationService 默认参数（A=1, B=0）会执行 sigmoid 导致 raw=0 → 0.5 的问题。
/// </summary>
public sealed class IdentityCalibration : ICalibrationStrategy
{
    /// <inheritdoc />
    public CalibrationMethodKind Kind => CalibrationMethodKind.Identity;

    /// <inheritdoc />
    public double Calibrate(double rawScore, CalibrationParameters parameters)
        => rawScore;
}

/// <summary>
/// Platt scaling。
/// calibrated = sigmoid(A * raw + B)；A 通常为正斜率，B 为偏置。
/// 与原 PlattCalibrationService 公式一致；区别在于 B 现在显式暴露在 CalibrationParameters 中。
/// </summary>
public sealed class PlattCalibration : ICalibrationStrategy
{
    /// <inheritdoc />
    public CalibrationMethodKind Kind => CalibrationMethodKind.Platt;

    /// <inheritdoc />
    public double Calibrate(double rawScore, CalibrationParameters parameters)
    {
        if (parameters.Kind != CalibrationMethodKind.Platt)
        {
            throw new ArgumentException(
                $"PlattCalibration 收到不匹配的 Kind={parameters.Kind}；期望 Platt。", nameof(parameters));
        }
        return Sigmoid(parameters.ParameterA * rawScore + parameters.ParameterB);
    }

    /// <summary>数值稳定的 sigmoid：1 / (1 + exp(-x))；|x| > 35 饱和。</summary>
    internal static double Sigmoid(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        if (x >= 35.0) return 1.0;
        if (x <= -35.0) return 0.0;
        return 1.0 / (1.0 + Math.Exp(-x));
    }
}

/// <summary>
/// Temperature scaling。
/// calibrated = sigmoid(raw / T)；T > 0。
/// 常用于多分类 logits 软化；T=1 等价 sigmoid(raw)。
/// </summary>
public sealed class TemperatureCalibration : ICalibrationStrategy
{
    /// <inheritdoc />
    public CalibrationMethodKind Kind => CalibrationMethodKind.Temperature;

    /// <inheritdoc />
    public double Calibrate(double rawScore, CalibrationParameters parameters)
    {
        if (parameters.Kind != CalibrationMethodKind.Temperature)
        {
            throw new ArgumentException(
                $"TemperatureCalibration 收到不匹配的 Kind={parameters.Kind}；期望 Temperature。", nameof(parameters));
        }
        var t = parameters.Temperature;
        if (t <= 0.0 || double.IsNaN(t) || double.IsInfinity(t))
        {
            throw new ArgumentException(
                $"Temperature 必须 > 0 且有限；实际为 {t}。", nameof(parameters));
        }
        return PlattCalibration.Sigmoid(rawScore / t);
    }
}

/// <summary>
/// Isotonic regression（分段线性插值）。
/// points 必须按 Input 升序；超出范围的输入 clamp 到边界输出。
/// 点数 < 2 时退化为 identity（无法插值）。
/// </summary>
public sealed class IsotonicCalibration : ICalibrationStrategy
{
    /// <inheritdoc />
    public CalibrationMethodKind Kind => CalibrationMethodKind.Isotonic;

    /// <inheritdoc />
    public double Calibrate(double rawScore, CalibrationParameters parameters)
    {
        if (parameters.Kind != CalibrationMethodKind.Isotonic)
        {
            throw new ArgumentException(
                $"IsotonicCalibration 收到不匹配的 Kind={parameters.Kind}；期望 Isotonic。", nameof(parameters));
        }

        var points = parameters.IsotonicPoints;
        if (points is null || points.Count < 2)
        {
            // 点数不足 → identity
            return rawScore;
        }

        // 边界 clamp（假设 points 按 Input 升序；构造时校验）
        if (rawScore <= points[0].Input) return points[0].Output;
        if (rawScore >= points[^1].Input) return points[^1].Output;

        // 二分查找区间
        var lo = 0;
        var hi = points.Count - 1;
        while (lo + 1 < hi)
        {
            var mid = (lo + hi) >> 1;
            if (points[mid].Input <= rawScore) lo = mid;
            else hi = mid;
        }

        var x0 = points[lo].Input;
        var x1 = points[hi].Input;
        var y0 = points[lo].Output;
        var y1 = points[hi].Output;
        var dx = x1 - x0;
        if (Math.Abs(dx) < 1e-12) return y0; // 防止除零
        return y0 + (y1 - y0) * (rawScore - x0) / dx;
    }
}
