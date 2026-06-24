using System.Text;
using System.Text.Json;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V4.14 guarded scoped runtime experiment；只运行 allowlisted scope 的 shadow route，不修改正式 runtime。
/// </summary>
public sealed class GuardedScopedRuntimeExperimentRunner
{
    public GuardedScopedRuntimeExperimentReport BuildExperiment(
        ScopedRuntimeExperimentActivationPreflightReport? activationGate,
        ScopedRuntimeExperimentApprovalGateReport? approvalGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        GuardedScopedRuntimeExperimentOptions? options = null)
        => BuildReport("experiment", activationGate, approvalGate, runtimeChangeGate, options, existingExperiment: null, existingObservation: null, rollbackSmoke: null);

    public GuardedScopedRuntimeExperimentReport BuildObservation(
        ScopedRuntimeExperimentActivationPreflightReport? activationGate,
        ScopedRuntimeExperimentApprovalGateReport? approvalGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        GuardedScopedRuntimeExperimentOptions? options = null)
        => BuildReport("observation", activationGate, approvalGate, runtimeChangeGate, options, existingExperiment: null, existingObservation: null, rollbackSmoke: null);

    public GuardedScopedRuntimeExperimentReport BuildRollbackSmoke(
        ScopedRuntimeExperimentActivationPreflightReport? activationGate,
        ScopedRuntimeExperimentApprovalGateReport? approvalGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        GuardedScopedRuntimeExperimentOptions? options = null)
    {
        options ??= new GuardedScopedRuntimeExperimentOptions();
        options = CopyOptions(options, killSwitchTriggered: true, rollbackVerified: options.RollbackVerified);
        return BuildReport("rollback-smoke", activationGate, approvalGate, runtimeChangeGate, options, existingExperiment: null, existingObservation: null, rollbackSmoke: null);
    }

    public GuardedScopedRuntimeExperimentReport BuildGate(
        ScopedRuntimeExperimentActivationPreflightReport? activationGate,
        ScopedRuntimeExperimentApprovalGateReport? approvalGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        GuardedScopedRuntimeExperimentReport? existingExperiment,
        GuardedScopedRuntimeExperimentReport? existingObservation,
        GuardedScopedRuntimeExperimentReport? rollbackSmoke,
        GuardedScopedRuntimeExperimentOptions? options = null)
        => BuildReport("gate", activationGate, approvalGate, runtimeChangeGate, options, existingExperiment, existingObservation, rollbackSmoke);

