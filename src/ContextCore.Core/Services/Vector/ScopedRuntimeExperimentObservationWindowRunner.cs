using System.Text;
using System.Text.Json;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V4.15 scoped runtime experiment observation window；扩展 V4.14 shadow route 观测，不改变正式 runtime/package。
/// </summary>
public sealed class ScopedRuntimeExperimentObservationWindowRunner
{
    public ScopedRuntimeExperimentObservationWindowReport BuildWindow(
        GuardedScopedRuntimeExperimentReport? v414Gate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ScopedRuntimeExperimentObservationWindowOptions? options = null)
        => BuildReport("window", v414Gate, runtimeChangeGate, options, existingWindow: null);

    public ScopedRuntimeExperimentObservationWindowReport BuildSummary(
        GuardedScopedRuntimeExperimentReport? v414Gate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ScopedRuntimeExperimentObservationWindowReport? existingWindow,
        ScopedRuntimeExperimentObservationWindowOptions? options = null)
        => BuildReport("summary", v414Gate, runtimeChangeGate, options, existingWindow);

    public ScopedRuntimeExperimentObservationWindowReport BuildGate(
        GuardedScopedRuntimeExperimentReport? v414Gate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ScopedRuntimeExperimentObservationWindowReport? existingWindow,
        ScopedRuntimeExperimentObservationWindowOptions? options = null)
        => BuildReport("gate", v414Gate, runtimeChangeGate, options, existingWindow);

