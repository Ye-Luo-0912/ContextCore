using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Testcontainers.PostgreSql;

namespace ContextCore.IntegrationTests;

/// <summary>
/// PostgreSQL + pgvector 端到端集成测试（Testcontainers）。
/// <para>
/// 测试环境：使用 <see cref="PostgreSqlContainer"/>（Docker）自动拉取 pgvector 镜像。
/// 若环境无 Docker（CI offline / Windows 无 Docker Desktop），测试将被自动跳过。
/// </para>
/// <para>
/// 覆盖范围：context ingest/query、memory promotion、relation build、
/// vector insert/search、constraint injection、job enqueue/dequeue、
/// package build trace、retrieval trace、migration apply。
/// </para>
/// </summary>
[TestClass]
public sealed class PostgresIntegrationTests
{
    // pgvector 官方镜像，含 pg 17 + vector 扩展
    private const string PgVectorImage = "pgvector/pgvector:pg17";

    private static PostgreSqlContainer? _container;
    private static string? _connectionString;

    // ── 容器生命周期 ────────────────────────────────────────────────────

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        // 若无法使用 Docker，跳过所有测试
        if (!await IsDockerAvailableAsync())
        {
            return;
        }

        _container = new PostgreSqlBuilder(PgVectorImage)
            .WithDatabase("cctest")
            .WithUsername("cctest")
            .WithPassword("cctest")
            .Build();

        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static async Task ClassCleanup()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    // ── 辅助工厂 ────────────────────────────────────────────────────────

    private static bool ShouldSkip => _connectionString is null;

    private static (PostgresConnectionFactory factory, PostgresMigrationRunner migrationRunner, PostgresJsonSerializer serializer) CreateInfrastructure(string prefix)
    {
        var options = new PostgresOptions
        {
            ConnectionString = _connectionString!,
            AutoMigrate = true,
            EnablePgVectorExtension = true,
            TablePrefix = prefix
        };
        var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        return (factory, migrationRunner, serializer);
    }