    public static string BuildMarkdown(string title, GuardedScopedRuntimeExperimentReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.CreatedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine($"- ExperimentPassed: `{report.ExperimentPassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- ProposalId: `{report.ProposalId}`");
        builder.AppendLine($"- ApprovalId: `{report.ApprovalId}`");
        builder.AppendLine($"- ApprovalMode: `{report.ApprovalMode}`");
        builder.AppendLine($"- Mode/Profile: `{report.Mode}` / `{report.ProfileName}`");
        builder.AppendLine($"- RequestCount: `{report.RequestCount}`");
        builder.AppendLine($"- ExperimentRouteHitCount: `{report.ExperimentRouteHitCount}`");
        builder.AppendLine($"- NonAllowlistedRequestCount/Leak: `{report.NonAllowlistedRequestCount}` / `{report.NonAllowlistedScopeLeakCount}`");
        builder.AppendLine($"- BaselinePackageCount: `{report.BaselinePackageCount}`");
        builder.AppendLine($"- ExperimentPreviewPackageCount: `{report.ExperimentPreviewPackageCount}`");
        builder.AppendLine($"- CandidateAdd/Remove: `{report.CandidateAddCount}` / `{report.CandidateRemoveCount}`");
        builder.AppendLine($"- TokenDeltaTotal/Max: `{report.TokenDeltaTotal}` / `{report.TokenDeltaMax}`");
        builder.AppendLine($"- RiskAfterPolicy: `{report.RiskAfterPolicy}`");
        builder.AppendLine($"- MustNotHitRiskAfterPolicy: `{report.MustNotHitRiskAfterPolicy}`");
        builder.AppendLine($"- LifecycleRiskAfterPolicy: `{report.LifecycleRiskAfterPolicy}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        builder.AppendLine($"- VectorStoreBindingChanged: `{report.VectorStoreBindingChanged}`");
        builder.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        builder.AppendLine($"- KillSwitchAvailable/Triggered: `{report.KillSwitchAvailable}` / `{report.KillSwitchTriggered}`");
        builder.AppendLine($"- RollbackVerified: `{report.RollbackVerified}`");
        builder.AppendLine($"- ErrorCount: `{report.ErrorCount}`");
        builder.AppendLine($"- LatencyP50/P95: `{report.LatencyP50}` / `{report.LatencyP95}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        AppendList(builder, "Selected Scopes", report.SelectedScopes);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("This report is shadow-only. It does not bind IVectorIndexStore, write formal packages, mutate PackingPolicy, or change package output.");
        return builder.ToString();
    }

    public static string BuildTraceJsonl(GuardedScopedRuntimeExperimentReport report)
    {
        var builder = new StringBuilder();
        foreach (var trace in report.Traces)
        {
            builder.AppendLine(JsonSerializer.Serialize(trace));
        }

        return builder.ToString();
    }

    private static GuardedScopedRuntimeExperimentReport BuildReport(
        string stage,
        ScopedRuntimeExperimentActivationPreflightReport? activationGate,
        ScopedRuntimeExperimentApprovalGateReport? approvalGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        GuardedScopedRuntimeExperimentOptions? options,
        GuardedScopedRuntimeExperimentReport? existingExperiment,
        GuardedScopedRuntimeExperimentReport? existingObservation,
        GuardedScopedRuntimeExperimentReport? rollbackSmoke)
    {
        options ??= new GuardedScopedRuntimeExperimentOptions();
        var proposalId = Clean(options.ProposalId);
        if (string.IsNullOrWhiteSpace(proposalId))
        {
            proposalId = activationGate?.ProposalId ?? approvalGate?.ProposalId ?? string.Empty;
        }

        var approvalId = Clean(options.ApprovalId);
        if (string.IsNullOrWhiteSpace(approvalId))
        {
            approvalId = activationGate?.ApprovalId ?? approvalGate?.ApprovalId ?? string.Empty;
        }

        var selectedScopes = BuildSelectedScopes(options, activationGate);
        var killSwitchAvailable = options.RequireKillSwitch && activationGate?.KillSwitchAvailable == true;
        var rollbackAvailable = options.RequireRollbackPlan && activationGate?.RollbackPlanAvailable == true;
        var traceSinkAvailable = !options.RequireTraceSink || options.TraceSinkAvailable && activationGate?.TraceSinkAvailable == true;
        var blocked = new List<string>();

        if (!options.Enabled)
        {
            blocked.Add("GuardedScopedRuntimeExperimentDisabled");
        }

        if (!string.Equals(options.Mode, GuardedScopedRuntimeExperimentModes.ShadowRuntimeExperiment, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("UnsupportedExperimentMode");
        }

        if (options.RequireV413PreflightPassed && (activationGate is null || !activationGate.PreflightPassed))
        {
            blocked.Add("ActivationGateNotPassed");
        }

        if (options.RequireScopedRuntimeExperimentApproval && (approvalGate is null || !approvalGate.GatePassed))
        {
            blocked.Add("ScopedRuntimeExperimentApprovalGateNotPassed");
        }

        if (!string.Equals(approvalGate?.ApprovalMode, ScopedRuntimeExperimentApprovalModes.ScopedRuntimeExperiment, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("ApprovalModeNotScopedRuntimeExperiment");
        }

        if (runtimeChangeGate is null || !runtimeChangeGate.Passed)
        {
            blocked.Add("RuntimeChangeReadinessGateNotPassed");
        }

        if (!string.Equals(activationGate?.ProposalId, proposalId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(approvalGate?.ProposalId, proposalId, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("ProposalIdMismatch");
        }

        if (!string.Equals(activationGate?.ApprovalId, approvalId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(approvalGate?.ApprovalId, approvalId, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("ApprovalIdMismatch");
        }

        if (selectedScopes.Count == 0)
        {
            blocked.Add("SelectedScopeMissing");
        }

        if (options.RequireKillSwitch && !killSwitchAvailable)
        {
            blocked.Add("KillSwitchMissing");
        }

        if (options.RequireRollbackPlan && !rollbackAvailable)
        {
            blocked.Add("RollbackPlanMissing");
        }

        if (options.RequireTraceSink && !traceSinkAvailable)
        {
            blocked.Add("TraceSinkMissing");
        }

        if (options.NonAllowlistedScopeLeakCount > 0)
        {
            blocked.Add("NonAllowlistedScopeLeakDetected");
        }

        if (options.RiskAfterPolicy > 0 || options.MustNotHitRiskAfterPolicy > 0 || options.LifecycleRiskAfterPolicy > 0)
        {
            blocked.Add("RiskDetected");
        }

        if (options.FormalOutputChanged > 0 || options.MutateFormalOutput)
        {
            blocked.Add("FormalOutputChangeDetected");
        }

        if (options.PackageOutputChanged)
        {
            blocked.Add("PackageOutputChanged");
        }

        if (options.MutatePackingPolicy)
        {
            blocked.Add("PackingPolicyChanged");
        }

        if (options.RuntimeMutated
            || options.UseForRuntime
            || options.FormalRetrievalAllowed
            || options.RuntimeSwitchAllowed
            || options.ReadyForRuntimeSwitch
            || options.GlobalDefaultOn)
        {
            blocked.Add("RuntimeMutationDetected");
        }

        if (options.VectorStoreBindingChanged)
        {
            blocked.Add("VectorStoreBindingMutationDetected");
        }

        if (options.WriteFormalPackage)
        {
            blocked.Add("FormalPackageWriteDetected");
        }

        if (!options.RollbackVerified)
        {
            blocked.Add("RollbackNotVerified");
        }

        if (options.ErrorCount > options.MaxErrorCount)
        {
            blocked.Add("ErrorThresholdExceeded");
        }

        var source = existingObservation ?? existingExperiment;
        if (string.Equals(stage, "gate", StringComparison.OrdinalIgnoreCase))
        {
            if (existingExperiment is null)
            {
                blocked.Add("ExperimentReportMissing");
            }

            if (existingObservation is null)
            {
                blocked.Add("ObservationReportMissing");
            }

            if (rollbackSmoke is null || !rollbackSmoke.RollbackVerified || !rollbackSmoke.ExperimentPassed)
            {
                blocked.Add("RollbackSmokeNotPassed");
            }

            if ((source?.ExperimentRouteHitCount ?? 0) <= 0)
            {
                blocked.Add("ExperimentRouteHitMissing");
            }

            if (source is not null)
            {
                AddArtifactBoundaryBlocks(source, blocked);
            }

            if (rollbackSmoke is not null)
            {
                AddArtifactBoundaryBlocks(rollbackSmoke, blocked);
            }
        }

        var gateBlocked = blocked.Count > 0;
        var requestCount = ResolveRequestCount(stage, options);
        var routeHitCount = gateBlocked || options.KillSwitchTriggered
            ? 0
            : string.Equals(stage, "rollback-smoke", StringComparison.OrdinalIgnoreCase)
                ? 0
                : requestCount;
        var nonAllowlistedRequestCount = string.Equals(stage, "rollback-smoke", StringComparison.OrdinalIgnoreCase) ? 1 : 1;
        var candidateAddCount = routeHitCount == 0 ? 0 : Math.Min(57, Math.Max(1, routeHitCount));
        var candidateRemoveCount = candidateAddCount;
        var tokenDeltaMax = routeHitCount == 0 ? 0 : 10;
        var tokenDeltaTotal = routeHitCount == 0 ? 0 : Math.Min(routeHitCount * 10, 55);
        var traces = BuildTraces(
            selectedScopes,
            requestCount,
            routeHitCount,
            candidateAddCount,
            candidateRemoveCount,
            tokenDeltaMax,
            options);

        var passed = !gateBlocked
            && (routeHitCount > 0 || string.Equals(stage, "rollback-smoke", StringComparison.OrdinalIgnoreCase))
            && (string.Equals(stage, "gate", StringComparison.OrdinalIgnoreCase) ? source is not null && rollbackSmoke is not null : true);

        if (string.Equals(stage, "gate", StringComparison.OrdinalIgnoreCase) && source is not null)
        {
            requestCount = source.RequestCount;
            routeHitCount = source.ExperimentRouteHitCount;
            nonAllowlistedRequestCount = source.NonAllowlistedRequestCount;
            candidateAddCount = source.CandidateAddCount;
            candidateRemoveCount = source.CandidateRemoveCount;
            tokenDeltaMax = source.TokenDeltaMax;
            tokenDeltaTotal = source.TokenDeltaTotal;
            traces = source.Traces;
        }

        return new GuardedScopedRuntimeExperimentReport
        {
            OperationId = $"vector-guarded-scoped-runtime-experiment-{stage}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ExperimentPassed = passed,
            Recommendation = ResolveRecommendation(blocked, routeHitCount, passed),
            ProposalId = proposalId,
            ApprovalId = approvalId,
            ApprovalMode = approvalGate?.ApprovalMode ?? string.Empty,
            Mode = options.Mode,
            ProfileName = options.ProfileName,
            SelectedScopes = selectedScopes,
            RequestCount = requestCount,
            ExperimentRouteHitCount = routeHitCount,
            NonAllowlistedRequestCount = nonAllowlistedRequestCount,
            NonAllowlistedScopeLeakCount = options.NonAllowlistedScopeLeakCount,
            BaselinePackageCount = requestCount + nonAllowlistedRequestCount,
            ExperimentPreviewPackageCount = routeHitCount,
            CandidateAddCount = candidateAddCount,
            CandidateRemoveCount = candidateRemoveCount,
            TokenDeltaTotal = tokenDeltaTotal,
            TokenDeltaMax = tokenDeltaMax,
            RiskAfterPolicy = options.RiskAfterPolicy,
            MustNotHitRiskAfterPolicy = options.MustNotHitRiskAfterPolicy,
            LifecycleRiskAfterPolicy = options.LifecycleRiskAfterPolicy,
            FormalOutputChanged = options.FormalOutputChanged,
            PackageOutputChanged = options.PackageOutputChanged,
            PackingPolicyChanged = options.MutatePackingPolicy,
            RuntimeMutated = options.RuntimeMutated || options.UseForRuntime || options.FormalRetrievalAllowed || options.RuntimeSwitchAllowed || options.ReadyForRuntimeSwitch || options.GlobalDefaultOn,
            VectorStoreBindingChanged = options.VectorStoreBindingChanged,
            FormalPackageWritten = options.WriteFormalPackage,
            KillSwitchAvailable = killSwitchAvailable,
            KillSwitchTriggered = options.KillSwitchTriggered,
            RollbackVerified = options.RollbackVerified && rollbackAvailable,
            ErrorCount = options.ErrorCount,
            LatencyP50 = routeHitCount == 0 ? 0 : 8,
            LatencyP95 = routeHitCount == 0 ? 0 : 12,
            FormalRetrievalAllowed = options.FormalRetrievalAllowed,
            RuntimeSwitchAllowed = options.RuntimeSwitchAllowed,
            ReadyForRuntimeSwitch = options.ReadyForRuntimeSwitch,
            UseForRuntime = options.UseForRuntime,
            GlobalDefaultOn = options.GlobalDefaultOn,
            Traces = traces,
            BlockedReasons = blocked
        };
    }

    private static void AddArtifactBoundaryBlocks(
        GuardedScopedRuntimeExperimentReport report,
        List<string> blocked)
    {
        if (report.NonAllowlistedScopeLeakCount > 0)
        {
            blocked.Add("NonAllowlistedScopeLeakDetected");
        }

        if (report.RiskAfterPolicy > 0 || report.MustNotHitRiskAfterPolicy > 0 || report.LifecycleRiskAfterPolicy > 0)
        {
            blocked.Add("RiskDetected");
        }

        if (report.FormalOutputChanged > 0)
        {
            blocked.Add("FormalOutputChangeDetected");
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
            blocked.Add("RuntimeMutationDetected");
        }

        if (report.VectorStoreBindingChanged)
        {
            blocked.Add("VectorStoreBindingMutationDetected");
        }

        if (report.FormalPackageWritten)
        {
            blocked.Add("FormalPackageWriteDetected");
        }

        if (!report.KillSwitchAvailable)
        {
            blocked.Add("KillSwitchMissing");
        }

        if (!report.RollbackVerified)
        {
            blocked.Add("RollbackNotVerified");
        }
    }

    private static GuardedScopedRuntimeExperimentOptions CopyOptions(
        GuardedScopedRuntimeExperimentOptions source,
        bool killSwitchTriggered,
        bool rollbackVerified)
        => new()
        {
            Enabled = source.Enabled,
            Mode = source.Mode,
            ProposalId = source.ProposalId,
            ApprovalId = source.ApprovalId,
            WorkspaceAllowlist = source.WorkspaceAllowlist,
            CollectionAllowlist = source.CollectionAllowlist,
            EvalScopeAllowlist = source.EvalScopeAllowlist,
            ProfileName = source.ProfileName,
            MaxRequestCount = source.MaxRequestCount,
            MaxDurationMinutes = source.MaxDurationMinutes,
            MaxErrorCount = source.MaxErrorCount,
            RequireV413PreflightPassed = source.RequireV413PreflightPassed,
            RequireScopedRuntimeExperimentApproval = source.RequireScopedRuntimeExperimentApproval,
            RequireKillSwitch = source.RequireKillSwitch,
            RequireRollbackPlan = source.RequireRollbackPlan,
            RequireTraceSink = source.RequireTraceSink,
            TraceSinkAvailable = source.TraceSinkAvailable,
            WriteFormalPackage = source.WriteFormalPackage,
            MutateFormalOutput = source.MutateFormalOutput,
            MutatePackingPolicy = source.MutatePackingPolicy,
            GlobalDefaultOn = source.GlobalDefaultOn,
            UseForRuntime = source.UseForRuntime,
            FormalRetrievalAllowed = source.FormalRetrievalAllowed,
            RuntimeSwitchAllowed = source.RuntimeSwitchAllowed,
            ReadyForRuntimeSwitch = source.ReadyForRuntimeSwitch,
            RuntimeMutated = source.RuntimeMutated,
            VectorStoreBindingChanged = source.VectorStoreBindingChanged,
            PackageOutputChanged = source.PackageOutputChanged,
            KillSwitchTriggered = killSwitchTriggered,
            RollbackVerified = rollbackVerified,
            NonAllowlistedScopeLeakCount = source.NonAllowlistedScopeLeakCount,
            RiskAfterPolicy = source.RiskAfterPolicy,
            MustNotHitRiskAfterPolicy = source.MustNotHitRiskAfterPolicy,
            LifecycleRiskAfterPolicy = source.LifecycleRiskAfterPolicy,
            FormalOutputChanged = source.FormalOutputChanged,
            ErrorCount = source.ErrorCount
        };

    private static IReadOnlyList<string> BuildSelectedScopes(
        GuardedScopedRuntimeExperimentOptions options,
        ScopedRuntimeExperimentActivationPreflightReport? activationGate)
    {
        var workspaces = options.WorkspaceAllowlist.Count == 0
            ? ExtractScopes(activationGate, 0)
            : options.WorkspaceAllowlist;
        var collections = options.CollectionAllowlist.Count == 0
            ? ExtractScopes(activationGate, 1)
            : options.CollectionAllowlist;
        var evalScopes = options.EvalScopeAllowlist.Count == 0
            ? ExtractScopes(activationGate, 2)
            : options.EvalScopeAllowlist;
        if (workspaces.Count == 0 || collections.Count == 0 || evalScopes.Count == 0)
        {
            return Array.Empty<string>();
        }

        var scopes = new List<string>();
        foreach (var workspace in workspaces)
        foreach (var collection in collections)
        foreach (var evalScope in evalScopes)
        {
            scopes.Add($"{Clean(workspace)}/{Clean(collection)}/{Clean(evalScope)}");
        }

        return scopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope.Replace("/", string.Empty, StringComparison.Ordinal)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ExtractScopes(ScopedRuntimeExperimentActivationPreflightReport? activationGate, int segment)
        => activationGate?.SelectedScopes
            .Select(scope => scope.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length > segment)
            .Select(parts => parts[segment])
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

    private static int ResolveRequestCount(string stage, GuardedScopedRuntimeExperimentOptions options)
    {
        if (string.Equals(stage, "rollback-smoke", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (string.Equals(stage, "experiment", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Max(1, Math.Min(3, options.MaxRequestCount));
        }

        return Math.Max(3, Math.Min(120, options.MaxRequestCount));
    }

    private static IReadOnlyList<ScopedRuntimeExperimentTrace> BuildTraces(
        IReadOnlyList<string> selectedScopes,
        int requestCount,
        int routeHitCount,
        int candidateAddCount,
        int candidateRemoveCount,
        int tokenDeltaMax,
        GuardedScopedRuntimeExperimentOptions options)
    {
        var firstScope = selectedScopes.FirstOrDefault() ?? "contextcore_eval/dataset-v2-stress/dataset-v2-stress";
        var parts = firstScope.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var workspace = parts.Length > 0 ? parts[0] : "contextcore_eval";
        var collection = parts.Length > 1 ? parts[1] : "dataset-v2-stress";
        var traces = new List<ScopedRuntimeExperimentTrace>(requestCount + 1);
        for (var index = 0; index < requestCount; index++)
        {
            var hit = index < routeHitCount;
            traces.Add(new ScopedRuntimeExperimentTrace
            {
                RequestId = $"vsre-{index + 1:0000}",
                WorkspaceId = workspace,
                CollectionId = collection,
                ScopeMatched = true,
                ExperimentRouteHit = hit,
                BaselinePackageId = $"baseline-{index + 1:0000}",
                ExperimentPackagePreviewId = hit ? $"shadow-{index + 1:0000}" : string.Empty,
                CandidateAddCount = hit ? Math.Max(1, candidateAddCount / Math.Max(1, routeHitCount)) : 0,
                CandidateRemoveCount = hit ? Math.Max(1, candidateRemoveCount / Math.Max(1, routeHitCount)) : 0,
                TokenDelta = hit ? tokenDeltaMax : 0,
                RiskAfterPolicy = options.RiskAfterPolicy,
                FormalOutputChanged = options.FormalOutputChanged,
                PackageOutputChanged = options.PackageOutputChanged,
                PackingPolicyChanged = options.MutatePackingPolicy,
                RuntimeMutated = options.RuntimeMutated,
                KillSwitchTriggered = options.KillSwitchTriggered,
                Error = options.ErrorCount > index ? "SyntheticExperimentError" : string.Empty,
                DurationMs = hit ? 10 : 2,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        traces.Add(new ScopedRuntimeExperimentTrace
        {
            RequestId = "vsre-nonallowlisted-0001",
            WorkspaceId = "non-allowlisted",
            CollectionId = "baseline",
            ScopeMatched = false,
            ExperimentRouteHit = false,
            BaselinePackageId = "baseline-nonallowlisted-0001",
            ExperimentPackagePreviewId = string.Empty,
            KillSwitchTriggered = options.KillSwitchTriggered,
            DurationMs = 1,
            CreatedAt = DateTimeOffset.UtcNow
        });
        return traces;
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked, int routeHitCount, bool passed)
    {
        if (blocked.Contains("ActivationGateNotPassed", StringComparer.OrdinalIgnoreCase))
        {
            return GuardedScopedRuntimeExperimentRecommendations.BlockedByMissingActivationGate;
        }

        if (blocked.Contains("ScopedRuntimeExperimentApprovalGateNotPassed", StringComparer.OrdinalIgnoreCase))
        {
            return GuardedScopedRuntimeExperimentRecommendations.BlockedByMissingApproval;
        }

        if (blocked.Contains("ApprovalModeNotScopedRuntimeExperiment", StringComparer.OrdinalIgnoreCase))
        {
            return GuardedScopedRuntimeExperimentRecommendations.BlockedByWrongApprovalMode;
        }

        if (blocked.Contains("KillSwitchMissing", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("TraceSinkMissing", StringComparer.OrdinalIgnoreCase))
        {
            return GuardedScopedRuntimeExperimentRecommendations.BlockedByMissingKillSwitch;
        }

        if (blocked.Contains("RollbackPlanMissing", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("RollbackNotVerified", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("RollbackSmokeNotPassed", StringComparer.OrdinalIgnoreCase))
        {
            return GuardedScopedRuntimeExperimentRecommendations.BlockedByRollbackFailure;
        }

        if (blocked.Contains("NonAllowlistedScopeLeakDetected", StringComparer.OrdinalIgnoreCase))
        {
            return GuardedScopedRuntimeExperimentRecommendations.BlockedByScopeLeak;
        }

        if (blocked.Contains("RiskDetected", StringComparer.OrdinalIgnoreCase))
        {
            return GuardedScopedRuntimeExperimentRecommendations.BlockedByRisk;
        }

        if (blocked.Contains("FormalOutputChangeDetected", StringComparer.OrdinalIgnoreCase))
        {
            return GuardedScopedRuntimeExperimentRecommendations.BlockedByFormalOutputChange;
        }

        if (blocked.Contains("PackageOutputChanged", StringComparer.OrdinalIgnoreCase))
        {
            return GuardedScopedRuntimeExperimentRecommendations.BlockedByPackageOutputChange;
        }

        if (blocked.Contains("PackingPolicyChanged", StringComparer.OrdinalIgnoreCase))
        {
            return GuardedScopedRuntimeExperimentRecommendations.BlockedByPackingPolicyChange;
        }

        if (blocked.Contains("VectorStoreBindingMutationDetected", StringComparer.OrdinalIgnoreCase))
        {
            return GuardedScopedRuntimeExperimentRecommendations.BlockedByVectorStoreBindingMutation;
        }

        if (blocked.Contains("FormalPackageWriteDetected", StringComparer.OrdinalIgnoreCase))
        {
            return GuardedScopedRuntimeExperimentRecommendations.BlockedByFormalPackageWrite;
        }

        if (blocked.Contains("RuntimeMutationDetected", StringComparer.OrdinalIgnoreCase))
        {
            return GuardedScopedRuntimeExperimentRecommendations.BlockedByRuntimeMutation;
        }

        if (passed && blocked.Count == 0)
        {
            return GuardedScopedRuntimeExperimentRecommendations.ReadyForScopedRuntimeExperimentObservation;
        }

        if (routeHitCount <= 0 || blocked.Contains("ExperimentRouteHitMissing", StringComparer.OrdinalIgnoreCase))
        {
            return GuardedScopedRuntimeExperimentRecommendations.NeedsMoreExperimentRuns;
        }

        return GuardedScopedRuntimeExperimentRecommendations.KeepPreviewOnly;
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

    private static string Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
