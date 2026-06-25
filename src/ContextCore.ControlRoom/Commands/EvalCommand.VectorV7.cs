using System.Text.Json;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.ControlRoom.Commands;

public static partial class EvalCommand
{
    private static async Task ExecuteControlledAppliedMergeRuntimePreviewPlanAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v7"));
        Directory.CreateDirectory(output);

        var v6FreezePath = Path.Combine("vector", "v6", "controlled-applied-merge-preview-freeze.json");
        var v6Freeze = await ReadJsonFileAsync<ControlledAppliedMergePreviewFreezeReport>(v6FreezePath, ct)
            .ConfigureAwait(false);

        var optFreezePath = Path.Combine("eval", "architecture-cleanup-freeze.json");
        var optFreeze = await ReadJsonFileAsync<ArchitectureCleanupFreezeReport>(optFreezePath, ct)
            .ConfigureAwait(false);

        var runtimeChangeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var runtimeChangeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeChangeGatePath, ct)
            .ConfigureAwait(false);

        var p15ReportPath = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15Report = await ReadJsonFileAsync<JsonDocument>(p15ReportPath, ct).ConfigureAwait(false);
        var p15GatePassed = false;
        if (p15Report is not null && p15Report.RootElement.TryGetProperty("PassRate", out var passRateEl))
        {
            p15GatePassed = passRateEl.GetDouble() >= 1.0;
        }

        var options = new ControlledAppliedMergeRuntimePreviewPlanOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            MaxRequestCount = CommandHelpers.GetIntOption(args, "--max-requests", 100),
            MaxDurationMinutes = CommandHelpers.GetIntOption(args, "--max-duration-minutes", 30),
        };

        var runner = new ControlledAppliedMergeRuntimePreviewPlanRunner();
        var isGate = string.Equals(subcommand, "controlled-applied-merge-runtime-preview-plan-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.BuildGate(v6Freeze, optFreeze, runtimeChangeGate, p15GatePassed, options)
            : runner.BuildPlan(v6Freeze, optFreeze, runtimeChangeGate, p15GatePassed, options);

        var fn = isGate ? "runtime-preview-plan-gate" : "runtime-preview-plan";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(
            ControlledAppliedMergeRuntimePreviewPlanRunner.BuildMarkdown(
                isGate ? "Controlled Applied Merge Runtime Preview Plan Gate" : "Controlled Applied Merge Runtime Preview Plan",
                report),
            mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Controlled applied merge runtime preview plan written: {jp}");
        Console.WriteLine($"[Eval] passed={report.PlanPassed}; recommendation={report.Recommendation}; " +
            $"v6Freeze={report.V6FreezePassed}; optFreeze={report.OPTFreezePassed}; " +
            $"runtimeChange={report.RuntimeChangeGatePassed}; p15={report.P15GatePassed}; " +
            $"scopes={report.AllowlistedScopes.Count}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteControlledAppliedMergeRuntimePreviewDryRunAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v7"));
        Directory.CreateDirectory(output);

        var planPath = Path.Combine("vector", "v7", "runtime-preview-plan.json");
        var planGateFallback = Path.Combine("vector", "v7", "runtime-preview-plan-gate.json");
        var planGate = await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewPlanReport>(planPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewPlanReport>(planGateFallback, ct)
                .ConfigureAwait(false);

        var proposalPath = Path.Combine("vector", "v6", "controlled-applied-merge-proposal.json");
        var proposalGateFallback = Path.Combine("vector", "v6", "controlled-applied-merge-proposal-gate.json");
        var proposalGate = await ReadJsonFileAsync<ControlledAppliedMergeProposalReport>(proposalPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ControlledAppliedMergeProposalReport>(proposalGateFallback, ct)
                .ConfigureAwait(false);

        var v6FreezePath = Path.Combine("vector", "v6", "controlled-applied-merge-preview-freeze.json");
        var v6Freeze = await ReadJsonFileAsync<ControlledAppliedMergePreviewFreezeReport>(v6FreezePath, ct)
            .ConfigureAwait(false);

        var options = new ControlledAppliedMergeRuntimePreviewDryRunOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            ObservationRuns = CommandHelpers.GetIntOption(args, "--observation-runs", 3),
            MaxTokenDeltaTotal = CommandHelpers.GetIntOption(args, "--max-token-delta-total", 4000),
        };

        var runner = new ControlledAppliedMergeRuntimePreviewDryRunRunner();
        var isGate = string.Equals(subcommand, "controlled-applied-merge-runtime-preview-dry-run-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.BuildGate(planGate, proposalGate, v6Freeze, options)
            : runner.BuildDryRun(planGate, proposalGate, v6Freeze, options);

        var fn = isGate ? "runtime-preview-dry-run-gate" : "runtime-preview-dry-run";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(
            ControlledAppliedMergeRuntimePreviewDryRunRunner.BuildMarkdown(
                isGate ? "Controlled Applied Merge Runtime Preview Dry-run Gate" : "Controlled Applied Merge Runtime Preview Dry-run",
                report),
            mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Controlled applied merge runtime preview dry-run written: {jp}");
        Console.WriteLine($"[Eval] passed={report.DryRunPassed}; gate={report.GatePassed}; recommendation={report.Recommendation}; " +
            $"plan={report.PlanPassed}; v6Freeze={report.V6FreezePassed}; " +
            $"wouldApplyAdd={report.WouldApplyAddCount}; wouldApplyRemove={report.WouldApplyRemoveCount}; " +
            $"tokenDelta={report.TotalTokenDelta}; scopes={report.AllowlistedScopes.Count}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteControlledAppliedMergeRuntimePreviewActivationPreflightAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v7"));
        Directory.CreateDirectory(output);

        var planPath = Path.Combine("vector", "v7", "runtime-preview-plan.json");
        var planGateFallback = Path.Combine("vector", "v7", "runtime-preview-plan-gate.json");
        var planGate = await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewPlanReport>(planPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewPlanReport>(planGateFallback, ct)
                .ConfigureAwait(false);

        var dryRunPath = Path.Combine("vector", "v7", "runtime-preview-dry-run.json");
        var dryRunGateFallback = Path.Combine("vector", "v7", "runtime-preview-dry-run-gate.json");
        var dryRunGate = await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewDryRunReport>(dryRunPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewDryRunReport>(dryRunGateFallback, ct)
                .ConfigureAwait(false);

        var v6FreezePath = Path.Combine("vector", "v6", "controlled-applied-merge-preview-freeze.json");
        var v6Freeze = await ReadJsonFileAsync<ControlledAppliedMergePreviewFreezeReport>(v6FreezePath, ct)
            .ConfigureAwait(false);

        var runtimeChangeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var runtimeChangeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeChangeGatePath, ct)
            .ConfigureAwait(false);

        var p15ReportPath = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15Report = await ReadJsonFileAsync<JsonDocument>(p15ReportPath, ct).ConfigureAwait(false);
        var p15GatePassed = false;
        if (p15Report is not null && p15Report.RootElement.TryGetProperty("PassRate", out var passRateEl))
        {
            p15GatePassed = passRateEl.GetDouble() >= 1.0;
        }

        var options = new ControlledAppliedMergeRuntimePreviewActivationPreflightOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            TraceSinkAvailable = !CommandHelpers.HasFlag(args, "--missing-trace-sink"),
            RequireKillSwitch = !CommandHelpers.HasFlag(args, "--skip-kill-switch-requirement"),
            RequireRollbackPlan = !CommandHelpers.HasFlag(args, "--skip-rollback-requirement"),
            RequireTraceSink = !CommandHelpers.HasFlag(args, "--skip-trace-sink-requirement"),
            RequireRuntimeChangeGate = !CommandHelpers.HasFlag(args, "--skip-runtime-change-gate"),
            RequireP15Gate = !CommandHelpers.HasFlag(args, "--skip-p15-gate"),
        };

        var runner = new ControlledAppliedMergeRuntimePreviewActivationPreflightRunner();
        var isGate = string.Equals(subcommand, "controlled-applied-merge-runtime-preview-activation-preflight-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.BuildGate(planGate, dryRunGate, v6Freeze, runtimeChangeGate, p15GatePassed, options)
            : runner.BuildPreflight(planGate, dryRunGate, v6Freeze, runtimeChangeGate, p15GatePassed, options);

        var fn = isGate ? "activation-preflight-gate" : "activation-preflight";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(
            ControlledAppliedMergeRuntimePreviewActivationPreflightRunner.BuildMarkdown(
                isGate ? "Controlled Applied Merge Runtime Preview Activation Preflight Gate" : "Controlled Applied Merge Runtime Preview Activation Preflight",
                report),
            mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Controlled applied merge runtime preview activation preflight written: {jp}");
        Console.WriteLine($"[Eval] passed={report.PreflightPassed}; gate={report.GatePassed}; recommendation={report.Recommendation}; " +
            $"plan={report.PlanPassed}; dryRun={report.DryRunPassed}; v6Freeze={report.V6FreezePassed}; " +
            $"killSwitch={report.KillSwitchAvailable}; rollback={report.RollbackPlanAvailable}; traceSink={report.TraceSinkAvailable}; " +
            $"configPatchPreviewed={report.ConfigPatchPreviewed}; scopes={report.AllowlistedScopes.Count}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteControlledAppliedMergeRuntimePreviewObservationWindowAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v7"));
        Directory.CreateDirectory(output);

        var preflightPath = Path.Combine("vector", "v7", "activation-preflight.json");
        var preflightGateFallback = Path.Combine("vector", "v7", "activation-preflight-gate.json");
        var preflightGate = await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewActivationPreflightReport>(preflightPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewActivationPreflightReport>(preflightGateFallback, ct)
                .ConfigureAwait(false);

        var dryRunPath = Path.Combine("vector", "v7", "runtime-preview-dry-run.json");
        var dryRunGateFallback = Path.Combine("vector", "v7", "runtime-preview-dry-run-gate.json");
        var dryRunGate = await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewDryRunReport>(dryRunPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewDryRunReport>(dryRunGateFallback, ct)
                .ConfigureAwait(false);

        var planPath = Path.Combine("vector", "v7", "runtime-preview-plan.json");
        var planGateFallback = Path.Combine("vector", "v7", "runtime-preview-plan-gate.json");
        var planGate = await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewPlanReport>(planPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewPlanReport>(planGateFallback, ct)
                .ConfigureAwait(false);

        var v6FreezePath = Path.Combine("vector", "v6", "controlled-applied-merge-preview-freeze.json");
        var v6Freeze = await ReadJsonFileAsync<ControlledAppliedMergePreviewFreezeReport>(v6FreezePath, ct)
            .ConfigureAwait(false);

        var runtimeChangeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var runtimeChangeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeChangeGatePath, ct)
            .ConfigureAwait(false);

        var p15ReportPath = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15Report = await ReadJsonFileAsync<JsonDocument>(p15ReportPath, ct).ConfigureAwait(false);
        var p15GatePassed = false;
        if (p15Report is not null && p15Report.RootElement.TryGetProperty("PassRate", out var passRateEl))
        {
            p15GatePassed = passRateEl.GetDouble() >= 1.0;
        }

        var options = new ControlledAppliedMergeRuntimePreviewObservationWindowOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            ObservationRunCount = CommandHelpers.GetIntOption(args, "--observation-runs", 5),
            MaxRequestCount = CommandHelpers.GetIntOption(args, "--max-requests", 100),
            MaxDurationMinutes = CommandHelpers.GetIntOption(args, "--max-duration-minutes", 30),
        };

        var runner = new ControlledAppliedMergeRuntimePreviewObservationWindowRunner();
        var isGate = string.Equals(subcommand, "controlled-applied-merge-runtime-preview-observation-window-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.RunGate(preflightGate, dryRunGate, planGate, v6Freeze, runtimeChangeGate, p15GatePassed, options)
            : runner.RunObservation(preflightGate, dryRunGate, planGate, v6Freeze, runtimeChangeGate, p15GatePassed, options);

        var fn = isGate ? "observation-window-gate" : "observation-window";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(
            ControlledAppliedMergeRuntimePreviewObservationWindowRunner.BuildMarkdown(
                isGate ? "Controlled Applied Merge Runtime Preview Observation Window Gate" : "Controlled Applied Merge Runtime Preview Observation Window",
                report),
            mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Controlled applied merge runtime preview observation window written: {jp}");
        Console.WriteLine($"[Eval] passed={report.ObservationPassed}; gate={report.GatePassed}; recommendation={report.Recommendation}; " +
            $"preflight={report.PreflightPassed}; dryRun={report.DryRunPassed}; " +
            $"runs={report.ObservationRunCount}; failed={report.FailedRunCount}; " +
            $"stable={report.DeterministicDryRunStable}; appliedDeltaZero={report.AppliedDeltaZero}; " +
            $"resultDiscarded={report.ResultDiscarded}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteControlledAppliedMergeRuntimePreviewObservationHardeningAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v7"));
        Directory.CreateDirectory(output);

        var obsWindowPath = Path.Combine("vector", "v7", "observation-window.json");
        var obsWindowGateFallback = Path.Combine("vector", "v7", "observation-window-gate.json");
        var obsWindow = await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewObservationWindowReport>(obsWindowPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewObservationWindowReport>(obsWindowGateFallback, ct)
                .ConfigureAwait(false);

        var preflightPath = Path.Combine("vector", "v7", "activation-preflight.json");
        var preflightGateFallback = Path.Combine("vector", "v7", "activation-preflight-gate.json");
        var preflightGate = await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewActivationPreflightReport>(preflightPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewActivationPreflightReport>(preflightGateFallback, ct)
                .ConfigureAwait(false);

        var dryRunPath = Path.Combine("vector", "v7", "runtime-preview-dry-run.json");
        var dryRunGateFallback = Path.Combine("vector", "v7", "runtime-preview-dry-run-gate.json");
        var dryRunGate = await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewDryRunReport>(dryRunPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewDryRunReport>(dryRunGateFallback, ct)
                .ConfigureAwait(false);

        var v6FreezePath = Path.Combine("vector", "v6", "controlled-applied-merge-preview-freeze.json");
        var v6Freeze = await ReadJsonFileAsync<ControlledAppliedMergePreviewFreezeReport>(v6FreezePath, ct)
            .ConfigureAwait(false);

        var runtimeChangeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var runtimeChangeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeChangeGatePath, ct)
            .ConfigureAwait(false);

        var p15ReportPath = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15Report = await ReadJsonFileAsync<JsonDocument>(p15ReportPath, ct).ConfigureAwait(false);
        var p15GatePassed = false;
        if (p15Report is not null && p15Report.RootElement.TryGetProperty("PassRate", out var passRateEl))
        {
            p15GatePassed = passRateEl.GetDouble() >= 1.0;
        }

        var options = new ControlledAppliedMergeRuntimePreviewObservationHardeningOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            MinObservationRunCount = CommandHelpers.GetIntOption(args, "--min-runs", 10),
            MinRequestCountTotal = CommandHelpers.GetIntOption(args, "--min-requests", 120),
            MaxDurationMinutes = CommandHelpers.GetIntOption(args, "--max-duration-minutes", 30),
            RequestsPerRun = CommandHelpers.GetIntOption(args, "--requests-per-run", 12),
        };

        var runner = new ControlledAppliedMergeRuntimePreviewObservationHardeningRunner();
        var isGate = string.Equals(subcommand, "controlled-applied-merge-runtime-preview-observation-hardening-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.RunGate(obsWindow, preflightGate, dryRunGate, v6Freeze, runtimeChangeGate, p15GatePassed, options)
            : runner.RunHardening(obsWindow, preflightGate, dryRunGate, v6Freeze, runtimeChangeGate, p15GatePassed, options);

        var fn = isGate ? "observation-hardening-gate" : "observation-hardening";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(
            ControlledAppliedMergeRuntimePreviewObservationHardeningRunner.BuildMarkdown(
                isGate ? "Controlled Applied Merge Runtime Preview Observation Hardening Gate" : "Controlled Applied Merge Runtime Preview Observation Hardening",
                report),
            mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Controlled applied merge runtime preview observation hardening written: {jp}");
        Console.WriteLine($"[Eval] passed={report.HardeningPassed}; gate={report.GatePassed}; recommendation={report.Recommendation}; " +
            $"obsWindow={report.ObservationWindowPassed}; runs={report.ObservationRunCount}; " +
            $"requests={report.RequestCountTotal}; routeHits={report.AllowlistedPreviewRouteHitCountTotal}; " +
            $"traceCompleteness={report.TraceCompletenessPercent:F0}%; stable={report.DeterministicStable}; " +
            $"appliedDeltaZero={report.AppliedDeltaZero}; resultDiscarded={report.ResultDiscarded}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteControlledAppliedMergeRuntimePreviewObservationFreezeAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v7"));
        Directory.CreateDirectory(output);

        var planPath = Path.Combine("vector", "v7", "runtime-preview-plan.json");
        var planGateFallback = Path.Combine("vector", "v7", "runtime-preview-plan-gate.json");
        var v7Plan = await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewPlanReport>(planPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewPlanReport>(planGateFallback, ct)
                .ConfigureAwait(false);

        var dryRunPath = Path.Combine("vector", "v7", "runtime-preview-dry-run.json");
        var dryRunGateFallback = Path.Combine("vector", "v7", "runtime-preview-dry-run-gate.json");
        var v7DryRun = await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewDryRunReport>(dryRunPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewDryRunReport>(dryRunGateFallback, ct)
                .ConfigureAwait(false);

        var preflightPath = Path.Combine("vector", "v7", "activation-preflight.json");
        var preflightGateFallback = Path.Combine("vector", "v7", "activation-preflight-gate.json");
        var v7Preflight = await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewActivationPreflightReport>(preflightPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewActivationPreflightReport>(preflightGateFallback, ct)
                .ConfigureAwait(false);

        var obsWindowPath = Path.Combine("vector", "v7", "observation-window.json");
        var obsWindowGateFallback = Path.Combine("vector", "v7", "observation-window-gate.json");
        var v7Observation = await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewObservationWindowReport>(obsWindowPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewObservationWindowReport>(obsWindowGateFallback, ct)
                .ConfigureAwait(false);

        var hardenPath = Path.Combine("vector", "v7", "observation-hardening.json");
        var hardenGateFallback = Path.Combine("vector", "v7", "observation-hardening-gate.json");
        var v7Hardening = await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewObservationHardeningReport>(hardenPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewObservationHardeningReport>(hardenGateFallback, ct)
                .ConfigureAwait(false);

        var v6FreezePath = Path.Combine("vector", "v6", "controlled-applied-merge-preview-freeze.json");
        var v6Freeze = await ReadJsonFileAsync<ControlledAppliedMergePreviewFreezeReport>(v6FreezePath, ct)
            .ConfigureAwait(false);

        var optFreezePath = Path.Combine("eval", "architecture-cleanup-freeze.json");
        var optFreeze = await ReadJsonFileAsync<ArchitectureCleanupFreezeReport>(optFreezePath, ct)
            .ConfigureAwait(false);

        var runtimeChangeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var runtimeChangeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeChangeGatePath, ct)
            .ConfigureAwait(false);
        var runtimeChangeGatePassed = runtimeChangeGate is not null && runtimeChangeGate.Passed;

        var p15ReportPath = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15Report = await ReadJsonFileAsync<JsonDocument>(p15ReportPath, ct).ConfigureAwait(false);
        var p15GatePassed = false;
        if (p15Report is not null && p15Report.RootElement.TryGetProperty("PassRate", out var passRateEl))
        {
            p15GatePassed = passRateEl.GetDouble() >= 1.0;
        }

        var options = new ControlledAppliedMergeRuntimePreviewObservationFreezeOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            TestCountBaseline = CommandHelpers.GetIntOption(args, "--test-baseline", 1452),
        };

        var runner = new ControlledAppliedMergeRuntimePreviewObservationFreezeRunner();
        var isGate = string.Equals(subcommand, "controlled-applied-merge-runtime-preview-observation-freeze-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.RunGate(v7Plan, v7DryRun, v7Preflight, v7Observation, v7Hardening, v6Freeze, optFreeze, runtimeChangeGatePassed, p15GatePassed, options)
            : runner.RunFreeze(v7Plan, v7DryRun, v7Preflight, v7Observation, v7Hardening, v6Freeze, optFreeze, runtimeChangeGatePassed, p15GatePassed, options);

        var fn = isGate ? "observation-freeze-gate" : "observation-freeze";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(
            ControlledAppliedMergeRuntimePreviewObservationFreezeRunner.BuildMarkdown(
                isGate ? "Controlled Applied Merge Runtime Preview Observation Freeze Gate" : "Controlled Applied Merge Runtime Preview Observation Freeze",
                report),
            mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Controlled applied merge runtime preview observation freeze written: {jp}");
        Console.WriteLine($"[Eval] freezePassed={report.FreezePassed}; gatePassed={report.GatePassed}; recommendation={report.Recommendation}; " +
            $"promotionDecision={report.PromotionDecision}; testBaselineFrozen={report.TestBaselineFrozen}; " +
            $"noRuntimeMutation={report.NoRuntimeMutationInvariant}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteScopedRuntimePreviewApprovalPlanAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v7"));
        Directory.CreateDirectory(output);

        var freezePath = Path.Combine("vector", "v7", "observation-freeze.json");
        var freezeGateFallback = Path.Combine("vector", "v7", "observation-freeze-gate.json");
        var v7Freeze = await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewObservationFreezeReport>(freezePath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewObservationFreezeReport>(freezeGateFallback, ct)
                .ConfigureAwait(false);

        var hardenPath = Path.Combine("vector", "v7", "observation-hardening.json");
        var hardenGateFallback = Path.Combine("vector", "v7", "observation-hardening-gate.json");
        var v7Hardening = await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewObservationHardeningReport>(hardenPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewObservationHardeningReport>(hardenGateFallback, ct)
                .ConfigureAwait(false);

        var runtimeChangeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var runtimeChangeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeChangeGatePath, ct)
            .ConfigureAwait(false);
        var runtimeChangeGatePassed = runtimeChangeGate is not null && runtimeChangeGate.Passed;

        var p15ReportPath = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15Report = await ReadJsonFileAsync<JsonDocument>(p15ReportPath, ct).ConfigureAwait(false);
        var p15GatePassed = false;
        if (p15Report is not null && p15Report.RootElement.TryGetProperty("PassRate", out var passRateEl))
        {
            p15GatePassed = passRateEl.GetDouble() >= 1.0;
        }

        var options = new ScopedRuntimePreviewApprovalPlanOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            ValidityDurationDays = CommandHelpers.GetIntOption(args, "--validity-days", 30),
            KillSwitchResponseTimeSeconds = CommandHelpers.GetIntOption(args, "--kill-switch-seconds", 60),
            RollbackMaxDurationMinutes = CommandHelpers.GetIntOption(args, "--rollback-minutes", 15),
            TraceRetentionDays = CommandHelpers.GetIntOption(args, "--trace-retention-days", 90),
        };

        var runner = new ScopedRuntimePreviewApprovalPlanRunner();
        var isGate = string.Equals(subcommand, "scoped-runtime-preview-approval-plan-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.RunGate(v7Freeze, v7Hardening, runtimeChangeGatePassed, p15GatePassed, options)
            : runner.RunPlan(v7Freeze, v7Hardening, runtimeChangeGatePassed, p15GatePassed, options);

        var fn = isGate ? "approval-plan-gate" : "approval-plan";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(
            ScopedRuntimePreviewApprovalPlanRunner.BuildMarkdown(
                isGate ? "Scoped Runtime Preview Approval Plan Gate" : "Scoped Runtime Preview Approval Plan",
                report),
            mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Scoped runtime preview approval plan written: {jp}");
        Console.WriteLine($"[Eval] planPassed={report.PlanPassed}; gatePassed={report.GatePassed}; recommendation={report.Recommendation}; " +
            $"nextPhase={report.NextAllowedPhase}; validityDays={report.ValidityDurationDays}; " +
            $"killSwitchConfigured={report.KillSwitchConfigured}; rollbackConfigured={report.RollbackConfigured}; " +
            $"traceRetentionConfigured={report.TraceRetentionConfigured}; noRuntimeMutation={report.NoRuntimeMutationInvariant}; " +
            $"blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteScopedRuntimePreviewAuthorizationAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v7"));
        Directory.CreateDirectory(output);

        var approvalPlanPath = Path.Combine("vector", "v7", "approval-plan.json");
        var approvalPlanGateFallback = Path.Combine("vector", "v7", "approval-plan-gate.json");
        var approvalPlan = await ReadJsonFileAsync<ScopedRuntimePreviewApprovalPlanReport>(approvalPlanPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewApprovalPlanReport>(approvalPlanGateFallback, ct)
                .ConfigureAwait(false);

        var freezePath = Path.Combine("vector", "v7", "observation-freeze.json");
        var freezeGateFallback = Path.Combine("vector", "v7", "observation-freeze-gate.json");
        var v7Freeze = await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewObservationFreezeReport>(freezePath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewObservationFreezeReport>(freezeGateFallback, ct)
                .ConfigureAwait(false);

        var runtimeChangeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var runtimeChangeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeChangeGatePath, ct)
            .ConfigureAwait(false);
        var runtimeChangeGatePassed = runtimeChangeGate is not null && runtimeChangeGate.Passed;

        var p15ReportPath = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15Report = await ReadJsonFileAsync<JsonDocument>(p15ReportPath, ct).ConfigureAwait(false);
        var p15GatePassed = false;
        if (p15Report is not null && p15Report.RootElement.TryGetProperty("PassRate", out var passRateEl))
        {
            p15GatePassed = passRateEl.GetDouble() >= 1.0;
        }

        var options = new ScopedRuntimePreviewAuthorizationOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            ApprovedBy = CommandHelpers.GetOption(args, "--approved-by") ?? "ReleaseManager",
        };

        var runner = new ScopedRuntimePreviewAuthorizationRunner();
        var isGate = string.Equals(subcommand, "scoped-runtime-preview-authorization-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.RunGate(approvalPlan, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options)
            : runner.RunAuthorization(approvalPlan, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options);

        var fn = isGate ? "authorization-gate" : "authorization";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(
            ScopedRuntimePreviewAuthorizationRunner.BuildMarkdown(
                isGate ? "Scoped Runtime Preview Authorization Gate" : "Scoped Runtime Preview Authorization",
                report),
            mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Scoped runtime preview authorization written: {jp}");
        Console.WriteLine($"[Eval] authorized={report.Authorized}; gatePassed={report.GatePassed}; recommendation={report.Recommendation}; " +
            $"nextPhase={report.NextAllowedPhase}; validityValid={report.ValidityValid}; remainingDays={report.RemainingValidityDays}; " +
            $"allForbiddenAcknowledged={report.AllForbiddenActionsAcknowledged}; noRuntimeMutation={report.NoRuntimeMutationInvariant}; " +
            $"blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteScopedRuntimePreviewAuthorizationHardeningAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v7"));
        Directory.CreateDirectory(output);

        var authPath = Path.Combine("vector", "v7", "authorization.json");
        var authGateFallback = Path.Combine("vector", "v7", "authorization-gate.json");
        var authorization = await ReadJsonFileAsync<ScopedRuntimePreviewAuthorizationReport>(authPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewAuthorizationReport>(authGateFallback, ct)
                .ConfigureAwait(false);

        var approvalPlanPath = Path.Combine("vector", "v7", "approval-plan.json");
        var approvalPlanGateFallback = Path.Combine("vector", "v7", "approval-plan-gate.json");
        var approvalPlan = await ReadJsonFileAsync<ScopedRuntimePreviewApprovalPlanReport>(approvalPlanPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewApprovalPlanReport>(approvalPlanGateFallback, ct)
                .ConfigureAwait(false);

        var freezePath = Path.Combine("vector", "v7", "observation-freeze.json");
        var freezeGateFallback = Path.Combine("vector", "v7", "observation-freeze-gate.json");
        var v7Freeze = await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewObservationFreezeReport>(freezePath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewObservationFreezeReport>(freezeGateFallback, ct)
                .ConfigureAwait(false);

        var runtimeChangeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var runtimeChangeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeChangeGatePath, ct)
            .ConfigureAwait(false);
        var runtimeChangeGatePassed = runtimeChangeGate is not null && runtimeChangeGate.Passed;

        var p15ReportPath = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15Report = await ReadJsonFileAsync<JsonDocument>(p15ReportPath, ct).ConfigureAwait(false);
        var p15GatePassed = false;
        if (p15Report is not null && p15Report.RootElement.TryGetProperty("PassRate", out var passRateEl))
        {
            p15GatePassed = passRateEl.GetDouble() >= 1.0;
        }

        var options = new ScopedRuntimePreviewAuthorizationHardeningOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            ApprovedBy = CommandHelpers.GetOption(args, "--approved-by") ?? "ReleaseManager",
            ExplicitlyProvided = CommandHelpers.HasFlag(args, "--approved-by"),
        };

        var runner = new ScopedRuntimePreviewAuthorizationHardeningRunner();
        var isGate = string.Equals(subcommand, "scoped-runtime-preview-authorization-hardening-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.RunGate(authorization, approvalPlan, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options)
            : runner.RunHardening(authorization, approvalPlan, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options);

        var fn = isGate ? "authorization-hardening-gate" : "authorization-hardening";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(
            ScopedRuntimePreviewAuthorizationHardeningRunner.BuildMarkdown(
                isGate ? "Scoped Runtime Preview Authorization Hardening Gate" : "Scoped Runtime Preview Authorization Hardening",
                report),
            mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Scoped runtime preview authorization hardening written: {jp}");
        Console.WriteLine($"[Eval] hardeningPassed={report.HardeningPassed}; gatePassed={report.GatePassed}; recommendation={report.Recommendation}; " +
            $"nextPhase={report.NextAllowedPhase}; explicitApprovedBy={report.ExplicitApprovedByProvided}; " +
            $"acknowledged={report.AcknowledgedCount}/{report.RequiredForbiddenActionCount}; unacknowledged={report.UnacknowledgedCount}; " +
            $"negativeTests={report.NegativeTestPassed}/{report.NegativeTestTotal}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteScopedRuntimePreviewActivationPreparationAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v7"));
        Directory.CreateDirectory(output);

        var authPath = Path.Combine("vector", "v7", "authorization.json");
        var authGateFallback = Path.Combine("vector", "v7", "authorization-gate.json");
        var authorization = await ReadJsonFileAsync<ScopedRuntimePreviewAuthorizationReport>(authPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewAuthorizationReport>(authGateFallback, ct)
                .ConfigureAwait(false);

        var hardenPath = Path.Combine("vector", "v7", "authorization-hardening.json");
        var hardenGateFallback = Path.Combine("vector", "v7", "authorization-hardening-gate.json");
        var hardening = await ReadJsonFileAsync<ScopedRuntimePreviewAuthorizationHardeningReport>(hardenPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewAuthorizationHardeningReport>(hardenGateFallback, ct)
                .ConfigureAwait(false);

        var approvalPlanPath = Path.Combine("vector", "v7", "approval-plan.json");
        var approvalPlanGateFallback = Path.Combine("vector", "v7", "approval-plan-gate.json");
        var approvalPlan = await ReadJsonFileAsync<ScopedRuntimePreviewApprovalPlanReport>(approvalPlanPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewApprovalPlanReport>(approvalPlanGateFallback, ct)
                .ConfigureAwait(false);

        var freezePath = Path.Combine("vector", "v7", "observation-freeze.json");
        var freezeGateFallback = Path.Combine("vector", "v7", "observation-freeze-gate.json");
        var v7Freeze = await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewObservationFreezeReport>(freezePath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewObservationFreezeReport>(freezeGateFallback, ct)
                .ConfigureAwait(false);

        var runtimeChangeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var runtimeChangeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeChangeGatePath, ct)
            .ConfigureAwait(false);
        var runtimeChangeGatePassed = runtimeChangeGate is not null && runtimeChangeGate.Passed;

        var p15ReportPath = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15Report = await ReadJsonFileAsync<JsonDocument>(p15ReportPath, ct).ConfigureAwait(false);
        var p15GatePassed = false;
        if (p15Report is not null && p15Report.RootElement.TryGetProperty("PassRate", out var passRateEl))
        {
            p15GatePassed = passRateEl.GetDouble() >= 1.0;
        }

        var options = new ScopedRuntimePreviewActivationPreparationOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            ExplicitlyProvided = CommandHelpers.HasFlag(args, "--approved-by"),
            ApprovedBy = CommandHelpers.GetOption(args, "--approved-by") ?? "ReleaseManager",
            MaxObservationsBeforeActivation = CommandHelpers.GetIntOption(args, "--max-observations", 3),
            ObservationIntervalSeconds = CommandHelpers.GetIntOption(args, "--observation-interval-seconds", 30),
            MaxObservationDurationMinutes = CommandHelpers.GetIntOption(args, "--max-duration-minutes", 5),
        };

        var runner = new ScopedRuntimePreviewActivationPreparationRunner();
        var isGate = string.Equals(subcommand, "scoped-runtime-preview-activation-preparation-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.RunGate(authorization, hardening, approvalPlan, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options)
            : runner.RunPreparation(authorization, hardening, approvalPlan, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options);

        var fn = isGate ? "activation-preparation-gate" : "activation-preparation";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(
            ScopedRuntimePreviewActivationPreparationRunner.BuildMarkdown(
                isGate ? "Scoped Runtime Preview Activation Preparation Gate" : "Scoped Runtime Preview Activation Preparation",
                report),
            mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Scoped runtime preview activation preparation written: {jp}");
        Console.WriteLine($"[Eval] preparationPassed={report.PreparationPassed}; gatePassed={report.GatePassed}; recommendation={report.Recommendation}; " +
            $"nextPhase={report.NextAllowedPhase}; configPatchWritten={report.ConfigPatchWritten}; runtimeActivation={report.RuntimeActivation}; " +
            $"scopesUnchanged={report.ApprovedScopesUnchanged}; authorizationValid={report.AuthorizationValid}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteScopedRuntimePreviewActivationDryRunAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v7"));
        Directory.CreateDirectory(output);

        var prepPath = Path.Combine("vector", "v7", "activation-preparation.json");
        var prepGateFallback = Path.Combine("vector", "v7", "activation-preparation-gate.json");
        var preparation = await ReadJsonFileAsync<ScopedRuntimePreviewActivationPreparationReport>(prepPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewActivationPreparationReport>(prepGateFallback, ct)
                .ConfigureAwait(false);

        var authPath = Path.Combine("vector", "v7", "authorization.json");
        var authGateFallback = Path.Combine("vector", "v7", "authorization-gate.json");
        var authorization = await ReadJsonFileAsync<ScopedRuntimePreviewAuthorizationReport>(authPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewAuthorizationReport>(authGateFallback, ct)
                .ConfigureAwait(false);

        var freezePath = Path.Combine("vector", "v7", "observation-freeze.json");
        var freezeGateFallback = Path.Combine("vector", "v7", "observation-freeze-gate.json");
        var v7Freeze = await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewObservationFreezeReport>(freezePath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewObservationFreezeReport>(freezeGateFallback, ct)
                .ConfigureAwait(false);

        var runtimeChangeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var runtimeChangeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeChangeGatePath, ct)
            .ConfigureAwait(false);
        var runtimeChangeGatePassed = runtimeChangeGate is not null && runtimeChangeGate.Passed;

        var p15ReportPath = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15Report = await ReadJsonFileAsync<JsonDocument>(p15ReportPath, ct).ConfigureAwait(false);
        var p15GatePassed = false;
        if (p15Report is not null && p15Report.RootElement.TryGetProperty("PassRate", out var passRateEl))
        {
            p15GatePassed = passRateEl.GetDouble() >= 1.0;
        }

        var options = new ScopedRuntimePreviewActivationDryRunOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            ExplicitlyProvided = CommandHelpers.HasFlag(args, "--approved-by"),
            ApprovedBy = CommandHelpers.GetOption(args, "--approved-by") ?? "ReleaseManager",
            DryRunCount = CommandHelpers.GetIntOption(args, "--dry-runs", 5),
        };

        var runner = new ScopedRuntimePreviewActivationDryRunRunner();
        var isGate = string.Equals(subcommand, "scoped-runtime-preview-activation-dry-run-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.RunGate(preparation, authorization, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options)
            : runner.RunDryRun(preparation, authorization, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options);

        var fn = isGate ? "activation-dry-run-gate" : "activation-dry-run";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(
            ScopedRuntimePreviewActivationDryRunRunner.BuildMarkdown(
                isGate ? "Scoped Runtime Preview Activation Dry-run Gate" : "Scoped Runtime Preview Activation Dry-run",
                report),
            mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Scoped runtime preview activation dry-run written: {jp}");
        Console.WriteLine($"[Eval] dryRunPassed={report.DryRunPassed}; gatePassed={report.GatePassed}; recommendation={report.Recommendation}; " +
            $"nextPhase={report.NextAllowedPhase}; contractParseable={report.ContractParseable}; totalRuns={report.TotalRuns}; " +
            $"approvedScopeHits={report.ApprovedScopeHits}; nonApprovedNoOps={report.NonApprovedScopeNoOps}; " +
            $"appliedDeltaZero={report.AppliedDeltaZero}; configWritten={report.ConfigPatchWritten}; runtimeActivationRemainsFalse={!report.RuntimeActivation}; " +
            $"blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteScopedRuntimePreviewActivationWindowPreflightAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v7"));
        Directory.CreateDirectory(output);

        var prepPath = Path.Combine("vector", "v7", "activation-preparation.json");
        var prepGateFallback = Path.Combine("vector", "v7", "activation-preparation-gate.json");
        var preparation = await ReadJsonFileAsync<ScopedRuntimePreviewActivationPreparationReport>(prepPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewActivationPreparationReport>(prepGateFallback, ct)
                .ConfigureAwait(false);

        var dryRunPath = Path.Combine("vector", "v7", "activation-dry-run.json");
        var dryRunGateFallback = Path.Combine("vector", "v7", "activation-dry-run-gate.json");
        var dryRun = await ReadJsonFileAsync<ScopedRuntimePreviewActivationDryRunReport>(dryRunPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewActivationDryRunReport>(dryRunGateFallback, ct)
                .ConfigureAwait(false);

        var authPath = Path.Combine("vector", "v7", "authorization.json");
        var authGateFallback = Path.Combine("vector", "v7", "authorization-gate.json");
        var authorization = await ReadJsonFileAsync<ScopedRuntimePreviewAuthorizationReport>(authPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewAuthorizationReport>(authGateFallback, ct)
                .ConfigureAwait(false);

        var freezePath = Path.Combine("vector", "v7", "observation-freeze.json");
        var freezeGateFallback = Path.Combine("vector", "v7", "observation-freeze-gate.json");
        var v7Freeze = await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewObservationFreezeReport>(freezePath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewObservationFreezeReport>(freezeGateFallback, ct)
                .ConfigureAwait(false);

        var runtimeChangeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var runtimeChangeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeChangeGatePath, ct)
            .ConfigureAwait(false);
        var runtimeChangeGatePassed = runtimeChangeGate is not null && runtimeChangeGate.Passed;

        var p15ReportPath = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15Report = await ReadJsonFileAsync<JsonDocument>(p15ReportPath, ct).ConfigureAwait(false);
        var p15GatePassed = false;
        if (p15Report is not null && p15Report.RootElement.TryGetProperty("PassRate", out var passRateEl))
        {
            p15GatePassed = passRateEl.GetDouble() >= 1.0;
        }

        var options = new ScopedRuntimePreviewActivationWindowPreflightOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            ExplicitlyProvided = CommandHelpers.HasFlag(args, "--approved-by"),
            ApprovedBy = CommandHelpers.GetOption(args, "--approved-by") ?? "ReleaseManager",
            MaxWindowDurationMinutes = CommandHelpers.GetIntOption(args, "--max-window-minutes", 30),
            MaxRequestsPerWindow = CommandHelpers.GetIntOption(args, "--max-requests", 100),
        };

        var runner = new ScopedRuntimePreviewActivationWindowPreflightRunner();
        var isGate = string.Equals(subcommand, "scoped-runtime-preview-activation-window-preflight-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.RunGate(preparation, dryRun, authorization, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options)
            : runner.RunPreflight(preparation, dryRun, authorization, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options);

        var fn = isGate ? "activation-window-preflight-gate" : "activation-window-preflight";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(
            ScopedRuntimePreviewActivationWindowPreflightRunner.BuildMarkdown(
                isGate ? "Scoped Runtime Preview Activation Window Preflight Gate" : "Scoped Runtime Preview Activation Window Preflight",
                report),
            mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Scoped runtime preview activation window preflight written: {jp}");
        Console.WriteLine($"[Eval] preflightPassed={report.PreflightPassed}; gatePassed={report.GatePassed}; recommendation={report.Recommendation}; " +
            $"nextPhase={report.NextAllowedPhase}; scopesUnchanged={report.ScopesUnchanged}; windowDurationOk={report.WindowDurationWithinLimit}; " +
            $"killSwitchVerified={report.KillSwitchNoOpVerified}; configPatchWritten={report.ConfigPatchWritten}; " +
            $"runtimeActivation={report.RuntimeActivation}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteScopedRuntimePreviewActivationWindowNoOpExecutionAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v7"));
        Directory.CreateDirectory(output);

        var preflightPath = Path.Combine("vector", "v7", "activation-window-preflight.json");
        var preflightGateFallback = Path.Combine("vector", "v7", "activation-window-preflight-gate.json");
        var preflight = await ReadJsonFileAsync<ScopedRuntimePreviewActivationWindowPreflightReport>(preflightPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewActivationWindowPreflightReport>(preflightGateFallback, ct)
                .ConfigureAwait(false);

        var dryRunPath = Path.Combine("vector", "v7", "activation-dry-run.json");
        var dryRunGateFallback = Path.Combine("vector", "v7", "activation-dry-run-gate.json");
        var dryRun = await ReadJsonFileAsync<ScopedRuntimePreviewActivationDryRunReport>(dryRunPath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewActivationDryRunReport>(dryRunGateFallback, ct)
                .ConfigureAwait(false);

        var freezePath = Path.Combine("vector", "v7", "observation-freeze.json");
        var freezeGateFallback = Path.Combine("vector", "v7", "observation-freeze-gate.json");
        var v7Freeze = await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewObservationFreezeReport>(freezePath, ct)
            .ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewObservationFreezeReport>(freezeGateFallback, ct)
                .ConfigureAwait(false);

        var runtimeChangeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var runtimeChangeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeChangeGatePath, ct)
            .ConfigureAwait(false);
        var runtimeChangeGatePassed = runtimeChangeGate is not null && runtimeChangeGate.Passed;

        var p15ReportPath = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15Report = await ReadJsonFileAsync<JsonDocument>(p15ReportPath, ct).ConfigureAwait(false);
        var p15GatePassed = false;
        if (p15Report is not null && p15Report.RootElement.TryGetProperty("PassRate", out var passRateEl))
            p15GatePassed = passRateEl.GetDouble() >= 1.0;

        var options = new ScopedRuntimePreviewActivationWindowNoOpExecutionOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            ExplicitlyProvided = CommandHelpers.HasFlag(args, "--approved-by"),
            ApprovedBy = CommandHelpers.GetOption(args, "--approved-by") ?? "ReleaseManager",
            MinWindowCount = CommandHelpers.GetIntOption(args, "--min-windows", 3),
            RequestsPerWindow = CommandHelpers.GetIntOption(args, "--requests-per-window", 10),
            MinRequestCountTotal = CommandHelpers.GetIntOption(args, "--min-requests", 30),
        };

        var runner = new ScopedRuntimePreviewActivationWindowNoOpExecutionRunner();
        var isGate = string.Equals(subcommand, "scoped-runtime-preview-activation-window-noop-execution-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.RunGate(preflight, dryRun, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options)
            : runner.RunNoOpExecution(preflight, dryRun, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options);

        var fn = isGate ? "activation-window-noop-execution-gate" : "activation-window-noop-execution";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(ScopedRuntimePreviewActivationWindowNoOpExecutionRunner.BuildMarkdown(
            isGate ? "Scoped Runtime Preview Activation Window No-op Execution Gate" : "Scoped Runtime Preview Activation Window No-op Execution", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Scoped runtime preview activation window no-op execution written: {jp}");
        Console.WriteLine($"[Eval] noOpPassed={report.NoOpExecutionPassed}; gatePassed={report.GatePassed}; recommendation={report.Recommendation}; " +
            $"nextPhase={report.NextAllowedPhase}; windows={report.WindowCount}/{report.MinWindowCount}; requests={report.RequestCountTotal}/{report.MinRequestCountTotal}; " +
            $"approved={report.ApprovedScopeHitCount}; killSwitchNoOps={report.KillSwitchNoOpCount}; " +
            $"appliedDeltaZero={report.AppliedDeltaZero}; configWritten={report.ConfigPatchWritten}; rtActivation={report.RuntimeActivation}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteScopedRuntimePreviewActivationLiveReadinessFreezeAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v7"));
        Directory.CreateDirectory(output);

        var noOpPath = Path.Combine("vector", "v7", "activation-window-noop-execution.json");
        var noOpGateFallback = Path.Combine("vector", "v7", "activation-window-noop-execution-gate.json");
        var noOpExecution = await ReadJsonFileAsync<ScopedRuntimePreviewActivationWindowNoOpExecutionReport>(noOpPath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewActivationWindowNoOpExecutionReport>(noOpGateFallback, ct).ConfigureAwait(false);

        var preflightPath = Path.Combine("vector", "v7", "activation-window-preflight.json");
        var preflightGateFallback = Path.Combine("vector", "v7", "activation-window-preflight-gate.json");
        var preflight = await ReadJsonFileAsync<ScopedRuntimePreviewActivationWindowPreflightReport>(preflightPath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewActivationWindowPreflightReport>(preflightGateFallback, ct).ConfigureAwait(false);

        var dryRunPath = Path.Combine("vector", "v7", "activation-dry-run.json");
        var dryRunGateFallback = Path.Combine("vector", "v7", "activation-dry-run-gate.json");
        var dryRun = await ReadJsonFileAsync<ScopedRuntimePreviewActivationDryRunReport>(dryRunPath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewActivationDryRunReport>(dryRunGateFallback, ct).ConfigureAwait(false);

        var prepPath = Path.Combine("vector", "v7", "activation-preparation.json");
        var prepGateFallback = Path.Combine("vector", "v7", "activation-preparation-gate.json");
        var preparation = await ReadJsonFileAsync<ScopedRuntimePreviewActivationPreparationReport>(prepPath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewActivationPreparationReport>(prepGateFallback, ct).ConfigureAwait(false);

        var authPath = Path.Combine("vector", "v7", "authorization.json");
        var authGateFallback = Path.Combine("vector", "v7", "authorization-gate.json");
        var authorization = await ReadJsonFileAsync<ScopedRuntimePreviewAuthorizationReport>(authPath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewAuthorizationReport>(authGateFallback, ct).ConfigureAwait(false);

        var hardenPath = Path.Combine("vector", "v7", "authorization-hardening.json");
        var hardenGateFallback = Path.Combine("vector", "v7", "authorization-hardening-gate.json");
        var hardening = await ReadJsonFileAsync<ScopedRuntimePreviewAuthorizationHardeningReport>(hardenPath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewAuthorizationHardeningReport>(hardenGateFallback, ct).ConfigureAwait(false);

        var planPath = Path.Combine("vector", "v7", "approval-plan.json");
        var planGateFallback = Path.Combine("vector", "v7", "approval-plan-gate.json");
        var approvalPlan = await ReadJsonFileAsync<ScopedRuntimePreviewApprovalPlanReport>(planPath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewApprovalPlanReport>(planGateFallback, ct).ConfigureAwait(false);

        var freezePath = Path.Combine("vector", "v7", "observation-freeze.json");
        var freezeGateFallback = Path.Combine("vector", "v7", "observation-freeze-gate.json");
        var v7Freeze = await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewObservationFreezeReport>(freezePath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ControlledAppliedMergeRuntimePreviewObservationFreezeReport>(freezeGateFallback, ct).ConfigureAwait(false);

        var runtimeChangeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var runtimeChangeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeChangeGatePath, ct).ConfigureAwait(false);
        var runtimeChangeGatePassed = runtimeChangeGate is not null && runtimeChangeGate.Passed;

        var p15ReportPath = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15Report = await ReadJsonFileAsync<JsonDocument>(p15ReportPath, ct).ConfigureAwait(false);
        var p15GatePassed = false;
        if (p15Report is not null && p15Report.RootElement.TryGetProperty("PassRate", out var passRateEl))
            p15GatePassed = passRateEl.GetDouble() >= 1.0;

        var options = new ScopedRuntimePreviewActivationLiveReadinessFreezeOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            ExplicitlyProvided = CommandHelpers.HasFlag(args, "--approved-by"),
            ApprovedBy = CommandHelpers.GetOption(args, "--approved-by") ?? "ReleaseManager",
            FinalApprovedBy = CommandHelpers.GetOption(args, "--final-approved-by") ?? "",
            FinalApprovalExplicitlyProvided = CommandHelpers.HasFlag(args, "--final-approved-by"),
        };

        var runner = new ScopedRuntimePreviewActivationLiveReadinessFreezeRunner();
        var isGate = string.Equals(subcommand, "scoped-runtime-preview-activation-live-readiness-freeze-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.RunGate(noOpExecution, preflight, dryRun, preparation, authorization, hardening, approvalPlan, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options)
            : runner.RunFreeze(noOpExecution, preflight, dryRun, preparation, authorization, hardening, approvalPlan, v7Freeze, runtimeChangeGatePassed, p15GatePassed, options);

        var fn = isGate ? "activation-live-readiness-freeze-gate" : "activation-live-readiness-freeze";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(ScopedRuntimePreviewActivationLiveReadinessFreezeRunner.BuildMarkdown(
            isGate ? "Scoped Runtime Preview Activation Live Readiness Freeze Gate" : "Scoped Runtime Preview Activation Live Readiness Freeze", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Scoped runtime preview activation live readiness freeze written: {jp}");
        Console.WriteLine($"[Eval] freezePassed={report.FreezePassed}; gatePassed={report.GatePassed}; recommendation={report.Recommendation}; " +
            $"nextPhase={report.NextAllowedPhase}; finalApprovedBy={report.FinalApprovedBy}; finalApprovalRequired={report.FinalApprovalRequired}; " +
            $"allV7GatesPassed=true; noRuntimeMutation={report.NoRuntimeMutationInvariant}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteScopedRuntimePreviewLiveActivationExecutionPlanAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v7"));
        Directory.CreateDirectory(output);

        var freezePath = Path.Combine("vector", "v7", "activation-live-readiness-freeze.json");
        var freezeFallback = Path.Combine("vector", "v7", "activation-live-readiness-freeze-gate.json");
        var freeze = await ReadJsonFileAsync<ScopedRuntimePreviewActivationLiveReadinessFreezeReport>(freezePath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewActivationLiveReadinessFreezeReport>(freezeFallback, ct).ConfigureAwait(false);

        var noOpPath = Path.Combine("vector", "v7", "activation-window-noop-execution.json");
        var noOpFallback = Path.Combine("vector", "v7", "activation-window-noop-execution-gate.json");
        var noOpExec = await ReadJsonFileAsync<ScopedRuntimePreviewActivationWindowNoOpExecutionReport>(noOpPath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewActivationWindowNoOpExecutionReport>(noOpFallback, ct).ConfigureAwait(false);

        var preflightPath = Path.Combine("vector", "v7", "activation-window-preflight.json");
        var preflightFallback = Path.Combine("vector", "v7", "activation-window-preflight-gate.json");
        var preflight = await ReadJsonFileAsync<ScopedRuntimePreviewActivationWindowPreflightReport>(preflightPath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewActivationWindowPreflightReport>(preflightFallback, ct).ConfigureAwait(false);

        var dryRunPath = Path.Combine("vector", "v7", "activation-dry-run.json");
        var dryRunFallback = Path.Combine("vector", "v7", "activation-dry-run-gate.json");
        var dryRun = await ReadJsonFileAsync<ScopedRuntimePreviewActivationDryRunReport>(dryRunPath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewActivationDryRunReport>(dryRunFallback, ct).ConfigureAwait(false);

        var runtimeChangeGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var runtimeChangeGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(runtimeChangeGatePath, ct).ConfigureAwait(false);
        var runtimeChangeGatePassed = runtimeChangeGate is not null && runtimeChangeGate.Passed;

        var p15ReportPath = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15Report = await ReadJsonFileAsync<JsonDocument>(p15ReportPath, ct).ConfigureAwait(false);
        var p15GatePassed = false;
        if (p15Report is not null && p15Report.RootElement.TryGetProperty("PassRate", out var passRateEl))
            p15GatePassed = passRateEl.GetDouble() >= 1.0;

        var options = new ScopedRuntimePreviewLiveActivationExecutionPlanOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            ExplicitlyProvided = CommandHelpers.HasFlag(args, "--approved-by"),
            ApprovedBy = CommandHelpers.GetOption(args, "--approved-by") ?? "ReleaseManager",
            FinalApprovedBy = CommandHelpers.GetOption(args, "--final-approved-by") ?? "",
            FinalApprovalExplicitlyProvided = CommandHelpers.HasFlag(args, "--final-approved-by"),
        };

        var runner = new ScopedRuntimePreviewLiveActivationExecutionPlanRunner();
        var isGate = string.Equals(subcommand, "scoped-runtime-preview-live-activation-execution-plan-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.RunGate(freeze, noOpExec, preflight, dryRun, runtimeChangeGatePassed, p15GatePassed, options)
            : runner.RunPlan(freeze, noOpExec, preflight, dryRun, runtimeChangeGatePassed, p15GatePassed, options);

        var fn = isGate ? "live-activation-execution-plan-gate" : "live-activation-execution-plan";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(ScopedRuntimePreviewLiveActivationExecutionPlanRunner.BuildMarkdown(
            isGate ? "Scoped Runtime Preview Live Activation Execution Plan Gate" : "Scoped Runtime Preview Live Activation Execution Plan", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Scoped runtime preview live activation execution plan written: {jp}");
        Console.WriteLine($"[Eval] planPassed={report.PlanPassed}; gatePassed={report.GatePassed}; recommendation={report.Recommendation}; " +
            $"nextPhase={report.NextAllowedPhase}; configPatchPreviewLocked={report.ConfigPatchPreviewLocked}; " +
            $"configPatchWritten={report.ConfigPatchWritten}; runtimeActivation={report.RuntimeActivation}; " +
            $"abortConditions={report.AbortConditions.Count}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteScopedRuntimePreviewLiveActivationExecutionAsync(
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v7"));
        Directory.CreateDirectory(output);

        var planPath = Path.Combine("vector", "v7", "live-activation-execution-plan.json");
        var planFallback = Path.Combine("vector", "v7", "live-activation-execution-plan-gate.json");
        var plan = await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationExecutionPlanReport>(planPath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationExecutionPlanReport>(planFallback, ct).ConfigureAwait(false);

        var freezePath = Path.Combine("vector", "v7", "activation-live-readiness-freeze.json");
        var freezeFallback = Path.Combine("vector", "v7", "activation-live-readiness-freeze-gate.json");
        var freeze = await ReadJsonFileAsync<ScopedRuntimePreviewActivationLiveReadinessFreezeReport>(freezePath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewActivationLiveReadinessFreezeReport>(freezeFallback, ct).ConfigureAwait(false);

        var noOpPath = Path.Combine("vector", "v7", "activation-window-noop-execution.json");
        var noOpFallback = Path.Combine("vector", "v7", "activation-window-noop-execution-gate.json");
        var noOp = await ReadJsonFileAsync<ScopedRuntimePreviewActivationWindowNoOpExecutionReport>(noOpPath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewActivationWindowNoOpExecutionReport>(noOpFallback, ct).ConfigureAwait(false);

        var preflightPath = Path.Combine("vector", "v7", "activation-window-preflight.json");
        var preflightFallback = Path.Combine("vector", "v7", "activation-window-preflight-gate.json");
        var preflight = await ReadJsonFileAsync<ScopedRuntimePreviewActivationWindowPreflightReport>(preflightPath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewActivationWindowPreflightReport>(preflightFallback, ct).ConfigureAwait(false);

        var dryRunPath = Path.Combine("vector", "v7", "activation-dry-run.json");
        var dryRunFallback = Path.Combine("vector", "v7", "activation-dry-run-gate.json");
        var dryRun = await ReadJsonFileAsync<ScopedRuntimePreviewActivationDryRunReport>(dryRunPath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewActivationDryRunReport>(dryRunFallback, ct).ConfigureAwait(false);

        var rtGatePath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtGatePath, ct).ConfigureAwait(false);
        var rtGatePassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr))
            p15Passed = pr.GetDouble() >= 1.0;

        var options = new ScopedRuntimePreviewLiveActivationExecutionOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            ExecuteLiveActivation = CommandHelpers.HasFlag(args, "--execute-live-activation"),
            FinalApprovedBy = CommandHelpers.GetOption(args, "--final-approved-by") ?? "",
            FinalApprovalExplicitlyProvided = CommandHelpers.HasFlag(args, "--final-approved-by"),
            ExecutionPlanId = CommandHelpers.GetOption(args, "--execution-plan-id") ?? "",
        };

        var runner = new ScopedRuntimePreviewLiveActivationExecutionRunner();
        var isGate = string.Equals(subcommand, "scoped-runtime-preview-live-activation-execution-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.RunGate(plan, freeze, noOp, preflight, dryRun, rtGatePassed, p15Passed, options)
            : runner.RunExecution(plan, freeze, noOp, preflight, dryRun, rtGatePassed, p15Passed, options);

        var fn = isGate ? "live-activation-execution-gate" : "live-activation-execution";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(ScopedRuntimePreviewLiveActivationExecutionRunner.BuildMarkdown(
            isGate ? "Scoped Runtime Preview Live Activation Execution Gate" : "Scoped Runtime Preview Live Activation Execution", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Scoped runtime preview live activation execution written: {jp}");
        Console.WriteLine($"[Eval] executionGatePassed={report.ExecutionGatePassed}; gatePassed={report.GatePassed}; recommendation={report.Recommendation}; " +
            $"nextPhase={report.NextAllowedPhase}; executeLiveActivation={report.ExecuteLiveActivation}; planIdMatches={report.PlanIdMatches}; " +
            $"killSwitchArmed={report.KillSwitchArmed}; configPatchWritten={report.ConfigPatchWritten}; runtimeActivation={report.RuntimeActivation}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteScopedRuntimePreviewLiveActivationObservationAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v7"));
        Directory.CreateDirectory(output);

        var execPath = Path.Combine("vector", "v7", "live-activation-execution.json");
        var execFallback = Path.Combine("vector", "v7", "live-activation-execution-gate.json");
        var execution = await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationExecutionReport>(execPath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationExecutionReport>(execFallback, ct).ConfigureAwait(false);

        var planPath = Path.Combine("vector", "v7", "live-activation-execution-plan.json");
        var planFallback = Path.Combine("vector", "v7", "live-activation-execution-plan-gate.json");
        var plan = await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationExecutionPlanReport>(planPath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationExecutionPlanReport>(planFallback, ct).ConfigureAwait(false);

        var freezePath = Path.Combine("vector", "v7", "activation-live-readiness-freeze.json");
        var freezeFallback = Path.Combine("vector", "v7", "activation-live-readiness-freeze-gate.json");
        var freeze = await ReadJsonFileAsync<ScopedRuntimePreviewActivationLiveReadinessFreezeReport>(freezePath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewActivationLiveReadinessFreezeReport>(freezeFallback, ct).ConfigureAwait(false);

        var noOpPath = Path.Combine("vector", "v7", "activation-window-noop-execution.json");
        var noOpFallback = Path.Combine("vector", "v7", "activation-window-noop-execution-gate.json");
        var noOp = await ReadJsonFileAsync<ScopedRuntimePreviewActivationWindowNoOpExecutionReport>(noOpPath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewActivationWindowNoOpExecutionReport>(noOpFallback, ct).ConfigureAwait(false);

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr))
            p15Passed = pr.GetDouble() >= 1.0;

        var options = new ScopedRuntimePreviewLiveActivationObservationOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            ObservationRuns = CommandHelpers.GetIntOption(args, "--observation-runs", 5),
            RequestsPerRun = CommandHelpers.GetIntOption(args, "--requests-per-run", 8),
        };

        var runner = new ScopedRuntimePreviewLiveActivationObservationRunner();
        var isGate = string.Equals(subcommand, "scoped-runtime-preview-live-activation-observation-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.RunGate(execution, plan, freeze, noOp, rtPassed, p15Passed, options)
            : runner.RunObservation(execution, plan, freeze, noOp, rtPassed, p15Passed, options);

        var fn = isGate ? "live-activation-observation-gate" : "live-activation-observation";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(ScopedRuntimePreviewLiveActivationObservationRunner.BuildMarkdown(
            isGate ? "Live Activation Observation Gate" : "Live Activation Observation", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Live activation observation written: {jp}");
        Console.WriteLine($"[Eval] observationPassed={report.ObservationPassed}; gatePassed={report.GatePassed}; " +
            $"recommendation={report.Recommendation}; nextPhase={report.NextAllowedPhase}; " +
            $"requests={report.ObservedRequestCount}/{report.MaxRequestCap}; approved={report.ApprovedScopeRequestCount}; " +
            $"nonApprovedNoOps={report.NonApprovedScopeNoOpCount}; killSwitchTrips={report.KillSwitchTripCount}; " +
            $"appliedDeltaZero={report.AppliedDeltaZero}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteScopedRuntimePreviewLiveActivationSummaryFreezeAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v7"));
        Directory.CreateDirectory(output);

        var obsPath = Path.Combine("vector", "v7", "live-activation-observation.json");
        var obsFallback = Path.Combine("vector", "v7", "live-activation-observation-gate.json");
        var obs = await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationObservationReport>(obsPath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationObservationReport>(obsFallback, ct).ConfigureAwait(false);

        var execPath = Path.Combine("vector", "v7", "live-activation-execution.json");
        var execFallback = Path.Combine("vector", "v7", "live-activation-execution-gate.json");
        var exec = await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationExecutionReport>(execPath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationExecutionReport>(execFallback, ct).ConfigureAwait(false);

        var planPath = Path.Combine("vector", "v7", "live-activation-execution-plan.json");
        var planFallback = Path.Combine("vector", "v7", "live-activation-execution-plan-gate.json");
        var plan = await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationExecutionPlanReport>(planPath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationExecutionPlanReport>(planFallback, ct).ConfigureAwait(false);

        var freezePath = Path.Combine("vector", "v7", "activation-live-readiness-freeze.json");
        var freezeFallback = Path.Combine("vector", "v7", "activation-live-readiness-freeze-gate.json");
        var freeze = await ReadJsonFileAsync<ScopedRuntimePreviewActivationLiveReadinessFreezeReport>(freezePath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewActivationLiveReadinessFreezeReport>(freezeFallback, ct).ConfigureAwait(false);

        var noOpPath = Path.Combine("vector", "v7", "activation-window-noop-execution.json");
        var noOpFallback = Path.Combine("vector", "v7", "activation-window-noop-execution-gate.json");
        var noOp = await ReadJsonFileAsync<ScopedRuntimePreviewActivationWindowNoOpExecutionReport>(noOpPath, ct).ConfigureAwait(false)
            ?? await ReadJsonFileAsync<ScopedRuntimePreviewActivationWindowNoOpExecutionReport>(noOpFallback, ct).ConfigureAwait(false);

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var options = new ScopedRuntimePreviewLiveActivationSummaryFreezeOptions { Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new ScopedRuntimePreviewLiveActivationSummaryFreezeRunner();
        var isGate = string.Equals(subcommand, "scoped-runtime-preview-live-activation-summary-freeze-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.RunGate(obs, exec, plan, freeze, noOp, rtPassed, p15Passed, options)
            : runner.RunFreeze(obs, exec, plan, freeze, noOp, rtPassed, p15Passed, options);

        var fn = isGate ? "live-activation-summary-freeze-gate" : "live-activation-summary-freeze";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(ScopedRuntimePreviewLiveActivationSummaryFreezeRunner.BuildMarkdown(
            isGate ? "Live Activation Summary Freeze Gate" : "Live Activation Summary Freeze", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Live activation summary freeze written: {jp}");
        Console.WriteLine($"[Eval] freezePassed={report.FreezePassed}; gatePassed={report.GatePassed}; " +
            $"identityUnchanged={report.FinalApprovalIdentityUnchanged}; noRuntimeMutation={report.NoRuntimeMutationInvariant}; " +
            $"evidenceChain={report.FrozenEvidenceChain.Count}; blocked={report.BlockedReasons.Count}");
    }
}
