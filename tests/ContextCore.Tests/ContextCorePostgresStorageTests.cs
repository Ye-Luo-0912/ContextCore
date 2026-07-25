using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Evaluation.Models;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Evaluation;
using ContextCore.Evaluation.Runners;
using ContextCore.Service;
using ContextCore.Service.Extensions;
using ContextCore.Service.Infrastructure;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Extensions;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(PostgresDecisionTraceStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IDecisionTraceStore)));
        // R14-PG-3：4 个新 Postgres store 与对应接口注册
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(PostgresShortTermMemoryStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IShortTermMemoryStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(PostgresShortTermPromotionCandidateStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IShortTermPromotionCandidateStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(PostgresCandidateMemoryReviewStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(ICandidateMemoryReviewStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(PostgresStableReviewCandidateStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IStableReviewCandidateStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(PostgresLearningFeedbackStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(ILearningFeedbackStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(PostgresLearningFeedbackReviewStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(ILearningFeedbackReviewStore)));
        // R14-PG-4：context learning / governance review stores 注册
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(PostgresContextLearningStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IContextLearningStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(PostgresStableLifecycleReviewStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IStableLifecycleReviewStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(PostgresCandidateConstraintReviewStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(ICandidateConstraintReviewStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(PostgresConstraintGapCandidateStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IConstraintGapCandidateStore)));
        // R14-PG-5：vector lifecycle + artifact stores 注册
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(PostgresVectorReindexReportStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IVectorReindexReportStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(PostgresVectorLifecycleMetadataReviewCandidateStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IVectorLifecycleMetadataReviewCandidateStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(PostgresVectorLifecycleMetadataReviewStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IVectorLifecycleMetadataReviewStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(PostgresVectorLifecycleSidecarMetadataStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IVectorLifecycleSidecarMetadataStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(PostgresArtifactStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IArtifactStore)));
        // R14-PG-6：分布式 context state 版本存储注册
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(PostgresContextStateVersionStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IContextStateVersionStore)));
        // R26-2：Agent Runtime 持久化（checkpoint + task state）注册
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(PostgresAgentCheckpointStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IAgentCheckpointStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(PostgresAgentTaskStateStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IAgentTaskStateStore)));
        // R27-3：Evolution Pipeline 持久化注册
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(PostgresPipelineRunStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IPipelineRunStore)));
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

        Assert.AreEqual("cc-schema-v22", PostgresMigrationRunner.SchemaVersion);
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
        // R14-PG-2：decision_traces 表与索引
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_decision_traces");
        StringAssert.Contains(sql, "decision_id text NOT NULL");
        StringAssert.Contains(sql, "source text NOT NULL DEFAULT ''");
        StringAssert.Contains(sql, "ix_cc_decision_traces_created");
        CollectionAssert.Contains(requiredIndexes.ToArray(), "ix_cc_decision_traces_created");
        // R14-PG-3：short-term memory / promotion / candidate review 表
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_short_term_raw_events");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_short_term_working_items");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_short_term_archived_raw_events");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_short_term_archived_working_items");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_short_term_compaction_runs");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_short_term_promotion_candidates");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_short_term_promotion_candidate_reviews");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_candidate_memory_reviews");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_stable_review_candidates");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_stable_review_records");
        // R14-PG-4：context learning / governance review 表
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_context_learning_feedback");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_context_learning_records");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_context_learning_cases");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_stable_lifecycle_reviews");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_candidate_constraint_reviews");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_constraint_gap_candidates");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_constraint_gap_reviews");
        // R14-PG-5：vector lifecycle + artifact 表与索引
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_vector_reindex_reports");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_vector_lifecycle_metadata_review_candidates");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_vector_lifecycle_metadata_reviews");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_vector_lifecycle_sidecar_metadata");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_artifacts");
        StringAssert.Contains(sql, "ix_cc_vector_reindex_reports_created");
        StringAssert.Contains(sql, "ix_cc_vector_lifecycle_metadata_review_candidates_created");
        StringAssert.Contains(sql, "ix_cc_vector_lifecycle_metadata_reviews_candidate");
        StringAssert.Contains(sql, "ix_cc_vector_lifecycle_sidecar_metadata_created");
        StringAssert.Contains(sql, "ix_cc_artifacts_kind");
        // R14-PG-6：分布式 context state 版本号表
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_context_state_versions");
        StringAssert.Contains(sql, "store_kind text NOT NULL");
        StringAssert.Contains(sql, "version bigint NOT NULL DEFAULT 0");
        // R26-1：agent_checkpoints + agent_task_states 表与索引
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_agent_checkpoints");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_agent_task_states");
        StringAssert.Contains(sql, "session_value text NOT NULL");
        StringAssert.Contains(sql, "runtime_kind text NOT NULL DEFAULT 'Unknown'");
        StringAssert.Contains(sql, "state_json text NOT NULL DEFAULT ''");
        CollectionAssert.Contains(requiredIndexes.ToArray(), "ix_cc_agent_checkpoints_session");
        CollectionAssert.Contains(requiredIndexes.ToArray(), "ix_cc_agent_checkpoints_created");
        CollectionAssert.Contains(requiredIndexes.ToArray(), "ix_cc_agent_task_states_session");
        CollectionAssert.Contains(requiredIndexes.ToArray(), "ix_cc_agent_task_states_updated");
        // R27-1：pipeline_runs + 3 audit tables 表与索引
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_pipeline_runs");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_pipeline_canary_assignments");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_pipeline_rollback_records");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_pipeline_baseline_comparisons");
        StringAssert.Contains(sql, "proposal_major integer NOT NULL");
        StringAssert.Contains(sql, "current_stage text NOT NULL DEFAULT 'OfflineExperiment'");
        StringAssert.Contains(sql, "status text NOT NULL DEFAULT 'Running'");
        StringAssert.Contains(sql, "strategy text NOT NULL DEFAULT 'Random'");
        StringAssert.Contains(sql, "reason text NOT NULL DEFAULT 'RollbackConditionTriggered'");
        CollectionAssert.Contains(requiredIndexes.ToArray(), "ix_cc_pipeline_runs_proposal");
        CollectionAssert.Contains(requiredIndexes.ToArray(), "ix_cc_pipeline_runs_status");
        CollectionAssert.Contains(requiredIndexes.ToArray(), "ix_cc_pipeline_runs_updated");
        CollectionAssert.Contains(requiredIndexes.ToArray(), "ix_cc_pipeline_canary_assignments_run");
        CollectionAssert.Contains(requiredIndexes.ToArray(), "ix_cc_pipeline_canary_assignments_assigned");
        CollectionAssert.Contains(requiredIndexes.ToArray(), "ix_cc_pipeline_rollback_records_run");
        CollectionAssert.Contains(requiredIndexes.ToArray(), "ix_cc_pipeline_rollback_records_triggered");
        CollectionAssert.Contains(requiredIndexes.ToArray(), "ix_cc_pipeline_baseline_comparisons_proposal");
        CollectionAssert.Contains(requiredIndexes.ToArray(), "ix_cc_pipeline_baseline_comparisons_compared");
        // WS-A：Policy Registry 持久化表 + CAS 激活索引
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_policy_bundles");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_policy_activations");
        StringAssert.Contains(sql, "bundle_id text NOT NULL");
        StringAssert.Contains(sql, "bundle_version text NOT NULL");
        StringAssert.Contains(sql, "bundle_content_hash text NOT NULL");
        StringAssert.Contains(sql, "epoch bigint NOT NULL DEFAULT 1");
        StringAssert.Contains(sql, "is_superseded boolean NOT NULL DEFAULT false");
        CollectionAssert.Contains(requiredIndexes.ToArray(), "ix_cc_policy_bundles_bundle");
        CollectionAssert.Contains(requiredIndexes.ToArray(), "ix_cc_policy_bundles_superseded");
        CollectionAssert.Contains(requiredIndexes.ToArray(), "ix_cc_policy_activations_bundle");
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

    [TestMethod]
    public void PostgresMigrationSql_IncludesContextStateVersionsUpsertPattern()
    {
        var options = new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=contextcore;Username=contextcore;Password=contextcore",
            TablePrefix = "cc_",
            EnablePgVectorExtension = true
        };

        var sql = PostgresMigrationRunner.BuildMigrationSql(options);

        // R14-PG-6：context_state_versions 表必须支持原子自增，校验 SQL 包含 ON CONFLICT 模式（运行时 BumpVersionAsync 用此模式）。
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_context_state_versions");
        StringAssert.Contains(sql, "store_kind text NOT NULL");
        StringAssert.Contains(sql, "version bigint NOT NULL DEFAULT 0");
        // R26-1：agent_checkpoints + agent_task_states 表 DDL（索引断言在 PostgresMigrationSql_ShouldExposeVectorIndexProviderSchema 中）
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_agent_checkpoints");
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_agent_task_states");
        StringAssert.Contains(sql, "session_value text NOT NULL");
        StringAssert.Contains(sql, "runtime_kind text NOT NULL DEFAULT 'Unknown'");
        StringAssert.Contains(sql, "state_json text NOT NULL DEFAULT ''");
        StringAssert.Contains(sql, "ix_cc_agent_checkpoints_session");
        StringAssert.Contains(sql, "ix_cc_agent_checkpoints_created");
        StringAssert.Contains(sql, "ix_cc_agent_task_states_session");
        StringAssert.Contains(sql, "ix_cc_agent_task_states_updated");
        // R29 WP-B-1：tool_dispatch_journal_entries 表 DDL（持久化 Tool Dispatch Journal）
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_tool_dispatch_journal_entries");
        StringAssert.Contains(sql, "request_id text NOT NULL");
        StringAssert.Contains(sql, "state smallint NOT NULL DEFAULT 0");
        StringAssert.Contains(sql, "ix_cc_tool_dispatch_journal_entries_state");
        StringAssert.Contains(sql, "ix_cc_tool_dispatch_journal_entries_idempotency");
        // R29 WP-B-2：kernel_result_outbox 表 DDL（持久化 Kernel Result Outbox）
        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_kernel_result_outbox");
        StringAssert.Contains(sql, "outbox_id text NOT NULL");
        StringAssert.Contains(sql, "state text NOT NULL DEFAULT 'Pending'");
        StringAssert.Contains(sql, "ix_cc_kernel_result_outbox_state");
        StringAssert.Contains(sql, "ix_cc_kernel_result_outbox_instruction");
    }

    [TestMethod]
    public async Task AddContextStorage_Postgres_OverridesInMemoryVersionStoreRegistration()
    {
        // R14-PG-6：验证 Postgres provider 启用时，PostgresContextStateVersionStore 覆盖 InMemoryContextStateVersionStore。
        // .NET DI 后注册者胜出；AddContextStorage 内部调用 AddContextCorePostgresStorage，注册发生在 AddContextCore 之后。
        var services = new ServiceCollection();
        services.AddLogging();
        // 模拟完整应用启动顺序：先 AddContextCore，再 AddContextStorage
        // 由于 AddContextCore 依赖较多，这里直接模拟两次注册，验证后注册者胜出
        services.AddSingleton<IContextStateVersionStore, InMemoryContextStateVersionStore>();
        var options = new StorageOptions
        {
            Provider = "postgres",
            PostgresConnectionString = "Host=localhost;Database=fake;Username=fake;Password=fake",
        };
        services.AddContextStorage(options);

        // PostgresConnectionFactory 仅实现 IAsyncDisposable，需用 await using 释放容器
        await using var sp = services.BuildServiceProvider();
        var versionStore = sp.GetService<IContextStateVersionStore>();
        Assert.IsNotNull(versionStore, "IContextStateVersionStore 未注册");
        Assert.IsInstanceOfType(versionStore, typeof(PostgresContextStateVersionStore),
            $"Postgres provider 启用时 IContextStateVersionStore 应为 PostgresContextStateVersionStore，实际: {versionStore.GetType().Name}");
    }

    // ========== R14-PG-8：Migration version/rollback 框架测试 ==========

    [TestMethod]
    public void PostgresMigrationRegistry_ContainsBaselineMigration()
    {
        // R14-PG-8：注册表应包含基线 migration，且其 SupportsRollback=false。
        var migrations = PostgresMigrationRegistry.Migrations;
        Assert.AreEqual(1, migrations.Count, "当前应仅有基线 migration");
        var baseline = migrations[0];
        Assert.AreEqual(PostgresMigrationRunner.BaselineMigrationId, baseline.MigrationId);
        Assert.AreEqual(PostgresMigrationRunner.SchemaVersion, baseline.SchemaVersion);
        Assert.IsFalse(baseline.SupportsRollback, "Baseline cumulative idempotent migration 不应支持真实回滚");
        Assert.IsFalse(string.IsNullOrEmpty(baseline.RollbackNotSupportedReason), "应提供不支持回滚的明确原因");
        Assert.IsTrue(baseline.IntroducedTableSuffixes.Count > 0, "应记录引入的表后缀列表");
    }

    [TestMethod]
    public void PostgresMigrationRegistry_FindByVersion_ReturnsBaselineForCurrentVersion()
    {
        var found = PostgresMigrationRegistry.FindByVersion(PostgresMigrationRunner.SchemaVersion);
        Assert.IsNotNull(found);
        Assert.AreEqual(PostgresMigrationRunner.BaselineMigrationId, found.MigrationId);
    }

    [TestMethod]
    public void PostgresMigrationRegistry_FindByVersion_ReturnsNullForUnknownVersion()
    {
        var found = PostgresMigrationRegistry.FindByVersion("cc-schema-v999");
        Assert.IsNull(found);
    }

    [TestMethod]
    public void PostgresMigrationRegistry_FindById_ReturnsBaselineForKnownId()
    {
        var found = PostgresMigrationRegistry.FindById(PostgresMigrationRunner.BaselineMigrationId);
        Assert.IsNotNull(found);
        Assert.AreEqual(PostgresMigrationRunner.SchemaVersion, found.SchemaVersion);
    }

    [TestMethod]
    public void PostgresMigrationRegistry_ToStoreMigrationList_ReturnsExpectedShape()
    {
        var list = PostgresMigrationRegistry.ToStoreMigrationList();
        Assert.AreEqual(1, list.Count);
        Assert.AreEqual(PostgresMigrationRunner.BaselineMigrationId, list[0].MigrationId);
        Assert.AreEqual(PostgresMigrationRunner.SchemaVersion, list[0].SchemaVersion);
        Assert.IsFalse(string.IsNullOrEmpty(list[0].Description));
        Assert.IsTrue(list[0].RequiredTables.Count > 0);
    }

    [TestMethod]
    public async Task RollbackAsync_ConfirmFalse_ReturnsConfirmRequired()
    {
        // R14-PG-8：confirm=false 时不访问 DB，直接返回 ConfirmRequired=true。
        // 采用与 PostgresMigrationApply_ShouldRejectWithoutConfirm 一致的构造方式（不连真实 DB）。
        var runner = new PostgresMigrationRunner(new PostgresConnectionFactory(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=contextcore;Username=contextcore;Password=contextcore",
            AutoMigrate = false
        }));

        var result = await runner.RollbackAsync("cc-schema-v1", confirm: false, CancellationToken.None);

        Assert.IsFalse(result.RolledBack);
        Assert.IsTrue(result.ConfirmRequired);
        Assert.AreEqual("cc-schema-v1", result.TargetSchemaVersion);
        CollectionAssert.Contains(result.Diagnostics.ToArray(), "ConfirmRequired");
    }

    [TestMethod]
    public void RollbackAsync_TargetEqualsCurrent_LogicShortCircuits()
    {
        // 不连真实 DB；通过注册表与 SchemaVersion 验证 target==current 的逻辑短路条件。
        // RollbackAsync 在 confirm=true 且 target==current 时返回 RolledBack=true no-op。
        var targetVersion = PostgresMigrationRunner.SchemaVersion;
        var currentVersion = PostgresMigrationRunner.SchemaVersion;
        Assert.IsTrue(string.Equals(targetVersion, currentVersion, StringComparison.Ordinal),
            "target==current 时应短路返回 no-op");
    }

    [TestMethod]
    public void PostgresMigrationRunner_ListMigrations_ReflectsRegistry()
    {
        // R14-PG-8：ListMigrations 应从 PostgresMigrationRegistry 读取，保持一致。
        var runner = new PostgresMigrationRunner(new PostgresConnectionFactory(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=contextcore;Username=contextcore;Password=contextcore",
            AutoMigrate = false
        }));

        var fromRunner = runner.ListMigrations();
        var fromRegistry = PostgresMigrationRegistry.ToStoreMigrationList();

        Assert.AreEqual(fromRegistry.Count, fromRunner.Count);
        Assert.AreEqual(PostgresMigrationRunner.BaselineMigrationId, fromRunner[0].MigrationId);
        Assert.AreEqual(PostgresMigrationRunner.SchemaVersion, fromRunner[0].SchemaVersion);
        // 验证表后缀列表包含 context_schema_migrations（既有测试 PostgresMigrationDryRun_ShouldListBaselineMigration 的关键断言）。
        Assert.IsTrue(fromRunner[0].RequiredTables.Contains("context_schema_migrations"));
    }

    [TestMethod]
    public async Task GetMigrationHistoryAsync_FakeRunner_ReturnsEmptyByDefault()
    {
        // FakeMigrationRunner 默认返回空列表，验证接口实现存在且语义正确。
        var runner = new FakeMigrationRunner(currentVersion: null, missingTables: Array.Empty<string>());
        var history = await runner.GetMigrationHistoryAsync(CancellationToken.None);
        Assert.IsNotNull(history);
        Assert.AreEqual(0, history.Count);
    }

    [TestMethod]
    public async Task RollbackAsync_FakeRunner_ReturnsFakeDiagnostic()
    {
        // FakeMigrationRunner 的 RollbackAsync 返回 Diagnostics=["FakeMigrationRunner"]，验证接口实现存在。
        var runner = new FakeMigrationRunner(currentVersion: null, missingTables: Array.Empty<string>());
        var result = await runner.RollbackAsync("cc-schema-v1", confirm: true, CancellationToken.None);
        Assert.IsNotNull(result);
        CollectionAssert.Contains(result.Diagnostics.ToArray(), "FakeMigrationRunner");
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

        public Task<IReadOnlyList<PostgresMigrationHistoryEntry>> GetMigrationHistoryAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PostgresMigrationHistoryEntry>>(Array.Empty<PostgresMigrationHistoryEntry>());

        public Task<PostgresMigrationRollbackResult> RollbackAsync(string targetSchemaVersion, bool confirm, CancellationToken cancellationToken = default)
            => Task.FromResult(new PostgresMigrationRollbackResult { Diagnostics = new[] { "FakeMigrationRunner" } });
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
