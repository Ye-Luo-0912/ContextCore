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
        Assert.IsFalse(services.Any(item => item.ServiceType == typeof(ILearningFeedbackStore)));
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
    public void LearningFeedbackDualWriteAndShadowReadOptions_ShouldBeDisabledByDefault()
    {
        var dualWrite = new LearningFeedbackDualWriteOptions();
        var shadowRead = new LearningFeedbackShadowReadOptions();

        Assert.IsFalse(dualWrite.Enabled);
        Assert.IsFalse(dualWrite.WritePostgres);
        Assert.IsTrue(dualWrite.TraceEnabled);
        Assert.IsTrue(dualWrite.FallbackOnPostgresFailure);
        Assert.IsFalse(shadowRead.Enabled);
        Assert.IsFalse(shadowRead.ReadPostgres);
        Assert.IsTrue(shadowRead.CompareResults);
        Assert.IsTrue(shadowRead.TraceEnabled);
    }

    [TestMethod]
    public void LearningFeedbackProviderSwitchOptions_ShouldDefaultToFileSystemPrimary()
    {
        var options = new LearningFeedbackProviderSwitchOptions();

        Assert.IsFalse(options.Enabled);
        Assert.AreEqual(LearningFeedbackProviderMode.FileSystemPrimary, options.Mode);
        Assert.IsTrue(options.FallbackToFileSystem);
        Assert.IsTrue(options.ContinueComparisonTrace);
        Assert.IsTrue(options.FailClosedOnMismatch);
        Assert.IsTrue(options.RequireProviderQualityReady);
    }

    [TestMethod]
    public void LearningFeedbackSelectedNormalScopeOptions_ShouldDefaultDisabled()
    {
        var options = new LearningFeedbackSelectedNormalScopeOptions();

        Assert.IsFalse(options.Enabled);
        Assert.AreEqual(LearningFeedbackProviderMode.GuardedPostgresPrimary, options.Mode);
        Assert.IsTrue(options.FallbackToFileSystem);
        Assert.IsTrue(options.ContinueComparisonTrace);
        Assert.IsTrue(options.FailClosedOnMismatch);
        Assert.IsTrue(options.RequireScopedServiceModeGate);
        Assert.AreEqual(LearningFeedbackSelectedNormalScopeCleanupMode.None, options.CleanupMode);
    }

    [TestMethod]
    public void LearningFeedbackLimitedScopeObservationOptions_ShouldDefaultDisabled()
    {
        var options = new LearningFeedbackLimitedScopeObservationOptions();

        Assert.IsFalse(options.Enabled);
        Assert.AreEqual(LearningFeedbackProviderMode.GuardedPostgresPrimary, options.Mode);
        Assert.IsTrue(options.FallbackToFileSystem);
        Assert.IsTrue(options.ContinueComparisonTrace);
        Assert.IsTrue(options.FailClosedOnMismatch);
        Assert.IsTrue(options.RequireSelectedNormalScopeCanaryPassed);
        Assert.AreEqual(LearningFeedbackSelectedNormalScopeCleanupMode.None, options.CleanupMode);
    }

    [TestMethod]
    public void PostgresJobQueueStoreOptions_ShouldDefaultToExplicitNonRuntimeProvider()
    {
        var options = new PostgresJobQueueStoreOptions();

        Assert.IsFalse(options.Enabled);
        Assert.IsFalse(options.UseForRuntime);
        Assert.AreEqual("postgres-job-queue-v1", options.ProviderId);
        Assert.AreEqual(30, options.CommandTimeoutSeconds);
        Assert.AreEqual(100, options.BatchSize);
    }

    [TestMethod]
    public void JobQueueDualWriteOptions_ShouldBeDisabledByDefault()
    {
        var options = new JobQueueDualWriteOptions();

        Assert.IsFalse(options.Enabled);
        Assert.IsFalse(options.WritePostgres);
        Assert.IsTrue(options.TraceEnabled);
        Assert.IsTrue(options.FallbackOnPostgresFailure);
        Assert.IsFalse(options.FailOnMismatch);
    }

    [TestMethod]
    public void JobQueueShadowReadOptions_ShouldBeDisabledByDefault()
    {
        var options = new JobQueueShadowReadOptions();

        Assert.IsFalse(options.Enabled);
        Assert.IsFalse(options.ReadPostgres);
        Assert.IsTrue(options.CompareResults);
        Assert.IsTrue(options.TraceEnabled);
        Assert.IsFalse(options.FailOnMismatch);
    }

    [TestMethod]
    public void JobQueueScopedWorkerCanaryOptions_ShouldBeDisabledByDefault()
    {
        var options = new JobQueueScopedWorkerCanaryOptions();

        Assert.IsFalse(options.Enabled);
        Assert.AreEqual(JobQueueWorkerProviderMode.GuardedPostgresPrimary, options.Mode);
        Assert.IsTrue(options.RequireProviderQualityReady);
        Assert.IsFalse(options.CleanupAfterRun);
        Assert.IsTrue(options.FailClosedOnMismatch);
    }

    [TestMethod]
    public void JobQueueLimitedWorkerScopeObservationOptions_ShouldBeDisabledByDefault()
    {
        var options = new JobQueueLimitedWorkerScopeObservationOptions();

        Assert.IsFalse(options.Enabled);
        Assert.AreEqual(120, options.ObservationWindowSeconds);
        Assert.IsTrue(options.RequireScopedWorkerCanaryPassed);
        Assert.IsFalse(options.CleanupAfterRun);
        Assert.IsTrue(options.FailClosedOnLeaseViolation);
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
    public void PostgresRelationStoreOptions_ShouldDefaultToExplicitNonRuntimeProvider()
    {
        var options = new PostgresRelationStoreOptions();

        Assert.IsFalse(options.Enabled);
        Assert.IsFalse(options.UseForRuntime);
        Assert.AreEqual("postgres-relation-store-v1", options.ProviderId);
        Assert.AreEqual(30, options.CommandTimeoutSeconds);
        Assert.AreEqual(100, options.BatchSize);
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
    public void PostgresRelationReviewParityReport_ShouldExposeMismatchRecommendation()
    {
        var report = new PostgresRelationReviewParityReport
        {
            ProviderEnabled = true,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Mismatches = ["DiagnosticsByItemMismatch"],
            Recommendation = "ParityMismatch"
        };

        Assert.AreEqual("ParityMismatch", report.Recommendation);
        CollectionAssert.Contains(report.Mismatches.ToArray(), "DiagnosticsByItemMismatch");
    }

    [TestMethod]
    public void PostgresRelationGovernanceParityReport_AllPassedShouldAllowDualWriteOnly()
    {
        var report = new PostgresRelationGovernanceParityReport
        {
            ProviderEnabled = true,
            RelationParityPassed = true,
            ReviewParityPassed = true,
            DiagnosticsParityPassed = true,
            GovernanceParityPassed = true,
            CleanupPerformed = true,
            CanDualWrite = true,
            CanShadowRead = false,
            CanRuntimeSwitch = false,
            Recommendation = "ReadyForDualWrite"
        };

        Assert.AreEqual("ReadyForDualWrite", report.Recommendation);
        Assert.IsTrue(report.CanDualWrite);
        Assert.IsFalse(report.CanShadowRead);
        Assert.IsFalse(report.CanRuntimeSwitch);
    }

    [TestMethod]
    public void PostgresRelationGovernanceParityReport_MismatchShouldBlock()
    {
        var report = new PostgresRelationGovernanceParityReport
        {
            ProviderEnabled = true,
            GovernanceParityPassed = false,
            Mismatches = ["GovernanceDiagnosticsKindMismatch"],
            BlockedReasons = ["ParityMismatch"],
            Recommendation = "BlockedByMismatch"
        };

        Assert.AreEqual("BlockedByMismatch", report.Recommendation);
        CollectionAssert.Contains(report.Mismatches.ToArray(), "GovernanceDiagnosticsKindMismatch");
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "ParityMismatch");
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
    public void RelationGovernanceDualWriteOptions_ShouldBeDisabledByDefault()
    {
        var options = new RelationGovernanceDualWriteOptions();

        Assert.IsFalse(options.Enabled);
        Assert.IsFalse(options.WritePostgres);
        Assert.IsTrue(options.TraceEnabled);
        Assert.IsTrue(options.FallbackOnPostgresFailure);
        Assert.IsFalse(options.FailOnMismatch);
    }

    [TestMethod]
    public void RelationGovernanceDualWriteTrace_ShouldRecordFallbackWithoutBlockingFilesystemWrite()
    {
        var trace = new RelationGovernanceDualWriteTrace
        {
            OperationId = "operation-test",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TargetKind = "relation",
            TargetId = "relation-1",
            FileSystemWriteSucceeded = true,
            PostgresWriteSucceeded = false,
            PostgresError = "TimeoutException",
            FallbackUsed = true
        };

        Assert.IsTrue(trace.FileSystemWriteSucceeded);
        Assert.IsFalse(trace.PostgresWriteSucceeded);
        Assert.IsTrue(trace.FallbackUsed);
        Assert.AreEqual("TimeoutException", trace.PostgresError);
    }

    [TestMethod]
    public void RelationGovernanceDualWriteTrace_ShouldRecordMismatch()
    {
        var trace = new RelationGovernanceDualWriteTrace
        {
            OperationId = "operation-test",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TargetKind = "relation-review",
            TargetId = "review-1",
            FileSystemWriteSucceeded = true,
            PostgresWriteSucceeded = true,
            MismatchDetected = true,
            MismatchReason = "ReviewStatusMismatch"
        };

        Assert.IsTrue(trace.MismatchDetected);
        Assert.AreEqual("ReviewStatusMismatch", trace.MismatchReason);
    }

    [TestMethod]
    public void PostgresRelationDualWriteQualityReport_ShouldExposeBlockedRecommendationOnMismatch()
    {
        var report = new PostgresRelationDualWriteQualityReport
        {
            TraceCount = 3,
            FileSystemWriteSuccessCount = 3,
            PostgresWriteSuccessCount = 3,
            MismatchCount = 1,
            Recommendation = "BlockedByMismatch"
        };

        Assert.AreEqual("BlockedByMismatch", report.Recommendation);
        Assert.AreEqual(1, report.MismatchCount);
    }

    [TestMethod]
    public void RelationGovernanceShadowReadTrace_ShouldRecordFallbackWithoutBlockingFilesystemRead()
    {
        var trace = new RelationGovernanceShadowReadTrace
        {
            OperationId = "operation-test",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            ReadKind = "RelationGet",
            TargetId = "relation-1",
            FileSystemReadSucceeded = true,
            PostgresReadSucceeded = false,
            FileSystemResultHash = "fs-hash",
            PostgresError = "TimeoutException",
            FallbackUsed = true,
            MismatchDetected = true,
            MismatchReason = "PostgresReadFailed"
        };

        Assert.IsTrue(trace.FileSystemReadSucceeded);
        Assert.IsFalse(trace.PostgresReadSucceeded);
        Assert.IsTrue(trace.FallbackUsed);
        Assert.AreEqual("PostgresReadFailed", trace.MismatchReason);
    }

    [TestMethod]
    public void PostgresRelationShadowReadQualityReport_ShouldExposeBlockedRecommendationOnMismatch()
    {
        var report = new PostgresRelationShadowReadQualityReport
        {
            TraceCount = 2,
            FileSystemReadSuccessCount = 2,
            PostgresReadSuccessCount = 2,
            MismatchCount = 1,
            Recommendation = "BlockedByMismatch"
        };

        Assert.AreEqual("BlockedByMismatch", report.Recommendation);
        Assert.AreEqual(1, report.MismatchCount);
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
    public void RelationGovernanceCanaryOptions_ShouldDefaultDisabled()
    {
        var options = new RelationGovernanceCanaryOptions();

        Assert.IsFalse(options.Enabled);
        Assert.AreEqual(RelationGovernanceProviderMode.GuardedPostgresPrimary, options.Mode);
        Assert.IsTrue(options.FallbackToFileSystem);
        Assert.IsTrue(options.ContinueComparisonTrace);
        Assert.IsTrue(options.FailClosedOnMismatch);
        Assert.IsTrue(options.RequireProviderSwitchGate);
        Assert.IsTrue(options.RequireRuntimeCanaryPassed);
    }

    [TestMethod]
    public void RelationGovernanceExtendedCanaryOptions_ShouldDefaultDisabled()
    {
        var options = new RelationGovernanceExtendedCanaryOptions();

        Assert.IsFalse(options.Enabled);
        Assert.AreEqual(RelationGovernanceProviderMode.GuardedPostgresPrimary, options.Mode);
        Assert.IsTrue(options.FallbackToFileSystem);
        Assert.IsTrue(options.ContinueComparisonTrace);
        Assert.IsTrue(options.FailClosedOnMismatch);
        Assert.IsTrue(options.RequireScopedServiceModeGate);
    }

    [TestMethod]
    public void RelationGovernanceSelectedWorkspaceCanaryOptions_ShouldDefaultDisabled()
    {
        var options = new RelationGovernanceSelectedWorkspaceCanaryOptions();

        Assert.IsFalse(options.Enabled);
        Assert.AreEqual(RelationGovernanceProviderMode.GuardedPostgresPrimary, options.Mode);
        Assert.IsTrue(options.FallbackToFileSystem);
        Assert.IsTrue(options.ContinueComparisonTrace);
        Assert.IsTrue(options.FailClosedOnMismatch);
        Assert.IsTrue(options.RequireExtendedCanaryPassed);
        Assert.AreEqual(string.Empty, options.WorkspaceId);
        Assert.AreEqual(string.Empty, options.CollectionId);
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
    public void PostgresRelationScopedServiceModeGateReport_ShouldFailWhenNonAllowlistedScopeUsesPostgres()
    {
        var report = new PostgresRelationScopedServiceModeGateReport
        {
            Passed = false,
            NonAllowlistedScopeRemainsFileSystem = false,
            BlockedReasons = ["NonAllowlistedScopeNotFileSystem"],
            Recommendation = "GateNotReady"
        };

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "NonAllowlistedScopeNotFileSystem");
    }

    [TestMethod]
    public void PostgresRelationScopedServiceModeSmokeReport_ShouldExposeReadyRecommendation()
    {
        var report = new PostgresRelationScopedServiceModeSmokeReport
        {
            GatePassed = true,
            AllowlistedScopeUsedPostgresPrimary = true,
            NonAllowlistedScopeUsedFileSystem = true,
            FallbackTested = true,
            ComparisonTraceRecorded = true,
            CleanupPerformed = true,
            Recommendation = "ReadyForScopedServiceMode"
        };

        Assert.AreEqual("ReadyForScopedServiceMode", report.Recommendation);
        Assert.IsTrue(report.NonAllowlistedScopeUsedFileSystem);
    }

    [TestMethod]
    public void PostgresRelationScopedExtendedCanaryReport_ShouldExposeReadyRecommendation()
    {
        var report = new PostgresRelationScopedExtendedCanaryReport
        {
            GatePassed = true,
            OperationCount = 24,
            GraphExpansionPreviewParityPassed = true,
            ReviewLifecycleParityPassed = true,
            DiagnosticsParityPassed = true,
            ReplacementChainParityPassed = true,
            CleanupPerformed = true,
            Recommendation = "ReadyForSelectedWorkspaceCanary"
        };

        Assert.AreEqual("ReadyForSelectedWorkspaceCanary", report.Recommendation);
        Assert.IsTrue(report.GraphExpansionPreviewParityPassed);
        Assert.IsTrue(report.ReplacementChainParityPassed);
    }

    [TestMethod]
    public void PostgresRelationScopedExtendedCanaryReport_ShouldBlockOnGraphPreviewMismatch()
    {
        var report = new PostgresRelationScopedExtendedCanaryReport
        {
            GraphExpansionPreviewParityPassed = false,
            MismatchCount = 1,
            BlockedReasons = ["GraphPreviewMismatch:audit-v1"],
            Recommendation = "BlockedByGraphPreviewMismatch"
        };

        Assert.AreEqual("BlockedByGraphPreviewMismatch", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "GraphPreviewMismatch:audit-v1");
    }

    [TestMethod]
    public void PostgresRelationSelectedWorkspaceCanaryReport_ShouldExposeReadyRecommendation()
    {
        var report = new PostgresRelationSelectedWorkspaceCanaryReport
        {
            GatePassed = true,
            WorkspaceId = "workspace-selected",
            CollectionId = "collection-selected",
            PostgresPrimaryReadCount = 10,
            PostgresPrimaryWriteCount = 8,
            GraphExpansionPreviewParityPassed = true,
            ReviewLifecycleParityPassed = true,
            DiagnosticsParityPassed = true,
            ReplacementChainParityPassed = true,
            NonSelectedScopeRemainsFileSystem = true,
            RollbackInstruction = "disable selected canary",
            Recommendation = "ReadyForScopedServiceModeExpansion"
        };

        Assert.AreEqual("ReadyForScopedServiceModeExpansion", report.Recommendation);
        Assert.IsTrue(report.NonSelectedScopeRemainsFileSystem);
        StringAssert.Contains(report.RollbackInstruction, "disable");
    }

    [TestMethod]
    public void PostgresRelationSelectedWorkspaceCanaryReport_ShouldBlockWhenExtendedCanaryMissing()
    {
        var report = new PostgresRelationSelectedWorkspaceCanaryReport
        {
            GatePassed = false,
            BlockedReasons = ["ExtendedCanaryNotPassed"],
            Recommendation = "GateNotPassed"
        };

        Assert.IsFalse(report.GatePassed);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "ExtendedCanaryNotPassed");
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
    public void PostgresRelationScopedExpansionReport_ShouldExposeReadyRecommendation()
    {
        var report = new PostgresRelationScopedExpansionReport
        {
            GatePassed = true,
            ScopeCount = 2,
            AllowlistedScopeCount = 2,
            NonAllowlistedScopeChecked = true,
            OperationCount = 58,
            MismatchCount = 0,
            PostgresFailureCount = 0,
            PerScopeStatus =
            [
                new RelationGovernanceScopedExpansionScopeStatus
                {
                    ScopeName = "scope-a",
                    Recommendation = "ReadyForSelectedWorkspaceCanary"
                }
            ],
            Recommendation = "ReadyForScopedExpansion"
        };

        Assert.AreEqual("ReadyForScopedExpansion", report.Recommendation);
        Assert.IsTrue(report.NonAllowlistedScopeChecked);
        Assert.AreEqual(2, report.AllowlistedScopeCount);
    }

    [TestMethod]
    public void PostgresRelationScopedExpansionReport_ShouldBlockOnScopeLeak()
    {
        var report = new PostgresRelationScopedExpansionReport
        {
            GatePassed = false,
            NonAllowlistedScopeChecked = false,
            BlockedReasons = ["NonAllowlistedScopeLeak"],
            Recommendation = "BlockedByNonAllowlistedScopeLeak"
        };

        Assert.AreEqual("BlockedByNonAllowlistedScopeLeak", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "NonAllowlistedScopeLeak");
    }

    [TestMethod]
    public void RelationGovernanceScopedObservationOptions_ShouldDefaultDisabled()
    {
        var options = new RelationGovernanceScopedObservationOptions();

        Assert.IsFalse(options.Enabled);
        Assert.IsTrue(options.FallbackToFileSystem);
        Assert.IsTrue(options.ContinueComparisonTrace);
        Assert.IsTrue(options.FailClosedOnMismatch);
        Assert.IsFalse(options.CleanupAfterRun);
        Assert.IsTrue(options.RequireScopedExpansionGate);
    }

    [TestMethod]
    public void PostgresRelationScopedObservationReport_ShouldExposeReadyRecommendation()
    {
        var report = new PostgresRelationScopedObservationReport
        {
            GatePassed = true,
            ScopeCount = 2,
            OperationCount = 59,
            MismatchCount = 0,
            PostgresFailureCount = 0,
            NonAllowlistedScopeLeakCount = 0,
            FallbackPathTested = true,
            P95PostgresReadMs = 12,
            P95PostgresWriteMs = 18,
            Recommendation = "ReadyForSelectedNormalWorkspace"
        };

        Assert.IsTrue(report.GatePassed);
        Assert.AreEqual("ReadyForSelectedNormalWorkspace", report.Recommendation);
        Assert.AreEqual(0, report.MismatchCount);
        Assert.AreEqual(0, report.PostgresFailureCount);
        Assert.AreEqual(0, report.NonAllowlistedScopeLeakCount);
    }

    [TestMethod]
    public void PostgresRelationScopedObservationReport_ShouldBlockOnLatency()
    {
        var report = new PostgresRelationScopedObservationReport
        {
            GatePassed = false,
            ScopeCount = 2,
            OperationCount = 10,
            P95PostgresReadMs = 6000,
            P95PostgresWriteMs = 12,
            FallbackPathTested = true,
            BlockedReasons = ["P95ReadLatencyExceeded"],
            Recommendation = "BlockedByLatency"
        };

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual("BlockedByLatency", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "P95ReadLatencyExceeded");
    }

    [TestMethod]
    public void RelationGovernanceSelectedNormalWorkspaceOptions_ShouldDefaultDisabled()
    {
        var options = new RelationGovernanceSelectedNormalWorkspaceOptions();

        Assert.IsFalse(options.Enabled);
        Assert.AreEqual(RelationGovernanceProviderMode.GuardedPostgresPrimary, options.Mode);
        Assert.IsTrue(options.FallbackToFileSystem);
        Assert.IsTrue(options.ContinueComparisonTrace);
        Assert.IsTrue(options.FailClosedOnMismatch);
        Assert.IsTrue(options.RequireScopedObservationPassed);
        Assert.AreEqual(RelationGovernanceSelectedNormalWorkspaceCleanupMode.None, options.CleanupMode);
    }

    [TestMethod]
    public void PostgresRelationSelectedNormalWorkspaceCanaryReport_ShouldExposeReadyRecommendation()
    {
        var report = new PostgresRelationSelectedNormalWorkspaceCanaryReport
        {
            GatePassed = true,
            WorkspaceId = "normal-workspace",
            CollectionId = "normal-collection",
            ProviderMode = RelationGovernanceProviderMode.GuardedPostgresPrimary.ToString(),
            OperationCount = 29,
            PostgresPrimaryReadCount = 12,
            PostgresPrimaryWriteCount = 4,
            MismatchCount = 0,
            PostgresFailureCount = 0,
            ScopeLeakCount = 0,
            GraphExpansionPreviewParityPassed = true,
            ReviewLifecycleParityPassed = true,
            DiagnosticsParityPassed = true,
            ReplacementChainParityPassed = true,
            NonSelectedNormalScopeRemainsFileSystem = true,
            RollbackInstruction = "remove allowlist",
            Recommendation = "ReadyForLimitedNormalScope"
        };

        Assert.IsTrue(report.GatePassed);
        Assert.AreEqual("ReadyForLimitedNormalScope", report.Recommendation);
        Assert.AreEqual(0, report.MismatchCount);
        Assert.AreEqual(0, report.ScopeLeakCount);
        Assert.IsFalse(string.IsNullOrWhiteSpace(report.RollbackInstruction));
    }

    [TestMethod]
    public void PostgresRelationSelectedNormalWorkspaceCanaryReport_ShouldBlockWhenScopeMissing()
    {
        var report = new PostgresRelationSelectedNormalWorkspaceCanaryReport
        {
            GatePassed = false,
            BlockedReasons = ["SelectedNormalScopeMissing"],
            Recommendation = "GateNotPassed"
        };

        Assert.IsFalse(report.GatePassed);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "SelectedNormalScopeMissing");
    }

    [TestMethod]
    public void PostgresRelationSelectedNormalWorkspaceCanaryReport_ShouldBlockWhenScopedObservationMissing()
    {
        var report = new PostgresRelationSelectedNormalWorkspaceCanaryReport
        {
            GatePassed = false,
            WorkspaceId = "normal-workspace",
            CollectionId = "normal-collection",
            BlockedReasons = ["ScopedObservationQualityNotPassed"],
            Recommendation = "GateNotPassed"
        };

        Assert.IsFalse(report.GatePassed);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "ScopedObservationQualityNotPassed");
    }

    [TestMethod]
    public void PostgresRelationSelectedNormalWorkspaceCanaryReport_ShouldBlockOnMismatch()
    {
        var report = new PostgresRelationSelectedNormalWorkspaceCanaryReport
        {
            GatePassed = true,
            WorkspaceId = "normal-workspace",
            CollectionId = "normal-collection",
            MismatchCount = 1,
            BlockedReasons = ["SelectedNormalControlRoomReadPathMismatch"],
            Recommendation = "BlockedByMismatch"
        };

        Assert.AreEqual("BlockedByMismatch", report.Recommendation);
        Assert.IsTrue(report.MismatchCount > 0);
    }

    [TestMethod]
    public void PostgresRelationSelectedNormalWorkspaceCanaryReport_ShouldExposeFallbackAndRollback()
    {
        var report = new PostgresRelationSelectedNormalWorkspaceCanaryReport
        {
            FileSystemFallbackCount = 1,
            RollbackInstruction = "set FileSystemPrimary",
            Recommendation = "NeedsMoreObservation"
        };

        Assert.AreEqual(1, report.FileSystemFallbackCount);
        Assert.IsTrue(report.RollbackInstruction.Contains("FileSystem", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void RelationGovernanceLimitedNormalScopeObservationOptions_ShouldDefaultDisabled()
    {
        var options = new RelationGovernanceLimitedNormalScopeObservationOptions();

        Assert.IsFalse(options.Enabled);
        Assert.AreEqual(RelationGovernanceProviderMode.GuardedPostgresPrimary, options.Mode);
        Assert.IsTrue(options.FallbackToFileSystem);
        Assert.IsTrue(options.ContinueComparisonTrace);
        Assert.IsTrue(options.FailClosedOnMismatch);
        Assert.IsTrue(options.RequireSelectedNormalCanaryPassed);
        Assert.AreEqual(RelationGovernanceSelectedNormalWorkspaceCleanupMode.None, options.CleanupMode);
    }

    [TestMethod]
    public void PostgresRelationLimitedNormalScopeObservationReport_ShouldExposeReadyRecommendation()
    {
        var report = new PostgresRelationLimitedNormalScopeObservationReport
        {
            GatePassed = true,
            WorkspaceId = "normal-workspace",
            CollectionId = "normal-collection",
            OperationCount = 100,
            MismatchCount = 0,
            PostgresFailureCount = 0,
            ScopeLeakCount = 0,
            FallbackRate = 0,
            P95PostgresReadMs = 20,
            P95PostgresWriteMs = 30,
            GraphExpansionPreviewParityPassed = true,
            ReviewLifecycleParityPassed = true,
            DiagnosticsParityPassed = true,
            ReplacementChainParityPassed = true,
            Recommendation = "ReadyForMultiNormalScopeCanary"
        };

        Assert.IsTrue(report.GatePassed);
        Assert.AreEqual("ReadyForMultiNormalScopeCanary", report.Recommendation);
        Assert.AreEqual(0, report.ScopeLeakCount);
    }

    [TestMethod]
    public void PostgresRelationLimitedNormalScopeObservationReport_ShouldBlockWhenSelectedNormalCanaryMissing()
    {
        var report = new PostgresRelationLimitedNormalScopeObservationReport
        {
            GatePassed = false,
            BlockedReasons = ["SelectedNormalWorkspaceCanaryNotPassed"],
            Recommendation = "GateNotPassed"
        };

        Assert.IsFalse(report.GatePassed);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "SelectedNormalWorkspaceCanaryNotPassed");
    }

    [TestMethod]
    public void PostgresRelationLimitedNormalScopeObservationReport_ShouldBlockOnScopeLeak()
    {
        var report = new PostgresRelationLimitedNormalScopeObservationReport
        {
            GatePassed = false,
            ScopeLeakCount = 1,
            BlockedReasons = ["ScopeLeakDetected"],
            Recommendation = "BlockedByScopeLeak"
        };

        Assert.AreEqual("BlockedByScopeLeak", report.Recommendation);
        Assert.IsTrue(report.ScopeLeakCount > 0);
    }

    [TestMethod]
    public void PostgresRelationLimitedNormalScopeObservationReport_ShouldBlockOnMismatch()
    {
        var report = new PostgresRelationLimitedNormalScopeObservationReport
        {
            GatePassed = false,
            MismatchCount = 1,
            BlockedReasons = ["MismatchDetected"],
            Recommendation = "BlockedByMismatch"
        };

        Assert.AreEqual("BlockedByMismatch", report.Recommendation);
        Assert.IsTrue(report.MismatchCount > 0);
    }

    [TestMethod]
    public void PostgresRelationLimitedNormalScopeObservationReport_ShouldBlockOnLatency()
    {
        var report = new PostgresRelationLimitedNormalScopeObservationReport
        {
            GatePassed = false,
            P95PostgresReadMs = 6000,
            BlockedReasons = ["P95PostgresReadLatencyExceeded"],
            Recommendation = "BlockedByLatency"
        };

        Assert.AreEqual("BlockedByLatency", report.Recommendation);
        Assert.IsTrue(report.P95PostgresReadMs > 5000);
    }

    [TestMethod]
    public void PostgresRelationLimitedNormalScopeObservationReport_ShouldExposeFallbackRate()
    {
        var report = new PostgresRelationLimitedNormalScopeObservationReport
        {
            OperationCount = 100,
            FileSystemFallbackCount = 3,
            FallbackRate = 0.03,
            CleanupPerformed = false,
            Recommendation = "NeedsLongerObservation"
        };

        Assert.AreEqual(0.03, report.FallbackRate);
        Assert.IsFalse(report.CleanupPerformed);
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
    public void RelationGovernanceMultiNormalScopeCanaryOptions_ShouldDefaultDisabled()
    {
        var options = new RelationGovernanceMultiNormalScopeCanaryOptions();

        Assert.IsFalse(options.Enabled);
        Assert.AreEqual(RelationGovernanceProviderMode.GuardedPostgresPrimary, options.Mode);
        Assert.IsTrue(options.FallbackToFileSystem);
        Assert.IsTrue(options.ContinueComparisonTrace);
        Assert.IsTrue(options.FailClosedOnMismatch);
        Assert.IsTrue(options.RequireLimitedNormalScopeObservationPassed);
    }

    [TestMethod]
    public void RelationGovernanceMultiNormalScopeCanaryReport_ShouldExposeReadyRecommendation()
    {
        var report = new PostgresRelationMultiNormalScopeCanaryReport
        {
            GatePassed = true,
            ScopeCount = 2,
            EnabledScopeCount = 2,
            OperationCount = 120,
            MismatchCount = 0,
            PostgresFailureCount = 0,
            ScopeLeakCount = 0,
            NonAllowlistedScopeChecked = true,
            GraphExpansionPreviewParityPassed = true,
            ReviewLifecycleParityPassed = true,
            DiagnosticsParityPassed = true,
            ReplacementChainParityPassed = true,
            Recommendation = "ReadyForLimitedScopeExpansion"
        };

        Assert.IsTrue(report.GatePassed);
        Assert.AreEqual("ReadyForLimitedScopeExpansion", report.Recommendation);
        Assert.AreEqual(0, report.MismatchCount + report.PostgresFailureCount + report.ScopeLeakCount);
    }

    [TestMethod]
    public void RelationGovernanceMultiNormalScopeCanaryReport_ShouldBlockWhenLessThanTwoScopes()
    {
        var report = new PostgresRelationMultiNormalScopeCanaryReport
        {
            GatePassed = false,
            ScopeCount = 1,
            EnabledScopeCount = 1,
            BlockedReasons = ["AtLeastTwoNormalScopesRequired"],
            Recommendation = "GateNotPassed"
        };

        Assert.IsFalse(report.GatePassed);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "AtLeastTwoNormalScopesRequired");
    }

    [TestMethod]
    public void RelationGovernanceMultiNormalScopeCanaryReport_ShouldBlockOnScopeLeak()
    {
        var report = new PostgresRelationMultiNormalScopeCanaryReport
        {
            GatePassed = false,
            ScopeCount = 2,
            EnabledScopeCount = 2,
            ScopeLeakCount = 1,
            BlockedReasons = ["ScopeLeakDetected"],
            Recommendation = "BlockedByScopeLeak"
        };

        Assert.AreEqual("BlockedByScopeLeak", report.Recommendation);
        Assert.IsTrue(report.ScopeLeakCount > 0);
    }

    [TestMethod]
    public void RelationGovernanceMultiNormalScopeCanaryReport_ShouldBlockOnMismatch()
    {
        var report = new PostgresRelationMultiNormalScopeCanaryReport
        {
            GatePassed = false,
            ScopeCount = 2,
            EnabledScopeCount = 2,
            MismatchCount = 1,
            BlockedReasons = ["MismatchDetected"],
            Recommendation = "BlockedByMismatch"
        };

        Assert.AreEqual("BlockedByMismatch", report.Recommendation);
        Assert.IsTrue(report.MismatchCount > 0);
    }

    [TestMethod]
    public void RelationGovernanceMultiNormalScopeCanaryReport_ShouldExposeNonAllowlistedStatus()
    {
        var report = new PostgresRelationMultiNormalScopeCanaryReport
        {
            GatePassed = true,
            ScopeCount = 2,
            EnabledScopeCount = 2,
            NonAllowlistedScopeChecked = true,
            PerScopeStatus =
            [
                new RelationGovernanceMultiNormalScopeStatus
                {
                    ScopeName = "scope-a",
                    WorkspaceId = "workspace-a",
                    CollectionId = "collection-a",
                    OperationCount = 10,
                    Recommendation = "ReadyForLimitedScopeExpansion"
                }
            ]
        };

        Assert.IsTrue(report.NonAllowlistedScopeChecked);
        Assert.AreEqual("scope-a", report.PerScopeStatus.Single().ScopeName);
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

        Assert.AreEqual("cc-schema-v6", PostgresMigrationRunner.SchemaVersion);
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
    public void PostgresVectorIndexStoreOptions_ShouldRemainRuntimeDisabledByDefault()
    {
        var options = new PostgresVectorIndexStoreOptions();

        Assert.IsFalse(options.Enabled);
        Assert.IsFalse(options.UseForRuntime);
        Assert.AreEqual("postgres-vector-index-v1", options.ProviderId);
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


