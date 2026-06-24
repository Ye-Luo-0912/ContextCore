using System.Globalization;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>统一学习 shadow readiness 冻结 runner；只读取报告，不修改 runtime 行为。</summary>
public sealed class LearningReadinessFreezeRunner
{
    public const string PolicyVersion = "learning-readiness-freeze-s0/v1";
    public const string DefaultOutputDirectory = "learning/readiness";
    public const string FreezeReportFileName = "learning-readiness-freeze-report.json";
    public const string FreezeMarkdownFileName = "learning-readiness-freeze-report.md";
    public const string RuntimeGateFileName = "learning-runtime-change-readiness-gate.json";
    public const string RuntimeGateMarkdownFileName = "learning-runtime-change-readiness-gate.md";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<LearningReadinessRegistry> RunFreezeReportAsync(
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var resolvedOutput = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(resolvedOutput);
        var registry = await BuildRegistryFromCurrentFilesAsync(cancellationToken).ConfigureAwait(false);
        var jsonPath = Path.Combine(resolvedOutput, FreezeReportFileName);
        var markdownPath = Path.Combine(resolvedOutput, FreezeMarkdownFileName);

        await File.WriteAllTextAsync(
                jsonPath,
                JsonSerializer.Serialize(registry, JsonOptions),
                cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(markdownPath, BuildMarkdownReport(registry), cancellationToken)
            .ConfigureAwait(false);
        return registry;
    }

    public async Task<LearningRuntimeChangeReadinessGateReport> RunRuntimeChangeGateAsync(
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var resolvedOutput = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(resolvedOutput);
        var registryPath = Path.Combine(resolvedOutput, FreezeReportFileName);
        var registry = await ReadJsonAsync<LearningReadinessRegistry>(registryPath, cancellationToken)
                .ConfigureAwait(false)
            ?? await BuildRegistryFromCurrentFilesAsync(cancellationToken).ConfigureAwait(false);
        if (!registry.Capabilities.Any(static item => string.Equals(item.CapabilityId, ShadowCapabilityIds.DatasetV2Stress, StringComparison.OrdinalIgnoreCase))
            || !registry.Capabilities.Any(static item => string.Equals(item.CapabilityId, ShadowCapabilityIds.VectorV4ReadinessRecheck, StringComparison.OrdinalIgnoreCase))
            || !registry.Capabilities.Any(static item => string.Equals(item.CapabilityId, ShadowCapabilityIds.GuardedFormalRetrievalPreview, StringComparison.OrdinalIgnoreCase))
            || !registry.Capabilities.Any(static item => string.Equals(item.CapabilityId, ShadowCapabilityIds.VectorShadowPackageComparison, StringComparison.OrdinalIgnoreCase))
            || !registry.Capabilities.Any(static item => string.Equals(item.CapabilityId, ShadowCapabilityIds.ScopedFormalPreviewOptIn, StringComparison.OrdinalIgnoreCase))
            || !registry.Capabilities.Any(static item => string.Equals(item.CapabilityId, ShadowCapabilityIds.LimitedFormalPreviewObservation, StringComparison.OrdinalIgnoreCase))
            || !registry.Capabilities.Any(static item => string.Equals(item.CapabilityId, ShadowCapabilityIds.VectorFormalPreviewFreeze, StringComparison.OrdinalIgnoreCase))
            || !registry.Capabilities.Any(static item => string.Equals(item.CapabilityId, ShadowCapabilityIds.ScopedRuntimeExperimentHarnessFreeze, StringComparison.OrdinalIgnoreCase)))
        {
            registry = await BuildRegistryFromCurrentFilesAsync(cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                    registryPath,
                    JsonSerializer.Serialize(registry, JsonOptions),
                    cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(
                    Path.Combine(resolvedOutput, FreezeMarkdownFileName),
                    BuildMarkdownReport(registry),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var report = BuildRuntimeChangeGate(registry, registryPath);
        var jsonPath = Path.Combine(resolvedOutput, RuntimeGateFileName);
        var markdownPath = Path.Combine(resolvedOutput, RuntimeGateMarkdownFileName);

        await File.WriteAllTextAsync(
                jsonPath,
                JsonSerializer.Serialize(report, JsonOptions),
                cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(markdownPath, BuildRuntimeChangeGateMarkdown(report), cancellationToken)
            .ConfigureAwait(false);
        return report;
    }

    public async Task<LearningReadinessRegistry> BuildRegistryFromCurrentFilesAsync(
        CancellationToken cancellationToken = default)
    {
        var current = Directory.GetCurrentDirectory();
        var capabilities = new List<ShadowCapabilityReadiness>
        {
            await BuildRelationGovernanceAsync(current, cancellationToken).ConfigureAwait(false),
            await BuildJobQueuePostgresAsync(current, cancellationToken).ConfigureAwait(false),
            await BuildVectorPostgresProviderAsync(current, cancellationToken).ConfigureAwait(false),
            await BuildQwen3EmbeddingProviderAsync(current, cancellationToken).ConfigureAwait(false),
            await BuildCurrentEmbeddingProviderAsync(current, cancellationToken).ConfigureAwait(false),
            await BuildGraphExpansionAsync(current, cancellationToken).ConfigureAwait(false),
            await BuildVectorRetrievalAsync(current, cancellationToken).ConfigureAwait(false),
            await BuildHybridRetrievalPreviewAsync(current, cancellationToken).ConfigureAwait(false),
            await BuildDatasetV2StressAsync(current, cancellationToken).ConfigureAwait(false),
            await BuildVectorV4ReadinessRecheckAsync(current, cancellationToken).ConfigureAwait(false),
            await BuildGuardedFormalRetrievalPreviewAsync(current, cancellationToken).ConfigureAwait(false),
            await BuildVectorShadowPackageComparisonAsync(current, cancellationToken).ConfigureAwait(false),
            await BuildScopedFormalPreviewOptInAsync(current, cancellationToken).ConfigureAwait(false),
            await BuildLimitedFormalPreviewObservationAsync(current, cancellationToken).ConfigureAwait(false),
            await BuildVectorFormalPreviewFreezeAsync(current, cancellationToken).ConfigureAwait(false),
            await BuildScopedRuntimeExperimentHarnessFreezeAsync(current, cancellationToken).ConfigureAwait(false),
            await BuildRouterIntentClassifierAsync(current, cancellationToken).ConfigureAwait(false),
            await BuildCandidateRerankerAsync(current, cancellationToken).ConfigureAwait(false),
            BuildAttentionRerank(current),
            BuildPlanningProposal(current)
        };
        var readyCount = capabilities.Count(item => item.GatePassed);
        var blockedCount = capabilities.Count - readyCount;

        return new LearningReadinessRegistry
        {
            OperationId = $"learning-readiness-freeze-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            PolicyVersion = PolicyVersion,
            Capabilities = capabilities,
            ReadyCount = readyCount,
            BlockedCount = blockedCount,
            OverallRecommendation = blockedCount == 0 ? "AllGuardedCapabilitiesReady" : "KeepRuntimeDefaults",
            Warnings =
            [
                "Registry 只记录 readiness，不启用任何正式 runtime 能力。",
                "Vector、router、candidate reranker 在 gate 未通过前不得进入 runtime shadow 或 opt-in。"
            ]
        };
    }

    public LearningRuntimeChangeReadinessGateReport BuildRuntimeChangeGate(
        LearningReadinessRegistry registry,
        string registryReportPath = "")
    {
        ArgumentNullException.ThrowIfNull(registry);

        var checks = new List<LearningRuntimeChangeReadinessGateCheck>();
        foreach (var capability in registry.Capabilities)
        {
            if (!capability.GatePassed)
            {
                AddCheck(
                    checks,
                    capability,
                    "NotReadyDoesNotAllowRuntimeModes",
                    !AllowsAny(capability, ShadowRuntimeModes.ApplyGuarded, ShadowRuntimeModes.RuntimeShadow, ShadowRuntimeModes.DefaultOn),
                    "未通过 readiness 的 capability 不得允许 ApplyGuarded / RuntimeShadow / DefaultOn。");
            }
        }

        var vector = Find(registry, ShadowCapabilityIds.VectorRetrieval);
        if (vector is not null)
        {
            AddCheck(
                checks,
                vector,
                "VectorV4GateBlocksRuntimeShadow",
                !vector.GatePassed
                    && IsForbidden(vector, ShadowRuntimeModes.RuntimeShadow)
                    && IsForbidden(vector, ShadowRuntimeModes.ApplyGuarded),
                "Vector V4 gate 未通过时必须禁止 RuntimeShadow / ApplyGuarded。");
        }

        var hybridRetrieval = Find(registry, ShadowCapabilityIds.HybridRetrievalPreview);
        if (hybridRetrieval is not null)
        {
            AddCheck(
                checks,
                hybridRetrieval,
                "HybridRetrievalFormalRetrievalSwitchForbiddenWithoutV4Gate",
                IsForbidden(hybridRetrieval, "FormalRetrievalSwitch")
                    && IsForbidden(hybridRetrieval, "FormalHybridRetrievalSwitch"),
                "HybridRetrieval 未过 V4 gate 时不得接 formal retrieval。");
            AddCheck(
                checks,
                hybridRetrieval,
                "HybridRetrievalPackingPolicyIntegrationForbidden",
                IsForbidden(hybridRetrieval, "PackingPolicyIntegration")
                    && IsForbidden(hybridRetrieval, "PackageOutputIntegration"),
                "HybridRetrieval preview 不得影响 PackingPolicy / package output。");
            AddCheck(
                checks,
                hybridRetrieval,
                "HybridRetrievalFormalSourceReplacementForbidden",
                IsForbidden(hybridRetrieval, "FormalRetrievalSourceReplacement"),
                "HybridRetrieval preview 不得替代正式 retrieval source。");
        }

        var datasetV2Stress = Find(registry, ShadowCapabilityIds.DatasetV2Stress);
        if (datasetV2Stress is not null)
        {
            AddCheck(
                checks,
                datasetV2Stress,
                "DatasetV2StressFreezeDoesNotAllowFormalRetrieval",
                IsForbidden(datasetV2Stress, "FormalRetrievalSwitch")
                    && IsForbidden(datasetV2Stress, "ReadyForFormalRetrieval")
                    && IsForbidden(datasetV2Stress, "FormalIVectorIndexStoreBinding"),
                "Dataset V2 stress freeze 通过也只允许作为 V4 复核输入，不得开启 formal retrieval。");
            AddCheck(
                checks,
                datasetV2Stress,
                "PostScoringRiskGatedProfileRuntimeUseForbidden",
                IsForbidden(datasetV2Stress, "PostScoringRiskGatedV1:Runtime")
                    && IsForbidden(datasetV2Stress, "post-scoring-risk-gated-v1:Runtime"),
                "post-scoring-risk-gated-v1 不得直接接入 runtime。");
            AddCheck(
                checks,
                datasetV2Stress,
                "DatasetV2StressPackingPolicyIntegrationForbiddenWithoutV4Gate",
                IsForbidden(datasetV2Stress, "PackingPolicyIntegration")
                    && IsForbidden(datasetV2Stress, "PackageOutputIntegration"),
                "未通过 V4 formal readiness gate 前不得改变 PackingPolicy / package output。");
        }

        var vectorV4Recheck = Find(registry, ShadowCapabilityIds.VectorV4ReadinessRecheck);
        if (vectorV4Recheck is not null)
        {
            AddCheck(
                checks,
                vectorV4Recheck,
                "VectorV4RecheckDoesNotAllowRuntimeSwitch",
                IsForbidden(vectorV4Recheck, "ReadyForRuntimeSwitch")
                    && IsForbidden(vectorV4Recheck, "RuntimeSwitch")
                    && IsForbidden(vectorV4Recheck, ShadowRuntimeModes.DefaultOn),
                "V4.R 通过也不等于 runtime switch。");
            AddCheck(
                checks,
                vectorV4Recheck,
                "VectorV4RecheckFormalRetrievalStillForbidden",
                IsForbidden(vectorV4Recheck, "FormalRetrievalSwitch")
                    && IsForbidden(vectorV4Recheck, "FormalRetrievalAllowed")
                    && IsForbidden(vectorV4Recheck, "FormalIVectorIndexStoreBinding"),
                "V4.R 阶段 formal retrieval 仍保持禁用。");
            AddCheck(
                checks,
                vectorV4Recheck,
                "VectorV4RecheckPackingPolicyIntegrationForbidden",
                IsForbidden(vectorV4Recheck, "PackingPolicyIntegration")
                    && IsForbidden(vectorV4Recheck, "PackageOutputIntegration"),
                "V4.R 不得改变 PackingPolicy / package output。");
        }

        var guardedFormalPreview = Find(registry, ShadowCapabilityIds.GuardedFormalRetrievalPreview);
        if (guardedFormalPreview is not null)
        {
            AddCheck(
                checks,
                guardedFormalPreview,
                "GuardedFormalPreviewDoesNotAllowRuntimeSwitch",
                IsForbidden(guardedFormalPreview, "ReadyForRuntimeSwitch")
                    && IsForbidden(guardedFormalPreview, "RuntimeSwitch")
                    && IsForbidden(guardedFormalPreview, ShadowRuntimeModes.DefaultOn),
                "Guarded formal retrieval preview 通过也不等于 runtime switch。");
            AddCheck(
                checks,
                guardedFormalPreview,
                "GuardedFormalPreviewFormalRetrievalStillForbidden",
                IsForbidden(guardedFormalPreview, "FormalRetrievalSwitch")
                    && IsForbidden(guardedFormalPreview, "FormalRetrievalAllowed")
                    && IsForbidden(guardedFormalPreview, "FormalIVectorIndexStoreBinding"),
                "V4.1 阶段 formal retrieval 仍保持禁用。");
            AddCheck(
                checks,
                guardedFormalPreview,
                "GuardedFormalPreviewPackageMutationForbidden",
                IsForbidden(guardedFormalPreview, "PackingPolicyIntegration")
                    && IsForbidden(guardedFormalPreview, "PackageOutputIntegration")
                    && IsForbidden(guardedFormalPreview, "FormalPackageWrite"),
                "V4.1 preview 不得改变 PackingPolicy 或写正式 package。");
        }

        var shadowPackageComparison = Find(registry, ShadowCapabilityIds.VectorShadowPackageComparison);
        if (shadowPackageComparison is not null)
        {
            AddCheck(
                checks,
                shadowPackageComparison,
                "VectorShadowPackageComparisonDoesNotAllowRuntimeSwitch",
                IsForbidden(shadowPackageComparison, "ReadyForRuntimeSwitch")
                    && IsForbidden(shadowPackageComparison, "RuntimeSwitch")
                    && IsForbidden(shadowPackageComparison, ShadowRuntimeModes.DefaultOn),
                "V4.2 shadow package comparison 通过也不等于 runtime switch。");
            AddCheck(
                checks,
                shadowPackageComparison,
                "VectorShadowPackageComparisonFormalRetrievalStillForbidden",
                IsForbidden(shadowPackageComparison, "FormalRetrievalSwitch")
                    && IsForbidden(shadowPackageComparison, "FormalRetrievalAllowed")
                    && IsForbidden(shadowPackageComparison, "FormalIVectorIndexStoreBinding"),
                "V4.2 阶段 formal retrieval 仍保持禁用。");
            AddCheck(
                checks,
                shadowPackageComparison,
                "VectorShadowPackageComparisonPackageMutationForbidden",
                IsForbidden(shadowPackageComparison, "PackingPolicyIntegration")
                    && IsForbidden(shadowPackageComparison, "PackageOutputIntegration")
                    && IsForbidden(shadowPackageComparison, "FormalPackageWrite"),
                "V4.2 shadow package comparison 不得改变 PackingPolicy 或写正式 package。");
        }

        var scopedFormalPreviewOptIn = Find(registry, ShadowCapabilityIds.ScopedFormalPreviewOptIn);
        if (scopedFormalPreviewOptIn is not null)
        {
            AddCheck(
                checks,
                scopedFormalPreviewOptIn,
                "ScopedFormalPreviewOptInDoesNotAllowRuntimeSwitch",
                IsForbidden(scopedFormalPreviewOptIn, "ReadyForRuntimeSwitch")
                    && IsForbidden(scopedFormalPreviewOptIn, "RuntimeSwitch")
                    && IsForbidden(scopedFormalPreviewOptIn, ShadowRuntimeModes.DefaultOn),
                "V4.3 scoped formal preview opt-in 通过也不等于 runtime switch。");
            AddCheck(
                checks,
                scopedFormalPreviewOptIn,
                "ScopedFormalPreviewOptInFormalRetrievalStillForbidden",
                IsForbidden(scopedFormalPreviewOptIn, "FormalRetrievalSwitch")
                    && IsForbidden(scopedFormalPreviewOptIn, "FormalRetrievalAllowed")
                    && IsForbidden(scopedFormalPreviewOptIn, "FormalIVectorIndexStoreBinding"),
                "V4.3 阶段 formal retrieval 仍保持禁用。");
            AddCheck(
                checks,
                scopedFormalPreviewOptIn,
                "ScopedFormalPreviewOptInPackageMutationForbidden",
                IsForbidden(scopedFormalPreviewOptIn, "PackingPolicyIntegration")
                    && IsForbidden(scopedFormalPreviewOptIn, "PackageOutputIntegration")
                    && IsForbidden(scopedFormalPreviewOptIn, "FormalPackageWrite"),
                "V4.3 scoped preview 不得改变 PackingPolicy 或写正式 package。");
        }

        var limitedFormalPreviewObservation = Find(registry, ShadowCapabilityIds.LimitedFormalPreviewObservation);
        if (limitedFormalPreviewObservation is not null)
        {
            AddCheck(
                checks,
                limitedFormalPreviewObservation,
                "LimitedFormalPreviewObservationDoesNotAllowRuntimeSwitch",
                IsForbidden(limitedFormalPreviewObservation, "ReadyForRuntimeSwitch")
                    && IsForbidden(limitedFormalPreviewObservation, "RuntimeSwitch")
                    && IsForbidden(limitedFormalPreviewObservation, ShadowRuntimeModes.DefaultOn),
                "V4.4 limited formal preview observation 通过也不等于 runtime switch。");
            AddCheck(
                checks,
                limitedFormalPreviewObservation,
                "LimitedFormalPreviewObservationFormalRetrievalStillForbidden",
                IsForbidden(limitedFormalPreviewObservation, "FormalRetrievalSwitch")
                    && IsForbidden(limitedFormalPreviewObservation, "FormalRetrievalAllowed")
                    && IsForbidden(limitedFormalPreviewObservation, "FormalIVectorIndexStoreBinding"),
                "V4.4 阶段 formal retrieval 仍保持禁用。");
            AddCheck(
                checks,
                limitedFormalPreviewObservation,
                "LimitedFormalPreviewObservationPackageMutationForbidden",
                IsForbidden(limitedFormalPreviewObservation, "PackingPolicyIntegration")
                    && IsForbidden(limitedFormalPreviewObservation, "PackageOutputIntegration")
                    && IsForbidden(limitedFormalPreviewObservation, "FormalPackageWrite"),
                "V4.4 observation 不得改变 PackingPolicy 或写正式 package。");
        }

        var vectorFormalPreviewFreeze = Find(registry, ShadowCapabilityIds.VectorFormalPreviewFreeze);
        if (vectorFormalPreviewFreeze is not null)
        {
            AddCheck(
                checks,
                vectorFormalPreviewFreeze,
                "VectorFormalPreviewFreezeDoesNotAllowRuntimeSwitch",
                IsForbidden(vectorFormalPreviewFreeze, "ReadyForRuntimeSwitch")
                    && IsForbidden(vectorFormalPreviewFreeze, "RuntimeSwitch")
                    && IsForbidden(vectorFormalPreviewFreeze, ShadowRuntimeModes.DefaultOn),
                "V4.F formal preview freeze 通过也不等于 runtime switch。");
            AddCheck(
                checks,
                vectorFormalPreviewFreeze,
                "VectorFormalPreviewFreezeFormalRetrievalStillForbidden",
                IsForbidden(vectorFormalPreviewFreeze, "FormalRetrievalSwitch")
                    && IsForbidden(vectorFormalPreviewFreeze, "FormalRetrievalAllowed")
                    && IsForbidden(vectorFormalPreviewFreeze, "FormalIVectorIndexStoreBinding"),
                "V4.F 阶段 formal retrieval 仍保持禁用。");
            AddCheck(
                checks,
                vectorFormalPreviewFreeze,
                "VectorFormalPreviewFreezePackageMutationForbidden",
                IsForbidden(vectorFormalPreviewFreeze, "PackingPolicyIntegration")
                    && IsForbidden(vectorFormalPreviewFreeze, "PackageOutputIntegration")
                    && IsForbidden(vectorFormalPreviewFreeze, "FormalPackageWrite"),
                "V4.F freeze 不得改变 PackingPolicy、写正式 package 或改变 package output。");
        }

        var scopedRuntimeExperimentHarnessFreeze = Find(registry, ShadowCapabilityIds.ScopedRuntimeExperimentHarnessFreeze);
        if (scopedRuntimeExperimentHarnessFreeze is not null)
        {
            AddCheck(
                checks,
                scopedRuntimeExperimentHarnessFreeze,
                "ScopedRuntimeExperimentHarnessFreezeDoesNotAllowRuntimeSwitch",
                IsForbidden(scopedRuntimeExperimentHarnessFreeze, "ReadyForRuntimeSwitch")
                    && IsForbidden(scopedRuntimeExperimentHarnessFreeze, "RuntimeSwitch")
                    && IsForbidden(scopedRuntimeExperimentHarnessFreeze, ShadowRuntimeModes.DefaultOn),
                "V4.10 no-op harness freeze 通过也不等于 runtime switch。");
            AddCheck(
                checks,
                scopedRuntimeExperimentHarnessFreeze,
                "ScopedRuntimeExperimentHarnessFreezeFormalRetrievalStillForbidden",
                IsForbidden(scopedRuntimeExperimentHarnessFreeze, "FormalRetrievalSwitch")
                    && IsForbidden(scopedRuntimeExperimentHarnessFreeze, "FormalRetrievalAllowed")
                    && IsForbidden(scopedRuntimeExperimentHarnessFreeze, "FormalIVectorIndexStoreBinding"),
                "V4.10 阶段 formal retrieval 仍保持禁用。");
            AddCheck(
                checks,
                scopedRuntimeExperimentHarnessFreeze,
                "ScopedRuntimeExperimentHarnessFreezePackageAndBindingMutationForbidden",
                IsForbidden(scopedRuntimeExperimentHarnessFreeze, "FormalPackageWrite")
                    && IsForbidden(scopedRuntimeExperimentHarnessFreeze, "DIBindingMutation")
                    && IsForbidden(scopedRuntimeExperimentHarnessFreeze, "VectorStoreBindingMutation")
                    && IsForbidden(scopedRuntimeExperimentHarnessFreeze, "PackingPolicyIntegration")
                    && IsForbidden(scopedRuntimeExperimentHarnessFreeze, "PackageOutputIntegration"),
                "V4.10 freeze 不得写正式 package、改变 DI/vector binding、PackingPolicy 或 package output。");
            AddCheck(
                checks,
                scopedRuntimeExperimentHarnessFreeze,
                "NoOpHarnessOnlyIsNotRuntimeApproval",
                IsForbidden(scopedRuntimeExperimentHarnessFreeze, "NoOpHarnessOnly:RuntimeApproval")
                    && IsForbidden(scopedRuntimeExperimentHarnessFreeze, "NoOpHarnessOnlyAsRuntimeApproval"),
                "ApprovalMode=NoOpHarnessOnly 不能被解释为 runtime approval。");
        }

        var router = Find(registry, ShadowCapabilityIds.RouterIntentClassifier);
        if (router is not null && router.BlockedReasons.Any(IsRouterBreakReason))
        {
            AddCheck(
                checks,
                router,
                "RouterBreaksBlockGuardedOptIn",
                IsForbidden(router, ShadowRuntimeModes.ApplyGuarded)
                    && IsForbidden(router, ShadowRuntimeModes.DefaultOn),
                "Router breaks > fixes 时必须禁止 guarded opt-in。");
        }

        var ranker = Find(registry, ShadowCapabilityIds.CandidateReranker);
        if (ranker is not null && ranker.BlockedReasons.Any(IsRankerNetGainReason))
        {
            AddCheck(
                checks,
                ranker,
                "CandidateRerankerNetGainBlocksRuntime",
                IsForbidden(ranker, ShadowRuntimeModes.RuntimeShadow)
                    && IsForbidden(ranker, ShadowRuntimeModes.ApplyGuarded),
                "Candidate reranker netGain <= 0 时必须禁止 runtime shadow / opt-in。");
        }

        var graph = Find(registry, ShadowCapabilityIds.GraphExpansion);
        if (graph is not null)
        {
            AddCheck(
                checks,
                graph,
                "GraphNormalCurrentTaskForbidden",
                IsForbidden(graph, "ApplyGuarded:normal-v1")
                    && IsForbidden(graph, "ApplyGuarded:current-task-v1")
                    && IsForbidden(graph, ShadowRuntimeModes.DefaultOn),
                "Graph normal-v1 / current-task-v1 不得默认启用。");
        }

        var relationGovernance = Find(registry, ShadowCapabilityIds.RelationGovernance);
        if (relationGovernance is not null)
        {
            AddCheck(
                checks,
                relationGovernance,
                "RelationGovernanceGlobalDefaultOnForbidden",
                IsForbidden(relationGovernance, ShadowRuntimeModes.DefaultOn)
                    && IsForbidden(relationGovernance, "GlobalDefaultOn"),
                "Relation governance Postgres provider 不得 global default-on。");
            AddCheck(
                checks,
                relationGovernance,
                "RelationGovernanceRequiresFallback",
                relationGovernance.AllowedRuntimeModes.Any(static mode =>
                    mode.Contains("FallbackToFileSystem", StringComparison.OrdinalIgnoreCase))
                    && IsForbidden(relationGovernance, "GuardedPostgresPrimary:FallbackDisabled"),
                "GuardedPostgresPrimary 必须保留 FileSystem fallback。");
            AddCheck(
                checks,
                relationGovernance,
                "RelationGovernanceRequiresComparisonTrace",
                relationGovernance.AllowedRuntimeModes.Any(static mode =>
                    mode.Contains("ComparisonTrace", StringComparison.OrdinalIgnoreCase))
                    && IsForbidden(relationGovernance, "GuardedPostgresPrimary:ComparisonTraceDisabled"),
                "GuardedPostgresPrimary 必须保留 comparison trace。");
        }

        var jobQueuePostgres = Find(registry, ShadowCapabilityIds.JobQueuePostgres);
        if (jobQueuePostgres is not null)
        {
            AddCheck(
                checks,
                jobQueuePostgres,
                "JobQueueGlobalWorkerProviderSwitchForbidden",
                IsForbidden(jobQueuePostgres, "GlobalWorkerProviderSwitch")
                    && IsForbidden(jobQueuePostgres, ShadowRuntimeModes.DefaultOn),
                "Job queue Postgres provider 不得 global worker provider switch。");
            AddCheck(
                checks,
                jobQueuePostgres,
                "JobQueueRequiresScopedAllowlist",
                jobQueuePostgres.AllowedRuntimeModes.Any(static mode =>
                    mode.Contains("AllowlistedWorkerScopes", StringComparison.OrdinalIgnoreCase))
                    && IsForbidden(jobQueuePostgres, "GuardedPostgresPrimary:MissingScopedAllowlist"),
                "GuardedPostgresPrimary 必须限定 explicit allowlisted worker scopes。");
            AddCheck(
                checks,
                jobQueuePostgres,
                "JobQueueRequiresLeaseQualityGate",
                jobQueuePostgres.AllowedRuntimeModes.Any(static mode =>
                    mode.Contains("LeaseHeartbeat", StringComparison.OrdinalIgnoreCase))
                    && IsForbidden(jobQueuePostgres, "GuardedPostgresPrimary:MissingLeaseQualityGate"),
                "Job queue scoped worker 必须保留 lease / heartbeat quality gate。");
            AddCheck(
                checks,
                jobQueuePostgres,
                "JobQueueRequiresRetryDeadLetterQualityGate",
                jobQueuePostgres.AllowedRuntimeModes.Any(static mode =>
                    mode.Contains("RetryDeadLetter", StringComparison.OrdinalIgnoreCase))
                    && IsForbidden(jobQueuePostgres, "GuardedPostgresPrimary:MissingRetryDeadLetterQualityGate"),
                "Job queue scoped worker 必须保留 retry / dead-letter quality gate。");
        }

        var vectorPostgresProvider = Find(registry, ShadowCapabilityIds.VectorPostgresProvider);
        if (vectorPostgresProvider is not null)
        {
            AddCheck(
                checks,
                vectorPostgresProvider,
                "VectorPostgresFormalRetrievalSwitchForbidden",
                IsForbidden(vectorPostgresProvider, "FormalRetrievalSwitch")
                    && IsForbidden(vectorPostgresProvider, "PgVectorFormalRetrievalSwitch"),
                "pgvector provider freeze 后仍不得切换正式 vector retrieval。");
            AddCheck(
                checks,
                vectorPostgresProvider,
                "VectorPostgresFormalStoreBindingForbiddenWithoutV4Gate",
                IsForbidden(vectorPostgresProvider, "PostgresVectorIndexStoreFormalBinding")
                    && IsForbidden(vectorPostgresProvider, "FormalIVectorIndexStoreBindingWithoutV4Gate"),
                "未通过 V4 gate 前不得把 PostgresVectorIndexStore 绑定为正式 IVectorIndexStore。");
            AddCheck(
                checks,
                vectorPostgresProvider,
                "VectorPostgresPackingPolicyIntegrationForbiddenWithoutV4Gate",
                IsForbidden(vectorPostgresProvider, "PackingPolicyIntegration")
                    && IsForbidden(vectorPostgresProvider, "PackageOutputIntegration"),
                "未通过 V4 gate 前不得接入 PackingPolicy 或 package output。");
            AddCheck(
                checks,
                vectorPostgresProvider,
                "VectorPostgresRequiresShadowEvalRecallRiskGate",
                vectorPostgresProvider.GatePassed
                    && vectorPostgresProvider.AllowedRuntimeModes.Any(static mode =>
                        mode.Contains("PreviewShadowEvalOnly", StringComparison.OrdinalIgnoreCase))
                    && IsForbidden(vectorPostgresProvider, "MissingShadowEvalGate")
                    && IsForbidden(vectorPostgresProvider, "MissingRecallRiskGate"),
                "pgvector 只可作为 preview/shadow/eval storage，必须有 shadow eval / recall / risk gate。");
        }

        // V3.10.F：未通过 provider comparison freeze 的 provider 不得触发 formal retrieval / 绑定正式 store / 接入 PackingPolicy。
        var qwen3EmbeddingProvider = Find(registry, ShadowCapabilityIds.Qwen3EmbeddingProvider);
        if (qwen3EmbeddingProvider is not null)
        {
            AddCheck(
                checks,
                qwen3EmbeddingProvider,
                "Qwen3ProviderFormalRetrievalSwitchForbidden",
                IsForbidden(qwen3EmbeddingProvider, "FormalRetrievalSwitch")
                    && IsForbidden(qwen3EmbeddingProvider, "PgVectorFormalRetrievalSwitch"),
                "未通过 provider comparison freeze 的 provider 不得切换正式 vector retrieval。");
            AddCheck(
                checks,
                qwen3EmbeddingProvider,
                "Qwen3ProviderFormalStoreBindingForbidden",
                IsForbidden(qwen3EmbeddingProvider, "FormalIVectorIndexStoreBinding"),
                "未通过 provider comparison freeze 的 provider 不得绑定为正式 IVectorIndexStore。");
            AddCheck(
                checks,
                qwen3EmbeddingProvider,
                "Qwen3ProviderPackingPolicyIntegrationForbidden",
                IsForbidden(qwen3EmbeddingProvider, "PackingPolicyIntegration")
                    && IsForbidden(qwen3EmbeddingProvider, "PackageOutputIntegration"),
                "未通过 provider comparison freeze 的 provider 不得接入 PackingPolicy 或 package output。");
        }

        var failed = checks
            .Where(static item => !item.Passed)
            .Select(static item => $"{item.CapabilityId}:{item.Condition}")
            .ToArray();
        return new LearningRuntimeChangeReadinessGateReport
        {
            OperationId = $"learning-runtime-change-gate-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            Passed = failed.Length == 0,
            RegistryReportPath = PathHygiene.ToRepoRelativePath(registryReportPath),
            Checks = checks,
            FailedConditions = failed,
            Recommendation = failed.Length == 0 ? "RuntimeChangeRulesSatisfied" : "KeepRuntimeDefaults",
            PolicyVersion = PolicyVersion
        };
    }

    public static string BuildMarkdownReport(LearningReadinessRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var builder = new StringBuilder();
        builder.AppendLine("# Learning Readiness Freeze Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {registry.GeneratedAt:O}");
        builder.AppendLine($"PolicyVersion: `{registry.PolicyVersion}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- ReadyCount: `{registry.ReadyCount}`");
        builder.AppendLine($"- BlockedCount: `{registry.BlockedCount}`");
        builder.AppendLine($"- OverallRecommendation: `{registry.OverallRecommendation}`");
        builder.AppendLine();
        builder.AppendLine("| Capability | Phase | Status | Gate | Recommendation | Blocked | Allowed | Forbidden | Report |");
        builder.AppendLine("|---|---|---|---:|---|---|---|---|---|");
        foreach (var item in registry.Capabilities.OrderBy(static item => item.CapabilityId, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine(
                $"| {item.CapabilityId} | {item.CurrentPhase} | {item.Status} | {item.GatePassed} | {item.Recommendation} | {FormatList(item.BlockedReasons)} | {FormatList(item.AllowedRuntimeModes)} | {FormatList(item.ForbiddenRuntimeModes)} | {item.LastEvalReportPath} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Runtime Boundary");
        builder.AppendLine();
        builder.AppendLine("- GraphExpansion 仅允许 `audit-v1` / `conflict-v1` 的 explicit guarded opt-in。");
        builder.AppendLine("- VectorRetrieval 因 A3 recall gate 未通过，保持 PreviewOnly。");
        builder.AppendLine("- HybridRetrievalPreview 因 recall 未达 V4 gate，保持 KeepPreviewOnly；不得接 formal retrieval、替代正式 retrieval source 或影响 PackingPolicy/package output。");
        builder.AppendLine("- RouterIntentClassifier 由于 breaks > fixes，保持现有 rule-based router。");
        builder.AppendLine("- CandidateReranker 因 netGain 仍为负，保持 formal ranking。");
        builder.AppendLine("- AttentionRerank 与 PlanningProposal 仍为 opt-in/default off。");
        builder.AppendLine("- RelationGovernance 仅允许 allowlisted scopes 使用 `GuardedPostgresPrimary`，且必须保留 fallback 与 comparison trace。");
        builder.AppendLine("- JobQueuePostgres 仅允许 explicit allowlisted worker scopes 使用 `GuardedPostgresPrimary`，且必须保留 lease / heartbeat / retry / dead-letter quality gates。");
        builder.AppendLine("- VectorPostgresProvider 仅允许 preview / shadow / eval storage；正式检索、PackingPolicy、package output 仍需 Vector V4 gate。");
        builder.AppendLine("- Qwen3EmbeddingProvider 因 V3.10.F comparison freeze 未通过（A3/Extended recall 或 risk 不达标），保持 DoNotPromote，不得触发 formal retrieval、绑定正式 IVectorIndexStore 或接入 PackingPolicy/package output。");
        builder.AppendLine("- CurrentEmbeddingProvider 保持 KeepCurrentPreviewProvider，作为 preview provider 不切换；VectorV4RecheckAllowed=false，需待某 provider 同时满足 recall 与 risk 门槛后才可重新评估。");
        builder.AppendLine("- DatasetV2Stress 只可作为 V4 recheck input；stress freeze 通过不等于 formal retrieval allowed，`post-scoring-risk-gated-v1` 不得直接接 runtime。");
        builder.AppendLine("- VectorV4ReadinessRecheck 通过后也只允许 `GuardedFormalPreviewOnly`，`ReadyForRuntimeSwitch=false`，formal retrieval / PackingPolicy / package output 仍禁止。");
        builder.AppendLine("- GuardedFormalRetrievalPreview 通过后也只允许 `ShadowPackageComparisonOnly`，不得写正式 package 或改变 runtime retrieval。");
        builder.AppendLine("- VectorShadowPackageComparison 通过后也只允许 `ScopedFormalPreviewOptInOnly`，不得写正式 package 或改变 runtime retrieval。");
        builder.AppendLine("- ScopedFormalPreviewOptIn 通过后也只允许 `LimitedFormalPreviewObservationOnly`，不得写正式 package 或改变 runtime retrieval。");
        builder.AppendLine("- LimitedFormalPreviewObservation 通过后也只允许 `FormalPreviewFreezeOnly`，不得写正式 package 或改变 runtime retrieval。");
        builder.AppendLine("- VectorFormalPreviewFreeze 通过后也只允许 `ScopedPreviewOnly`，不得 runtime switch、写正式 package 或改变 package output。");
        return builder.ToString();
    }

    private static async Task<ShadowCapabilityReadiness> BuildRelationGovernanceAsync(
        string current,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            current,
            "storage",
            "postgres",
            "postgres-relation-multi-normal-scope-quality-report.json");
        var report = await ReadJsonAsync<PostgresRelationMultiNormalScopeCanaryReport>(path, cancellationToken)
            .ConfigureAwait(false);
        if (report is null)
        {
            return Missing(
                ShadowCapabilityIds.RelationGovernance,
                "DB2.F",
                path,
                ["MissingRelationGovernanceMultiNormalScopeQualityReport"],
                forbidden: [ShadowRuntimeModes.DefaultOn, "GlobalDefaultOn", "GuardedPostgresPrimary"]);
        }

        var blocked = report.BlockedReasons.ToList();
        if (!report.GatePassed)
        {
            blocked.Add("MultiNormalScopeGateNotPassed");
        }

        if (report.MismatchCount != 0)
        {
            blocked.Add("MismatchCountNonZero");
        }

        if (report.PostgresFailureCount != 0)
        {
            blocked.Add("PostgresFailureCountNonZero");
        }

        if (report.ScopeLeakCount != 0)
        {
            blocked.Add("ScopeLeakCountNonZero");
        }

        var ready = blocked.Count == 0
                    && string.Equals(report.Recommendation, "ReadyForLimitedScopeExpansion", StringComparison.OrdinalIgnoreCase);
        return new ShadowCapabilityReadiness
        {
            CapabilityId = ShadowCapabilityIds.RelationGovernance,
            CurrentPhase = "DB2.F",
            Status = ready ? "ReadyForLimitedScopeExpansion" : "KeepFileSystemPrimary",
            Recommendation = ready ? "ReadyForLimitedScopeExpansion" : "KeepFileSystemPrimary",
            GatePassed = ready,
            BlockedReasons = blocked,
            AllowedRuntimeModes = ready
                ? [ShadowRuntimeModes.Off, "FileSystemPrimary", "GuardedPostgresPrimary:AllowlistedScopes:FallbackToFileSystem:ComparisonTrace"]
                : [ShadowRuntimeModes.Off, "FileSystemPrimary"],
            ForbiddenRuntimeModes =
            [
                ShadowRuntimeModes.DefaultOn,
                "GlobalDefaultOn",
                "GuardedPostgresPrimary:Global",
                "GuardedPostgresPrimary:FallbackDisabled",
                "GuardedPostgresPrimary:ComparisonTraceDisabled",
                "HistoricalMigrationAsSwitchPrerequisite"
            ],
            LastEvalReportPath = PathHygiene.ToRepoRelativePath(path),
            LastUpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static async Task<ShadowCapabilityReadiness> BuildJobQueuePostgresAsync(
        string current,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            current,
            "storage",
            "postgres",
            "postgres-job-queue-freeze-gate.json");
        var report = await ReadJsonAsync<JobQueuePostgresFreezeGateReport>(path, cancellationToken)
            .ConfigureAwait(false);
        if (report is null)
        {
            return Missing(
                ShadowCapabilityIds.JobQueuePostgres,
                "DB4.F",
                path,
                ["MissingJobQueuePostgresFreezeGateReport"],
                forbidden:
                [
                    ShadowRuntimeModes.DefaultOn,
                    "GlobalWorkerProviderSwitch",
                    "ProductionWorkerLoopSwitchWithoutGate",
                    "GuardedPostgresPrimary"
                ]);
        }

        var blocked = report.BlockedReasons.ToList();
        if (!report.Passed)
        {
            blocked.Add("JobQueueFreezeGateNotPassed");
        }

        if (report.DuplicateExecutionCount != 0)
        {
            blocked.Add("DuplicateExecutionCountNonZero");
        }

        if (report.LeaseViolationCount != 0)
        {
            blocked.Add("LeaseViolationCountNonZero");
        }

        if (report.RetryViolationCount != 0)
        {
            blocked.Add("RetryViolationCountNonZero");
        }

        if (report.DeadLetterViolationCount != 0)
        {
            blocked.Add("DeadLetterViolationCountNonZero");
        }

        if (report.PostgresFailureCount != 0)
        {
            blocked.Add("PostgresFailureCountNonZero");
        }

        if (report.ScopeLeakCount != 0)
        {
            blocked.Add("ScopeLeakCountNonZero");
        }

        if (!report.RuntimeWorkerGlobalProviderUnchanged)
        {
            blocked.Add("RuntimeWorkerGlobalProviderChanged");
        }

        var ready = blocked.Count == 0
                    && string.Equals(report.JobQueuePostgres, "ReadyForScopedWorkerMode", StringComparison.OrdinalIgnoreCase);
        return new ShadowCapabilityReadiness
        {
            CapabilityId = ShadowCapabilityIds.JobQueuePostgres,
            CurrentPhase = "DB4.F",
            Status = ready ? "ReadyForScopedWorkerMode" : "KeepExistingProvider",
            Recommendation = ready ? "ReadyForScopedWorkerMode" : "KeepExistingProvider",
            GatePassed = ready,
            BlockedReasons = blocked,
            AllowedRuntimeModes = ready
                ?
                [
                    ShadowRuntimeModes.Off,
                    "ExistingProvider",
                    "GuardedPostgresPrimary:ExplicitAllowlistedWorkerScopes:LeaseHeartbeatQualityGate:RetryDeadLetterQualityGate"
                ]
                : [ShadowRuntimeModes.Off, "ExistingProvider"],
            ForbiddenRuntimeModes =
            [
                ShadowRuntimeModes.DefaultOn,
                "GlobalWorkerProviderSwitch",
                "ProductionWorkerLoopSwitchWithoutGate",
                "GuardedPostgresPrimary:Global",
                "GuardedPostgresPrimary:MissingScopedAllowlist",
                "GuardedPostgresPrimary:MissingLeaseQualityGate",
                "GuardedPostgresPrimary:MissingRetryDeadLetterQualityGate"
            ],
            LastEvalReportPath = PathHygiene.ToRepoRelativePath(path),
            LastUpdatedAt = report.GeneratedAt
        };
    }

    private static async Task<ShadowCapabilityReadiness> BuildVectorPostgresProviderAsync(
        string current,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            current,
            "storage",
            "postgres",
            "postgres-vector-freeze-gate.json");
        var report = await ReadJsonAsync<VectorPostgresProviderFreezeGateReport>(path, cancellationToken)
            .ConfigureAwait(false);
        if (report is null)
        {
            return Missing(
                ShadowCapabilityIds.VectorPostgresProvider,
                "DB5.F",
                path,
                ["MissingVectorPostgresFreezeGateReport"],
                forbidden:
                [
                    ShadowRuntimeModes.DefaultOn,
                    "FormalRetrievalSwitch",
                    "PgVectorFormalRetrievalSwitch",
                    "PostgresVectorIndexStoreFormalBinding",
                    "FormalIVectorIndexStoreBindingWithoutV4Gate",
                    "PackingPolicyIntegration",
                    "PackageOutputIntegration"
                ]);
        }

        var blocked = report.BlockedReasons.ToList();
        if (!report.Passed)
        {
            blocked.Add("VectorPostgresFreezeGateNotPassed");
        }

        if (Math.Abs(report.A3RecallDelta) > 0.000000001d)
        {
            blocked.Add("A3RecallDeltaNonZero");
        }

        if (Math.Abs(report.ExtendedRecallDelta) > 0.000000001d)
        {
            blocked.Add("ExtendedRecallDeltaNonZero");
        }

        if (report.RiskAfterPolicy != 0
            || report.MustNotHitRiskAfterPolicy > 0.000000001d
            || report.LifecycleRiskAfterPolicy > 0.000000001d)
        {
            blocked.Add("RiskAfterPolicyNonZero");
        }

        if (report.FormalOutputChanged != 0)
        {
            blocked.Add("FormalOutputChangedNonZero");
        }

        if (report.ProjectionMismatchCount != 0)
        {
            blocked.Add("ProjectionMismatchNonZero");
        }

        if (report.UseForRuntime || report.FormalRetrievalAllowed)
        {
            blocked.Add("FormalRuntimeVectorPathEnabled");
        }

        var ready = blocked.Count == 0
                    && string.Equals(report.VectorPostgresProvider, "ReadyForPreviewShadowStorage", StringComparison.OrdinalIgnoreCase);
        return new ShadowCapabilityReadiness
        {
            CapabilityId = ShadowCapabilityIds.VectorPostgresProvider,
            CurrentPhase = "DB5.F",
            Status = ready ? "ReadyForPreviewShadowStorage" : ShadowCapabilityReadinessStatuses.PreviewOnly,
            Recommendation = ready ? "ReadyForPreviewShadowStorage" : "KeepPreviewOnly",
            GatePassed = ready,
            BlockedReasons = blocked,
            AllowedRuntimeModes = ready
                ? [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly, ShadowRuntimeModes.Shadow, "PreviewShadowEvalOnly"]
                : [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly],
            ForbiddenRuntimeModes =
            [
                ShadowRuntimeModes.DefaultOn,
                ShadowRuntimeModes.ApplyGuarded,
                ShadowRuntimeModes.RuntimeShadow,
                "FormalRetrievalSwitch",
                "PgVectorFormalRetrievalSwitch",
                "PostgresVectorIndexStoreFormalBinding",
                "FormalIVectorIndexStoreBindingWithoutV4Gate",
                "PackingPolicyIntegration",
                "PackageOutputIntegration",
                "FormalVectorRetrieval",
                "MissingShadowEvalGate",
                "MissingRecallRiskGate"
            ],
            LastEvalReportPath = PathHygiene.ToRepoRelativePath(path),
            LastUpdatedAt = report.GeneratedAt
        };
    }

    // V3.10.F：读取 embedding provider comparison freeze 报告；未通过 readiness gate 时保持 DoNotPromote / BlockedByRisk。
    private static async Task<ShadowCapabilityReadiness> BuildQwen3EmbeddingProviderAsync(
        string current,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            current,
            "vector",
            "providers",
            "qwen3",
            "vector-provider-comparison-freeze.json");
        var report = await ReadJsonAsync<EmbeddingProviderComparisonFreezeReport>(path, cancellationToken)
            .ConfigureAwait(false);
        var forbidden = new[]
        {
            ShadowRuntimeModes.DefaultOn,
            ShadowRuntimeModes.ApplyGuarded,
            ShadowRuntimeModes.RuntimeShadow,
            "FormalRetrievalSwitch",
            "PgVectorFormalRetrievalSwitch",
            "FormalIVectorIndexStoreBinding",
            "PackingPolicyIntegration",
            "PackageOutputIntegration"
        };
        if (report is null)
        {
            return Missing(
                ShadowCapabilityIds.Qwen3EmbeddingProvider,
                "V3.10.F",
                path,
                ["MissingEmbeddingProviderComparisonFreezeReport"],
                forbidden: forbidden);
        }

        var blocked = report.BlockedReasons.ToList();
        if (!report.Passed)
        {
            blocked.Add("EmbeddingProviderComparisonFreezeNotPassed");
        }

        if (!report.ReadinessGatePassed)
        {
            blocked.Add("Qwen3ReadinessGateNotPassed");
        }

        if (!report.ProviderConfigurationSanityPassed)
        {
            blocked.Add("ProviderConfigurationSanityAuditNotPassed");
        }

        if (report.RiskAfterPolicy != 0)
        {
            blocked.Add("RiskAfterPolicyNonZero");
        }

        if (report.FormalOutputChanged != 0)
        {
            blocked.Add("FormalOutputChangedNonZero");
        }

        if (report.FormalRetrievalAllowed)
        {
            blocked.Add("FormalRetrievalSwitchEnabled");
        }

        var ready = blocked.Count == 0 && report.Passed;
        var recommendation = ready
            ? "ReadyForVectorV4Recheck"
            : (!report.ProviderConfigurationSanityPassed
                ? "BlockedByProviderConfigurationMismatch"
                : report.RiskAfterPolicy != 0
                    ? "BlockedByRisk"
                    : "DoNotPromote");
        return new ShadowCapabilityReadiness
        {
            CapabilityId = ShadowCapabilityIds.Qwen3EmbeddingProvider,
            CurrentPhase = "V3.10.F",
            Status = ready
                ? ShadowCapabilityReadinessStatuses.PreviewOnly
                : ShadowCapabilityReadinessStatuses.PreviewOnly,
            Recommendation = recommendation,
            GatePassed = ready,
            BlockedReasons = blocked,
            AllowedRuntimeModes = ready
                ? [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly, ShadowRuntimeModes.Shadow, "PreviewShadowEvalOnly"]
                : [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly, ShadowRuntimeModes.Shadow],
            ForbiddenRuntimeModes = forbidden,
            LastEvalReportPath = PathHygiene.ToRepoRelativePath(path),
            LastUpdatedAt = report.GeneratedAt
        };
    }

    // V3.10.F：当前 preview provider 保持现状，仅记录为 KeepCurrentPreviewProvider，不切换。
    private static async Task<ShadowCapabilityReadiness> BuildCurrentEmbeddingProviderAsync(
        string current,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var path = Path.Combine(
            current,
            "vector",
            "providers",
            "qwen3",
            "vector-provider-comparison.json");
        return new ShadowCapabilityReadiness
        {
            CapabilityId = ShadowCapabilityIds.CurrentEmbeddingProvider,
            CurrentPhase = "V3.10.F",
            Status = ShadowCapabilityReadinessStatuses.KeepCurrentPreviewProvider,
            Recommendation = ShadowCapabilityReadinessStatuses.KeepCurrentPreviewProvider,
            GatePassed = true,
            BlockedReasons = Array.Empty<string>(),
            AllowedRuntimeModes = [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly, ShadowRuntimeModes.ExistingRuntime],
            ForbiddenRuntimeModes =
            [
                ShadowRuntimeModes.DefaultOn,
                ShadowRuntimeModes.ApplyGuarded,
                ShadowRuntimeModes.RuntimeShadow,
                "FormalRetrievalSwitch",
                "PgVectorFormalRetrievalSwitch",
                "FormalIVectorIndexStoreBinding",
                "PackingPolicyIntegration",
                "PackageOutputIntegration"
            ],
            LastEvalReportPath = PathHygiene.ToRepoRelativePath(path),
            LastUpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public static string BuildRuntimeChangeGateMarkdown(LearningRuntimeChangeReadinessGateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Learning Runtime Change Readiness Gate");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:O}");
        builder.AppendLine($"PolicyVersion: `{report.PolicyVersion}`");
        builder.AppendLine($"Registry: `{report.RegistryReportPath}`");
        builder.AppendLine();
        builder.AppendLine($"- Passed: `{report.Passed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("| Capability | Condition | Passed | Reason |");
        builder.AppendLine("|---|---|---:|---|");
        foreach (var check in report.Checks)
        {
            builder.AppendLine($"| {check.CapabilityId} | {check.Condition} | {check.Passed} | {check.Reason} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Failed Conditions");
        if (report.FailedConditions.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var condition in report.FailedConditions)
            {
                builder.AppendLine($"- `{condition}`");
            }
        }

        return builder.ToString();
    }

    private static async Task<ShadowCapabilityReadiness> BuildGraphExpansionAsync(
        string current,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(current, "eval", "graph-expansion-guarded-optin-gate.json");
        var report = await ReadJsonAsync<GraphExpansionGuardedOptInGateReport>(path, cancellationToken)
            .ConfigureAwait(false);
        if (report is null)
        {
            return Missing(
                ShadowCapabilityIds.GraphExpansion,
                "G7.1",
                path,
                ["MissingGraphExpansionGateReport"],
                forbidden: [ShadowRuntimeModes.ApplyGuarded, ShadowRuntimeModes.RuntimeShadow, ShadowRuntimeModes.DefaultOn]);
        }

        return new ShadowCapabilityReadiness
        {
            CapabilityId = ShadowCapabilityIds.GraphExpansion,
            CurrentPhase = "G7.1",
            Status = report.Passed
                ? ShadowCapabilityReadinessStatuses.ReadyForGuardedOptIn
                : ShadowCapabilityReadinessStatuses.PreviewOnly,
            Recommendation = report.Passed ? "ReadyForGuardedOptIn:audit-v1,conflict-v1" : "KeepPreviewOnly",
            GatePassed = report.Passed,
            BlockedReasons = report.FailedConditions,
            AllowedRuntimeModes = report.Passed
                ? [ShadowRuntimeModes.Off, ShadowRuntimeModes.Shadow, "ApplyGuarded:audit-v1", "ApplyGuarded:conflict-v1"]
                : [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly],
            ForbiddenRuntimeModes =
            [
                ShadowRuntimeModes.DefaultOn,
                "ApplyGuarded:normal-v1",
                "ApplyGuarded:current-task-v1",
                "NormalContextInjection"
            ],
            LastEvalReportPath = PathHygiene.ToRepoRelativePath(path),
            LastUpdatedAt = report.CreatedAt
        };
    }

    private static async Task<ShadowCapabilityReadiness> BuildVectorRetrievalAsync(
        string current,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(current, "eval", "vector-retrieval-shadow-readiness-gate.json");
        var report = await ReadJsonAsync<VectorRetrievalShadowReadinessGateReport>(path, cancellationToken)
            .ConfigureAwait(false);
        if (report is null)
        {
            return Missing(
                ShadowCapabilityIds.VectorRetrieval,
                "V3.F",
                path,
                ["MissingVectorReadinessGateReport"],
                forbidden: [ShadowRuntimeModes.RuntimeShadow, ShadowRuntimeModes.ApplyGuarded, ShadowRuntimeModes.DefaultOn]);
        }

        return new ShadowCapabilityReadiness
        {
            CapabilityId = ShadowCapabilityIds.VectorRetrieval,
            CurrentPhase = "V3.F",
            Status = report.Passed
                ? "ReadyForRetrievalShadow"
                : ShadowCapabilityReadinessStatuses.PreviewOnly,
            Recommendation = report.Passed ? "ReadyForRetrievalShadow" : "BlockedByRecall",
            GatePassed = report.Passed,
            BlockedReasons = report.FailReasons,
            AllowedRuntimeModes = report.Passed
                ? [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly, ShadowRuntimeModes.RuntimeShadow]
                : [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly],
            ForbiddenRuntimeModes = report.Passed
                ? [ShadowRuntimeModes.DefaultOn, ShadowRuntimeModes.ApplyGuarded]
                : [ShadowRuntimeModes.RuntimeShadow, ShadowRuntimeModes.ApplyGuarded, ShadowRuntimeModes.DefaultOn],
            LastEvalReportPath = PathHygiene.ToRepoRelativePath(path),
            LastUpdatedAt = report.CreatedAt
        };
    }

    private static async Task<ShadowCapabilityReadiness> BuildHybridRetrievalPreviewAsync(
        string current,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(current, "vector", "hybrid", "vector-hybrid-freeze-gate.json");
        var report = await ReadJsonAsync<HybridRetrievalPreviewFreezeReport>(path, cancellationToken)
            .ConfigureAwait(false);
        var forbidden = new[]
        {
            ShadowRuntimeModes.DefaultOn,
            ShadowRuntimeModes.ApplyGuarded,
            ShadowRuntimeModes.RuntimeShadow,
            "FormalRetrievalSwitch",
            "FormalHybridRetrievalSwitch",
            "FormalRetrievalSourceReplacement",
            "FormalIVectorIndexStoreBinding",
            "PackingPolicyIntegration",
            "PackageOutputIntegration"
        };
        if (report is null)
        {
            return Missing(
                ShadowCapabilityIds.HybridRetrievalPreview,
                "V3.11.F",
                path,
                ["MissingHybridRetrievalPreviewFreezeReport"],
                forbidden: forbidden);
        }

        var blocked = report.BlockedReasons.ToList();
        if (!report.FreezePassed)
        {
            blocked.Add("HybridRetrievalPreviewFreezeNotPassed");
        }

        if (!report.V4RecheckAllowed)
        {
            blocked.Add("HybridV4RecheckNotAllowed");
        }

        if (report.FormalRetrievalAllowed)
        {
            blocked.Add("FormalRetrievalAllowed");
        }

        if (report.UseForRuntime)
        {
            blocked.Add("UseForRuntimeEnabled");
        }

        return new ShadowCapabilityReadiness
        {
            CapabilityId = ShadowCapabilityIds.HybridRetrievalPreview,
            CurrentPhase = "V3.11.F",
            Status = ShadowCapabilityReadinessStatuses.PreviewOnly,
            Recommendation = report.Recommendation,
            GatePassed = report.FreezePassed && report.V4RecheckAllowed,
            BlockedReasons = blocked.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            AllowedRuntimeModes = [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly, ShadowRuntimeModes.Shadow, "PreviewShadowEvalOnly"],
            ForbiddenRuntimeModes = forbidden,
            LastEvalReportPath = PathHygiene.ToRepoRelativePath(path),
            LastUpdatedAt = report.GeneratedAt
        };
    }

    private static async Task<ShadowCapabilityReadiness> BuildDatasetV2StressAsync(
        string current,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(current, "vector", "dataset-v2", "stress", "stress-freeze-gate.json");
        var report = await ReadJsonAsync<RetrievalDatasetV2StressFreezeReport>(path, cancellationToken)
            .ConfigureAwait(false);
        var forbidden = new[]
        {
            ShadowRuntimeModes.DefaultOn,
            ShadowRuntimeModes.ApplyGuarded,
            ShadowRuntimeModes.RuntimeShadow,
            "FormalRetrievalSwitch",
            "ReadyForFormalRetrieval",
            "FormalIVectorIndexStoreBinding",
            "PostScoringRiskGatedV1:Runtime",
            "post-scoring-risk-gated-v1:Runtime",
            "PackingPolicyIntegration",
            "PackageOutputIntegration"
        };
        if (report is null)
        {
            return Missing(
                ShadowCapabilityIds.DatasetV2Stress,
                "V3.24",
                path,
                ["MissingDatasetV2StressFreezeGateReport"],
                forbidden: forbidden);
        }

        var blocked = report.BlockedReasons.ToList();
        if (!report.FreezePassed)
        {
            blocked.Add("DatasetV2StressFreezeNotPassed");
        }

        if (!string.Equals(report.DatasetV2Stress, RetrievalDatasetV2StressFreezeStatuses.ReadyForV4RecheckInput, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("DatasetV2StressNotReadyForV4RecheckInput");
        }

        if (!report.V4RecheckAllowed)
        {
            blocked.Add("V4RecheckNotAllowed");
        }

        if (report.ReadyForFormalRetrieval || report.FormalRetrievalAllowed || report.UseForRuntime)
        {
            blocked.Add("FormalRetrievalOrRuntimeFlagEnabled");
        }

        if (report.RiskAfterPolicy != 0
            || report.MustNotHitRiskAfterPolicy != 0
            || report.LifecycleRiskAfterPolicy != 0
            || report.HybridScoringRiskCandidateCount != 0)
        {
            blocked.Add("RiskCountNonZero");
        }

        if (report.LeakageIssueCount != 0)
        {
            blocked.Add("LeakageIssueCountNonZero");
        }

        if (report.AnchorDominanceScore > 0.000000001d)
        {
            blocked.Add("AnchorDominanceScoreNonZero");
        }

        if (report.FormalOutputChanged != 0)
        {
            blocked.Add("FormalOutputChangedNonZero");
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ready = distinctBlocked.Length == 0;
        return new ShadowCapabilityReadiness
        {
            CapabilityId = ShadowCapabilityIds.DatasetV2Stress,
            CurrentPhase = "V3.24",
            Status = ready
                ? RetrievalDatasetV2StressFreezeStatuses.ReadyForV4RecheckInput
                : ShadowCapabilityReadinessStatuses.PreviewOnly,
            Recommendation = report.Recommendation,
            GatePassed = ready,
            BlockedReasons = distinctBlocked,
            AllowedRuntimeModes = ready
                ? [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly, ShadowRuntimeModes.Shadow, "V4RecheckInputOnly"]
                : [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly],
            ForbiddenRuntimeModes = forbidden,
            LastEvalReportPath = PathHygiene.ToRepoRelativePath(path),
            LastUpdatedAt = report.CreatedAt
        };
    }

    private static async Task<ShadowCapabilityReadiness> BuildVectorV4ReadinessRecheckAsync(
        string current,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(current, "vector", "v4", "vector-v4-readiness-recheck.json");
        var report = await ReadJsonAsync<VectorV4ReadinessRecheckReport>(path, cancellationToken)
            .ConfigureAwait(false);
        var forbidden = new[]
        {
            ShadowRuntimeModes.DefaultOn,
            ShadowRuntimeModes.ApplyGuarded,
            ShadowRuntimeModes.RuntimeShadow,
            "ReadyForRuntimeSwitch",
            "RuntimeSwitch",
            "FormalRetrievalSwitch",
            "FormalRetrievalAllowed",
            "FormalIVectorIndexStoreBinding",
            "PostgresVectorIndexStoreFormalBinding",
            "post-scoring-risk-gated-v1:Runtime",
            "PackingPolicyIntegration",
            "PackageOutputIntegration"
        };
        if (report is null)
        {
            return Missing(
                ShadowCapabilityIds.VectorV4ReadinessRecheck,
                "V4.R",
                path,
                ["MissingVectorV4ReadinessRecheckReport"],
                forbidden: forbidden);
        }

        var blocked = report.BlockedReasons.ToList();
        if (!report.RecheckPassed)
        {
            blocked.Add("VectorV4RecheckNotPassed");
        }

        if (!report.ReadyForGuardedFormalPreview)
        {
            blocked.Add("GuardedFormalPreviewNotReady");
        }

        if (report.ReadyForRuntimeSwitch || report.FormalRetrievalAllowed || report.UseForRuntime)
        {
            blocked.Add("RuntimeOrFormalRetrievalFlagEnabled");
        }

        if (report.RiskAfterPolicy != 0
            || report.MustNotHitRiskAfterPolicy != 0
            || report.LifecycleRiskAfterPolicy != 0)
        {
            blocked.Add("RiskCountNonZero");
        }

        if (report.FormalOutputChanged != 0)
        {
            blocked.Add("FormalOutputChangedNonZero");
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ready = distinctBlocked.Length == 0;
        return new ShadowCapabilityReadiness
        {
            CapabilityId = ShadowCapabilityIds.VectorV4ReadinessRecheck,
            CurrentPhase = "V4.R",
            Status = ready
                ? VectorV4ReadinessRecheckRecommendations.ReadyForGuardedFormalPreview
                : ShadowCapabilityReadinessStatuses.PreviewOnly,
            Recommendation = report.Recommendation,
            GatePassed = ready,
            BlockedReasons = distinctBlocked,
            AllowedRuntimeModes = ready
                ? [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly, ShadowRuntimeModes.Shadow, "GuardedFormalPreviewOnly"]
                : [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly],
            ForbiddenRuntimeModes = forbidden,
            LastEvalReportPath = PathHygiene.ToRepoRelativePath(path),
            LastUpdatedAt = report.CreatedAt
        };
    }

    private static async Task<ShadowCapabilityReadiness> BuildGuardedFormalRetrievalPreviewAsync(
        string current,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(current, "vector", "v4", "vector-guarded-formal-retrieval-preview-gate.json");
        var report = await ReadJsonAsync<GuardedFormalRetrievalPreviewReport>(path, cancellationToken)
            .ConfigureAwait(false);
        var forbidden = new[]
        {
            ShadowRuntimeModes.DefaultOn,
            ShadowRuntimeModes.ApplyGuarded,
            ShadowRuntimeModes.RuntimeShadow,
            "ReadyForRuntimeSwitch",
            "RuntimeSwitch",
            "FormalRetrievalSwitch",
            "FormalRetrievalAllowed",
            "FormalIVectorIndexStoreBinding",
            "PostgresVectorIndexStoreFormalBinding",
            "GuardedFormalPreview:Runtime",
            "post-scoring-risk-gated-v1:Runtime",
            "PackingPolicyIntegration",
            "PackageOutputIntegration",
            "FormalPackageWrite"
        };
        if (report is null)
        {
            return Missing(
                ShadowCapabilityIds.GuardedFormalRetrievalPreview,
                "V4.1",
                path,
                ["MissingGuardedFormalRetrievalPreviewGateReport"],
                forbidden: forbidden);
        }

        var blocked = report.BlockedReasons.ToList();
        if (!report.GatePassed)
        {
            blocked.Add("GuardedFormalRetrievalPreviewGateNotPassed");
        }

        if (!string.Equals(report.Recommendation, GuardedFormalRetrievalPreviewRecommendations.ReadyForShadowPackageComparison, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("GuardedFormalRetrievalPreviewRecommendationNotReady");
        }

        if (report.ReadyForRuntimeSwitch || report.FormalRetrievalAllowed || report.UseForRuntime)
        {
            blocked.Add("RuntimeOrFormalRetrievalFlagEnabled");
        }

        if (report.RiskAfterPolicy != 0
            || report.MustNotHitRiskAfterPolicy != 0
            || report.LifecycleRiskAfterPolicy != 0)
        {
            blocked.Add("RiskCountNonZero");
        }

        if (report.FormalOutputChanged != 0)
        {
            blocked.Add("FormalOutputChangedNonZero");
        }

        if (report.PackingPolicyChanged)
        {
            blocked.Add("PackingPolicyChanged");
        }

        if (report.PackageOutputChanged)
        {
            blocked.Add("PackageOutputChanged");
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ready = distinctBlocked.Length == 0;
        return new ShadowCapabilityReadiness
        {
            CapabilityId = ShadowCapabilityIds.GuardedFormalRetrievalPreview,
            CurrentPhase = "V4.1",
            Status = ready
                ? GuardedFormalRetrievalPreviewRecommendations.ReadyForShadowPackageComparison
                : ShadowCapabilityReadinessStatuses.PreviewOnly,
            Recommendation = report.Recommendation,
            GatePassed = ready,
            BlockedReasons = distinctBlocked,
            AllowedRuntimeModes = ready
                ? [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly, ShadowRuntimeModes.Shadow, "GuardedFormalPreviewOnly", "ShadowPackageComparisonOnly"]
                : [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly],
            ForbiddenRuntimeModes = forbidden,
            LastEvalReportPath = PathHygiene.ToRepoRelativePath(path),
            LastUpdatedAt = report.CreatedAt
        };
    }

    private static async Task<ShadowCapabilityReadiness> BuildVectorShadowPackageComparisonAsync(
        string current,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(current, "vector", "v4", "vector-shadow-package-comparison-gate.json");
        var report = await ReadJsonAsync<VectorShadowPackageComparisonReport>(path, cancellationToken)
            .ConfigureAwait(false);
        var forbidden = new[]
        {
            ShadowRuntimeModes.DefaultOn,
            ShadowRuntimeModes.ApplyGuarded,
            ShadowRuntimeModes.RuntimeShadow,
            "ReadyForRuntimeSwitch",
            "RuntimeSwitch",
            "FormalRetrievalSwitch",
            "FormalRetrievalAllowed",
            "FormalIVectorIndexStoreBinding",
            "PostgresVectorIndexStoreFormalBinding",
            "ShadowPackageComparison:Runtime",
            "post-scoring-risk-gated-v1:Runtime",
            "PackingPolicyIntegration",
            "PackageOutputIntegration",
            "FormalPackageWrite"
        };
        if (report is null)
        {
            return Missing(
                ShadowCapabilityIds.VectorShadowPackageComparison,
                "V4.2",
                path,
                ["MissingVectorShadowPackageComparisonGateReport"],
                forbidden: forbidden);
        }

        var blocked = report.BlockedReasons.ToList();
        if (!report.GatePassed)
        {
            blocked.Add("VectorShadowPackageComparisonGateNotPassed");
        }

        if (!string.Equals(report.Recommendation, VectorShadowPackageComparisonRecommendations.ReadyForScopedFormalPreviewOptIn, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("VectorShadowPackageComparisonRecommendationNotReady");
        }

        if (report.ReadyForRuntimeSwitch || report.FormalRetrievalAllowed || report.UseForRuntime)
        {
            blocked.Add("RuntimeOrFormalRetrievalFlagEnabled");
        }

        if (report.RiskAfterPolicy != 0
            || report.MustNotHitRiskAfterPolicy != 0
            || report.LifecycleRiskAfterPolicy != 0)
        {
            blocked.Add("RiskCountNonZero");
        }

        if (report.FormalOutputChanged != 0)
        {
            blocked.Add("FormalOutputChangedNonZero");
        }

        if (report.PackageOutputChanged)
        {
            blocked.Add("PackageOutputChanged");
        }

        if (report.PackingPolicyChanged)
        {
            blocked.Add("PackingPolicyChanged");
        }

        if (report.RuntimeMutated)
        {
            blocked.Add("RuntimeMutated");
        }

        if (report.ShadowPackageWritten)
        {
            blocked.Add("ShadowPackageWrittenToFormalPath");
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ready = distinctBlocked.Length == 0;
        return new ShadowCapabilityReadiness
        {
            CapabilityId = ShadowCapabilityIds.VectorShadowPackageComparison,
            CurrentPhase = "V4.2",
            Status = ready
                ? VectorShadowPackageComparisonRecommendations.ReadyForScopedFormalPreviewOptIn
                : ShadowCapabilityReadinessStatuses.PreviewOnly,
            Recommendation = report.Recommendation,
            GatePassed = ready,
            BlockedReasons = distinctBlocked,
            AllowedRuntimeModes = ready
                ? [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly, ShadowRuntimeModes.Shadow, "GuardedFormalPreviewOnly", "ShadowPackageComparisonOnly", "ScopedFormalPreviewOptInOnly"]
                : [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly],
            ForbiddenRuntimeModes = forbidden,
            LastEvalReportPath = PathHygiene.ToRepoRelativePath(path),
            LastUpdatedAt = report.CreatedAt
        };
    }

    private static async Task<ShadowCapabilityReadiness> BuildScopedFormalPreviewOptInAsync(
        string current,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(current, "vector", "v4", "vector-scoped-formal-preview-optin-gate.json");
        var report = await ReadJsonAsync<ScopedFormalPreviewOptInReport>(path, cancellationToken)
            .ConfigureAwait(false);
        var forbidden = new[]
        {
            ShadowRuntimeModes.DefaultOn,
            ShadowRuntimeModes.ApplyGuarded,
            ShadowRuntimeModes.RuntimeShadow,
            "ReadyForRuntimeSwitch",
            "RuntimeSwitch",
            "FormalRetrievalSwitch",
            "FormalRetrievalAllowed",
            "FormalIVectorIndexStoreBinding",
            "PostgresVectorIndexStoreFormalBinding",
            "ScopedFormalPreviewOptIn:Runtime",
            "PreviewOnly:Runtime",
            "FormalPackageWrite",
            "PackingPolicyIntegration",
            "PackageOutputIntegration"
        };
        if (report is null)
        {
            return Missing(
                ShadowCapabilityIds.ScopedFormalPreviewOptIn,
                "V4.3",
                path,
                ["MissingScopedFormalPreviewOptInGateReport"],
                forbidden: forbidden);
        }

        var blocked = report.BlockedReasons.ToList();
        if (!report.GatePassed)
        {
            blocked.Add("ScopedFormalPreviewOptInGateNotPassed");
        }

        if (!string.Equals(report.Recommendation, ScopedFormalPreviewOptInRecommendations.ReadyForLimitedFormalPreviewObservation, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("ScopedFormalPreviewOptInRecommendationNotReady");
        }

        if (report.ReadyForRuntimeSwitch || report.FormalRetrievalAllowed || report.UseForRuntime)
        {
            blocked.Add("RuntimeOrFormalRetrievalFlagEnabled");
        }

        if (report.RiskAfterPolicy != 0
            || report.MustNotHitRiskAfterPolicy != 0
            || report.LifecycleRiskAfterPolicy != 0)
        {
            blocked.Add("RiskCountNonZero");
        }

        if (report.FormalOutputChanged != 0)
        {
            blocked.Add("FormalOutputChangedNonZero");
        }

        if (report.PackageOutputChanged)
        {
            blocked.Add("PackageOutputChanged");
        }

        if (report.PackingPolicyChanged)
        {
            blocked.Add("PackingPolicyChanged");
        }

        if (report.FormalPackageWritten)
        {
            blocked.Add("FormalPackageWritten");
        }

        if (report.RuntimeMutated)
        {
            blocked.Add("RuntimeMutated");
        }

        if (report.NonAllowlistedScopeLeakCount != 0)
        {
            blocked.Add("NonAllowlistedScopeLeak");
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ready = distinctBlocked.Length == 0;
        return new ShadowCapabilityReadiness
        {
            CapabilityId = ShadowCapabilityIds.ScopedFormalPreviewOptIn,
            CurrentPhase = "V4.3",
            Status = ready
                ? ScopedFormalPreviewOptInRecommendations.ReadyForLimitedFormalPreviewObservation
                : ShadowCapabilityReadinessStatuses.PreviewOnly,
            Recommendation = report.Recommendation,
            GatePassed = ready,
            BlockedReasons = distinctBlocked,
            AllowedRuntimeModes = ready
                ? [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly, ShadowRuntimeModes.Shadow, "GuardedFormalPreviewOnly", "ShadowPackageComparisonOnly", "ScopedFormalPreviewOptInOnly", "LimitedFormalPreviewObservationOnly"]
                : [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly],
            ForbiddenRuntimeModes = forbidden,
            LastEvalReportPath = PathHygiene.ToRepoRelativePath(path),
            LastUpdatedAt = report.CreatedAt
        };
    }

    private static async Task<ShadowCapabilityReadiness> BuildLimitedFormalPreviewObservationAsync(
        string current,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(current, "vector", "v4", "vector-limited-formal-preview-observation-gate.json");
        var report = await ReadJsonAsync<LimitedFormalPreviewObservationReport>(path, cancellationToken)
            .ConfigureAwait(false);
        var forbidden = new[]
        {
            ShadowRuntimeModes.DefaultOn,
            ShadowRuntimeModes.ApplyGuarded,
            ShadowRuntimeModes.RuntimeShadow,
            "ReadyForRuntimeSwitch",
            "RuntimeSwitch",
            "FormalRetrievalSwitch",
            "FormalRetrievalAllowed",
            "FormalIVectorIndexStoreBinding",
            "PostgresVectorIndexStoreFormalBinding",
            "LimitedFormalPreviewObservation:Runtime",
            "PreviewOnly:Runtime",
            "FormalPackageWrite",
            "PackingPolicyIntegration",
            "PackageOutputIntegration"
        };
        if (report is null)
        {
            return Missing(
                ShadowCapabilityIds.LimitedFormalPreviewObservation,
                "V4.4",
                path,
                ["MissingLimitedFormalPreviewObservationGateReport"],
                forbidden: forbidden);
        }

        var blocked = report.BlockedReasons.ToList();
        if (!report.GatePassed)
        {
            blocked.Add("LimitedFormalPreviewObservationGateNotPassed");
        }

        if (!string.Equals(report.Recommendation, LimitedFormalPreviewObservationRecommendations.ReadyForFormalPreviewFreeze, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("LimitedFormalPreviewObservationRecommendationNotReady");
        }

        if (report.ReadyForRuntimeSwitch || report.FormalRetrievalAllowed || report.UseForRuntime)
        {
            blocked.Add("RuntimeOrFormalRetrievalFlagEnabled");
        }

        if (report.RiskAfterPolicy != 0
            || report.MustNotHitRiskAfterPolicy != 0
            || report.LifecycleRiskAfterPolicy != 0)
        {
            blocked.Add("RiskCountNonZero");
        }

        if (report.FormalOutputChanged != 0)
        {
            blocked.Add("FormalOutputChangedNonZero");
        }

        if (report.PackageOutputChanged)
        {
            blocked.Add("PackageOutputChanged");
        }

        if (report.PackingPolicyChanged)
        {
            blocked.Add("PackingPolicyChanged");
        }

        if (report.FormalPackageWritten)
        {
            blocked.Add("FormalPackageWritten");
        }

        if (report.RuntimeMutated)
        {
            blocked.Add("RuntimeMutated");
        }

        if (report.NonAllowlistedScopeLeakCount != 0)
        {
            blocked.Add("NonAllowlistedScopeLeak");
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ready = distinctBlocked.Length == 0;
        return new ShadowCapabilityReadiness
        {
            CapabilityId = ShadowCapabilityIds.LimitedFormalPreviewObservation,
            CurrentPhase = "V4.4",
            Status = ready
                ? LimitedFormalPreviewObservationRecommendations.ReadyForFormalPreviewFreeze
                : ShadowCapabilityReadinessStatuses.PreviewOnly,
            Recommendation = report.Recommendation,
            GatePassed = ready,
            BlockedReasons = distinctBlocked,
            AllowedRuntimeModes = ready
                ? [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly, ShadowRuntimeModes.Shadow, "FormalPreviewFreezeOnly"]
                : [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly],
            ForbiddenRuntimeModes = forbidden,
            LastEvalReportPath = PathHygiene.ToRepoRelativePath(path),
            LastUpdatedAt = report.CreatedAt
        };
    }

    private static async Task<ShadowCapabilityReadiness> BuildVectorFormalPreviewFreezeAsync(
        string current,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(current, "vector", "v4", "vector-formal-preview-freeze-gate.json");
        var report = await ReadJsonAsync<VectorFormalPreviewFreezeReport>(path, cancellationToken)
            .ConfigureAwait(false);
        var forbidden = new[]
        {
            ShadowRuntimeModes.DefaultOn,
            ShadowRuntimeModes.ApplyGuarded,
            ShadowRuntimeModes.RuntimeShadow,
            "ReadyForRuntimeSwitch",
            "RuntimeSwitch",
            "FormalRetrievalSwitch",
            "FormalRetrievalAllowed",
            "FormalIVectorIndexStoreBinding",
            "PostgresVectorIndexStoreFormalBinding",
            "VectorFormalPreview:Runtime",
            "ScopedPreviewOnly:Runtime",
            "FormalPackageWrite",
            "PackingPolicyIntegration",
            "PackageOutputIntegration",
            "NonAllowlistedScopeUse"
        };
        if (report is null)
        {
            return Missing(
                ShadowCapabilityIds.VectorFormalPreviewFreeze,
                "V4.F",
                path,
                ["MissingVectorFormalPreviewFreezeGateReport"],
                forbidden: forbidden);
        }

        var blocked = report.BlockedReasons.ToList();
        if (!report.FreezePassed)
        {
            blocked.Add("VectorFormalPreviewFreezeNotPassed");
        }

        if (!string.Equals(report.Recommendation, VectorFormalPreviewFreezeRecommendations.ReadyForScopedOptInPreview, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("VectorFormalPreviewFreezeRecommendationNotReady");
        }

        if (report.ReadyForRuntimeSwitch
            || report.FormalRetrievalAllowed
            || report.UseForRuntime
            || report.RuntimeSwitchAllowed)
        {
            blocked.Add("RuntimeOrFormalRetrievalFlagEnabled");
        }

        if (report.RiskAfterPolicy != 0
            || report.MustNotHitRiskAfterPolicy != 0
            || report.LifecycleRiskAfterPolicy != 0)
        {
            blocked.Add("RiskCountNonZero");
        }

        if (report.FormalOutputChanged != 0)
        {
            blocked.Add("FormalOutputChangedNonZero");
        }

        if (report.PackageOutputChanged)
        {
            blocked.Add("PackageOutputChanged");
        }

        if (report.PackingPolicyChanged)
        {
            blocked.Add("PackingPolicyChanged");
        }

        if (report.FormalPackageWritten)
        {
            blocked.Add("FormalPackageWritten");
        }

        if (report.RuntimeMutated)
        {
            blocked.Add("RuntimeMutated");
        }

        if (report.NonAllowlistedScopeLeakCount != 0)
        {
            blocked.Add("NonAllowlistedScopeLeak");
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ready = distinctBlocked.Length == 0;
        return new ShadowCapabilityReadiness
        {
            CapabilityId = ShadowCapabilityIds.VectorFormalPreviewFreeze,
            CurrentPhase = "V4.F",
            Status = ready
                ? VectorFormalPreviewFreezeStatuses.ReadyForScopedOptInPreview
                : ShadowCapabilityReadinessStatuses.PreviewOnly,
            Recommendation = report.Recommendation,
            GatePassed = ready,
            BlockedReasons = distinctBlocked,
            AllowedRuntimeModes = ready
                ? [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly, ShadowRuntimeModes.Shadow, "ScopedPreviewOnly", "FormalPreviewFreezeOnly"]
                : [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly],
            ForbiddenRuntimeModes = forbidden,
            LastEvalReportPath = PathHygiene.ToRepoRelativePath(path),
            LastUpdatedAt = report.CreatedAt
        };
    }

    private static async Task<ShadowCapabilityReadiness> BuildScopedRuntimeExperimentHarnessFreezeAsync(
        string current,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(current, "vector", "v4", "runtime-experiment", "harness-freeze-gate.json");
        var report = await ReadJsonAsync<ScopedRuntimeExperimentHarnessFreezeReport>(path, cancellationToken)
            .ConfigureAwait(false);
        var forbidden = new[]
        {
            ShadowRuntimeModes.DefaultOn,
            ShadowRuntimeModes.ApplyGuarded,
            ShadowRuntimeModes.RuntimeShadow,
            "ReadyForRuntimeSwitch",
            "RuntimeSwitch",
            "FormalRetrievalSwitch",
            "FormalRetrievalAllowed",
            "FormalIVectorIndexStoreBinding",
            "PostgresVectorIndexStoreFormalBinding",
            "FormalPackageWrite",
            "DIBindingMutation",
            "VectorStoreBindingMutation",
            "PackingPolicyIntegration",
            "PackageOutputIntegration",
            "GlobalDefaultOn",
            "NoOpHarnessOnly:RuntimeApproval",
            "NoOpHarnessOnlyAsRuntimeApproval"
        };
        if (report is null)
        {
            return Missing(
                ShadowCapabilityIds.ScopedRuntimeExperimentHarnessFreeze,
                "V4.10",
                path,
                ["MissingScopedRuntimeExperimentHarnessFreezeGateReport"],
                forbidden: forbidden);
        }

        var blocked = report.BlockedReasons.ToList();
        if (!report.FreezePassed)
        {
            blocked.Add("ScopedRuntimeExperimentHarnessFreezeNotPassed");
        }

        if (!string.Equals(
                report.Recommendation,
                ScopedRuntimeExperimentHarnessFreezeRecommendations.ReadyForGuardedRuntimeExperimentPlanning,
                StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("ScopedRuntimeExperimentHarnessFreezeRecommendationNotReady");
        }

        if (!string.Equals(report.ApprovalMode, ScopedRuntimeExperimentApprovalModes.NoOpHarnessOnly, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("ApprovalModeNotNoOpHarnessOnly");
        }

        if (report.RuntimeMutated
            || report.VectorStoreBindingChanged
            || report.FormalPackageWritten
            || report.PackingPolicyChanged
            || report.PackageOutputChanged)
        {
            blocked.Add("RuntimeOrPackageMutationDetected");
        }

        if (report.FormalRetrievalAllowed
            || report.RuntimeSwitchAllowed
            || report.ReadyForRuntimeSwitch)
        {
            blocked.Add("RuntimeOrFormalRetrievalFlagEnabled");
        }

        if (report.RiskAfterPolicy != 0)
        {
            blocked.Add("RiskCountNonZero");
        }

        if (report.FormalOutputChanged != 0)
        {
            blocked.Add("FormalOutputChangedNonZero");
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ready = distinctBlocked.Length == 0;
        return new ShadowCapabilityReadiness
        {
            CapabilityId = ShadowCapabilityIds.ScopedRuntimeExperimentHarnessFreeze,
            CurrentPhase = "V4.10",
            Status = ready
                ? "ReadyForGuardedRuntimeExperimentPlanning"
                : ShadowCapabilityReadinessStatuses.PreviewOnly,
            Recommendation = report.Recommendation,
            GatePassed = ready,
            BlockedReasons = distinctBlocked,
            AllowedRuntimeModes = ready
                ? [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly, ShadowRuntimeModes.Shadow, "NoOpHarnessOnly", "ExplicitScopedExperimentPlanningOnly"]
                : [ShadowRuntimeModes.Off, ShadowRuntimeModes.PreviewOnly],
            ForbiddenRuntimeModes = forbidden,
            LastEvalReportPath = PathHygiene.ToRepoRelativePath(path),
            LastUpdatedAt = report.CreatedAt
        };
    }

    private static async Task<ShadowCapabilityReadiness> BuildRouterIntentClassifierAsync(
        string current,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(current, "learning", "router", "router-guarded-optin-readiness-gate.json");
        var report = await ReadJsonAsync<RouterGuardedOptInReadinessGateReport>(path, cancellationToken)
            .ConfigureAwait(false);
        if (report is null)
        {
            return Missing(
                ShadowCapabilityIds.RouterIntentClassifier,
                "R2.F",
                path,
                ["MissingRouterReadinessGateReport"],
                forbidden: [ShadowRuntimeModes.RuntimeShadow, ShadowRuntimeModes.ApplyGuarded, ShadowRuntimeModes.DefaultOn]);
        }

        return new ShadowCapabilityReadiness
        {
            CapabilityId = ShadowCapabilityIds.RouterIntentClassifier,
            CurrentPhase = "R2.F",
            Status = report.Passed
                ? ShadowCapabilityReadinessStatuses.ReadyForGuardedOptIn
                : ShadowCapabilityReadinessStatuses.KeepRuleBased,
            Recommendation = report.Recommendation,
            GatePassed = report.Passed,
            BlockedReasons = report.FailureReasons,
            AllowedRuntimeModes = report.Passed
                ? [ShadowRuntimeModes.ExistingRuntime, ShadowRuntimeModes.Shadow, ShadowRuntimeModes.ApplyGuarded]
                : [ShadowRuntimeModes.ExistingRuntime],
            ForbiddenRuntimeModes = report.Passed
                ? [ShadowRuntimeModes.DefaultOn]
                : [ShadowRuntimeModes.RuntimeShadow, ShadowRuntimeModes.ApplyGuarded, ShadowRuntimeModes.DefaultOn],
            LastEvalReportPath = PathHygiene.ToRepoRelativePath(path),
            LastUpdatedAt = report.GeneratedAt
        };
    }

    private static async Task<ShadowCapabilityReadiness> BuildCandidateRerankerAsync(
        string current,
        CancellationToken cancellationToken)
    {
        var a3Path = Path.Combine(current, "learning", "ranker", "candidate-reranker-shadow-eval-a3.json");
        var extendedPath = Path.Combine(current, "learning", "ranker", "candidate-reranker-shadow-eval-extended.json");
        var a3 = await ReadJsonAsync<CandidateRerankerShadowEvalReport>(a3Path, cancellationToken)
            .ConfigureAwait(false);
        var extended = await ReadJsonAsync<CandidateRerankerShadowEvalReport>(extendedPath, cancellationToken)
            .ConfigureAwait(false);
        if (a3 is null || extended is null)
        {
            return Missing(
                ShadowCapabilityIds.CandidateReranker,
                "CR1.4",
                $"{a3Path};{extendedPath}",
                ["MissingCandidateRerankerShadowEvalReport"],
                forbidden: [ShadowRuntimeModes.RuntimeShadow, ShadowRuntimeModes.ApplyGuarded, ShadowRuntimeModes.DefaultOn]);
        }

        var blocked = new List<string>();
        if (a3.NetGain <= 0)
        {
            blocked.Add("A3NetGainNotPositive");
        }

        if (extended.NetGain <= 0)
        {
            blocked.Add("ExtendedNetGainNotPositive");
        }

        if (a3.NetGain + extended.NetGain <= 0)
        {
            blocked.Add("NetGainNotPositive");
        }

        if (a3.LifecycleRiskCount + a3.DeprecatedRiskCount + a3.MustNotRiskCount
            + extended.LifecycleRiskCount + extended.DeprecatedRiskCount + extended.MustNotRiskCount > 0)
        {
            blocked.Add("RiskCountNonZero");
        }

        var passed = blocked.Count == 0;
        return new ShadowCapabilityReadiness
        {
            CapabilityId = ShadowCapabilityIds.CandidateReranker,
            CurrentPhase = "CR1.4",
            Status = passed
                ? "ReadyForRankerShadow"
                : ShadowCapabilityReadinessStatuses.KeepFormalRanking,
            Recommendation = passed ? "ReadyForRankerShadow" : ShadowCapabilityReadinessStatuses.KeepFormalRanking,
            GatePassed = passed,
            BlockedReasons = blocked,
            AllowedRuntimeModes = passed
                ? [ShadowRuntimeModes.Off, ShadowRuntimeModes.RuntimeShadow]
                : [ShadowRuntimeModes.Off, ShadowCapabilityReadinessStatuses.KeepFormalRanking],
            ForbiddenRuntimeModes = passed
                ? [ShadowRuntimeModes.DefaultOn, ShadowRuntimeModes.ApplyGuarded]
                : [ShadowRuntimeModes.RuntimeShadow, ShadowRuntimeModes.ApplyGuarded, ShadowRuntimeModes.DefaultOn],
            LastEvalReportPath = $"{PathHygiene.ToRepoRelativePath(a3Path)};{PathHygiene.ToRepoRelativePath(extendedPath)}",
            LastUpdatedAt = a3.GeneratedAt > extended.GeneratedAt ? a3.GeneratedAt : extended.GeneratedAt
        };
    }

    private static ShadowCapabilityReadiness BuildAttentionRerank(string current)
        => new()
        {
            CapabilityId = ShadowCapabilityIds.AttentionRerank,
            CurrentPhase = "AttentionPhase4",
            Status = ShadowCapabilityReadinessStatuses.ApplyGuardedOptInOnly,
            Recommendation = "ApplyGuarded opt-in only; default off",
            GatePassed = true,
            BlockedReasons = [],
            AllowedRuntimeModes = [ShadowRuntimeModes.Off, ShadowRuntimeModes.Shadow, ShadowRuntimeModes.ApplyGuarded],
            ForbiddenRuntimeModes = [ShadowRuntimeModes.DefaultOn, "SelectedSetChanging"],
            LastEvalReportPath = PathHygiene.ToRepoRelativePath(Path.Combine(current, "docs", "attention-profile-selection-report.md")),
            LastUpdatedAt = DateTimeOffset.UtcNow
        };

    private static ShadowCapabilityReadiness BuildPlanningProposal(string current)
        => new()
        {
            CapabilityId = ShadowCapabilityIds.PlanningProposal,
            CurrentPhase = "P9/P10",
            Status = ShadowCapabilityReadinessStatuses.IntentScopedOptInOnly,
            Recommendation = "intent-scoped opt-in only; default off",
            GatePassed = true,
            BlockedReasons = [],
            AllowedRuntimeModes = [ShadowRuntimeModes.Off, ShadowRuntimeModes.Shadow, "ApplyGuarded:IntentScoped"],
            ForbiddenRuntimeModes = [ShadowRuntimeModes.DefaultOn, "GlobalApply", "VectorEnabled"],
            LastEvalReportPath = PathHygiene.ToRepoRelativePath(Path.Combine(current, "eval", "planning-optin-comparison-a3.json")),
            LastUpdatedAt = DateTimeOffset.UtcNow
        };

    private static ShadowCapabilityReadiness Missing(
        string capabilityId,
        string phase,
        string path,
        IReadOnlyList<string> blocked,
        IReadOnlyList<string> forbidden)
        => new()
        {
            CapabilityId = capabilityId,
            CurrentPhase = phase,
            Status = ShadowCapabilityReadinessStatuses.MissingReport,
            Recommendation = "RegenerateFreezeInputReport",
            GatePassed = false,
            BlockedReasons = blocked,
            AllowedRuntimeModes = [ShadowRuntimeModes.Off],
            ForbiddenRuntimeModes = forbidden,
            LastEvalReportPath = PathHygiene.ToRepoRelativePath(path),
            LastUpdatedAt = DateTimeOffset.UtcNow
        };

    private static void AddCheck(
        List<LearningRuntimeChangeReadinessGateCheck> checks,
        ShadowCapabilityReadiness capability,
        string condition,
        bool passed,
        string reason)
        => checks.Add(new LearningRuntimeChangeReadinessGateCheck
        {
            CapabilityId = capability.CapabilityId,
            Condition = condition,
            Passed = passed,
            Reason = reason
        });

    private static bool AllowsAny(ShadowCapabilityReadiness capability, params string[] runtimeModes)
        => runtimeModes.Any(mode => capability.AllowedRuntimeModes.Any(allowed =>
            string.Equals(allowed, mode, StringComparison.OrdinalIgnoreCase)
            || allowed.StartsWith($"{mode}:", StringComparison.OrdinalIgnoreCase)));

    private static bool IsForbidden(ShadowCapabilityReadiness capability, string runtimeMode)
        => capability.ForbiddenRuntimeModes.Any(forbidden =>
            string.Equals(forbidden, runtimeMode, StringComparison.OrdinalIgnoreCase));

    private static ShadowCapabilityReadiness? Find(LearningReadinessRegistry registry, string capabilityId)
        => registry.Capabilities.FirstOrDefault(item => string.Equals(
            item.CapabilityId,
            capabilityId,
            StringComparison.OrdinalIgnoreCase));

    private static bool IsRouterBreakReason(string reason)
        => string.Equals(reason, RouterGuardedOptInGateFailureReasons.ShadowBreaksRuntimeGreaterThanFixes, StringComparison.OrdinalIgnoreCase);

    private static bool IsRankerNetGainReason(string reason)
        => reason.Contains("NetGain", StringComparison.OrdinalIgnoreCase);

    private static string FormatList(IReadOnlyList<string> values)
        => values.Count == 0 ? "-" : string.Join("<br>", values);

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }
}
