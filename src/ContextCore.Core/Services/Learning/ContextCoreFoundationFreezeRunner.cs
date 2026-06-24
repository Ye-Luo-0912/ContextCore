using System.Text;
using System.Text.Json;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 全局 foundation freeze 汇总器；只读取既有报告，不改变任何 runtime/provider/package 行为。
/// </summary>
public sealed class ContextCoreFoundationFreezeRunner
{
    public const string DefaultOutputDirectory = "foundation";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] CriticalReportPaths =
    [
        Path.Combine("storage", "postgres", "postgres-relation-multi-normal-scope-quality-report.json"),
        Path.Combine("storage", "postgres", "postgres-learning-feedback-freeze-gate.json"),
        Path.Combine("storage", "postgres", "postgres-job-queue-freeze-gate.json"),
        Path.Combine("storage", "postgres", "postgres-vector-freeze-gate.json"),
        Path.Combine("vector", "v4", "vector-formal-preview-freeze-gate.json"),
        Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json"),
        Path.Combine("eval", "eval-report-p15-a3.json"),
        Path.Combine("eval", "eval-report-p15-extended.json")
    ];

    private static readonly string[] CriticalDocPaths =
    [
        Path.Combine("docs", "relation-governance-postgres-freeze.md"),
        Path.Combine("docs", "postgres-operational-store.md"),
        Path.Combine("docs", "job-queue-postgres-freeze.md"),
        Path.Combine("docs", "vector-postgres-provider-freeze.md"),
        Path.Combine("docs", "vector-embedding-provider-comparison-freeze.md"),
        Path.Combine("docs", "vector-hybrid-retrieval-freeze.md"),
        Path.Combine("docs", "vector-preview-shadow-freeze.md"),
        Path.Combine("docs", "learning-loop-foundation.md"),
        Path.Combine("docs", "ContextCore_Foundation_Freeze_Report.md"),
        Path.Combine("docs", "controlroom-service-mode.md")
    ];

    public async Task<ContextCoreFoundationFreezeReport> BuildFromCurrentFilesAsync(
        string currentDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        var relation = await ReadJsonAsync<PostgresRelationMultiNormalScopeCanaryReport>(
                Path.Combine(currentDirectory, CriticalReportPaths[0]),
                cancellationToken)
            .ConfigureAwait(false);
        var learningFeedback = await ReadJsonAsync<LearningFeedbackPostgresFreezeGateReport>(
                Path.Combine(currentDirectory, CriticalReportPaths[1]),
                cancellationToken)
            .ConfigureAwait(false);
        var jobQueue = await ReadJsonAsync<JobQueuePostgresFreezeGateReport>(
                Path.Combine(currentDirectory, CriticalReportPaths[2]),
                cancellationToken)
            .ConfigureAwait(false);
        var vectorPostgres = await ReadJsonAsync<VectorPostgresProviderFreezeGateReport>(
                Path.Combine(currentDirectory, CriticalReportPaths[3]),
                cancellationToken)
            .ConfigureAwait(false);
        var vectorFormal = await ReadJsonAsync<VectorFormalPreviewFreezeReport>(
                Path.Combine(currentDirectory, CriticalReportPaths[4]),
                cancellationToken)
            .ConfigureAwait(false);
        var runtimeGate = await ReadJsonAsync<LearningRuntimeChangeReadinessGateReport>(
                Path.Combine(currentDirectory, CriticalReportPaths[5]),
                cancellationToken)
            .ConfigureAwait(false);
        var p15A3 = await ReadP15StatusAsync(Path.Combine(currentDirectory, CriticalReportPaths[6]), cancellationToken)
            .ConfigureAwait(false);
        var p15Extended = await ReadP15StatusAsync(Path.Combine(currentDirectory, CriticalReportPaths[7]), cancellationToken)
            .ConfigureAwait(false);

        var generatedReportCoverage = CriticalReportPaths.ToDictionary(
            static path => path,
            path => File.Exists(Path.Combine(currentDirectory, path)),
            StringComparer.OrdinalIgnoreCase);
        var docsCoverage = CriticalDocPaths.ToDictionary(
            static path => path,
            path => File.Exists(Path.Combine(currentDirectory, path)),
            StringComparer.OrdinalIgnoreCase);
        var controlRoomCoverage = BuildControlRoomCoverage(currentDirectory);

        return BuildReport(
            relation,
            learningFeedback,
            jobQueue,
            vectorPostgres,
            vectorFormal,
            runtimeGate,
            p15A3,
            p15Extended,
            generatedReportCoverage,
            docsCoverage,
            controlRoomCoverage);
    }

    public ContextCoreFoundationFreezeReport BuildReport(
        PostgresRelationMultiNormalScopeCanaryReport? relation,
        LearningFeedbackPostgresFreezeGateReport? learningFeedback,
        JobQueuePostgresFreezeGateReport? jobQueue,
        VectorPostgresProviderFreezeGateReport? vectorPostgres,
        VectorFormalPreviewFreezeReport? vectorFormal,
        LearningRuntimeChangeReadinessGateReport? runtimeGate,
        P15ReportStatus p15A3,
        P15ReportStatus p15Extended,
        IReadOnlyDictionary<string, bool> generatedReportCoverage,
        IReadOnlyDictionary<string, bool> docsCoverage,
        IReadOnlyDictionary<string, bool> controlRoomCoverage)
    {
        var blocked = new List<string>();

        var relationReady = relation is not null
            && relation.GatePassed
            && relation.MismatchCount == 0
            && relation.PostgresFailureCount == 0
            && relation.ScopeLeakCount == 0
            && string.Equals(relation.Recommendation, "ReadyForLimitedScopeExpansion", StringComparison.OrdinalIgnoreCase);
        AddBlockedIfFalse(blocked, relationReady, "RelationGovernancePostgresFreezeNotPassed");

        var learningReady = learningFeedback is not null
            && learningFeedback.Passed
            && learningFeedback.MismatchCount == 0
            && learningFeedback.PostgresFailureCount == 0
            && learningFeedback.ScopeLeakCount == 0
            && learningFeedback.TrainableCandidateLeakCount == 0
            && string.Equals(learningFeedback.LearningFeedbackPostgres, "ReadyForScopedServiceMode", StringComparison.OrdinalIgnoreCase);
        AddBlockedIfFalse(blocked, learningReady, "LearningFeedbackPostgresFreezeNotPassed");

        var jobQueueReady = jobQueue is not null
            && jobQueue.Passed
            && jobQueue.DuplicateExecutionCount == 0
            && jobQueue.LeaseViolationCount == 0
            && jobQueue.RetryViolationCount == 0
            && jobQueue.DeadLetterViolationCount == 0
            && jobQueue.PostgresFailureCount == 0
            && jobQueue.ScopeLeakCount == 0
            && jobQueue.RuntimeWorkerGlobalProviderUnchanged
            && string.Equals(jobQueue.JobQueuePostgres, "ReadyForScopedWorkerMode", StringComparison.OrdinalIgnoreCase);
        AddBlockedIfFalse(blocked, jobQueueReady, "JobQueuePostgresFreezeNotPassed");

        var vectorPostgresReady = vectorPostgres is not null
            && vectorPostgres.Passed
            && !vectorPostgres.UseForRuntime
            && !vectorPostgres.FormalRetrievalAllowed
            && string.Equals(vectorPostgres.VectorPostgresProvider, "ReadyForPreviewShadowStorage", StringComparison.OrdinalIgnoreCase);
        AddBlockedIfFalse(blocked, vectorPostgresReady, "VectorPostgresProviderFreezeNotPassed");

        var vectorFormalReady = vectorFormal is not null
            && vectorFormal.FreezePassed
            && !vectorFormal.UseForRuntime
            && !vectorFormal.FormalRetrievalAllowed
            && !vectorFormal.ReadyForRuntimeSwitch
            && !vectorFormal.RuntimeSwitchAllowed
            && !vectorFormal.PackingPolicyChanged
            && !vectorFormal.PackageOutputChanged
            && !vectorFormal.FormalPackageWritten
            && !vectorFormal.RuntimeMutated
            && vectorFormal.NonAllowlistedScopeLeakCount == 0
            && string.Equals(vectorFormal.VectorFormalPreview, VectorFormalPreviewFreezeStatuses.ReadyForScopedOptInPreview, StringComparison.OrdinalIgnoreCase);
        AddBlockedIfFalse(blocked, vectorFormalReady, "VectorFormalPreviewFreezeNotPassed");

        var runtimeGatePassed = runtimeGate?.Passed == true;
        AddBlockedIfFalse(blocked, runtimeGatePassed, "RuntimeChangeGateNotPassed");

        var p15Passed = p15A3.Passed && p15Extended.Passed;
        AddBlockedIfFalse(blocked, p15Passed, "P15GateNotPassed");

        var formalRetrievalAllowed = vectorFormal?.FormalRetrievalAllowed == true
            || vectorPostgres?.FormalRetrievalAllowed == true;
        AddBlockedIfFalse(blocked, !formalRetrievalAllowed, "FormalRetrievalAllowed");

        var readyForRuntimeSwitch = vectorFormal?.ReadyForRuntimeSwitch == true
            || vectorFormal?.RuntimeSwitchAllowed == true;
        AddBlockedIfFalse(blocked, !readyForRuntimeSwitch, "RuntimeSwitchAllowed");

        var packingPolicyChanged = vectorFormal?.PackingPolicyChanged == true;
        AddBlockedIfFalse(blocked, !packingPolicyChanged, "PackingPolicyChanged");

        var packageOutputChanged = vectorFormal?.PackageOutputChanged == true;
        AddBlockedIfFalse(blocked, !packageOutputChanged, "PackageOutputChanged");

        var missingReportCount = generatedReportCoverage.Count(static pair => !pair.Value);
        if (missingReportCount != 0)
        {
            blocked.Add("MissingCriticalReport");
        }

        var missingDocCount = docsCoverage.Count(static pair => !pair.Value);
        if (missingDocCount != 0)
        {
            blocked.Add("MissingFreezeDocument");
        }

        var missingControlRoomCoverage = controlRoomCoverage.Count(static pair => !pair.Value);
        if (missingControlRoomCoverage != 0)
        {
            blocked.Add("MissingControlRoomCoverage");
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var freezePassed = distinctBlocked.Length == 0;

        return new ContextCoreFoundationFreezeReport
        {
            OperationId = $"contextcore-foundation-freeze-{Guid.NewGuid():N}",
            GeneratedAt = DateTimeOffset.UtcNow,
            FreezePassed = freezePassed,
            Recommendation = freezePassed
                ? ContextCoreFoundationFreezeRecommendations.ReadyForReleaseCandidate
                : ResolveRecommendation(distinctBlocked),
            ContextCoreFoundation = freezePassed ? "Frozen" : "NotFrozen",
            StorageFoundation = relationReady && learningReady && jobQueueReady && vectorPostgresReady ? "Frozen" : "NotFrozen",
            VectorFoundation = vectorFormalReady ? "ReadyForScopedFormalPreview" : "KeepPreviewOnly",
            NextAllowedPhase = freezePassed
                ? "ScopedRuntimeExperimentPlanning or NextSubsystemDevelopment"
                : "ResolveBlockedFoundationFreezeItems",
            RelationGovernanceStatus = relation?.Recommendation ?? "MissingReport",
            LearningFeedbackStatus = learningFeedback?.LearningFeedbackPostgres ?? "MissingReport",
            JobQueueStatus = jobQueue?.JobQueuePostgres ?? "MissingReport",
            VectorPostgresProviderStatus = vectorPostgres?.VectorPostgresProvider ?? "MissingReport",
            VectorFormalPreviewStatus = vectorFormal?.VectorFormalPreview ?? "MissingReport",
            RuntimeChangeGateStatus = runtimeGate is null
                ? "MissingReport"
                : runtimeGate.Passed ? "Passed" : "Failed",
            P15GateStatus = p15Passed ? "Passed" : "Failed",
            FormalRetrievalAllowed = formalRetrievalAllowed,
            ReadyForRuntimeSwitch = readyForRuntimeSwitch,
            RuntimeSwitchAllowed = false,
            PackingPolicyChanged = packingPolicyChanged,
            PackageOutputChanged = packageOutputChanged,
            MissingReportCount = missingReportCount,
            MissingDocCount = missingDocCount,
            StorageProviderReadiness = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ShadowCapabilityIds.RelationGovernance] = relationReady ? "ReadyForLimitedScopeExpansion" : "Blocked",
                ["LearningFeedbackPostgres"] = learningReady ? "ReadyForScopedServiceMode" : "Blocked",
                [ShadowCapabilityIds.JobQueuePostgres] = jobQueueReady ? "ReadyForScopedWorkerMode" : "Blocked",
                [ShadowCapabilityIds.VectorPostgresProvider] = vectorPostgresReady ? "ReadyForPreviewShadowStorage" : "Blocked"
            },
            VectorProviderReadiness = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ShadowCapabilityIds.VectorPostgresProvider] = vectorPostgres?.VectorPostgresProvider ?? "MissingReport",
                [ShadowCapabilityIds.Qwen3EmbeddingProvider] = "DoNotPromoteOrPreviewOnly",
                [ShadowCapabilityIds.HybridRetrievalPreview] = "KeepPreviewOnly",
                [ShadowCapabilityIds.DatasetV2Stress] = "ReadyForV4RecheckInput"
            },
            VectorFormalPreviewReadiness = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ShadowCapabilityIds.VectorV4ReadinessRecheck] = vectorFormal?.V4ReadinessRecheckPassed == true ? "Passed" : "Blocked",
                [ShadowCapabilityIds.GuardedFormalRetrievalPreview] = vectorFormal?.GuardedFormalPreviewGatePassed == true ? "Passed" : "Blocked",
                [ShadowCapabilityIds.VectorShadowPackageComparison] = vectorFormal?.ShadowPackageComparisonGatePassed == true ? "Passed" : "Blocked",
                [ShadowCapabilityIds.ScopedFormalPreviewOptIn] = vectorFormal?.ScopedFormalPreviewOptInGatePassed == true ? "Passed" : "Blocked",
                [ShadowCapabilityIds.LimitedFormalPreviewObservation] = vectorFormal?.LimitedFormalPreviewObservationGatePassed == true ? "Passed" : "Blocked",
                [ShadowCapabilityIds.VectorFormalPreviewFreeze] = vectorFormalReady ? "ReadyForScopedOptInPreview" : "Blocked"
            },
            ControlRoomCoverage = controlRoomCoverage,
            DocsCoverage = docsCoverage,
            GeneratedReportCoverage = generatedReportCoverage,
            BlockedReasons = distinctBlocked
        };
    }

    public static string BuildMarkdown(ContextCoreFoundationFreezeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# ContextCore Foundation Freeze / Release Candidate Gate");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.GeneratedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- FreezePassed: `{report.FreezePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- ContextCoreFoundation: `{report.ContextCoreFoundation}`");
        builder.AppendLine($"- StorageFoundation: `{report.StorageFoundation}`");
        builder.AppendLine($"- VectorFoundation: `{report.VectorFoundation}`");
        builder.AppendLine($"- NextAllowedPhase: `{report.NextAllowedPhase}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- MissingReportCount: `{report.MissingReportCount}`");
        builder.AppendLine($"- MissingDocCount: `{report.MissingDocCount}`");
        AppendMap(builder, "Storage Provider Readiness", report.StorageProviderReadiness);
        AppendMap(builder, "Vector Provider Readiness", report.VectorProviderReadiness);
        AppendMap(builder, "Vector Formal Preview Readiness", report.VectorFormalPreviewReadiness);
        AppendBoolMap(builder, "Generated Report Coverage", report.GeneratedReportCoverage);
        AppendBoolMap(builder, "Docs Coverage", report.DocsCoverage);
        AppendBoolMap(builder, "ControlRoom Coverage", report.ControlRoomCoverage);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("## Runtime Boundary");
        builder.AppendLine();
        builder.AppendLine("- Foundation freeze 通过不等于 runtime switch。");
        builder.AppendLine("- formal retrieval、正式 `IVectorIndexStore` 绑定、正式 package 写入、`PackingPolicy` / package output mutation 继续禁止。");
        builder.AppendLine("- 下一阶段只允许 scoped experiment planning 或独立子系统开发。");
        return builder.ToString();
    }

    private static IReadOnlyDictionary<string, bool> BuildControlRoomCoverage(string currentDirectory)
    {
        var rendererPath = Path.Combine(
            currentDirectory,
            "src",
            "ContextCore.ControlRoom",
            "Rendering",
            "ServiceOperationalRenderer.cs");
        var servicePath = Path.Combine(
            currentDirectory,
            "src",
            "ContextCore.ControlRoom",
            "Services",
            "ControlRoomService.cs");
        var renderer = File.Exists(rendererPath) ? File.ReadAllText(rendererPath) : string.Empty;
        var service = File.Exists(servicePath) ? File.ReadAllText(servicePath) : string.Empty;
        return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["Foundation Freeze Summary renderer"] = renderer.Contains("Foundation Freeze Summary", StringComparison.Ordinal),
            ["Foundation freeze report loader"] = service.Contains("ReadFoundationFreezeReportAsync", StringComparison.Ordinal),
            ["Vector formal preview freeze status"] = renderer.Contains("Formal Preview Freeze Status", StringComparison.Ordinal)
        };
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using var stream = File.OpenRead(path);
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static async Task<P15ReportStatus> ReadP15StatusAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new P15ReportStatus(false, 0, 0, 0, "MissingReport");
        }

        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            var total = GetInt(root, "TotalSamples");
            var failed = GetInt(root, "FailedSamples");
            var invalid = GetInt(root, "InvalidSamples");
            return new P15ReportStatus(
                total > 0 && failed == 0 && invalid == 0,
                total,
                failed,
                invalid,
                "Loaded");
        }
        catch (JsonException)
        {
            return new P15ReportStatus(false, 0, 0, 0, "InvalidReport");
        }
    }

    private static int GetInt(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result)
            ? result
            : 0;
    }

    private static void AddBlockedIfFalse(ICollection<string> blocked, bool condition, string reason)
    {
        if (!condition)
        {
            blocked.Add(reason);
        }
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked)
    {
        if (blocked.Any(static reason => reason.Contains("MissingCriticalReport", StringComparison.OrdinalIgnoreCase)))
        {
            return ContextCoreFoundationFreezeRecommendations.BlockedByMissingReport;
        }

        if (blocked.Any(static reason => reason.Contains("MissingFreezeDocument", StringComparison.OrdinalIgnoreCase)))
        {
            return ContextCoreFoundationFreezeRecommendations.BlockedByMissingDoc;
        }

        if (blocked.Any(static reason => reason.Contains("RuntimeChangeGate", StringComparison.OrdinalIgnoreCase)))
        {
            return ContextCoreFoundationFreezeRecommendations.BlockedByRuntimeChangeGate;
        }

        if (blocked.Any(static reason => reason.Contains("P15", StringComparison.OrdinalIgnoreCase)))
        {
            return ContextCoreFoundationFreezeRecommendations.BlockedByP15Gate;
        }

        if (blocked.Any(static reason => reason.Contains("RuntimeSwitch", StringComparison.OrdinalIgnoreCase)))
        {
            return ContextCoreFoundationFreezeRecommendations.BlockedByRuntimeSwitch;
        }

        if (blocked.Any(static reason => reason.Contains("FormalRetrieval", StringComparison.OrdinalIgnoreCase)))
        {
            return ContextCoreFoundationFreezeRecommendations.BlockedByFormalRetrieval;
        }

        if (blocked.Any(static reason => reason.Contains("PackingPolicy", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("PackageOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return ContextCoreFoundationFreezeRecommendations.BlockedByPackageMutation;
        }

        return ContextCoreFoundationFreezeRecommendations.KeepFrozenPreviewOnly;
    }

    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var value in values)
        {
            builder.AppendLine($"- `{value}`");
        }
    }

    private static void AppendMap(StringBuilder builder, string title, IReadOnlyDictionary<string, string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        foreach (var pair in values.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {pair.Key}: `{pair.Value}`");
        }
    }

    private static void AppendBoolMap(StringBuilder builder, string title, IReadOnlyDictionary<string, bool> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        foreach (var pair in values.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {pair.Key}: `{pair.Value}`");
        }
    }
}

public readonly record struct P15ReportStatus(
    bool Passed,
    int TotalSamples,
    int FailedSamples,
    int InvalidSamples,
    string Status);
