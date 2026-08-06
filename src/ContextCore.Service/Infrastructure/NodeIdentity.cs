namespace ContextCore.Service.Infrastructure;

/// <summary>
/// 节点身份解析：NodeGroupId（节点组/部署单元）与 InstanceId（组内具体进程实例）。
/// 支持环境变量覆盖（容器化 / 多实例部署下机器名不唯一或不可靠）：
/// CONTEXTCORE_NODE_ID → NodeGroupId；CONTEXTCORE_INSTANCE_ID → InstanceId。
/// 成员资格与已应用状态均以 (NodeGroupId, InstanceId) 为键：同一节点组可驻留多个实例，
/// 各实例独立持有成员租约与已应用状态（修复"每节点仅一个活跃实例"的部署限制）。
/// 解析结果进程级缓存（懒加载）：同一进程内所有组件（Worker / 准入 / 端点）必须看到
/// 一致的实例身份——实例 ID 每次调用重新生成会导致"Worker 写入 applied state 的实例
/// 与准入校验的实例不一致"而误拒流量。
/// </summary>
public static class NodeIdentity
{
    private static readonly Lazy<string> NodeGroupId = new(
        () => Environment.GetEnvironmentVariable("CONTEXTCORE_NODE_ID")
              ?? Environment.MachineName,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<string> InstanceId = new(
        () => Environment.GetEnvironmentVariable("CONTEXTCORE_INSTANCE_ID")
              ?? Environment.MachineName + "-" + Guid.NewGuid().ToString("N")[..8],
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>节点组 Id：CONTEXTCORE_NODE_ID 环境变量覆盖，回退机器名（跨进程重启稳定）。</summary>
    public static string ResolveNodeGroupId() => NodeGroupId.Value;

    /// <summary>实例 Id：CONTEXTCORE_INSTANCE_ID 环境变量覆盖，回退 机器名-guid8（每进程唯一，进程内稳定）。</summary>
    public static string ResolveInstanceId() => InstanceId.Value;
}
