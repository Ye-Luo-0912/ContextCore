using ContextCore.Abstractions;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Tests;

/// <summary>覆盖 PostgreSQL 存储后端的迁移 SQL、序列化和 DI 注册。</summary>
[TestClass]
public sealed class ContextCorePostgresStorageTests
{
    [TestMethod]
    public void PostgresMigrationSql_ShouldCreateMetadataAndPgVectorTables()
    {
        var sql = PostgresMigrationRunner.BuildMigrationSql(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=contextcore;Username=contextcore;Password=contextcore",
            TablePrefix = "cc_",
            EnablePgVectorExtension = true
        });

        StringAssert.Contains(sql, "CREATE EXTENSION IF NOT EXISTS vector");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_collections");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_context_items");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_memory_items");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_relations");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_vectors");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_retrieval_traces");
        StringAssert.Contains(sql, "embedding vector NOT NULL");
        StringAssert.Contains(sql, "data jsonb NOT NULL");
    }

    [TestMethod]
    public void PostgresVectorFormat_ShouldRenderInvariantPgVectorLiteral()
    {
        var literal = PostgresVectorFormat.ToVectorLiteral([1f, -0.25f, 3.5f]);

        Assert.AreEqual("[1,-0.25,3.5]", literal);
    }

    [TestMethod]
    public void PostgresJsonSerializer_ShouldRoundtripChineseContextItem()
    {
        var serializer = new PostgresJsonSerializer();
        var item = new ContextItem
        {
            Id = "item-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "note",
            Title = "中文标题",
            Content = "PostgreSQL jsonb 应完整保存中文上下文。",
            Tags = ["中文", "postgres"],
            Metadata = new Dictionary<string, string>
            {
                ["来源"] = "单元测试"
            },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var json = serializer.Serialize(item);
        var roundtrip = serializer.Deserialize<ContextItem>(json);

        Assert.AreEqual(item.Title, roundtrip.Title);
        Assert.AreEqual(item.Content, roundtrip.Content);
        CollectionAssert.AreEqual(item.Tags.ToArray(), roundtrip.Tags.ToArray());
        Assert.AreEqual("单元测试", roundtrip.Metadata["来源"]);
    }

    [TestMethod]
    public void PostgresServiceCollectionExtensions_ShouldRegisterStorageContracts()
    {
        var services = new ServiceCollection();
        services.AddContextCorePostgresStorage(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=contextcore;Username=contextcore;Password=contextcore",
            AutoMigrate = false
        });

        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IContextStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IContextCollectionStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IMemoryStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IRelationStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IVectorStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IRetrievalTraceStore)));
    }
}