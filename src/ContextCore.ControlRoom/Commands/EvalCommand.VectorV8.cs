using System.Text.Json;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Commands;
using ContextCore.Core.Services;

namespace ContextCore.ControlRoom.Commands;

public static partial class EvalCommand
{
    private static async Task ExecuteFormalRetrievalPromotionReadinessAuditAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);

        var closePath = Path.Combine("vector", "v7", "live-activation-closeout-gate.json");
        var closeout = await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationCloseoutReport>(closePath, ct).ConfigureAwait(false);

        var sumPath = Path.Combine("vector", "v7", "live-activation-summary-freeze-gate.json");
        var summaryFreeze = await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationSummaryFreezeReport>(sumPath, ct).ConfigureAwait(false);

        var obsPath = Path.Combine("vector", "v7", "live-activation-observation-gate.json");
        var obs = await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationObservationReport>(obsPath, ct).ConfigureAwait(false);

        var execPath = Path.Combine("vector", "v7", "live-activation-execution-gate.json");
        var exec = await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationExecutionReport>(execPath, ct).ConfigureAwait(false);

        var planPath = Path.Combine("vector", "v7", "live-activation-execution-plan-gate.json");
        var plan = await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationExecutionPlanReport>(planPath, ct).ConfigureAwait(false);

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var options = new FormalRetrievalPromotionReadinessAuditOptions { Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new FormalRetrievalPromotionReadinessAuditRunner();
        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-readiness-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.RunGate(closeout, summaryFreeze, obs, exec, plan, rtPassed, p15Passed, options)
            : runner.RunAudit(closeout, summaryFreeze, obs, exec, plan, rtPassed, p15Passed, options);

        var fn = isGate ? "formal-retrieval-promotion-readiness-gate" : "formal-retrieval-promotion-readiness-audit";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionReadinessAuditRunner.BuildMarkdown(
            isGate ? "Formal Retrieval Promotion Readiness Gate" : "Formal Retrieval Promotion Readiness Audit", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Formal retrieval promotion readiness audit written: {jp}");
        Console.WriteLine($"[Eval] auditPassed={report.AuditPassed}; gatePassed={report.GatePassed}; " +
            $"formalRetrievalStillBlocked={report.FormalRetrievalStillBlocked}; " +
            $"noRuntimeMutation={report.NoRuntimeMutationInvariant}; auditItems={report.AuditItems.Count}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteFormalRetrievalPromotionPlanAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);

        var auditPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-readiness-gate.json");
        var audit = await ReadJsonFileAsync<FormalRetrievalPromotionReadinessAuditReport>(auditPath, ct).ConfigureAwait(false);
        var upstreamReadinessArtifactPath = "vector/v8/formal-retrieval-promotion-readiness-gate.json";

        var closePath = Path.Combine("vector", "v7", "live-activation-closeout-gate.json");
        var closeout = await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationCloseoutReport>(closePath, ct).ConfigureAwait(false);

        var sumPath = Path.Combine("vector", "v7", "live-activation-summary-freeze-gate.json");
        var summaryFreeze = await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationSummaryFreezeReport>(sumPath, ct).ConfigureAwait(false);

        var obsPath = Path.Combine("vector", "v7", "live-activation-observation-gate.json");
        var obs = await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationObservationReport>(obsPath, ct).ConfigureAwait(false);

        var execPath = Path.Combine("vector", "v7", "live-activation-execution-gate.json");
        var exec = await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationExecutionReport>(execPath, ct).ConfigureAwait(false);

        var planPath = Path.Combine("vector", "v7", "live-activation-execution-plan-gate.json");
        var plan = await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationExecutionPlanReport>(planPath, ct).ConfigureAwait(false);

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var options = new FormalRetrievalPromotionPlanOptions { Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new FormalRetrievalPromotionPlanRunner();
        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-plan-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.RunGate(audit, closeout, summaryFreeze, obs, exec, plan, rtPassed, p15Passed, options)
            : runner.RunPlan(audit, closeout, summaryFreeze, obs, exec, plan, rtPassed, p15Passed, options);

        var fn = isGate ? "formal-retrieval-promotion-plan-gate" : "formal-retrieval-promotion-plan";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionPlanRunner.BuildMarkdown(
            isGate ? "Formal Retrieval Promotion Plan Gate" : "Formal Retrieval Promotion Plan", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Formal retrieval promotion plan written: {jp}");
        Console.WriteLine($"[Eval] planPassed={report.PlanPassed}; gatePassed={report.GatePassed}; " +
            $"formalRetrievalStillBlocked={report.FormalRetrievalStillBlocked}; " +
            $"requiredManualApproval={report.RequiredManualApproval}; abortConditions={report.AbortConditions.Count}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteFormalRetrievalPromotionApprovalAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);

        var planGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-plan-gate.json");
        var planGate = await ReadJsonFileAsync<FormalRetrievalPromotionPlanReport>(planGatePath, ct).ConfigureAwait(false);

        var readinessPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-readiness-gate.json");
        var readinessGate = await ReadJsonFileAsync<FormalRetrievalPromotionReadinessAuditReport>(readinessPath, ct).ConfigureAwait(false);

        var closePath = Path.Combine("vector", "v7", "live-activation-closeout-gate.json");
        var closeoutGate = await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationCloseoutReport>(closePath, ct).ConfigureAwait(false);

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var options = new FormalRetrievalPromotionApprovalOptions
        {
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            ApprovedBy = CommandHelpers.GetOption(args, "--approved-by") ?? "",
            ExplicitlyProvided = CommandHelpers.HasFlag(args, "--approved-by"),
            ApprovalId = CommandHelpers.GetOption(args, "--approval-id") ?? "",
            ApprovalIdExplicitlyProvided = CommandHelpers.HasFlag(args, "--approval-id"),
            ApprovalScopes = CommandHelpers.GetMultiOption(args, "--approval-scope"),
        };

        var runner = new FormalRetrievalPromotionApprovalRunner();
        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-approval-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.RunGate(planGate, readinessGate, closeoutGate, rtPassed, p15Passed, options)
            : runner.RunApproval(planGate, readinessGate, closeoutGate, rtPassed, p15Passed, options);

        var fn = isGate ? "formal-retrieval-promotion-approval-gate" : "formal-retrieval-promotion-approval";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionApprovalRunner.BuildMarkdown(
            isGate ? "Formal Retrieval Promotion Approval Gate" : "Formal Retrieval Promotion Approval", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Formal retrieval promotion approval written: {jp}");
        Console.WriteLine($"[Eval] approvalGatePassed={report.ApprovalGatePassed}; gatePassed={report.GatePassed}; " +
            $"approvalGranted={report.ApprovalGranted}; approvedBy={report.ApprovedBy}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteFormalRetrievalPromotionApprovalEvidenceSealAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);

        var evidencePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var evidence = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalEvidence>(evidencePath, ct).ConfigureAwait(false);

        var trustPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var trustRegistry = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalTrustRegistry>(trustPath, ct).ConfigureAwait(false);

        var approvalPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-gate.json");
        var approval = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalReport>(approvalPath, ct).ConfigureAwait(false);

        var planGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-plan-gate.json");
        var planGate = await ReadJsonFileAsync<FormalRetrievalPromotionPlanReport>(planGatePath, ct).ConfigureAwait(false);

        var readinessPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-readiness-gate.json");
        var readinessGate = await ReadJsonFileAsync<FormalRetrievalPromotionReadinessAuditReport>(readinessPath, ct).ConfigureAwait(false);

        var closePath = Path.Combine("vector", "v7", "live-activation-closeout-gate.json");
        var closeoutGate = await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationCloseoutReport>(closePath, ct).ConfigureAwait(false);

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var options = new FormalRetrievalPromotionApprovalEvidenceSealOptions { Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new FormalRetrievalPromotionApprovalEvidenceSealRunner();
        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-approval-evidence-seal-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.RunGate(evidence, trustRegistry, approval, planGate, readinessGate, closeoutGate, rtPassed, p15Passed, options)
            : runner.RunSeal(evidence, trustRegistry, approval, planGate, readinessGate, closeoutGate, rtPassed, p15Passed, options);

        var fn = isGate ? "formal-retrieval-promotion-approval-evidence-seal-gate" : "formal-retrieval-promotion-approval-evidence-seal";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionApprovalEvidenceSealRunner.BuildMarkdown(
            isGate ? "Approval Evidence Seal Gate" : "Approval Evidence Seal", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Approval evidence seal written: {jp}");
        Console.WriteLine($"[Eval] sealPassed={report.SealPassed}; gatePassed={report.GatePassed}; " +
            $"evidencePresent={report.EvidencePresent}; approvedBy={report.ApprovedBy}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteFormalRetrievalPromotionExternalApprovalIntakeAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);

        var evidencePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var evidence = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalEvidence>(evidencePath, ct).ConfigureAwait(false);

        var trustPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var trustRegistry = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalTrustRegistry>(trustPath, ct).ConfigureAwait(false);

        var approvalPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-gate.json");
        var pendingApproval = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalReport>(approvalPath, ct).ConfigureAwait(false);

        var planGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-plan-gate.json");
        var planGate = await ReadJsonFileAsync<FormalRetrievalPromotionPlanReport>(planGatePath, ct).ConfigureAwait(false);

        var readinessPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-readiness-gate.json");
        var readinessGate = await ReadJsonFileAsync<FormalRetrievalPromotionReadinessAuditReport>(readinessPath, ct).ConfigureAwait(false);

        var closePath = Path.Combine("vector", "v7", "live-activation-closeout-gate.json");
        var closeoutGate = await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationCloseoutReport>(closePath, ct).ConfigureAwait(false);

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var options = new FormalRetrievalPromotionExternalApprovalIntakeOptions { Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new FormalRetrievalPromotionExternalApprovalIntakeRunner();
        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-external-approval-intake-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.RunGate(evidence, trustRegistry, pendingApproval, planGate, readinessGate, closeoutGate, rtPassed, p15Passed, options)
            : runner.RunIntake(evidence, trustRegistry, pendingApproval, planGate, readinessGate, closeoutGate, rtPassed, p15Passed, options);

        var fn = isGate ? "formal-retrieval-promotion-external-approval-intake-gate" : "formal-retrieval-promotion-external-approval-intake";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionExternalApprovalIntakeRunner.BuildMarkdown(
            isGate ? "External Approval Intake Gate" : "External Approval Intake", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] External approval intake written: {jp}");
        Console.WriteLine($"[Eval] intakePassed={report.IntakePassed}; gatePassed={report.GatePassed}; " +
            $"evidencePresent={report.EvidencePresent}; trustPresent={report.TrustRegistryPresent}; " +
            $"structureValid={report.EvidenceStructureValid}; upstreamMatch={report.UpstreamGateIdsMatch}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteFormalRetrievalPromotionExternalApprovalSubmissionPackAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);

        var evidenceSchemaExists = File.Exists(Path.Combine("vector", "v8", "schemas", "formal-retrieval-promotion-approval-evidence.schema.json"));
        var trustSchemaExists = File.Exists(Path.Combine("vector", "v8", "schemas", "formal-retrieval-promotion-approval-trust-registry.schema.json"));
        var evidenceTemplateExists = File.Exists(Path.Combine("vector", "v8", "templates", "formal-retrieval-promotion-approval-evidence.template.json"));
        var trustTemplateExists = File.Exists(Path.Combine("vector", "v8", "templates", "formal-retrieval-promotion-approval-trust-registry.template.json"));
        var noRealEvidence = !File.Exists(Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json"));
        var noRealRegistry = !File.Exists(Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json"));

        var intakePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-external-approval-intake-gate.json");
        var intake = await ReadJsonFileAsync<FormalRetrievalPromotionExternalApprovalIntakeReport>(intakePath, ct).ConfigureAwait(false);
        var mainlineIntakeBlocked = intake is not null && intake.IntakePassed == false;

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var opt = new FormalRetrievalPromotionExternalApprovalSubmissionPackOptions { Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new FormalRetrievalPromotionExternalApprovalSubmissionPackRunner();
        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-external-approval-submission-pack-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.RunGate(evidenceSchemaExists, trustSchemaExists, evidenceTemplateExists, trustTemplateExists, mainlineIntakeBlocked, noRealEvidence, noRealRegistry, rtPassed, p15Passed, opt)
            : runner.RunPack(evidenceSchemaExists, trustSchemaExists, evidenceTemplateExists, trustTemplateExists, mainlineIntakeBlocked, noRealEvidence, noRealRegistry, rtPassed, p15Passed, opt);

        var fn = isGate ? "formal-retrieval-promotion-external-approval-submission-pack-gate" : "formal-retrieval-promotion-external-approval-submission-pack";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionExternalApprovalSubmissionPackRunner.BuildMarkdown(
            isGate ? "Submission Pack Gate" : "Submission Pack", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Submission pack written: {jp}");
        Console.WriteLine($"[Eval] packPassed={report.PackPassed}; gatePassed={report.GatePassed}; " +
            $"schemas={report.EvidenceSchemaPresent && report.TrustRegistrySchemaPresent}; " +
            $"mainlineBlocked={report.MainlineIntakeStillBlocked}; blocked={report.BlockedReasons.Count}");
    }
}