    // ── 测试方法 ─────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("Postgres")]
    public async Task Migration_ShouldCreateAllTablesIdempotently()
    {
        if (ShouldSkip) return;

        var (factory, migrationRunner, _) = CreateInfrastructure("mig_");
        try
        {
            // 首次迁移
            await migrationRunner.MigrateAsync();
            // 幂等：第二次迁移不应抛出
            await migrationRunner.MigrateAsync();

            var (success, error) = await factory.PingAsync();
            Assert.IsTrue(success, $"Ping 失败：{error}");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("Postgres")]
    public async Task ContextStore_SaveAndGet_ShouldRoundtrip()
    {
        if (ShouldSkip) return;

        var (factory, migrationRunner, serializer) = CreateInfrastructure("ctx_");
        try
        {
            var store = new PostgresContextStore(factory, serializer, migrationRunner);
            var now = DateTimeOffset.UtcNow;

            var item = new ContextItem
            {
                Id = "ctx-item-1",
                WorkspaceId = "ws-test",
                CollectionId = "col-test",
                Type = "note",
                Title = "集成测试条目",
                Content = "PostgreSQL 存储端到端测试内容。",
                ContentFormat = ContextContentFormat.PlainText,
                Tags = ["integration", "postgres"],
                SourceRefs = ["source:1"],
                Importance = 0.85,
                CreatedAt = now,
                UpdatedAt = now
            };

            await store.SaveAsync(item);
            var loaded = await store.GetAsync("ws-test", "col-test", "ctx-item-1");

            Assert.IsNotNull(loaded, "应能取回刚存入的条目");
            Assert.AreEqual("集成测试条目", loaded.Title);
            Assert.AreEqual(0.85, loaded.Importance, 1e-9);
            CollectionAssert.Contains(loaded.Tags.ToList(), "postgres");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("Postgres")]
    public async Task ContextStore_ListQuery_ShouldFilterByCollection()
    {
        if (ShouldSkip) return;

        var (factory, migrationRunner, serializer) = CreateInfrastructure("ctxq_");
        try
        {
            var store = new PostgresContextStore(factory, serializer, migrationRunner);
            var now = DateTimeOffset.UtcNow;

            for (var i = 1; i <= 5; i++)
            {
                await store.SaveAsync(new ContextItem
                {
                    Id = $"item-{i}",
                    WorkspaceId = "ws",
                    CollectionId = i <= 3 ? "col-a" : "col-b",
                    Type = "note",
                    Content = $"内容 {i}",
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            var query = new ContextQuery { WorkspaceId = "ws", CollectionId = "col-a", Take = 10 };
            var results = await store.QueryAsync(query);

            Assert.AreEqual(3, results.Count, "col-a 应有 3 条");
            Assert.IsTrue(results.All(r => r.CollectionId == "col-a"));
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("Postgres")]
    public async Task MemoryStore_SaveAndPromotion_ShouldRoundtrip()
    {
        if (ShouldSkip) return;

        var (factory, migrationRunner, serializer) = CreateInfrastructure("mem_");
        try
        {
            var store = new PostgresMemoryStore(factory, serializer, migrationRunner);
            var now = DateTimeOffset.UtcNow;

            var item = new ContextMemoryItem
            {
                Id = "mem-1",
                WorkspaceId = "ws",
                CollectionId = "col",
                Layer = ContextMemoryLayer.Working,
                Status = ContextMemoryStatus.Active,
                Type = "task",
                Content = "工作记忆测试。",
                Importance = 0.7,
                Confidence = 0.9,
                SourceRefs = ["src:mem-1"],
                CreatedAt = now,
                UpdatedAt = now
            };

            await store.SaveAsync(item);
            var loaded = await store.GetAsync("ws", "col", "mem-1");

            Assert.IsNotNull(loaded);
            Assert.AreEqual(ContextMemoryLayer.Working, loaded.Layer);
            Assert.AreEqual(ContextMemoryStatus.Active, loaded.Status);

            // 晋升：Working → Stable
            var promoted = new ContextMemoryItem
            {
                Id = item.Id,
                WorkspaceId = item.WorkspaceId,
                CollectionId = item.CollectionId,
                Layer = ContextMemoryLayer.Stable,
                Status = ContextMemoryStatus.Verified,
                Type = item.Type,
                Content = item.Content,
                Importance = item.Importance,
                Confidence = item.Confidence,
                SourceRefs = item.SourceRefs,
                CreatedAt = item.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await store.SaveAsync(promoted);
            var reloaded = await store.GetAsync("ws", "col", "mem-1");

            Assert.IsNotNull(reloaded);
            Assert.AreEqual(ContextMemoryLayer.Stable, reloaded.Layer);
            Assert.AreEqual(ContextMemoryStatus.Verified, reloaded.Status);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("Postgres")]
    public async Task RelationStore_SaveAndQuery_ShouldBuildGraph()
    {
        if (ShouldSkip) return;

        var (factory, migrationRunner, serializer) = CreateInfrastructure("rel_");
        try
        {
            var store = new PostgresRelationStore(factory, serializer, migrationRunner);
            var now = DateTimeOffset.UtcNow;

            await store.SaveAsync(new ContextRelation
            {
                Id = "rel-1",
                WorkspaceId = "ws",
                CollectionId = "col",
                SourceId = "node-a",
                TargetId = "node-b",
                RelationType = "depends_on",
                Weight = 0.8,
                Confidence = 1.0,
                CreatedAt = now
            });

            await store.SaveAsync(new ContextRelation
            {
                Id = "rel-2",
                WorkspaceId = "ws",
                CollectionId = "col",
                SourceId = "node-a",
                TargetId = "node-c",
                RelationType = "references",
                Weight = 0.6,
                Confidence = 0.9,
                CreatedAt = now
            });

            var outgoing = await store.QueryAsync(new ContextRelationQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                SourceId = "node-a"
            });
            Assert.AreEqual(2, outgoing.Count, "node-a 应有 2 条出边");
            Assert.IsTrue(outgoing.Any(r => r.TargetId == "node-b" && r.RelationType == "depends_on"));
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("Postgres")]
    public async Task VectorStore_UpsertAndGet_ShouldRoundtrip()
    {
        if (ShouldSkip) return;

        var (factory, migrationRunner, serializer) = CreateInfrastructure("vec_");
        try
        {
            var store = new PostgresVectorStore(factory, serializer, migrationRunner);
            var now = DateTimeOffset.UtcNow;

            // 4 维向量（测试用，无需真实 embedding）
            var vector = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };

            await store.UpsertAsync(new VectorRecord
            {
                Id = "vec-1",
                WorkspaceId = "ws",
                CollectionId = "col",
                SourceId = "src-1",
                SourceKind = "context",
                ModelName = "test-model",
                Dimensions = 4,
                ContentHash = "hash-abc",
                Vector = vector,
                Tags = ["test"],
                CreatedAt = now,
                UpdatedAt = now
            });

            var loaded = await store.GetAsync("ws", "vec-1");
            Assert.IsNotNull(loaded, "应能取回向量记录");
            Assert.AreEqual("test-model", loaded.ModelName);
            Assert.AreEqual(4, loaded.Vector.Count);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("Postgres")]
    public async Task VectorStore_SearchByVector_ShouldReturnNearest()
    {
        if (ShouldSkip) return;

        var (factory, migrationRunner, serializer) = CreateInfrastructure("vsrch_");
        try
        {
            var store = new PostgresVectorStore(factory, serializer, migrationRunner);
            var now = DateTimeOffset.UtcNow;

            // 存入 3 个向量，查询最近邻
            var vectors = new[]
            {
                (id: "v1", v: new float[] { 1.0f, 0.0f, 0.0f, 0.0f }),
                (id: "v2", v: new float[] { 0.0f, 1.0f, 0.0f, 0.0f }),
                (id: "v3", v: new float[] { 0.0f, 0.0f, 1.0f, 0.0f })
            };

            foreach (var (id, v) in vectors)
            {
                await store.UpsertAsync(new VectorRecord
                {
                    Id = id,
                    WorkspaceId = "ws",
                    CollectionId = "col",
                    SourceId = id,
                    SourceKind = "context",
                    ModelName = "test-model",
                    Dimensions = 4,
                    ContentHash = $"hash-{id}",
                    Vector = v,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            // 查询向量最接近 v1
            var query = new VectorQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                Vector = new float[] { 0.99f, 0.1f, 0.0f, 0.0f },
                TopK = 1
            };

            var results = await store.SearchAsync(query);
            Assert.IsTrue(results.Count > 0, "应返回至少一个结果");
            Assert.AreEqual("v1", results[0].Record.Id, "最近邻应为 v1");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("Postgres")]
    public async Task ConstraintStore_SaveAndQuery_ShouldFilter()
    {
        if (ShouldSkip) return;

        var (factory, migrationRunner, serializer) = CreateInfrastructure("cst_");
        try
        {
            var store = new PostgresConstraintStore(factory, serializer, migrationRunner);
            var now = DateTimeOffset.UtcNow;

            await store.SaveAsync(new ContextConstraint
            {
                Id = "c-1",
                WorkspaceId = "ws",
                CollectionId = "col",
                Content = "token_budget:4096",
                Scope = ContextScope.Collection,
                Level = ConstraintLevel.Soft,
                Status = ContextMemoryStatus.Active,
                Confidence = 1.0,
                CreatedAt = now,
                UpdatedAt = now
            });

            var results = await store.QueryAsync(new ContextConstraintQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                Status = ContextMemoryStatus.Active
            });

            Assert.IsTrue(results.Any(c => c.Id == "c-1"), "应返回已存入的约束");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("Postgres")]
    public async Task JobQueue_EnqueueAndDequeue_ShouldRoundtrip()
    {
        if (ShouldSkip) return;

        var (factory, migrationRunner, serializer) = CreateInfrastructure("jq_");
        try
        {
            var queue = new PostgresContextJobQueue(factory, serializer, migrationRunner);
            var now = DateTimeOffset.UtcNow;

            var job = new ContextJob
            {
                JobId = "job-test-1",
                WorkspaceId = "ws",
                CollectionId = "col",
                Kind = ContextJobKind.Compression,
                PayloadJson = """{"contextItemId":"item-1"}""",
                Priority = 5,
                State = ContextJobState.Queued,
                CreatedAt = now
            };

            await queue.EnqueueAsync(job);

            var dequeued = await queue.DequeueAsync();
            Assert.IsNotNull(dequeued, "应能出队刚入队的作业");
            Assert.AreEqual("job-test-1", dequeued.JobId);
            Assert.AreEqual(ContextJobKind.Compression, dequeued.Kind);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("Postgres")]
    public async Task JobQueue_CompleteAndFail_ShouldUpdateState()
    {
        if (ShouldSkip) return;

        var (factory, migrationRunner, serializer) = CreateInfrastructure("jqcf_");
        try
        {
            var queue = new PostgresContextJobQueue(factory, serializer, migrationRunner);
            var now = DateTimeOffset.UtcNow;

            var job = new ContextJob
            {
                JobId = "job-cf-1",
                WorkspaceId = "ws",
                CollectionId = "col",
                Kind = ContextJobKind.PackageRefresh,
                State = ContextJobState.Queued,
                CreatedAt = now
            };
            await queue.EnqueueAsync(job);

            var dequeued = await queue.DequeueAsync();
            Assert.IsNotNull(dequeued);

            // Nack 标记为失败（超过 maxRetry → Failed）
            await queue.NackAsync(dequeued.JobId, "测试失败");

            var jobs = await queue.QueryAsync(new ContextJobQuery
            {
                WorkspaceId = "ws"
            });

            var loaded = jobs.FirstOrDefault(j => j.JobId == dequeued.JobId);
            Assert.IsNotNull(loaded, "应能查询到 Nack 后的作业");
            Assert.AreEqual("测试失败", loaded.ErrorMessage);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("Postgres")]
    public async Task PackageBuildTraceStore_SaveAndQuery_ShouldRoundtrip()
    {
        if (ShouldSkip) return;

        var (factory, migrationRunner, serializer) = CreateInfrastructure("pbt_");
        try
        {
            var store = new PostgresContextPackageBuildTraceStore(factory, serializer, migrationRunner);
            var now = DateTimeOffset.UtcNow;

            var buildId = "build-pbt-1";
            var result = new ContextPackageBuildResult
            {
                BuildId = buildId,
                Package = new ContextPackage
                {
                    PackageId = "pkg-1",
                    WorkspaceId = "ws",
                    CollectionId = "col",
                    Sections = [],
                    EstimatedTokens = 100,
                    CreatedAt = now
                },
                CreatedAt = now
            };

            await store.SaveAsync(result);
            var results = await store.QueryRecentAsync("ws", "col", 10);

            Assert.IsTrue(results.Any(r => r.BuildId == buildId), "应能查询到刚存入的 BuildTrace");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("Postgres")]
    public async Task RetrievalTraceStore_SaveAndQuery_ShouldRoundtrip()
    {
        if (ShouldSkip) return;

        var (factory, migrationRunner, serializer) = CreateInfrastructure("rtr_");
        try
        {
            var store = new PostgresRetrievalTraceStore(factory, serializer, migrationRunner);
            var now = DateTimeOffset.UtcNow;

            var trace = new ContextRetrievalTrace
            {
                RetrievalId = "rtr-1",
                WorkspaceId = "ws",
                CollectionId = "col",
                QueryText = "测试检索",
                Stages = [],
                Candidates = [],
                SelectedItems = [],
                DroppedItems = [],
                Metadata = new Dictionary<string, string>(),
                CreatedAt = now
            };

            await store.SaveAsync(trace);
            var results = await store.QueryRecentAsync("ws", "col", 10);

            Assert.IsTrue(results.Any(t => t.RetrievalId == "rtr-1"), "应能查询到检索 trace");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("Postgres")]
    public async Task EventSink_EmitAndQuery_ShouldRoundtrip()
    {
        if (ShouldSkip) return;

        var (factory, migrationRunner, serializer) = CreateInfrastructure("ev_");
        try
        {
            var sink = new PostgresContextEventSink(factory, serializer, migrationRunner);
            var now = DateTimeOffset.UtcNow;

            var opEvent = new ContextOperationEvent
            {
                EventId = "event-test-1",
                OperationId = "op-test-123",
                OperationName = "TestOperation",
                WorkspaceId = "ws-event-test",
                CollectionId = "col-event-test",
                Level = ContextEventLevel.Information,
                Message = "这是一条 PostgreSQL 事件接收器集成测试日志。",
                Duration = TimeSpan.FromMilliseconds(123.45),
                CreatedAt = now
            };

            await sink.EmitAsync(opEvent);

            var list = await sink.QueryEventsAsync("ws-event-test", 10);
            Assert.AreEqual(1, list.Count, "事件列表应包含刚才保存的事件");
            var loaded = list[0];
            Assert.AreEqual("event-test-1", loaded.EventId);
            Assert.AreEqual("op-test-123", loaded.OperationId);
            Assert.AreEqual("TestOperation", loaded.OperationName);
            Assert.AreEqual(ContextEventLevel.Information, loaded.Level);
            Assert.AreEqual("这是一条 PostgreSQL 事件接收器集成测试日志。", loaded.Message);
            Assert.IsNotNull(loaded.Duration);
            Assert.AreEqual(123.45, loaded.Duration.Value.TotalMilliseconds, 1e-9);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // ── Docker 可用性检测 ────────────────────────────────────────────────

    private static async Task<bool> IsDockerAvailableAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // Testcontainers 会在构建期间检查 Docker，此处用简单 ping 规避超时
            await Task.Run(() =>
            {
                using var client = new System.Net.Sockets.TcpClient();
                // Docker Desktop on Windows 默认 named pipe；尝试 TCP 连接 localhost:2375
                // 若失败则表明 Docker 不可用
                client.Connect("localhost", 2375);
            }, cts.Token);
            return true;
        }
        catch
        {
            // Docker 不可用：通过直接尝试启动 Testcontainers 来检测
            // 此处捕获所有异常（连接失败/超时），跳过测试
            return await IsDockerSocketAvailableAsync();
        }
    }

    private static async Task<bool> IsDockerSocketAvailableAsync()
    {
        try
        {
            // 尝试直接通过 Testcontainers 内置检测
            var testContainer = new PostgreSqlBuilder(PgVectorImage)
                .Build();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await testContainer.StartAsync(cts.Token);
            await testContainer.DisposeAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
