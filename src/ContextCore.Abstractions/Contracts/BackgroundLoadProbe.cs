namespace ContextCore.Abstractions;

/// <summary>
/// 后台负载探针：为 <see cref="Core.Services.BackgroundDrainBudget"/> 提供动态降速信号。
/// 当前信号：DB 连接池利用率（0-1）；Queue Lag / Worker Age 为后续演进维度。
/// </summary>
public interface IBackgroundLoadProbe
{
    /// <summary>
    /// 当前 DB 连接池利用率（0-1；null = 无信号——预算回退到静态值）。
    /// 实现应廉价（无 DB 往返，仅读池统计）。
    /// </summary>
    double? GetDbPoolUtilization();
}
