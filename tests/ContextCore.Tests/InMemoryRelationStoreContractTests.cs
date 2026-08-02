using ContextCore.Abstractions;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>InMemory provider 的 RelationStore contract 测试。</summary>
[TestClass]
[TestCategory("Storage")]
[TestCategory("Graph")]
public sealed class InMemoryRelationStoreContractTests : RelationStoreContractBase
{
    protected override Task<IRelationStore> CreateStoreAsync(CancellationToken cancellationToken)
    {
        IRelationStore store = new InMemoryRelationStore();
        return Task.FromResult(store);
    }
}
