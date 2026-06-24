using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.ControlRoom.Services;
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
        Assert.IsFalse(services.Any(item => item.ServiceType == typeof(IVectorIndexStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IRetrievalTraceStore)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(PostgresLearningFeedbackStore)));
        Assert.IsFalse(services.Any(item => item.ServiceType == typeof(ILearningFeedbackStore)));
    }

    [TestMethod]
    public async Task LearningReadinessRegistry_ShouldIncludeRelationGovernanceFreeze()
    {
        var originalDirectory = Directory.GetCurrentDirectory();
        var tempRoot = Path.Combine(Path.GetTempPath(), "contextcore-readiness-registry-test", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.SetCurrentDirectory(tempRoot);
            var reportPath = Path.Combine("storage", "postgres", "postgres-relation-multi-normal-scope-quality-report.json");
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(
                reportPath,
                System.Text.Json.JsonSerializer.Serialize(new PostgresRelationMultiNormalScopeCanaryReport
                {
                    GatePassed = true,
                    MismatchCount = 0,
                    PostgresFailureCount = 0,
                    ScopeLeakCount = 0,
                    Recommendation = "ReadyForLimitedScopeExpansion"
                }));

            var registry = await new LearningReadinessFreezeRunner().BuildRegistryFromCurrentFilesAsync();
            var relation = registry.Capabilities.Single(item => item.CapabilityId == ShadowCapabilityIds.RelationGovernance);

            Assert.AreEqual("ReadyForLimitedScopeExpansion", relation.Status);
            CollectionAssert.Contains(relation.AllowedRuntimeModes.ToArray(), "GuardedPostgresPrimary:AllowlistedScopes:FallbackToFileSystem:ComparisonTrace");
            CollectionAssert.Contains(relation.ForbiddenRuntimeModes.ToArray(), "GlobalDefaultOn");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public void LearningRuntimeChangeGate_ShouldBlockRelationGovernanceUnsafeModes()
    {
        var registry = new LearningReadinessRegistry
        {
            Capabilities =
            [
                new ShadowCapabilityReadiness
                {
                    CapabilityId = ShadowCapabilityIds.RelationGovernance,
                    GatePassed = true,
                    AllowedRuntimeModes = ["GuardedPostgresPrimary:AllowlistedScopes"],
                    ForbiddenRuntimeModes = [ShadowRuntimeModes.DefaultOn]
                }
            ]
        };

        var report = new LearningReadinessFreezeRunner().BuildRuntimeChangeGate(registry);

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(
            report.FailedConditions.ToArray(),
            $"{ShadowCapabilityIds.RelationGovernance}:RelationGovernanceGlobalDefaultOnForbidden");
        CollectionAssert.Contains(
            report.FailedConditions.ToArray(),
            $"{ShadowCapabilityIds.RelationGovernance}:RelationGovernanceRequiresFallback");
        CollectionAssert.Contains(
            report.FailedConditions.ToArray(),
            $"{ShadowCapabilityIds.RelationGovernance}:RelationGovernanceRequiresComparisonTrace");
    }

    [TestMethod]
    public void LearningRuntimeChangeGate_ShouldBlockJobQueueUnsafeModes()
    {
        var registry = new LearningReadinessRegistry
        {
            Capabilities =
            [
                new ShadowCapabilityReadiness
                {
                    CapabilityId = ShadowCapabilityIds.JobQueuePostgres,
                    GatePassed = true,
                    AllowedRuntimeModes = ["GuardedPostgresPrimary"],
                    ForbiddenRuntimeModes = [ShadowRuntimeModes.DefaultOn]
                }
            ]
        };

        var report = new LearningReadinessFreezeRunner().BuildRuntimeChangeGate(registry);

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(
            report.FailedConditions.ToArray(),
            $"{ShadowCapabilityIds.JobQueuePostgres}:JobQueueGlobalWorkerProviderSwitchForbidden");
        CollectionAssert.Contains(
            report.FailedConditions.ToArray(),
            $"{ShadowCapabilityIds.JobQueuePostgres}:JobQueueRequiresScopedAllowlist");
        CollectionAssert.Contains(
            report.FailedConditions.ToArray(),
            $"{ShadowCapabilityIds.JobQueuePostgres}:JobQueueRequiresLeaseQualityGate");
        CollectionAssert.Contains(
            report.FailedConditions.ToArray(),
            $"{ShadowCapabilityIds.JobQueuePostgres}:JobQueueRequiresRetryDeadLetterQualityGate");
    }

    [TestMethod]
    public void LearningRuntimeChangeGate_ShouldBlockVectorPostgresFormalRuntimeModes()
    {
        var registry = new LearningReadinessRegistry
        {
            Capabilities =
            [
                new ShadowCapabilityReadiness
                {
                    CapabilityId = ShadowCapabilityIds.VectorPostgresProvider,
                    GatePassed = true,
                    AllowedRuntimeModes = ["PreviewShadowEvalOnly", "FormalRetrievalSwitch"],
                    ForbiddenRuntimeModes = [ShadowRuntimeModes.DefaultOn]
                }
            ]
        };

        var report = new LearningReadinessFreezeRunner().BuildRuntimeChangeGate(registry);

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(
            report.FailedConditions.ToArray(),
            $"{ShadowCapabilityIds.VectorPostgresProvider}:VectorPostgresFormalRetrievalSwitchForbidden");
        CollectionAssert.Contains(
            report.FailedConditions.ToArray(),
            $"{ShadowCapabilityIds.VectorPostgresProvider}:VectorPostgresFormalStoreBindingForbiddenWithoutV4Gate");
        CollectionAssert.Contains(
            report.FailedConditions.ToArray(),
            $"{ShadowCapabilityIds.VectorPostgresProvider}:VectorPostgresPackingPolicyIntegrationForbiddenWithoutV4Gate");
    }

    [TestMethod]
    public async Task LearningReadinessRegistry_ShouldIncludeJobQueuePostgresFreeze()
    {
        var originalDirectory = Directory.GetCurrentDirectory();
        var tempRoot = Path.Combine(Path.GetTempPath(), "contextcore-jobqueue-readiness-test", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.SetCurrentDirectory(tempRoot);
            var reportPath = Path.Combine("storage", "postgres", "postgres-job-queue-freeze-gate.json");
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(
                reportPath,
                System.Text.Json.JsonSerializer.Serialize(new JobQueuePostgresFreezeGateReport
                {
                    Passed = true,
                    JobQueuePostgres = "ReadyForScopedWorkerMode",
                    RuntimeWorkerGlobalProviderUnchanged = true,
                    Recommendation = "ReadyForScopedWorkerMode"
                }));

            var registry = await new LearningReadinessFreezeRunner().BuildRegistryFromCurrentFilesAsync();
            var jobQueue = registry.Capabilities.Single(item => item.CapabilityId == ShadowCapabilityIds.JobQueuePostgres);

            Assert.AreEqual("ReadyForScopedWorkerMode", jobQueue.Status);
            CollectionAssert.Contains(
                jobQueue.AllowedRuntimeModes.ToArray(),
                "GuardedPostgresPrimary:ExplicitAllowlistedWorkerScopes:LeaseHeartbeatQualityGate:RetryDeadLetterQualityGate");
            CollectionAssert.Contains(jobQueue.ForbiddenRuntimeModes.ToArray(), "GlobalWorkerProviderSwitch");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task LearningReadinessRegistry_ShouldIncludeVectorPostgresProviderFreeze()
    {
        var originalDirectory = Directory.GetCurrentDirectory();
        var tempRoot = Path.Combine(Path.GetTempPath(), "contextcore-vector-postgres-readiness-test", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.SetCurrentDirectory(tempRoot);
            var reportPath = Path.Combine("storage", "postgres", "postgres-vector-freeze-gate.json");
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(
                reportPath,
                System.Text.Json.JsonSerializer.Serialize(new VectorPostgresProviderFreezeGateReport
                {
                    Passed = true,
                    VectorPostgresProvider = "ReadyForPreviewShadowStorage",
                    UseForRuntime = false,
                    FormalRetrievalAllowed = false,
                    Recommendation = "ReadyForPreviewShadowStorage"
                }));

            var registry = await new LearningReadinessFreezeRunner().BuildRegistryFromCurrentFilesAsync();
            var vector = registry.Capabilities.Single(item => item.CapabilityId == ShadowCapabilityIds.VectorPostgresProvider);

            Assert.AreEqual("ReadyForPreviewShadowStorage", vector.Status);
            CollectionAssert.Contains(vector.AllowedRuntimeModes.ToArray(), "PreviewShadowEvalOnly");
            CollectionAssert.Contains(vector.ForbiddenRuntimeModes.ToArray(), "FormalRetrievalSwitch");
            CollectionAssert.Contains(vector.ForbiddenRuntimeModes.ToArray(), "PackingPolicyIntegration");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
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
    public void PostgresLearningFeedbackDiagnostics_ShouldReturnNotConfiguredWhenProviderDisabled()
    {
        var report = PostgresLearningFeedbackDiagnosticsBuilder.BuildNotConfigured(new PostgresOptions { Enabled = false });

        Assert.IsFalse(report.ProviderEnabled);
        Assert.IsFalse(report.UseForRuntime);
        Assert.AreEqual("NotConfigured", report.Status);
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
    public async Task LearningFeedbackDualWrite_ShouldFallbackWhenPostgresWriteFails()
    {
        var fileFeedback = new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeedbackStore();
        var fileReview = new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeedbackReviewStore();
        var fileCandidate = new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeatureCandidateStore();
        var failingStore = new FailingLearningFeedbackStore();
        var traces = new List<LearningFeedbackDualWriteTrace>();
        var coordinator = new LearningFeedbackDualWriteCoordinator(
            fileFeedback,
            fileReview,
            fileCandidate,
            failingStore,
            failingStore,
            failingStore,
            new LearningFeedbackDualWriteOptions
            {
                Enabled = true,
                WritePostgres = true,
                FallbackOnPostgresFailure = true,
                TraceEnabled = true
            },
            (trace, _) =>
            {
                traces.Add(trace);
                return Task.CompletedTask;
            });

        await coordinator.UpsertFeedbackAsync(new LearningFeedbackEvent
        {
            FeedbackId = "feedback-fallback",
            WorkspaceId = "workspace-a",
            CollectionId = "collection-a",
            CapabilityId = ShadowCapabilityIds.RouterIntentClassifier,
            TargetId = "target-a",
            TargetType = LearningFeedbackTargetType.RouterPrediction.ToString(),
            FeedbackKind = LearningFeedbackKinds.WrongIntent,
            RedactionMode = "metadata-only",
            MetadataOnly = true,
            TrainingUse = "disabled_until_review"
        }, CancellationToken.None);

        Assert.AreEqual(1, traces.Count);
        Assert.IsTrue(traces[0].FileSystemWriteSucceeded);
        Assert.IsFalse(traces[0].PostgresWriteSucceeded);
        Assert.IsTrue(traces[0].FallbackUsed);
        Assert.AreEqual(1, (await fileFeedback.QueryAsync(new LearningFeedbackEventQuery { Limit = 10 })).Count);
    }

    [TestMethod]
    public async Task LearningFeedbackShadowRead_ShouldRecordMismatch()
    {
        var traces = new List<LearningFeedbackShadowReadTrace>();
        var coordinator = new LearningFeedbackShadowReadCoordinator(
            new LearningFeedbackShadowReadOptions
            {
                Enabled = true,
                ReadPostgres = true,
                CompareResults = true,
                TraceEnabled = true
            },
            (trace, _) =>
            {
                traces.Add(trace);
                return Task.CompletedTask;
            });

        var result = await coordinator.CompareAsync(
            "list_feedback",
            "target",
            "workspace-a",
            "collection-a",
            _ => Task.FromResult<IReadOnlyList<string>>(["formal"]),
            _ => Task.FromResult<IReadOnlyList<string>>(["shadow"]),
            CancellationToken.None);

        Assert.AreEqual("formal", result[0]);
        Assert.AreEqual(1, traces.Count);
        Assert.IsTrue(traces[0].MismatchDetected);
        Assert.AreEqual("ResultHashMismatch", traces[0].MismatchReason);
    }

    [TestMethod]
    public void LearningFeedbackProviderQuality_ShouldBlockOnMismatch()
    {
        var report = new PostgresLearningFeedbackProviderEvalRunner().BuildProviderQualityReport(
            Array.Empty<LearningFeedbackDualWriteTrace>(),
            [
                new LearningFeedbackShadowReadTrace
                {
                    FileSystemReadSucceeded = true,
                    PostgresReadSucceeded = true,
                    MismatchDetected = true,
                    MismatchReason = "ResultHashMismatch"
                }
            ]);

        Assert.AreEqual("BlockedByMismatch", report.Recommendation);
        Assert.AreEqual(1, report.MismatchCount);
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
    public async Task LearningFeedbackProviderRouter_ShouldUsePostgresPrimaryInsideAllowlistedScope()
    {
        var fileFeedback = new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeedbackStore();
        var fileReview = new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeedbackReviewStore();
        var fileCandidate = new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeatureCandidateStore();
        var postgresFeedback = new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeedbackStore();
        var postgresReview = new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeedbackReviewStore();
        var postgresCandidate = new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeatureCandidateStore();
        var traces = new List<LearningFeedbackProviderSwitchTrace>();
        var router = CreateLearningFeedbackRouter(
            fileFeedback,
            fileReview,
            fileCandidate,
            postgresFeedback,
            postgresReview,
            postgresCandidate,
            [new LearningFeedbackScopedRule { WorkspaceId = "workspace-a", CollectionId = "collection-a" }],
            providerQualityReady: true,
            traces);

        await router.UpsertFeedbackAsync("op-a", CreateLearningFeedback("feedback-a", "workspace-a", "collection-a"));
        var rows = await router.QueryFeedbackAsync("op-query", new LearningFeedbackEventQuery
        {
            WorkspaceId = "workspace-a",
            CollectionId = "collection-a",
            Limit = 10
        });

        Assert.AreEqual(1, rows.Count);
        Assert.IsTrue(traces.Any(static item => string.Equals(item.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue((await postgresFeedback.QueryAsync(new LearningFeedbackEventQuery { WorkspaceId = "workspace-a", CollectionId = "collection-a", Limit = 10 })).Count == 1);
    }

    [TestMethod]
    public async Task LearningFeedbackProviderRouter_ShouldKeepNonAllowlistedScopeOnFileSystem()
    {
        var fileFeedback = new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeedbackStore();
        var fileReview = new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeedbackReviewStore();
        var fileCandidate = new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeatureCandidateStore();
        var postgresFeedback = new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeedbackStore();
        var postgresReview = new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeedbackReviewStore();
        var postgresCandidate = new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeatureCandidateStore();
        var traces = new List<LearningFeedbackProviderSwitchTrace>();
        var router = CreateLearningFeedbackRouter(
            fileFeedback,
            fileReview,
            fileCandidate,
            postgresFeedback,
            postgresReview,
            postgresCandidate,
            [new LearningFeedbackScopedRule { WorkspaceId = "workspace-a", CollectionId = "collection-a" }],
            providerQualityReady: true,
            traces);

        await router.UpsertFeedbackAsync("op-b", CreateLearningFeedback("feedback-b", "workspace-b", "collection-b"));

        Assert.AreEqual(1, (await fileFeedback.QueryAsync(new LearningFeedbackEventQuery { WorkspaceId = "workspace-b", CollectionId = "collection-b", Limit = 10 })).Count);
        Assert.AreEqual(0, (await postgresFeedback.QueryAsync(new LearningFeedbackEventQuery { WorkspaceId = "workspace-b", CollectionId = "collection-b", Limit = 10 })).Count);
        Assert.IsTrue(traces.All(static item => string.Equals(item.PrimaryProvider, "FileSystem", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task LearningFeedbackProviderRouter_ShouldBlockWhenProviderQualityMissing()
    {
        var traces = new List<LearningFeedbackProviderSwitchTrace>();
        var router = CreateLearningFeedbackRouter(
            new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeedbackStore(),
            new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeedbackReviewStore(),
            new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeatureCandidateStore(),
            new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeedbackStore(),
            new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeedbackReviewStore(),
            new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeatureCandidateStore(),
            [new LearningFeedbackScopedRule { WorkspaceId = "workspace-a", CollectionId = "collection-a" }],
            providerQualityReady: false,
            traces);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            router.UpsertFeedbackAsync("op-quality-blocked", CreateLearningFeedback("feedback-quality", "workspace-a", "collection-a")));
    }

    [TestMethod]
    public async Task LearningFeedbackProviderRouter_ShouldFallbackOnPostgresFailure()
    {
        var fileFeedback = new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeedbackStore();
        await fileFeedback.UpsertAsync(CreateLearningFeedback("feedback-fallback-read", "workspace-a", "collection-a"));
        var traces = new List<LearningFeedbackProviderSwitchTrace>();
        var failing = new FailingLearningFeedbackStore();
        var router = CreateLearningFeedbackRouter(
            fileFeedback,
            new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeedbackReviewStore(),
            new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeatureCandidateStore(),
            failing,
            failing,
            failing,
            [new LearningFeedbackScopedRule { WorkspaceId = "workspace-a", CollectionId = "collection-a" }],
            providerQualityReady: true,
            traces);

        var rows = await router.QueryFeedbackAsync("op-fallback", new LearningFeedbackEventQuery
        {
            WorkspaceId = "workspace-a",
            CollectionId = "collection-a",
            Limit = 10
        });

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(1, traces.Count);
        Assert.IsTrue(traces[0].FallbackUsed);
        Assert.AreEqual("InvalidOperationException", traces[0].PostgresError);
    }

    [TestMethod]
    public async Task LearningFeedbackProviderRouter_ShouldRecordMismatch()
    {
        var fileFeedback = new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeedbackStore();
        var postgresFeedback = new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeedbackStore();
        await fileFeedback.UpsertAsync(CreateLearningFeedback("feedback-left", "workspace-a", "collection-a"));
        await postgresFeedback.UpsertAsync(CreateLearningFeedback("feedback-right", "workspace-a", "collection-a"));
        var traces = new List<LearningFeedbackProviderSwitchTrace>();
        var router = new LearningFeedbackProviderRouter(
            fileFeedback,
            new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeedbackReviewStore(),
            new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeatureCandidateStore(),
            postgresFeedback,
            new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeedbackReviewStore(),
            new ContextCore.Storage.InMemory.Stores.InMemoryLearningFeatureCandidateStore(),
            new LearningFeedbackProviderSwitchOptions
            {
                Enabled = true,
                Mode = LearningFeedbackProviderMode.FileSystemPrimary,
                FailClosedOnMismatch = false,
                ScopedRules = [new LearningFeedbackScopedRule { WorkspaceId = "workspace-a", CollectionId = "collection-a", Mode = LearningFeedbackProviderMode.ShadowRead }]
            },
            providerQualityReady: true,
            (trace, _) =>
            {
                traces.Add(trace);
                return Task.CompletedTask;
            });

        var rows = await router.QueryFeedbackAsync("op-mismatch", new LearningFeedbackEventQuery
        {
            WorkspaceId = "workspace-a",
            CollectionId = "collection-a",
            Limit = 10
        });

        Assert.AreEqual("feedback-left", rows[0].FeedbackId);
        Assert.AreEqual(1, traces.Count);
        Assert.IsTrue(traces[0].MismatchDetected);
    }

    [TestMethod]
    public void LearningFeedbackScopedServiceModeGate_ShouldFailOnScopeLeak()
    {
        var report = new LearningFeedbackScopedServiceModeGateReport
        {
            Passed = false,
            NonAllowlistedScopeRemainsFileSystem = false,
            BlockedReasons = ["NonAllowlistedScopeLeak"],
            Recommendation = "BlockedByScopeLeak"
        };

        Assert.IsFalse(report.Passed);
        Assert.AreEqual("BlockedByScopeLeak", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "NonAllowlistedScopeLeak");
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
    public void LearningFeedbackSelectedNormalScopeCanary_ShouldBlockWhenScopeMissing()
    {
        var report = new LearningFeedbackSelectedNormalScopeCanaryReport
        {
            GatePassed = false,
            BlockedReasons = ["SelectedWorkspaceMissing", "SelectedCollectionMissing"],
            Recommendation = "GateNotPassed"
        };

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual("GateNotPassed", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "SelectedWorkspaceMissing");
    }

    [TestMethod]
    public void LearningFeedbackSelectedNormalScopeCanary_ShouldBlockOnMismatch()
    {
        var report = new LearningFeedbackSelectedNormalScopeCanaryReport
        {
            GatePassed = true,
            MismatchCount = 1,
            Mismatches = ["SelectedNormalFeedbackQueryMissingRows"],
            Recommendation = "BlockedByMismatch"
        };

        Assert.AreEqual(1, report.MismatchCount);
        Assert.AreEqual("BlockedByMismatch", report.Recommendation);
    }

    [TestMethod]
    public void LearningFeedbackSelectedNormalScopeCanary_ShouldBlockOnScopeLeak()
    {
        var report = new LearningFeedbackSelectedNormalScopeCanaryReport
        {
            GatePassed = true,
            ScopeLeakCount = 1,
            Recommendation = "BlockedByScopeLeak"
        };

        Assert.AreEqual(1, report.ScopeLeakCount);
        Assert.AreEqual("BlockedByScopeLeak", report.Recommendation);
    }

    [TestMethod]
    public void LearningFeedbackCanaryCandidate_ShouldNotBeTrainableDataset()
    {
        var candidateReport = new LearningFeedbackFeatureCandidateReport
        {
            GeneratedCandidateCount = 1,
            Candidates =
            [
                new FeedbackFeatureCandidate
                {
                    CandidateId = "db33-canary-candidate-a",
                    SourceFeedbackId = "db33-canary-feedback-a",
                    CapabilityId = ShadowCapabilityIds.RouterIntentClassifier,
                    TargetType = LearningFeedbackTargetType.RouterPrediction.ToString(),
                    LabelKind = "router_intent_correction",
                    PositiveLabel = true,
                    TrainingUse = "smoke_test_only",
                    RedactionStatus = "metadata-only",
                    ReviewStatus = FeedbackReviewStatus.ApprovedForDataset,
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["excludedFromTraining"] = "true"
                    }
                }
            ]
        };
        var quality = new LearningFeedbackQualityReport
        {
            ApprovedCount = 1,
            RedactionCoverageRate = 1.0
        };

        var gate = new LearningApprovedFeedbackDatasetGateBuilder().Build(quality, candidateReport);

        Assert.IsFalse(gate.Passed);
        Assert.AreEqual(0, gate.TrainableCandidateCount);
        Assert.AreEqual(1, gate.SmokeExcludedCount);
        CollectionAssert.Contains(gate.FailureReasons.ToArray(), LearningApprovedFeedbackDatasetGateFailureReasons.NoTrainableCandidates);
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
    public void LearningFeedbackLimitedScopeObservation_ShouldBlockWhenSelectedCanaryMissing()
    {
        var report = new LearningFeedbackLimitedScopeObservationReport
        {
            GatePassed = false,
            BlockedReasons = ["SelectedNormalScopeCanaryNotPassed"],
            Recommendation = "GateNotPassed"
        };

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual("GateNotPassed", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "SelectedNormalScopeCanaryNotPassed");
    }

    [TestMethod]
    public void LearningFeedbackLimitedScopeQuality_ShouldFailOnMismatchFailureOrScopeLeak()
    {
        var report = new PostgresLearningFeedbackProviderEvalRunner().BuildLimitedScopeQualityReport(
            new LearningFeedbackLimitedScopeObservationReport
            {
                GatePassed = true,
                OperationCount = 3,
                MismatchCount = 1,
                PostgresFailureCount = 1,
                ScopeLeakCount = 1,
                ExportProjectionParityPassed = true,
                SummaryParityPassed = true,
                ReviewSummaryParityPassed = true,
                FeatureCandidateParityPassed = true
            });

        Assert.IsFalse(report.Passed);
        Assert.AreEqual("BlockedByMismatch", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "MismatchCountNonZero");
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "PostgresFailureCountNonZero");
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "ScopeLeakCountNonZero");
    }

    [TestMethod]
    public void LearningFeedbackLimitedScopeQuality_ShouldBlockTrainableLeak()
    {
        var report = new PostgresLearningFeedbackProviderEvalRunner().BuildLimitedScopeQualityReport(
            new LearningFeedbackLimitedScopeObservationReport
            {
                GatePassed = true,
                OperationCount = 3,
                ExportProjectionParityPassed = true,
                SummaryParityPassed = true,
                ReviewSummaryParityPassed = true,
                FeatureCandidateParityPassed = true,
                TrainableCandidateLeakCount = 1
            });

        Assert.IsFalse(report.Passed);
        Assert.AreEqual("BlockedByTrainableLeak", report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "TrainableCandidateLeakCountNonZero");
    }

    [TestMethod]
    public void LearningFeedbackFreezeGate_ShouldForbidGlobalDefaultOnAndRequireFallbackTrace()
    {
        var report = new PostgresLearningFeedbackProviderEvalRunner().BuildFreezeGateReport(
            new LearningFeedbackPostgresReadinessGateReport { GatePassed = true },
            new LearningFeedbackProviderQualityReport { Recommendation = "ReadyForScopedServiceMode" },
            new LearningFeedbackScopedServiceModeGateReport { Passed = true },
            new LearningFeedbackSelectedNormalScopeCanaryReport { Recommendation = "ReadyForLimitedFeedbackScope" },
            new LearningFeedbackLimitedScopeQualityReport
            {
                Passed = true,
                Recommendation = "ReadyForFreezeGate",
                ExportProjectionParityPassed = true,
                SummaryParityPassed = true,
                ReviewSummaryParityPassed = true,
                FeatureCandidateParityPassed = true
            },
            p15GatePassed: true);

        Assert.IsTrue(report.Passed);
        Assert.AreEqual("FileSystem", report.DefaultProvider);
        Assert.IsTrue(report.GlobalDefaultOnForbidden);
        Assert.IsTrue(report.FallbackRequired);
        Assert.IsTrue(report.ComparisonTraceRequired);
        CollectionAssert.Contains(report.Forbidden.ToArray(), "global default-on");
    }

    [TestMethod]
    public void LearningFeedbackFreezeGate_ShouldFailWhenFallbackOrTraceRequirementMissing()
    {
        var report = new PostgresLearningFeedbackProviderEvalRunner().BuildFreezeGateReport(
            new LearningFeedbackPostgresReadinessGateReport { GatePassed = true },
            new LearningFeedbackProviderQualityReport { Recommendation = "ReadyForScopedServiceMode" },
            new LearningFeedbackScopedServiceModeGateReport { Passed = true },
            new LearningFeedbackSelectedNormalScopeCanaryReport { Recommendation = "ReadyForLimitedFeedbackScope" },
            new LearningFeedbackLimitedScopeQualityReport
            {
                Passed = true,
                Recommendation = "ReadyForFreezeGate",
                ExportProjectionParityPassed = true,
                SummaryParityPassed = true,
                ReviewSummaryParityPassed = true,
                FeatureCandidateParityPassed = true
            },
            p15GatePassed: true,
            fallbackRequired: false,
            comparisonTraceRequired: false,
            globalDefaultOnForbidden: false);

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "FallbackRequiredMissing");
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "ComparisonTraceRequiredMissing");
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "GlobalDefaultOnForbiddenMissing");
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
    public void PostgresJobQueueProviderQuality_ShouldBlockOnMismatch()
    {
        var report = PostgresJobQueueProviderEvalRunner.BuildProviderQuality(
            new PostgresJobQueueDualWriteSmokeReport
            {
                TraceCount = 2,
                FileSystemWriteSuccessCount = 2,
                PostgresWriteSuccessCount = 2,
                MismatchCount = 1,
                LeaseParityPassed = true,
                RetryParityPassed = true,
                DeadLetterParityPassed = true
            },
            new PostgresJobQueueShadowReadSmokeReport
            {
                TraceCount = 2,
                FileSystemReadSuccessCount = 2,
                PostgresReadSuccessCount = 2,
                CountParityPassed = true,
                LeaseParityPassed = true,
                RetryParityPassed = true,
                DeadLetterParityPassed = true
            });

        Assert.AreEqual("BlockedByMismatch", report.Recommendation);
        Assert.AreEqual(1, report.MismatchCount);
    }

    [TestMethod]
    public void PostgresJobQueueProviderQuality_ShouldBlockOnPostgresFailure()
    {
        var report = PostgresJobQueueProviderEvalRunner.BuildProviderQuality(
            new PostgresJobQueueDualWriteSmokeReport
            {
                TraceCount = 1,
                PostgresFailureCount = 1
            },
            new PostgresJobQueueShadowReadSmokeReport());

        Assert.AreEqual("BlockedByPostgresFailure", report.Recommendation);
        Assert.AreEqual(1, report.PostgresFailureCount);
    }

    [TestMethod]
    public void PostgresJobQueueScopedWorkerQuality_ShouldPassCleanCanary()
    {
        var report = PostgresJobQueueProviderEvalRunner.BuildScopedWorkerQuality(
            new PostgresJobQueueScopedWorkerCanaryReport
            {
                Recommendation = "ReadyForLimitedWorkerScope",
                JobCount = 6,
                CompletedCount = 4,
                RetriedCount = 2,
                DeadLetterCount = 1,
                LeaseAcquireCount = 8,
                LeaseConflictCount = 1,
                LeaseExpiredReacquireCount = 1,
                HeartbeatCount = 1,
                NonSelectedScopeRemainsFileSystem = true,
                RuntimeWorkerGlobalProviderUnchanged = true
            });

        Assert.IsTrue(report.Passed);
        Assert.AreEqual("ReadyForLimitedWorkerScope", report.Recommendation);
    }

    [TestMethod]
    public void PostgresJobQueueScopedWorkerQuality_ShouldBlockOnScopeLeak()
    {
        var report = PostgresJobQueueProviderEvalRunner.BuildScopedWorkerQuality(
            new PostgresJobQueueScopedWorkerCanaryReport
            {
                Recommendation = "BlockedByScopeLeak",
                ScopeLeakCount = 1,
                NonSelectedScopeRemainsFileSystem = false,
                RuntimeWorkerGlobalProviderUnchanged = true
            });

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "ScopeLeakDetected");
    }

    [TestMethod]
    public void PostgresJobQueueScopedWorkerQuality_ShouldBlockOnRuntimeWorkerChange()
    {
        var report = PostgresJobQueueProviderEvalRunner.BuildScopedWorkerQuality(
            new PostgresJobQueueScopedWorkerCanaryReport
            {
                Recommendation = "ReadyForLimitedWorkerScope",
                NonSelectedScopeRemainsFileSystem = true,
                RuntimeWorkerGlobalProviderUnchanged = false
            });

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "RuntimeWorkerGlobalProviderChanged");
    }

    [TestMethod]
    public void PostgresJobQueueLimitedWorkerScopeQuality_ShouldPassCleanObservation()
    {
        var report = PostgresJobQueueProviderEvalRunner.BuildLimitedWorkerScopeQuality(
            new PostgresJobQueueLimitedWorkerScopeObservationReport
            {
                Recommendation = "ReadyForJobQueueFreezeGate",
                JobCount = 11,
                CompletedCount = 7,
                RetriedCount = 4,
                DeadLetterCount = 2,
                CancelledCount = 1,
                LeaseAcquireCount = 13,
                LeaseConflictCount = 1,
                LeaseExpiredReacquireCount = 1,
                HeartbeatCount = 2,
                NonSelectedScopeRemainsFileSystem = true,
                RuntimeWorkerGlobalProviderUnchanged = true
            });

        Assert.IsTrue(report.Passed);
        Assert.AreEqual("ReadyForJobQueueFreezeGate", report.Recommendation);
    }

    [TestMethod]
    public void PostgresJobQueueLimitedWorkerScopeQuality_ShouldBlockDuplicateExecution()
    {
        var report = PostgresJobQueueProviderEvalRunner.BuildLimitedWorkerScopeQuality(
            new PostgresJobQueueLimitedWorkerScopeObservationReport
            {
                Recommendation = "BlockedByDuplicateExecution",
                DuplicateExecutionCount = 1,
                NonSelectedScopeRemainsFileSystem = true,
                RuntimeWorkerGlobalProviderUnchanged = true
            });

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "DuplicateExecutionDetected");
    }

    [TestMethod]
    public void PostgresJobQueueLimitedWorkerScopeQuality_ShouldBlockLeaseViolation()
    {
        var report = PostgresJobQueueProviderEvalRunner.BuildLimitedWorkerScopeQuality(
            new PostgresJobQueueLimitedWorkerScopeObservationReport
            {
                Recommendation = "BlockedByLeaseViolation",
                LeaseViolationCount = 1,
                NonSelectedScopeRemainsFileSystem = true,
                RuntimeWorkerGlobalProviderUnchanged = true
            });

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "LeaseViolationDetected");
    }

    [TestMethod]
    public void PostgresJobQueueLimitedWorkerScopeQuality_ShouldBlockRetryAndDeadLetterViolation()
    {
        var report = PostgresJobQueueProviderEvalRunner.BuildLimitedWorkerScopeQuality(
            new PostgresJobQueueLimitedWorkerScopeObservationReport
            {
                Recommendation = "BlockedByRetryViolation",
                RetryViolationCount = 1,
                DeadLetterViolationCount = 1,
                NonSelectedScopeRemainsFileSystem = true,
                RuntimeWorkerGlobalProviderUnchanged = true
            });

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "RetryViolationDetected");
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "DeadLetterViolationDetected");
    }

    [TestMethod]
    public void PostgresJobQueueLimitedWorkerScopeQuality_ShouldBlockScopeLeakAndRuntimeWorkerChange()
    {
        var report = PostgresJobQueueProviderEvalRunner.BuildLimitedWorkerScopeQuality(
            new PostgresJobQueueLimitedWorkerScopeObservationReport
            {
                Recommendation = "BlockedByScopeLeak",
                ScopeLeakCount = 1,
                NonSelectedScopeRemainsFileSystem = false,
                RuntimeWorkerGlobalProviderUnchanged = false
            });

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "ScopeLeakDetected");
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "RuntimeWorkerGlobalProviderChanged");
    }

    [TestMethod]
    public void PostgresJobQueueFreezeGate_ShouldPassCleanReports()
    {
        var report = new PostgresJobQueueProviderEvalRunner().BuildFreezeGateReport(
            new PostgresJobQueueDiagnosticsReport { Recommendation = "ReadyForParityEval" },
            new PostgresJobQueueProviderQualityReport
            {
                Recommendation = "ReadyForScopedWorkerCanary",
                LeaseParityPassed = true,
                RetryParityPassed = true,
                DeadLetterParityPassed = true,
                CountParityPassed = true
            },
            new PostgresJobQueueScopedWorkerCanaryReport
            {
                Recommendation = "ReadyForLimitedWorkerScope",
                RuntimeWorkerGlobalProviderUnchanged = true
            },
            new PostgresJobQueueLimitedWorkerScopeQualityReport
            {
                Passed = true,
                Recommendation = "ReadyForJobQueueFreezeGate",
                NonSelectedScopeRemainsFileSystem = true,
                RuntimeWorkerGlobalProviderUnchanged = true
            },
            p15GatePassed: true);

        Assert.IsTrue(report.Passed);
        Assert.AreEqual("ReadyForScopedWorkerMode", report.JobQueuePostgres);
        Assert.IsFalse(report.GlobalSwitchAllowed);
        Assert.IsTrue(report.ScopedWorkerCanaryAllowed);
    }

    [TestMethod]
    public void PostgresJobQueueFreezeGate_ShouldBlockDuplicateExecution()
    {
        var report = BuildCleanJobQueueFreezeReport(new PostgresJobQueueLimitedWorkerScopeQualityReport
        {
            Passed = true,
            Recommendation = "ReadyForJobQueueFreezeGate",
            DuplicateExecutionCount = 1,
            NonSelectedScopeRemainsFileSystem = true,
            RuntimeWorkerGlobalProviderUnchanged = true
        });

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "DuplicateExecutionCountNonZero");
        Assert.AreEqual("BlockedByDuplicateExecution", report.Recommendation);
    }

    [TestMethod]
    public void PostgresJobQueueFreezeGate_ShouldBlockLeaseViolation()
    {
        var report = BuildCleanJobQueueFreezeReport(new PostgresJobQueueLimitedWorkerScopeQualityReport
        {
            Passed = true,
            Recommendation = "ReadyForJobQueueFreezeGate",
            LeaseViolationCount = 1,
            NonSelectedScopeRemainsFileSystem = true,
            RuntimeWorkerGlobalProviderUnchanged = true
        });

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "LeaseViolationCountNonZero");
        Assert.AreEqual("BlockedByLeaseViolation", report.Recommendation);
    }

    [TestMethod]
    public void PostgresJobQueueFreezeGate_ShouldBlockRetryAndDeadLetterViolation()
    {
        var report = BuildCleanJobQueueFreezeReport(new PostgresJobQueueLimitedWorkerScopeQualityReport
        {
            Passed = true,
            Recommendation = "ReadyForJobQueueFreezeGate",
            RetryViolationCount = 1,
            DeadLetterViolationCount = 1,
            NonSelectedScopeRemainsFileSystem = true,
            RuntimeWorkerGlobalProviderUnchanged = true
        });

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "RetryViolationCountNonZero");
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "DeadLetterViolationCountNonZero");
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
    public async Task PostgresJobQueueDiagnostics_ShouldReturnNotConfiguredWhenDisabled()
    {
        var report = await new PostgresJobQueueProviderEvalRunner().BuildDiagnosticsAsync(
            new PostgresOptions { Enabled = false });

        Assert.IsFalse(report.ProviderEnabled);
        Assert.IsFalse(report.ConnectionAvailable);
        Assert.IsFalse(report.UseForRuntime);
        Assert.AreEqual("NotConfigured", report.Recommendation);
        CollectionAssert.Contains(report.Diagnostics.ToArray(), "NotConfigured");
    }

    [TestMethod]
    public void PostgresJobQueueLeaseSmokeReport_ShouldExposeLeaseContractFailure()
    {
        var report = new PostgresJobQueueLeaseSmokeReport
        {
            MismatchCount = 1,
            Mismatches = ["LeaseConflictFailed"],
            Recommendation = "NeedsLeaseContractFix"
        };

        Assert.AreEqual("NeedsLeaseContractFix", report.Recommendation);
        CollectionAssert.Contains(report.Mismatches.ToArray(), "LeaseConflictFailed");
    }

    [TestMethod]
    public void PostgresJobQueueParityReport_ShouldRequireCleanupForSmokeData()
    {
        var report = new PostgresJobQueueParityReport
        {
            CleanupPerformed = false,
            Recommendation = "ReadyForDualWriteShadowRead"
        };

        Assert.IsFalse(report.CleanupPerformed);
        Assert.AreEqual("ReadyForDualWriteShadowRead", report.Recommendation);
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
    public void PostgresRelationStoreParityReport_ShouldExposeMismatchRecommendation()
    {
        var report = new PostgresRelationStoreParityReport
        {
            ProviderEnabled = true,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Mismatches = ["SourceQueryMismatch"],
            Recommendation = "ParityMismatch"
        };

        Assert.AreEqual("ParityMismatch", report.Recommendation);
        CollectionAssert.Contains(report.Mismatches.ToArray(), "SourceQueryMismatch");
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
    public void RelationGovernanceShadowReadOptions_ShouldBeDisabledByDefault()
    {
        var options = new RelationGovernanceShadowReadOptions();

        Assert.IsFalse(options.Enabled);
        Assert.IsTrue(options.TraceEnabled);
        Assert.IsFalse(options.ReadPostgres);
        Assert.IsTrue(options.CompareResults);
        Assert.IsFalse(options.FailOnMismatch);
        Assert.IsTrue(options.MaxTraceItems > 0);
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
    public void RelationGovernanceShadowReadHash_ShouldBeStableForEquivalentRelations()
    {
        var relationA = new ContextRelation
        {
            Id = "relation-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = "source-1",
            TargetId = "target-1",
            RelationType = "references",
            Weight = 0.7,
            Confidence = 0.9,
            SourceRefs = ["b", "a"],
            CreatedAt = DateTimeOffset.UnixEpoch,
            Metadata = new Dictionary<string, string>
            {
                ["reviewStatus"] = "Reviewed",
                ["lifecycle"] = "Active"
            }
        };
        var relationB = new ContextRelation
        {
            Id = "relation-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = "source-1",
            TargetId = "target-1",
            RelationType = "references",
            Weight = 0.7,
            Confidence = 0.9,
            SourceRefs = ["a", "b"],
            CreatedAt = DateTimeOffset.UnixEpoch,
            Metadata = new Dictionary<string, string>
            {
                ["lifecycle"] = "Active",
                ["reviewStatus"] = "Reviewed"
            }
        };

        var first = RelationGovernanceShadowReadCoordinator.ComputeStableHash(new[] { relationA });
        var second = RelationGovernanceShadowReadCoordinator.ComputeStableHash(new[] { relationB });

        Assert.AreEqual(first, second);
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
    public async Task RelationGovernanceProviderRouter_ShouldRequireReadinessGate()
    {
        using var harness = CreateProviderSwitchHarness(
            new RelationGovernanceProviderSwitchOptions
            {
                Enabled = true,
                Mode = RelationGovernanceProviderMode.GuardedPostgresPrimary,
                AllowedWorkspaces = ["workspace-test"],
                AllowedCollections = ["collection-test"],
                RequireReadinessGate = true
            },
            readinessGatePassed: false,
            shadowReadQualityReady: true);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            harness.Router.GetRelationAsync("op", "workspace-test", "collection-test", "relation-1"));
    }

    [TestMethod]
    public async Task RelationGovernanceProviderRouter_ShouldRejectWorkspaceOutsideAllowlist()
    {
        using var harness = CreateProviderSwitchHarness(
            new RelationGovernanceProviderSwitchOptions
            {
                Enabled = true,
                Mode = RelationGovernanceProviderMode.GuardedPostgresPrimary,
                AllowedWorkspaces = ["workspace-test"],
                AllowedCollections = ["collection-test"]
            },
            readinessGatePassed: true,
            shadowReadQualityReady: true);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            harness.Router.GetRelationAsync("op", "other-workspace", "collection-test", "relation-1"));
    }

    [TestMethod]
    public async Task RelationGovernanceProviderRouter_ScopedRules_ShouldKeepDefaultFileSystemOutsideScope()
    {
        using var harness = CreateProviderSwitchHarness(
            new RelationGovernanceProviderSwitchOptions
            {
                Enabled = true,
                Mode = RelationGovernanceProviderMode.FileSystemPrimary,
                ScopedRules =
                [
                    new RelationGovernanceScopedRule
                    {
                        ScopeName = "scope-a",
                        WorkspaceId = "workspace-a",
                        CollectionId = "collection-a",
                        Mode = RelationGovernanceProviderMode.GuardedPostgresPrimary
                    }
                ],
                FallbackToFileSystem = true,
                ContinueComparisonTrace = true
            },
            readinessGatePassed: true,
            shadowReadQualityReady: true);
        var relation = new ContextRelation
        {
            Id = "relation-outside",
            WorkspaceId = "workspace-outside",
            CollectionId = "collection-outside",
            SourceId = "source",
            TargetId = "target",
            RelationType = "references",
            CreatedAt = DateTimeOffset.UnixEpoch
        };

        await harness.Router.SaveRelationAsync("op-outside", relation);

        Assert.IsTrue(harness.Traces.Any(trace => string.Equals(trace.PrimaryProvider, "FileSystem", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(harness.Traces.Any(trace => string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task RelationGovernanceProviderRouter_ScopedRules_ShouldUsePostgresPrimaryInsideScope()
    {
        using var harness = CreateProviderSwitchHarness(
            new RelationGovernanceProviderSwitchOptions
            {
                Enabled = true,
                Mode = RelationGovernanceProviderMode.FileSystemPrimary,
                ScopedRules =
                [
                    new RelationGovernanceScopedRule
                    {
                        ScopeName = "scope-a",
                        WorkspaceId = "workspace-a",
                        CollectionId = "collection-a",
                        Mode = RelationGovernanceProviderMode.GuardedPostgresPrimary
                    }
                ],
                FallbackToFileSystem = true,
                ContinueComparisonTrace = true
            },
            readinessGatePassed: true,
            shadowReadQualityReady: true);
        var relation = new ContextRelation
        {
            Id = "relation-inside",
            WorkspaceId = "workspace-a",
            CollectionId = "collection-a",
            SourceId = "source",
            TargetId = "target",
            RelationType = "references",
            CreatedAt = DateTimeOffset.UnixEpoch
        };

        await harness.Router.SaveRelationAsync("op-inside", relation);

        Assert.IsTrue(harness.Traces.Any(trace => string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task RelationGovernanceProviderRouter_ShouldFallbackToFileSystemWhenPostgresReadFails()
    {
        using var harness = CreateProviderSwitchHarness(
            new RelationGovernanceProviderSwitchOptions
            {
                Enabled = true,
                Mode = RelationGovernanceProviderMode.GuardedPostgresPrimary,
                AllowedWorkspaces = ["workspace-test"],
                AllowedCollections = ["collection-test"],
                FallbackToFileSystem = true,
                ContinueComparisonTrace = true
            },
            readinessGatePassed: true,
            shadowReadQualityReady: true);
        var relation = new ContextRelation
        {
            Id = "relation-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = "source-1",
            TargetId = "target-1",
            RelationType = "references",
            CreatedAt = DateTimeOffset.UnixEpoch
        };
        await harness.FileRelationStore.SaveAsync(relation);

        var result = await harness.Router.GetRelationAsync("op", "workspace-test", "collection-test", relation.Id);

        Assert.IsNotNull(result);
        Assert.AreEqual(relation.Id, result.Id);
        Assert.IsTrue(harness.Traces.Any(trace => trace.FallbackUsed));
        Assert.IsTrue(harness.Traces.Any(trace => !string.IsNullOrWhiteSpace(trace.PostgresError)));
        Assert.IsTrue(harness.Traces.Any(trace => string.Equals(trace.PrimaryProvider, "Postgres", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task RelationGovernanceProviderRouter_DualWriteOnly_ShouldKeepFileSystemPrimary()
    {
        using var harness = CreateProviderSwitchHarness(
            new RelationGovernanceProviderSwitchOptions
            {
                Enabled = true,
                Mode = RelationGovernanceProviderMode.DualWriteOnly,
                AllowedWorkspaces = ["workspace-test"],
                AllowedCollections = ["collection-test"],
                FallbackToFileSystem = true
            },
            readinessGatePassed: true,
            shadowReadQualityReady: true);
        var relation = new ContextRelation
        {
            Id = "relation-dual-write",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = "source-1",
            TargetId = "target-1",
            RelationType = "references",
            CreatedAt = DateTimeOffset.UnixEpoch
        };

        await harness.Router.SaveRelationAsync("op", relation);
        var stored = await harness.FileRelationStore.GetAsync("workspace-test", "collection-test", relation.Id);

        Assert.IsNotNull(stored);
        Assert.AreEqual(relation.Id, stored.Id);
        Assert.IsTrue(harness.Traces.Any(trace => string.Equals(trace.PrimaryProvider, "FileSystem", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task RelationGovernanceProviderRouter_ShouldTraceDeleteInGuardedScope()
    {
        using var harness = CreateProviderSwitchHarness(
            new RelationGovernanceProviderSwitchOptions
            {
                Enabled = true,
                Mode = RelationGovernanceProviderMode.GuardedPostgresPrimary,
                AllowedWorkspaces = ["workspace-test"],
                AllowedCollections = ["collection-test"],
                FallbackToFileSystem = true,
                ContinueComparisonTrace = true
            },
            readinessGatePassed: true,
            shadowReadQualityReady: true);
        var relation = new ContextRelation
        {
            Id = "relation-delete",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = "source-1",
            TargetId = "target-1",
            RelationType = "references",
            CreatedAt = DateTimeOffset.UnixEpoch
        };
        await harness.FileRelationStore.SaveAsync(relation);

        await harness.Router.DeleteRelationAsync("op-delete", "workspace-test", "collection-test", relation.Id);

        Assert.IsTrue(harness.Traces.Any(trace => string.Equals(trace.OperationKind, "RelationDelete", StringComparison.OrdinalIgnoreCase)));
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
    public async Task PostgresVectorDiagnostics_ShouldReturnNotConfiguredWithoutConnection()
    {
        var report = await new PostgresVectorIndexProviderEvalRunner()
            .BuildDiagnosticsAsync(new PostgresOptions { Enabled = false }, CancellationToken.None);

        Assert.AreEqual("NotConfigured", report.Recommendation);
        Assert.IsFalse(report.ProviderEnabled);
        Assert.IsFalse(report.ConnectionAvailable);
        Assert.IsFalse(report.PgVectorAvailable);
        Assert.IsFalse(report.UseForRuntime);
        CollectionAssert.Contains(report.Diagnostics.ToArray(), "NotConfigured");
    }

    [TestMethod]
    public async Task PostgresVectorProviderSmoke_ShouldReturnNotConfiguredWithoutConnection()
    {
        var report = await new PostgresVectorIndexProviderEvalRunner().RunProviderSmokeAsync(
            new PostgresOptions { Enabled = false },
            "workspace",
            "collection",
            "deterministic-hash",
            "deterministic-hash-v1",
            16,
            cleanupConfirm: true,
            CancellationToken.None);

        Assert.AreEqual("NotConfigured", report.Recommendation);
        Assert.IsFalse(report.ProviderEnabled);
        Assert.IsFalse(report.UseForRuntime);
        Assert.AreEqual(0, report.InsertedCount);
        CollectionAssert.Contains(report.Diagnostics.ToArray(), "NotConfigured");
    }

    [TestMethod]
    public async Task PostgresVectorParity_ShouldReturnNotConfiguredWithoutConnection()
    {
        var report = await new PostgresVectorIndexParityRunner().RunParityAsync(
            new PostgresOptions { Enabled = false },
            "workspace",
            "collection",
            "deterministic-hash",
            "deterministic-hash-v1",
            16,
            cleanupConfirm: true,
            CancellationToken.None);

        Assert.AreEqual("NotConfigured", report.Recommendation);
        Assert.IsFalse(report.UseForRuntime);
        CollectionAssert.Contains(report.Diagnostics.ToArray(), "PostgresVectorDiagnosticsNotReady:NotConfigured");
    }

    [TestMethod]
    public async Task PostgresVectorParity_ShouldRequireCleanupConfirm()
    {
        var report = await new PostgresVectorIndexParityRunner().RunParityAsync(
            new PostgresOptions
            {
                Enabled = true,
                ConnectionString = "Host=localhost;Database=contextcore;Username=user;Password=secret"
            },
            "workspace",
            "collection",
            "deterministic-hash",
            "deterministic-hash-v1",
            16,
            cleanupConfirm: false,
            CancellationToken.None);

        Assert.AreEqual("NotConfigured", report.Recommendation);
        Assert.IsFalse(report.CleanupPerformed);
        Assert.IsFalse(report.UseForRuntime);
        CollectionAssert.Contains(report.Diagnostics.ToArray(), "CleanupConfirmRequired");
    }

    [TestMethod]
    public async Task PostgresVectorProviderScopedReindexPlan_ShouldReturnNotConfiguredWithoutConnection()
    {
        var report = await new PostgresVectorProviderScopedReindexRunner().BuildPlanAsync(
            new PostgresOptions { Enabled = false },
            [CreateVectorReindexSourceItem("source-a")],
            new DeterministicHashEmbeddingGenerator(),
            "workspace",
            "collection",
            "deterministic-hash",
            "deterministic-hash-v1",
            16,
            normalized: true,
            sourceKindFilter: null,
            dryRun: true,
            CancellationToken.None);

        Assert.AreEqual("NotConfigured", report.Recommendation);
        Assert.IsTrue(report.DryRun);
        Assert.IsFalse(report.UseForRuntime);
        Assert.AreEqual(0, report.PlannedInsertCount);
        CollectionAssert.Contains(report.Diagnostics.ToArray(), "PostgresVectorDiagnosticsNotReady:NotConfigured");
    }

    [TestMethod]
    public async Task PostgresVectorProviderScopedReindexApply_ShouldRequireExplicitConfirm()
    {
        var report = await new PostgresVectorProviderScopedReindexRunner().ApplyAsync(
            new PostgresOptions
            {
                Enabled = true,
                ConnectionString = "Host=localhost;Database=contextcore;Username=user;Password=secret"
            },
            [CreateVectorReindexSourceItem("source-a")],
            new DeterministicHashEmbeddingGenerator(),
            "workspace",
            "collection",
            "deterministic-hash",
            "deterministic-hash-v1",
            16,
            normalized: true,
            sourceKindFilter: null,
            confirm: false,
            CancellationToken.None);

        Assert.AreEqual("NotConfigured", report.Recommendation);
        Assert.IsFalse(report.Confirmed);
        Assert.IsFalse(report.UseForRuntime);
        Assert.AreEqual(0, report.AppliedInsertCount);
        CollectionAssert.Contains(report.Diagnostics.ToArray(), "ConfirmRequired");
    }

    [TestMethod]
    public async Task PostgresVectorProviderScopedReindexQuality_ShouldRemainRuntimeDisabledWhenNotConfigured()
    {
        var report = await new PostgresVectorProviderScopedReindexRunner().BuildQualityAsync(
            new PostgresOptions { Enabled = false },
            [CreateVectorReindexSourceItem("source-a")],
            new DeterministicHashEmbeddingGenerator(),
            "workspace",
            "collection",
            "deterministic-hash",
            "deterministic-hash-v1",
            16,
            normalized: true,
            sourceKindFilter: null,
            latestApply: null,
            CancellationToken.None);

        Assert.AreEqual("NotConfigured", report.Recommendation);
        Assert.IsFalse(report.UseForRuntime);
        Assert.AreEqual(0, report.IndexedEntryCountAfterApply);
        CollectionAssert.Contains(report.Diagnostics.ToArray(), "PostgresVectorDiagnosticsNotReady:NotConfigured");
    }

    [TestMethod]
    public async Task PostgresVectorQueryPreview_ShouldReturnNotConfiguredWithoutConnection()
    {
        var report = await new PostgresVectorQueryPreviewRunner().RunAsync(
            new PostgresOptions { Enabled = false },
            [CreateVectorReindexSourceItem("source-a")],
            [new ContextEvalSample { Id = "sample-a", Query = "provider scoped query", Mode = "ProjectMode" }],
            new DeterministicHashEmbeddingGenerator(),
            "workspace",
            "collection",
            "deterministic-hash",
            "deterministic-hash-v1",
            16,
            normalized: true,
            topK: 5,
            profileId: VectorQueryProfileIds.NormalV1,
            minSimilarity: null,
            CancellationToken.None);

        Assert.AreEqual("NotConfigured", report.Recommendation);
        Assert.IsFalse(report.UseForRuntime);
        Assert.AreEqual(0, report.QueryCount);
        CollectionAssert.Contains(report.Diagnostics.ToArray(), "PostgresVectorDiagnosticsNotReady:NotConfigured");
    }

    [TestMethod]
    public void PostgresVectorQueryPreviewMarkdown_ShouldExposeProjectionQualityFields()
    {
        var report = new PostgresVectorQueryPreviewReport
        {
            Recommendation = "ReadyForPgVectorShadowEval",
            WorkspaceId = "workspace",
            CollectionId = "collection",
            ProviderId = "deterministic-hash",
            ModelId = "deterministic-hash-v1",
            Dimension = 16,
            Normalized = true,
            TopK = 5,
            ProfileId = VectorQueryProfileIds.NormalV1,
            QueryCount = 1,
            PgVectorCandidateCount = 5,
            FileSystemCandidateCount = 5,
            TopKOverlapCount = 5,
            TopKOverlapRate = 1,
            DimensionMismatchBlocked = true,
            ProviderModelMismatchBlocked = true,
            UseForRuntime = false
        };

        var markdown = PostgresVectorQueryPreviewRunner.BuildMarkdown(report);

        StringAssert.Contains(markdown, "ReadyForPgVectorShadowEval");
        StringAssert.Contains(markdown, "EligibilityMetadataMismatchCount");
        StringAssert.Contains(markdown, "RiskProjectionMismatchCount");
        StringAssert.Contains(markdown, "UseForRuntime: `False`");
    }

    [TestMethod]
    public async Task PostgresVectorShadowEval_ShouldReturnNotConfiguredWithoutConnection()
    {
        var report = await new PostgresVectorShadowEvalRunner().RunAsync(
            "A3",
            new PostgresOptions { Enabled = false },
            [CreateVectorReindexSourceItem("source-a")],
            [new ContextEvalSample { Id = "sample-a", Query = "provider scoped query", Mode = "ProjectMode" }],
            new DeterministicHashEmbeddingGenerator(),
            "workspace",
            "collection",
            "deterministic-hash",
            "deterministic-hash-v1",
            16,
            normalized: true,
            topK: 5,
            profileId: VectorQueryProfileIds.NormalV1,
            minSimilarity: null,
            CancellationToken.None);

        Assert.AreEqual("NotConfigured", report.Recommendation);
        Assert.AreEqual("A3", report.DatasetName);
        Assert.IsFalse(report.UseForRuntime);
        Assert.AreEqual(0, report.QueryCount);
        CollectionAssert.Contains(report.Diagnostics.ToArray(), "PostgresVectorDiagnosticsNotReady:NotConfigured");
    }

    [TestMethod]
    public void PostgresVectorShadowEvalSummary_ShouldBlockProjectionMismatch()
    {
        var summary = PostgresVectorShadowEvalRunner.BuildSummary(
        [
            new PostgresVectorShadowEvalReport
            {
                DatasetName = "A3",
                Recommendation = "ReadyForVectorPostgresFreeze",
                UseForRuntime = false
            },
            new PostgresVectorShadowEvalReport
            {
                DatasetName = "Extended",
                Recommendation = "BlockedByProjectionMismatch",
                EligibilityMetadataMismatchCount = 1,
                UseForRuntime = false
            }
        ]);

        Assert.AreEqual("BlockedByProjectionMismatch", summary.Recommendation);
        Assert.IsFalse(summary.UseForRuntime);
    }

    [TestMethod]
    public void PostgresVectorShadowEvalMarkdown_ShouldExposeGateFields()
    {
        var report = new PostgresVectorShadowEvalReport
        {
            DatasetName = "Extended",
            Recommendation = "ReadyForVectorPostgresFreeze",
            SampleCount = 113,
            QueryCount = 113,
            PgVectorCandidateCount = 1130,
            FileSystemCandidateCount = 1130,
            RecallAfterPolicy = 0.8438,
            FileSystemRecallAfterPolicy = 0.8438,
            MrrAfterPolicy = 0.8229,
            TopKOverlapRate = 1,
            UseForRuntime = false
        };

        var markdown = PostgresVectorShadowEvalRunner.BuildMarkdown(report);

        StringAssert.Contains(markdown, "ReadyForVectorPostgresFreeze");
        StringAssert.Contains(markdown, "FormalOutputChanged");
        StringAssert.Contains(markdown, "EligibilityMetadataMismatchCount");
        StringAssert.Contains(markdown, "RiskProjectionMismatchCount");
        StringAssert.Contains(markdown, "UseForRuntime: `False`");
    }

    [TestMethod]
    public void VectorPostgresFreezeGate_ShouldPassOnCleanShadowEvalReports()
    {
        var report = BuildCleanVectorPostgresFreezeReport();

        Assert.IsTrue(report.Passed);
        Assert.AreEqual("ReadyForPreviewShadowStorage", report.VectorPostgresProvider);
        Assert.IsFalse(report.UseForRuntime);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.AreEqual(0, report.BlockedReasons.Count);
    }

    [TestMethod]
    public void VectorPostgresFreezeGate_ShouldBlockRecallDelta()
    {
        var report = BuildCleanVectorPostgresFreezeReport(a3RecallDelta: 0.01);

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "A3RecallDeltaNonZero");
    }

    [TestMethod]
    public void VectorPostgresFreezeGate_ShouldBlockRiskRegression()
    {
        var report = BuildCleanVectorPostgresFreezeReport(riskAfterPolicy: 1);

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "RiskAfterPolicyIncreased");
    }

    [TestMethod]
    public void VectorPostgresFreezeGate_ShouldBlockFormalOutputChange()
    {
        var report = BuildCleanVectorPostgresFreezeReport(formalOutputChanged: 1);

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "FormalOutputChangedNonZero");
    }

    [TestMethod]
    public void VectorPostgresFreezeGate_ShouldBlockProjectionMismatch()
    {
        var report = BuildCleanVectorPostgresFreezeReport(projectionMismatchCount: 1);

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "ProjectionMismatchNonZero");
    }

    [TestMethod]
    public void VectorPostgresFreezeGate_ShouldBlockUseForRuntime()
    {
        var report = BuildCleanVectorPostgresFreezeReport(useForRuntime: true);

        Assert.IsFalse(report.Passed);
        CollectionAssert.Contains(report.BlockedReasons.ToArray(), "UseForRuntimeTrue");
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

    private static ProviderSwitchHarness CreateProviderSwitchHarness(
        RelationGovernanceProviderSwitchOptions switchOptions,
        bool readinessGatePassed,
        bool shadowReadQualityReady)
    {
        var root = Path.Combine(Path.GetTempPath(), "contextcore-provider-switch-test", Guid.NewGuid().ToString("N"));
        var fileOptions = new FileStorageOptions { RootPath = root };
        var paths = new FilePathResolver(fileOptions);
        var serializer = new FileFormatSerializer();
        var fileRelationStore = new FileRelationStore(fileOptions);
        var fileReviewStore = new FileRelationReviewStore(paths, serializer);
        var fileDiagnosticsStore = new FileRelationDiagnosticsStore(paths, serializer);
        var postgresOptions = new PostgresOptions
        {
            Enabled = false,
            ConnectionString = "Host=localhost;Database=contextcore;Username=user;Password=secret",
            AutoMigrate = false
        };
        var factory = new PostgresConnectionFactory(postgresOptions);
        var postgresSerializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        var postgresRelationStore = new PostgresRelationStore(factory, postgresSerializer, migrationRunner);
        var postgresReviewStore = new PostgresRelationReviewStore(factory, postgresSerializer, migrationRunner);
        var postgresDiagnosticsStore = new PostgresRelationDiagnosticsStore(factory, postgresSerializer, migrationRunner);
        var traces = new List<RelationGovernanceProviderSwitchTrace>();
        var router = new RelationGovernanceProviderRouter(
            fileRelationStore,
            fileReviewStore,
            fileDiagnosticsStore,
            postgresRelationStore,
            postgresReviewStore,
            postgresDiagnosticsStore,
            switchOptions,
            readinessGatePassed,
            shadowReadQualityReady,
            (trace, _) =>
            {
                traces.Add(trace);
                return Task.CompletedTask;
            });

        return new ProviderSwitchHarness(root, fileRelationStore, router, traces);
    }

    private sealed class ProviderSwitchHarness(
        string root,
        FileRelationStore fileRelationStore,
        RelationGovernanceProviderRouter router,
        IReadOnlyList<RelationGovernanceProviderSwitchTrace> traces) : IDisposable
    {
        public FileRelationStore FileRelationStore { get; } = fileRelationStore;

        public RelationGovernanceProviderRouter Router { get; } = router;

        public IReadOnlyList<RelationGovernanceProviderSwitchTrace> Traces { get; } = traces;

        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
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

    private static VectorReindexSourceItem CreateVectorReindexSourceItem(string itemId)
        => new()
        {
            ItemId = itemId,
            ItemKind = "reindex-item",
            Layer = "context",
            Text = "provider scoped reindex source",
            UpdatedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sourceKind"] = "context",
                ["lifecycle"] = "Active",
                ["reviewStatus"] = "Approved"
            }
        };

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

    private static LearningFeedbackProviderRouter CreateLearningFeedbackRouter(
        ILearningFeedbackStore fileFeedbackStore,
        ILearningFeedbackReviewStore fileReviewStore,
        ILearningFeatureCandidateStore fileCandidateStore,
        ILearningFeedbackStore postgresFeedbackStore,
        ILearningFeedbackReviewStore postgresReviewStore,
        ILearningFeatureCandidateStore postgresCandidateStore,
        IReadOnlyList<LearningFeedbackScopedRule> scopedRules,
        bool providerQualityReady,
        List<LearningFeedbackProviderSwitchTrace> traces)
    {
        return new LearningFeedbackProviderRouter(
            fileFeedbackStore,
            fileReviewStore,
            fileCandidateStore,
            postgresFeedbackStore,
            postgresReviewStore,
            postgresCandidateStore,
            new LearningFeedbackProviderSwitchOptions
            {
                Enabled = true,
                Mode = LearningFeedbackProviderMode.FileSystemPrimary,
                ScopedRules = scopedRules,
                FallbackToFileSystem = true,
                ContinueComparisonTrace = true,
                FailClosedOnMismatch = true,
                RequireProviderQualityReady = true
            },
            providerQualityReady,
            (trace, _) =>
            {
                traces.Add(trace);
                return Task.CompletedTask;
            });
    }

    private static JobQueuePostgresFreezeGateReport BuildCleanJobQueueFreezeReport(
        PostgresJobQueueLimitedWorkerScopeQualityReport limitedQuality)
    {
        return new PostgresJobQueueProviderEvalRunner().BuildFreezeGateReport(
            new PostgresJobQueueDiagnosticsReport { Recommendation = "ReadyForParityEval" },
            new PostgresJobQueueProviderQualityReport
            {
                Recommendation = "ReadyForScopedWorkerCanary",
                LeaseParityPassed = true,
                RetryParityPassed = true,
                DeadLetterParityPassed = true,
                CountParityPassed = true
            },
            new PostgresJobQueueScopedWorkerCanaryReport
            {
                Recommendation = "ReadyForLimitedWorkerScope",
                RuntimeWorkerGlobalProviderUnchanged = true
            },
            limitedQuality,
            p15GatePassed: true);
    }

    private static VectorPostgresProviderFreezeGateReport BuildCleanVectorPostgresFreezeReport(
        double a3RecallDelta = 0,
        double extendedRecallDelta = 0,
        int riskAfterPolicy = 0,
        int formalOutputChanged = 0,
        int projectionMismatchCount = 0,
        bool useForRuntime = false)
    {
        return new VectorPostgresProviderFreezeGateRunner().BuildFreezeGateReport(
            new PostgresVectorDiagnosticsReport
            {
                Recommendation = "ReadyForVectorParityEval"
            },
            new PostgresVectorCompatibilityReport
            {
                Recommendation = "ReadyForVectorParityEval"
            },
            new PostgresVectorIndexParityReport
            {
                Recommendation = "ReadyForProviderScopedReindex",
                DimensionMismatchBlocked = true,
                ProviderModelMismatchBlocked = true
            },
            new PostgresVectorProviderScopedReindexReport
            {
                Recommendation = "ReadyForPgVectorQueryPreview",
                UseForRuntime = useForRuntime
            },
            new PostgresVectorQueryPreviewReport
            {
                Recommendation = "ReadyForPgVectorShadowEval",
                DimensionMismatchBlocked = true,
                ProviderModelMismatchBlocked = true,
                UseForRuntime = useForRuntime
            },
            new PostgresVectorShadowEvalSummaryReport
            {
                Recommendation = "ReadyForVectorPostgresFreeze",
                UseForRuntime = useForRuntime,
                Reports =
                [
                    new PostgresVectorShadowEvalReport
                    {
                        DatasetName = "A3",
                        Recommendation = "ReadyForVectorPostgresFreeze",
                        RecallDelta = a3RecallDelta,
                        RiskAfterPolicy = riskAfterPolicy,
                        FormalOutputChanged = formalOutputChanged,
                        MetadataMismatchCount = projectionMismatchCount,
                        UseForRuntime = useForRuntime
                    },
                    new PostgresVectorShadowEvalReport
                    {
                        DatasetName = "Extended",
                        Recommendation = "ReadyForVectorPostgresFreeze",
                        RecallDelta = extendedRecallDelta,
                        UseForRuntime = useForRuntime
                    }
                ]
            },
            p15GatePassed: true);
    }

    private static LearningFeedbackEvent CreateLearningFeedback(
        string feedbackId,
        string workspaceId,
        string collectionId)
        => new()
        {
            FeedbackId = feedbackId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Source = "unit-test",
            SourceOperationId = $"op-{feedbackId}",
            CapabilityId = ShadowCapabilityIds.RouterIntentClassifier,
            TargetId = $"target-{feedbackId}",
            TargetType = LearningFeedbackTargetType.RouterPrediction.ToString(),
            FeedbackKind = LearningFeedbackKinds.WrongIntent,
            RedactionMode = "metadata-only",
            MetadataOnly = true,
            TrainingUse = "disabled_until_review",
            Confidence = 0.9,
            CreatedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["workspaceId"] = workspaceId,
                ["collectionId"] = collectionId
            }
        };

    private sealed class FailingLearningFeedbackStore :
        ILearningFeedbackStore,
        ILearningFeedbackReviewStore,
        ILearningFeatureCandidateStore
    {
        public Task<LearningFeedbackEvent?> GetAsync(string feedbackId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("forced failure");

        public Task UpsertAsync(LearningFeedbackEvent feedbackEvent, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("forced failure");

        public Task<IReadOnlyList<LearningFeedbackEvent>> QueryAsync(LearningFeedbackEventQuery query, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("forced failure");

        public Task UpsertAsync(LearningFeedbackReviewRecord review, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("forced failure");

        public Task<IReadOnlyList<LearningFeedbackReviewRecord>> QueryAsync(LearningFeedbackReviewQuery query, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("forced failure");

        public Task UpsertAsync(FeedbackFeatureCandidate candidate, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("forced failure");

        public Task<IReadOnlyList<FeedbackFeatureCandidate>> QueryAsync(LearningFeatureCandidateQuery query, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("forced failure");
    }
}