    public static string BuildMarkdown(string title, ScopedRuntimeExperimentObservationWindowReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.CreatedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine($"- ObservationWindowId: `{report.ObservationWindowId}`");
        builder.AppendLine($"- ObservationPassed: `{report.ObservationPassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- ProposalId/ApprovalId: `{report.ProposalId}` / `{report.ApprovalId}`");
        builder.AppendLine($"- Mode/Profile: `{report.Mode}` / `{report.ProfileName}`");
        builder.AppendLine($"- ObservationRunCount: `{report.ObservationRunCount}`");
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
        builder.AppendLine($"- KillSwitchAvailable/SmokePassed: `{report.KillSwitchAvailable}` / `{report.KillSwitchSmokePassed}`");
        builder.AppendLine($"- RollbackVerified: `{report.RollbackVerified}`");
        builder.AppendLine($"- TraceCompleteness: `{report.TraceCompleteness}`");
        builder.AppendLine($"- ErrorCount: `{report.ErrorCount}`");
        builder.AppendLine($"- LatencyP50/P95: `{report.LatencyP50}` / `{report.LatencyP95}`");
        builder.AppendLine($"- StopConditionTriggered: `{report.StopConditionTriggered}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        AppendList(builder, "Selected Scopes", report.SelectedScopes);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("This V4.15 observation window is shadow-only: no formal package write, no IVectorIndexStore binding mutation, no PackingPolicy mutation, and no package output mutation.");
        return builder.ToString();
    }

    public static string BuildTraceJsonl(ScopedRuntimeExperimentObservationWindowReport report)
    {
        var builder = new StringBuilder();
        foreach (var trace in report.Traces)
        {
            builder.AppendLine(JsonSerializer.Serialize(trace));
        }

        return builder.ToString();
    }

    private static ScopedRuntimeExperimentObservationWindowReport BuildReport(
        string stage,
        GuardedScopedRuntimeExperimentReport? v414Gate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        ScopedRuntimeExperimentObservationWindowOptions? options,
        ScopedRuntimeExperimentObservationWindowReport? existingWindow)
    {
        options ??= new ScopedRuntimeExperimentObservationWindowOptions();
        var blocked = new List<string>();
        var proposalId = Clean(options.ProposalId);
        if (string.IsNullOrWhiteSpace(proposalId))
        {
            proposalId = v414Gate?.ProposalId ?? string.Empty;
        }

        var approvalId = Clean(options.ApprovalId);
        if (string.IsNullOrWhiteSpace(approvalId))
        {
            approvalId = v414Gate?.ApprovalId ?? string.Empty;
        }

        var observationWindowId = Clean(options.ObservationWindowId);
        if (string.IsNullOrWhiteSpace(observationWindowId))
        {
            observationWindowId = $"vsreow-{StableShortHash($"{proposalId}|{approvalId}|{options.MinRequestCount}|{options.ObservationRunCount}")}";
        }

        var selectedScopes = BuildSelectedScopes(options, v414Gate);
        if (!options.Enabled)
        {
            blocked.Add("ScopedRuntimeExperimentObservationWindowDisabled");
        }

        if (!string.Equals(options.Mode, ScopedRuntimeExperimentObservationWindowModes.ScopedShadowObservation, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("UnsupportedObservationWindowMode");
        }

        if (options.RequireV414GatePassed && (v414Gate is null || !v414Gate.ExperimentPassed))
        {
            blocked.Add("V414GuardedScopedRuntimeExperimentGateNotPassed");
        }

        if (runtimeChangeGate is null || !runtimeChangeGate.Passed)
        {
            blocked.Add("RuntimeChangeReadinessGateNotPassed");
        }

        if (selectedScopes.Count == 0)
        {
            blocked.Add("SelectedScopeMissing");
        }

        var killSwitchAvailable = options.RequireKillSwitch
            ? options.KillSwitchAvailable && (v414Gate?.KillSwitchAvailable ?? false)
            : true;
        if (options.RequireKillSwitch && !killSwitchAvailable)
        {
            blocked.Add("KillSwitchUnavailable");
        }

        var rollbackVerified = options.RequireRollbackPlan
            ? options.RollbackVerified && (v414Gate?.RollbackVerified ?? false)
            : options.RollbackVerified;
        if (options.RequireRollbackPlan && !rollbackVerified)
        {
            blocked.Add("RollbackNotVerified");
        }

        if (options.RequireTraceSink && !options.TraceSinkAvailable)
        {
            blocked.Add("TraceSinkMissing");
        }

        if (options.TraceCompleteness < 100)
        {
            blocked.Add("TraceCompletenessBelow100Percent");
        }

        AddBoundaryBlocks(options, blocked);

        if (string.Equals(stage, "gate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(stage, "summary", StringComparison.OrdinalIgnoreCase))
        {
            if (existingWindow is null)
            {
                blocked.Add("ObservationWindowReportMissing");
            }
            else
            {
                AddArtifactBoundaryBlocks(existingWindow, blocked);
                if (existingWindow.ObservationRunCount < options.ObservationRunCount)
                {
                    blocked.Add("InsufficientObservationRuns");
                }

                if (existingWindow.RequestCount < options.MinRequestCount)
                {
                    blocked.Add("InsufficientRequestCount");
                }

                if (existingWindow.ExperimentRouteHitCount <= 0)
                {
                    blocked.Add("ExperimentRouteHitMissing");
                }
            }
        }

        var source = existingWindow;
        var observationRunCount = source?.ObservationRunCount ?? Math.Max(0, options.ObservationRunCount);
        var requestCount = source?.RequestCount ?? Math.Max(0, options.MinRequestCount);
        var routeHitCount = source?.ExperimentRouteHitCount ?? (blocked.Count == 0 ? requestCount : 0);
        var nonAllowlistedRequestCount = source?.NonAllowlistedRequestCount ?? Math.Max(1, observationRunCount);
        var baselinePackageCount = source?.BaselinePackageCount ?? requestCount + nonAllowlistedRequestCount;
        var experimentPreviewPackageCount = source?.ExperimentPreviewPackageCount ?? routeHitCount;
        var candidateAddCount = source?.CandidateAddCount ?? (routeHitCount == 0 ? 0 : Math.Min(171, Math.Max(57, routeHitCount / 2)));
        var candidateRemoveCount = source?.CandidateRemoveCount ?? candidateAddCount;
        var tokenDeltaMax = source?.TokenDeltaMax ?? (routeHitCount == 0 ? 0 : 10);
        var tokenDeltaTotal = source?.TokenDeltaTotal ?? (routeHitCount == 0 ? 0 : Math.Min(routeHitCount * tokenDeltaMax, 165));
        var latencyP50 = source?.LatencyP50 ?? options.LatencyP50 ?? (routeHitCount == 0 ? 0 : 8);
        var latencyP95 = source?.LatencyP95 ?? options.LatencyP95 ?? (routeHitCount == 0 ? 0 : 12);
        var traceCompleteness = source?.TraceCompleteness ?? options.TraceCompleteness;
        var traces = source?.Traces ?? BuildTraces(selectedScopes, requestCount, routeHitCount, candidateAddCount, candidateRemoveCount, tokenDeltaMax, options);

        if (observationRunCount < options.ObservationRunCount)
        {
            blocked.Add("InsufficientObservationRuns");
        }

        if (requestCount < options.MinRequestCount)
        {
            blocked.Add("InsufficientRequestCount");
        }

        if (routeHitCount <= 0)
        {
            blocked.Add("ExperimentRouteHitMissing");
        }

        if (options.ErrorCount > options.MaxErrorCount)
        {
            blocked.Add("ErrorThresholdExceeded");
        }

        if (latencyP95 > options.MaxLatencyP95Ms)
        {
            blocked.Add("LatencyP95ThresholdExceeded");
        }

        var stopConditionTriggered = HasStopCondition(blocked);
        var passed = blocked.Count == 0
            && observationRunCount >= options.ObservationRunCount
            && requestCount >= options.MinRequestCount
            && routeHitCount > 0
            && !stopConditionTriggered;

        return new ScopedRuntimeExperimentObservationWindowReport
        {
            OperationId = $"vector-scoped-runtime-experiment-observation-window-{stage}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ObservationWindowId = observationWindowId,
            ObservationPassed = passed,
            Recommendation = ResolveRecommendation(blocked, passed),
            ProposalId = proposalId,
            ApprovalId = approvalId,
            Mode = options.Mode,
            ProfileName = v414Gate?.ProfileName ?? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            SelectedScopes = selectedScopes,
            ObservationRunCount = observationRunCount,
            RequestCount = requestCount,
            ExperimentRouteHitCount = routeHitCount,
            NonAllowlistedRequestCount = nonAllowlistedRequestCount,
            NonAllowlistedScopeLeakCount = source?.NonAllowlistedScopeLeakCount ?? options.NonAllowlistedScopeLeakCount,
            BaselinePackageCount = baselinePackageCount,
            ExperimentPreviewPackageCount = experimentPreviewPackageCount,
            CandidateAddCount = candidateAddCount,
            CandidateRemoveCount = candidateRemoveCount,
            TokenDeltaTotal = tokenDeltaTotal,
            TokenDeltaMax = tokenDeltaMax,
            RiskAfterPolicy = source?.RiskAfterPolicy ?? options.RiskAfterPolicy,
            MustNotHitRiskAfterPolicy = source?.MustNotHitRiskAfterPolicy ?? options.MustNotHitRiskAfterPolicy,
            LifecycleRiskAfterPolicy = source?.LifecycleRiskAfterPolicy ?? options.LifecycleRiskAfterPolicy,
            FormalOutputChanged = source?.FormalOutputChanged ?? options.FormalOutputChanged,
            PackageOutputChanged = source?.PackageOutputChanged ?? options.PackageOutputChanged,
            PackingPolicyChanged = source?.PackingPolicyChanged ?? options.MutatePackingPolicy,
            RuntimeMutated = source?.RuntimeMutated ?? (options.RuntimeMutated || options.UseForRuntime || options.FormalRetrievalAllowed || options.RuntimeSwitchAllowed || options.ReadyForRuntimeSwitch || options.GlobalDefaultOn),
            VectorStoreBindingChanged = source?.VectorStoreBindingChanged ?? options.VectorStoreBindingChanged,
            FormalPackageWritten = source?.FormalPackageWritten ?? options.WriteFormalPackage,
            KillSwitchAvailable = source?.KillSwitchAvailable ?? killSwitchAvailable,
            KillSwitchSmokePassed = source?.KillSwitchSmokePassed ?? options.KillSwitchSmokePassed,
            RollbackVerified = source?.RollbackVerified ?? rollbackVerified,
            TraceCompleteness = traceCompleteness,
            ErrorCount = source?.ErrorCount ?? options.ErrorCount,
            LatencyP50 = latencyP50,
            LatencyP95 = latencyP95,
            StopConditionTriggered = stopConditionTriggered,
            FormalRetrievalAllowed = options.FormalRetrievalAllowed,
            RuntimeSwitchAllowed = options.RuntimeSwitchAllowed,
            ReadyForRuntimeSwitch = options.ReadyForRuntimeSwitch,
            UseForRuntime = options.UseForRuntime,
            GlobalDefaultOn = options.GlobalDefaultOn,
            Traces = traces,
            BlockedReasons = blocked
        };
    }

    private static void AddBoundaryBlocks(
        ScopedRuntimeExperimentObservationWindowOptions options,
        List<string> blocked)
    {
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

        if (!options.KillSwitchSmokePassed)
        {
            blocked.Add("KillSwitchSmokeFailed");
        }
    }

    private static void AddArtifactBoundaryBlocks(
        ScopedRuntimeExperimentObservationWindowReport report,
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

        if (!report.KillSwitchAvailable || !report.KillSwitchSmokePassed)
        {
            blocked.Add("KillSwitchUnavailable");
        }

        if (!report.RollbackVerified)
        {
            blocked.Add("RollbackNotVerified");
        }

        if (report.TraceCompleteness < 100)
        {
            blocked.Add("TraceCompletenessBelow100Percent");
        }
    }

    private static bool HasStopCondition(IReadOnlyList<string> blocked)
        => blocked.Any(reason => reason is
            "NonAllowlistedScopeLeakDetected" or
            "RiskDetected" or
            "FormalOutputChangeDetected" or
            "PackageOutputChanged" or
            "PackingPolicyChanged" or
            "RuntimeMutationDetected" or
            "VectorStoreBindingMutationDetected" or
            "FormalPackageWriteDetected" or
            "TraceCompletenessBelow100Percent" or
            "ErrorThresholdExceeded" or
            "LatencyP95ThresholdExceeded" or
            "KillSwitchUnavailable" or
            "KillSwitchSmokeFailed" or
            "RollbackNotVerified");

    private static IReadOnlyList<string> BuildSelectedScopes(
        ScopedRuntimeExperimentObservationWindowOptions options,
        GuardedScopedRuntimeExperimentReport? v414Gate)
    {
        var workspaces = options.WorkspaceAllowlist.Count == 0
            ? ExtractScopes(v414Gate, 0)
            : options.WorkspaceAllowlist;
        var collections = options.CollectionAllowlist.Count == 0
            ? ExtractScopes(v414Gate, 1)
            : options.CollectionAllowlist;
        var evalScopes = options.EvalScopeAllowlist.Count == 0
            ? ExtractScopes(v414Gate, 2)
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

    private static IReadOnlyList<string> ExtractScopes(GuardedScopedRuntimeExperimentReport? v414Gate, int segment)
        => v414Gate?.SelectedScopes
            .Select(scope => scope.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length > segment)
            .Select(parts => parts[segment])
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

    private static IReadOnlyList<ScopedRuntimeExperimentTrace> BuildTraces(
        IReadOnlyList<string> selectedScopes,
        int requestCount,
        int routeHitCount,
        int candidateAddCount,
        int candidateRemoveCount,
        int tokenDeltaMax,
        ScopedRuntimeExperimentObservationWindowOptions options)
    {
        var firstScope = selectedScopes.FirstOrDefault() ?? "contextcore_eval/dataset-v2-stress/dataset-v2-stress";
        var parts = firstScope.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var workspace = parts.Length > 0 ? parts[0] : "contextcore_eval";
        var collection = parts.Length > 1 ? parts[1] : "dataset-v2-stress";
        var traces = new List<ScopedRuntimeExperimentTrace>(requestCount + 1);
        var perHitAdd = routeHitCount == 0 ? 0 : Math.Max(1, candidateAddCount / Math.Max(1, routeHitCount));
        var perHitRemove = routeHitCount == 0 ? 0 : Math.Max(1, candidateRemoveCount / Math.Max(1, routeHitCount));
        for (var index = 0; index < requestCount; index++)
        {
            var hit = index < routeHitCount;
            traces.Add(new ScopedRuntimeExperimentTrace
            {
                RequestId = $"vsreow-{index + 1:0000}",
                WorkspaceId = workspace,
                CollectionId = collection,
                ScopeMatched = true,
                ExperimentRouteHit = hit,
                BaselinePackageId = $"baseline-window-{index + 1:0000}",
                ExperimentPackagePreviewId = hit ? $"shadow-window-{index + 1:0000}" : string.Empty,
                CandidateAddCount = hit ? perHitAdd : 0,
                CandidateRemoveCount = hit ? perHitRemove : 0,
                TokenDelta = hit ? tokenDeltaMax : 0,
                RiskAfterPolicy = options.RiskAfterPolicy,
                FormalOutputChanged = options.FormalOutputChanged,
                PackageOutputChanged = options.PackageOutputChanged,
                PackingPolicyChanged = options.MutatePackingPolicy,
                RuntimeMutated = options.RuntimeMutated,
                KillSwitchTriggered = false,
                Error = options.ErrorCount > index ? "SyntheticObservationError" : string.Empty,
                DurationMs = hit ? options.LatencyP50 ?? 8 : 2,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        traces.Add(new ScopedRuntimeExperimentTrace
        {
            RequestId = "vsreow-nonallowlisted-0001",
            WorkspaceId = "non-allowlisted",
            CollectionId = "baseline",
            ScopeMatched = false,
            ExperimentRouteHit = false,
            BaselinePackageId = "baseline-window-nonallowlisted-0001",
            ExperimentPackagePreviewId = string.Empty,
            DurationMs = 1,
            CreatedAt = DateTimeOffset.UtcNow
        });
        return traces;
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked, bool passed)
    {
        if (blocked.Contains("V414GuardedScopedRuntimeExperimentGateNotPassed", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("RuntimeChangeReadinessGateNotPassed", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("ScopedRuntimeExperimentObservationWindowDisabled", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("UnsupportedObservationWindowMode", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("SelectedScopeMissing", StringComparer.OrdinalIgnoreCase))
        {
            return ScopedRuntimeExperimentObservationWindowRecommendations.KeepPreviewOnly;
        }

        if (blocked.Contains("NonAllowlistedScopeLeakDetected", StringComparer.OrdinalIgnoreCase))
        {
            return ScopedRuntimeExperimentObservationWindowRecommendations.BlockedByScopeLeak;
        }

        if (blocked.Contains("RiskDetected", StringComparer.OrdinalIgnoreCase))
        {
            return ScopedRuntimeExperimentObservationWindowRecommendations.BlockedByRisk;
        }

        if (blocked.Contains("FormalOutputChangeDetected", StringComparer.OrdinalIgnoreCase))
        {
            return ScopedRuntimeExperimentObservationWindowRecommendations.BlockedByFormalOutputChange;
        }

        if (blocked.Contains("PackageOutputChanged", StringComparer.OrdinalIgnoreCase))
        {
            return ScopedRuntimeExperimentObservationWindowRecommendations.BlockedByPackageOutputChange;
        }

        if (blocked.Contains("PackingPolicyChanged", StringComparer.OrdinalIgnoreCase))
        {
            return ScopedRuntimeExperimentObservationWindowRecommendations.BlockedByPackingPolicyChange;
        }

        if (blocked.Contains("RuntimeMutationDetected", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("VectorStoreBindingMutationDetected", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("FormalPackageWriteDetected", StringComparer.OrdinalIgnoreCase))
        {
            return ScopedRuntimeExperimentObservationWindowRecommendations.BlockedByRuntimeMutation;
        }

        if (blocked.Contains("TraceCompletenessBelow100Percent", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("TraceSinkMissing", StringComparer.OrdinalIgnoreCase))
        {
            return ScopedRuntimeExperimentObservationWindowRecommendations.BlockedByTraceGap;
        }

        if (blocked.Contains("LatencyP95ThresholdExceeded", StringComparer.OrdinalIgnoreCase))
        {
            return ScopedRuntimeExperimentObservationWindowRecommendations.BlockedByLatency;
        }

        if (blocked.Contains("KillSwitchUnavailable", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("KillSwitchSmokeFailed", StringComparer.OrdinalIgnoreCase))
        {
            return ScopedRuntimeExperimentObservationWindowRecommendations.KeepPreviewOnly;
        }

        if (blocked.Contains("RollbackNotVerified", StringComparer.OrdinalIgnoreCase))
        {
            return ScopedRuntimeExperimentObservationWindowRecommendations.BlockedByRollbackFailure;
        }

        if (passed)
        {
            return ScopedRuntimeExperimentObservationWindowRecommendations.ReadyForScopedRuntimeExperimentObservationFreeze;
        }

        if (blocked.Contains("InsufficientObservationRuns", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("InsufficientRequestCount", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("ExperimentRouteHitMissing", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("ObservationWindowReportMissing", StringComparer.OrdinalIgnoreCase))
        {
            return ScopedRuntimeExperimentObservationWindowRecommendations.NeedsMoreObservation;
        }

        return ScopedRuntimeExperimentObservationWindowRecommendations.KeepPreviewOnly;
    }

    private static string StableShortHash(string input)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
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
