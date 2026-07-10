using System.Text;
using System.Text.Json;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 项目主线状态审计：只读取既有报告并输出汇总，不改变运行时、检索绑定或 package 输出。
/// </summary>
public sealed class ProjectStateAuditRunner
{
    private const string OverallStatus = "FoundationFrozen_FormalRetrievalPlanOnly";

    private static readonly string[] MustDoBeforeFormalRetrieval =
    [
        "Build ShadowFormalRetrievalAdapter as the next V5 phase.",
        "Run formal adapter shadow comparison against package assembly without package output mutation.",
        "Recheck graph relation quality, relation noise, and graph contribution before formal candidate use.",
        "Enforce ingestion evidence/provenance/lifecycle metadata contract for formal retrieval inputs.",
        "Define output package token budget and priority policy shadow checks before any PackingPolicy integration."
    ];

    private static readonly string[] CanDefer =
    [
        "Legacy corpus recall repair that lacks evidence/provenance.",
        "Additional service API surfaces beyond the frozen read-only foundation API.",
        "Broad provider comparison refresh unless the formal adapter requires it."
    ];

    private static readonly string[] OptimizationLater =
    [
        "Consolidate repeated report/gate runner boilerplate after mainline adapter gates are stable.",
        "Add focused performance baselines for shadow adapter candidate generation.",
        "Reduce artifact reader duplication in ControlRoom status rendering."
    ];

    private static readonly string[] SideBranchCleanupLater =
    [
        "Prune obsolete smoke traces and superseded generated reports after a release tag.",
        "Compact older phase notes that are already represented by freeze reports.",
        "Archive exploratory side-branch eval outputs outside the mainline gate chain."
    ];

