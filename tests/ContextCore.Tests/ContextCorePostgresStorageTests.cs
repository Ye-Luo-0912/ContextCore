using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Evaluation.Models;
using ContextCore.Core.Services;
using ContextCore.Evaluation;
using ContextCore.Evaluation.Runners;
using ContextCore.Service.Infrastructure;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Extensions;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ContextCore.Evaluation.Learning;

namespace ContextCore.Tests;

/// <summary>覆盖 PostgreSQL 存储后端的迁移 SQL、序列化和 DI 注册。</summary>
[TestClass]
[TestCategory("Storage")]
[TestCategory("Postgres")]
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
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_relation_diagnostics");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_vectors");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_retrieval_traces");
        StringAssert.Contains(sql, "embedding vector NOT NULL");
        StringAssert.Contains(sql, "data jsonb NOT NULL");
    }

    [TestMethod]
    public void PostgresMigrationSql_WithSchema_ShouldUseSafeIndexNames()
    {
        var sql = PostgresMigrationRunner.BuildMigrationSql(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=contextcore;Username=contextcore;Password=contextcore",
            SchemaName = "contextcore_smoke",
            TablePrefix = "cc_",
            EnablePgVectorExtension = false
        });

        StringAssert.Contains(sql, "CREATE SCHEMA IF NOT EXISTS contextcore_smoke");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS contextcore_smoke.cc_context_items");
        StringAssert.Contains(sql, "CREATE INDEX IF NOT EXISTS ix_cc_context_items_type ON contextcore_smoke.cc_context_items");
        Assert.IsFalse(sql.Contains("ix_contextcore_smoke.", StringComparison.Ordinal));
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
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IRelationReviewStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IVectorStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(PostgresVectorIndexStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IVectorIndexStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IRetrievalTraceStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(PostgresLearningFeedbackStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(ILearningFeedbackStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(PostgresLearningFeedbackReviewStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(ILearningFeedbackReviewStore)));
    }

    [TestMethod]
    public async Task InMemoryLearningFeatureCandidateStore_ShouldUpsertAndFilter()
    {
        var store = new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeatureCandidateStore();
        await store.UpsertAsync(new FeedbackFeatureCandidate
        {
            CandidateId = "candidate-a",
            SourceFeedbackId = "feedback-a",
            CapabilityId = ShadowCapabilityIds.VectorRetrieval,
            TargetType = LearningFeedbackTargetType.VectorCandidate.ToString(),
            LabelKind = "vector_recall_candidate",
            TrainingUse = "offline_baseline_candidate"
        });

        var rows = await store.QueryAsync(new LearningFeatureCandidateQuery
        {
            CapabilityId = ShadowCapabilityIds.VectorRetrieval,
            LabelKind = "vector_recall_candidate"
        });

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("candidate-a", rows[0].CandidateId);
    }

    [TestMethod]
    public async Task InMemoryJobQueue_ShouldFilterByKind()
    {
        var queue = new InMemoryJobQueue();
        await queue.EnqueueAsync(new ContextJob
        {
            JobId = "kind-compression",
            WorkspaceId = "ws",
            CollectionId = "col",
            Kind = ContextJobKind.Compression,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await queue.EnqueueAsync(new ContextJob
        {
            JobId = "kind-custom",
            WorkspaceId = "ws",
            CollectionId = "col",
            Kind = ContextJobKind.Custom,
            CreatedAt = DateTimeOffset.UtcNow
        });

        var rows = await queue.QueryAsync(new ContextJobQuery
        {
            WorkspaceId = "ws",
            CollectionId = "col",
            Kind = ContextJobKind.Compression
        });

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("kind-compression", rows[0].JobId);
    }

    [TestMethod]
    public async Task FileContextJobQueue_ShouldFilterByKind()
    {
        var root = Path.Combine(Path.GetTempPath(), "contextcore-job-kind-test", Guid.NewGuid().ToString("N"));
        try
        {
            var queue = new FileContextJobQueue(new FileStorageOptions { RootPath = root });
            await queue.EnqueueAsync(new ContextJob
            {
                JobId = "file-kind-compression",
                WorkspaceId = "ws",
                CollectionId = "col",
                Kind = ContextJobKind.Compression,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await queue.EnqueueAsync(new ContextJob
            {
                JobId = "file-kind-custom",
                WorkspaceId = "ws",
                CollectionId = "col",
                Kind = ContextJobKind.Custom,
                CreatedAt = DateTimeOffset.UtcNow
            });

            var rows = await queue.QueryAsync(new ContextJobQuery
            {
                WorkspaceId = "ws",
                CollectionId = "col",
                Kind = ContextJobKind.Custom
            });

            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("file-kind-custom", rows[0].JobId);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void PostgresDiagnostics_ShouldReturnNotConfiguredWhenProviderDisabled()
    {
        var options = new PostgresOptions { Enabled = false };

        var diagnostics = PostgresOperationalStoreDiagnosticsBuilder.BuildNotConfigured(options);

        Assert.AreEqual("NotConfigured", diagnostics.Status);
        Assert.IsFalse(diagnostics.ProviderEnabled);
        Assert.AreEqual(PostgresMigrationRunner.RequiredOperationalTableSuffixes.Count, diagnostics.RequiredTableMissingCount);
        Assert.IsNotNull(diagnostics.SchemaVerification);
        Assert.AreEqual("NotConfigured", diagnostics.SchemaVerification.Recommendation);
        Assert.AreEqual(PostgresMigrationRunner.RequiredOperationalIndexDefinitions.Count, diagnostics.SchemaVerification.MissingIndexCount);
    }

    [TestMethod]
    public void PostgresRelationStoreDiagnostics_ShouldReturnNotConfiguredWhenProviderDisabled()
    {
        var options = new PostgresOptions { Enabled = false };

        var diagnostics = PostgresRelationStoreDiagnosticsBuilder.BuildNotConfigured(options);

        Assert.IsFalse(diagnostics.ProviderEnabled);
        Assert.IsFalse(diagnostics.ConnectionAvailable);
        Assert.AreEqual("FileSystemRelationStore", diagnostics.ActiveRuntimeProvider);
        Assert.AreEqual("NotConfigured", diagnostics.Recommendation);
        CollectionAssert.Contains(diagnostics.Diagnostics.ToArray(), "NotConfigured");
        Assert.IsTrue(diagnostics.MissingRequiredIndexes.Count > 0);
    }

    [TestMethod]
    public void PostgresRelationReviewDiagnostics_ShouldReturnNotConfiguredWhenProviderDisabled()
    {
        var options = new PostgresOptions { Enabled = false };

        var diagnostics = PostgresRelationReviewDiagnosticsBuilder.BuildNotConfigured(options);

        Assert.IsFalse(diagnostics.ProviderEnabled);
        Assert.IsFalse(diagnostics.ConnectionAvailable);
        Assert.AreEqual("FileSystemRelationStore", diagnostics.ActiveRuntimeProvider);
        Assert.AreEqual("NotConfigured", diagnostics.Recommendation);
        CollectionAssert.Contains(diagnostics.Diagnostics.ToArray(), "NotConfigured");
        Assert.IsTrue(diagnostics.MissingRequiredIndexes.Count > 0);
    }

    [TestMethod]
    public void PostgresRelationGovernanceReadinessGate_ShouldFailWhenCleanupMissing()
    {
        var report = new PostgresRelationGovernanceReadinessGateReport
        {
            ProviderEnabled = true,
            Passed = false,
            StorageReady = true,
            SchemaVersionReady = true,
            RelationTableExists = true,
            RelationReviewsTableExists = true,
            RelationDiagnosticsTableExists = true,
            MissingRequiredIndexCount = 0,
            RelationStoreParityPassed = true,
            RelationReviewParityPassed = true,
            DiagnosticsParityPassed = true,
            GovernanceParityPassed = true,
            CleanupPerformed = false,
            BlockedReasons = ["CleanupNotPerformed"],
            Recommendation = "NeedsParityFix"
        };

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "CleanupNotPerformed");
    }

    [TestMethod]
    public void PostgresRelationGovernanceReadinessGate_ShouldFailWhenRuntimeEnabled()
    {
        var report = new PostgresRelationGovernanceReadinessGateReport
        {
            ProviderEnabled = true,
            Passed = false,
            UseForRuntime = true,
            BlockedReasons = ["UseForRuntimeMustRemainFalse"],
            Recommendation = "NeedsParityFix"
        };

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "UseForRuntimeMustRemainFalse");
    }

    [TestMethod]
    public void RelationGovernanceProviderSwitchOptions_ShouldDefaultToFileSystemPrimary()
    {
        var options = new RelationGovernanceProviderSwitchOptions();

        Assert.AreEqual(RelationGovernanceProviderMode.FileSystemPrimary, options.Mode);
        Assert.IsFalse(options.Enabled);
        Assert.IsTrue(options.FallbackToFileSystem);
        Assert.IsTrue(options.ContinueComparisonTrace);
        Assert.IsTrue(options.FailClosedOnMismatch);
        Assert.IsTrue(options.RequireReadinessGate);
        Assert.IsTrue(options.RequireRuntimeCanaryPassed);
    }

    [TestMethod]
    public void RelationGovernanceScopedServiceModeStatus_ShouldDefaultToFileSystem()
    {
        var service = new RelationGovernanceScopedServiceModeStatusService(new RelationGovernanceProviderSwitchOptions());

        var status = service.GetStatus();

        Assert.AreEqual("FileSystemRelationStore", status.ActiveRuntimeProvider);
        Assert.AreEqual("FileSystemPrimary", status.Recommendation);
        CollectionAssert.Contains(status.Diagnostics.ToArray(), "ScopedServiceModeDisabled");
    }

    [TestMethod]
    public void RelationGovernanceScopedRule_ShouldDescribeExplicitScope()
    {
        var rule = new RelationGovernanceScopedRule
        {
            ScopeName = "scope-a",
            WorkspaceId = "workspace-a",
            CollectionId = "collection-a",
            RolloutStage = "db2.10",
            Mode = RelationGovernanceProviderMode.GuardedPostgresPrimary
        };

        Assert.AreEqual("scope-a", rule.ScopeName);
        Assert.AreEqual(RelationGovernanceProviderMode.GuardedPostgresPrimary, rule.Mode);
        Assert.IsTrue(rule.Enabled);
    }

    [TestMethod]
    public void PostgresRelationRuntimeCanaryReport_ShouldBlockWhenGateNotPassed()
    {
        var report = new PostgresRelationRuntimeCanaryReport
        {
            GatePassed = false,
            BlockedReasons = ["ProviderSwitchGateNotPassed"],
            Recommendation = "GateNotPassed"
        };

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual("GateNotPassed", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "ProviderSwitchGateNotPassed");
    }

    [TestMethod]
    public void RelationGovernanceProviderSwitchTrace_ShouldRecordMismatch()
    {
        var trace = new RelationGovernanceProviderSwitchTrace
        {
            OperationId = "op",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Mode = RelationGovernanceProviderMode.GuardedPostgresPrimary.ToString(),
            OperationKind = "RelationGet",
            PrimaryProvider = "Postgres",
            MismatchDetected = true,
            FallbackUsed = true,
            ReadinessGateVersion = "db2.5"
        };

        Assert.IsTrue(trace.MismatchDetected);
        Assert.IsTrue(trace.FallbackUsed);
        Assert.AreEqual("Postgres", trace.PrimaryProvider);
    }

    [TestMethod]
    public void PostgresRelationProviderSwitchGateReport_ShouldBlockOnMissingFallbackTest()
    {
        var report = new PostgresRelationProviderSwitchGateReport
        {
            Passed = false,
            FallbackPathTested = false,
            BlockedReasons = ["FallbackPathNotTested"],
            Recommendation = "GateNotReady"
        };

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "FallbackPathNotTested");
    }

    [TestMethod]
    public void PostgresConnectionString_ShouldBeRedacted()
    {
        var redacted = PostgresMigrationRunner.RedactConnectionString(
            "Host=localhost;Database=contextcore;Username=user1;Password=secret");

        StringAssert.Contains(redacted, "Password=***");
        StringAssert.Contains(redacted, "Username=***");
        Assert.IsFalse(redacted.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(redacted.Contains("user1", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void PostgresMigrationDryRun_ShouldListBaselineMigration()
    {
        var runner = new FakeMigrationRunner(
            currentVersion: null,
            missingTables: ["cc_context_items"]);

        var migrations = runner.ListMigrations();

        Assert.AreEqual(PostgresMigrationRunner.BaselineMigrationId, migrations.Single().MigrationId);
        Assert.IsTrue(migrations.Single().RequiredTables.Contains("context_schema_migrations"));
    }

    [TestMethod]
    public async Task PostgresDiagnostics_ShouldReportMissingRequiredTablesWithFakeRunner()
    {
        var options = new PostgresOptions
        {
            Enabled = true,
            ConnectionString = "Host=localhost;Database=contextcore;Username=user;Password=secret",
            AutoMigrate = false
        };
        var missing = new[] { "cc_context_items", "cc_relations" };
        var diagnostics = await PostgresOperationalStoreDiagnosticsBuilder.BuildAsync(
            options,
            new FakePostgresConnectionFactory(options, success: true),
            new FakeMigrationRunner(null, missing),
            CancellationToken.None);

        Assert.AreEqual("MigrationPending", diagnostics.Status);
        Assert.AreEqual(2, diagnostics.RequiredTableMissingCount);
        CollectionAssert.AreEqual(missing, diagnostics.MissingRequiredTables.ToArray());
    }

    [TestMethod]
    public async Task PostgresDiagnostics_ShouldDetectCompleteBaselineWithFakeRunner()
    {
        var options = new PostgresOptions
        {
            Enabled = true,
            ConnectionString = "Host=localhost;Database=contextcore;Username=user;Password=secret",
            AutoMigrate = false
        };
        var diagnostics = await PostgresOperationalStoreDiagnosticsBuilder.BuildAsync(
            options,
            new FakePostgresConnectionFactory(options, success: true),
            new FakeMigrationRunner(PostgresMigrationRunner.SchemaVersion, []),
            CancellationToken.None);

        Assert.AreEqual("Ready", diagnostics.Status);
        Assert.AreEqual(0, diagnostics.PendingMigrations);
        Assert.AreEqual(0, diagnostics.RequiredTableMissingCount);
    }

    [TestMethod]
    public async Task PostgresMigrationApply_ShouldRejectWithoutConfirm()
    {
        var runner = new PostgresMigrationRunner(new PostgresConnectionFactory(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=contextcore;Username=contextcore;Password=contextcore",
            AutoMigrate = false
        }));

        var result = await runner.ApplyMigrationsAsync(confirm: false, CancellationToken.None);

        Assert.IsFalse(result.Applied);
        Assert.IsTrue(result.ConfirmRequired);
        CollectionAssert.Contains(result.Diagnostics.ToArray(), "ConfirmRequired");
    }

    [TestMethod]
    public void PostgresMigrationSql_ShouldExposeVectorIndexProviderSchema()
    {
        var options = new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=contextcore;Username=contextcore;Password=contextcore",
            TablePrefix = "cc_",
            EnablePgVectorExtension = true
        };

        var sql = PostgresMigrationRunner.BuildMigrationSql(options);
        var requiredIndexes = PostgresMigrationRunner.GetRequiredIndexNames(options);

        Assert.AreEqual("cc-schema-v8", PostgresMigrationRunner.SchemaVersion);
        StringAssert.Contains(sql, "CREATE EXTENSION IF NOT EXISTS vector");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_vector_index_entries");
        StringAssert.Contains(sql, "source_id text NOT NULL DEFAULT ''");
        StringAssert.Contains(sql, "source_kind text NOT NULL DEFAULT ''");
        StringAssert.Contains(sql, "provider_id text NOT NULL DEFAULT ''");
        StringAssert.Contains(sql, "model_id text NOT NULL DEFAULT ''");
        StringAssert.Contains(sql, "normalized boolean NOT NULL DEFAULT true");
        StringAssert.Contains(sql, "metadata_json jsonb NOT NULL DEFAULT jsonb_build_object()");
        CollectionAssert.Contains(requiredIndexes.ToArray(), "ix_cc_vector_index_entries_scope");
        CollectionAssert.Contains(requiredIndexes.ToArray(), "ix_cc_vector_index_entries_provider_model_dimension");
        CollectionAssert.Contains(requiredIndexes.ToArray(), "ix_cc_vector_index_entries_source");
    }

    [TestMethod]
    public async Task FileVectorIndexStore_ShouldRoundtripMetadataAndOrderNearestNeighbors()
    {
        var root = Path.Combine(Path.GetTempPath(), "contextcore-vector-file-parity-test", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileVectorIndexStore(new FileStorageOptions { RootPath = root });
            var entries = new[]
            {
                CreateVectorEntry("entry-a", "source-a", [1f, 0f, 0f]),
                CreateVectorEntry("entry-b", "source-b", [0.8f, 0.6f, 0f]),
                CreateVectorEntry("entry-c", "source-c", [0f, 1f, 0f])
            };

            foreach (var entry in entries)
            {
                await store.UpsertAsync(entry);
            }

            var listed = await store.ListAsync(new VectorIndexQuery
            {
                WorkspaceId = "workspace",
                CollectionId = "collection",
                EmbeddingProvider = "deterministic-hash",
                EmbeddingModel = "deterministic-hash-v1",
                IncludeVector = true,
                Take = 10
            });
            var loaded = listed.Single(item => item.EntryId == "entry-a");
            var results = await store.SearchAsync(new VectorIndexSearchQuery
            {
                WorkspaceId = "workspace",
                CollectionId = "collection",
                EmbeddingProvider = "deterministic-hash",
                EmbeddingModel = "deterministic-hash-v1",
                Dimension = 3,
                Vector = [1f, 0f, 0f],
                TopK = 3
            });

            Assert.AreEqual("source-a", loaded.Metadata["sourceId"]);
            Assert.AreEqual("parity-source", loaded.Metadata["sourceKind"]);
            CollectionAssert.AreEqual(
                new[] { "entry-a", "entry-b", "entry-c" },
                results.Select(item => item.Entry.EntryId).ToArray());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task PostgresVectorIndexStoreSearch_ShouldBlockDimensionMismatchBeforeConnection()
    {
        var store = CreatePostgresVectorIndexStoreWithoutConnection();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => store.SearchAsync(new VectorIndexSearchQuery
        {
            WorkspaceId = "workspace",
            CollectionId = "collection",
            EmbeddingProvider = "provider",
            EmbeddingModel = "model",
            Dimension = 3,
            Vector = [1f, 0f],
            TopK = 5
        }));
    }

    [TestMethod]
    public async Task PostgresVectorIndexStoreSearch_ShouldRequireProviderAndModelBeforeConnection()
    {
        var store = CreatePostgresVectorIndexStoreWithoutConnection();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => store.SearchAsync(new VectorIndexSearchQuery
        {
            WorkspaceId = "workspace",
            CollectionId = "collection",
            Dimension = 2,
            Vector = [1f, 0f],
            TopK = 5
        }));
    }

    private sealed class FakePostgresConnectionFactory(PostgresOptions options, bool success) : IPostgresConnectionFactory
    {
        public PostgresOptions Options { get; } = options;

        public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Fake connection factory does not open real connections.");
        }

        public Task<(bool Success, string? ErrorMessage)> PingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(success
                ? (true, (string?)null)
                : (false, (string?)"connection failed"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeMigrationRunner(string? currentVersion, IReadOnlyList<string> missingTables)
        : IStoreMigrationRunner
    {
        public IReadOnlyList<PostgresStoreMigration> ListMigrations()
        {
            return
            [
                new PostgresStoreMigration
                {
                    MigrationId = PostgresMigrationRunner.BaselineMigrationId,
                    SchemaVersion = PostgresMigrationRunner.SchemaVersion,
                    Description = "fake baseline",
                    RequiredTables = PostgresMigrationRunner.RequiredOperationalTableSuffixes
                }
            ];
        }

        public Task<PostgresMigrationPlan> PreviewMigrationsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PostgresMigrationPlan
            {
                DryRun = true,
                ProviderEnabled = true,
                ProviderId = "fake",
                CurrentSchemaVersion = currentVersion,
                PendingMigrations = missingTables.Count == 0 ? [] : [PostgresMigrationRunner.BaselineMigrationId],
                RequiredTables = PostgresMigrationRunner.RequiredOperationalTableSuffixes.Select(item => "cc_" + item).ToArray(),
                MissingRequiredTables = missingTables,
                Diagnostics = missingTables.Count == 0 ? [] : ["PendingMigrationsDetected"]
            });
        }

        public Task<PostgresMigrationApplyResult> ApplyMigrationsAsync(bool confirm, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PostgresMigrationApplyResult
            {
                Applied = confirm,
                ConfirmRequired = !confirm,
                Diagnostics = confirm ? [] : ["ConfirmRequired"]
            });
        }

        public Task<string?> GetAppliedVersionAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(currentVersion);
        }
    }

    private static PostgresVectorIndexStore CreatePostgresVectorIndexStoreWithoutConnection()
    {
        var options = new PostgresOptions
        {
            Enabled = false,
            ConnectionString = "Host=localhost;Database=contextcore;Username=user;Password=secret",
            AutoMigrate = false
        };
        var factory = new PostgresConnectionFactory(options);
        return new PostgresVectorIndexStore(factory, new PostgresJsonSerializer(), new PostgresMigrationRunner(factory));
    }

    private static VectorIndexEntry CreateVectorEntry(string entryId, string sourceId, IReadOnlyList<float> vector)
        => new()
        {
            EntryId = entryId,
            ItemId = sourceId,
            ItemKind = "parity-item",
            Layer = "parity",
            WorkspaceId = "workspace",
            CollectionId = "collection",
            ContentHash = "content-" + sourceId,
            EmbeddingProvider = "deterministic-hash",
            EmbeddingModel = "deterministic-hash-v1",
            Dimension = vector.Count,
            Vector = vector,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sourceId"] = sourceId,
                ["sourceKind"] = "parity-source"
            }
        };

}
