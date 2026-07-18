using ContextCore.Abstractions;

namespace ContextCore.Service.Infrastructure;

/// <summary>
/// R13.3 #1：IStoreRuntimeCapabilities 实现——按 StorageProviderKind 返回对应的 StorageExecutionProfile。
/// 通过 DI 注入到需要查询存储能力的组件，替代各处对 "filesystem"/"postgres"/"memory" 字符串的判断。
/// </summary>
internal sealed class StoreRuntimeCapabilities : IStoreRuntimeCapabilities
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