    public ProjectStateAuditReport BuildProjectStateAudit(string repositoryRoot)
    {
        var matrix = BuildMatrix(repositoryRoot);
        var gaps = BuildMainlineGaps(matrix);
        var ready = matrix
            .Where(static entry => string.Equals(entry.Status, ProjectStateAuditStatuses.Frozen, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Status, ProjectStateAuditStatuses.Ready, StringComparison.OrdinalIgnoreCase))
            .Select(static entry => entry.CapabilityId)
            .ToArray();
        var preview = matrix
            .Where(static entry => string.Equals(entry.Status, ProjectStateAuditStatuses.PreviewOnly, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Status, ProjectStateAuditStatuses.PlanOnly, StringComparison.OrdinalIgnoreCase))
            .Select(static entry => entry.CapabilityId)
            .ToArray();
        var blocked = matrix
            .Where(static entry => string.Equals(entry.Status, ProjectStateAuditStatuses.Blocked, StringComparison.OrdinalIgnoreCase)
                || !entry.SourceReportExists)
            .Select(static entry => entry.CapabilityId)
            .Concat(["FormalRetrievalRuntimeSwitch", "FormalVectorStoreBinding", "FormalPackageWrite"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var missing = matrix
            .Where(static entry => !entry.SourceReportExists)
            .Select(static entry => entry.CapabilityId)
            .ToArray();

        return new ProjectStateAuditReport
        {
            OperationId = $"project-state-audit-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CurrentOverallStatus = OverallStatus,
            Recommendation = missing.Length == 0
                ? ProjectStateAuditRecommendations.ReadyForMainlineGapRepairPlanning
                : ProjectStateAuditRecommendations.NeedsMissingReportRegeneration,
            ReadyCapabilities = ready,
            PreviewOnlyCapabilities = preview,
            BlockedCapabilities = blocked,
            CapabilityReadinessMatrix = matrix,
            MainlineRisks = BuildMainlineRisks(),
            QualityGaps = gaps
                .Where(static gap => string.Equals(gap.Severity, "High", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(gap.Severity, "Medium", StringComparison.OrdinalIgnoreCase))
                .Select(static gap => $"{gap.Area}: {gap.Summary}")
                .ToArray(),
            PerformanceGaps = BuildPerformanceGaps(),
            RecommendedNextPhases = BuildRecommendedNextPhases(),
            SourceReports = matrix.ToDictionary(
                static entry => entry.CapabilityId,
                static entry => entry.SourceReportPath,
                StringComparer.OrdinalIgnoreCase),
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            PackingPolicyChanged = false,
            PackageOutputChanged = false,
            BlockedReasons = missing
        };
    }

    public MainlineGapMapReport BuildMainlineGapMap(string repositoryRoot)
    {
        var audit = BuildProjectStateAudit(repositoryRoot);
        var gaps = BuildMainlineGaps(audit.CapabilityReadinessMatrix);

        return new MainlineGapMapReport
        {
            OperationId = $"mainline-gap-map-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CurrentOverallStatus = audit.CurrentOverallStatus,
            Recommendation = ProjectStateAuditRecommendations.ReadyForMainlineGapRepairPlanning,
            ReadyCapabilities = audit.ReadyCapabilities,
            PreviewOnlyCapabilities = audit.PreviewOnlyCapabilities,
            BlockedCapabilities = audit.BlockedCapabilities,
            MainlineGaps = gaps,
            MainlineRisks = audit.MainlineRisks,
            QualityGaps = audit.QualityGaps,
            PerformanceGaps = audit.PerformanceGaps,
            MustDoBeforeFormalRetrieval = MustDoBeforeFormalRetrieval,
            CanDefer = CanDefer,
            OptimizationLater = OptimizationLater,
            SideBranchCleanupLater = SideBranchCleanupLater,
            RecommendedNextPhases = BuildRecommendedNextPhases(),
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            PackingPolicyChanged = false,
            PackageOutputChanged = false
        };
    }

    public static string BuildProjectStateMarkdown(ProjectStateAuditReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# ContextCore Project State Audit");
        builder.AppendLine();
        AppendConclusion(builder, report.CurrentOverallStatus, report.Recommendation);
        AppendList(builder, "Ready Capabilities", report.ReadyCapabilities);
        AppendList(builder, "Preview Only Capabilities", report.PreviewOnlyCapabilities);
        AppendList(builder, "Blocked Capabilities", report.BlockedCapabilities);
        builder.AppendLine();
        builder.AppendLine("## Capability Readiness Matrix");
        builder.AppendLine("| Area | Capability | Status | Recommendation | Source |");
        builder.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var entry in report.CapabilityReadinessMatrix)
        {
            builder.AppendLine($"| {entry.Area} | {entry.CapabilityId} | {entry.Status} | {entry.Recommendation} | {entry.SourceReportPath} |");
        }

        AppendList(builder, "Mainline Risks", report.MainlineRisks);
        AppendList(builder, "Quality Gaps", report.QualityGaps);
        AppendList(builder, "Performance Gaps", report.PerformanceGaps);
        AppendList(builder, "Recommended Next Phases", report.RecommendedNextPhases);
        AppendBoundary(builder, report.FormalRetrievalAllowed, report.RuntimeSwitchAllowed, report.ReadyForRuntimeSwitch, report.PackingPolicyChanged, report.PackageOutputChanged);
        return builder.ToString();
    }

    public static string BuildMainlineGapMapMarkdown(MainlineGapMapReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# ContextCore Mainline Gap Map");
        builder.AppendLine();
        AppendConclusion(builder, report.CurrentOverallStatus, report.Recommendation);
        builder.AppendLine("## Mainline Gaps");
        builder.AppendLine("| Area | Severity | Gap | Bucket | Recommended Action |");
        builder.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var gap in report.MainlineGaps)
        {
            builder.AppendLine($"| {gap.Area} | {gap.Severity} | {gap.Summary} | {gap.Bucket} | {gap.RecommendedAction} |");
        }

        AppendList(builder, "Must Do Before Formal Retrieval", report.MustDoBeforeFormalRetrieval);
        AppendList(builder, "Can Defer", report.CanDefer);
        AppendList(builder, "Optimization Later", report.OptimizationLater);
        AppendList(builder, "Side Branch Cleanup Later", report.SideBranchCleanupLater);
        AppendBoundary(builder, report.FormalRetrievalAllowed, report.RuntimeSwitchAllowed, report.ReadyForRuntimeSwitch, report.PackingPolicyChanged, report.PackageOutputChanged);
        return builder.ToString();
    }

    private static IReadOnlyList<CapabilityReadinessMatrixEntry> BuildMatrix(string repositoryRoot)
    {
        return
        [
            Entry(repositoryRoot, "Foundation", "Foundation", "foundation/foundation-release-candidate-gate.json", ProjectStateAuditStatuses.Frozen),
            Entry(repositoryRoot, "ServiceFoundation", "Service", "service/service-foundation-freeze-gate.json", ProjectStateAuditStatuses.Frozen),
            Entry(repositoryRoot, "StorageFoundation", "Storage", "foundation/foundation-freeze-report.json", ProjectStateAuditStatuses.Frozen),
            Entry(repositoryRoot, "RelationGovernancePostgres", "Graph", "storage/postgres/postgres-relation-governance-readiness-gate.json", ProjectStateAuditStatuses.Ready),
            Entry(repositoryRoot, "LearningFeedbackPostgres", "Learning", "storage/postgres/postgres-learning-feedback-freeze-gate.json", ProjectStateAuditStatuses.Ready),
            Entry(repositoryRoot, "JobQueuePostgres", "Storage", "storage/postgres/postgres-job-queue-freeze-gate.json", ProjectStateAuditStatuses.Ready),
            Entry(repositoryRoot, "VectorPostgresProvider", "Vector", "storage/postgres/postgres-vector-freeze-gate.json", ProjectStateAuditStatuses.PreviewOnly),
            Entry(repositoryRoot, "VectorFormalPreview", "Vector", "vector/v4/vector-formal-preview-freeze-gate.json", ProjectStateAuditStatuses.PreviewOnly),
            Entry(repositoryRoot, "ScopedRuntimeExperiment", "Runtime Experiment", "vector/v4/runtime-experiment/promotion-decision.json", ProjectStateAuditStatuses.PreviewOnly),
            Entry(repositoryRoot, "FormalRetrievalIntegrationPlan", "Vector", "vector/v5/formal-retrieval-integration-plan-gate.json", ProjectStateAuditStatuses.PlanOnly),
            Entry(repositoryRoot, "RouterGuardedOptIn", "Router", "learning/router/router-guarded-optin-readiness-gate.json", ProjectStateAuditStatuses.PreviewOnly),
            Entry(repositoryRoot, "CandidateReranker", "Reranker", "eval/vector-retrieval-shadow-readiness-gate.json", ProjectStateAuditStatuses.PreviewOnly),
            Entry(repositoryRoot, "RuntimeChangeGate", "Learning", "learning/readiness/learning-runtime-change-readiness-gate.json", ProjectStateAuditStatuses.Ready),
            Entry(repositoryRoot, "InputDatasetV2", "Input", "vector/dataset-v2/generated/materialization-gate.json", ProjectStateAuditStatuses.Ready),
            Entry(repositoryRoot, "OutputPackageAssembly", "Output", "vector/v4/vector-shadow-package-comparison-gate.json", ProjectStateAuditStatuses.PreviewOnly)
        ];
    }

    private static CapabilityReadinessMatrixEntry Entry(
        string repositoryRoot,
        string capabilityId,
        string area,
        string relativePath,
        string successStatus)
    {
        var exists = File.Exists(Path.Combine(repositoryRoot, relativePath));
        var passed = exists && ReadAnyBoolean(repositoryRoot, relativePath, "FreezePassed", "GatePassed", "PlanPassed", "Passed", "RecheckPassed", "SmokePassed", "ExperimentPassed");
        var recommendation = exists
            ? ReadString(repositoryRoot, relativePath, "Recommendation", "PromotionDecision", "ContextCoreFoundation", "ServiceFoundation")
            : "MissingReport";
        var blocked = new List<string>();
        if (!exists)
        {
            blocked.Add("MissingReport");
        }
        else if (!passed && !string.Equals(successStatus, ProjectStateAuditStatuses.PreviewOnly, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("GateNotPassed");
        }

        var formalAllowed = ReadAnyBoolean(repositoryRoot, relativePath, "FormalRetrievalAllowed");
        var runtimeSwitchAllowed = ReadAnyBoolean(repositoryRoot, relativePath, "RuntimeSwitchAllowed");
        var readyForRuntime = ReadAnyBoolean(repositoryRoot, relativePath, "ReadyForRuntimeSwitch", "UseForRuntime");
        if (formalAllowed)
        {
            blocked.Add("FormalRetrievalAllowedUnexpected");
        }

        if (runtimeSwitchAllowed || readyForRuntime)
        {
            blocked.Add("RuntimeSwitchAllowedUnexpected");
        }

        return new CapabilityReadinessMatrixEntry
        {
            CapabilityId = capabilityId,
            Area = area,
            Status = !exists
                ? ProjectStateAuditStatuses.Unknown
                : blocked.Count == 0 || string.Equals(successStatus, ProjectStateAuditStatuses.PreviewOnly, StringComparison.OrdinalIgnoreCase)
                    ? successStatus
                    : ProjectStateAuditStatuses.Blocked,
            Recommendation = string.IsNullOrWhiteSpace(recommendation) ? successStatus : recommendation,
            SourceReportPath = relativePath,
            SourceReportExists = exists,
            ReadyForRuntime = readyForRuntime,
            FormalRetrievalAllowed = formalAllowed,
            RuntimeSwitchAllowed = runtimeSwitchAllowed,
            BlockedReasons = blocked
        };
    }

    private static IReadOnlyList<MainlineGapEntry> BuildMainlineGaps(IReadOnlyList<CapabilityReadinessMatrixEntry> matrix)
    {
        return
        [
            Gap("graph-recall-quality", "Graph", "High", "Graph recall, noise, and relation quality still need a mainline formal-candidate audit.", "Relation governance storage is frozen, but formal retrieval graph contribution is still not integrated.", "Run relation quality and noise audit before graph candidates can influence formal retrieval.", "must-do before formal retrieval"),
            Gap("vector-ranking-precision", "Vector", "High", "Vector recall and ranking are validated through preview/shadow gates but remain outside formal retrieval.", "V5 integration plan is PlanOnly and requires ShadowFormalRetrievalAdapter next.", "Build shadow adapter and compare vector candidates against package assembly without mutation.", "must-do before formal retrieval"),
            Gap("input-evidence-provenance", "Input", "High", "Input ingestion evidence, provenance, and lifecycle metadata remain the strongest formal-readiness dependency.", "Dataset V2 is materialized, while legacy candidate review showed evidence/source/provenance gaps.", "Require Dataset V2 metadata contract and backfill checks before formal adapter input use.", "must-do before formal retrieval"),
            Gap("output-package-policy", "Output", "High", "Output package assembly, token budget, and priority policy have not accepted formal vector changes.", "Shadow package comparison kept PackageOutputChanged=false and PackingPolicyChanged=false.", "Add formal adapter package comparison with explicit token budget and priority invariants.", "must-do before formal retrieval"),
            Gap("learning-training-readiness", "Learning", "Medium", "Learning feedback has approved-data surfaces but no runtime training or negative-sample promotion path.", "Runtime-change gate remains pass-only while runtime switch stays forbidden.", "Define approved feedback and negative sample shadow-training readiness before any learning-driven ranking switch.", "can-defer"),
            Gap("performance-runner-complexity", "Foundation", "Medium", "Phase report runners and artifact readers are duplicated across many gates.", $"Current audit matrix reads {matrix.Count} capability artifacts.", "Consolidate report reading and markdown helpers after the V5 adapter gate is stable.", "optimization later"),
            Gap("side-branch-artifact-cleanup", "Service", "Low", "Superseded side-branch reports and smoke artifacts should be archived after mainline freeze points are tagged.", "Foundation and service freezes already carry critical summaries.", "Prune or archive obsolete generated artifacts later without touching gate inputs.", "side-branch cleanup later")
        ];
    }

    private static MainlineGapEntry Gap(
        string id,
        string area,
        string severity,
        string summary,
        string evidence,
        string action,
        string bucket)
        => new()
        {
            GapId = id,
            Area = area,
            Severity = severity,
            Summary = summary,
            Evidence = evidence,
            RecommendedAction = action,
            Bucket = bucket
        };

    private static IReadOnlyList<string> BuildMainlineRisks()
        =>
        [
            "Formal retrieval must not be enabled from preview or experiment freeze reports alone.",
            "Graph relation quality can add noise if relation evidence is not audited at formal-candidate time.",
            "Vector ranking improvements can regress precision unless post-scoring risk gates remain final.",
            "Input lifecycle metadata gaps can block or misroute otherwise relevant candidates.",
            "Package assembly must preserve token budget, priority, and formal output invariants.",
            "Learning feedback is not yet a runtime training signal."
        ];

    private static IReadOnlyList<string> BuildPerformanceGaps()
        =>
        [
            "Shadow adapter candidate generation needs a bounded latency and allocation baseline.",
            "Repeated report-runner JSON readers and markdown builders increase maintenance cost.",
            "ControlRoom status aggregation can share a single capability artifact reader."
        ];

    private static IReadOnlyList<string> BuildRecommendedNextPhases()
        =>
        [
            "V5.1 ShadowFormalRetrievalAdapter Plan",
            "V5.2 Formal Adapter Shadow Package Comparison",
            "Graph Relation Quality and Noise Audit",
            "Input Evidence/Provenance Contract Enforcement",
            "Output Token Budget and Priority Policy Shadow Gate"
        ];

    private static bool ReadAnyBoolean(string repositoryRoot, string relativePath, params string[] propertyNames)
    {
        var root = TryReadRoot(repositoryRoot, relativePath);
        if (root is null)
        {
            return false;
        }

        foreach (var propertyName in propertyNames)
        {
            if (TryGetProperty(root, propertyName, out var value)
                && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False))
            {
                return value.GetBoolean();
            }
        }

        return false;
    }

    private static string ReadString(string repositoryRoot, string relativePath, params string[] propertyNames)
    {
        var root = TryReadRoot(repositoryRoot, relativePath);
        if (root is null)
        {
            return string.Empty;
        }

        foreach (var propertyName in propertyNames)
        {
            if (TryGetProperty(root, propertyName, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static Dictionary<string, JsonElement>? TryReadRoot(string repositoryRoot, string relativePath)
    {
        var path = Path.Combine(repositoryRoot, relativePath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetProperty(Dictionary<string, JsonElement> root, string propertyName, out JsonElement value)
    {
        foreach (var property in root)
        {
            if (string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static void AppendConclusion(StringBuilder builder, string status, string recommendation)
    {
        builder.AppendLine($"- CurrentOverallStatus: `{status}`");
        builder.AppendLine($"- Recommendation: `{recommendation}`");
    }

    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- none");
            return;
        }

        foreach (var value in values)
        {
            builder.AppendLine($"- {value}");
        }
    }

    private static void AppendBoundary(
        StringBuilder builder,
        bool formalRetrievalAllowed,
        bool runtimeSwitchAllowed,
        bool readyForRuntimeSwitch,
        bool packingPolicyChanged,
        bool packageOutputChanged)
    {
        builder.AppendLine();
        builder.AppendLine("## Boundary");
        builder.AppendLine($"- FormalRetrievalAllowed: `{formalRetrievalAllowed}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{runtimeSwitchAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{readyForRuntimeSwitch}`");
        builder.AppendLine($"- PackingPolicyChanged: `{packingPolicyChanged}`");
        builder.AppendLine($"- PackageOutputChanged: `{packageOutputChanged}`");
    }
}
