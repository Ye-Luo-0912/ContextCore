using ContextCore.Abstractions;

namespace ContextCore.Runtime;

/// <summary>
/// IStoreRuntimeCapabilities 默认实现——按 StorageProviderKind 返回对应的 StorageExecutionProfile。
/// 由各宿主（Service DI / ControlRoom 直构 / Eval）注入到 RuntimeBuildOptions，
/// 替代各处对 "filesystem"/"postgres"/"memory" 字符串的判断。
/// </summary>
public sealed class StoreRuntimeCapabilities : IStoreRuntimeCapabilities
{
    public StoreRuntimeCapabilities(StorageProviderKind providerKind)
    {
        Profile = providerKind switch
        {
            StorageProviderKind.InMemory => StorageExecutionProfile.InMemory,
            StorageProviderKind.FileSystem => StorageExecutionProfile.FileSystem,
            StorageProviderKind.Postgres => StorageExecutionProfile.Postgres,
            _ => StorageExecutionProfile.InMemory // 未知时退回最保守的 InMemory
        };
    }

    public StorageExecutionProfile Profile { get; }
}
