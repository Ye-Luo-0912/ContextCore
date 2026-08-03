namespace ContextCore.Service.Infrastructure;

/// <summary>
/// 生产准入控制器配置。
/// 配置节：<c>ProductionAdmission</c>（如 <c>ProductionAdmission:ProbeInterval=00:00:05</c>）。
/// </summary>
public sealed class ProductionAdmissionOptions
{
    /// <summary>
    /// 实时探针轮询间隔（TTL）。两次全量校验之间的最短间隔，默认 5 秒。
    /// TTL 内请求直接复用缓存报告；到期后下一次请求触发全量刷新（含静态强制项 + 实时探针）。
    /// 设为 <see cref="TimeSpan.Zero"/> 时每次请求都强制执行全量校验（调试/测试用）。
    /// </summary>
    public TimeSpan ProbeInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 单个实时探针超时，默认 3 秒。
    /// 超过该时间的探针（Postgres Ping / Model Slot 查询）按失败处理并阻断准入。
    /// </summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(3);
}
