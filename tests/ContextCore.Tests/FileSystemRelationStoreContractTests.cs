using ContextCore.Abstractions;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;

namespace ContextCore.Tests;

/// <summary>GRAPH-10：FileSystem provider 的 RelationStore contract 测试。</summary>
/// <remarks>
/// 每个测试创建独立的临时根目录，避免 JSONL 文件跨测试干扰；类清理时删除整个目录。
/// </remarks>
[TestClass]
[TestCategory("Storage")]
[TestCategory("Graph")]
public sealed class FileSystemRelationStoreContractTests : RelationStoreContractBase
{
    private string? _rootPath;

    protected override Task<IRelationStore> CreateStoreAsync(CancellationToken cancellationToken)
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "cc-graph10-fs-" + Guid.NewGuid().ToString("N"));
        var options = new FileStorageOptions { RootPath = _rootPath };
        IRelationStore store = new FileRelationStore(options);
        return Task.FromResult(store);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_rootPath is not null && Directory.Exists(_rootPath))
        {
            try { Directory.Delete(_rootPath, recursive: true); } catch { /* best-effort */ }
        }
    }
}
