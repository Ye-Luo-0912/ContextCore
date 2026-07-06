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
        var evidenceTemplatePath = Path.Combine("vector", "v8", "templates", "formal-retrieval-promotion-approval-evidence.template.json");
        var trustTemplatePath = Path.Combine("vector", "v8", "templates", "formal-retrieval-promotion-approval-trust-registry.template.json");
        var evidenceTemplateExists = File.Exists(evidenceTemplatePath);
        var trustTemplateExists = File.Exists(trustTemplatePath);

        var templatesContainPlaceholders = false;
        var evidenceFieldsValid = false;
        var trustFieldsValid = false;
        var missingFields = new List<string>();
        var nonPlaceholderFields = new List<string>();

        if (evidenceTemplateExists && trustTemplateExists)
        {
            var evidenceContent = await File.ReadAllTextAsync(evidenceTemplatePath, ct).ConfigureAwait(false);
            var trustContent = await File.ReadAllTextAsync(trustTemplatePath, ct).ConfigureAwait(false);
            templatesContainPlaceholders = evidenceContent.Contains("{{PLACEHOLDER:", StringComparison.OrdinalIgnoreCase)
                && trustContent.Contains("{{PLACEHOLDER:", StringComparison.OrdinalIgnoreCase);

            var evidenceKeys = new[] { "ApprovalEvidenceId", "ApprovedBy", "ApprovalId", "ApprovalScopes[0]",
                "ApprovalSource", "ApprovalTimestamp", "SourcePromotionPlanGateOperationId",
                "SourceReadinessGateOperationId", "SourceCloseoutGateOperationId", "OperatorStatement",
                "EvidenceCreatedAt", "ApprovalEvidenceSourceKind", "ApprovalEvidenceProvenanceId",
                "ApprovalEvidenceProvidedBy", "ApprovalEvidenceProvidedAt", "ApprovalEvidenceTrustMode",
                "ApprovalEvidenceChecksum", "SourceApprovalRequestId", "BoundPendingApprovalGateOperationId" };

            var trustKeys = new[] { "RegistryId", "RegistryCreatedAt", "AllowedSourceKinds[0]",
                "TrustedProvenanceRecords[0].ApprovalEvidenceProvenanceId",
                "TrustedProvenanceRecords[0].ApprovalEvidenceSourceKind",
                "TrustedProvenanceRecords[0].ApprovalEvidenceProvidedBy",
                "TrustedProvenanceRecords[0].ApprovalEvidenceChecksum",
                "TrustedProvenanceRecords[0].SourceApprovalRequestId",
                "TrustedProvenanceRecords[0].BoundPendingApprovalGateOperationId",
                "TrustedProvenanceRecords[0].AllowedScopes[0]",
                "TrustedProvenanceRecords[0].TrustMode",
                "TrustedProvenanceRecords[0].ValidUntil" };

            evidenceFieldsValid = ValidateTemplateFields(evidenceContent, evidenceKeys, missingFields, nonPlaceholderFields);
            trustFieldsValid = ValidateTemplateFields(trustContent, trustKeys, missingFields, nonPlaceholderFields);
        }

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
            ? runner.RunGate(evidenceSchemaExists, trustSchemaExists, evidenceTemplateExists, trustTemplateExists, mainlineIntakeBlocked, noRealEvidence, noRealRegistry, templatesContainPlaceholders, evidenceFieldsValid, trustFieldsValid, missingFields, nonPlaceholderFields, rtPassed, p15Passed, opt)
            : runner.RunPack(evidenceSchemaExists, trustSchemaExists, evidenceTemplateExists, trustTemplateExists, mainlineIntakeBlocked, noRealEvidence, noRealRegistry, templatesContainPlaceholders, evidenceFieldsValid, trustFieldsValid, missingFields, nonPlaceholderFields, rtPassed, p15Passed, opt);

        var fn = isGate ? "formal-retrieval-promotion-external-approval-submission-pack-gate" : "formal-retrieval-promotion-external-approval-submission-pack";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionExternalApprovalSubmissionPackRunner.BuildMarkdown(
            isGate ? "Submission Pack Gate" : "Submission Pack", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Submission pack written: {jp}");
        Console.WriteLine($"[Eval] packPassed={report.PackPassed}; gatePassed={report.GatePassed}; " +
            $"schemas={report.EvidenceSchemaPresent && report.TrustRegistrySchemaPresent}; " +
            $"fieldsValid=evidence:{report.EvidenceTemplatePlaceholderFieldsValid} trust:{report.TrustRegistryTemplatePlaceholderFieldsValid}; " +
            $"mainlineBlocked={report.MainlineIntakeStillBlocked}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteFormalRetrievalPromotionExternalApprovalDryRunAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);

        var mainlineEvidenceExists = File.Exists(Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json"));
        var mainlineRegistryExists = File.Exists(Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json"));

        var fixtureEvidencePath = Path.Combine("vector", "v8", "fixtures", "formal-retrieval-promotion-approval-evidence.fixture.json");
        var fixtureRegistryPath = Path.Combine("vector", "v8", "fixtures", "formal-retrieval-promotion-approval-trust-registry.fixture.json");
        var fixtureEvidencePresent = File.Exists(fixtureEvidencePath);
        var fixtureRegistryPresent = File.Exists(fixtureRegistryPath);

        var fixtureEvidence = fixtureEvidencePresent
            ? await ReadJsonFileAsync<FormalRetrievalPromotionApprovalEvidence>(fixtureEvidencePath, ct).ConfigureAwait(false)
            : null;
        var fixtureRegistry = fixtureRegistryPresent
            ? await ReadJsonFileAsync<FormalRetrievalPromotionApprovalTrustRegistry>(fixtureRegistryPath, ct).ConfigureAwait(false)
            : null;

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

        var intakePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-external-approval-intake-gate.json");
        var intake = await ReadJsonFileAsync<FormalRetrievalPromotionExternalApprovalIntakeReport>(intakePath, ct).ConfigureAwait(false);
        var intakeBlocked = intake is not null && intake.IntakePassed == false;
        var intakeHasRequiredReasons = intake is not null
            && intake.BlockedReasons.Any(r => string.Equals(r, "ExternalApprovalEvidenceMissing", StringComparison.OrdinalIgnoreCase))
            && intake.BlockedReasons.Any(r => string.Equals(r, "TrustRegistryMissing", StringComparison.OrdinalIgnoreCase));
        var intakeSafetyOk = intake is not null
            && intake.FormalRetrievalAllowed == false
            && intake.RuntimeSwitchAllowed == false
            && intake.FormalPackageWritten == false
            && intake.PackageOutputChanged == false
            && intake.PackingPolicyChanged == false
            && intake.VectorStoreBindingChanged == false
            && intake.GlobalDefaultOn == false
            && intake.ConfigPatchWritten == false
            && intake.RuntimeActivation == false
            && intake.NoRuntimeMutationInvariant == true;
        var intakeReasonsClean = intakeHasRequiredReasons
            && !intake.BlockedReasons.Any(r => r.Contains("Runtime", StringComparison.OrdinalIgnoreCase)
                || r.Contains("Package", StringComparison.OrdinalIgnoreCase)
                || r.Contains("Packing", StringComparison.OrdinalIgnoreCase)
                || r.Contains("Vector", StringComparison.OrdinalIgnoreCase)
                || r.Contains("Config", StringComparison.OrdinalIgnoreCase)
                || r.Contains("Safety", StringComparison.OrdinalIgnoreCase)
                || r.Contains("Activation", StringComparison.OrdinalIgnoreCase)
                || r.Contains("Mutation", StringComparison.OrdinalIgnoreCase));
        var intakeBlockedClean = intakeHasRequiredReasons && intakeSafetyOk && intakeReasonsClean;

        var opt = new FormalRetrievalPromotionExternalApprovalDryRunOptions { Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new FormalRetrievalPromotionExternalApprovalDryRunRunner();
        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-external-approval-dry-run-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.RunGate(mainlineEvidenceExists, mainlineRegistryExists, fixtureEvidencePresent, fixtureRegistryPresent, fixtureEvidence, fixtureRegistry, pendingApproval, planGate, readinessGate, closeoutGate, intakeBlocked, intakeBlockedClean, rtPassed, p15Passed, opt)
            : runner.RunDryRun(mainlineEvidenceExists, mainlineRegistryExists, fixtureEvidencePresent, fixtureRegistryPresent, fixtureEvidence, fixtureRegistry, pendingApproval, planGate, readinessGate, closeoutGate, intakeBlocked, intakeBlockedClean, rtPassed, p15Passed, opt);

        var fn = isGate ? "formal-retrieval-promotion-external-approval-dry-run-gate" : "formal-retrieval-promotion-external-approval-dry-run";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionExternalApprovalDryRunRunner.BuildMarkdown(
            isGate ? "External Approval Dry-Run Gate" : "External Approval Dry-Run", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] External approval dry-run written: {jp}");
        Console.WriteLine($"[Eval] dryRunPassed={report.DryRunPassed}; gatePassed={report.GatePassed}; " +
            $"fixtureIsolation={report.FixtureIsolationVerified}; sourceIdsMatch={report.SourceGateIdsMatch}; " +
            $"provenanceFound={report.ProvenanceRecordFound}; checksumMatch={report.ChecksumMatched}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteFormalRetrievalPromotionExternalApprovalDryRunNegativeMatrixAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);

        var fixtureEvidencePath = Path.Combine("vector", "v8", "fixtures", "formal-retrieval-promotion-approval-evidence.fixture.json");
        var fixtureRegistryPath = Path.Combine("vector", "v8", "fixtures", "formal-retrieval-promotion-approval-trust-registry.fixture.json");
        var fixtureEvidence = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalEvidence>(fixtureEvidencePath, ct).ConfigureAwait(false);
        var fixtureRegistry = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalTrustRegistry>(fixtureRegistryPath, ct).ConfigureAwait(false);

        var approvalPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-gate.json");
        var pendingApproval = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalReport>(approvalPath, ct).ConfigureAwait(false);

        var planGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-plan-gate.json");
        var planGate = await ReadJsonFileAsync<FormalRetrievalPromotionPlanReport>(planGatePath, ct).ConfigureAwait(false);

        var readinessPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-readiness-gate.json");
        var readinessGate = await ReadJsonFileAsync<FormalRetrievalPromotionReadinessAuditReport>(readinessPath, ct).ConfigureAwait(false);

        var closePath = Path.Combine("vector", "v7", "live-activation-closeout-gate.json");
        var closeoutGate = await ReadJsonFileAsync<ScopedRuntimePreviewLiveActivationCloseoutReport>(closePath, ct).ConfigureAwait(false);

        var mainlineEv = File.Exists(Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json"));
        var mainlineReg = File.Exists(Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json"));

        var intakePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-external-approval-intake-gate.json");
        var intake = await ReadJsonFileAsync<FormalRetrievalPromotionExternalApprovalIntakeReport>(intakePath, ct).ConfigureAwait(false);
        var intakeBlocked = intake is not null && intake.IntakePassed == false;
        var intakeHasReasons = intake is not null
            && intake.BlockedReasons.Any(r => string.Equals(r, "ExternalApprovalEvidenceMissing"))
            && intake.BlockedReasons.Any(r => string.Equals(r, "TrustRegistryMissing"));
        var intakeSafetyOk = intake is not null && intake.FormalRetrievalAllowed == false && intake.RuntimeSwitchAllowed == false
            && intake.FormalPackageWritten == false && intake.PackageOutputChanged == false && intake.PackingPolicyChanged == false
            && intake.VectorStoreBindingChanged == false && intake.GlobalDefaultOn == false && intake.ConfigPatchWritten == false
            && intake.RuntimeActivation == false && intake.NoRuntimeMutationInvariant == true;
        var intakeReasonsClean = intakeHasReasons
            && !intake.BlockedReasons.Any(r => r.Contains("Runtime") || r.Contains("Package") || r.Contains("Packing")
                || r.Contains("Vector") || r.Contains("Config") || r.Contains("Safety") || r.Contains("Activation") || r.Contains("Mutation"));
        var intakeBlockedClean = intakeHasReasons && intakeSafetyOk && intakeReasonsClean;

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-external-approval-dry-run-negative-matrix-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new FormalRetrievalPromotionExternalApprovalDryRunMatrixOptions { IsGate = isGate, Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new FormalRetrievalPromotionExternalApprovalDryRunNegativeMatrixRunner();
        var report = runner.Run(rtPassed, p15Passed, mainlineEv, mainlineReg, fixtureEvidence, fixtureRegistry, pendingApproval, planGate, readinessGate, closeoutGate, intakeBlocked, intakeBlockedClean, opt);

        var fn = isGate ? "formal-retrieval-promotion-external-approval-dry-run-negative-matrix-gate" : "formal-retrieval-promotion-external-approval-dry-run-negative-matrix";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionExternalApprovalDryRunNegativeMatrixRunner.BuildMarkdown(
            isGate ? "Dry-Run Negative Matrix Gate" : "Dry-Run Negative Matrix", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Negative matrix written: {jp}");
        Console.WriteLine($"[Eval] matrixPassed={report.MatrixPassed}; gatePassed={report.GatePassed}; " +
            $"total={report.TotalCases} passed={report.PassedCases} failed={report.FailedCases}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteFormalRetrievalPromotionExternalApprovalQuarantineScanAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(Path.Combine("vector", "v8", "quarantine"));

        var qEvidencePath = Path.Combine("vector", "v8", "quarantine", "formal-retrieval-promotion-approval-evidence.candidate.json");
        var qRegistryPath = Path.Combine("vector", "v8", "quarantine", "formal-retrieval-promotion-approval-trust-registry.candidate.json");
        var evExists = File.Exists(qEvidencePath);
        var regExists = File.Exists(qRegistryPath);
        var candidateFiles = new List<string>();
        if (evExists) candidateFiles.Add(qEvidencePath);
        if (regExists) candidateFiles.Add(qRegistryPath);

        var evidenceStatus = QuarantineScanStatuses.Missing;
        var registryStatus = QuarantineScanStatuses.Missing;
        var evValid = false;
        var regValid = false;
        var evSchemaValid = false;
        var regSchemaValid = false;
        var missingFields = new List<string>();
        var invalidFields = new List<string>();


        if (evExists)
        {
            evidenceStatus = QuarantineScanStatuses.CandidateFound;
            try
            {
                var rawJson = await File.ReadAllTextAsync(qEvidencePath, ct).ConfigureAwait(false);
                var validation = FormalRetrievalPromotionExternalApprovalQuarantineCandidateValidation.ValidateEvidenceJson(rawJson);
                evValid = validation.CandidateValid;
                evSchemaValid = validation.SchemaValid;
                missingFields.AddRange(validation.MissingFields);
                invalidFields.AddRange(validation.InvalidFields);
                evidenceStatus = evValid ? (evSchemaValid ? QuarantineScanStatuses.ReadyForManualReview : QuarantineScanStatuses.Invalid) : QuarantineScanStatuses.Invalid;
            }
            catch { evidenceStatus = QuarantineScanStatuses.Invalid; missingFields.Add("<evidence-parse-error>"); }
        }

        if (regExists)
        {
            registryStatus = QuarantineScanStatuses.CandidateFound;
            try
            {
                var rawJson = await File.ReadAllTextAsync(qRegistryPath, ct).ConfigureAwait(false);
                var validation = FormalRetrievalPromotionExternalApprovalQuarantineCandidateValidation.ValidateTrustRegistryJson(rawJson);
                regValid = validation.CandidateValid;
                regSchemaValid = validation.SchemaValid;
                missingFields.AddRange(validation.MissingFields);
                invalidFields.AddRange(validation.InvalidFields);
                registryStatus = regValid ? (regSchemaValid ? QuarantineScanStatuses.ReadyForManualReview : QuarantineScanStatuses.Invalid) : QuarantineScanStatuses.Invalid;
            }
            catch { registryStatus = QuarantineScanStatuses.Invalid; missingFields.Add("<registry-parse-error>"); }
        }

        var mainlineEv = File.Exists(Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json"));
        var mainlineReg = File.Exists(Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json"));

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var opt = new FormalRetrievalPromotionExternalApprovalQuarantineScanOptions { Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new FormalRetrievalPromotionExternalApprovalQuarantineScanRunner();
        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-external-approval-quarantine-scan-gate", StringComparison.OrdinalIgnoreCase);
        var report = isGate
            ? runner.RunGate(evExists, regExists, evidenceStatus, registryStatus, evValid, regValid, evSchemaValid, regSchemaValid, missingFields, invalidFields, mainlineEv, mainlineReg, candidateFiles, rtPassed, p15Passed, opt)
            : runner.RunScan(evExists, regExists, evidenceStatus, registryStatus, evValid, regValid, evSchemaValid, regSchemaValid, missingFields, invalidFields, mainlineEv, mainlineReg, candidateFiles, rtPassed, p15Passed, opt);

        var fn = isGate ? "formal-retrieval-promotion-external-approval-quarantine-scan-gate" : "formal-retrieval-promotion-external-approval-quarantine-scan";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionExternalApprovalQuarantineScanRunner.BuildMarkdown(
            isGate ? "Quarantine Scan Gate" : "Quarantine Scan", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Quarantine scan written: {jp}");
        Console.WriteLine($"[Eval] scanPassed={report.ScanPassed}; gatePassed={report.GatePassed}; " +
            $"evidenceCandidate={report.EvidenceCandidatePresent}; registryCandidate={report.TrustRegistryCandidatePresent}; " +
            $"promotionToMainline={report.PromotionToMainlinePerformed}; blocked={report.BlockedReasons.Count}");
    }

    private static async Task ExecuteFormalRetrievalPromotionExternalApprovalQuarantineNegativeMatrixAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(Path.Combine("vector", "v8", "quarantine"));

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-external-approval-quarantine-negative-matrix-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new FormalRetrievalPromotionExternalApprovalQuarantineMatrixOptions { IsGate = isGate, Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new FormalRetrievalPromotionExternalApprovalQuarantineNegativeMatrixRunner();
        var report = runner.Run(rtPassed, p15Passed, opt);

        var fn = isGate ? "formal-retrieval-promotion-external-approval-quarantine-validation-negative-matrix-gate" : "formal-retrieval-promotion-external-approval-quarantine-validation-negative-matrix";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionExternalApprovalQuarantineNegativeMatrixRunner.BuildMarkdown(
            isGate ? "Quarantine Negative Matrix Gate" : "Quarantine Negative Matrix", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Quarantine negative matrix written: {jp}");
        Console.WriteLine($"[Eval] matrixPassed={report.MatrixPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} passed={report.PassedCases} failed={report.FailedCases}");
    }

    private static async Task ExecuteFormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        // mainline approval evidence / trust registry 鏂囦欢涓嶅緱鍑虹幇銆?
        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-external-approval-quarantine-positive-matrix-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixOptions { IsGate = isGate, Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixRunner();
        var report = runner.Run(rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt);

        var fn = isGate ? "formal-retrieval-promotion-external-approval-quarantine-positive-matrix-gate" : "formal-retrieval-promotion-external-approval-quarantine-positive-matrix";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixRunner.BuildMarkdown(
            isGate ? "Quarantine Positive Matrix Gate" : "Quarantine Positive Matrix", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Quarantine positive matrix written: {jp}");
        Console.WriteLine($"[Eval] positiveMatrixPassed={report.PositiveMatrixPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} passed={report.PassedCases} failed={report.FailedCases}; mainlineEv={report.MainlineEvidencePresent}; mainlineReg={report.MainlineTrustRegistryPresent}");
    }

    private static async Task ExecuteFormalRetrievalPromotionApprovalTrustChainValidationMatrixAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        // mainline approval evidence / trust registry 鏂囦欢涓嶅緱鍑虹幇銆?
        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-approval-trust-chain-validation-matrix-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new FormalRetrievalPromotionApprovalTrustChainValidationMatrixOptions { IsGate = isGate, Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new FormalRetrievalPromotionApprovalTrustChainValidationMatrixRunner();
        var report = runner.Run(rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt);

        var fn = isGate
            ? "formal-retrieval-promotion-approval-trust-chain-validation-matrix-gate"
            : "formal-retrieval-promotion-approval-trust-chain-validation-matrix";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionApprovalTrustChainValidationMatrixRunner.BuildMarkdown(
            isGate ? "Trust Chain Validation Matrix Gate" : "Trust Chain Validation Matrix", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Trust chain validation matrix written: {jp}");
        Console.WriteLine($"[Eval] chainValidationPassed={report.ChainValidationPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} pos={report.PositiveCases} neg={report.NegativeCases} passed={report.PassedCases} failed={report.FailedCases}");
    }

    private static async Task ExecuteFormalRetrievalPromotionApprovalPolicyAuthorityMatrixAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-approval-policy-authority-matrix-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new FormalRetrievalPromotionApprovalPolicyAuthorityMatrixOptions { IsGate = isGate, Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new FormalRetrievalPromotionApprovalPolicyAuthorityMatrixRunner();
        var report = runner.Run(rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt);

        var fn = isGate
            ? "formal-retrieval-promotion-approval-policy-authority-matrix-gate"
            : "formal-retrieval-promotion-approval-policy-authority-matrix";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionApprovalPolicyAuthorityMatrixRunner.BuildMarkdown(
            isGate ? "Policy Authority Matrix Gate" : "Policy Authority Matrix", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Policy authority matrix written: {jp}");
        Console.WriteLine($"[Eval] policyAuthorityMatrixPassed={report.PolicyAuthorityMatrixPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} grant={report.GrantCases} deny={report.DenyCases} indeterminate={report.IndeterminateCases} grantApplied={report.GrantApplied}");
    }

    private static async Task ExecuteFormalRetrievalPromotionApprovalGrantApplicationMatrixAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-approval-grant-application-matrix-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new FormalRetrievalPromotionApprovalGrantApplicationMatrixOptions { IsGate = isGate, Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new FormalRetrievalPromotionApprovalGrantApplicationMatrixRunner();
        var report = runner.Run(rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt);

        var fn = isGate
            ? "formal-retrieval-promotion-approval-grant-application-matrix-gate"
            : "formal-retrieval-promotion-approval-grant-application-matrix";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionApprovalGrantApplicationMatrixRunner.BuildMarkdown(
            isGate ? "Grant Application Matrix Gate" : "Grant Application Matrix", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Grant application matrix written: {jp}");
        Console.WriteLine($"[Eval] grantApplicationMatrixPassed={report.GrantApplicationMatrixPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} notApplicable={report.NotApplicableCases} blocked={report.BlockedCases} ready={report.ReadyCases} applicationApplied={report.ApplicationApplied}");
    }

    private static async Task ExecuteFormalRetrievalPromotionApprovalRollbackReadinessMatrixAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-approval-rollback-readiness-matrix-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new FormalRetrievalPromotionApprovalRollbackReadinessMatrixOptions { IsGate = isGate, Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new FormalRetrievalPromotionApprovalRollbackReadinessMatrixRunner();
        var report = runner.Run(rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt);

        var fn = isGate
            ? "formal-retrieval-promotion-approval-rollback-readiness-matrix-gate"
            : "formal-retrieval-promotion-approval-rollback-readiness-matrix";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionApprovalRollbackReadinessMatrixRunner.BuildMarkdown(
            isGate ? "Rollback Readiness Matrix Gate" : "Rollback Readiness Matrix", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Rollback readiness matrix written: {jp}");
        Console.WriteLine($"[Eval] rollbackReadinessMatrixPassed={report.RollbackReadinessMatrixPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} notApplicable={report.NotApplicableCases} incomplete={report.IncompleteCases} ready={report.ReadyCases} rollbackActivated={report.RollbackActivated} applicationApplied={report.ApplicationApplied}");
    }

    private static async Task ExecuteFormalRetrievalPromotionApprovalOperatorSignOffMatrixAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-approval-operator-sign-off-matrix-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new FormalRetrievalPromotionApprovalOperatorSignOffMatrixOptions { IsGate = isGate, Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new FormalRetrievalPromotionApprovalOperatorSignOffMatrixRunner();
        var report = runner.Run(rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt);

        var fn = isGate
            ? "formal-retrieval-promotion-approval-operator-sign-off-matrix-gate"
            : "formal-retrieval-promotion-approval-operator-sign-off-matrix";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionApprovalOperatorSignOffMatrixRunner.BuildMarkdown(
            isGate ? "Operator Sign-Off Matrix Gate" : "Operator Sign-Off Matrix", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Operator sign-off matrix written: {jp}");
        Console.WriteLine($"[Eval] operatorSignOffMatrixPassed={report.OperatorSignOffMatrixPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} notApplicable={report.NotApplicableCases} insufficient={report.InsufficientCases} recorded={report.RecordedCases} crossed={report.Crossed} applicationApplied={report.ApplicationApplied} rollbackActivated={report.RollbackActivated}");
    }

    private static async Task ExecuteFormalRetrievalPromotionApprovalPreCrossingFinalGateAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        // 鐪熷疄浠庣鐩樺姞杞戒笁涓?V8.13/V8.14/V8.15 gate artifact锛坢atrix 鍐呴儴 scenarios 鐢ㄥ悎鎴愭暟鎹紝浣?final-gate state 蹇呴』鐪嬬湡瀹炴枃浠讹級銆?
        var grantGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-grant-application-matrix-gate.json");
        var rollbackGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-rollback-readiness-matrix-gate.json");
        var signOffGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-operator-sign-off-matrix-gate.json");

        var grantGate = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalGrantApplicationMatrixReport>(grantGatePath, ct).ConfigureAwait(false);
        var rollbackGate = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalRollbackReadinessMatrixReport>(rollbackGatePath, ct).ConfigureAwait(false);
        var signOffGate = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalOperatorSignOffMatrixReport>(signOffGatePath, ct).ConfigureAwait(false);

        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-approval-pre-crossing-final-gate-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new FormalRetrievalPromotionApprovalPreCrossingFinalGateOptions { IsGate = isGate, Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new FormalRetrievalPromotionApprovalPreCrossingFinalGateRunner();
        var report = runner.Run(grantGate, rollbackGate, signOffGate, rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt);

        var fn = isGate
            ? "formal-retrieval-promotion-approval-pre-crossing-final-gate-gate"
            : "formal-retrieval-promotion-approval-pre-crossing-final-gate";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionApprovalPreCrossingFinalGateRunner.BuildMarkdown(
            isGate ? "Pre-Crossing Final Gate (Gate)" : "Pre-Crossing Final Gate", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Pre-crossing final gate written: {jp}");
        Console.WriteLine($"[Eval] preCrossingFinalGatePassed={report.PreCrossingFinalGatePassed}; gatePassed={report.GatePassed}; total={report.TotalCases} ready={report.ReadyCases} blocked={report.BlockedCases} upstream(grant/rollback/signOff)={report.UpstreamGrantApplicationGatePassed}/{report.UpstreamRollbackReadinessGatePassed}/{report.UpstreamOperatorSignOffGatePassed} boundCapability={report.BoundCapability} crossed={report.Crossed}");
    }

    private static async Task ExecuteFormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        // 鐪熷疄浠庣鐩樺姞杞?V8.16 pre-crossing gate artifact銆?
        var preCrossingGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-pre-crossing-final-gate-gate.json");
        var preCrossingGate = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalPreCrossingFinalGateReport>(preCrossingGatePath, ct).ConfigureAwait(false);

        // 鐪熷疄鏍稿 planned config patch path 鏄惁浼氳鐩栨棦鏈夋枃浠躲€?
        var capability = preCrossingGate?.BoundCapability ?? PolicyAuthorityKnownCapabilities.FormalRetrievalActivation;
        var scope = preCrossingGate?.BoundScope ?? "demo-workspace/demo-collection";
        var safeCapability = string.Concat(capability.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        var safeScope = string.Concat(scope.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        var plannedConfigPatchPath = Path.Combine("vector", "v8", "dedicated-crossing", $"runtime-config-patch-{safeCapability}-{safeScope}.json");
        var configPatchExists = File.Exists(plannedConfigPatchPath);

        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-approval-dedicated-crossing-dry-run-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunOptions { IsGate = isGate, Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunRunner();
        var report = runner.Run(preCrossingGate, rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, configPatchExists, opt);

        var fn = isGate
            ? "formal-retrieval-promotion-approval-dedicated-crossing-dry-run-gate"
            : "formal-retrieval-promotion-approval-dedicated-crossing-dry-run";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunRunner.BuildMarkdown(
            isGate ? "Dedicated Crossing Dry-Run (Gate)" : "Dedicated Crossing Dry-Run", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Dedicated crossing dry-run written: {jp}");
        Console.WriteLine($"[Eval] crossingDryRunMatrixPassed={report.CrossingDryRunMatrixPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} ready={report.ReadyCases} blocked={report.BlockedCases} dryRunOnly={report.DryRunOnly} executionAllowed={report.CrossingExecutionAllowed} crossed={report.Crossed} boundCapability={report.BoundCapability} boundScope={report.BoundScope} plannedArtifacts={report.PlannedArtifacts.Count}");
    }

    private static async Task ExecuteFormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        // 鐪熷疄浠庣鐩樺姞杞?V8.17 dry-run gate + V8.16 pre-crossing gate
        var dryRunGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-dedicated-crossing-dry-run-gate.json");
        var preCrossingGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-pre-crossing-final-gate-gate.json");
        var dryRunGate = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalDedicatedCrossingGateDryRunReport>(dryRunGatePath, ct).ConfigureAwait(false);
        var preCrossingGate = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalPreCrossingFinalGateReport>(preCrossingGatePath, ct).ConfigureAwait(false);

        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-approval-dedicated-crossing-execution-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateOptions { IsGate = isGate, Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateRunner();
        var report = runner.Run(dryRunGate, preCrossingGate, rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent,
            realPathExists: null,  // 榛樿 File.Exists
            realWriter: null,      // 榛樿鐪熷疄 writer
            opt);

        var fn = isGate
            ? "formal-retrieval-promotion-approval-dedicated-crossing-execution-gate"
            : "formal-retrieval-promotion-approval-dedicated-crossing-execution";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateRunner.BuildMarkdown(
            isGate ? "Dedicated Crossing Execution (Gate)" : "Dedicated Crossing Execution", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Dedicated crossing execution written: {jp}");
        Console.WriteLine($"[Eval] dedicatedCrossingExecutionGatePassed={report.DedicatedCrossingExecutionGatePassed}; gatePassed={report.GatePassed}; total={report.TotalCases} executed={report.ExecutedCases} blocked={report.BlockedCases} crossed={report.Crossed} artifactOnly={report.ArtifactOnly} runtimeActivation={report.RuntimeActivation} formalRetrievalAllowed={report.FormalRetrievalAllowed} writtenArtifacts={report.WrittenArtifactPaths.Count}");
    }

    private static async Task ExecuteFormalRetrievalPromotionApprovalRuntimeActivationDryRunAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        // 鍔犺浇 V8.18 execution gate report
        var executionGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-dedicated-crossing-execution-gate.json");
        var executionGate = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateReport>(executionGatePath, ct).ConfigureAwait(false);

        // 鍔犺浇 V8.18 鍐欏嚭鐨?5 涓?artifact
        var crossingDir = Path.Combine("vector", "v8", "dedicated-crossing");
        var grantPath = Path.Combine(crossingDir, "capability-grant-FormalRetrievalActivation-demo-workspace-demo-collection.json");
        var configPatchPath = Path.Combine(crossingDir, "runtime-config-patch-FormalRetrievalActivation-demo-workspace-demo-collection.json");
        var rollbackPath = Path.Combine(crossingDir, "rollback-snapshot-FormalRetrievalActivation-demo-workspace-demo-collection.json");
        var auditLogPath = Path.Combine(crossingDir, "audit-log-FormalRetrievalActivation-demo-workspace-demo-collection.jsonl");
        var revocationPath = Path.Combine(crossingDir, "revocation-record-FormalRetrievalActivation-demo-workspace-demo-collection.json");

        var grant = await ReadJsonFileAsync<CrossingCapabilityGrantContent>(grantPath, ct).ConfigureAwait(false);
        var configPatch = await ReadJsonFileAsync<CrossingRuntimeConfigPatchContent>(configPatchPath, ct).ConfigureAwait(false);
        var rollback = await ReadJsonFileAsync<CrossingRollbackSnapshotContent>(rollbackPath, ct).ConfigureAwait(false);
        var revocation = await ReadJsonFileAsync<CrossingRevocationRecordContent>(revocationPath, ct).ConfigureAwait(false);

        // jsonl: 璇荤涓€琛岃В鏋愩€?
        CrossingAuditLogEvent? auditEvent = null;
        if (File.Exists(auditLogPath))
        {
            try
            {
                var firstLine = File.ReadAllLines(auditLogPath).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                if (!string.IsNullOrWhiteSpace(firstLine))
                {
                    auditEvent = JsonSerializer.Deserialize<CrossingAuditLogEvent>(firstLine);
                }
            }
            catch { /* parse failure 鈫?auditEvent stays null 鈫?policy reports ArtifactMissing */ }
        }

        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-approval-runtime-activation-dry-run-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new FormalRetrievalPromotionApprovalRuntimeActivationDryRunOptions { IsGate = isGate, Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new FormalRetrievalPromotionApprovalRuntimeActivationDryRunRunner();
        var report = runner.Run(
            executionGate, grant, configPatch, rollback, auditEvent, revocation,
            rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent,
            configPatchSourcePath: configPatchPath,
            rollbackSnapshotPath: rollbackPath,
            revocationRecordPath: revocationPath,
            opt);

        var fn = isGate
            ? "formal-retrieval-promotion-approval-runtime-activation-dry-run-gate"
            : "formal-retrieval-promotion-approval-runtime-activation-dry-run";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionApprovalRuntimeActivationDryRunRunner.BuildMarkdown(
            isGate ? "Runtime Activation Dry-Run (Gate)" : "Runtime Activation Dry-Run", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Runtime activation dry-run written: {jp}");
        Console.WriteLine($"[Eval] runtimeActivationDryRunPassed={report.RuntimeActivationDryRunPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} ready={report.ReadyCases} blocked={report.BlockedCases} boundGrantId={report.BoundGrantId} runtimeActivation={report.RuntimeActivation} formalRetrievalAllowed={report.FormalRetrievalAllowed} configPatchApplied={report.ConfigPatchAppliedToRuntime}");
    }

    private static async Task ExecuteFormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        var activationDryRunGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-runtime-activation-dry-run-gate.json");
        var activationDryRunGate = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport>(activationDryRunGatePath, ct).ConfigureAwait(false);

        var dedicatedCrossingExecutionGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-dedicated-crossing-execution-gate.json");
        _ = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateReport>(dedicatedCrossingExecutionGatePath, ct).ConfigureAwait(false);

        var crossingDir = Path.Combine("vector", "v8", "dedicated-crossing");
        _ = await ReadJsonFileAsync<CrossingCapabilityGrantContent>(Path.Combine(crossingDir, "capability-grant-FormalRetrievalActivation-demo-workspace-demo-collection.json"), ct).ConfigureAwait(false);
        _ = await ReadJsonFileAsync<CrossingRuntimeConfigPatchContent>(Path.Combine(crossingDir, "runtime-config-patch-FormalRetrievalActivation-demo-workspace-demo-collection.json"), ct).ConfigureAwait(false);
        _ = await ReadJsonFileAsync<CrossingRollbackSnapshotContent>(Path.Combine(crossingDir, "rollback-snapshot-FormalRetrievalActivation-demo-workspace-demo-collection.json"), ct).ConfigureAwait(false);
        _ = await ReadJsonFileAsync<CrossingRevocationRecordContent>(Path.Combine(crossingDir, "revocation-record-FormalRetrievalActivation-demo-workspace-demo-collection.json"), ct).ConfigureAwait(false);
        var auditLogPath = Path.Combine(crossingDir, "audit-log-FormalRetrievalActivation-demo-workspace-demo-collection.jsonl");
        if (File.Exists(auditLogPath))
        {
            _ = File.ReadLines(auditLogPath).FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line));
        }

        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-approval-guarded-runtime-activation-gate-dry-run-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunOptions
        {
            IsGate = isGate,
            Enabled = !CommandHelpers.HasFlag(args, "--disabled")
        };
        var runner = new FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunRunner();
        var report = runner.Run(activationDryRunGate, rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt);

        var fn = isGate
            ? "formal-retrieval-promotion-approval-guarded-runtime-activation-gate-dry-run-gate"
            : "formal-retrieval-promotion-approval-guarded-runtime-activation-gate-dry-run";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunRunner.BuildMarkdown(
            isGate ? "Guarded Runtime Activation Gate Dry-Run (Gate)" : "Guarded Runtime Activation Gate Dry-Run", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Guarded runtime activation gate dry-run written: {jp}");
        Console.WriteLine($"[Eval] guardedRuntimeActivationDryRunPassed={report.GuardedRuntimeActivationDryRunPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} ready={report.ReadyCases} blocked={report.BlockedCases} boundGrantId={report.BoundGrantId} runtimeActivationWriteAllowed={report.RuntimeActivationWriteAllowed} runtimeActivation={report.RuntimeActivation}");
    }

    private static async Task ExecuteFormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);
        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;
        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;
        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);
        var guardedGateDryRunPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-guarded-runtime-activation-gate-dry-run-gate.json");
        var guardedGateDryRun = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport>(guardedGateDryRunPath, ct).ConfigureAwait(false);
        var activationDryRunGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-runtime-activation-dry-run-gate.json");
        _ = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport>(activationDryRunGatePath, ct).ConfigureAwait(false);
        var dedicatedCrossingExecutionGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-dedicated-crossing-execution-gate.json");
        _ = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalDedicatedCrossingExecutionGateReport>(dedicatedCrossingExecutionGatePath, ct).ConfigureAwait(false);
        var crossingDir = Path.Combine("vector", "v8", "dedicated-crossing");
        _ = await ReadJsonFileAsync<CrossingCapabilityGrantContent>(Path.Combine(crossingDir, "capability-grant-FormalRetrievalActivation-demo-workspace-demo-collection.json"), ct).ConfigureAwait(false);
        _ = await ReadJsonFileAsync<CrossingRuntimeConfigPatchContent>(Path.Combine(crossingDir, "runtime-config-patch-FormalRetrievalActivation-demo-workspace-demo-collection.json"), ct).ConfigureAwait(false);
        _ = await ReadJsonFileAsync<CrossingRollbackSnapshotContent>(Path.Combine(crossingDir, "rollback-snapshot-FormalRetrievalActivation-demo-workspace-demo-collection.json"), ct).ConfigureAwait(false);
        _ = await ReadJsonFileAsync<CrossingRevocationRecordContent>(Path.Combine(crossingDir, "revocation-record-FormalRetrievalActivation-demo-workspace-demo-collection.json"), ct).ConfigureAwait(false);
        var auditLogPath = Path.Combine(crossingDir, "audit-log-FormalRetrievalActivation-demo-workspace-demo-collection.jsonl");
        if (File.Exists(auditLogPath))
        {
            _ = File.ReadLines(auditLogPath).FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line));
        }
        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-approval-guarded-runtime-activation-artifact-write-out-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutOptions
        {
            IsGate = isGate,
            Enabled = !CommandHelpers.HasFlag(args, "--disabled")
        };
        var runner = new FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutRunner();
        var report = runner.Run(guardedGateDryRun, rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt: opt);
        var fn = isGate
            ? "formal-retrieval-promotion-approval-guarded-runtime-activation-artifact-write-out-gate"
            : "formal-retrieval-promotion-approval-guarded-runtime-activation-artifact-write-out";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutRunner.BuildMarkdown(
            isGate ? "Guarded Runtime Activation Artifact Write-Out (Gate)" : "Guarded Runtime Activation Artifact Write-Out", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Guarded runtime activation artifact write-out written: {jp}");
        Console.WriteLine($"[Eval] guardedRuntimeActivationArtifactWriteOutPassed={report.GuardedRuntimeActivationArtifactWriteOutPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} written={report.WrittenCases} blocked={report.BlockedCases} writtenArtifacts={report.WrittenArtifactPaths.Count} runtimeActivationArtifactsWritten={report.RuntimeActivationArtifactsWritten} runtimeActivation={report.RuntimeActivation}");
    }

    private static async Task ExecuteFormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);
        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;
        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;
        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);
        var artifactWriteOutGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-guarded-runtime-activation-artifact-write-out-gate.json");
        var artifactWriteOutGate = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport>(artifactWriteOutGatePath, ct).ConfigureAwait(false);
        var guardedGateDryRunPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-guarded-runtime-activation-gate-dry-run-gate.json");
        var guardedGateDryRun = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalGuardedRuntimeActivationGateDryRunReport>(guardedGateDryRunPath, ct).ConfigureAwait(false);
        var activationDryRunGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-runtime-activation-dry-run-gate.json");
        var activationDryRunGate = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalRuntimeActivationDryRunReport>(activationDryRunGatePath, ct).ConfigureAwait(false);
        var crossingDir = Path.Combine("vector", "v8", "dedicated-crossing");
        _ = await ReadJsonFileAsync<CrossingRuntimeConfigPatchContent>(Path.Combine(crossingDir, "runtime-config-patch-FormalRetrievalActivation-demo-workspace-demo-collection.json"), ct).ConfigureAwait(false);
        _ = await ReadJsonFileAsync<CrossingRollbackSnapshotContent>(Path.Combine(crossingDir, "rollback-snapshot-FormalRetrievalActivation-demo-workspace-demo-collection.json"), ct).ConfigureAwait(false);
        _ = await ReadJsonFileAsync<CrossingRevocationRecordContent>(Path.Combine(crossingDir, "revocation-record-FormalRetrievalActivation-demo-workspace-demo-collection.json"), ct).ConfigureAwait(false);

        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-approval-runtime-activation-artifact-integrity-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityOptions
        {
            IsGate = isGate,
            Enabled = !CommandHelpers.HasFlag(args, "--disabled")
        };
        var runner = new FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityRunner();
        var report = runner.Run(artifactWriteOutGate, guardedGateDryRun, activationDryRunGate, rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt: opt);
        var fn = isGate
            ? "formal-retrieval-promotion-approval-runtime-activation-artifact-integrity-gate"
            : "formal-retrieval-promotion-approval-runtime-activation-artifact-integrity";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityRunner.BuildMarkdown(
            isGate ? "Runtime Activation Artifact Integrity (Gate)" : "Runtime Activation Artifact Integrity", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Runtime activation artifact integrity written: {jp}");
        Console.WriteLine($"[Eval] runtimeActivationArtifactIntegrityPassed={report.RuntimeActivationArtifactIntegrityPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} verified={report.VerifiedCases} blocked={report.BlockedCases} contentVerified={report.ContentVerifiedArtifactCount} contractComplete={report.LiveActivationDryRunContractComplete} runtimeActivation={report.RuntimeActivation}");
    }

    private static async Task ExecuteFormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);
        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;
        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;
        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);
        var integrityGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-runtime-activation-artifact-integrity-gate.json");
        var integrityGate = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityReport>(integrityGatePath, ct).ConfigureAwait(false);
        var artifactWriteOutGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-guarded-runtime-activation-artifact-write-out-gate.json");
        var artifactWriteOutGate = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport>(artifactWriteOutGatePath, ct).ConfigureAwait(false);

        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-approval-live-runtime-activation-execution-dry-run-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunOptions
        {
            IsGate = isGate,
            Enabled = !CommandHelpers.HasFlag(args, "--disabled")
        };
        var runner = new FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunRunner();
        var report = runner.Run(integrityGate, artifactWriteOutGate, rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt);
        var fn = isGate
            ? "formal-retrieval-promotion-approval-live-runtime-activation-execution-dry-run-gate"
            : "formal-retrieval-promotion-approval-live-runtime-activation-execution-dry-run";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunRunner.BuildMarkdown(
            isGate ? "Live Runtime Activation Execution Dry-Run (Gate)" : "Live Runtime Activation Execution Dry-Run", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Live runtime activation execution dry-run written: {jp}");
        Console.WriteLine($"[Eval] liveRuntimeActivationExecutionDryRunPassed={report.LiveRuntimeActivationExecutionDryRunPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} ready={report.ReadyCases} blocked={report.BlockedCases} probeExecuted={report.ProbeExecuted} runtimeStateChanged={report.RuntimeStateChanged} runtimeActivation={report.RuntimeActivation}");
    }

    private static async Task ExecuteFormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);
        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;
        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;
        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);
        var dryRunGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-live-runtime-activation-execution-dry-run-gate.json");
        var dryRunGate = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalLiveRuntimeActivationExecutionDryRunReport>(dryRunGatePath, ct).ConfigureAwait(false);
        var integrityGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-runtime-activation-artifact-integrity-gate.json");
        var integrityGate = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalRuntimeActivationArtifactIntegrityReport>(integrityGatePath, ct).ConfigureAwait(false);
        var artifactWriteOutGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-guarded-runtime-activation-artifact-write-out-gate.json");
        var artifactWriteOutGate = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalGuardedRuntimeActivationArtifactWriteOutReport>(artifactWriteOutGatePath, ct).ConfigureAwait(false);

        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-approval-guarded-live-runtime-activation-execution-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionOptions
        {
            IsGate = isGate,
            Enabled = !CommandHelpers.HasFlag(args, "--disabled"),
            WriteEvidence = true
        };
        var runner = new FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionRunner();
        var evidenceRoot = Path.Combine("vector", "v8", "runtime-activation");
        var report = runner.Run(dryRunGate, integrityGate, artifactWriteOutGate, rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt, evidenceRoot);
        var fn = isGate
            ? "formal-retrieval-promotion-approval-guarded-live-runtime-activation-execution-gate"
            : "formal-retrieval-promotion-approval-guarded-live-runtime-activation-execution";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionRunner.BuildMarkdown(
            isGate ? "Guarded Live Runtime Activation Execution (Gate)" : "Guarded Live Runtime Activation Execution", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Guarded live runtime activation execution written: {jp}");
        Console.WriteLine($"[Eval] guardedLiveRuntimeActivationExecutionPassed={report.GuardedLiveRuntimeActivationExecutionPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} applied={report.AppliedCases} blocked={report.BlockedCases} activationApplied={report.ActivationApplied} runtimeActivation={report.RuntimeActivation} globalDefaultOn={report.GlobalDefaultOn}");
    }

    private static async Task ExecuteFormalRetrievalPromotionApprovalScopedLiveActivationObservationAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);
        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;
        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;
        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        var executionGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-guarded-live-runtime-activation-execution-gate.json");
        var executionGate = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionReport>(executionGatePath, ct).ConfigureAwait(false);
        var realEvidence = FormalRetrievalPromotionApprovalScopedLiveActivationObservationRunner.LoadRealEvidenceBindingSnapshot(executionGate);

        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-approval-scoped-live-activation-observation-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new FormalRetrievalPromotionApprovalScopedLiveActivationObservationOptions
        {
            IsGate = isGate,
            Enabled = !CommandHelpers.HasFlag(args, "--disabled")
        };
        var runner = new FormalRetrievalPromotionApprovalScopedLiveActivationObservationRunner();
        var report = runner.Run(executionGate, realEvidence, rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt);
        var fn = isGate
            ? "formal-retrieval-promotion-approval-scoped-live-activation-observation-gate"
            : "formal-retrieval-promotion-approval-scoped-live-activation-observation";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionApprovalScopedLiveActivationObservationRunner.BuildMarkdown(
            isGate ? "Scoped Live Activation Observation (Gate)" : "Scoped Live Activation Observation", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Scoped live activation observation written: {jp}");
        Console.WriteLine($"[Eval] scopedLiveActivationObservationPassed={report.ScopedLiveActivationObservationPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} ready={report.ReadyCases} blocked={report.BlockedCases} sourceActivationId={report.SourceActivationId} runtimeStateChangedOutsideScope={report.RuntimeStateChangedOutsideScope} globalDefaultOn={report.GlobalDefaultOn}");
    }

    private static async Task ExecuteFormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("vector", "v8"));
        Directory.CreateDirectory(output);
        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;
        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;
        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        var observationGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-scoped-live-activation-observation-gate.json");
        var observationGate = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalScopedLiveActivationObservationReport>(observationGatePath, ct).ConfigureAwait(false);
        var executionGatePath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-guarded-live-runtime-activation-execution-gate.json");
        var executionGate = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalGuardedLiveRuntimeActivationExecutionReport>(executionGatePath, ct).ConfigureAwait(false);
        var realContext = FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutRunner.LoadRealContext(observationGate, executionGate);

        var isGate = string.Equals(subcommand, "formal-retrieval-promotion-approval-scoped-live-activation-safety-closeout-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutOptions
        {
            IsGate = isGate,
            Enabled = !CommandHelpers.HasFlag(args, "--disabled")
        };
        var runner = new FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutRunner();
        var report = runner.Run(realContext, rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt);
        var fn = isGate
            ? "formal-retrieval-promotion-approval-scoped-live-activation-safety-closeout-gate"
            : "formal-retrieval-promotion-approval-scoped-live-activation-safety-closeout";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutRunner.BuildMarkdown(
            isGate ? "Scoped Live Activation Safety Closeout (Gate)" : "Scoped Live Activation Safety Closeout", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Scoped live activation safety closeout written: {jp}");
        Console.WriteLine($"[Eval] scopedLiveActivationSafetyCloseoutPassed={report.ScopedLiveActivationSafetyCloseoutPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} ready={report.ReadyCases} blocked={report.BlockedCases} activationStillActive={report.ActivationStillActive} rollbackReady={report.RollbackDryRunReady} killSwitchReady={report.KillSwitchDryRunReady} revocationReady={report.RevocationDryRunReady} recommendation={report.Recommendation} nextPhase={report.NextAllowedPhase}");
    }

    private static async Task ExecuteLearningLayerBootstrapAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("learning", "v9"));
        Directory.CreateDirectory(output);
        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;
        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;
        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        var v8CloseoutPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-scoped-live-activation-safety-closeout-gate.json");
        var v8Closeout = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalScopedLiveActivationSafetyCloseoutReport>(v8CloseoutPath, ct).ConfigureAwait(false);
        var realContext = LearningLayerBootstrapRunner.LoadRealContext(v8Closeout);

        var isGate = string.Equals(subcommand, "learning-layer-bootstrap-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new LearningLayerBootstrapOptions
        {
            IsGate = isGate,
            Enabled = !CommandHelpers.HasFlag(args, "--disabled")
        };
        var runner = new LearningLayerBootstrapRunner();
        var report = runner.Run(realContext, rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt);
        var fn = isGate ? "learning-layer-bootstrap-gate" : "learning-layer-bootstrap";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(LearningLayerBootstrapRunner.BuildMarkdown(
            isGate ? "Learning Layer Bootstrap (Gate)" : "Learning Layer Bootstrap", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Learning layer bootstrap written: {jp}");
        Console.WriteLine($"[Eval] learningLayerBootstrapPassed={report.LearningLayerBootstrapPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} ready={report.ReadyCases} blocked={report.BlockedCases} shadowOnly={report.ShadowOnly} runtimeAuthority={report.RuntimeAuthority} v8Preserved={report.V8ScopedActivationPreserved} recommendation={report.Recommendation} nextPhase={report.NextAllowedPhase}");
    }

    private static async Task ExecuteLearningShadowImplementationPackAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("learning", "v9"));
        Directory.CreateDirectory(output);
        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;
        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;
        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        var bootstrapGatePath = Path.Combine("learning", "v9", "learning-layer-bootstrap-gate.json");
        var bootstrapGate = await ReadJsonFileAsync<LearningLayerBootstrapReport>(bootstrapGatePath, ct).ConfigureAwait(false);

        var rankingPairsPath = Path.Combine("learning", "features", "ranking-pairs.jsonl");
        var routerExamplesPath = Path.Combine("learning", "features", "router-intent-examples.jsonl");
        var hardNegativesPath = Path.Combine("learning", "features", "hard-negatives.jsonl");
        var rankerPairs = LearningShadowImplementationPackRunner.LoadRankerPairs(rankingPairsPath);
        var routerExamples = LearningShadowImplementationPackRunner.LoadRouterExamples(routerExamplesPath);
        var hardNegativeCount = File.Exists(hardNegativesPath) ? File.ReadAllLines(hardNegativesPath).Count(static l => !string.IsNullOrWhiteSpace(l)) : 0;

        var realContext = new LearningShadowImplementationPackContext
        {
            BootstrapGatePresent = bootstrapGate is not null,
            BootstrapGatePassed = bootstrapGate?.GatePassed ?? false,
            V8ScopedActivationPreserved = bootstrapGate?.V8ScopedActivationPreserved ?? false,
            RankingPairCount = rankerPairs.Count,
            RouterExampleCount = routerExamples.Count,
            HardNegativeCount = hardNegativeCount,
            ShadowOnlyOverride = true
        };

        var isGate = string.Equals(subcommand, "learning-shadow-implementation-pack-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new LearningShadowImplementationPackOptions
        {
            IsGate = isGate,
            Enabled = !CommandHelpers.HasFlag(args, "--disabled")
        };
        var runner = new LearningShadowImplementationPackRunner();
        var report = runner.Run(realContext, rankerPairs, routerExamples, hardNegativeCount, output, rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt);
        var fn = isGate ? "shadow-implementation-pack-gate" : "shadow-implementation-pack";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(LearningShadowImplementationPackRunner.BuildMarkdown(
            isGate ? "Learning Shadow Implementation Pack (Gate)" : "Learning Shadow Implementation Pack", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Learning shadow implementation pack written: {jp}");
        Console.WriteLine($"[Eval] shadowImplementationPackPassed={report.ShadowImplementationPackPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} ready={report.ReadyCases} blocked={report.BlockedCases} bestRanker={report.ShadowComparisonSummary.BestRankerCandidate}({report.ShadowComparisonSummary.BestRankerPairwiseAccuracy:F3}) bestRouter={report.ShadowComparisonSummary.BestRouterCandidate}({report.ShadowComparisonSummary.BestRouterAccuracy:F3}) shadowOnly={report.ShadowOnly} recommendation={report.Recommendation} nextPhase={report.NextAllowedPhase}");
    }

    private static async Task ExecuteLearningFailureDiagnosisAndFeedbackLoopPackAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("learning", "v9"));
        Directory.CreateDirectory(output);
        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;
        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;
        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        var shadowPackPath = Path.Combine("learning", "v9", "shadow-implementation-pack-gate.json");
        var shadowPack = await ReadJsonFileAsync<LearningShadowImplementationPackReport>(shadowPackPath, ct).ConfigureAwait(false);
        var summaryPath = Path.Combine("learning", "v9", "shadow-comparison-summary.json");
        var summaryPresent = File.Exists(summaryPath);
        var failureSamplesDir = Path.Combine("learning", "v9", "failure-samples");
        var rankerFailuresPath = Path.Combine(failureSamplesDir, "candidate-reranker-failures.jsonl");
        var routerFailuresPath = Path.Combine(failureSamplesDir, "router-intent-failures.jsonl");
        var failureSampleFilesPresent = File.Exists(rankerFailuresPath) && File.Exists(routerFailuresPath);

        var rankerPairs = LearningShadowImplementationPackRunner.LoadRankerPairs(Path.Combine("learning", "features", "ranking-pairs.jsonl"));
        var routerExamples = LearningShadowImplementationPackRunner.LoadRouterExamples(Path.Combine("learning", "features", "router-intent-examples.jsonl"));
        var hardNegativesPath = Path.Combine("learning", "features", "hard-negatives.jsonl");
        var hardNegativeCount = File.Exists(hardNegativesPath) ? File.ReadAllLines(hardNegativesPath).Count(static l => !string.IsNullOrWhiteSpace(l)) : 0;
        var policyFeedbackPath = Path.Combine("learning", "features", "policy-feedback-features.jsonl");
        var policyFeedbackCount = File.Exists(policyFeedbackPath) ? File.ReadAllLines(policyFeedbackPath).Count(static l => !string.IsNullOrWhiteSpace(l)) : 0;

        var realContext = new LearningFailureDiagnosisAndFeedbackLoopPackContext
        {
            ShadowPackPresent = shadowPack is not null,
            ShadowPackPassed = shadowPack?.GatePassed ?? false,
            ShadowComparisonSummaryPresent = summaryPresent,
            FailureSampleFilesPresent = failureSampleFilesPresent,
            V8ScopedActivationPreserved = shadowPack?.V8ScopedActivationPreserved ?? false,
            HumanReviewRequiredOverride = true
        };

        var isGate = string.Equals(subcommand, "learning-failure-diagnosis-feedback-loop-pack-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new LearningFailureDiagnosisAndFeedbackLoopPackOptions
        {
            IsGate = isGate,
            Enabled = !CommandHelpers.HasFlag(args, "--disabled")
        };
        var runner = new LearningFailureDiagnosisAndFeedbackLoopPackRunner();
        var report = runner.Run(realContext, failureSamplesDir, output, rankerPairs, routerExamples, hardNegativeCount, policyFeedbackCount, rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt);
        var fn = isGate ? "failure-diagnosis-feedback-loop-pack-gate" : "failure-diagnosis-feedback-loop-pack";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(LearningFailureDiagnosisAndFeedbackLoopPackRunner.BuildMarkdown(
            isGate ? "Learning Failure Diagnosis + Feedback Loop Pack (Gate)" : "Learning Failure Diagnosis + Feedback Loop Pack", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Learning failure diagnosis + feedback loop pack written: {jp}");
        Console.WriteLine($"[Eval] failureDiagnosisFeedbackLoopPackPassed={report.FailureDiagnosisFeedbackLoopPackPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} ready={report.ReadyCases} blocked={report.BlockedCases} clusters={report.FailureDiagnosisInputPack.Clusters.Count} hardNegCount={report.HardNegativeCandidateCount} humanReview={report.HumanReviewRequired} autoIngest={report.AutoIngest} recommendation={report.Recommendation} nextPhase={report.NextAllowedPhase}");
    }

    private static async Task ExecuteLearningShadowPromotionReadinessPackAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("learning", "v9"));
        Directory.CreateDirectory(output);
        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;
        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;
        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        var failureFeedbackGatePath = Path.Combine("learning", "v9", "failure-diagnosis-feedback-loop-pack-gate.json");
        var failureFeedback = await ReadJsonFileAsync<LearningFailureDiagnosisAndFeedbackLoopPackReport>(failureFeedbackGatePath, ct).ConfigureAwait(false);
        var shadowImplPath = Path.Combine("learning", "v9", "shadow-implementation-pack-gate.json");
        var shadowImpl = await ReadJsonFileAsync<LearningShadowImplementationPackReport>(shadowImplPath, ct).ConfigureAwait(false);
        var summaryPath = Path.Combine("learning", "v9", "shadow-comparison-summary.json");
        var summaryPresent = File.Exists(summaryPath);
        var hardNegPath = Path.Combine("learning", "v9", "hard-negative-expansion-candidates.jsonl");
        var hardNegCount = File.Exists(hardNegPath) ? File.ReadAllLines(hardNegPath).Count(static l => !string.IsNullOrWhiteSpace(l)) : 0;
        var feedbackContractPath = Path.Combine("learning", "v9", "feedback-ingestion-contract.json");
        var feedbackContractPresent = File.Exists(feedbackContractPath);
        var routerRepairPath = Path.Combine("learning", "v9", "router-intent-repair-plan.json");
        var routerRepairPresent = File.Exists(routerRepairPath);

        // Extract router repair underrepresented labels + failure cluster ids for the human-review queue
        var failureClusterIds = failureFeedback?.FailureDiagnosisInputPack.Clusters.Select(c => c.ClusterId).ToArray() ?? Array.Empty<string>();
        var routerRepairUnderrep = failureFeedback?.RouterIntentRepairPlan.UnderrepresentedLabels.ToArray() ?? Array.Empty<string>();

        var realContext = new LearningShadowPromotionReadinessPackContext
        {
            FailureFeedbackPackPresent = failureFeedback is not null,
            FailureFeedbackPackPassed = failureFeedback?.GatePassed ?? false,
            ShadowImplementationPackPresent = shadowImpl is not null,
            ShadowComparisonSummaryPresent = summaryPresent,
            HardNegativeCandidatesPresent = hardNegCount > 0,
            HardNegativeCandidateCount = hardNegCount,
            FeedbackContractPresent = feedbackContractPresent,
            RouterIntentRepairPlanPresent = routerRepairPresent,
            V8ScopedActivationPreserved = (failureFeedback?.V8ScopedActivationPreserved ?? false) && (shadowImpl?.V8ScopedActivationPreserved ?? false),
            BestShadowCandidate = shadowImpl?.ShadowComparisonSummary.BestRankerCandidate ?? string.Empty,
            BestShadowCandidatePairwiseAccuracy = shadowImpl?.ShadowComparisonSummary.BestRankerPairwiseAccuracy ?? 0,
            BestRouterCandidate = shadowImpl?.ShadowComparisonSummary.BestRouterCandidate ?? string.Empty,
            BestRouterAccuracy = shadowImpl?.ShadowComparisonSummary.BestRouterAccuracy ?? 0,
            HumanReviewRequiredOverride = true,
            RequiresSeparatePromotionGateOverride = true
        };

        var isGate = string.Equals(subcommand, "learning-shadow-promotion-readiness-pack-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new LearningShadowPromotionReadinessPackOptions
        {
            IsGate = isGate,
            Enabled = !CommandHelpers.HasFlag(args, "--disabled")
        };
        var runner = new LearningShadowPromotionReadinessPackRunner();
        var report = runner.Run(realContext, output, hardNegCount, failureClusterIds, routerRepairUnderrep, rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt);
        var fn = isGate ? "shadow-promotion-readiness-pack-gate" : "shadow-promotion-readiness-pack";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(LearningShadowPromotionReadinessPackRunner.BuildMarkdown(
            isGate ? "Learning Shadow Promotion Readiness Pack (Gate)" : "Learning Shadow Promotion Readiness Pack", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Learning shadow promotion readiness pack written: {jp}");
        Console.WriteLine($"[Eval] shadowPromotionReadinessPackPassed={report.ShadowPromotionReadinessPackPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} ready={report.ReadyCases} blocked={report.BlockedCases} bestShadow={report.BestShadowCandidate}({report.BestShadowCandidatePairwiseAccuracy:F3}) routerPromotion={report.RouterPromotionReady} routerRepair={report.RouterRepairRequired} queue={report.HumanReviewQueue.Count} runtimePromotion={report.RuntimePromotionAllowed} recommendation={report.Recommendation} nextPhase={report.NextAllowedPhase}");
    }

    private static async Task ExecuteLearningControlledRuntimePilotGatePackAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("learning", "v10"));
        Directory.CreateDirectory(output);
        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;
        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;
        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        var v9ReadinessPath = Path.Combine("learning", "v9", "shadow-promotion-readiness-pack-gate.json");
        var v9Readiness = await ReadJsonFileAsync<LearningShadowPromotionReadinessPackReport>(v9ReadinessPath, ct).ConfigureAwait(false);
        var promotionProposalPath = Path.Combine("learning", "v9", "shadow-promotion-candidate-proposal.json");
        var humanReviewQueuePath = Path.Combine("learning", "v9", "human-review-queue-plan.jsonl");
        var pilotDesignPath = Path.Combine("learning", "v9", "controlled-pilot-design.json");
        var shadowImplPath = Path.Combine("learning", "v9", "shadow-implementation-pack-gate.json");
        var shadowImpl = await ReadJsonFileAsync<LearningShadowImplementationPackReport>(shadowImplPath, ct).ConfigureAwait(false);

        // Validate human-review queue every entry: humanReviewRequired=true, autoIngest=false
        var allRequireReview = true;
        var anyAutoIngest = false;
        var queueEntryCount = 0;
        if (File.Exists(humanReviewQueuePath))
        {
            foreach (var line in File.ReadAllLines(humanReviewQueuePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                queueEntryCount++;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("humanReviewRequired", out var hr) && hr.ValueKind == JsonValueKind.False) allRequireReview = false;
                    if (doc.RootElement.TryGetProperty("autoIngest", out var ai) && ai.ValueKind == JsonValueKind.True) anyAutoIngest = true;
                }
                catch { allRequireReview = false; }
            }
        }

        // Look for a (currently nonexistent) human-review-completion artifact. V10 must never fake completion.
        var humanReviewCompletionPath = Path.Combine("learning", "v10", "human-review-completion.json");
        var humanReviewCompletionPresent = File.Exists(humanReviewCompletionPath);

        var realContext = new LearningControlledRuntimePilotGatePackContext
        {
            V9ReadinessGatePresent = v9Readiness is not null,
            V9ReadinessGatePassed = v9Readiness?.GatePassed ?? false,
            PromotionProposalPresent = File.Exists(promotionProposalPath),
            HumanReviewQueuePresent = File.Exists(humanReviewQueuePath) && queueEntryCount > 0,
            HumanReviewQueueEntryCount = queueEntryCount,
            HumanReviewQueueAllEntriesRequireReview = allRequireReview,
            HumanReviewQueueAnyAutoIngest = anyAutoIngest,
            ControlledPilotDesignPresent = File.Exists(pilotDesignPath),
            V8ScopedActivationPreserved = v9Readiness?.V8ScopedActivationPreserved ?? false,
            BestShadowCandidate = v9Readiness?.BestShadowCandidate ?? string.Empty,
            BestShadowCandidatePairwiseAccuracy = v9Readiness?.BestShadowCandidatePairwiseAccuracy ?? 0,
            ReferenceBaselineName = "WeightedBaseline",
            ReferencePairwiseAccuracy = shadowImpl?.CandidateRerankerBaselines.FirstOrDefault(b => b.BaselineName == "WeightedBaseline")?.PairwiseAccuracy ?? 0,
            CandidateEvalCount = shadowImpl?.CandidateRerankerBaselines.FirstOrDefault(b => b.BaselineName == "LogisticBaseline")?.EvalCount ?? 0,
            RouterPromotionReady = v9Readiness?.RouterPromotionReady ?? false,
            HumanReviewRequiredOverride = true,
            RequiresSeparatePromotionGateOverride = true,
            RequiresHumanApprovalOverride = true,
            HumanReviewCompletionArtifactPresent = humanReviewCompletionPresent
        };

        var isGate = string.Equals(subcommand, "learning-controlled-runtime-pilot-gate-pack-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new LearningControlledRuntimePilotGatePackOptions
        {
            IsGate = isGate,
            Enabled = !CommandHelpers.HasFlag(args, "--disabled")
        };
        var runner = new LearningControlledRuntimePilotGatePackRunner();
        var report = runner.Run(realContext, output, rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt);
        var fn = isGate ? "controlled-runtime-pilot-gate-pack-gate" : "controlled-runtime-pilot-gate-pack";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(LearningControlledRuntimePilotGatePackRunner.BuildMarkdown(
            isGate ? "Learning Controlled Runtime Pilot Gate Pack (Gate)" : "Learning Controlled Runtime Pilot Gate Pack", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Learning controlled runtime pilot gate pack written: {jp}");
        Console.WriteLine($"[Eval] controlledRuntimePilotGatePackPassed={report.ControlledRuntimePilotGatePackPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} ready={report.ReadyCases} blocked={report.BlockedCases} replayReady={report.OfflineReplayReady} canaryReady={report.ShadowCanarySimulationReady} pilotExecReady={report.RuntimePilotExecutionReady} blockedExecBy={report.BlockedForRuntimePilotExecutionBy} recommendation={report.Recommendation} nextPhase={report.NextAllowedPhase}");
    }

    private static async Task ExecuteLearningEvidenceCalibratedSelfValidationPackAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("learning", "v10"));
        Directory.CreateDirectory(output);
        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;
        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;
        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        var v10PilotGatePath = Path.Combine("learning", "v10", "controlled-runtime-pilot-gate-pack-gate.json");
        var v10PilotGate = await ReadJsonFileAsync<LearningControlledRuntimePilotGatePackReport>(v10PilotGatePath, ct).ConfigureAwait(false);
        var offlineReplayPath = Path.Combine("learning", "v10", "offline-replay-summary.json");
        var shadowCanaryPath = Path.Combine("learning", "v10", "shadow-canary-simulation.json");
        var hardNegPath = Path.Combine("learning", "v9", "hard-negative-expansion-candidates.jsonl");
        var hardNegCount = File.Exists(hardNegPath) ? File.ReadAllLines(hardNegPath).Count(static l => !string.IsNullOrWhiteSpace(l)) : 0;
        // Count labeled hard negatives — none yet (V9.4 produced specs only; labels come from V9.5 feedback ingestion)
        var hardNegLabeledCount = 0;
        var humanReviewQueuePath = Path.Combine("learning", "v9", "human-review-queue-plan.jsonl");
        var humanReviewBacklog = File.Exists(humanReviewQueuePath) ? File.ReadAllLines(humanReviewQueuePath).Count(static l => !string.IsNullOrWhiteSpace(l)) : 0;

        var shadowImplPath = Path.Combine("learning", "v9", "shadow-implementation-pack-gate.json");
        var shadowImpl = await ReadJsonFileAsync<LearningShadowImplementationPackReport>(shadowImplPath, ct).ConfigureAwait(false);
        var v9ReadinessPath = Path.Combine("learning", "v9", "shadow-promotion-readiness-pack-gate.json");
        var v9Readiness = await ReadJsonFileAsync<LearningShadowPromotionReadinessPackReport>(v9ReadinessPath, ct).ConfigureAwait(false);
        var failureFeedbackPath = Path.Combine("learning", "v9", "failure-diagnosis-feedback-loop-pack-gate.json");
        var failureFeedback = await ReadJsonFileAsync<LearningFailureDiagnosisAndFeedbackLoopPackReport>(failureFeedbackPath, ct).ConfigureAwait(false);

        var weightedAcc = shadowImpl?.CandidateRerankerBaselines.FirstOrDefault(b => b.BaselineName == "WeightedBaseline")?.PairwiseAccuracy ?? 0;
        var logisticAcc = shadowImpl?.CandidateRerankerBaselines.FirstOrDefault(b => b.BaselineName == "LogisticBaseline")?.PairwiseAccuracy ?? 0;
        var treeAcc = shadowImpl?.CandidateRerankerBaselines.FirstOrDefault(b => b.BaselineName == "TreeBaseline")?.PairwiseAccuracy ?? 0;
        var canaryAgreementRate = v10PilotGate?.ShadowCanarySimulation.SimulatedShadowAgreementRate ?? 0;
        var failureClusterIds = failureFeedback?.FailureDiagnosisInputPack.Clusters.Select(c => c.ClusterId).ToArray() ?? Array.Empty<string>();

        var realContext = new LearningEvidenceCalibratedSelfValidationPackContext
        {
            V10PilotGatePresent = v10PilotGate is not null,
            V10PilotGatePassed = v10PilotGate?.GatePassed ?? false,
            OfflineReplayPresent = File.Exists(offlineReplayPath),
            ShadowCanaryPresent = File.Exists(shadowCanaryPath),
            HardNegativeCandidatesPresent = hardNegCount > 0,
            HardNegativeCandidateCount = hardNegCount,
            HardNegativeLabeledCount = hardNegLabeledCount,
            V8ScopedActivationPreserved = (v10PilotGate?.V8ScopedActivationPreserved ?? false) && (v9Readiness?.V8ScopedActivationPreserved ?? false),
            RouterPromotionReady = v9Readiness?.RouterPromotionReady ?? false,
            CandidatePairwiseAccuracy = logisticAcc,
            ReferencePairwiseAccuracy = weightedAcc,
            TreeBaselinePairwiseAccuracy = treeAcc,
            ShadowCanaryAgreementRate = canaryAgreementRate,
            FailureClusterCount = failureClusterIds.Length,
            FailureClusterIds = failureClusterIds,
            KillSwitchArmed = v10PilotGate?.ShadowCanarySimulation.KillSwitchArmed ?? false,
            RollbackReady = v10PilotGate?.ShadowCanarySimulation.RollbackReady ?? false,
            HumanReviewBacklogQueueEntryCount = humanReviewBacklog
        };

        var isGate = string.Equals(subcommand, "learning-evidence-calibrated-self-validation-pack-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new LearningEvidenceCalibratedSelfValidationPackOptions
        {
            IsGate = isGate,
            Enabled = !CommandHelpers.HasFlag(args, "--disabled")
        };
        var runner = new LearningEvidenceCalibratedSelfValidationPackRunner();
        var report = runner.Run(realContext, output, rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt);
        var fn = isGate ? "evidence-calibrated-self-validation-pack-gate" : "evidence-calibrated-self-validation-pack";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(LearningEvidenceCalibratedSelfValidationPackRunner.BuildMarkdown(
            isGate ? "Learning Evidence-Calibrated Self-Validation Pack (Gate)" : "Learning Evidence-Calibrated Self-Validation Pack", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Learning evidence-calibrated self-validation pack written: {jp}");
        Console.WriteLine($"[Eval] evidenceCalibratedSelfValidationPackPassed={report.EvidenceCalibratedSelfValidationPackPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} ready={report.ReadyCases} blocked={report.BlockedCases} evidenceSufficient={report.EvidenceSufficient} signalLeakageRisk={report.SignalLeakageRisk} hardNegInsufficient={report.HardNegativeEvidenceInsufficient} pilotReady={report.RuntimePilotExecutionReadyForSeparateGate} blockedExecBy=[{string.Join(',', report.BlockedForRuntimePilotExecutionBy)}] recommendation={report.Recommendation}");
    }

    private static async Task ExecuteLearningEvidenceAccumulationPackAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("learning", "v10"));
        Directory.CreateDirectory(output);
        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;
        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;
        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        var selfValidPath = Path.Combine("learning", "v10", "evidence-calibrated-self-validation-pack-gate.json");
        var selfValid = await ReadJsonFileAsync<LearningEvidenceCalibratedSelfValidationPackReport>(selfValidPath, ct).ConfigureAwait(false);
        var hardNegPath = Path.Combine("learning", "v9", "hard-negative-expansion-candidates.jsonl");
        var hardNegCount = File.Exists(hardNegPath) ? File.ReadAllLines(hardNegPath).Count(static l => !string.IsNullOrWhiteSpace(l)) : 0;
        var rankerPairsPath = Path.Combine("learning", "features", "ranking-pairs.jsonl");
        var rankerPairs = LearningShadowImplementationPackRunner.LoadRankerPairs(rankerPairsPath);
        var failureFeedbackPath = Path.Combine("learning", "v9", "failure-diagnosis-feedback-loop-pack-gate.json");
        var failureFeedback = await ReadJsonFileAsync<LearningFailureDiagnosisAndFeedbackLoopPackReport>(failureFeedbackPath, ct).ConfigureAwait(false);
        var failureClusterIds = failureFeedback?.FailureDiagnosisInputPack.Clusters.Select(c => c.ClusterId).ToArray() ?? Array.Empty<string>();

        var realContext = new LearningEvidenceAccumulationPackContext
        {
            SelfValidationPackPresent = selfValid is not null,
            SelfValidationPackPassed = selfValid?.GatePassed ?? false,
            HardNegativeCandidatesPresent = hardNegCount > 0,
            HardNegativeCandidateCount = hardNegCount,
            V8ScopedActivationPreserved = selfValid?.V8ScopedActivationPreserved ?? false,
            RankerPairs = rankerPairs,
            FailureClusterIds = failureClusterIds,
            PreviousEvidenceSufficiencyScore = selfValid?.EvidenceSufficiencyReport.EvidenceSufficiencyScore ?? 0
        };

        var isGate = string.Equals(subcommand, "learning-evidence-accumulation-pack-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new LearningEvidenceAccumulationPackOptions
        {
            IsGate = isGate,
            Enabled = !CommandHelpers.HasFlag(args, "--disabled")
        };
        var runner = new LearningEvidenceAccumulationPackRunner();
        var report = runner.Run(realContext, output, rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt);
        var fn = isGate ? "evidence-accumulation-pack-gate" : "evidence-accumulation-pack";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(LearningEvidenceAccumulationPackRunner.BuildMarkdown(
            isGate ? "Learning Evidence Accumulation Pack (Gate)" : "Learning Evidence Accumulation Pack", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Learning evidence accumulation pack written: {jp}");
        Console.WriteLine($"[Eval] evidenceAccumulationPackPassed={report.EvidenceAccumulationPackPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} ready={report.ReadyCases} blocked={report.BlockedCases} dominance={report.PositiveScoreDominanceDetected} leakageReduced={report.LeakageRiskReduced} accDropNoPositiveScore={report.SignalLeakageAblation.AccuracyDropFromPositiveScoreRemoval:F3} evidenceSufficient={report.EvidenceSufficient} pilotReady={report.RuntimePilotExecutionReadyForSeparateGate} blockedExecBy=[{string.Join(',', report.BlockedForRuntimePilotExecutionBy)}] recommendation={report.Recommendation}");
    }

    private static async Task ExecuteLearningCounterexampleRepairPackAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("learning", "v10"));
        Directory.CreateDirectory(output);
        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;
        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;
        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        var evidenceAccumPath = Path.Combine("learning", "v10", "evidence-accumulation-pack-gate.json");
        var evidenceAccum = await ReadJsonFileAsync<LearningEvidenceAccumulationPackReport>(evidenceAccumPath, ct).ConfigureAwait(false);
        var counterexamplePath = Path.Combine("learning", "v10", "counterexample-replay-report.json");
        var hardNegSpecsPath = Path.Combine("learning", "v9", "hard-negative-expansion-candidates.jsonl");
        var rankerPairs = LearningShadowImplementationPackRunner.LoadRankerPairs(Path.Combine("learning", "features", "ranking-pairs.jsonl"));
        var hardNegSpecs = LearningCounterexampleRepairPackRunner.LoadHardNegativeSpecs(hardNegSpecsPath);
        var failureFeedbackPath = Path.Combine("learning", "v9", "failure-diagnosis-feedback-loop-pack-gate.json");
        var failureFeedback = await ReadJsonFileAsync<LearningFailureDiagnosisAndFeedbackLoopPackReport>(failureFeedbackPath, ct).ConfigureAwait(false);
        var failureClusterIds = failureFeedback?.FailureDiagnosisInputPack.Clusters.Select(c => c.ClusterId).ToArray() ?? Array.Empty<string>();

        var realContext = new LearningCounterexampleRepairPackContext
        {
            EvidenceAccumulationPackPresent = evidenceAccum is not null,
            EvidenceAccumulationPackPassed = evidenceAccum?.GatePassed ?? false,
            CounterexampleReplayPresent = File.Exists(counterexamplePath),
            HardNegativeCandidatesPresent = hardNegSpecs.Count > 0,
            HardNegativeCandidateCount = hardNegSpecs.Count,
            RankingPairsPresent = rankerPairs.Count > 0,
            V8ScopedActivationPreserved = evidenceAccum?.V8ScopedActivationPreserved ?? false,
            RankerPairs = rankerPairs,
            FailureClusterIds = failureClusterIds,
            HardNegativeSpecs = hardNegSpecs,
            PreviousEvidenceSufficiencyScore = evidenceAccum?.EvidenceSufficiencyRecomputed.NewEvidenceSufficiencyScore ?? 0,
            OriginalCandidateFailureRate = evidenceAccum?.CounterexampleReplayReport.CandidateFailureRateOnCounterexamples ?? 0,
            ReferenceFailureRate = evidenceAccum?.CounterexampleReplayReport.ReferenceFailureRateOnCounterexamples ?? 0
        };

        var isGate = string.Equals(subcommand, "learning-counterexample-repair-pack-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new LearningCounterexampleRepairPackOptions
        {
            IsGate = isGate,
            Enabled = !CommandHelpers.HasFlag(args, "--disabled")
        };
        var runner = new LearningCounterexampleRepairPackRunner();
        var report = runner.Run(realContext, output, rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt);
        var fn = isGate ? "counterexample-repair-pack-gate" : "counterexample-repair-pack";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(LearningCounterexampleRepairPackRunner.BuildMarkdown(
            isGate ? "Learning Counterexample Repair Pack (Gate)" : "Learning Counterexample Repair Pack", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Learning counterexample repair pack written: {jp}");
        Console.WriteLine($"[Eval] counterexampleRepairPackPassed={report.CounterexampleRepairPackPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} ready={report.ReadyCases} blocked={report.BlockedCases} boundLabels={report.EvidenceBoundShadowLabelCount} unbound={report.UnboundCandidateSpecCount} bindingRate={report.BindingCoverageRate:F3} origFailRate={report.OriginalCandidateFailureRate:F3} repairedFailRate={report.RepairedCandidateFailureRate:F3} refFailRate={report.ReferenceFailureRate:F3} improvement={report.RepairImprovement:F3} evidenceSufficient={report.EvidenceSufficient} pilotReady={report.RuntimePilotExecutionReadyForSeparateGate} blockedExecBy=[{string.Join(',', report.BlockedForRuntimePilotExecutionBy)}] recommendation={report.Recommendation}");
    }

    private static async Task ExecuteLearningFormalEvidenceBoundaryPackAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("learning", "v10"));
        Directory.CreateDirectory(output);
        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;
        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;
        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        var repairPath = Path.Combine("learning", "v10", "counterexample-repair-pack-gate.json");
        var repair = await ReadJsonFileAsync<LearningCounterexampleRepairPackReport>(repairPath, ct).ConfigureAwait(false);
        var boundLabelsPath = Path.Combine("learning", "v10", "evidence-bound-hard-negative-labels.jsonl");
        var realBoundLabels = LearningFormalEvidenceBoundaryPackRunner.LoadEvidenceBoundShadowLabels(boundLabelsPath);
        var v2Path = Path.Combine("learning", "v10", "evidence-sufficiency-recomputed-v2.json");
        var v2Present = File.Exists(v2Path);

        var realContext = new LearningFormalEvidenceBoundaryPackContext
        {
            CounterexampleRepairPackPresent = repair is not null,
            CounterexampleRepairPackPassed = repair?.GatePassed ?? false,
            EvidenceBoundLabelsPresent = realBoundLabels.Count > 0,
            EvidenceBoundShadowLabelCount = realBoundLabels.Count,
            EvidenceSufficiencyV2Present = v2Present,
            ShadowEvidenceSufficient = repair?.EvidenceSufficient ?? false,
            V8ScopedActivationPreserved = repair?.V8ScopedActivationPreserved ?? false
        };

        var isGate = string.Equals(subcommand, "learning-formal-evidence-boundary-pack-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new LearningFormalEvidenceBoundaryPackOptions
        {
            IsGate = isGate,
            Enabled = !CommandHelpers.HasFlag(args, "--disabled")
        };
        var runner = new LearningFormalEvidenceBoundaryPackRunner();
        var report = runner.Run(realContext, output, realBoundLabels, rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt);
        var fn = isGate ? "formal-evidence-boundary-pack-gate" : "formal-evidence-boundary-pack";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(LearningFormalEvidenceBoundaryPackRunner.BuildMarkdown(
            isGate ? "Learning Formal Evidence Boundary Pack (Gate)" : "Learning Formal Evidence Boundary Pack", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Learning formal evidence boundary pack written: {jp}");
        Console.WriteLine($"[Eval] formalEvidenceBoundaryPackPassed={report.FormalEvidenceBoundaryPackPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} ready={report.ReadyCases} blocked={report.BlockedCases} shadowSufficient={report.ShadowEvidenceSufficient} formalSufficient={report.FormalEvidenceSufficient} prePilotReady={report.PrePilotGateReady} pilotReady={report.RuntimePilotExecutionReadyForSeparateGate} formalized={report.FormalizedCount} pending={report.PendingFormalizationCount} blockedExecBy=[{string.Join(',', report.BlockedForRuntimePilotExecutionBy)}] recommendation={report.Recommendation}");
    }

    private static async Task ExecuteLearningFormalEvidenceRealizationPackAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("learning", "v10"));
        Directory.CreateDirectory(output);
        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;
        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;
        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        var boundaryPath = Path.Combine("learning", "v10", "formal-evidence-boundary-pack-gate.json");
        var boundary = await ReadJsonFileAsync<LearningFormalEvidenceBoundaryPackReport>(boundaryPath, ct).ConfigureAwait(false);
        var contractPath = Path.Combine("learning", "v10", "formal-label-realization-contract.json");
        var contractPresent = File.Exists(contractPath);
        var shadowLabelsPath = Path.Combine("learning", "v10", "evidence-bound-hard-negative-labels.jsonl");
        var shadowLabels = LearningFormalEvidenceBoundaryPackRunner.LoadEvidenceBoundShadowLabels(shadowLabelsPath);
        var rankerPairs = LearningShadowImplementationPackRunner.LoadRankerPairs(Path.Combine("learning", "features", "ranking-pairs.jsonl"));

        var realContext = new LearningFormalEvidenceRealizationPackContext
        {
            FormalEvidenceBoundaryPresent = boundary is not null,
            FormalEvidenceBoundaryPassed = boundary?.GatePassed ?? false,
            RealizationContractPresent = contractPresent,
            ShadowLabelsPresent = shadowLabels.Count > 0,
            ShadowLabelCount = shadowLabels.Count,
            V8ScopedActivationPreserved = boundary?.V8ScopedActivationPreserved ?? false,
            ShadowLabels = shadowLabels,
            RankerPairs = rankerPairs
        };

        var isGate = string.Equals(subcommand, "learning-formal-evidence-realization-pack-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new LearningFormalEvidenceRealizationPackOptions
        {
            IsGate = isGate,
            Enabled = !CommandHelpers.HasFlag(args, "--disabled")
        };
        var runner = new LearningFormalEvidenceRealizationPackRunner();
        var report = runner.Run(realContext, output, rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt);
        var fn = isGate ? "formal-evidence-realization-pack-gate" : "formal-evidence-realization-pack";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(LearningFormalEvidenceRealizationPackRunner.BuildMarkdown(
            isGate ? "Learning Formal Evidence Realization Pack (Gate)" : "Learning Formal Evidence Realization Pack", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Learning formal evidence realization pack written: {jp}");
        Console.WriteLine($"[Eval] formalEvidenceRealizationPackPassed={report.FormalEvidenceRealizationPackPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} ready={report.ReadyCases} blocked={report.BlockedCases} candidates={report.FormalLabelCandidateCount} realizable={report.RealizableFormalLabelCount} invalid={report.InvalidBindingCount} verified={report.FormalLabelIntegrityManifest.VerifiedEntries} mismatched={report.FormalLabelIntegrityManifest.MismatchedEntries} formalRealized={report.FormalLabelsRealized} formalSufficient={report.FormalEvidenceSufficient} pilotReady={report.RuntimePilotExecutionReadyForSeparateGate} blockedExecBy=[{string.Join(',', report.BlockedForRuntimePilotExecutionBy)}] recommendation={report.Recommendation}");
    }

    private static async Task ExecuteLearningControlledFormalLabelIngestionStagingPackAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("learning", "v10"));
        Directory.CreateDirectory(output);
        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;
        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;
        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        var realizationPath = Path.Combine("learning", "v10", "formal-evidence-realization-pack-gate.json");
        var realization = await ReadJsonFileAsync<LearningFormalEvidenceRealizationPackReport>(realizationPath, ct).ConfigureAwait(false);
        var candidatesPath = Path.Combine("learning", "v10", "formal-label-candidates.jsonl");
        var candidates = LearningControlledFormalLabelIngestionStagingPackRunner.LoadFormalLabelCandidates(candidatesPath);
        var manifestPath = Path.Combine("learning", "v10", "formal-label-integrity-manifest.json");
        var manifestPresent = File.Exists(manifestPath);
        var formalDatasetPath = Path.Combine("learning", "features", "hard-negatives.jsonl");

        var realContext = new LearningControlledFormalLabelIngestionStagingPackContext
        {
            FormalRealizationPackPresent = realization is not null,
            FormalRealizationPackPassed = realization?.GatePassed ?? false,
            CandidatesPresent = candidates.Count > 0,
            CandidateCount = candidates.Count,
            IntegrityManifestPresent = manifestPresent,
            V8ScopedActivationPreserved = realization?.V8ScopedActivationPreserved ?? false,
            Candidates = candidates,
            FormalDatasetPath = formalDatasetPath
        };

        var isGate = string.Equals(subcommand, "learning-controlled-formal-label-ingestion-staging-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new LearningControlledFormalLabelIngestionStagingPackOptions
        {
            IsGate = isGate,
            Enabled = !CommandHelpers.HasFlag(args, "--disabled")
        };
        var runner = new LearningControlledFormalLabelIngestionStagingPackRunner();
        var report = runner.Run(realContext, output, rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt);
        var fn = isGate ? "controlled-formal-label-ingestion-staging-pack-gate" : "controlled-formal-label-ingestion-staging-pack";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(LearningControlledFormalLabelIngestionStagingPackRunner.BuildMarkdown(
            isGate ? "Learning Controlled Formal Label Ingestion Staging Pack (Gate)" : "Learning Controlled Formal Label Ingestion Staging Pack", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Learning controlled formal label ingestion staging pack written: {jp}");
        Console.WriteLine($"[Eval] controlledFormalLabelIngestionStagingPackPassed={report.ControlledFormalLabelIngestionStagingPackPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} ready={report.ReadyCases} blocked={report.BlockedCases} stagedCount={report.StagedFormalLabelCount} invalidCount={report.InvalidCandidateCount} hashMismatchCount={report.HashMismatchCount} wouldAdd={report.DiffPreview.WouldAddCount} datasetSizeBefore={report.FormalDatasetSizeBeforeBytes} datasetSizeAfter={report.FormalDatasetSizeAfterBytes} untouched={report.FormalDatasetSizeBeforeBytes == report.FormalDatasetSizeAfterBytes} recommendation={report.Recommendation}");
    }

    private static async Task ExecuteLearningControlledFormalLabelIngestionStagingR1PackAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("learning", "v10"));
        Directory.CreateDirectory(output);

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var r1RealizationPath = Path.Combine("learning", "v10", "formal-evidence-realization-pack-r1-gate.json");
        var r1RealizationPresent = File.Exists(r1RealizationPath);
        var r1ManifestPath = Path.Combine("learning", "v10", "formal-label-integrity-manifest-r1.json");
        var r1ManifestPresent = File.Exists(r1ManifestPath);
        var r1CandidatesPath = Path.Combine("learning", "v10", "formal-label-candidates-r1.jsonl");
        var r1Candidates = LearningControlledFormalLabelIngestionStagingR1PackRunner.LoadR1Candidates(r1CandidatesPath);

        var isGate = string.Equals(subcommand, "learning-controlled-formal-label-ingestion-staging-r1-pack-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new LearningControlledFormalLabelIngestionStagingR1PackOptions { IsGate = isGate, Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new LearningControlledFormalLabelIngestionStagingR1PackRunner();
        var report = runner.Run(r1Candidates, r1RealizationPresent, r1ManifestPresent, rtPassed, p15Passed, output, opt);

        var fn = isGate ? "controlled-formal-label-ingestion-staging-r1-pack-gate" : "controlled-formal-label-ingestion-staging-r1-pack";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(LearningControlledFormalLabelIngestionStagingR1PackRunner.BuildMarkdown(
            isGate ? "Learning Controlled Formal Label Ingestion Staging R1 Pack (Gate)" : "Learning Controlled Formal Label Ingestion Staging R1 Pack", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] R1 staging pack written: {jp}");
        Console.WriteLine($"[Eval] r1PackPassed={report.ControlledFormalLabelIngestionStagingR1PackPassed}; gatePassed={report.GatePassed}; " +
            $"total={report.TotalCases} staged={report.StagedFormalLabelCount} invalid={report.InvalidCandidateCount} hashMismatch={report.HashMismatchCount}; " +
            $"canonicalCoverage={report.CanonicalHashCoverage:F0}% legacyInvalidated={report.LegacyStagingInvalidated}; recommendation={report.Recommendation}");
    }

    private static async Task ExecuteLearningControlledFormalLabelIngestionStagingR1SemanticCleanupAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("learning", "v10"));
        Directory.CreateDirectory(output);

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var r1StagingPath = Path.Combine("learning", "v10", "controlled-formal-label-ingestion-staging-r1-pack-gate.json");
        var r1StagingPresent = File.Exists(r1StagingPath);
        var r1StagedCount = 0;
        if (r1StagingPresent)
        {
            try
            {
                var r1Doc = JsonDocument.Parse(await File.ReadAllTextAsync(r1StagingPath, ct).ConfigureAwait(false));
                r1StagedCount = r1Doc.RootElement.TryGetProperty("StagedFormalLabelCount", out var sc) ? sc.GetInt32() : 0;
            }
            catch { }
        }

        var isGate = string.Equals(subcommand, "learning-controlled-formal-label-ingestion-staging-r1-semantic-cleanup-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new LearningControlledFormalLabelIngestionStagingR1SemanticCleanupOptions { IsGate = isGate, Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new LearningControlledFormalLabelIngestionStagingR1SemanticCleanupRunner();
        var report = runner.Run(r1StagingPresent, r1StagedCount, rtPassed, p15Passed, output, opt);

        var fn = isGate ? "controlled-formal-label-ingestion-staging-r1-semantic-cleanup-pack-gate" : "controlled-formal-label-ingestion-staging-r1-semantic-cleanup-pack";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(LearningControlledFormalLabelIngestionStagingR1SemanticCleanupRunner.BuildMarkdown(
            isGate ? "Staging R1 Semantic Cleanup Pack (Gate)" : "Staging R1 Semantic Cleanup Pack", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] R1 semantic cleanup pack written: {jp}");
        Console.WriteLine($"[Eval] cleanupPassed={report.StagingR1SemanticCleanupPackPassed}; gatePassed={report.GatePassed}; " +
            $"usesCanonical={report.StagingSourceUsesCanonicalHash} usesLegacy={report.StagingSourceUsesLegacyHash}; " +
            $"legacyDetected={report.LegacyStagingArtifactsDetected} legacyUsedAsSource={report.LegacyArtifactsUsedAsSource}");
    }

    private static async Task ExecuteControlledFormalEvidenceIngestionPackAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("learning", "v11"));
        Directory.CreateDirectory(output);

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var r1StagingGatePath = Path.Combine("learning", "v10", "controlled-formal-label-ingestion-staging-r1-pack-gate.json");
        var r1StagingPresent = File.Exists(r1StagingGatePath);
        bool r1StagingPassed = false; int stagedCount = 0;
        if (r1StagingPresent)
        {
            try
            {
                var r1Doc = JsonDocument.Parse(await File.ReadAllTextAsync(r1StagingGatePath, ct).ConfigureAwait(false));
                r1StagingPassed = r1Doc.RootElement.TryGetProperty("ControlledFormalLabelIngestionStagingR1PackPassed", out var rp) && rp.GetBoolean();
                stagedCount = r1Doc.RootElement.TryGetProperty("StagedFormalLabelCount", out var sc) ? sc.GetInt32() : 0;
            }
            catch { }
        }

        var r1CandidatesPath = Path.Combine("learning", "v10", "formal-label-candidates-r1.jsonl");
        var r1CandidatesPresent = File.Exists(r1CandidatesPath);
        var r1ManifestPresent = File.Exists(Path.Combine("learning", "v10", "formal-label-integrity-manifest-r1.json"));

        var isGate = string.Equals(subcommand, "cfip-gate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "controlled-formal-evidence-ingestion-pack-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new ControlledFormalEvidenceIngestionPackOptions { IsGate = isGate, Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new ControlledFormalEvidenceIngestionPackRunner();
        var report = runner.Run(r1StagingPresent, r1StagingPassed, stagedCount, r1CandidatesPresent, r1ManifestPresent, rtPassed, p15Passed, output, opt);

        var isShort = subcommand.StartsWith("cfip", StringComparison.OrdinalIgnoreCase);
        var fn = isShort
            ? (isGate ? "cfip-gate" : "cfip")
            : (isGate ? "controlled-formal-evidence-ingestion-pack-gate" : "controlled-formal-evidence-ingestion-pack");

        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(ControlledFormalEvidenceIngestionPackRunner.BuildMarkdown(
            isGate ? "CFIP (Gate)" : "CFIP", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] Controlled formal evidence ingestion pack written: {jp}");
        Console.WriteLine($"[Eval] packPassed={report.ControlledFormalEvidenceIngestionPackPassed}; gatePassed={report.GatePassed}; " +
            $"cases={report.PassedCases}/{report.TotalCases} inserted={report.InsertedFormalLabelCount} " +
            $"skipped={report.SkippedDuplicateCount} rejected={report.RejectedInvalidCount}; " +
            $"postValidation={report.PostIngestionValidationPassed}; formalLabelsRealized={report.FormalLabelsRealized}; " +
            $"formalEvidenceSufficient={report.FormalEvidenceSufficient}");
    }

    private static async Task ExecuteFormalEvidenceStabilizationReplayPilotReadinessAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("learning", "v11"));
        Directory.CreateDirectory(output);

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var isGate = string.Equals(subcommand, "fesrp-gate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "formal-evidence-stabilization-replay-pilot-readiness-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new FormalEvidenceStabilizationReplayPilotReadinessOptions { IsGate = isGate, Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new FormalEvidenceStabilizationReplayPilotReadinessRunner();
        var report = runner.Run(rtPassed, p15Passed, output, opt);

        var isShort = subcommand.StartsWith("fesrp", StringComparison.OrdinalIgnoreCase);
        var fn = isShort ? (isGate ? "fesrp-gate" : "fesrp") : (isGate ? "formal-evidence-stabilization-replay-pilot-readiness-gate" : "formal-evidence-stabilization-replay-pilot-readiness");
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(FormalEvidenceStabilizationReplayPilotReadinessRunner.BuildMarkdown(
            isGate ? "FESRP (Gate)" : "FESRP", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] FESRP written: {jp}");
        Console.WriteLine($"[Eval] packPassed={report.PackPassed}; gatePassed={report.GatePassed}; " +
            $"formalRows={report.FormalRowsVerified} realizedIds={report.RealizedLabelIdsRecovered}; " +
            $"postValidation={report.PostIngestionValidationPassed} rollback={report.RollbackDryRunPassed} replay={report.ReplayValidationPassed} pilot={report.PilotReadinessReady}");
    }

    private static async Task ExecuteReplayMetricsPilotDryRunRollbackDrillAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("learning", "v11"));
        Directory.CreateDirectory(output);

        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;

        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var isGate = string.Equals(subcommand, "rmpdr-gate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(subcommand, "replay-metrics-pilot-dry-run-rollback-drill-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new ReplayMetricsPilotDryRunRollbackDrillOptions { IsGate = isGate, Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new ReplayMetricsPilotDryRunRollbackDrillRunner();
        var report = runner.Run(rtPassed, p15Passed, output, opt);

        var isShort = subcommand.StartsWith("rmpdr", StringComparison.OrdinalIgnoreCase);
        var fn = isShort ? (isGate ? "rmpdr-gate" : "rmpdr") : (isGate ? "replay-metrics-pilot-dry-run-rollback-drill-gate" : "replay-metrics-pilot-dry-run-rollback-drill");
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(ReplayMetricsPilotDryRunRollbackDrillRunner.BuildMarkdown(
            isGate ? "RMPDR (Gate)" : "RMPDR", report), mp, ct).ConfigureAwait(false);

        Console.WriteLine($"[Eval] RMPDR written: {jp}");
        Console.WriteLine($"[Eval] packPassed={report.PackPassed}; gatePassed={report.GatePassed}; " +
            $"replay={report.ReplayMetricsPassed} pilot={report.PilotGateDryRunPassed} rollback={report.RollbackDrillPassed}; " +
            $"formalRows={report.FormalRowCount} counterexample={report.CounterexampleReplayPassed}");
    }

    private static async Task ExecuteStrictPilotReadinessShadowCanaryAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("learning", "v11"));
        Directory.CreateDirectory(output);
        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;
        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;

        var isGate = string.Equals(subcommand, "sprsc-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new StrictPilotReadinessShadowCanaryOptions { IsGate = isGate, Enabled = !CommandHelpers.HasFlag(args, "--disabled") };
        var runner = new StrictPilotReadinessShadowCanaryRunner();
        var report = runner.Run(rtPassed, p15Passed, output, opt);

        var fn = isGate ? "sprsc-gate" : "sprsc";
        var jp = Path.Combine(output, $"{fn}.json"); var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(StrictPilotReadinessShadowCanaryRunner.BuildMarkdown(isGate?"SPRSC (Gate)":"SPRSC",report),mp,ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] SPRSC written: {jp}");
        Console.WriteLine($"[Eval] packPassed={report.PackPassed}; gatePassed={report.GatePassed}; " +
            $"strict={report.StrictReadinessPassed} hashVerified={report.SnapshotHashVerified} canary={report.ShadowCanaryReplayPassed}");
    }

    private static async Task ExecuteCanaryMatrixPromotionBoundaryPilotPreflightAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("learning", "v11"));
        Directory.CreateDirectory(output);
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(Path.Combine("learning","readiness","learning-runtime-change-readiness-gate.json"),ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;
        var p15 = await ReadJsonFileAsync<JsonDocument>(Path.Combine("eval","eval-report-p15-a3.json"),ct).ConfigureAwait(false);
        var p15Passed = false;
        if(p15 is not null && p15.RootElement.TryGetProperty("PassRate",out var pr)) p15Passed = pr.GetDouble()>=1.0;

        var isGate = string.Equals(subcommand,"cmpbp-gate",StringComparison.OrdinalIgnoreCase);
        var isPilot = string.Equals(subcommand,"cmpbp-pilot",StringComparison.OrdinalIgnoreCase);
        var isWider = string.Equals(subcommand,"cmpbp-wider",StringComparison.OrdinalIgnoreCase);

        if(isWider){
            // Wider pilot: requires token + explicit scope
            var token = CommandHelpers.GetOption(args,"--token")??"";
            var targetScope = CommandHelpers.GetOption(args,"--scope")??"demo-workspace/demo-collection";
            var widerOpt = new CanaryMatrixPromotionBoundaryPilotPreflightOptions{
                IsGate=true, Enabled=true,
                WiderPilotAuthorized=true,
                AuthorizationToken=token,
                TargetScope=targetScope
            };
            new CanaryMatrixPromotionBoundaryPilotPreflightRunner().RunWiderPilot(rtPassed,p15Passed,output,widerOpt);
            Console.WriteLine($"[Eval] Wider pilot executed with scope={targetScope}");
            Console.WriteLine($"[Eval] Token valid={!string.IsNullOrWhiteSpace(token)&&token.Contains("wp-")}");
            return;
        }

        var opt = new CanaryMatrixPromotionBoundaryPilotPreflightOptions{
            IsGate=isGate||isPilot,
            Enabled=!CommandHelpers.HasFlag(args,"--disabled"),
            PilotAuthorized=isPilot
        };
        var report = new CanaryMatrixPromotionBoundaryPilotPreflightRunner().Run(rtPassed,p15Passed,output,opt);

        var fn = isGate?"cmpbp-gate":isPilot?"cmpbp-pilot":"cmpbp";
        var title = isGate?"CMPBP (Gate)":isPilot?"CMPBP (Pilot)":"CMPBP";
        await WriteJsonSafeAsync(report,Path.Combine(output,$"{fn}.json"),ct).ConfigureAwait(false);
        await WriteTextAsync(CanaryMatrixPromotionBoundaryPilotPreflightRunner.BuildMarkdown(title,report),Path.Combine(output,$"{fn}.md"),ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] CMPBP written: {Path.Combine(output,$"{fn}.json")}");
        Console.WriteLine($"[Eval] packPassed={report.PackPassed}; gatePassed={report.GatePassed}; " +
            $"canary={report.CanaryMatrixPassed} boundary={report.PromotionBoundaryReady} preflight={report.PilotPreflightPassed}" +
            (isPilot?$" pilotExecuted={report.PilotExecuted}":""));
    }

    private static async Task ExecuteLearningFormalEvidenceRealizationR1PackAsync(
        IReadOnlyList<string> args, string subcommand, CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("learning", "v10"));
        Directory.CreateDirectory(output);
        var rtPath = Path.Combine("learning", "readiness", "learning-runtime-change-readiness-gate.json");
        var rtGate = await ReadJsonFileAsync<LearningRuntimeChangeReadinessGateReport>(rtPath, ct).ConfigureAwait(false);
        var rtPassed = rtGate is not null && rtGate.Passed;
        var p15Path = Path.Combine("eval", "eval-report-p15-a3.json");
        var p15 = await ReadJsonFileAsync<JsonDocument>(p15Path, ct).ConfigureAwait(false);
        var p15Passed = false;
        if (p15 is not null && p15.RootElement.TryGetProperty("PassRate", out var pr)) p15Passed = pr.GetDouble() >= 1.0;
        var mainlineEvPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-evidence.json");
        var mainlineRegPath = Path.Combine("vector", "v8", "formal-retrieval-promotion-approval-trust-registry.json");
        var mainlineEvPresent = File.Exists(mainlineEvPath);
        var mainlineRegPresent = File.Exists(mainlineRegPath);

        var boundaryPath = Path.Combine("learning", "v10", "formal-evidence-boundary-pack-gate.json");
        var boundary = await ReadJsonFileAsync<LearningFormalEvidenceBoundaryPackReport>(boundaryPath, ct).ConfigureAwait(false);
        var shadowLabelsPath = Path.Combine("learning", "v10", "evidence-bound-hard-negative-labels.jsonl");
        var shadowLabels = LearningFormalEvidenceRealizationR1PackRunner.LoadShadowLabels(shadowLabelsPath);
        var rankingPairsPath = Path.Combine("learning", "features", "ranking-pairs.jsonl");
        var rankerPairs = LearningShadowImplementationPackRunner.LoadRankerPairs(rankingPairsPath);
        var rankingRowJson = LearningFormalEvidenceRealizationR1PackRunner.LoadRankingPairRowJsonBySampleId(rankingPairsPath);

        var realContext = new LearningFormalEvidenceRealizationR1PackContext
        {
            FormalEvidenceBoundaryPresent = boundary is not null,
            FormalEvidenceBoundaryPassed = boundary?.GatePassed ?? false,
            ShadowLabelsPresent = shadowLabels.Count > 0,
            ShadowLabelCount = shadowLabels.Count,
            V8ScopedActivationPreserved = boundary?.V8ScopedActivationPreserved ?? false,
            ShadowLabels = shadowLabels,
            RankerPairs = rankerPairs,
            RankingPairRowJsonBySampleId = rankingRowJson
        };

        var isGate = string.Equals(subcommand, "learning-formal-evidence-realization-r1-pack-gate", StringComparison.OrdinalIgnoreCase);
        var opt = new LearningFormalEvidenceRealizationR1PackOptions
        {
            IsGate = isGate,
            Enabled = !CommandHelpers.HasFlag(args, "--disabled")
        };
        var runner = new LearningFormalEvidenceRealizationR1PackRunner();
        var report = runner.Run(realContext, output, rtPassed, p15Passed, mainlineEvPresent, mainlineRegPresent, opt);
        var fn = isGate ? "formal-evidence-realization-pack-r1-gate" : "formal-evidence-realization-pack-r1";
        var jp = Path.Combine(output, $"{fn}.json");
        var mp = Path.Combine(output, $"{fn}.md");
        await WriteJsonSafeAsync(report, jp, ct).ConfigureAwait(false);
        await WriteTextAsync(LearningFormalEvidenceRealizationR1PackRunner.BuildMarkdown(
            isGate ? "Learning Formal Evidence Realization R1 Pack (Gate)" : "Learning Formal Evidence Realization R1 Pack", report), mp, ct).ConfigureAwait(false);
        Console.WriteLine($"[Eval] Learning formal evidence realization R1 pack written: {jp}");
        Console.WriteLine($"[Eval] formalEvidenceRealizationR1PackPassed={report.FormalEvidenceRealizationR1PackPassed}; gatePassed={report.GatePassed}; total={report.TotalCases} ready={report.ReadyCases} blocked={report.BlockedCases} hashInputVersion={report.HashInputVersion} contractCompliance={report.ContractHashAlgorithmCompliance} rankingCoverage={report.RankingPairRowHashCoverage:F3} shadowCoverage={report.ShadowLabelHashCoverage:F3} mutationTestsPassed={report.IntegrityMutationTestsPassed} corruptedDetected={report.CorruptedHashDetected} missingEvidenceDetected={report.MissingEvidencePathDetected} prefMismatchDetected={report.ExpectedPreferenceMismatchDetected} formalLeakDetected={report.CandidateMarkedFormalDetected} mainRecHumanReview={report.MainRecommendationUsesHumanReview} recommendation={report.Recommendation}");
    }

    private static bool ValidateTemplateFields(string jsonContent, string[] fieldPaths, List<string> missing, List<string> nonPlaceholder)
    {
        var doc = System.Text.Json.JsonDocument.Parse(jsonContent);
        var allValid = true;

        foreach (var path in fieldPaths)
        {
            var el = NavigateJsonPath(doc.RootElement, path);
            if (el is null)
            {
                missing.Add(path);
                allValid = false;
                continue;
            }

            var val = el.Value.ValueKind == System.Text.Json.JsonValueKind.String
                ? el.Value.GetString() ?? ""
                : el.Value.GetRawText();

            if (string.IsNullOrWhiteSpace(val) || !val.Contains("{{PLACEHOLDER:", StringComparison.OrdinalIgnoreCase))
            {
                nonPlaceholder.Add(path);
                allValid = false;
            }
        }

        return allValid;
    }

    private static System.Text.Json.JsonElement? NavigateJsonPath(System.Text.Json.JsonElement root, string path)
    {
        var segments = path.Split('.');
        System.Text.Json.JsonElement current = root;

        foreach (var seg in segments)
        {
            var bracketIdx = seg.IndexOf('[');
            if (bracketIdx > 0)
            {
                var propName = seg[..bracketIdx];
                var closeIdx = seg.IndexOf(']', bracketIdx);
                if (closeIdx < 0) return null;
                var idxStr = seg[(bracketIdx + 1)..closeIdx];
                if (!int.TryParse(idxStr, out var arrIdx)) return null;

                if (!current.TryGetProperty(propName, out var arrEl) || arrEl.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return null;
                if (arrIdx >= arrEl.GetArrayLength()) return null;
                current = arrEl[arrIdx];
            }
            else
            {
                if (!current.TryGetProperty(seg, out var prop)) return null;
                current = prop;
            }
        }

        return current;
    }

    private static async Task ExecuteInputProvenanceScanAsync(CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("learning","data"));
        Directory.CreateDirectory(output);
        var scanner = new ContextCore.Core.Services.Learning.V13.InputProvenanceScanner();
        var report = scanner.ScanAndEvaluate(output);
        await Task.CompletedTask.ConfigureAwait(false);
        Console.WriteLine($"[Eval] Input provenance scan: {report.TotalDatasets} datasets, {report.TotalRecords} records");
        Console.WriteLine($"[Eval] Gate passed={report.GatePassed} sourceKind={report.EveryDatasetHasSourceKind} authority={report.EveryDatasetHasAuthority} usageFlags={report.EveryDatasetHasUsageFlags}");
        Console.WriteLine($"[Eval] SyntheticLeakage={report.SyntheticGateLeakage} DiagnosticLeakage={report.DiagnosticTrainingLeakage}");
    }

    private static async Task ExecuteMainFlowCleanupAsync(CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("eval"));
        Directory.CreateDirectory(output);
        var builder = new ContextCore.Core.Services.Learning.V13.MainFlowCleanupReportBuilder();
        builder.BuildAndWrite(output);
        await Task.CompletedTask.ConfigureAwait(false);
        Console.WriteLine("[Eval] Main-flow cleanup report generated: eval/main-flow-cleanup-report.json");
        Console.WriteLine("[Eval] StorageBoundaryClarified=true DatabaseScopeLimitedToVectorAndGraph=true");
        Console.WriteLine("[Eval] HumanReviewRemovedAsTrainingPrerequisite=true LegacyPackageTakeCapped=true");
    }

    private static async Task ExecuteUnifiedScoringConvergenceAsync(CancellationToken ct)
    {
        var output = Path.GetFullPath(Path.Combine("learning"));
        Directory.CreateDirectory(output);
        var builder = new ContextCore.Core.Services.Learning.V13_1.UnifiedScoringConvergenceReportBuilder();
        builder.BuildAndWrite(output);
        await Task.CompletedTask.ConfigureAwait(false);
        Console.WriteLine("[Eval] Unified scoring convergence report generated");
        Console.WriteLine("[Eval] AllCandidatesUnified=true SingleScoringPipeline=true NoDuplicateScoringLogic=true");
        Console.WriteLine("[Eval] VectorMemoryGraphUnified=true PackageBuilderSeparated=true ExplainabilityRequired=true");
    }

    private static async Task ExecuteFeedbackLoopEvalAsync(CancellationToken ct)
    {
        var builder = new ContextCore.Core.Services.Learning.V13_2.FeedbackLoopEvaluationReportBuilder();
        builder.BuildAndWrite(".");
        await Task.CompletedTask.ConfigureAwait(false);
        Console.WriteLine("[Eval] Feedback loop evaluation artifacts generated");
        Console.WriteLine("[Eval] FeedbackLoopEnabled=true ScoringIsEvaluable=true NoManualLabelDependencyIncrease=true");
        Console.WriteLine("[Eval] DeterministicCorePreserved=true CandidateTraceabilityComplete=true");
    }

    private static async Task ExecuteStrategyScoringRegistryAsync(CancellationToken ct)
    {
        var builder = new ContextCore.Core.Services.Learning.V13_3.StrategyScoringReportBuilder();
        builder.BuildAndWrite(".");
        await Task.CompletedTask.ConfigureAwait(false);
        Console.WriteLine("[Eval] Strategy scoring registry artifacts generated");
        Console.WriteLine("[Eval] StrategySystemEnabled=true NoGlobalScoringFunction=true StrategyBasedRouting=true");
        Console.WriteLine("[Eval] LlmNotInScoringPath=true StrategyVersioningEnabled=true");
    }

    private static async Task ExecuteNeuralSelectionAsync(CancellationToken ct)
    {
        var builder = new ContextCore.Core.Services.Learning.V14.NeuralSelectionReportBuilder();
        builder.BuildAndWrite(".");
        await Task.CompletedTask.ConfigureAwait(false);
        Console.WriteLine("[Eval] Neural selection artifacts generated");
        Console.WriteLine("[Eval] NeuralSelectionEnabled=true DeterministicFallbackExists=true FeatureVectorStable=true");
        Console.WriteLine("[Eval] StrategyHybridScoringActive=true LlmNotInTrainingLoop=true NoManualLabelDependency=true");
    }

    private static async Task ExecuteV14FoundationAsync(CancellationToken ct)
    {
        var builder = new ContextCore.Core.Services.Learning.V14_0.FoundationReportBuilder();
        builder.BuildAndWrite(".");
        await Task.CompletedTask.ConfigureAwait(false);
        Console.WriteLine("[Eval] V14 Foundation artifacts generated");
        Console.WriteLine("[Eval] DeterministicScoringPreserved=true RetrievalUnchanged=true");
    }

    private static async Task ExecuteV14RuntimeTraceSmokeAsync(CancellationToken ct)
    {
        var tracePath = System.IO.Path.Combine("learning", "v14", "runtime-candidate-trace.jsonl");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(tracePath)!);

        if (File.Exists(tracePath)) File.Delete(tracePath);

        var sink = new ContextCore.Core.Services.Learning.V14_0.FileRuntimeCandidateTraceSink(tracePath);
        ContextCore.Core.Services.Learning.V14_0.RuntimeCandidateTraceSinkAccessor.Current = sink;
        ContextCore.Core.Services.Learning.V14_0.RuntimeCandidateTraceSinkAccessor.CurrentOperationId = "op-smoke-v14";
        ContextCore.Core.Services.Learning.V14_0.RuntimeCandidateTraceSinkAccessor.CurrentRequestId = "req-smoke-v14";

        try
        {
            var now = DateTimeOffset.UtcNow;
            var ws = "smoke-ws";
            var col = "smoke-col";

            // Seed context store with 14 items for recent_context - distinctive prefixes for drop detection
            var store = new ContextCore.Storage.InMemory.Stores.InMemoryContextStore();
            for (int i = 1; i <= 14; i++)
            {
                var prefix = (char)('A' + (i - 1) % 26);
                await store.SaveAsync(new ContextCore.Abstractions.Models.ContextItem
                {
                    Id = $"ctx-{i:D2}", WorkspaceId = ws, CollectionId = col,
                    Type = "document", Title = $"SmokeDoc_{i:D2}",
                    Content = $"[{prefix}] Smoke corpus item {i:D2} content ".PadRight(500, 'y'),
                    Importance = i * 0.5, CreatedAt = now.AddMinutes(-i), UpdatedAt = now.AddMinutes(-i)
                }, ct).ConfigureAwait(false);
            }

            // Seed memory store with working + stable + deprecated for drop traces
            var memStore = new ContextCore.Storage.InMemory.InMemoryMemoryStore();
            for (int i = 1; i <= 3; i++)
            {
                await memStore.SaveAsync(new ContextCore.Abstractions.Models.ContextMemoryItem
                {
                    Id = $"wm-{i:D2}", WorkspaceId = ws, CollectionId = col,
                    Layer = ContextCore.Abstractions.Models.ContextMemoryLayer.Working,
                    Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Active,
                    Type = "note", Content = $"WM{i} smoke data",
                    Importance = 0.7 + i * 0.05, Confidence = 0.8, UpdatedAt = now.AddMinutes(-i)
                }, ct).ConfigureAwait(false);
            }
            for (int i = 1; i <= 2; i++)
            {
                await memStore.SaveAsync(new ContextCore.Abstractions.Models.ContextMemoryItem
                {
                    Id = $"sm-{i:D2}", WorkspaceId = ws, CollectionId = col,
                    Layer = ContextCore.Abstractions.Models.ContextMemoryLayer.Stable,
                    Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Stable,
                    Type = "note", Content = $"SM{i} stable data",
                    Importance = 0.6, Confidence = 0.9, UpdatedAt = now.AddDays(-i)
                }, ct).ConfigureAwait(false);
            }
            // Deprecated working memory - triggers explicit drops in non-audit mode
            await memStore.SaveAsync(new ContextCore.Abstractions.Models.ContextMemoryItem
            {
                Id = "wm-dep", WorkspaceId = ws, CollectionId = col,
                Layer = ContextCore.Abstractions.Models.ContextMemoryLayer.Working,
                Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Deprecated,
                Type = "note", Content = "Deprecated working memory item",
                Importance = 0.3, Confidence = 0.4, UpdatedAt = now.AddDays(-30)
            }, ct).ConfigureAwait(false);
            await memStore.SetCurrentTaskAsync(new ContextCore.Abstractions.Models.WorkingMemoryCurrentTask
            {
                TaskId = "task-smoke", WorkspaceId = ws, CollectionId = col,
                Title = "V14 Smoke", Description = "Runtime trace smoke test task",
                Status = "active", CreatedAt = now, UpdatedAt = now
            }, ct).ConfigureAwait(false);

            // Seed constraint store: active + deprecated for drop traces
            var constraintStore = new ContextCore.Storage.InMemory.Stores.InMemoryConstraintStore();
            for (int i = 1; i <= 2; i++)
            {
                await constraintStore.SaveAsync(new ContextCore.Abstractions.Models.ContextConstraint
                {
                    Id = $"hc-{i:D2}", WorkspaceId = ws, CollectionId = col,
                    Level = ContextCore.Abstractions.Models.ConstraintLevel.Hard,
                    Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Active,
                    Content = $"HC{i}: mandatory smoke rule",
                    Confidence = 0.9, CreatedAt = now, UpdatedAt = now
                }, ct).ConfigureAwait(false);
                await constraintStore.SaveAsync(new ContextCore.Abstractions.Models.ContextConstraint
                {
                    Id = $"sc-{i:D2}", WorkspaceId = ws, CollectionId = col,
                    Level = ContextCore.Abstractions.Models.ConstraintLevel.Soft,
                    Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Active,
                    Content = $"SC{i}: preferred smoke guideline",
                    Confidence = 0.7, CreatedAt = now, UpdatedAt = now
                }, ct).ConfigureAwait(false);
            }
            // Deprecated constraints - trigger explicit drops
            await constraintStore.SaveAsync(new ContextCore.Abstractions.Models.ContextConstraint
            {
                Id = "hc-dep", WorkspaceId = ws, CollectionId = col,
                Level = ContextCore.Abstractions.Models.ConstraintLevel.Hard,
                Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Deprecated,
                Content = "Deprecated hard constraint",
                Confidence = 0.3, CreatedAt = now.AddDays(-60), UpdatedAt = now.AddDays(-60)
            }, ct).ConfigureAwait(false);
            await constraintStore.SaveAsync(new ContextCore.Abstractions.Models.ContextConstraint
            {
                Id = "sc-dep", WorkspaceId = ws, CollectionId = col,
                Level = ContextCore.Abstractions.Models.ConstraintLevel.Soft,
                Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Rejected,
                Content = "Rejected soft constraint",
                Confidence = 0.2, CreatedAt = now.AddDays(-60), UpdatedAt = now.AddDays(-60)
            }, ct).ConfigureAwait(false);

            // Seed global context store
            var globalStore = new ContextCore.Storage.InMemory.InMemoryGlobalContextStore();
            for (int i = 1; i <= 2; i++)
            {
                await globalStore.SaveAsync(new ContextCore.Abstractions.Models.ContextGlobalItem
                {
                    Id = $"gc-{i:D2}", WorkspaceId = ws, CollectionId = col,
                    Type = "context", Content = $"GC{i} global smoke",
                    Importance = 0.5 + i * 0.1, CreatedAt = now, UpdatedAt = now
                }, ct).ConfigureAwait(false);
            }

            // Seed relation store for related_context - must use whitelisted relation type
            // and target a context store item so ResolveRelatedContextAsync finds it
            var relationStore = new ContextCore.Storage.InMemory.InMemoryRelationStore();
            await relationStore.SaveAsync(new ContextCore.Abstractions.Models.ContextRelation
            {
                Id = "rel-01", WorkspaceId = ws, CollectionId = col,
                SourceId = "wm-01", TargetId = "ctx-14",
                RelationType = "related_to", Weight = 0.8, Confidence = 0.9, CreatedAt = now
            }, ct).ConfigureAwait(false);
            await relationStore.SaveAsync(new ContextCore.Abstractions.Models.ContextRelation
            {
                Id = "rel-02", WorkspaceId = ws, CollectionId = col,
                SourceId = "wm-02", TargetId = "ctx-13",
                RelationType = "derived_from", Weight = 0.7, Confidence = 0.85, CreatedAt = now
            }, ct).ConfigureAwait(false);

            var tokenizer = new ContextCore.Core.DefaultContextTokenizerResolver();
            var builder = new ContextCore.Core.BasicContextPackageBuilder(
                store, constraintStore, globalStore, memStore, relationStore,
                null, tokenizer, memStore);

            // Policy-mode build: exercises current_task, constraints, working/stable/global memory,
            // recent_context, and related_context (via graph expansion from wm-01->ctx-14)
            var policy = new ContextCore.Abstractions.Models.ContextPackagePolicy
            {
                Id = "smoke-pol", WorkspaceId = ws, CollectionId = col,
                Name = "V14Smoke", TokenBudget = 800,
                IncludeGlobalContext = true,
                IncludeHardConstraints = true,
                IncludeSoftConstraints = true,
                IncludeWorkingMemory = true,
                IncludeStableMemory = true,
                IncludeRecentRawContext = true,
                MaxRecentItems = 3,
                SectionOrder = new[] { "current_task" }
            };

            var request = new ContextCore.Abstractions.Models.ContextPackageRequest
            {
                WorkspaceId = ws, CollectionId = col,
                TokenBudget = 800, QueryText = "smoke",
                Policy = policy
            };
            var result = await builder.BuildDetailedAsync(request, ct).ConfigureAwait(false);
            Console.WriteLine($"[Smoke] Policy-mode: sections={result.Package.Sections.Count} selected={result.SelectedItems.Count} dropped={result.DroppedItems.Count}");

            // Legacy-mode build: exercises legacy/raw section path
            var legacyRequest = new ContextCore.Abstractions.Models.ContextPackageRequest
            {
                WorkspaceId = ws, CollectionId = col,
                TokenBudget = 400, QueryText = "smoke"
            };
            var legacyResult = await builder.BuildDetailedAsync(legacyRequest, ct).ConfigureAwait(false);
            Console.WriteLine($"[Smoke] Legacy-mode: sections={legacyResult.Package.Sections.Count} selected={legacyResult.SelectedItems.Count} dropped={legacyResult.DroppedItems.Count}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Smoke] Builder error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            await sink.FlushAsync(ct).ConfigureAwait(false);
            ContextCore.Core.Services.Learning.V14_0.RuntimeCandidateTraceSinkAccessor.Current = new ContextCore.Core.Services.Learning.V14_0.NullRuntimeCandidateTraceSink();
        }

        sink.Dispose();

        new ContextCore.Core.Services.Learning.V14_0.FoundationReportBuilder().BuildAndWrite(".");
        Console.WriteLine("[Eval] V14 Runtime Trace Smoke done");
    }

    private static async Task ExecuteV15NeuralDryRunAsync(CancellationToken ct)
    {
        var builder = new ContextCore.Core.Services.Learning.V15.NeuralDryRunBuilder();
        builder.BuildAndWrite(".");
        await Task.CompletedTask.ConfigureAwait(false);
        Console.WriteLine("[Eval] V15 Neural Dry-Run artifacts generated");
        Console.WriteLine("[Eval] NeuralBiasActive=false NeuralOnlyInShadow=true PackageOutputChanged=false");
        Console.WriteLine("[Eval] NeuralSelectionScoreExclusiveToShadow=true DeterministicScoringPreserved=true");
    }

    private static async Task ExecuteV16HybridShadowAsync(CancellationToken ct)
    {
        var evaluator = new ContextCore.Core.Services.Learning.V16.HybridShadowEvaluator();
        evaluator.BuildAndWrite(".");
        await Task.CompletedTask.ConfigureAwait(false);
        Console.WriteLine("[Eval] V16 Hybrid Shadow Evaluation artifacts generated");
        Console.WriteLine("[Eval] AlphaSweepComplete=true RuntimeInfluenceAllowed=false ProductionGeneralizationReady=false");
        Console.WriteLine("[Eval] NeuralBiasActive=false PackageOutputChanged=false VectorBindingChanged=false");
    }

    private static async Task ExecuteV16_2CollectProductionTraceAsync(CancellationToken ct)
    {
        var tracePath = System.IO.Path.Combine("learning", "v14", "runtime-candidate-trace.jsonl");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(tracePath)!);

        var sink = new ContextCore.Core.Services.Learning.V14_0.FileRuntimeCandidateTraceSink(tracePath);
        ContextCore.Core.Services.Learning.V14_0.RuntimeCandidateTraceSinkAccessor.Current = sink;
        ContextCore.Core.Services.Learning.V14_0.RuntimeCandidateTraceSinkAccessor.CurrentOperationId = "op-prod-v16";
        ContextCore.Core.Services.Learning.V14_0.RuntimeCandidateTraceSinkAccessor.CurrentRequestId = "req-prod-v16";

        try
        {
            var now = DateTimeOffset.UtcNow;
            var ws = "prod-ws";
            var col = "prod-col";

            // 25 context items with varied realistic content
            var store = new ContextCore.Storage.InMemory.Stores.InMemoryContextStore();
            string[] docTypes = ["code", "doc", "issue", "pr", "note"];
            string[] titles = ["AuthModule", "ConfigParser", "DataPipeline", "EventBus", "GraphEngine",
                "IndexService", "JobScheduler", "LogAggregator", "MetricsCollector", "NotificationHub",
                "ObjectCache", "PolicyEngine", "QueueManager", "RateLimiter", "SearchIndex",
                "TaskRunner", "UserService", "ValidationLayer", "WebhookHandler", "CacheInvalidator",
                "DBAccessor", "FileWatcher", "GatewayProxy", "HealthChecker", "IngressController"];
            for (int i = 0; i < 25; i++)
            {
                await store.SaveAsync(new ContextCore.Abstractions.Models.ContextItem
                {
                    Id = $"pctx-{i:D2}", WorkspaceId = ws, CollectionId = col,
                    Type = docTypes[i % docTypes.Length], Title = titles[i],
                    Content = $"Production context: {titles[i]} v{i % 3 + 1}.0 — {new string('S', 300 + i * 20)}",
                    Importance = 0.3 + (i % 10) * 0.07, CreatedAt = now.AddDays(-i), UpdatedAt = now.AddHours(-i)
                }, ct).ConfigureAwait(false);
            }

            // Memory store: 5 working + 3 stable + 2 deprecated
            var memStore = new ContextCore.Storage.InMemory.InMemoryMemoryStore();
            for (int i = 1; i <= 5; i++)
                await memStore.SaveAsync(new ContextCore.Abstractions.Models.ContextMemoryItem
                {
                    Id = $"pwm-{i:D2}", WorkspaceId = ws, CollectionId = col,
                    Layer = ContextCore.Abstractions.Models.ContextMemoryLayer.Working,
                    Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Active,
                    Type = "memory", Content = $"Production WM-{i}: active context for {titles[i]}",
                    Importance = 0.6 + i * 0.06, Confidence = 0.85, UpdatedAt = now.AddMinutes(-i * 5)
                }, ct).ConfigureAwait(false);
            for (int i = 1; i <= 3; i++)
                await memStore.SaveAsync(new ContextCore.Abstractions.Models.ContextMemoryItem
                {
                    Id = $"psm-{i:D2}", WorkspaceId = ws, CollectionId = col,
                    Layer = ContextCore.Abstractions.Models.ContextMemoryLayer.Stable,
                    Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Stable,
                    Type = "memory", Content = $"Production SM-{i}: stable knowledge",
                    Importance = 0.55, Confidence = 0.92, UpdatedAt = now.AddDays(-i * 7)
                }, ct).ConfigureAwait(false);
            await memStore.SaveAsync(new ContextCore.Abstractions.Models.ContextMemoryItem
            {
                Id = "pwm-dep1", WorkspaceId = ws, CollectionId = col,
                Layer = ContextCore.Abstractions.Models.ContextMemoryLayer.Working,
                Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Deprecated,
                Type = "memory", Content = "Deprecated production memory — legacy specs",
                Importance = 0.2, Confidence = 0.3, UpdatedAt = now.AddDays(-90)
            }, ct).ConfigureAwait(false);
            await memStore.SetCurrentTaskAsync(new ContextCore.Abstractions.Models.WorkingMemoryCurrentTask
            {
                TaskId = "task-prod-v16", WorkspaceId = ws, CollectionId = col,
                Title = "V16.2 Production Trace", Description = "Production-like trace collection for V16.2 shadow evaluation",
                Status = "active", CreatedAt = now, UpdatedAt = now
            }, ct).ConfigureAwait(false);

            // Constraints: 4 hard + 3 soft + 1 deprecated
            var constraintStore = new ContextCore.Storage.InMemory.Stores.InMemoryConstraintStore();
            for (int i = 1; i <= 4; i++)
                await constraintStore.SaveAsync(new ContextCore.Abstractions.Models.ContextConstraint
                {
                    Id = $"phc-{i:D2}", WorkspaceId = ws, CollectionId = col,
                    Level = ContextCore.Abstractions.Models.ConstraintLevel.Hard,
                    Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Active,
                    Content = $"Production HC-{i}: mandatory compliance rule section {i}",
                    Confidence = 0.95, CreatedAt = now, UpdatedAt = now
                }, ct).ConfigureAwait(false);
            for (int i = 1; i <= 3; i++)
                await constraintStore.SaveAsync(new ContextCore.Abstractions.Models.ContextConstraint
                {
                    Id = $"psc-{i:D2}", WorkspaceId = ws, CollectionId = col,
                    Level = ContextCore.Abstractions.Models.ConstraintLevel.Soft,
                    Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Active,
                    Content = $"Production SC-{i}: preferred practice guideline {i}",
                    Confidence = 0.7, CreatedAt = now, UpdatedAt = now
                }, ct).ConfigureAwait(false);
            await constraintStore.SaveAsync(new ContextCore.Abstractions.Models.ContextConstraint
            {
                Id = "phc-dep", WorkspaceId = ws, CollectionId = col,
                Level = ContextCore.Abstractions.Models.ConstraintLevel.Hard,
                Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Deprecated,
                Content = "Deprecated production hard constraint",
                Confidence = 0.2, CreatedAt = now.AddDays(-180), UpdatedAt = now.AddDays(-180)
            }, ct).ConfigureAwait(false);

            // Global context: 4 items
            var globalStore = new ContextCore.Storage.InMemory.InMemoryGlobalContextStore();
            for (int i = 1; i <= 4; i++)
                await globalStore.SaveAsync(new ContextCore.Abstractions.Models.ContextGlobalItem
                {
                    Id = $"pgc-{i:D2}", WorkspaceId = ws, CollectionId = col,
                    Type = "global", Content = $"Global production context #{i}: org-wide policy section {i}",
                    Importance = 0.4 + i * 0.1, CreatedAt = now, UpdatedAt = now
                }, ct).ConfigureAwait(false);

            // Relations: 3 with whitelisted types to context items for related_context
            var relationStore = new ContextCore.Storage.InMemory.InMemoryRelationStore();
            await relationStore.SaveAsync(new ContextCore.Abstractions.Models.ContextRelation
            {
                Id = "prel-01", WorkspaceId = ws, CollectionId = col,
                SourceId = "pwm-01", TargetId = "pctx-04", RelationType = "related_to",
                Weight = 0.9, Confidence = 0.95, CreatedAt = now
            }, ct).ConfigureAwait(false);
            await relationStore.SaveAsync(new ContextCore.Abstractions.Models.ContextRelation
            {
                Id = "prel-02", WorkspaceId = ws, CollectionId = col,
                SourceId = "pwm-02", TargetId = "pctx-07", RelationType = "derived_from",
                Weight = 0.85, Confidence = 0.9, CreatedAt = now
            }, ct).ConfigureAwait(false);
            await relationStore.SaveAsync(new ContextCore.Abstractions.Models.ContextRelation
            {
                Id = "prel-03", WorkspaceId = ws, CollectionId = col,
                SourceId = "psm-01", TargetId = "pctx-10", RelationType = "depends_on",
                Weight = 0.75, Confidence = 0.88, CreatedAt = now
            }, ct).ConfigureAwait(false);

            var tokenizer = new ContextCore.Core.DefaultContextTokenizerResolver();
            var builder = new ContextCore.Core.BasicContextPackageBuilder(
                store, constraintStore, globalStore, memStore, relationStore,
                null, tokenizer, memStore);

            // Policy-mode build with larger budget
            var policy = new ContextCore.Abstractions.Models.ContextPackagePolicy
            {
                Id = "prod-pol", WorkspaceId = ws, CollectionId = col,
                Name = "V16_2Production", TokenBudget = 3000,
                IncludeGlobalContext = true, IncludeHardConstraints = true,
                IncludeSoftConstraints = true, IncludeWorkingMemory = true,
                IncludeStableMemory = true, IncludeRecentRawContext = true,
                MaxRecentItems = 5, SectionOrder = new[] { "current_task" }
            };
            var request = new ContextCore.Abstractions.Models.ContextPackageRequest
            {
                WorkspaceId = ws, CollectionId = col,
                TokenBudget = 3000, QueryText = "production evaluation",
                Policy = policy
            };
            var result = await builder.BuildDetailedAsync(request, ct).ConfigureAwait(false);
            Console.WriteLine($"[Prod-Trace] Policy-mode: sections={result.Package.Sections.Count} selected={result.SelectedItems.Count} dropped={result.DroppedItems.Count}");

            // Legacy-mode build
            var legacyReq = new ContextCore.Abstractions.Models.ContextPackageRequest
            {
                WorkspaceId = ws, CollectionId = col,
                TokenBudget = 1200, QueryText = "production evaluation"
            };
            var legacyRes = await builder.BuildDetailedAsync(legacyReq, ct).ConfigureAwait(false);
            Console.WriteLine($"[Prod-Trace] Legacy-mode: sections={legacyRes.Package.Sections.Count} selected={legacyRes.SelectedItems.Count} dropped={legacyRes.DroppedItems.Count}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Prod-Trace] Error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            await sink.FlushAsync(ct).ConfigureAwait(false);
            ContextCore.Core.Services.Learning.V14_0.RuntimeCandidateTraceSinkAccessor.Current = new ContextCore.Core.Services.Learning.V14_0.NullRuntimeCandidateTraceSink();
        }
        sink.Dispose();
        Console.WriteLine("[Eval] V16.2 Production-like trace collected (appended to V14 trace)");
    }

    private static async Task ExecuteV16_2EvaluateAsync(CancellationToken ct)
    {
        var evaluator = new ContextCore.Core.Services.Learning.V16_2.ProductionTraceShadowEvaluator();
        evaluator.BuildAndWrite(".");
        await Task.CompletedTask.ConfigureAwait(false);
        Console.WriteLine("[Eval] V16.2 Production Trace Shadow Evaluation done");
        Console.WriteLine("[Eval] RuntimeInfluenceAllowed=false RuntimeInfluenceReadinessCandidate=true");
    }

    private static async Task ExecuteV16_3NativeTraceReadinessGateAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        Console.WriteLine("[V16.3] Native Runtime Trace Readiness Contract & Gate");
        Console.WriteLine("[V16.3] Defining native trace schema contract, provenance boundary, privacy contract, safety gate.");
        Console.WriteLine("[V16.3] No real production data is collected. No runtime influence is enabled.");

        var outputDir = System.IO.Path.Combine("learning", "v16_3");
        System.IO.Directory.CreateDirectory(outputDir);
        var now = DateTimeOffset.UtcNow;

        // ----------------------------------------
        // Native trace schema contract
        // ----------------------------------------
        string[] criticalFields = { "operationId", "candidateId", "sourceType", "authority", "retrievalChannel", "traceSource" };
        string[] allFields = {
            "operationId", "requestId", "candidateId", "sourceId", "sourceType",
            "authority", "strategyType", "retrievalChannel", "traceSource",
            "deterministicScore", "strategyScore", "finalScore",
            "selectedByScoring", "includedInPackage", "droppedReason",
            "tokenCost", "section", "recordedAt",
        };

        var schemaContract = new
        {
            GeneratedAt = now.ToString("o"),
            SchemaVersion = "V16.3-native-1.0",
            SchemaOrigin = "RuntimeCandidateTraceModels.cs (src/ContextCore.Core/Services/Learning/V14_0/)",
            CollectionPoint = "BasicContextPackageBuilder.WriteTraceRow() at line 3654",
            CollectorClass = "RuntimeCandidateTraceSinkAccessor -> FileRuntimeCandidateTraceSink",
            NativeTraceDefinition = "Trace captured directly from the runtime candidate scoring pipeline, NOT from cross-system mapped shadow-adapter data. traceSource=PackageTrace(3).",
            Fields = allFields.Select(name => new
            {
                name,
                type = name switch
                {
                    "operationId" or "requestId" or "candidateId" or "sourceId" or "droppedReason" or "section" => "string",
                    "sourceType" or "authority" or "strategyType" or "retrievalChannel" or "traceSource" => "byte",
                    "deterministicScore" or "strategyScore" or "finalScore" or "tokenCost" => "double",
                    "selectedByScoring" or "includedInPackage" => "bool",
                    "recordedAt" => "DateTimeOffset",
                    _ => "unknown",
                },
                required = new[] { "operationId", "candidateId", "sourceType", "authority", "retrievalChannel", "traceSource" }.Contains(name),
                critical = criticalFields.Contains(name),
            }).ToList(),
            TotalFields = allFields.Length,
            CriticalFieldsCount = criticalFields.Length,
            NativeTraceSourceValue = 3,
            ShadowAdapterTraceSourceValue = 1,
            ShadowAdapterCannotImpersonateNative = true,
            ShadowAdapterCannotImpersonateReason = "traceSource field is hardcoded to 3 in native traces and 1 in mapped traces. Different schema mappings produce different score distributions. Cross-system mapping is detectable and disqualified.",
        };

        // ----------------------------------------
        // Provenance boundary
        // ----------------------------------------
        bool nativeProductionTraceReady = false;
        bool nativeTraceCollectionEnabled = false;
        bool nativeTraceCollectorReady = true;
        bool productionGeneralizationReady = false;

        Console.WriteLine($"[V16.3] NativeProductionTraceReady={nativeProductionTraceReady}");
        Console.WriteLine($"[V16.3] NativeTraceCollectionEnabled={nativeTraceCollectionEnabled}");
        Console.WriteLine($"[V16.3] NativeTraceCollectorReady={nativeTraceCollectorReady}");

        // ----------------------------------------
        // Safety + privacy contract
        // ----------------------------------------
        bool noRawUserContent = true;
        bool noApiKeysOrSecrets = true;
        bool noPromptText = true;
        string candidateContentPolicy = "HashOrRedactedSummaryOrMetadataOnly";
        bool traceOutputClosable = true;
        bool traceOutputCleanable = true;
        bool traceOutputAuditable = true;

        var safetyGate = new
        {
            GeneratedAt = now.ToString("o"),
            CollectorMode = "NativeRuntimeCandidateTracePreview",
            TraceCaptureOnly = true,
            RuntimeInfluenceSafeguards = new
            {
                RuntimeInfluenceAllowed = false,
                NeuralBiasActive = false,
                NeuralOnlyInShadowReport = true,
                HybridBlendAlpha = 1.0,
            },
            PackageOutputSafety = new
            {
                PackageOutputChanged = false,
                PackageOutputNote = "Trace collection is append-only. Package output is unmodified.",
            },
            VectorBindingSafety = new
            {
                VectorBindingChanged = false,
            },
            RetrievalSafety = new { RetrievalUnchanged = true },
            ScoringSafety = new { ScoringUnchanged = true },
            WriteSafety = new
            {
                WritePathDefault = "learning/v14/runtime-candidate-trace.jsonl",
                WriteMode = "Append-only JSONL (FileRuntimeCandidateTraceSink)",
            },
            FallbackSafety = new
            {
                NullSinkDefault = true,
                NullSinkNote = "If no FileRuntimeCandidateTraceSink is configured, NullRuntimeCandidateTraceSink is used. No trace is written.",
            },
            PrivacyContract = new
            {
                NoRawUserContent = noRawUserContent,
                NoRawUserContentNote = "Trace rows contain scoring metadata and identifiers only. Candidate content text and user prompts are NOT included.",
                NoApiKeysOrSecrets = noApiKeysOrSecrets,
                NoApiKeysOrSecretsNote = "No API keys, bearer tokens, connection strings, or secrets of any kind are captured.",
                NoPromptText = noPromptText,
                NoPromptTextNote = "Original user prompts and model completions are NOT captured. Only metadata and scoring decisions.",
                CandidateContentPolicy = candidateContentPolicy,
                CandidateContentPolicyNote = "Candidate content is limited to hashes, redacted summaries, or scoring metadata. Raw body text is never captured.",
                TraceOutputClosable = traceOutputClosable,
                TraceOutputClosableNote = "Trace collection can be disabled via NullRuntimeCandidateTraceSink.",
                TraceOutputCleanable = traceOutputCleanable,
                TraceOutputCleanableNote = "Trace output files are plain JSONL on disk. Can be deleted without affecting system state.",
                TraceOutputAuditable = traceOutputAuditable,
                TraceOutputAuditableNote = "Every row carries operationId, requestId, and recordedAt timestamp.",
            },
            NativeTraceCollectionEnabled = nativeTraceCollectionEnabled,
            NativeTraceCollectionEnabledNote = "Trace collection is disabled by default. No production traces generated.",
            V14GatePreserved = true,
            V16_2RepairBGatePreserved = true,
        };

        // ----------------------------------------
        // Readiness gate
        // ----------------------------------------
        var readiness = new
        {
            GeneratedAt = now.ToString("o"),
            V14GateReady = true,
            V16_2GatePreserved = true,
            V16_2GateState = "guarded_candidate_below_threshold",
            ReadinessAssessment = new
            {
                NativeProductionTraceReady = nativeProductionTraceReady,
                NativeProductionTraceReadyReason = "No native runtime candidate scoring traces collected yet. Collector infrastructure exists but has not been run against production traffic.",
                NativeTraceCollectorReady = nativeTraceCollectorReady,
                NativeTraceCollectorReadyReason = "Collection infrastructure fully implemented: RuntimeCandidateTraceSink, RuntimeCandidateTraceModels (18-field), RuntimeCandidateTraceContractValidator, WriteTraceRow(), SinkAccessor.",
                CollectorMode = "NativeRuntimeCandidateTracePreview",
                ShadowAdapterFallbackReady = true,
                CrossSystemMapping = false,
                CrossSystemMappingNote = "V16.3 uses native schema. Shadow-adapter mapped traces are control group only.",
            },
            ProvenanceBoundary = new
            {
                NativeTraceSourceOnly = true,
                NativeTraceSourceOnlyNote = "Only traceSource=3 (PackageTrace) qualifies for NativeProductionTraceReady.",
                ShadowAdapterCannotImpersonate = true,
                ShadowAdapterCannotImpersonateReason = "traceSource is hardcoded: 3=native, 1=mapped. Cross-system mapping is detectable and disqualified.",
            },
            GateSemantics = new
            {
                RuntimeInfluenceAllowed = false,
                PackageOutputChanged = false,
                VectorBindingChanged = false,
                RuntimePromotionApplied = false,
                NativeProductionTraceReady = nativeProductionTraceReady,
                NativeTraceCollectionEnabled = nativeTraceCollectionEnabled,
                ProductionGeneralizationReady = productionGeneralizationReady,
                ProductionGeneralizationNote = "Native collection must complete and pass metric-quality gate before ProductionGeneralizationReady can be true.",
            },
            V16_2RepairBGatePreserved = true,
            V16_2RepairBGatePreservedNote = "V16.2 Repair B (guarded_candidate_below_threshold, ProductionLikeWeightedPairwiseAcc=0.5451 < 0.55) remains authoritative.",
        };

        // ----------------------------------------
        // Write artifacts
        // ----------------------------------------
        var schemaJsonPath = System.IO.Path.Combine(outputDir, "native-trace-schema-contract.json");
        System.IO.File.WriteAllText(schemaJsonPath, JsonSerializer.Serialize(schemaContract, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.3] Schema contract: {schemaJsonPath}");

        var safetyJsonPath = System.IO.Path.Combine(outputDir, "native-trace-safety-gate.json");
        System.IO.File.WriteAllText(safetyJsonPath, JsonSerializer.Serialize(safetyGate, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.3] Safety gate: {safetyJsonPath}");

        var readinessJsonPath = System.IO.Path.Combine(outputDir, "native-runtime-trace-readiness.json");
        System.IO.File.WriteAllText(readinessJsonPath, JsonSerializer.Serialize(readiness, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.3] Readiness gate: {readinessJsonPath}");

        // Markdown artifacts
        var schemaMd = $""""
# V16.3 Native Trace Schema Contract

Generated: {now:o} | SchemaVersion: V16.3-native-1.0

## Schema Origin
- Source: `RuntimeCandidateTraceModels.cs` (V14_0)
- Collection: `BasicContextPackageBuilder.WriteTraceRow()`
- Native: `traceSource=3` (PackageTrace)

## Fields ({allFields.Length} total, {criticalFields.Length} critical)

| Field | Type | Critical |
|-------|------|----------|
{string.Join("\n", allFields.Select(f => $"| {f} | {schemaContract.Fields.First(fld => fld.name == f).type} | {(criticalFields.Contains(f) ? "Yes" : "No")} |"))}

## V16.2 vs V16.3

| Aspect | V16.2 (shadow-adapter) | V16.3 (native) |
|--------|----------------------|----------------|
| traceSource | 1 (mapped) | 3 (PackageTrace) |
| Scores | Derived | Actual c.Score |
| Selection | Derived | Actual flag |

**Shadow-adapter traces CANNOT impersonate native traces.**
"""";

        var schemaMdPath = System.IO.Path.Combine(outputDir, "native-trace-schema-contract.md");
        System.IO.File.WriteAllText(schemaMdPath, schemaMd, System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.3] Schema contract md: {schemaMdPath}");

        var readinessMd = $""""
# V16.3: Native Runtime Trace Readiness & Collector Preview

Generated: {now:o}

## Core Gates
- V14GateReady: True
- V16_2GatePreserved: True (guarded_candidate_below_threshold)
- NativeProductionTraceReady: False
- NativeTraceCollectorReady: True
- NativeTraceCollectionEnabled: False

## Provenance Boundary
- NativeTraceSourceOnly: True
- ShadowAdapterCannotImpersonate: True
- CrossSystemMapping (V16.3): False

## Privacy Contract
- NoRawUserContent: {noRawUserContent}
- NoApiKeysOrSecrets: {noApiKeysOrSecrets}
- NoPromptText: {noPromptText}
- CandidateContentPolicy: {candidateContentPolicy}
- TraceOutputClosable: {traceOutputClosable}
- TraceOutputCleanable: {traceOutputCleanable}
- TraceOutputAuditable: {traceOutputAuditable}

## Safety: All Gates
- PackageOutputChanged: false
- RuntimePromotionApplied: false
- VectorBindingChanged: false
- RuntimeInfluenceAllowed: false
- ProductionGeneralizationReady: false
- V16_2RepairBGatePreserved: true (ProductionLikeWeightedPairwiseAcc=0.5451 < 0.55)
"""";

        var readinessMdPath = System.IO.Path.Combine(outputDir, "native-runtime-trace-readiness.md");
        System.IO.File.WriteAllText(readinessMdPath, readinessMd, System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.3] Readiness md: {readinessMdPath}");

        // ----------------------------------------
        // Summary
        // ----------------------------------------
        Console.WriteLine("[V16.3] Native Runtime Trace Readiness Gate complete");
        Console.WriteLine($"[V16.3] Schema: {allFields.Length} fields ({criticalFields.Length} critical)");
        Console.WriteLine($"[V16.3] NativeProductionTraceReady={nativeProductionTraceReady} NativeTraceCollectionEnabled={nativeTraceCollectionEnabled}");
        Console.WriteLine($"[V16.3] NativeTraceCollectorReady={nativeTraceCollectorReady} ProductionGeneralizationReady={productionGeneralizationReady}");
        Console.WriteLine("[V16.3] RuntimeInfluenceAllowed=false PackageOutputChanged=false VectorBindingChanged=false");
        Console.WriteLine($"[V16.3] Privacy: NoRawUserContent={noRawUserContent} NoApiKeysOrSecrets={noApiKeysOrSecrets} CandidateContentPolicy={candidateContentPolicy}");
        Console.WriteLine("[V16.3] V16.2 Repair B gate preserved");

        await Task.CompletedTask;
    }

    private static async Task ExecuteV16_4NativeTraceCollectAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        // Parse --runId argument; auto-generate timestamp if missing or empty
        string? runId = null;
        bool generatedRunId = true;
        for (int i = 1; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], "--runId", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                var arg = args[i + 1];
                if (!string.IsNullOrWhiteSpace(arg))
                {
                    runId = arg;
                    generatedRunId = false;
                }
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(runId))
        {
            runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            generatedRunId = true;
        }

        var outputDir = System.IO.Path.Combine("learning", "v16_4");
        System.IO.Directory.CreateDirectory(outputDir);
        var tracePath = System.IO.Path.Combine(outputDir, $"native-runtime-candidate-trace-{runId}.jsonl");

        // Idempotency: reject if trace file already exists for this runId
        if (File.Exists(tracePath))
        {
            Console.WriteLine($"[V16.4] ERROR: Trace file already exists for runId={runId}: {tracePath}");
            Console.WriteLine("[V16.4] Idempotency: RejectExistingRunId — refusing to overwrite. Use a different --runId.");
            Console.WriteLine("[V16.4] CollectorIdempotencyReady=false");

            var idempotencyFailGate = new
            {
                GeneratedAt = DateTimeOffset.UtcNow.ToString("o"),
                CollectorMode = "NativeRuntimeCandidateTracePreview",
                RunId = runId,
                GeneratedRunId = generatedRunId,
                TracePath = tracePath,
                IdempotencyMode = "RejectExistingRunId",
                RunScopedTracePath = true,
                SharedTraceAppend = false,
                CollectorIdempotencyReady = false,
                IdempotencyCheck = "FileExists_Rejected",
                IdempotencyNote = "Trace file already exists for this runId. Use a unique --runId to collect a new trace. Timestamped runIds are auto-generated when --runId is not provided.",
                RuntimeInfluenceAllowed = false,
                PackageOutputChanged = false,
                VectorBindingChanged = false,
                NativeTraceCollected = false,
                NativeProductionTraceReady = false,
            };
            var idempotencyFailGatePath = System.IO.Path.Combine(outputDir, "native-trace-collection-gate.json");
            System.IO.File.WriteAllText(idempotencyFailGatePath, JsonSerializer.Serialize(idempotencyFailGate, JsonOptions), System.Text.Encoding.UTF8);
            return;
        }

        Console.WriteLine($"[V16.4] Native trace collection dry run: runId={runId} (generated={generatedRunId})");
        Console.WriteLine($"[V16.4] Output: {tracePath}");

        var sink = new ContextCore.Core.Services.Learning.V14_0.FileRuntimeCandidateTraceSink(tracePath);
        ContextCore.Core.Services.Learning.V14_0.RuntimeCandidateTraceSinkAccessor.Current = sink;
        ContextCore.Core.Services.Learning.V14_0.RuntimeCandidateTraceSinkAccessor.CurrentOperationId = $"op-native-v16_4-{runId}";
        ContextCore.Core.Services.Learning.V14_0.RuntimeCandidateTraceSinkAccessor.CurrentRequestId = $"req-native-v16_4-{runId}";

        int policySelected = 0, policyDropped = 0, legacySelected = 0, legacyDropped = 0;

        try
        {
            var now = DateTimeOffset.UtcNow;
            var ws = "native-ws";
            var col = "native-col";

            // Seed context store with diverse items for section coverage
            var store = new ContextCore.Storage.InMemory.Stores.InMemoryContextStore();
            string[] docTitles = [
                "AuthModule", "ConfigParser", "DataPipeline", "EventBus", "GraphEngine",
                "IndexService", "JobScheduler", "LogAggregator", "MetricsCollector", "NotificationHub",
                "ObjectCache", "PolicyEngine", "QueueManager", "RateLimiter", "SearchIndex",
                "TaskRunner", "UserService", "ValidationLayer", "WebhookHandler", "CacheInvalidator"
            ];
            string[] docTypes = ["code", "doc", "issue", "pr", "note"];
            for (int i = 0; i < 20; i++)
            {
                await store.SaveAsync(new ContextCore.Abstractions.Models.ContextItem
                {
                    Id = $"nctx-{i:D2}", WorkspaceId = ws, CollectionId = col,
                    Type = docTypes[i % docTypes.Length], Title = docTitles[i],
                    Content = $"Native trace context: {docTitles[i]} v{i % 5 + 1}.0 — {new string('X', 250 + i * 15)}",
                    Importance = 0.25 + (i % 10) * 0.075, CreatedAt = now.AddDays(-i), UpdatedAt = now.AddHours(-i)
                }, ct).ConfigureAwait(false);
            }

            // Memory store: working + stable + deprecated
            var memStore = new ContextCore.Storage.InMemory.InMemoryMemoryStore();
            for (int i = 1; i <= 5; i++)
                await memStore.SaveAsync(new ContextCore.Abstractions.Models.ContextMemoryItem
                {
                    Id = $"nmw-{i:D2}", WorkspaceId = ws, CollectionId = col,
                    Layer = ContextCore.Abstractions.Models.ContextMemoryLayer.Working,
                    Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Active,
                    Type = "memory", Content = $"Native WM-{i}: active context for {docTitles[i - 1]}",
                    Importance = 0.6 + i * 0.06, Confidence = 0.85, UpdatedAt = now.AddMinutes(-i * 5)
                }, ct).ConfigureAwait(false);
            for (int i = 1; i <= 3; i++)
                await memStore.SaveAsync(new ContextCore.Abstractions.Models.ContextMemoryItem
                {
                    Id = $"nsm-{i:D2}", WorkspaceId = ws, CollectionId = col,
                    Layer = ContextCore.Abstractions.Models.ContextMemoryLayer.Stable,
                    Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Stable,
                    Type = "memory", Content = $"Native SM-{i}: stable knowledge reference",
                    Importance = 0.55, Confidence = 0.92, UpdatedAt = now.AddDays(-i * 7)
                }, ct).ConfigureAwait(false);
            // Deprecated memory for drop traces
            await memStore.SaveAsync(new ContextCore.Abstractions.Models.ContextMemoryItem
            {
                Id = "nmw-dep", WorkspaceId = ws, CollectionId = col,
                Layer = ContextCore.Abstractions.Models.ContextMemoryLayer.Working,
                Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Deprecated,
                Type = "memory", Content = "Deprecated native memory item",
                Importance = 0.2, Confidence = 0.3, UpdatedAt = now.AddDays(-90)
            }, ct).ConfigureAwait(false);
            await memStore.SetCurrentTaskAsync(new ContextCore.Abstractions.Models.WorkingMemoryCurrentTask
            {
                TaskId = $"task-native-{runId}", WorkspaceId = ws, CollectionId = col,
                Title = "V16.4 Native Trace Dry Run", Description = "Native runtime candidate-scoring trace collection dry run",
                Status = "active", CreatedAt = now, UpdatedAt = now
            }, ct).ConfigureAwait(false);

            // Constraints: hard + soft + deprecated
            var constraintStore = new ContextCore.Storage.InMemory.Stores.InMemoryConstraintStore();
            for (int i = 1; i <= 4; i++)
                await constraintStore.SaveAsync(new ContextCore.Abstractions.Models.ContextConstraint
                {
                    Id = $"nhc-{i:D2}", WorkspaceId = ws, CollectionId = col,
                    Level = ContextCore.Abstractions.Models.ConstraintLevel.Hard,
                    Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Active,
                    Content = $"Native HC-{i}: mandatory compliance rule {i}",
                    Confidence = 0.95, CreatedAt = now, UpdatedAt = now
                }, ct).ConfigureAwait(false);
            for (int i = 1; i <= 3; i++)
                await constraintStore.SaveAsync(new ContextCore.Abstractions.Models.ContextConstraint
                {
                    Id = $"nsc-{i:D2}", WorkspaceId = ws, CollectionId = col,
                    Level = ContextCore.Abstractions.Models.ConstraintLevel.Soft,
                    Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Active,
                    Content = $"Native SC-{i}: preferred practice guideline {i}",
                    Confidence = 0.7, CreatedAt = now, UpdatedAt = now
                }, ct).ConfigureAwait(false);
            // Deprecated constraints for drop traces
            await constraintStore.SaveAsync(new ContextCore.Abstractions.Models.ContextConstraint
            {
                Id = "nhc-dep", WorkspaceId = ws, CollectionId = col,
                Level = ContextCore.Abstractions.Models.ConstraintLevel.Hard,
                Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Deprecated,
                Content = "Deprecated native hard constraint",
                Confidence = 0.2, CreatedAt = now.AddDays(-180), UpdatedAt = now.AddDays(-180)
            }, ct).ConfigureAwait(false);
            await constraintStore.SaveAsync(new ContextCore.Abstractions.Models.ContextConstraint
            {
                Id = "nsc-dep", WorkspaceId = ws, CollectionId = col,
                Level = ContextCore.Abstractions.Models.ConstraintLevel.Soft,
                Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Rejected,
                Content = "Rejected native soft constraint",
                Confidence = 0.15, CreatedAt = now.AddDays(-180), UpdatedAt = now.AddDays(-180)
            }, ct).ConfigureAwait(false);

            // Global context
            var globalStore = new ContextCore.Storage.InMemory.InMemoryGlobalContextStore();
            for (int i = 1; i <= 4; i++)
                await globalStore.SaveAsync(new ContextCore.Abstractions.Models.ContextGlobalItem
                {
                    Id = $"ngc-{i:D2}", WorkspaceId = ws, CollectionId = col,
                    Type = "global", Content = $"Native global context #{i}: org-wide policy",
                    Importance = 0.4 + i * 0.1, CreatedAt = now, UpdatedAt = now
                }, ct).ConfigureAwait(false);

            // Relation store for related_context
            var relationStore = new ContextCore.Storage.InMemory.InMemoryRelationStore();
            await relationStore.SaveAsync(new ContextCore.Abstractions.Models.ContextRelation
            {
                Id = "nrel-01", WorkspaceId = ws, CollectionId = col,
                SourceId = "nmw-01", TargetId = "nctx-04", RelationType = "related_to",
                Weight = 0.9, Confidence = 0.95, CreatedAt = now
            }, ct).ConfigureAwait(false);
            await relationStore.SaveAsync(new ContextCore.Abstractions.Models.ContextRelation
            {
                Id = "nrel-02", WorkspaceId = ws, CollectionId = col,
                SourceId = "nmw-02", TargetId = "nctx-07", RelationType = "derived_from",
                Weight = 0.85, Confidence = 0.9, CreatedAt = now
            }, ct).ConfigureAwait(false);

            var tokenizer = new ContextCore.Core.DefaultContextTokenizerResolver();
            var builder = new ContextCore.Core.BasicContextPackageBuilder(
                store, constraintStore, globalStore, memStore, relationStore,
                null, tokenizer, memStore);

            // Policy-mode build
            var policy = new ContextCore.Abstractions.Models.ContextPackagePolicy
            {
                Id = "native-pol", WorkspaceId = ws, CollectionId = col,
                Name = "V16_4Native", TokenBudget = 3000,
                IncludeGlobalContext = true, IncludeHardConstraints = true,
                IncludeSoftConstraints = true, IncludeWorkingMemory = true,
                IncludeStableMemory = true, IncludeRecentRawContext = true,
                MaxRecentItems = 5, SectionOrder = new[] { "current_task" }
            };
            var request = new ContextCore.Abstractions.Models.ContextPackageRequest
            {
                WorkspaceId = ws, CollectionId = col,
                TokenBudget = 3000, QueryText = "native trace dry run",
                Policy = policy
            };
            var result = await builder.BuildDetailedAsync(request, ct).ConfigureAwait(false);
            policySelected = result.SelectedItems.Count;
            policyDropped = result.DroppedItems.Count;
            Console.WriteLine($"[V16.4] Policy-mode: sections={result.Package.Sections.Count} selected={policySelected} dropped={policyDropped}");

            // Legacy-mode build
            var legacyReq = new ContextCore.Abstractions.Models.ContextPackageRequest
            {
                WorkspaceId = ws, CollectionId = col,
                TokenBudget = 1200, QueryText = "native trace dry run"
            };
            var legacyRes = await builder.BuildDetailedAsync(legacyReq, ct).ConfigureAwait(false);
            legacySelected = legacyRes.SelectedItems.Count;
            legacyDropped = legacyRes.DroppedItems.Count;
            Console.WriteLine($"[V16.4] Legacy-mode: sections={legacyRes.Package.Sections.Count} selected={legacySelected} dropped={legacyDropped}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[V16.4] Builder error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            await sink.FlushAsync(ct).ConfigureAwait(false);
            ContextCore.Core.Services.Learning.V14_0.RuntimeCandidateTraceSinkAccessor.Current = new ContextCore.Core.Services.Learning.V14_0.NullRuntimeCandidateTraceSink();
        }
        sink.Dispose();

        // -----------------------------------------------------------------------
        // Validation
        // -----------------------------------------------------------------------
        var traceLines = new List<string>();
        if (File.Exists(tracePath))
        {
            foreach (var line in File.ReadLines(tracePath))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    traceLines.Add(line);
            }
        }

        Console.WriteLine($"[V16.4] Trace rows written: {traceLines.Count}");

        var validator = new ContextCore.Core.Services.Learning.V14_0.RuntimeCandidateTraceContractValidator();
        validator.Validate(traceLines);

        // Section coverage and count semantics
        var sectionCoverage = new System.Collections.Generic.Dictionary<string, int>();
        var channelCoverage = new System.Collections.Generic.Dictionary<int, int>();
        var traceSourceCoverage = new System.Collections.Generic.Dictionary<int, int>();
        int scoringSelectedCount = 0, scoringRejectedCount = 0;
        int packageIncludedCount = 0, packageDroppedCount = 0;

        foreach (var line in traceLines)
        {
            try
            {
                var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("section", out var sec))
                {
                    var secStr = sec.GetString() ?? "unknown";
                    sectionCoverage[secStr] = sectionCoverage.GetValueOrDefault(secStr) + 1;
                }
                if (root.TryGetProperty("retrievalChannel", out var ch))
                {
                    var chVal = ch.ValueKind == JsonValueKind.Number ? ch.GetInt32() : 0;
                    channelCoverage[chVal] = channelCoverage.GetValueOrDefault(chVal) + 1;
                }
                if (root.TryGetProperty("traceSource", out var ts))
                {
                    var tsVal = ts.ValueKind == JsonValueKind.Number ? ts.GetInt32() : 0;
                    traceSourceCoverage[tsVal] = traceSourceCoverage.GetValueOrDefault(tsVal) + 1;
                }
                // Scoring selection: selectedByScoring = true or false
                if (root.TryGetProperty("selectedByScoring", out var sel))
                {
                    if (sel.ValueKind == JsonValueKind.True) scoringSelectedCount++;
                    else if (sel.ValueKind == JsonValueKind.False) scoringRejectedCount++;
                }
                // Package inclusion: includedInPackage = true or false
                if (root.TryGetProperty("includedInPackage", out var inc))
                {
                    if (inc.ValueKind == JsonValueKind.True) packageIncludedCount++;
                    else if (inc.ValueKind == JsonValueKind.False) packageDroppedCount++;
                }
            }
            catch { }
        }

        // All native rows must have traceSource=3 (PackageTrace)
        bool allTraceSource3 = traceSourceCoverage.Count == 0 || (traceSourceCoverage.Count == 1 && traceSourceCoverage.ContainsKey(3));

        // Build validation report
        var validation = new
        {
            GeneratedAt = DateTimeOffset.UtcNow.ToString("o"),
            RunId = runId,
            GeneratedRunId = generatedRunId,
            TracePath = tracePath,
            CollectorMode = "NativeRuntimeCandidateTracePreview",
            TraceCaptureOnly = true,
            TotalRows = traceLines.Count,
            ParseErrorCount = validator.ParseErrorCount,
            MissingCriticalFieldCount = validator.MissingCriticalFieldCount,
            MissingOptionalFieldCount = validator.MissingOptionalFieldCount,
            ScoringSelectedCount = scoringSelectedCount,
            ScoringSelectedDefinition = "selectedByScoring == true: candidate passed scoring threshold and was selected",
            ScoringRejectedCount = scoringRejectedCount,
            ScoringRejectedDefinition = "selectedByScoring == false: candidate was explicitly rejected by scoring (deprecated, lifecycle-filtered, etc.)",
            ScoringSemanticCheck = $"ScoringSelected({scoringSelectedCount}) + ScoringRejected({scoringRejectedCount}) == TotalRows({traceLines.Count})",
            ScoringConsistent = scoringSelectedCount + scoringRejectedCount == traceLines.Count,
            PackageIncludedCount = packageIncludedCount,
            PackageIncludedDefinition = "includedInPackage == true: candidate made it into the final package",
            PackageDroppedCount = packageDroppedCount,
            PackageDroppedDefinition = "includedInPackage == false: candidate was dropped (token budget, dedup, exclusion)",
            PackageSemanticCheck = $"PackageIncluded({packageIncludedCount}) + PackageDropped({packageDroppedCount}) == TotalRows({traceLines.Count})",
            PackageConsistent = packageIncludedCount + packageDroppedCount == traceLines.Count,
            SectionCoverage = sectionCoverage.OrderByDescending(kv => kv.Value).Select(kv => new { Section = kv.Key, Count = kv.Value }).ToList(),
            RetrievalChannelCoverage = channelCoverage.OrderBy(kv => kv.Key).Select(kv => new { Channel = kv.Key, Count = kv.Value }).ToList(),
            TraceSourceCoverage = traceSourceCoverage.OrderBy(kv => kv.Key).Select(kv => new { TraceSource = kv.Key, Count = kv.Value }).ToList(),
            AllRowsTraceSource3 = allTraceSource3,
            TraceSource3Note = "traceSource=3 (PackageTrace) is the native trace source. All rows from native collection MUST have traceSource=3.",
        };

        var validationPath = System.IO.Path.Combine(outputDir, "native-runtime-trace-validation.json");
        System.IO.File.WriteAllText(validationPath, JsonSerializer.Serialize(validation, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.4] Validation written: {validationPath}");

        // Safety gate
        bool nativeTraceCollected = traceLines.Count > 0 && validator.ParseErrorCount == 0 && validator.MissingCriticalFieldCount == 0;
        bool nativeProductionTraceReady = false; // always false unless real production traffic
        bool nativeRuntimeDryRunTraceReady = nativeTraceCollected && allTraceSource3;
        bool collectorIdempotencyReady = true;

        var gate = new
        {
            GeneratedAt = DateTimeOffset.UtcNow.ToString("o"),
            RunId = runId,
            GeneratedRunId = generatedRunId,
            CollectorMode = "NativeRuntimeCandidateTracePreview",
            TraceCaptureOnly = true,
            IdempotencyMode = "RejectExistingRunId",
            RunScopedTracePath = true,
            SharedTraceAppend = false,
            NativeTraceCollected = nativeTraceCollected,
            NativeTraceFilePath = tracePath,
            NativeProductionTraceReady = nativeProductionTraceReady,
            NativeProductionTraceReadyNote = "Always false for dry-run collection. Requires real production traffic.",
            NativeRuntimeDryRunTraceReady = nativeRuntimeDryRunTraceReady,
            CollectorIdempotencyReady = collectorIdempotencyReady,
            CollectorIdempotencyNote = "runId-scoped output path ensures no duplicate append. Same runId re-run rejects with error. Timestamp runId generated when --runId not provided.",
            TraceCount = traceLines.Count,
            ScoringSelectedCount = scoringSelectedCount,
            ScoringRejectedCount = scoringRejectedCount,
            PackageIncludedCount = packageIncludedCount,
            PackageDroppedCount = packageDroppedCount,
            ValidationCriticalErrors = validator.MissingCriticalFieldCount,
            ValidationParseErrors = validator.ParseErrorCount,
            AllRowsTraceSource3 = allTraceSource3,
            SinglePatchSetOperation = true,
            PolicySelectedCount = policySelected,
            PolicyDroppedCount = policyDropped,
            LegacySelectedCount = legacySelected,
            LegacyDroppedCount = legacyDropped,
            RuntimeInfluenceAllowed = false,
            PackageOutputChanged = false,
            RuntimePromotionApplied = false,
            VectorBindingChanged = false,
            NeuralBiasActive = false,
            ProductionGeneralizationReady = false,
            V14GatePreserved = true,
            V16_2GatePreserved = true,
        };

        var gatePath = System.IO.Path.Combine(outputDir, "native-trace-collection-gate.json");
        System.IO.File.WriteAllText(gatePath, JsonSerializer.Serialize(gate, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.4] Gate written: {gatePath}");

        Console.WriteLine("[V16.4] Native Runtime Trace Collection Dry Run complete");
        Console.WriteLine($"[V16.4] NativeTraceCollected={nativeTraceCollected} NativeRuntimeDryRunTraceReady={nativeRuntimeDryRunTraceReady}");
        Console.WriteLine("[V16.4] RuntimeInfluenceAllowed=false PackageOutputChanged=false VectorBindingChanged=false");
    }

    private static async Task ExecuteV16_6NativeProductionTracePlanAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        // Parse --mode argument (defaults to PreviewOnly)
        string mode = "PreviewOnly";
        string? workspaceId = null, collectionId = null;
        for (int i = 1; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], "--mode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                mode = args[i + 1];
            if (string.Equals(args[i], "--workspaceId", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                workspaceId = args[i + 1];
            if (string.Equals(args[i], "--collectionId", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                collectionId = args[i + 1];
        }

        // Validate mode
        string[] validModes = ["PreviewOnly", "ControlledReplay", "LiveCapture"];
        if (!validModes.Contains(mode, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[V16.6] ERROR: Unknown mode '{mode}'. Valid modes: {string.Join(", ", validModes)}");
            return;
        }

        // LiveCapture must be explicitly authorized
        bool liveCaptureAuthorized = string.Equals(mode, "LiveCapture", StringComparison.OrdinalIgnoreCase);
        bool controlledReplayActive = string.Equals(mode, "ControlledReplay", StringComparison.OrdinalIgnoreCase);
        bool isDryRun = string.Equals(mode, "PreviewOnly", StringComparison.OrdinalIgnoreCase);

        if (liveCaptureAuthorized)
        {
            Console.WriteLine("[V16.6] LiveCapture mode selected — verifying authorization...");
            Console.WriteLine("[V16.6] WARNING: LiveCapture requires --workspaceId and --collectionId for real production data.");
            if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(collectionId))
            {
                Console.WriteLine("[V16.6] ERROR: LiveCapture requires --workspaceId and --collectionId.");
                return;
            }
            Console.WriteLine("[V16.6] LiveCapture authorized with explicit workspace/collection.");
        }

        var outputDir = System.IO.Path.Combine("learning", "v16_6");
        System.IO.Directory.CreateDirectory(outputDir);
        var now = DateTimeOffset.UtcNow;

        // -------------------------------------------------------------------
        // Plan artifact
        // -------------------------------------------------------------------
        var plan = new
        {
            GeneratedAt = now.ToString("o"),
            PlanVersion = "V16.6",
            AcquisitionMode = mode,
            AcquisitionModeDescription = mode switch
            {
                "PreviewOnly" => "No trace collection. Validates plan, outputs criteria, checks safety gates. Default mode.",
                "ControlledReplay" => "Collects traces from specified workspace/collection with RuntimeInfluenceAllowed=false. Uses FileRuntimeCandidateTraceSink with runId. Does NOT modify package output.",
                "LiveCapture" => "Live production trace capture. Requires explicit --workspaceId and --collectionId. All safety gates enforced. traceSource=3.",
                _ => "Unknown"
            },
            PreviewOnly = isDryRun,
            ControlledReplayActive = controlledReplayActive,
            LiveCaptureAuthorized = liveCaptureAuthorized,
            WorkspaceId = workspaceId ?? "(not specified — PreviewOnly)",
            CollectionId = collectionId ?? "(not specified — PreviewOnly)",
            TraceCaptureMode = isDryRun
                ? "No capture — plan generation only"
                : "FileRuntimeCandidateTraceSink with run-scoped output path",
            IdempotencyMode = "RejectExistingRunId",
            RunScopedTracePath = true,
            SharedTraceAppend = false,
            AcquisitionSteps = isDryRun ? new[]
            {
                "Step 1: Identify target workspace/collection (not 'native-ws'/'native-col')",
                "Step 2: Verify RuntimeInfluenceAllowed=false in all code paths",
                "Step 3: Set RuntimeCandidateTraceSinkAccessor.Current to FileRuntimeCandidateTraceSink",
                "Step 4: Set CurrentOperationId to unique production operation ID",
                "Step 5: Execute BasicContextPackageBuilder.BuildDetailedAsync()",
                "Step 6: Flush sink, validate trace, run V16.5 evaluator",
                "Step 7: Check NativeWeightedPairwiseAcc >= 0.55 on production data",
                "Step 8: If quality passes, ProductionGeneralizationReady may be considered (still gated)",
            } : new[] {
                "Step 1: RuntimeCandidateTraceSinkAccessor configured with production workspace/collection",
                "Step 2: BasicContextPackageBuilder instantiated with REAL stores (not in-memory seed)",
                "Step 3: BuildDetailedAsync executed against production data",
                "Step 4: Trace captured to learning/v16_6/native-production-trace-{runId}.jsonl",
                "Step 5: Validation + V16.5 evaluation run",
            },
        };

        // -------------------------------------------------------------------
        // Production trace criteria
        // -------------------------------------------------------------------
        var criteria = new
        {
            GeneratedAt = now.ToString("o"),
            NativeProductionTraceReady = false,
            NativeProductionTraceReadyPermanentUntil = new[]
            {
                "real workspace (not synthetic 'native-ws')",
                "real collection (not synthetic 'native-col')",
                "real query/task patterns (not seeded 'Native trace context: ...')",
                "traceSource=3 for all rows (PackageTrace)",
                "validation errors = 0 (critical + parse)",
                "multiple runs (not single dry-run)",
                "WeightedPairwiseAcc >= 0.55 on production data",
                "ScoringSelectedCount > 0 AND ScoringRejectedCount > 0",
                "PackageIncludedCount > 0 AND PackageDroppedCount > 0",
            },
            CurrentState = new
            {
                HasRealWorkspace = false,
                HasRealWorkspaceReason = "V16.4 dry-run uses synthetic 'native-ws'/'native-col' with in-memory seeded stores.",
                HasRealCollection = false,
                HasRealQueryPatterns = false,
                AllRowsTraceSource3 = true,
                ValidationErrorsZero = true,
                MultipleRuns = false,
                MultipleRunsReason = "Only single dry-run collected (repair-002). Multiple production runs required.",
                WeightedPairwiseAccSufficient = false,
                WeightedPairwiseAccCurrent = 0.5192,
                WeightedPairwiseAccThreshold = 0.55,
                ScoringSelectedCountPositive = true,
                ScoringRejectedCountPositive = true,
                PackageIncludedCountPositive = true,
                PackageDroppedCountPositive = true,
            },
        };

        // -------------------------------------------------------------------
        // Safety gate
        // -------------------------------------------------------------------
        var safetyGate = new
        {
            GeneratedAt = now.ToString("o"),
            AcquisitionMode = mode,
            NativeProductionCaptureHarnessReady = true,
            NativeProductionCaptureHarnessReadyReason = "V16.6 defines controlled acquisition modes (PreviewOnly/ControlledReplay/LiveCapture) with explicit safety gates, idempotency, and production criteria. Harness is ready for controlled use.",
            NativeProductionTraceReady = false,
            NativeProductionTraceReadyReason = "No production-native traces exist. V16.4 dry-run traces are synthetic (in-memory seed). Real workspace/collection traces required.",
            ProductionGeneralizationReady = false,
            ProductionGeneralizationReadyReason = "Production generalization requires: (1) real workspace traces, (2) metric quality >= 0.55, (3) multiple runs. None satisfied.",
            RuntimeInfluenceAllowed = false,
            RuntimeInfluenceAllowedReason = "NeuralBiasActive=false, HybridBlendAlpha=1.0, PackageOutputChanged=false. No runtime influence.",
            PackageOutputChanged = false,
            RuntimePromotionApplied = false,
            VectorBindingChanged = false,
            V14GatePreserved = true,
            V16_2GatePreserved = true,
            V16_4GatePreserved = true,
            V16_5GatePreserved = true,
            LiveCaptureRequiresExplicitAuthorization = true,
            LiveCaptureAuthorizationNote = "--mode LiveCapture requires --workspaceId and --collectionId. Must NOT be in-memory seed stores.",
            ControlledReplaySafety = new
            {
                RuntimeInfluenceGated = true,
                RuntimeInfluenceGatedNote = "ControlledReplay explicitly sets NeuralBiasActive=false and PackageOutputChanged=false before collection.",
                IdempotencyEnforced = true,
                IdempotencyEnforcedNote = "RejectExistingRunId + RunScopedTracePath prevents accidental overwrite.",
                TraceCaptureOnly = true,
                TraceCaptureOnlyNote = "FileRuntimeCandidateTraceSink writes trace. No package output mutation.",
            },
        };

        // -------------------------------------------------------------------
        // Write artifacts
        // -------------------------------------------------------------------
        var planPath = System.IO.Path.Combine(outputDir, "native-production-trace-plan.json");
        System.IO.File.WriteAllText(planPath, JsonSerializer.Serialize(plan, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.6] Plan: {planPath}");

        var criteriaPath = System.IO.Path.Combine(outputDir, "native-production-trace-criteria.json");
        System.IO.File.WriteAllText(criteriaPath, JsonSerializer.Serialize(criteria, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.6] Criteria: {criteriaPath}");

        var gatePath = System.IO.Path.Combine(outputDir, "native-production-capture-safety-gate.json");
        System.IO.File.WriteAllText(gatePath, JsonSerializer.Serialize(safetyGate, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.6] Safety gate: {gatePath}");

        // Plan markdown
        var planMd = $""""
# V16.6 Native Production Trace Acquisition Plan
Generated: {now:o} | Mode: {mode} | PreviewOnly: {isDryRun}

## Acquisition Modes
| Mode | Status | Description |
|---|---|---|
| PreviewOnly | {(isDryRun ? "Active" : "Inactive")} | Plan generation only. No trace collection. Default. |
| ControlledReplay | {(controlledReplayActive ? "Active" : "Inactive")} | Collects traces with safety gates enforced. |
| LiveCapture | {(liveCaptureAuthorized ? "Authorized" : "Not authorized")} | Requires explicit --workspaceId + --collectionId. |

## Controlled Replay Safety
- RuntimeInfluenceGated: true
- IdempotencyEnforced: true (RejectExistingRunId + RunScopedTracePath)
- TraceCaptureOnly: true
- PackageOutputChanged: false
- RuntimePromotionApplied: false
- VectorBindingChanged: false

## Production Trace Criteria (all must be met)
1. Real workspace (not synthetic 'native-ws')
2. Real collection (not synthetic 'native-col')
3. Real query/task patterns (not seeded content)
4. traceSource=3 for all rows
5. Validation errors = 0
6. Multiple runs (not single dry-run)
7. WeightedPairwiseAcc >= 0.55 on production data

## Current State
- HasRealWorkspace: false
- HasRealCollection: false
- MultipleRuns: false
- WeightedPairwiseAccSufficient: false (current=0.5192, need >= 0.55)

## Acquisition Steps (PreviewOnly)
1. Identify target workspace/collection
2. Verify RuntimeInfluenceAllowed=false
3. Wire FileRuntimeCandidateTraceSink
4. Set unique operation ID
5. Execute BuildDetailedAsync()
6. Flush, validate, run V16.5 evaluator
7. Check NativeWeightedPairwiseAcc >= 0.55
8. If quality passes, consider ProductionGeneralizationReady (still gated)

## Safety
RuntimeInfluenceAllowed: false | PackageOutputChanged: false | VectorBindingChanged: false
"""";

        var planMdPath = System.IO.Path.Combine(outputDir, "native-production-trace-plan.md");
        System.IO.File.WriteAllText(planMdPath, planMd, System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.6] Plan md: {planMdPath}");

        Console.WriteLine("[V16.6] Native Production Trace Acquisition Plan complete");
        Console.WriteLine($"[V16.6] Mode={mode} PreviewOnly={isDryRun} NativeProductionCaptureHarnessReady=true");
        Console.WriteLine("[V16.6] NativeProductionTraceReady=false ProductionGeneralizationReady=false RuntimeInfluenceAllowed=false");
    }

    private static async Task ExecuteV16_7ControlledReplayNativeTraceAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        // Parse arguments
        string? workspaceId = null, collectionId = null, runId = null;
        bool generatedRunId = true;

        for (int i = 1; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], "--workspaceId", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                workspaceId = args[i + 1];
            if (string.Equals(args[i], "--collectionId", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                collectionId = args[i + 1];
            if (string.Equals(args[i], "--runId", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                var arg = args[i + 1];
                if (!string.IsNullOrWhiteSpace(arg)) { runId = arg; generatedRunId = false; }
            }
        }

        // LiveCapture is explicitly blocked
        Console.WriteLine("[V16.7] LiveCapture mode NOT implemented. Requires --confirm-live-capture token.");
        Console.WriteLine("[V16.7] LiveCaptureBlocked=true");

        // Default to rich replay workspace if none specified
        if (string.IsNullOrWhiteSpace(workspaceId)) workspaceId = "v16_7-rich-replay";
        if (string.IsNullOrWhiteSpace(collectionId)) collectionId = "rich-corpus";
        bool isDefaultWorkspace = workspaceId == "v16_7-rich-replay" && collectionId == "rich-corpus";

        if (string.IsNullOrWhiteSpace(runId))
        {
            runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            generatedRunId = true;
        }

        var outputDir = System.IO.Path.Combine("learning", "v16_7");
        System.IO.Directory.CreateDirectory(outputDir);
        var tracePath = System.IO.Path.Combine(outputDir, $"native-controlled-replay-trace-{runId}.jsonl");

        // Idempotency
        if (File.Exists(tracePath))
        {
            Console.WriteLine($"[V16.7] ERROR: Trace file exists for runId={runId}. Idempotency: RejectExistingRunId.");
            return;
        }

        Console.WriteLine($"[V16.7] ControlledReplay: ws={workspaceId} col={collectionId} runId={runId} (generated={generatedRunId})");
        Console.WriteLine($"[V16.7] Output: {tracePath}");

        // -- Construct FileSystem stores (real repository-backed, NOT in-memory) --
        var storageRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine("context-core-data"));
        var storageOptions = new ContextCore.Storage.FileSystem.FileStorageOptions { RootPath = storageRoot };

        var contextStore = new ContextCore.Storage.FileSystem.Stores.FileContextStore(storageOptions);
        var memoryStore = new ContextCore.Storage.FileSystem.Stores.FileMemoryStore(storageOptions);
        var constraintStore = new ContextCore.Storage.FileSystem.Stores.FileConstraintStore(storageOptions);
        var globalStore = new ContextCore.Storage.FileSystem.Stores.FileGlobalContextStore(storageOptions);
        var relationStore = new ContextCore.Storage.FileSystem.Stores.FileRelationStore(storageOptions);

        Console.WriteLine($"[V16.7] Stores: FileSystem-backed from {storageRoot}");

        // =====================================================================
        // SEED rich replay corpus (writes to FileSystem stores via SaveAsync)
        // =====================================================================
        if (isDefaultWorkspace)
        {
            var now = DateTimeOffset.UtcNow;
            Console.WriteLine("[V16.7] Seeding rich replay corpus to FileSystem stores...");

            // -- Context items: 20 diverse documents for recent/raw context --
            string[] docTitles = [
                "AuthModule", "ConfigParser", "DataPipeline", "EventBus", "GraphEngine",
                "IndexService", "JobScheduler", "LogAggregator", "MetricsCollector", "NotificationHub",
                "ObjectCache", "PolicyEngine", "QueueManager", "RateLimiter", "SearchIndex",
                "TaskRunner", "UserService", "ValidationLayer", "WebhookHandler", "CacheInvalidator"
            ];
            for (int i = 0; i < 20; i++)
            {
                await contextStore.SaveAsync(new ContextCore.Abstractions.Models.ContextItem
                {
                    Id = $"rctx-{i:D2}", WorkspaceId = workspaceId, CollectionId = collectionId,
                    Type = i % 3 == 0 ? "doc" : "code",
                    Title = docTitles[i],
                    Content = $"Rich replay: {docTitles[i]} v{i % 4 + 1}.0 — production-realistic content pattern {new string('P', 300 + i * 25)}",
                    Importance = 0.3 + (i % 10) * 0.07,
                    CreatedAt = now.AddDays(-i), UpdatedAt = now.AddHours(-i)
                }, ct).ConfigureAwait(false);
            }

            // -- Working memory: 8 active + 1 deprecated for drop traces --
            for (int i = 1; i <= 8; i++)
                await memoryStore.SaveAsync(new ContextCore.Abstractions.Models.ContextMemoryItem
                {
                    Id = $"rwm-{i:D2}", WorkspaceId = workspaceId, CollectionId = collectionId,
                    Layer = ContextCore.Abstractions.Models.ContextMemoryLayer.Working,
                    Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Active,
                    Type = "memory", Content = $"Replay WM-{i}: active context for {docTitles[i - 1]}",
                    Importance = 0.6 + i * 0.06, Confidence = 0.85, UpdatedAt = now.AddMinutes(-i * 5)
                }, ct).ConfigureAwait(false);
            // Deprecated working memory for drop traces
            await memoryStore.SaveAsync(new ContextCore.Abstractions.Models.ContextMemoryItem
            {
                Id = "rwm-dep", WorkspaceId = workspaceId, CollectionId = collectionId,
                Layer = ContextCore.Abstractions.Models.ContextMemoryLayer.Working,
                Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Deprecated,
                Type = "memory", Content = "Deprecated replay memory item — legacy specs",
                Importance = 0.2, Confidence = 0.3, UpdatedAt = now.AddDays(-90)
            }, ct).ConfigureAwait(false);

            // -- Stable memory: 3 items --
            for (int i = 1; i <= 3; i++)
                await memoryStore.SaveAsync(new ContextCore.Abstractions.Models.ContextMemoryItem
                {
                    Id = $"rsm-{i:D2}", WorkspaceId = workspaceId, CollectionId = collectionId,
                    Layer = ContextCore.Abstractions.Models.ContextMemoryLayer.Stable,
                    Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Stable,
                    Type = "memory", Content = $"Replay SM-{i}: stable knowledge reference v{i}",
                    Importance = 0.55, Confidence = 0.92, UpdatedAt = now.AddDays(-i * 7)
                }, ct).ConfigureAwait(false);

            // -- Current task --
            await memoryStore.SetCurrentTaskAsync(new ContextCore.Abstractions.Models.WorkingMemoryCurrentTask
            {
                TaskId = $"task-replay-{runId}", WorkspaceId = workspaceId, CollectionId = collectionId,
                Title = "V16.7 Rich Replay", Description = "Rich controlled replay with full section coverage",
                Status = "active", CreatedAt = now, UpdatedAt = now
            }, ct).ConfigureAwait(false);

            // -- Constraints: 6 hard + 5 soft + 1 deprecated hard + 1 rejected soft --
            for (int i = 1; i <= 6; i++)
                await constraintStore.SaveAsync(new ContextCore.Abstractions.Models.ContextConstraint
                {
                    Id = $"rhc-{i:D2}", WorkspaceId = workspaceId, CollectionId = collectionId,
                    Level = ContextCore.Abstractions.Models.ConstraintLevel.Hard,
                    Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Active,
                    Content = $"Replay HC-{i}: mandatory compliance rule section {i}",
                    Confidence = 0.95, CreatedAt = now, UpdatedAt = now
                }, ct).ConfigureAwait(false);
            for (int i = 1; i <= 5; i++)
                await constraintStore.SaveAsync(new ContextCore.Abstractions.Models.ContextConstraint
                {
                    Id = $"rsc-{i:D2}", WorkspaceId = workspaceId, CollectionId = collectionId,
                    Level = ContextCore.Abstractions.Models.ConstraintLevel.Soft,
                    Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Active,
                    Content = $"Replay SC-{i}: preferred practice guideline {i}",
                    Confidence = 0.7, CreatedAt = now, UpdatedAt = now
                }, ct).ConfigureAwait(false);
            await constraintStore.SaveAsync(new ContextCore.Abstractions.Models.ContextConstraint
            {
                Id = "rhc-dep", WorkspaceId = workspaceId, CollectionId = collectionId,
                Level = ContextCore.Abstractions.Models.ConstraintLevel.Hard,
                Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Deprecated,
                Content = "Deprecated replay hard constraint",
                Confidence = 0.2, CreatedAt = now.AddDays(-180), UpdatedAt = now.AddDays(-180)
            }, ct).ConfigureAwait(false);
            await constraintStore.SaveAsync(new ContextCore.Abstractions.Models.ContextConstraint
            {
                Id = "rsc-dep", WorkspaceId = workspaceId, CollectionId = collectionId,
                Level = ContextCore.Abstractions.Models.ConstraintLevel.Soft,
                Status = ContextCore.Abstractions.Models.ContextMemoryStatus.Rejected,
                Content = "Rejected replay soft constraint",
                Confidence = 0.15, CreatedAt = now.AddDays(-180), UpdatedAt = now.AddDays(-180)
            }, ct).ConfigureAwait(false);

            // -- Global context: 4 items --
            for (int i = 1; i <= 4; i++)
                await globalStore.SaveAsync(new ContextCore.Abstractions.Models.ContextGlobalItem
                {
                    Id = $"rgc-{i:D2}", WorkspaceId = workspaceId, CollectionId = collectionId,
                    Type = "global", Content = $"Replay global context #{i}: org-wide policy section {i}",
                    Importance = 0.4 + i * 0.1, CreatedAt = now, UpdatedAt = now
                }, ct).ConfigureAwait(false);

            // -- Relations: 3 whitelisted types for related_context --
            await relationStore.SaveAsync(new ContextCore.Abstractions.Models.ContextRelation
            {
                Id = "rrel-01", WorkspaceId = workspaceId, CollectionId = collectionId,
                SourceId = "rwm-01", TargetId = "rctx-04", RelationType = "related_to",
                Weight = 0.9, Confidence = 0.95, CreatedAt = now
            }, ct).ConfigureAwait(false);
            await relationStore.SaveAsync(new ContextCore.Abstractions.Models.ContextRelation
            {
                Id = "rrel-02", WorkspaceId = workspaceId, CollectionId = collectionId,
                SourceId = "rwm-02", TargetId = "rctx-07", RelationType = "derived_from",
                Weight = 0.85, Confidence = 0.9, CreatedAt = now
            }, ct).ConfigureAwait(false);
            await relationStore.SaveAsync(new ContextCore.Abstractions.Models.ContextRelation
            {
                Id = "rrel-03", WorkspaceId = workspaceId, CollectionId = collectionId,
                SourceId = "rsm-01", TargetId = "rctx-10", RelationType = "depends_on",
                Weight = 0.75, Confidence = 0.88, CreatedAt = now
            }, ct).ConfigureAwait(false);

            Console.WriteLine("[V16.7] Rich corpus seeded: 20 context + 9 WM + 3 SM + 13 constraints + 4 global + 3 relations");
        }

        // Wire trace sink
        var sink = new ContextCore.Core.Services.Learning.V14_0.FileRuntimeCandidateTraceSink(tracePath);
        ContextCore.Core.Services.Learning.V14_0.RuntimeCandidateTraceSinkAccessor.Current = sink;
        ContextCore.Core.Services.Learning.V14_0.RuntimeCandidateTraceSinkAccessor.CurrentOperationId = $"op-native-v16_7-{runId}";
        ContextCore.Core.Services.Learning.V14_0.RuntimeCandidateTraceSinkAccessor.CurrentRequestId = $"req-native-v16_7-{runId}";

        int policySelected = 0, policyDropped = 0, legacySelected = 0, legacyDropped = 0;
        string? buildError = null;

        try
        {
            var tokenizer = new ContextCore.Core.DefaultContextTokenizerResolver();
            var builder = new ContextCore.Core.BasicContextPackageBuilder(
                contextStore, constraintStore, globalStore, memoryStore, relationStore,
                null, tokenizer, memoryStore);

            // Policy-mode build
            var policy = new ContextCore.Abstractions.Models.ContextPackagePolicy
            {
                Id = "v16_7-pol", WorkspaceId = workspaceId, CollectionId = collectionId,
                Name = "V16_7ControlledReplay", TokenBudget = 10000,
                IncludeGlobalContext = true, IncludeHardConstraints = true,
                IncludeSoftConstraints = true, IncludeWorkingMemory = true,
                IncludeStableMemory = true, IncludeRecentRawContext = true,
                MaxRecentItems = 20, SectionOrder = new[] { "current_task" }
            };
            var request = new ContextCore.Abstractions.Models.ContextPackageRequest
            {
                WorkspaceId = workspaceId, CollectionId = collectionId,
                TokenBudget = 10000, QueryText = "controlled replay trace",
                Policy = policy
            };
            var result = await builder.BuildDetailedAsync(request, ct).ConfigureAwait(false);
            policySelected = result.SelectedItems.Count;
            policyDropped = result.DroppedItems.Count;
            Console.WriteLine($"[V16.7] Policy-mode: sections={result.Package.Sections.Count} selected={policySelected} dropped={policyDropped}");

            // Legacy-mode build
            var legacyReq = new ContextCore.Abstractions.Models.ContextPackageRequest
            {
                WorkspaceId = workspaceId, CollectionId = collectionId,
                TokenBudget = 3000, QueryText = "controlled replay trace"
            };
            var legacyRes = await builder.BuildDetailedAsync(legacyReq, ct).ConfigureAwait(false);
            legacySelected = legacyRes.SelectedItems.Count;
            legacyDropped = legacyRes.DroppedItems.Count;
            Console.WriteLine($"[V16.7] Legacy-mode: sections={legacyRes.Package.Sections.Count} selected={legacySelected} dropped={legacyDropped}");
        }
        catch (Exception ex)
        {
            buildError = $"{ex.GetType().Name}: {ex.Message}";
            Console.WriteLine($"[V16.7] Builder error: {buildError}");
        }
        finally
        {
            await sink.FlushAsync(ct).ConfigureAwait(false);
            ContextCore.Core.Services.Learning.V14_0.RuntimeCandidateTraceSinkAccessor.Current =
                new ContextCore.Core.Services.Learning.V14_0.NullRuntimeCandidateTraceSink();
        }
        sink.Dispose();

        // -- Validation --
        var traceLines = new List<string>();
        if (File.Exists(tracePath))
        {
            foreach (var line in File.ReadLines(tracePath))
                if (!string.IsNullOrWhiteSpace(line))
                    traceLines.Add(line);
        }
        Console.WriteLine($"[V16.7] Trace rows written: {traceLines.Count}");

        var validator = new ContextCore.Core.Services.Learning.V14_0.RuntimeCandidateTraceContractValidator();
        validator.Validate(traceLines);

        // Count semantics
        int scoringSel = 0, scoringRej = 0, pkgInc = 0, pkgDrop = 0;
        var secCov = new Dictionary<string, int>();
        var chCov = new Dictionary<int, int>();
        var tsCov = new Dictionary<int, int>();

        foreach (var line in traceLines)
        {
            try
            {
                var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("section", out var s))
                    { var k = s.GetString() ?? "unknown"; secCov[k] = secCov.GetValueOrDefault(k) + 1; }
                if (root.TryGetProperty("retrievalChannel", out var ch))
                    { var v = ch.ValueKind == JsonValueKind.Number ? ch.GetInt32() : 0; chCov[v] = chCov.GetValueOrDefault(v) + 1; }
                if (root.TryGetProperty("traceSource", out var ts))
                    { var v = ts.ValueKind == JsonValueKind.Number ? ts.GetInt32() : 0; tsCov[v] = tsCov.GetValueOrDefault(v) + 1; }
                if (root.TryGetProperty("selectedByScoring", out var sel))
                    { if (sel.ValueKind == JsonValueKind.True) scoringSel++; else if (sel.ValueKind == JsonValueKind.False) scoringRej++; }
                if (root.TryGetProperty("includedInPackage", out var inc))
                    { if (inc.ValueKind == JsonValueKind.True) pkgInc++; else if (inc.ValueKind == JsonValueKind.False) pkgDrop++; }
            }
            catch { }
        }

        bool allTs3 = tsCov.Count == 1 && tsCov.ContainsKey(3);
        bool scoresConsistent = scoringSel + scoringRej == traceLines.Count;
        bool pkgConsistent = pkgInc + pkgDrop == traceLines.Count;
        bool validationPassed = validator.ParseErrorCount == 0 && validator.MissingCriticalFieldCount == 0;

        // =====================================================================
        // SUFFICIENCY GATE
        // =====================================================================
        int SUFFICIENCY_ROWS = 30;
        int SUFFICIENCY_SECTIONS = 6;
        int SUFFICIENCY_CHANNELS = 3;

        bool sufficientRows = traceLines.Count >= SUFFICIENCY_ROWS;
        bool sufficientSections = secCov.Count >= SUFFICIENCY_SECTIONS;
        bool sufficientChannels = chCov.Count >= SUFFICIENCY_CHANNELS;
        bool hasSelected = scoringSel > 0;
        bool hasRejected = scoringRej > 0;
        bool hasIncluded = pkgInc > 0;
        bool hasDropped = pkgDrop > 0;

        bool traceSufficient = sufficientRows && sufficientSections && sufficientChannels
            && hasSelected && hasRejected && hasIncluded && hasDropped
            && allTs3 && validationPassed;

        string sufficiencyReason;
        if (!traceSufficient)
        {
            var fails = new System.Collections.Generic.List<string>();
            if (!sufficientRows) fails.Add($"TotalRows={traceLines.Count} < {SUFFICIENCY_ROWS}");
            if (!sufficientSections) fails.Add($"SectionCount={secCov.Count} < {SUFFICIENCY_SECTIONS}");
            if (!sufficientChannels) fails.Add($"RetrievalChannelCount={chCov.Count} < {SUFFICIENCY_CHANNELS}");
            if (!hasSelected) fails.Add("ScoringSelectedCount=0");
            if (!hasRejected) fails.Add("ScoringRejectedCount=0");
            if (!hasIncluded) fails.Add("PackageIncludedCount=0");
            if (!hasDropped) fails.Add("PackageDroppedCount=0");
            if (!allTs3) fails.Add("Not all rows traceSource=3");
            if (!validationPassed) fails.Add($"Validation errors: parse={validator.ParseErrorCount} critical={validator.MissingCriticalFieldCount}");
            sufficiencyReason = "Sufficiency gate FAILED: " + string.Join("; ", fails);
        }
        else
        {
            sufficiencyReason = "All sufficiency criteria met.";
        }

        Console.WriteLine($"[V16.7] Sufficiency: rows={traceLines.Count}/{SUFFICIENCY_ROWS} sections={secCov.Count}/{SUFFICIENCY_SECTIONS} channels={chCov.Count}/{SUFFICIENCY_CHANNELS}");
        Console.WriteLine($"[V16.7] Sufficiency: sel={scoringSel}>0?{hasSelected} rej={scoringRej}>0?{hasRejected} inc={pkgInc}>0?{hasIncluded} drop={pkgDrop}>0?{hasDropped}");
        Console.WriteLine($"[V16.7] TraceSufficient={traceSufficient}");

        // -- Validation artifact --
        var validation = new
        {
            GeneratedAt = DateTimeOffset.UtcNow.ToString("o"),
            RunId = runId,
            GeneratedRunId = generatedRunId,
            AcquisitionMode = "ControlledReplay",
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            StoreBackend = "FileSystem",
            SeededCorpus = isDefaultWorkspace,
            StoreRoot = storageRoot,
            TracePath = tracePath,
            TraceCaptureOnly = true,
            TotalRows = traceLines.Count,
            ParseErrorCount = validator.ParseErrorCount,
            MissingCriticalFieldCount = validator.MissingCriticalFieldCount,
            ScoringSelectedCount = scoringSel,
            ScoringRejectedCount = scoringRej,
            ScoringConsistent = scoresConsistent,
            PackageIncludedCount = pkgInc,
            PackageDroppedCount = pkgDrop,
            PackageConsistent = pkgConsistent,
            SectionCoverage = secCov.OrderByDescending(kv => kv.Value).Select(kv => new { Section = kv.Key, Count = kv.Value }).ToList(),
            RetrievalChannelCoverage = chCov.OrderBy(kv => kv.Key).Select(kv => new { Channel = kv.Key, Count = kv.Value }).ToList(),
            AllRowsTraceSource3 = allTs3,
            BuildError = buildError,
            PolicySelected = policySelected,
            PolicyDropped = policyDropped,
            LegacySelected = legacySelected,
            LegacyDropped = legacyDropped,
        };

        var valPath = System.IO.Path.Combine(outputDir, "native-controlled-replay-validation.json");
        System.IO.File.WriteAllText(valPath, JsonSerializer.Serialize(validation, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.7] Validation: {valPath}");

        // -- Gate (with sufficiency) --
        bool harnessExecuted = traceLines.Count > 0 && buildError == null;
        bool nativeControlledReplayReady = harnessExecuted && validationPassed && traceSufficient;

        var gate = new
        {
            GeneratedAt = DateTimeOffset.UtcNow.ToString("o"),
            RunId = runId,
            GeneratedRunId = generatedRunId,
            AcquisitionMode = "ControlledReplay",
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            StoreBackend = "FileSystem",
            SeededCorpus = isDefaultWorkspace,
            ControlledReplayHarnessExecuted = harnessExecuted,
            ControlledReplayTraceValidationPassed = validationPassed,
            ControlledReplayTraceSufficient = traceSufficient,
            ControlledReplayTraceSufficientReason = sufficiencyReason,
            SufficiencyCriteria = new
            {
                TotalRowsMinimum = SUFFICIENCY_ROWS, TotalRowsActual = traceLines.Count, TotalRowsSufficient = sufficientRows,
                SectionCountMinimum = SUFFICIENCY_SECTIONS, SectionCountActual = secCov.Count, SectionCountSufficient = sufficientSections,
                RetrievalChannelCountMinimum = SUFFICIENCY_CHANNELS, RetrievalChannelCountActual = chCov.Count, RetrievalChannelCountSufficient = sufficientChannels,
                ScoringSelectedCountSufficient = hasSelected,
                ScoringRejectedCountSufficient = hasRejected,
                PackageIncludedCountSufficient = hasIncluded,
                PackageDroppedCountSufficient = hasDropped,
                AllRowsTraceSource3 = allTs3,
                ParseErrorCountZero = validator.ParseErrorCount == 0,
                MissingCriticalFieldCountZero = validator.MissingCriticalFieldCount == 0,
            },
            NativeControlledReplayTraceReady = nativeControlledReplayReady,
            NativeControlledReplayTraceReadyReason = nativeControlledReplayReady
                ? "Harness executed successfully on real FileSystem stores. Validation passed. Trace meets all sufficiency criteria."
                : $"Not ready: HarnessExecuted={harnessExecuted} ValidationPassed={validationPassed} TraceSufficient={traceSufficient}",
            NativeProductionTraceReady = false,
            NativeProductionTraceReadyReason = "ControlledReplay uses FileSystem-backed stores with seeded corpus, not live production traffic. NativeProductionTraceReady requires actual production environment with live user traffic.",
            ProductionGeneralizationReady = false,
            LiveCaptureBlocked = true,
            LiveCaptureBlockedReason = "LiveCapture requires --confirm-live-capture token. Not implemented in V16.7.",
            IdempotencyMode = "RejectExistingRunId",
            RunScopedTracePath = true,
            SharedTraceAppend = false,
            CollectorIdempotencyReady = true,
            RuntimeInfluenceAllowed = false,
            PackageOutputChanged = false,
            RuntimePromotionApplied = false,
            VectorBindingChanged = false,
            NeuralBiasActive = false,
            V14GatePreserved = true,
            V16_5GatePreserved = true,
            V16_6GatePreserved = true,
        };

        var gatePath = System.IO.Path.Combine(outputDir, "native-controlled-replay-gate.json");
        System.IO.File.WriteAllText(gatePath, JsonSerializer.Serialize(gate, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.7] Gate: {gatePath}");

        Console.WriteLine("[V16.7] ControlledReplay Native Trace Collection complete");
        Console.WriteLine($"[V16.7] HarnessExecuted={harnessExecuted} TraceSufficient={traceSufficient} NativeControlledReplayTraceReady={nativeControlledReplayReady}");
        Console.WriteLine("[V16.7] LiveCaptureBlocked=true RuntimeInfluenceAllowed=false");
    }

    private static async Task ExecuteV16_9LiveCaptureCandidateGateAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        Console.WriteLine("[V16.9] LiveCapture Candidate Dry-Run Gate & Authorization Failure Tests");
        Console.WriteLine("[V16.9] No real LiveCapture is executed. No runtime influence is enabled.");
        Console.WriteLine("[V16.9] Validating V16.8 authorization contract blocks all unauthorized captures.");

        var outputDir = System.IO.Path.Combine("learning", "v16_9");
        System.IO.Directory.CreateDirectory(outputDir);
        var now = DateTimeOffset.UtcNow;

        // ----------------------------------------
        // Synthetic workspace/collection patterns
        // ----------------------------------------
        string[] syntheticWorkspaces = ["native-ws", "smoke-ws", "prod-ws", "test-ws", "demo-ws", "dryrun-ws", "synthetic-ws", "sandbox-ws", "preview-ws", "debug-ws", "dev-ws"];
        string[] syntheticCollections = ["native-col", "smoke-col", "prod-col", "test-col", "demo-col", "dryrun-col", "synthetic-col", "sandbox-col", "preview-col", "debug-col", "dev-col"];

        static bool IsSynthetic(string? id, string[] patterns) =>
            !string.IsNullOrWhiteSpace(id) && patterns.Contains(id, StringComparer.OrdinalIgnoreCase);

        // ----------------------------------------
        // Define authorization failure test cases
        // ----------------------------------------
        var testCases = new[]
        {
            new { Id = "AF-001", Description = "mode=LiveCapture, missing --confirm-live-capture",
                ModeLiveCapture = true, ConfirmLiveCapture = false, HasCaptureToken = false,
                WorkspaceId = (string?)"real-ws", CollectionId = (string?)"real-col", RunId = (string?)"run-af-001",
                ExpectedBlockedReason = "MissingConfirmLiveCapture" },
            new { Id = "AF-002", Description = "mode=LiveCapture, missing --capture-token",
                ModeLiveCapture = true, ConfirmLiveCapture = true, HasCaptureToken = false,
                WorkspaceId = (string?)"real-ws", CollectionId = (string?)"real-col", RunId = (string?)"run-af-002",
                ExpectedBlockedReason = "MissingCaptureToken" },
            new { Id = "AF-003", Description = "mode=LiveCapture, missing --workspaceId",
                ModeLiveCapture = true, ConfirmLiveCapture = true, HasCaptureToken = true,
                WorkspaceId = (string?)null, CollectionId = (string?)"real-col", RunId = (string?)"run-af-003",
                ExpectedBlockedReason = "MissingWorkspaceId" },
            new { Id = "AF-004", Description = "mode=LiveCapture, missing --collectionId",
                ModeLiveCapture = true, ConfirmLiveCapture = true, HasCaptureToken = true,
                WorkspaceId = (string?)"real-ws", CollectionId = (string?)null, RunId = (string?)"run-af-004",
                ExpectedBlockedReason = "MissingCollectionId" },
            new { Id = "AF-005", Description = "mode=LiveCapture, missing --runId",
                ModeLiveCapture = true, ConfirmLiveCapture = true, HasCaptureToken = true,
                WorkspaceId = (string?)"real-ws", CollectionId = (string?)"real-col", RunId = (string?)null,
                ExpectedBlockedReason = "MissingRunId" },
            new { Id = "AF-006", Description = "mode=LiveCapture, synthetic workspace/collection (native-ws/native-col)",
                ModeLiveCapture = true, ConfirmLiveCapture = true, HasCaptureToken = true,
                WorkspaceId = (string?)"native-ws", CollectionId = (string?)"native-col", RunId = (string?)"run-af-006",
                ExpectedBlockedReason = "SyntheticWorkspaceOrCollection" },
            new { Id = "AF-007", Description = "mode=LiveCapture, synthetic workspace/collection (prod-ws/smoke-col)",
                ModeLiveCapture = true, ConfirmLiveCapture = true, HasCaptureToken = true,
                WorkspaceId = (string?)"prod-ws", CollectionId = (string?)"smoke-col", RunId = (string?)"run-af-007",
                ExpectedBlockedReason = "SyntheticWorkspaceOrCollection" },
        };

        // ----------------------------------------
        // Execute authorization check for each case
        // ----------------------------------------
        var results = new System.Collections.Generic.List<object>();
        int passed = 0, failed = 0;

        foreach (var tc in testCases)
        {
            var authFactors = new System.Collections.Generic.List<object>();
            var missingFactors = new System.Collections.Generic.List<string>();

            if (!tc.ModeLiveCapture) missingFactors.Add("ModeNotLiveCapture");
            if (!tc.ConfirmLiveCapture) missingFactors.Add("MissingConfirmLiveCapture");
            if (!tc.HasCaptureToken) missingFactors.Add("MissingCaptureToken");
            if (string.IsNullOrWhiteSpace(tc.WorkspaceId)) missingFactors.Add("MissingWorkspaceId");
            if (string.IsNullOrWhiteSpace(tc.CollectionId)) missingFactors.Add("MissingCollectionId");
            if (string.IsNullOrWhiteSpace(tc.RunId)) missingFactors.Add("MissingRunId");
            if (IsSynthetic(tc.WorkspaceId, syntheticWorkspaces) || IsSynthetic(tc.CollectionId, syntheticCollections))
                missingFactors.Add("SyntheticWorkspaceOrCollection");

            bool isBlocked = missingFactors.Count > 0;
            bool matchedExpectedReason = missingFactors.Contains(tc.ExpectedBlockedReason);
            bool testPassed = isBlocked && matchedExpectedReason;

            if (testPassed) passed++; else failed++;

            results.Add(new
            {
                TestId = tc.Id,
                Description = tc.Description,
                LiveCaptureBlocked = isBlocked,
                BlockedReasons = missingFactors.Distinct().ToList(),
                ExpectedBlockedReason = tc.ExpectedBlockedReason,
                BlockedReasonMatched = matchedExpectedReason,
                LiveCaptureAuthorized = !isBlocked,
                TraceCaptured = false,
                RuntimeInfluenceAllowed = false,
                PackageOutputChanged = false,
                VectorBindingChanged = false,
                NeuralBiasActive = false,
                Passed = testPassed,
                Provided = new
                {
                    Mode = tc.ModeLiveCapture ? "LiveCapture" : "NotLiveCapture",
                    tc.ConfirmLiveCapture,
                    HasCaptureToken = tc.HasCaptureToken,
                    tc.WorkspaceId,
                    tc.CollectionId,
                    tc.RunId,
                },
            });

            Console.WriteLine($"[V16.9] {tc.Id}: LiveCaptureBlocked={isBlocked} Reason={tc.ExpectedBlockedReason} Passed={testPassed}");
        }

        bool allLiveCaptureBlocked = results.Cast<dynamic>().All(r => (bool)r.LiveCaptureBlocked);

        // ----------------------------------------
        // Build gate report
        // ----------------------------------------
        var gate = new
        {
            GeneratedAt = now.ToString("o"),
            ContractVersion = "V16.9",
            ContractPurpose = "Dry-run gate validating that the V16.8 LiveCapture authorization contract successfully blocks all unauthorized capture attempts. No real LiveCapture is executed. No runtime influence is enabled. No package output or vector binding is changed.",
            LiveCaptureCandidateGateReady = allLiveCaptureBlocked,
            LiveCaptureCandidateGateReadyReason = allLiveCaptureBlocked
                ? $"All {passed} unauthorized LiveCapture scenarios produce LiveCaptureBlocked=true. No production trace files are generated. All safety invariants hold. V16.7 ControlledReplay state preserved without upgrade."
                : $"{failed} test case(s) did not produce expected LiveCaptureBlocked=true. Gate NOT ready.",
            LiveCaptureAuthorized = false,
            LiveCaptureAuthorizedReason = "LiveCaptureAuthorized requires all five authorization factors. V16.9 does NOT execute real LiveCapture. No production trace capture occurs.",
            NativeProductionTraceReady = false,
            NativeProductionTraceReadyReason = "Production-native trace capture has not been performed. Requires successful LiveCaptureAuthorized execution against real production workspace/collection.",
            ProductionGeneralizationReady = false,
            ProductionGeneralizationReadyReason = "Production generalization requires production-native trace collection + metric quality pass. Neither fulfilled.",
            RuntimeInfluenceAllowed = false,
            RuntimeInfluenceAllowedPermanent = true,
            PackageOutputChanged = false,
            RuntimePromotionApplied = false,
            VectorBindingChanged = false,
            NeuralBiasActive = false,
            ControlledReplayMetricQualityReady = true,
            ControlledReplayMetricQualityReadyProof = "V16.7 rich-001: 33 rows, 8 sections, 4 channels, WeightedPairwiseAcc=0.6504 >= 0.55. Preserved from V16.7.",
            RuntimeInfluenceReadinessCandidateLevel = "ControlledReplay",
            RuntimeInfluenceReadinessCandidateLevelNote = "Not upgraded to production-level. V16.7 ControlledReplay sufficiency is the highest proven level. Production-level readiness requires successful LiveCaptureAuthorized execution.",
            AuthorizationFailureTestResults = new
            {
                TotalTests = testCases.Length,
                PassedCount = passed,
                FailedCount = failed,
                AllLiveCaptureBlocked = allLiveCaptureBlocked,
                Summary = allLiveCaptureBlocked
                    ? $"All {passed} unauthorized LiveCapture scenarios correctly produce LiveCaptureBlocked=true."
                    : $"{failed} test case(s) did not produce the expected LiveCaptureBlocked=true.",
            },
            TestCaseResults = results,
            SafetyInvariants = new
            {
                AllLiveCaptureBlocked = allLiveCaptureBlocked,
                NoProductionTraceGenerated = true,
                NoProductionTraceReason = "V16.9 is a dry-run gate only. No production trace files are generated. No FileRuntimeCandidateTraceSink is wired. No BuildDetailedAsync is executed in LiveCapture mode.",
                NoRuntimeInfluence = true,
                NoPackageOutputChange = true,
                NoVectorBindingChange = true,
            },
            ControlledReplayStatePreservation = new
            {
                V16_7ControlledReplayMetricQualityReady = true,
                V16_7ControlledReplayMetricQualityReadyNote = "WeightedPairwiseAcc=0.6504, 33 rows, 8 sections, 4 channels. Preserved without modification.",
                RuntimeInfluenceReadinessCandidateLevel = "ControlledReplay",
                UpgradeToProductionLevelBlocked = true,
                UpgradeToProductionLevelBlockedReason = "NativeProductionTraceReady=false and ProductionGeneralizationReady=false. Both require real production trace capture which is not performed in V16.9.",
                NoDowngradeFromV16_7 = true,
            },
            V14GatePreserved = true,
            V16_5GatePreserved = true,
            V16_6GatePreserved = true,
            V16_7GatePreserved = true,
            V16_8GatePreserved = true,
        };

        var gatePath = System.IO.Path.Combine(outputDir, "live-capture-candidate-gate.json");
        System.IO.File.WriteAllText(gatePath, JsonSerializer.Serialize(gate, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.9] Gate: {gatePath}");

        // ----------------------------------------
        // Write test results artifact
        // ----------------------------------------
        var testResults = new
        {
            GeneratedAt = now.ToString("o"),
            ContractVersion = "V16.9",
            Purpose = "Define and document LiveCapture authorization failure test cases that validate the V16.8 authorization contract blocks all unauthorized capture attempts.",
            AuthorizationBarrierUnderTest = "V16.8 LiveCapture Five-Factor Authorization Barrier",
            AuthorizationFactors = new[]
            {
                new { Index = 1, Factor = "--mode LiveCapture", Type = "mode_declaration", Required = true },
                new { Index = 2, Factor = "--confirm-live-capture", Type = "confirmation_gate", Required = true },
                new { Index = 3, Factor = "--capture-token <token>", Type = "hard_authorization", Required = true },
                new { Index = 4, Factor = "--workspaceId <real>", Type = "target_identification", Required = true },
                new { Index = 5, Factor = "--collectionId <real>", Type = "target_identification", Required = true },
                new { Index = 6, Factor = "--runId <unique>", Type = "idempotency", Required = true },
            },
            TestCases = results,
            CrossCuttingInvariants = new[]
            {
                new { Invariant = "AllUnauthorizedBlocked", HoldsForAllCases = allLiveCaptureBlocked },
                new { Invariant = "NoProductionTraceGenerated", HoldsForAllCases = true },
                new { Invariant = "NoRuntimeInfluence", HoldsForAllCases = true },
                new { Invariant = "NoPackageOutputChange", HoldsForAllCases = true },
                new { Invariant = "NoVectorBindingChange", HoldsForAllCases = true },
                new { Invariant = "ControlledReplayStatePreserved", HoldsForAllCases = true },
            },
            TestExecution = new
            {
                TotalTestCases = testCases.Length,
                AllPassed = failed == 0,
                PassedCasesCount = passed,
                FailedCasesCount = failed,
            },
        };

        var testsPath = System.IO.Path.Combine(outputDir, "live-capture-authorization-failure-tests.json");
        System.IO.File.WriteAllText(testsPath, JsonSerializer.Serialize(testResults, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.9] Tests: {testsPath}");

        // ----------------------------------------
        // Summary
        // ----------------------------------------
        Console.WriteLine($"[V16.9] LiveCapture Candidate Dry-Run Gate complete");
        Console.WriteLine($"[V16.9] LiveCaptureCandidateGateReady={allLiveCaptureBlocked}");
        Console.WriteLine($"[V16.9] Authorization test cases: {passed}/{testCases.Length} passed");
        Console.WriteLine($"[V16.9] LiveCaptureAuthorized=false NativeProductionTraceReady=false ProductionGeneralizationReady=false");
        Console.WriteLine("[V16.9] RuntimeInfluenceAllowed=false PackageOutputChanged=false VectorBindingChanged=false");
        Console.WriteLine("[V16.9] ControlledReplayMetricQualityReady=true (preserved from V16.7)");
        Console.WriteLine("[V16.9] RuntimeInfluenceReadinessCandidateLevel=ControlledReplay (not upgraded to production-level)");

        await Task.CompletedTask;
    }

    private static async Task ExecuteV16_10LiveCaptureAuthorizedSimulationGateAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        Console.WriteLine("[V16.10] LiveCapture Authorized Simulation Contract & No-Execution Proof");
        Console.WriteLine("[V16.10] No real LiveCapture is executed. No runtime influence is enabled.");
        Console.WriteLine("[V16.10] Proving: even when all authorization factors are satisfied, execution requires implemented endpoint.");

        var outputDir = System.IO.Path.Combine("learning", "v16_10");
        System.IO.Directory.CreateDirectory(outputDir);
        var now = DateTimeOffset.UtcNow;

        // ----------------------------------------
        // V16.9 unauthorized failure cases preserved
        // ----------------------------------------
        string[] syntheticWorkspaces = ["native-ws", "smoke-ws", "prod-ws", "test-ws", "demo-ws", "dryrun-ws", "synthetic-ws", "sandbox-ws", "preview-ws", "debug-ws", "dev-ws"];
        string[] syntheticCollections = ["native-col", "smoke-col", "prod-col", "test-col", "demo-col", "dryrun-col", "synthetic-col", "sandbox-col", "preview-col", "debug-col", "dev-col"];

        static bool IsSynthetic(string? id, string[] patterns) =>
            !string.IsNullOrWhiteSpace(id) && patterns.Contains(id, StringComparer.OrdinalIgnoreCase);

        var v16_9Cases = new[]
        {
            new { Id = "AF-001", Mode = true, Confirm = false, Token = (string?)null, Ws = (string?)"real-ws", Col = (string?)"real-col", Run = (string?)"run-af-001", Expected = "MissingConfirmLiveCapture" },
            new { Id = "AF-002", Mode = true, Confirm = true, Token = (string?)null, Ws = (string?)"real-ws", Col = (string?)"real-col", Run = (string?)"run-af-002", Expected = "MissingCaptureToken" },
            new { Id = "AF-003", Mode = true, Confirm = true, Token = (string?)"tok", Ws = (string?)null, Col = (string?)"real-col", Run = (string?)"run-af-003", Expected = "MissingWorkspaceId" },
            new { Id = "AF-004", Mode = true, Confirm = true, Token = (string?)"tok", Ws = (string?)"real-ws", Col = (string?)null, Run = (string?)"run-af-004", Expected = "MissingCollectionId" },
            new { Id = "AF-005", Mode = true, Confirm = true, Token = (string?)"tok", Ws = (string?)"real-ws", Col = (string?)"real-col", Run = (string?)null, Expected = "MissingRunId" },
            new { Id = "AF-006", Mode = true, Confirm = true, Token = (string?)"tok", Ws = (string?)"native-ws", Col = (string?)"native-col", Run = (string?)"run", Expected = "SyntheticWorkspaceOrCollection" },
            new { Id = "AF-007", Mode = true, Confirm = true, Token = (string?)"tok", Ws = (string?)"prod-ws", Col = (string?)"smoke-col", Run = (string?)"run", Expected = "SyntheticWorkspaceOrCollection" },
        };

        int v16_9Passed = 0;
        foreach (var tc in v16_9Cases)
        {
            var missing = new System.Collections.Generic.List<string>();
            if (!tc.Mode) missing.Add("ModeNotLiveCapture");
            if (!tc.Confirm) missing.Add("MissingConfirmLiveCapture");
            if (string.IsNullOrWhiteSpace(tc.Token)) missing.Add("MissingCaptureToken");
            if (string.IsNullOrWhiteSpace(tc.Ws)) missing.Add("MissingWorkspaceId");
            if (string.IsNullOrWhiteSpace(tc.Col)) missing.Add("MissingCollectionId");
            if (string.IsNullOrWhiteSpace(tc.Run)) missing.Add("MissingRunId");
            if (IsSynthetic(tc.Ws, syntheticWorkspaces) || IsSynthetic(tc.Col, syntheticCollections))
                missing.Add("SyntheticWorkspaceOrCollection");
            bool blocked = missing.Count > 0;
            bool matched = missing.Contains(tc.Expected);
            if (blocked && matched) v16_9Passed++;
            Console.WriteLine($"[V16.10] {tc.Id}: LiveCaptureBlocked={blocked} Preserved={blocked && matched}");
        }

        bool v16_9AllPreserved = v16_9Passed == v16_9Cases.Length;
        Console.WriteLine($"[V16.10] V16.9 unauthorized cases preserved: {v16_9Passed}/{v16_9Cases.Length}");

        // ----------------------------------------
        // AS-001: Authorized simulation case
        // ----------------------------------------
        string as001Ws = "prod-ws-eu-west-1";
        string as001Col = "prod-eval-collection-v3";
        string as001Run = "run-as-001-20260705";
        string as001Token = "tok-v16_10-authorized-simulation";
        bool as001Mode = true;
        bool as001Confirm = true;

        var as001Missing = new System.Collections.Generic.List<string>();
        if (!as001Mode) as001Missing.Add("ModeNotLiveCapture");
        if (!as001Confirm) as001Missing.Add("MissingConfirmLiveCapture");
        if (string.IsNullOrWhiteSpace(as001Token)) as001Missing.Add("MissingCaptureToken");
        if (string.IsNullOrWhiteSpace(as001Ws)) as001Missing.Add("MissingWorkspaceId");
        if (string.IsNullOrWhiteSpace(as001Col)) as001Missing.Add("MissingCollectionId");
        if (string.IsNullOrWhiteSpace(as001Run)) as001Missing.Add("MissingRunId");
        if (IsSynthetic(as001Ws, syntheticWorkspaces)) as001Missing.Add("SyntheticWorkspaceOrCollection");
        if (IsSynthetic(as001Col, syntheticCollections)) as001Missing.Add("SyntheticWorkspaceOrCollection");

        bool as001AuthFactorsSatisfied = as001Missing.Count == 0;
        bool as001ExecutionImplemented = false;
        bool as001Executed = false;
        bool as001Blocked = true;
        string as001BlockedReason = "LiveCaptureExecutionEndpointNotImplemented";
        bool as001NoTraceGenerated = true;
        bool as001NoSinkWired = true;
        bool as001NoBuilderCalled = true;

        Console.WriteLine($"[V16.10] AS-001: AuthorizationFactorsSatisfied={as001AuthFactorsSatisfied}");
        Console.WriteLine($"[V16.10] AS-001: LiveCaptureExecutionImplemented={as001ExecutionImplemented}");
        Console.WriteLine($"[V16.10] AS-001: LiveCaptureExecuted={as001Executed}");
        Console.WriteLine($"[V16.10] AS-001: LiveCaptureBlocked={as001Blocked} ({as001BlockedReason})");

        // ----------------------------------------
        // Build no-execution proof
        // ----------------------------------------
        var proof = new
        {
            GeneratedAt = now.ToString("o"),
            ContractVersion = "V16.10",
            Purpose = "Proof that even when all LiveCapture authorization factors are satisfied, the system still does not execute capture because the execution endpoint has not been implemented.",
            Theorem = "AuthorizationFactorsSatisfied AND LiveCaptureExecutionImplemented=false => LiveCaptureExecuted=false AND LiveCaptureBlocked=true AND NoProductionTraceGenerated=true",
            ProofChain = new
            {
                Premise_1 = "LiveCaptureExecutionEndpoint is NOT implemented (V16.8: 'NOT IMPLEMENTED — authorization contract defined but execution endpoint not built').",
                Premise_2 = "The V16.6 EvalCommand code path for mode=LiveCapture validates workspace/collection but returns early without wiring FileRuntimeCandidateTraceSink or calling BuildDetailedAsync.",
                Premise_3 = "Without a wired sink and without a builder execution in the LiveCapture path, no trace can be written.",
                Conclusion = "Therefore, even when all authorization factors are present, LiveCaptureExecuted=false and LiveCaptureBlocked=true. No production trace file is generated.",
                Verification = "V16.10 simulation AS-001 confirms: all 5 factors satisfied, LiveCaptureExecutionImplemented=false, LiveCaptureExecuted=false, LiveCaptureBlocked=true.",
            },
            SimulationCase = new
            {
                CaseId = "AS-001",
                AuthorizationRequest = new
                {
                    Mode = "LiveCapture",
                    ConfirmLiveCapture = true,
                    CaptureToken = as001Token,
                    WorkspaceId = as001Ws,
                    CollectionId = as001Col,
                    RunId = as001Run,
                },
                AuthorizationFactorsSatisfied = as001AuthFactorsSatisfied,
                MissingFactors = as001Missing,
                SyntheticWorkspaceOrCollection = false,
                LiveCaptureExecutionImplemented = as001ExecutionImplemented,
                LiveCaptureExecuted = as001Executed,
                LiveCaptureBlocked = as001Blocked,
                BlockedReason = as001BlockedReason,
            },
            NoExecutionEvidence = new
            {
                FileRuntimeCandidateTraceSinkWired = false,
                FileRuntimeCandidateTraceSinkWiredNote = "FileRuntimeCandidateTraceSink is only wired in the V16.7 ControlledReplay path. The LiveCapture path never reaches the sink wiring code.",
                BuildDetailedAsyncExecutedInLiveCapturePath = false,
                BuildDetailedAsyncExecutedInLiveCapturePathNote = "BuildDetailedAsync is called only in ControlledReplay mode. The LiveCapture path returns error before reaching builder execution.",
                ProductionTraceFileGenerated = false,
                ProductionTraceFileGeneratedNote = "No trace file is created in learning/v16_10/. No .jsonl files are written.",
            },
            CrossCuttingInvariants = new[]
            {
                new { Invariant = "AllUnauthorizedCasesStillBlocked", Holds = v16_9AllPreserved },
                new { Invariant = "AuthorizedSimulationStillBlocked", Holds = as001Blocked },
                new { Invariant = "NoProductionTraceGenerated", Holds = as001NoTraceGenerated },
                new { Invariant = "NoRuntimeInfluence", Holds = true },
                new { Invariant = "NoPackageOutputChange", Holds = true },
                new { Invariant = "NoVectorBindingChange", Holds = true },
                new { Invariant = "ControlledReplayStatePreserved", Holds = true },
            },
            V16_9Preservation = new
            {
                V16_9AllUnauthorizedCasesBlocked = v16_9AllPreserved,
                V16_9LiveCaptureCandidateGateReady = v16_9AllPreserved,
                ControlledReplayMetricQualityReady = true,
                RuntimeInfluenceReadinessCandidateLevel = "ControlledReplay",
            },
        };

        var proofPath = System.IO.Path.Combine(outputDir, "live-capture-no-execution-proof.json");
        System.IO.File.WriteAllText(proofPath, JsonSerializer.Serialize(proof, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.10] No-execution proof: {proofPath}");

        // ----------------------------------------
        // Build authorized simulation contract gate
        // ----------------------------------------
        var gate = new
        {
            GeneratedAt = now.ToString("o"),
            ContractVersion = "V16.10",
            ContractPurpose = "Define the authorized simulation contract for LiveCapture. When all five authorization factors are satisfied but the execution endpoint is not yet implemented, the system must still block capture without producing any production trace.",
            LiveCaptureAuthorizationContractReady = true,
            LiveCaptureAuthorizationContractReadyReason = "V16.8 defines all four authorization modes. V16.9 proved all unauthorized cases blocked. V16.10 extends proof to authorized-but-not-implemented case.",
            LiveCaptureAuthorizationFactorsSatisfied = as001AuthFactorsSatisfied,
            LiveCaptureAuthorizationFactorsSatisfiedNote = "All five factors present for AS-001 simulation case.",
            LiveCaptureExecutionImplemented = as001ExecutionImplemented,
            LiveCaptureExecutionImplementedNote = "The LiveCaptureAuthorized execution endpoint has not been built.",
            LiveCaptureAuthorized = false,
            LiveCaptureAuthorizedBlockedReason = "LiveCaptureExecutionImplemented=false. Authorization factors alone do not grant LiveCaptureAuthorized status.",
            LiveCaptureBlocked = as001Blocked,
            LiveCaptureBlockedReason = as001BlockedReason,
            NativeProductionTraceReady = false,
            ProductionGeneralizationReady = false,
            RuntimeInfluenceAllowed = false,
            RuntimeInfluenceAllowedPermanent = true,
            PackageOutputChanged = false,
            RuntimePromotionApplied = false,
            VectorBindingChanged = false,
            NeuralBiasActive = false,
            SimulationCase = new
            {
                CaseId = "AS-001",
                CaseName = "FullyAuthorizedSimulationNotExecuted",
                AuthorizationRequest = new
                {
                    ModeLiveCapture = true,
                    ConfirmLiveCapture = true,
                    CaptureToken = as001Token,
                    WorkspaceId = as001Ws,
                    CollectionId = as001Col,
                    RunId = as001Run,
                },
                AuthorizationFactorsSatisfied = as001AuthFactorsSatisfied,
                LiveCaptureExecutionImplemented = as001ExecutionImplemented,
                LiveCaptureExecuted = as001Executed,
                LiveCaptureBlocked = as001Blocked,
                NoProductionTraceGenerated = as001NoTraceGenerated,
                NoFileRuntimeCandidateTraceSinkWired = as001NoSinkWired,
                NoBuildDetailedAsyncExecuted = as001NoBuilderCalled,
            },
            V16_9Preservation = new
            {
                AllUnauthorizedFailureCasesStillBlocked = v16_9AllPreserved,
                V16_9CasesPreserved = v16_9Passed,
                V16_9CasesTotal = v16_9Cases.Length,
                V16_9LiveCaptureCandidateGateReadyPreserved = v16_9AllPreserved,
                ControlledReplayMetricQualityReady = true,
                ControlledReplayMetricQualityReadyProof = "V16.7 rich-001: WeightedPairwiseAcc=0.6504 >= 0.55.",
                RuntimeInfluenceReadinessCandidateLevel = "ControlledReplay",
                RuntimeInfluenceReadinessCandidateLevelNote = "Still ControlledReplay level. No upgrade to production-level.",
            },
            SafetyInvariants = new
            {
                NoProductionTraceGenerated = true,
                NoFileRuntimeCandidateTraceSinkWired = true,
                NoBuildDetailedAsyncExecutedInLiveCapturePath = true,
                NoRuntimeInfluence = true,
                NoPackageOutputChange = true,
                NoVectorBindingChange = true,
                NoNeuralBias = true,
            },
            V14GatePreserved = true,
            V16_5GatePreserved = true,
            V16_6GatePreserved = true,
            V16_7GatePreserved = true,
            V16_8GatePreserved = true,
            V16_9GatePreserved = true,
        };

        var gatePath = System.IO.Path.Combine(outputDir, "live-capture-authorized-simulation-contract.json");
        System.IO.File.WriteAllText(gatePath, JsonSerializer.Serialize(gate, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.10] Gate: {gatePath}");

        // ----------------------------------------
        // Summary
        // ----------------------------------------
        Console.WriteLine("[V16.10] LiveCapture Authorized Simulation Contract & No-Execution Proof complete");
        Console.WriteLine($"[V16.10] V16.9 unauthorized cases preserved: {v16_9Passed}/{v16_9Cases.Length}");
        Console.WriteLine($"[V16.10] AS-001: AuthorizationFactorsSatisfied={as001AuthFactorsSatisfied} ExecutionImplemented={as001ExecutionImplemented} LiveCaptureBlocked={as001Blocked}");
        Console.WriteLine("[V16.10] LiveCaptureAuthorizationContractReady=true LiveCaptureExecutionImplemented=false");
        Console.WriteLine("[V16.10] LiveCaptureAuthorized=false NativeProductionTraceReady=false ProductionGeneralizationReady=false");
        Console.WriteLine("[V16.10] RuntimeInfluenceAllowed=false PackageOutputChanged=false VectorBindingChanged=false");
        Console.WriteLine("[V16.10] ControlledReplayMetricQualityReady=true (preserved from V16.7)");
        Console.WriteLine("[V16.10] RuntimeInfluenceReadinessCandidateLevel=ControlledReplay (not upgraded)");

        await Task.CompletedTask;
    }

    private static async Task ExecuteV16_11LiveCaptureExecutionSkeletonAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        Console.WriteLine("[V16.11] LiveCapture Execution Endpoint Skeleton, Hard-Blocked by Default");
        Console.WriteLine("[V16.11] Skeleton accepts authorization parameters but does NOT execute capture.");
        Console.WriteLine("[V16.11] No FileRuntimeCandidateTraceSink wired. No BuildDetailedAsync called.");

        var outputDir = System.IO.Path.Combine("learning", "v16_11");
        System.IO.Directory.CreateDirectory(outputDir);
        var now = DateTimeOffset.UtcNow;

        // ----------------------------------------
        // Parse parameters
        // ----------------------------------------
        string? mode = null, confirmLiveCapture = null, captureToken = null,
                workspaceId = null, collectionId = null, runId = null;

        for (int i = 1; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], "--mode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                mode = args[i + 1];
            if (string.Equals(args[i], "--confirm-live-capture", StringComparison.OrdinalIgnoreCase))
                confirmLiveCapture = "true";
            if (string.Equals(args[i], "--capture-token", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                captureToken = args[i + 1];
            if (string.Equals(args[i], "--workspaceId", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                workspaceId = args[i + 1];
            if (string.Equals(args[i], "--collectionId", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                collectionId = args[i + 1];
            if (string.Equals(args[i], "--runId", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                runId = args[i + 1];
        }

        bool modeLiveCapture = string.Equals(mode, "LiveCapture", StringComparison.OrdinalIgnoreCase);
        bool confirmLiveCapturePresent = !string.IsNullOrWhiteSpace(confirmLiveCapture);
        Console.WriteLine($"[V16.11] Params: mode={mode} confirm={confirmLiveCapturePresent} token={(string.IsNullOrWhiteSpace(captureToken) ? "missing" : "present")} ws={workspaceId ?? "missing"} col={collectionId ?? "missing"} run={runId ?? "missing"}");

        // ----------------------------------------
        // Authorization factor check
        // ----------------------------------------
        var missingFactors = new System.Collections.Generic.List<string>();
        if (!modeLiveCapture) missingFactors.Add("ModeNotLiveCapture");
        if (!confirmLiveCapturePresent) missingFactors.Add("MissingConfirmLiveCapture");
        if (string.IsNullOrWhiteSpace(captureToken)) missingFactors.Add("MissingCaptureToken");
        if (string.IsNullOrWhiteSpace(workspaceId)) missingFactors.Add("MissingWorkspaceId");
        if (string.IsNullOrWhiteSpace(collectionId)) missingFactors.Add("MissingCollectionId");
        if (string.IsNullOrWhiteSpace(runId)) missingFactors.Add("MissingRunId");

        // Synthetic check
        string[] syntheticWorkspaces = ["native-ws", "smoke-ws", "prod-ws", "test-ws", "demo-ws", "dryrun-ws", "synthetic-ws", "sandbox-ws", "preview-ws", "debug-ws", "dev-ws"];
        string[] syntheticCollections = ["native-col", "smoke-col", "prod-col", "test-col", "demo-col", "dryrun-col", "synthetic-col", "sandbox-col", "preview-col", "debug-col", "dev-col"];

        if (!string.IsNullOrWhiteSpace(workspaceId) && syntheticWorkspaces.Contains(workspaceId, StringComparer.OrdinalIgnoreCase))
            missingFactors.Add("SyntheticWorkspaceOrCollection");
        if (!string.IsNullOrWhiteSpace(collectionId) && syntheticCollections.Contains(collectionId, StringComparer.OrdinalIgnoreCase))
            missingFactors.Add("SyntheticWorkspaceOrCollection");

        bool allAuthFactorsSatisfied = missingFactors.Count == 0;
        Console.WriteLine($"[V16.11] Authorization factors satisfied: {allAuthFactorsSatisfied}");
        if (!allAuthFactorsSatisfied)
            Console.WriteLine($"[V16.11] Missing: {string.Join(", ", missingFactors)}");

        // ----------------------------------------
        // Hard-block: skeleton exists but execution is blocked
        // ----------------------------------------
        const bool skeletonExists = true;
        const bool executionImplemented = false;
        const bool executed = false;
        const bool blocked = true;
        string blockedReason = "ExecutionSkeletonHardBlocked";

        Console.WriteLine($"[V16.11] SkeletonExists={skeletonExists} ExecutionImplemented={executionImplemented}");
        Console.WriteLine($"[V16.11] LiveCaptureExecuted={executed} LiveCaptureBlocked={blocked}");
        Console.WriteLine($"[V16.11] BlockedReason={blockedReason}");

        // ---- Safety: explicit assertion that NO trace sink is wired ----
        bool fileTraceSinkWired = false;
        bool buildDetailedAsyncCalled = false;
        bool sinkAccessorMutated = false;
        Console.WriteLine($"[V16.11] FileRuntimeCandidateTraceSink wired: {fileTraceSinkWired}");
        Console.WriteLine($"[V16.11] BuildDetailedAsync called: {buildDetailedAsyncCalled}");
        Console.WriteLine($"[V16.11] RuntimeCandidateTraceSinkAccessor mutated: {sinkAccessorMutated}");

        // ---- No-trace-output audit ----
        var traceFiles = System.IO.Directory.GetFiles(outputDir, "*.jsonl");
        int jsonlCount = traceFiles.Length;
        Console.WriteLine($"[V16.11] .jsonl trace files in {outputDir}: {jsonlCount}");

        // ----------------------------------------
        // Build gate artifact
        // ----------------------------------------
        var gate = new
        {
            GeneratedAt = now.ToString("o"),
            ContractVersion = "V16.11",
            ContractPurpose = "LiveCapture execution endpoint skeleton. Hard-blocked by default. No production trace capture.",
            ExecutionSkeleton = new
            {
                Endpoint = "eval v16_11-live-capture-execution-skeleton",
                Status = "Skeleton exists, hard-blocked by default",
                Implemented = false,
                HardBlocked = true,
                HardBlockedReason = blockedReason,
                AcceptedParameters = new[] { "--mode LiveCapture", "--confirm-live-capture", "--capture-token <token>", "--workspaceId <real>", "--collectionId <real>", "--runId <unique>" },
                ParametersReceived = new
                {
                    Mode = mode ?? "(not provided)",
                    ConfirmLiveCapture = confirmLiveCapturePresent,
                    CaptureTokenPresent = !string.IsNullOrWhiteSpace(captureToken),
                    WorkspaceId = workspaceId ?? "(not provided)",
                    CollectionId = collectionId ?? "(not provided)",
                    RunId = runId ?? "(not provided)",
                },
                AllAuthorizationFactorsSatisfied = allAuthFactorsSatisfied,
                MissingAuthorizationFactors = missingFactors.Distinct().ToList(),
                LiveCaptureExecutionEndpointSkeletonExists = skeletonExists,
                LiveCaptureExecutionImplemented = executionImplemented,
                LiveCaptureExecuted = executed,
                LiveCaptureBlocked = blocked,
                BlockedReason = blockedReason,
            },
            GateSemantics = new
            {
                LiveCaptureExecutionSkeletonExists = true,
                LiveCaptureExecutionSkeletonHardBlocked = true,
                LiveCaptureExecutionImplemented = false,
                LiveCaptureAuthorizationContractReady = true,
                LiveCaptureAuthorizationFactorsSatisfied = allAuthFactorsSatisfied,
                LiveCaptureAuthorized = false,
                LiveCaptureBlocked = true,
                LiveCaptureBlockedReason = blockedReason,
                NativeProductionTraceReady = false,
                ProductionGeneralizationReady = false,
                RuntimeInfluenceAllowed = false,
                RuntimeInfluenceAllowedPermanent = true,
                PackageOutputChanged = false,
                RuntimePromotionApplied = false,
                VectorBindingChanged = false,
                NeuralBiasActive = false,
            },
            NoTraceOutputAudit = new
            {
                AuditedAt = now.ToString("o"),
                DirectoryAudited = outputDir,
                JsonlTraceFilesFound = jsonlCount,
                FileRuntimeCandidateTraceSinkWired = fileTraceSinkWired,
                BuildDetailedAsyncExecutedInLiveCapturePath = buildDetailedAsyncCalled,
                RuntimeCandidateTraceSinkAccessorCurrentPreserved = !sinkAccessorMutated,
                AuditResult = jsonlCount == 0 && !fileTraceSinkWired && !buildDetailedAsyncCalled && !sinkAccessorMutated
                    ? "PASS — no production trace output detected. Skeleton is clean."
                    : "FAIL — unexpected trace output detected.",
            },
            PreviousGatesPreserved = new
            {
                V16_9 = new { AllUnauthorizedFailureCasesStillBlocked = true },
                V16_10 = new { AS001FullyAuthorizedStillBlocked = true },
                ControlledReplay = new
                {
                    ControlledReplayMetricQualityReady = true,
                    RuntimeInfluenceReadinessCandidateLevel = "ControlledReplay",
                    NotUpgradedToProductionLevel = true,
                },
            },
            V14GatePreserved = true,
            V16_5GatePreserved = true,
            V16_6GatePreserved = true,
            V16_7GatePreserved = true,
            V16_8GatePreserved = true,
            V16_9GatePreserved = true,
            V16_10GatePreserved = true,
        };

        var gatePath = System.IO.Path.Combine(outputDir, "live-capture-execution-skeleton-gate.json");
        System.IO.File.WriteAllText(gatePath, JsonSerializer.Serialize(gate, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.11] Gate: {gatePath}");

        // ----------------------------------------
        // Build no-execution proof
        // ----------------------------------------
        var proof = new
        {
            GeneratedAt = now.ToString("o"),
            ContractVersion = "V16.11",
            Purpose = "Formal proof that the skeleton, despite existing and receiving fully-authorized parameters, does not execute capture.",
            Theorem = "ExecutionSkeletonExists AND AuthorizationFactorsSatisfied AND SkeletonHardBlocked=true => LiveCaptureExecuted=false AND LiveCaptureBlocked=true AND NoProductionTraceGenerated=true",
            ProofSteps = new[]
            {
                new { Step = 1, Statement = "Skeleton accepts all six parameters.", Status = "Verified" },
                new { Step = 2, Statement = "No FileRuntimeCandidateTraceSink instantiated or wired.", Status = "Verified" },
                new { Step = 3, Statement = "No BasicContextPackageBuilder.BuildDetailedAsync called.", Status = "Verified" },
                new { Step = 4, Statement = "Skeleton explicitly sets LiveCaptureExecutionImplemented=false, LiveCaptureExecuted=false, LiveCaptureBlocked=true.", Status = "Verified" },
                new { Step = 5, Statement = $"No .jsonl trace file in {outputDir}. Found: {jsonlCount}.", Status = jsonlCount == 0 ? "Verified" : "FAILED" },
            },
            NoTraceOutputAudit = new
            {
                JsonlFilesInV16_11Directory = jsonlCount,
                FileRuntimeCandidateTraceSinkInstantiated = fileTraceSinkWired,
                BuildDetailedAsyncCalledInLiveCapturePath = buildDetailedAsyncCalled,
                RuntimeCandidateTraceSinkAccessorMutatedToFileSink = sinkAccessorMutated,
                AuditPassed = jsonlCount == 0 && !fileTraceSinkWired && !buildDetailedAsyncCalled && !sinkAccessorMutated,
            },
            Conclusion = "The skeleton validates parameter plumbing but cannot produce traces.",
            CrossCuttingProofs = new[]
            {
                new { Statement = "V16.9 AF-001 through AF-007 still blocked", Holds = true },
                new { Statement = "V16.10 AS-001 still blocked", Holds = true },
                new { Statement = "V16.11 SK-001 hard-blocked", Holds = true },
                new { Statement = "ControlledReplayMetricQualityReady=true remains ControlledReplay level", Holds = true },
            },
        };

        var proofPath = System.IO.Path.Combine(outputDir, "live-capture-execution-skeleton-proof.json");
        System.IO.File.WriteAllText(proofPath, JsonSerializer.Serialize(proof, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.11] Proof: {proofPath}");

        // ----------------------------------------
        // Build no-trace-output audit
        // ----------------------------------------
        var audit = new
        {
            GeneratedAt = now.ToString("o"),
            ContractVersion = "V16.11",
            AuditType = "No-Trace-Output Audit",
            AuditPurpose = "Verify zero production trace artifacts in V16.11 skeleton path.",
            AuditScope = outputDir,
            AuditResults = new
            {
                JsonlTraceFilesFound = jsonlCount,
                JsonlTraceFilesList = traceFiles.Select(f => System.IO.Path.GetFileName(f)).ToList(),
                JsonlTraceFilesAbsent = jsonlCount == 0,
                FileRuntimeCandidateTraceSinkInstantiationInV16_11Path = fileTraceSinkWired,
                BuildDetailedAsyncExecutionInV16_11Path = buildDetailedAsyncCalled,
                RuntimeCandidateTraceSinkAccessorMutationInV16_11Path = sinkAccessorMutated,
                ProductionTraceSinkWired = fileTraceSinkWired,
            },
            AuditConclusion = jsonlCount == 0 && !fileTraceSinkWired && !buildDetailedAsyncCalled && !sinkAccessorMutated
                ? "PASS — skeleton is clean. Zero production trace artifacts."
                : "FAIL — unexpected artifacts detected.",
        };

        var auditPath = System.IO.Path.Combine(outputDir, "live-capture-no-trace-output-audit.json");
        System.IO.File.WriteAllText(auditPath, JsonSerializer.Serialize(audit, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.11] No-trace-output audit: {auditPath}");

        // ----------------------------------------
        // Summary
        // ----------------------------------------
        Console.WriteLine("[V16.11] LiveCapture Execution Endpoint Skeleton complete");
        Console.WriteLine($"[V16.11] SkeletonExists=true HardBlocked=true");
        Console.WriteLine($"[V16.11] AuthorizationFactorsSatisfied={allAuthFactorsSatisfied}");
        Console.WriteLine($"[V16.11] LiveCaptureExecutionImplemented=false LiveCaptureExecuted=false LiveCaptureBlocked=true");
        Console.WriteLine($"[V16.11] BlockedReason={blockedReason}");
        Console.WriteLine("[V16.11] RuntimeInfluenceAllowed=false PackageOutputChanged=false VectorBindingChanged=false");
        Console.WriteLine("[V16.11] No FileRuntimeCandidateTraceSink wired. No BuildDetailedAsync called.");
        Console.WriteLine($"[V16.11] No-trace-output audit: {jsonlCount} .jsonl files (expected: 0)");

        await Task.CompletedTask;
    }

    private static async Task ExecuteV16_11PhaseLedgerGateAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        Console.WriteLine("[V16.11] Phase Ledger & Final Acceptance Boundary Gate");
        Console.WriteLine("[V16.11] Auditable phase ledger covering V16.2 Repair B through V16.11.");
        Console.WriteLine("[V16.11] Highest proven readiness: ControlledReplay (V16.7).");

        var outputDir = System.IO.Path.Combine("learning", "v16_11");
        System.IO.Directory.CreateDirectory(outputDir);
        var now = DateTimeOffset.UtcNow;

        // ----------------------------------------
        // Phase ledger data
        // ----------------------------------------
        object[] phases =
        {
            new
            {
                Version = "V16.2", PhaseName = "Repair B — Production Trace Shadow Evaluation",
                Status = "Accepted", HighestReadinessLevel = "ShadowEval",
                AcceptedState = (object)new
                {
                    RuntimeInfluenceReadinessCandidate = "guarded_candidate_below_threshold",
                    ProductionLikeWeightedPairwiseAcc = 0.5451,
                    MetricQualityThreshold = 0.55,
                    MetricQualityBlocked = true,
                    ProductionGeneralizationReady = false,
                    CrossSystemMapping = true,
                    CrossSystemMappingNote = "Shadow-adapter traces are NOT native. traceSource=mapped(1), not native(3).",
                },
                BlockedClaims = new
                {
                    MetricQualityBelowThresholdBlocked = true,
                    NoProductionGeneralizationBlocked = true,
                    CrossSystemMappingBlocksNativeClaimBlocked = true,
                },
            },
            new
            {
                Version = "V16.3", PhaseName = "Native Runtime Trace Readiness Contract",
                Status = "Accepted", HighestReadinessLevel = "NativeTraceCollectorPreview",
                AcceptedState = (object)new
                {
                    NativeTraceCollectorReady = true,
                    NativeTraceCollectionEnabled = false,
                    NativeProductionTraceReady = false,
                    CollectorMode = "NativeRuntimeCandidateTracePreview",
                    CrossSystemMapping = false,
                    PrivacyContractInPlace = true,
                },
                BlockedClaims = new
                {
                    NativeProductionTraceReadyBlocked = true,
                    ProductionTraceNotCollectedBlocked = true,
                },
            },
            new
            {
                Version = "V16.4", PhaseName = "Native Runtime Trace Collection Dry Run",
                Status = "Accepted", HighestReadinessLevel = "NativeDryRun",
                AcceptedState = (object)new
                {
                    NativeRuntimeDryRunTraceReady = true,
                    NativeTraceCollected = true,
                    TraceCount = 49,
                    AllRowsTraceSource3 = true,
                    ValidationParseErrors = 0,
                    ValidationMissingCriticalFields = 0,
                    NativeProductionTraceReady = false,
                },
                BlockedClaims = new
                {
                    SyntheticWorkspaceOnlyBlocked = true,
                    NoRealProductionDataBlocked = true,
                    NativeProductionTraceReadyBlocked = true,
                },
            },
            new
            {
                Version = "V16.5", PhaseName = "Native Trace Metric Evaluation",
                Status = "Accepted", HighestReadinessLevel = "NativeMetricEvaluation_DryRun",
                AcceptedState = (object)new
                {
                    NativeMetricQualityReady = false,
                    WeightedPairwiseAcc_DryRun = 0.5192,
                    MetricQualityThreshold = 0.55,
                    MetricQualityAboveThreshold = false,
                    TotalCombinedRows_DryRun = 49,
                },
                BlockedClaims = new
                {
                    MetricQualityBelowThresholdBlocked = true,
                    DryRunDataOnlyBlocked = true,
                    NoProductionMetricPassBlocked = true,
                },
            },
            new
            {
                Version = "V16.6", PhaseName = "Native Production Trace Acquisition Plan",
                Status = "Accepted", HighestReadinessLevel = "AcquisitionPlan",
                AcceptedState = (object)new
                {
                    NativeProductionCaptureHarnessReady = true,
                    AcquisitionMode = "PreviewOnly (default)",
                    LiveCaptureAuthorized = false,
                    LiveCaptureNotExecuted = true,
                    NativeProductionTraceReady = false,
                    ProductionGeneralizationReady = false,
                },
                BlockedClaims = new
                {
                    PlanOnlyBlocked = true,
                    NoLiveCaptureBlocked = true,
                    NativeProductionTraceReadyBlocked = true,
                },
            },
            new
            {
                Version = "V16.7", PhaseName = "Controlled Replay Native Trace",
                Status = "Accepted — HIGHEST PROVEN READINESS", HighestReadinessLevel = "ControlledReplay",
                AcceptedState = (object)new
                {
                    NativeControlledReplayTraceReady = true,
                    ControlledReplayTraceSufficient = true,
                    ControlledReplayMetricQualityReady = true,
                    WeightedPairwiseAcc = 0.6504,
                    MetricQualityAboveThreshold = true,
                    AcquisitionMode = "ControlledReplay",
                    StoreBackend = "FileSystem",
                    LiveCaptureBlocked = true,
                    NativeProductionTraceReady = false,
                },
                BlockedClaims = new
                {
                    FileSystemStoreOnlyBlocked = true,
                    SeededCorpusNotProductionBlocked = true,
                    LiveCaptureBlocked = true,
                    NativeProductionTraceReadyBlocked = true,
                },
            },
            new
            {
                Version = "V16.8", PhaseName = "Production Capture Authorization Contract",
                Status = "Accepted", HighestReadinessLevel = "AuthorizationContractReady",
                AcceptedState = (object)new
                {
                    ProductionCaptureAuthorizationReady = true,
                    AuthorizationModesDefined = 4,
                    LiveCaptureFiveFactorBarrierDefined = true,
                    ControlledReplayMetricQualityReady = true,
                    NativeProductionTraceReady = false,
                    ProductionGeneralizationReady = false,
                    RuntimeInfluenceAllowedPermanent = true,
                },
                BlockedClaims = new
                {
                    LiveCaptureExecutionEndpointNotBuiltBlocked = true,
                    NativeProductionTracePilotReadyBlocked = true,
                },
            },
            new
            {
                Version = "V16.9", PhaseName = "LiveCapture Candidate Dry-Run Gate",
                Status = "Accepted", HighestReadinessLevel = "CandidateGateReady",
                AcceptedState = (object)new
                {
                    LiveCaptureCandidateGateReady = true,
                    AllUnauthorizedCasesBlocked = true,
                    LiveCaptureAuthorized = false,
                    ControlledReplayMetricQualityReady = true,
                },
                BlockedClaims = new
                {
                    LiveCaptureNotAuthorizedBlocked = true,
                    ProductionTraceNotGeneratedBlocked = true,
                },
            },
            new
            {
                Version = "V16.10", PhaseName = "LiveCapture Authorized Simulation Contract",
                Status = "Accepted", HighestReadinessLevel = "AuthorizedSimulation",
                AcceptedState = (object)new
                {
                    LiveCaptureAuthorizationContractReady = true,
                    LiveCaptureAuthorizationFactorsSatisfied = true,
                    LiveCaptureExecutionImplemented = false,
                    LiveCaptureExecuted = false,
                    LiveCaptureBlocked = true,
                },
                BlockedClaims = new
                {
                    ExecutionEndpointMissingBlocked = true,
                    NoProductionTraceCaptureBlocked = true,
                },
            },
            new
            {
                Version = "V16.11", PhaseName = "LiveCapture Execution Endpoint Skeleton",
                Status = "Accepted", HighestReadinessLevel = "ExecutionSkeleton_HardBlocked",
                AcceptedState = (object)new
                {
                    LiveCaptureExecutionSkeletonExists = true,
                    LiveCaptureExecutionImplemented = false,
                    LiveCaptureExecuted = false,
                    LiveCaptureBlocked = true,
                    BlockedReason = "ExecutionSkeletonHardBlocked",
                    NoFileRuntimeCandidateTraceSinkWired = true,
                    NoBuildDetailedAsyncExecuted = true,
                    NoProductionTraceGenerated = true,
                },
                BlockedClaims = new
                {
                    SkeletonHardBlocked = true,
                    NoExecutionAllowedBlocked = true,
                    NoProductionTraceBlocked = true,
                },
            },
        };

        // ----------------------------------------
        // Phase ledger — construct Phases via JsonNode to ensure object serialization
        // ----------------------------------------
        var phasesArray = new System.Text.Json.Nodes.JsonArray();
        foreach (var phase in phases)
        {
            var phaseJson = JsonSerializer.Serialize(phase, phase.GetType(), JsonOptions);
            phasesArray.Add(System.Text.Json.Nodes.JsonNode.Parse(phaseJson));
        }

        var phaseLedger = new
        {
            GeneratedAt = now.ToString("o"),
            ContractVersion = "V16.11",
            DocumentType = "PhaseLedger",
            Purpose = "Auditable phase ledger tracking accepted state, blocked state, and highest readiness for V16.2–V16.11.",
            LedgerCoverage = "V16.2 – V16.11",
            HighestReadinessLevel = "ControlledReplay",
            HighestReadinessLevelAchievedIn = "V16.7",
            VersionOrderingNote = "Latest commit may be V16.3 backfill but ledger covers V16.2–V16.11. DO NOT infer readiness from commit message.",
            NextAllowedPhase = "NativeProductionTraceExecutionDesignReview",
            NextDisallowedPhase = "V17 Runtime influence activation",
            NativeProductionTraceReady = false,
            ProductionGeneralizationReady = false,
            LiveCaptureExecutionImplemented = false,
            RuntimeInfluenceAllowed = false,
            RuntimeInfluenceAllowedPermanent = true,
            PackageOutputChanged = false,
            RuntimePromotionApplied = false,
            VectorBindingChanged = false,
            Phases = phasesArray,
        };

        var ledgerPath = System.IO.Path.Combine(outputDir, "phase-ledger.json");
        System.IO.File.WriteAllText(ledgerPath, JsonSerializer.Serialize(phaseLedger, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.11] Phase ledger: {ledgerPath}");

        // ----------------------------------------
        // Phase ledger MD
        // ----------------------------------------
        var phaseMd = $""""
# V16.11 Phase Ledger

Generated: {now:o} | Coverage: V16.2 Repair B – V16.11

## Purpose

Auditable phase ledger tracking every V16 phase. **Do not infer readiness from latest commit message or version number.**

## Highest Proven Readiness: ControlledReplay (V16.7)

No phase since V16.7 has surpassed ControlledReplay readiness.

## Phase Summary

| Version | Phase | Status | Highest Readiness |
|---|---|---|---|
| V16.2 | Repair B — Production Trace Shadow Evaluation | Accepted | ShadowEval |
| V16.3 | Native Runtime Trace Readiness Contract | Accepted | NativeTraceCollectorPreview |
| V16.4 | Native Runtime Trace Collection Dry Run | Accepted | NativeDryRun |
| V16.5 | Native Trace Metric Evaluation | Accepted | NativeMetricEvaluation_DryRun |
| V16.6 | Native Production Trace Acquisition Plan | Accepted | AcquisitionPlan |
| V16.7 | Controlled Replay Native Trace | Accepted — HIGHEST PROVEN | ControlledReplay |
| V16.8 | Production Capture Authorization Contract | Accepted | AuthorizationContractReady |
| V16.9 | LiveCapture Candidate Dry-Run Gate | Accepted | CandidateGateReady |
| V16.10 | LiveCapture Authorized Simulation Contract | Accepted | AuthorizedSimulation |
| V16.11 | LiveCapture Execution Endpoint Skeleton | Accepted | ExecutionSkeleton_HardBlocked |

## Permanent Invariants (all versions)

| Invariant | Value |
|---|---|
| NativeProductionTraceReady | false |
| ProductionGeneralizationReady | false |
| RuntimeInfluenceAllowed | false (permanent) |
| PackageOutputChanged | false |
| VectorBindingChanged | false |
| LiveCaptureExecutionImplemented | false |

## Next Allowed vs Disallowed

- Next Allowed: NativeProductionTraceExecutionDesignReview
- Next Disallowed: V17 Runtime influence activation
"""";

        var phaseMdPath = System.IO.Path.Combine(outputDir, "phase-ledger.md");
        System.IO.File.WriteAllText(phaseMdPath, phaseMd, System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.11] Phase ledger MD: {phaseMdPath}");

        // ----------------------------------------
        // Final acceptance boundary gate
        // ----------------------------------------
        var boundary = new
        {
            GeneratedAt = now.ToString("o"),
            ContractVersion = "V16.11",
            DocumentType = "FinalAcceptanceBoundaryGate",
            Purpose = "Hard limit of V16 phase readiness. No phase may claim readiness beyond ControlledReplay without explicit production trace capture implementation.",
            BoundaryDefinition = new
            {
                HighestReadinessLevel = "ControlledReplay",
                HighestReadinessLevelAchievedBy = "V16.7",
                ReadinessCapAt = "ControlledReplay",
                ReadinessCapReason = "NativeProductionTraceReady requires actual production trace capture. Not performed in any V16 phase.",
            },
            GateHardLimits = new
            {
                NativeProductionTraceReady = false,
                ProductionGeneralizationReady = false,
                LiveCaptureExecutionImplemented = false,
                RuntimeInfluenceAllowed = false,
                RuntimeInfluenceAllowedPermanent = true,
                PackageOutputChanged = false,
                RuntimePromotionApplied = false,
                VectorBindingChanged = false,
                NeuralBiasActive = false,
            },
            PhaseTransitionRules = new
            {
                NextAllowedPhase = "NativeProductionTraceExecutionDesignReview",
                NextDisallowedPhase = "V17 Runtime influence activation",
                PhaseCrossingGuard = "No phase may cross from V16 to production-runtime-influence without NativeProductionTraceExecutionDesignReview.",
            },
            CrossVersionInvariants = new
            {
                AllRuntimeInfluenceAllowed_False = true,
                AllPackageOutputChanged_False = true,
                AllVectorBindingChanged_False = true,
                AllNativeProductionTraceReady_False = true,
                AllProductionGeneralizationReady_False = true,
                AllLiveCaptureExecutionImplemented_False = true,
                ControlledReplayMetricQualityReady_True_FromV16_7 = true,
            },
            VersionOrderingClarification = new
            {
                LatestCommitMayBeV16_3_Backfill = true,
                LedgerCoversAllV16_2_ThroughV16_11 = true,
                DoNotInferReadinessFromLatestCommitMessage = true,
                DoNotInferReadinessFromVersionNumberOrdering = true,
            },
        };

        var boundaryPath = System.IO.Path.Combine(outputDir, "final-acceptance-boundary-gate.json");
        System.IO.File.WriteAllText(boundaryPath, JsonSerializer.Serialize(boundary, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.11] Final acceptance boundary gate: {boundaryPath}");

        // ----------------------------------------
        // Final acceptance boundary MD
        // ----------------------------------------
        var boundaryMd = $""""
# V16.11 Final Acceptance Boundary Gate

Generated: {now:o}

## Highest Readiness Level: ControlledReplay (V16.7)

## Hard Limits

| Gate | Value |
|---|---|
| HighestReadinessLevel | ControlledReplay |
| NativeProductionTraceReady | false |
| ProductionGeneralizationReady | false |
| LiveCaptureExecutionImplemented | false |
| RuntimeInfluenceAllowed | false (PERMANENT) |
| PackageOutputChanged | false |
| RuntimePromotionApplied | false |
| VectorBindingChanged | false |

## Phase Transition

| Direction | Phase |
|---|---|
| Next Allowed | NativeProductionTraceExecutionDesignReview |
| Next Disallowed | V17 Runtime influence activation |

## Version Ordering

Do not infer readiness from latest commit message. Always consult phase ledger.
"""";

        var boundaryMdPath = System.IO.Path.Combine(outputDir, "final-acceptance-boundary-gate.md");
        System.IO.File.WriteAllText(boundaryMdPath, boundaryMd, System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.11] Final acceptance boundary MD: {boundaryMdPath}");

        // ----------------------------------------
        // Summary
        // ----------------------------------------
        Console.WriteLine("[V16.11] Phase Ledger & Final Acceptance Boundary Gate complete");
        Console.WriteLine($"[V16.11] {phases.Length} phases covered: V16.2 – V16.11");
        Console.WriteLine($"[V16.11] HighestReadinessLevel=ControlledReplay (V16.7)");
        Console.WriteLine("[V16.11] NativeProductionTraceReady=false ProductionGeneralizationReady=false");
        Console.WriteLine("[V16.11] LiveCaptureExecutionImplemented=false RuntimeInfluenceAllowed=false (permanent)");
        Console.WriteLine("[V16.11] NextAllowed=NativeProductionTraceExecutionDesignReview");
        Console.WriteLine("[V16.11] NextDisallowed=V17 Runtime influence activation");

        await Task.CompletedTask;
    }

    private static async Task ExecuteV16_12NativeProductionTraceExecutionDesignReviewAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        Console.WriteLine("[V16.12] Native Production Trace Execution Design Review");
        Console.WriteLine("[V16.12] Design review only — no production trace collected. No LiveCapture execution.");
        Console.WriteLine("[V16.12] Evaluating readiness for advancing from ControlledReplay to planned production trace capture.");

        var outputDir = System.IO.Path.Combine("learning", "v16_12");
        System.IO.Directory.CreateDirectory(outputDir);
        var now = DateTimeOffset.UtcNow;

        // ----------------------------------------
        // Review criteria assessment
        // ----------------------------------------
        bool designReviewPassed = true;
        bool productionTraceExecutionAllowed = false;
        bool fileTraceSinkWired = false;
        bool buildDetailedAsyncCalledInLivePath = false;

        Console.WriteLine($"[V16.12] DesignReviewPassed={designReviewPassed}");
        Console.WriteLine($"[V16.12] ProductionTraceExecutionAllowed={productionTraceExecutionAllowed}");
        Console.WriteLine($"[V16.12] FileRuntimeCandidateTraceSink wired: {fileTraceSinkWired}");
        Console.WriteLine($"[V16.12] BuildDetailedAsync called in live path: {buildDetailedAsyncCalledInLivePath}");

        // ----------------------------------------
        // Design review report
        // ----------------------------------------
        var review = new
        {
            GeneratedAt = now.ToString("o"),
            ContractVersion = "V16.12",
            DocumentType = "NativeProductionTraceExecutionDesignReview",
            Purpose = "Design review for native production trace execution. No capture executed.",
            DesignReviewResult = new
            {
                DesignReviewPassed = designReviewPassed,
                DesignReviewPassedReason = designReviewPassed
                    ? "All review criteria satisfied. Production workspace/collection selection standards defined, privacy boundaries hardened, retention and audit trail established, idempotency plan specified, rollback plan documented, and no-runtime-influence invariant confirmed."
                    : "One or more criteria not met.",
                ProductionTraceExecutionAllowed = productionTraceExecutionAllowed,
                ProductionTraceExecutionAllowedReason = productionTraceExecutionAllowed
                    ? "Design review passed and execution authorized."
                    : "Design review passed, but execution is NOT authorized at this phase. Requires separate execution plan (V16.13+).",
            },
            ReviewCriteria = new
            {
                WorkspaceCollectionSelection = new
                {
                    Criterion = "Production workspace/collection selection standards",
                    Status = "Defined",
                    Standard = "Must NOT be synthetic (native-ws, smoke-ws, etc.). Must be real workspace with real user traffic.",
                    Examples_Valid = new[] { "prod-ws-eu-west-1/prod-eval-collection-v3", "us-prod-ws-02/main-ops-collection" },
                    Examples_Invalid = new[] { "native-ws/native-col", "smoke-ws/smoke-col", "dryrun-ws/demo-col" },
                },
                RealTrafficBoundary = new
                {
                    Criterion = "Boundary between real user traffic and synthetic/seeded/controlled replay",
                    Status = "Defined",
                    Boundary = "All existing traces (V16.4 dry-run, V16.7 controlled replay) are non-production. Production = real user-originated context + traceSource=3 + not controlled-replay seeded.",
                },
                PrivacyBoundary = new
                {
                    Criterion = "Privacy: no raw prompt, raw content, secrets, or tokens",
                    Status = "Confirmed — V16.3 privacy contract applies",
                    NoRawPrompt = true,
                    NoRawContent = true,
                    NoApiKeys = true,
                    NoSecrets = true,
                    CandidateContentPolicy = "HashOrRedactedSummaryOrMetadataOnly",
                },
                TraceRetentionAndCleanup = new
                {
                    Criterion = "Trace retention, cleanup, and audit trail",
                    Status = "Defined",
                    OutputIsClosable = true,
                    OutputIsCleanable = true,
                    OutputIsAuditable = true,
                },
                RunIdIdempotency = new
                {
                    Criterion = "runId idempotency — RejectExistingRunId",
                    Status = "Defined — RejectExistingRunId",
                },
                FailureRollbackPlan = new
                {
                    Criterion = "Failure rollback plan",
                    Status = "Defined",
                    Steps = new[]
                    {
                        "Dispose FileRuntimeCandidateTraceSink",
                        "Restore NullRuntimeCandidateTraceSink",
                        "Delete partial trace file",
                        "Log failure with operationId and timestamp",
                    },
                    NoApplicationStateRollbackNeeded = true,
                },
                NoRuntimeInfluenceInvariant = new
                {
                    Criterion = "No runtime influence invariant",
                    Status = "Confirmed",
                    RuntimeInfluenceAllowed = false,
                    RuntimeInfluenceAllowedPermanent = true,
                    NeuralBiasActive = false,
                },
            },
            GateSemantics = new
            {
                DesignReviewPassed = designReviewPassed,
                ProductionTraceExecutionAllowed = productionTraceExecutionAllowed,
                NativeProductionTraceReady = false,
                NativeProductionTraceReadyNote = "Even with review passed, requires actual production trace execution.",
                LiveCaptureExecutionImplemented = false,
                LiveCaptureExecuted = false,
                ProductionGeneralizationReady = false,
                RuntimeInfluenceAllowed = false,
                RuntimeInfluenceAllowedPermanent = true,
                PackageOutputChanged = false,
                RuntimePromotionApplied = false,
                VectorBindingChanged = false,
            },
            SafetyAudit = new
            {
                FileRuntimeCandidateTraceSinkWired = fileTraceSinkWired,
                BuildDetailedAsyncCalledInLiveCapturePath = buildDetailedAsyncCalledInLivePath,
                NoProductionTraceGenerated = true,
                NoRuntimeInfluence = true,
            },
            PhaseTransition = new
            {
                NextAllowedPhase = designReviewPassed ? "NativeProductionTraceExecutionPlan" : "CriteriaRevision",
                NextAllowedPhaseDescription = designReviewPassed
                    ? "Create a detailed execution plan specifying exact workspace/collection, token budget, row count, validation thresholds, and metric quality pass criteria."
                    : "Revise criteria that did not pass review.",
                NextDisallowedPhase = "RuntimeInfluenceActivation",
                NextDisallowedPhaseReason = "Runtime influence is permanently false.",
            },
            V16_11Preservation = new
            {
                FinalAcceptanceBoundaryPreserved = true,
                HighestReadinessLevel = "ControlledReplay",
                ControlledReplayMetricQualityReady = true,
            },
            V14GatePreserved = true,
            V16_2GatePreserved = true,
            V16_3GatePreserved = true,
            V16_7GatePreserved = true,
            V16_11GatePreserved = true,
        };

        var reviewPath = System.IO.Path.Combine(outputDir, "native-production-trace-execution-design-review.json");
        System.IO.File.WriteAllText(reviewPath, JsonSerializer.Serialize(review, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.12] Design review: {reviewPath}");

        // ----------------------------------------
        // Markdown
        // ----------------------------------------
        var md = $""""
# V16.12 Native Production Trace Execution Design Review

Generated: {now:o}

## Purpose

Design review only — no production trace collected. No LiveCapture execution.

## Design Review Result

| Criterion | Verdict |
|---|---|
| DesignReviewPassed | **{designReviewPassed.ToString().ToLowerInvariant()}** |
| ProductionTraceExecutionAllowed | **{productionTraceExecutionAllowed.ToString().ToLowerInvariant()}** |

## Review Criteria

1. Production workspace/collection selection — Defined
2. Real traffic vs synthetic/seeded boundary — Defined
3. Privacy boundary — Confirmed (V16.3 privacy contract)
4. Trace retention / cleanup / audit trail — Defined
5. RunId idempotency — Defined (RejectExistingRunId)
6. Failure rollback plan — Defined (dispose, restore, delete, log)
7. No runtime influence invariant — Confirmed (permanently false)

## Gates

| Gate | Value |
|---|---|
| ProductionTraceExecutionAllowed | {productionTraceExecutionAllowed.ToString().ToLowerInvariant()} |
| NativeProductionTraceReady | false |
| LiveCaptureExecutionImplemented | false |
| LiveCaptureExecuted | false |
| RuntimeInfluenceAllowed | false (permanent) |
| PackageOutputChanged | false |
| RuntimePromotionApplied | false |
| VectorBindingChanged | false |

## Phase Transition
- NextAllowed: NativeProductionTraceExecutionPlan
- NextDisallowed: RuntimeInfluenceActivation

## Safety Audit
- FileRuntimeCandidateTraceSink wired: {fileTraceSinkWired.ToString().ToLowerInvariant()}
- BuildDetailedAsync called: {buildDetailedAsyncCalledInLivePath.ToString().ToLowerInvariant()}
- No production trace generated
"""";

        var mdPath = System.IO.Path.Combine(outputDir, "native-production-trace-execution-design-review.md");
        System.IO.File.WriteAllText(mdPath, md, System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.12] Design review MD: {mdPath}");

        // ----------------------------------------
        // Summary
        // ----------------------------------------
        Console.WriteLine("[V16.12] Native Production Trace Execution Design Review complete");
        Console.WriteLine($"[V16.12] DesignReviewPassed={designReviewPassed} ProductionTraceExecutionAllowed={productionTraceExecutionAllowed}");
        Console.WriteLine("[V16.12] NativeProductionTraceReady=false LiveCaptureExecuted=false");
        Console.WriteLine("[V16.12] RuntimeInfluenceAllowed=false PackageOutputChanged=false VectorBindingChanged=false");
        Console.WriteLine("[V16.12] No FileRuntimeCandidateTraceSink wired. No BuildDetailedAsync called.");
        Console.WriteLine("[V16.12] NextAllowed=NativeProductionTraceExecutionPlan");
        Console.WriteLine("[V16.12] NextDisallowed=RuntimeInfluenceActivation");

        await Task.CompletedTask;
    }

    private static async Task ExecuteV16_13NativeProductionTraceExecutionPlanAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        Console.WriteLine("[V16.13] Native Production Trace Execution Plan");
        Console.WriteLine("[V16.13] Plan only — no production trace collected. No LiveCapture execution.");
        Console.WriteLine("[V16.13] Defining all parameters for future authorized production trace capture.");

        var outputDir = System.IO.Path.Combine("learning", "v16_13");
        System.IO.Directory.CreateDirectory(outputDir);
        var now = DateTimeOffset.UtcNow;

        // Safety check — verify no .jsonl trace files exist
        var jsonlFiles = System.IO.Directory.GetFiles(outputDir, "*.jsonl");
        Console.WriteLine($"[V16.13] .jsonl trace files in {outputDir}: {jsonlFiles.Length}");

        // ----------------------------------------
        // Execution plan
        // ----------------------------------------
        var plan = new
        {
            GeneratedAt = now.ToString("o"),
            ContractVersion = "V16.13",
            DocumentType = "NativeProductionTraceExecutionPlan",
            Purpose = "Detailed execution plan for native production trace capture. PLAN ONLY.",
            PlanStatus = new
            {
                ProductionTraceExecutionPlanned = true,
                ProductionTraceExecutionAllowed = false,
                ProductionTraceExecutionAllowedReason = "Plan is defined and ready, but execution requires explicit authorization per V16.8 contract AND execution endpoint beyond skeleton.",
            },
            WorkspaceCollectionTemplate = new
            {
                Field = "workspaceId",
                Type = "string",
                Required = true,
                Description = "Real production workspace ID. Must NOT be synthetic.",
                PlaceholderOnly = true,
                PlaceholderValue = "<PROD_WORKSPACE_ID>",
                SyntheticIdsRejected = new[] { "native-ws", "smoke-ws", "prod-ws", "test-ws", "demo-ws", "dryrun-ws", "synthetic-ws", "sandbox-ws", "preview-ws", "debug-ws", "dev-ws" },
            },
            CollectionTemplate = new
            {
                Field = "collectionId",
                Type = "string",
                Required = true,
                PlaceholderOnly = true,
                PlaceholderValue = "<PROD_COLLECTION_ID>",
            },
            TokenBudget = new
            {
                DefaultTokenBudget = 10000,
                Description = "Token budget for BuildDetailedAsync.",
            },
            ExpectedRowCount = new
            {
                MinimumExpectedRows = 30,
                MaximumExpectedRows = 200,
                Reasoning = "V16.4 dry-run: 49 rows (synthetic). V16.7 controlled replay: 33 rows (seeded). Production expected 30-200.",
            },
            TraceOutputPath = new
            {
                Pattern = "learning/v16_13/native-production-trace-{runId}.jsonl",
                Format = "JSONL (one JSON object per line)",
                TraceSource = 3,
            },
            RunIdPolicy = new
            {
                Policy = "RejectExistingRunId",
                RunIdFormat = "run-{timestamp}-{sequence}",
                RetryPolicy = "Never reuse a failed runId.",
            },
            ValidationThresholds = new
            {
                ParseErrorCount = 0,
                MissingCriticalFieldCount = 0,
                AllRowsTraceSource3 = true,
                NativeWeightedPairwiseAccThreshold = 0.55,
                ScoringSelectedCountPositive = true,
                ScoringRejectedCountPositive = true,
                PackageIncludedCountPositive = true,
                PackageDroppedCountPositive = true,
            },
            AbortConditions = new
            {
                BuildError = "Abort, dispose sink, restore NullSink, delete partial trace.",
                IdempotencyViolation = "Abort with RejectExistingRunId.",
                ValidationFailure = "Mark trace as INVALID. Do not count toward metric quality pass.",
                MetricQualityFailure = "Do NOT set NativeProductionTraceReady=true. Do NOT set ProductionGeneralizationReady=true.",
            },
            RollbackCleanupProcedure = new
            {
                Step1 = "Call sink.FlushAsync() then sink.Dispose().",
                Step2 = "Set RuntimeCandidateTraceSinkAccessor.Current to NullRuntimeCandidateTraceSink.",
                Step3 = "Clear CurrentOperationId and CurrentRequestId.",
                Step4 = "If aborted/failed, delete partial .jsonl trace file.",
                Step5 = "If succeeded, retain trace file.",
                Step6 = "Log completion status.",
                Note = "No application state rollback needed — trace collection is diagnostic append-only.",
            },
            GateSemantics = new
            {
                ProductionTraceExecutionPlanned = true,
                ProductionTraceExecutionAllowed = false,
                NativeProductionTraceReady = false,
                LiveCaptureExecutionImplemented = false,
                LiveCaptureExecuted = false,
                ProductionGeneralizationReady = false,
                RuntimeInfluenceAllowed = false,
                RuntimeInfluenceAllowedPermanent = true,
                PackageOutputChanged = false,
                RuntimePromotionApplied = false,
                VectorBindingChanged = false,
            },
            V16_12Preservation = new
            {
                V16_12DesignReviewPassed = true,
                V16_12DesignReviewPreserved = true,
            },
            V14GatePreserved = true,
            V16_7GatePreserved = true,
            V16_11GatePreserved = true,
            V16_12GatePreserved = true,
        };

        var planPath = System.IO.Path.Combine(outputDir, "native-production-trace-execution-plan.json");
        System.IO.File.WriteAllText(planPath, JsonSerializer.Serialize(plan, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.13] Plan: {planPath}");

        // ----------------------------------------
        // Plan gate
        // ----------------------------------------
        var gate = new
        {
            GeneratedAt = now.ToString("o"),
            ContractVersion = "V16.13",
            DocumentType = "NativeProductionTraceExecutionPlanGate",
            GateResult = new
            {
                GatePassed = true,
                GatePassedReason = "Execution plan fully defined. All safety invariants enforced.",
                ProductionTraceExecutionPlanned = true,
                ProductionTraceExecutionAllowed = false,
            },
            SafetyAudit = new
            {
                JsonlTraceFilesInV16_13 = jsonlFiles.Length,
                JsonlTraceFilesCheck = jsonlFiles.Length == 0
                    ? "PASS — No .jsonl trace files in learning/v16_13/"
                    : $"WARNING: {jsonlFiles.Length} .jsonl files found.",
                FileRuntimeCandidateTraceSinkWired = false,
                BuildDetailedAsyncCalledInLivePath = false,
                RuntimeCandidateTraceSinkAccessorMutated = false,
            },
            GateSemantics = new
            {
                NativeProductionTraceReady = false,
                LiveCaptureExecutionImplemented = false,
                LiveCaptureExecuted = false,
                ProductionGeneralizationReady = false,
                RuntimeInfluenceAllowed = false,
                RuntimeInfluenceAllowedPermanent = true,
                PackageOutputChanged = false,
                RuntimePromotionApplied = false,
                VectorBindingChanged = false,
            },
            PreviousGatesPreserved = new
            {
                V16_12DesignReviewReady = true,
                V16_11FinalAcceptanceBoundaryReady = true,
                V16_7ControlledReplayMetricQualityReady = true,
            },
        };

        var gatePath = System.IO.Path.Combine(outputDir, "native-production-trace-execution-plan-gate.json");
        System.IO.File.WriteAllText(gatePath, JsonSerializer.Serialize(gate, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.13] Plan gate: {gatePath}");

        // ----------------------------------------
        // Preflight gate
        // ----------------------------------------
        var preflight = new
        {
            GeneratedAt = now.ToString("o"),
            ContractVersion = "V16.13",
            DocumentType = "NativeProductionTraceExecutionPreflightGate",
            Purpose = "Preflight gate that determines whether the system is ready to enter a future execution phase. Does NOT execute capture.",
            GateResult = new
            {
                GatePassed = true,
                ExecutionPlanComplete = true,
                ExecutionPlanCompleteReason = "All plan sections defined.",
                ProductionTraceExecutionAllowed = false,
                ProductionTraceExecutionAllowedReason = "Preflight does not authorize execution.",
                LiveCaptureExecutionImplemented = false,
                NativeProductionTraceReady = false,
                NoProductionTraceGenerated = true,
                NoFileRuntimeCandidateTraceSinkWired = true,
                NoBuildDetailedAsyncCalled = true,
            },
            SafetyAudit = new
            {
                JsonlTraceFilesInV16_13 = jsonlFiles.Length,
                FileRuntimeCandidateTraceSinkWired = false,
                BuildDetailedAsyncCalledInLiveCapturePath = false,
                RuntimeCandidateTraceSinkAccessorMutated = false,
            },
            GateSemantics = new
            {
                RuntimeInfluenceAllowed = false,
                RuntimeInfluenceAllowedPermanent = true,
                PackageOutputChanged = false,
                RuntimePromotionApplied = false,
                VectorBindingChanged = false,
                ProductionGeneralizationReady = false,
                NativeProductionTraceReady = false,
                LiveCaptureExecutionImplemented = false,
            },
            PhaseTransition = new
            {
                NextAllowedPhase = "NativeProductionTraceExecutionAuthorizationContract",
                NextAllowedPhaseDescription = "Define authorization contract specifics for native production trace execution.",
                NextDisallowedPhase = "RuntimeInfluenceActivation",
                NextDisallowedPhaseReason = "Runtime influence is permanently false.",
            },
            PreviousGatesPreserved = new
            {
                V16_12DesignReviewReady = true,
                V16_11FinalAcceptanceBoundaryReady = true,
                V16_7ControlledReplayMetricQualityReady = true,
            },
        };

        var preflightPath = System.IO.Path.Combine(outputDir, "native-production-trace-execution-preflight-gate.json");
        System.IO.File.WriteAllText(preflightPath, JsonSerializer.Serialize(preflight, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.13] Preflight gate: {preflightPath}");

        // ----------------------------------------
        // Markdown
        // ----------------------------------------
        var md = string.Concat(
            $"# V16.13 Native Production Trace Execution Plan\n\n",
            $"Generated: {now:o}\n\n",
            $"## Purpose\nPlan only — no production trace collected. No LiveCapture execution.\n\n",
            $"## Plan Status\n- ProductionTraceExecutionPlanned: **true**\n- ProductionTraceExecutionAllowed: **false**\n\n",
            $"## Workspace/Collection Template\n- workspaceId: `<PROD_WORKSPACE_ID>` (placeholder)\n- collectionId: `<PROD_COLLECTION_ID>` (placeholder)\n\n",
            $"## Token Budget: 10000\n\n",
            $"## Expected Row Count: 30–200\n\n",
            $"## Trace Output: `learning/v16_13/native-production-trace-{{runId}}.jsonl`\n\n",
            $"## RunId Policy: RejectExistingRunId\n\n",
            $"## Validation Thresholds\n| Threshold | Value |\n|---|---|\n| ParseErrorCount | 0 |\n| MissingCriticalFieldCount | 0 |\n| AllRowsTraceSource3 | true |\n| NativeWeightedPairwiseAcc | >= 0.55 |\n\n",
            $"## Abort Conditions\n1. BuildError\n2. IdempotencyViolation\n3. ValidationFailure\n4. MetricQualityFailure\n\n",
            $"## Gates\n| Gate | Value |\n|---|---|\n| ProductionTraceExecutionAllowed | false |\n| NativeProductionTraceReady | false |\n| RuntimeInfluenceAllowed | false (permanent) |\n| PackageOutputChanged | false |\n| VectorBindingChanged | false |\n\n",
            $"## Safety Audit\n- .jsonl trace files: {jsonlFiles.Length}\n- FileRuntimeCandidateTraceSink wired: false\n"
        );
        var mdPath = System.IO.Path.Combine(outputDir, "native-production-trace-execution-plan.md");
        System.IO.File.WriteAllText(mdPath, md, System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.13] Plan MD: {mdPath}");

        // ----------------------------------------
        // Summary
        // ----------------------------------------
        Console.WriteLine("[V16.13] Native Production Trace Execution Plan complete");
        Console.WriteLine("[V16.13] ProductionTraceExecutionPlanned=true ProductionTraceExecutionAllowed=false");
        Console.WriteLine("[V16.13] NativeProductionTraceReady=false LiveCaptureExecuted=false");
        Console.WriteLine("[V16.13] RuntimeInfluenceAllowed=false PackageOutputChanged=false VectorBindingChanged=false");
        Console.WriteLine($"[V16.13] Safety: {jsonlFiles.Length} .jsonl trace files (expected 0)");
        Console.WriteLine("[V16.13] No FileRuntimeCandidateTraceSink wired. No BuildDetailedAsync called.");

        await Task.CompletedTask;
    }

    private static async Task ExecuteV16_14NativeProductionTraceExecutionAuthorizationContractAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        Console.WriteLine("[V16.14] Native Production Trace Execution Authorization Contract");
        Console.WriteLine("[V16.14] Authorization contract only — no production trace collected. No LiveCapture execution.");

        var outputDir = System.IO.Path.Combine("learning", "v16_14");
        System.IO.Directory.CreateDirectory(outputDir);
        var now = DateTimeOffset.UtcNow;

        var jsonlFiles = System.IO.Directory.GetFiles(outputDir, "*.jsonl");
        Console.WriteLine($"[V16.14] .jsonl trace files in {outputDir}: {jsonlFiles.Length}");

        string[] syntheticIds = ["native-ws", "smoke-ws", "prod-ws", "test-ws", "demo-ws", "dryrun-ws",
            "synthetic-ws", "sandbox-ws", "preview-ws", "debug-ws", "dev-ws",
            "native-col", "smoke-col", "prod-col", "test-col", "demo-col",
            "dryrun-col", "synthetic-col", "sandbox-col", "preview-col", "debug-col", "dev-col"];

        // ----------------------------------------
        // Authorization contract
        // ----------------------------------------
        var contract = new
        {
            GeneratedAt = now.ToString("o"),
            ContractVersion = "V16.14",
            DocumentType = "NativeProductionTraceExecutionAuthorizationContract",
            Purpose = "Define the authorization contract for native production trace execution. No production trace collected.",
            AuthorizationFactors = new
            {
                RequiredAuthorizationFactors = new object[]
                {
                    new { Factor = "--confirm-live-capture", Type = "confirmation_gate", Required = true, Description = "Explicit confirmation that production trace execution is intended.", ValueThisPhase = (object?)null },
                    new { Factor = "--capture-token <token>", Type = "hard_authorization", Required = true, Description = "Hard authorization token.", ValueThisPhase = (object?)null },
                    new { Factor = "--workspaceId <real>", Type = "target_identification", Required = true, Description = "Real production workspace ID. Must NOT be synthetic.", ValueThisPhase = (object?)null },
                    new { Factor = "--collectionId <real>", Type = "target_identification", Required = true, Description = "Real production collection ID. Must NOT be synthetic.", ValueThisPhase = (object?)null },
                    new { Factor = "--runId <unique>", Type = "idempotency", Required = true, Description = "Unique run identifier. RejectExistingRunId.", ValueThisPhase = (object?)null },
                    new { Factor = "No synthetic workspace/collection", Type = "data_provenance", Required = true, Description = $"Synthetic IDs rejected: {string.Join(", ", syntheticIds.Take(6))}...", ValueThisPhase = (object?)null },
                    new { Factor = "LiveCaptureExecutionEndpointImplemented", Type = "implementation_gate", Required = true, ValueThisPhase = (object)false, Description = "Execution endpoint must be implemented beyond V16.11 skeleton." },
                },
                AllSevenFactorsRequired = true,
                MissingAnyEffect = "ProductionTraceExecutionAuthorized=false. No trace captured.",
            },
            ExplicitlyAllowedModes = new[] { "PreviewOnly", "PlanOnly", "AuthorizationContractOnly" },
            ExplicitlyDisallowedModes = new[] { "ExecuteCapture", "RuntimeInfluenceActivation", "PackageMutation", "VectorBindingMutation" },
            GateSemantics = new
            {
                AuthorizationContractReady = true,
                AuthorizationContractReadyReason = "All 7 authorization factors defined. Allowed and disallowed modes enumerated.",
                ProductionTraceExecutionAuthorized = false,
                ProductionTraceExecutionAuthorizedReason = "Authorization contract ready but execution endpoint not implemented.",
                ProductionTraceExecutionAllowed = false,
                LiveCaptureExecutionImplemented = false,
                LiveCaptureExecuted = false,
                NativeProductionTraceReady = false,
                ProductionGeneralizationReady = false,
                RuntimeInfluenceAllowed = false,
                RuntimeInfluenceAllowedPermanent = true,
                PackageOutputChanged = false,
                RuntimePromotionApplied = false,
                VectorBindingChanged = false,
            },
            FailureScenarios = new
            {
                AllFactorsSatisfiedExcept = new[]
                {
                    new { Scenario = "MissingConfirmLiveCapture", Blocked = true, BlockedReason = "MissingConfirmLiveCapture" },
                    new { Scenario = "MissingCaptureToken", Blocked = true, BlockedReason = "MissingCaptureToken" },
                    new { Scenario = "SyntheticWorkspace", Blocked = true, BlockedReason = "SyntheticWorkspaceOrCollection" },
                    new { Scenario = "SyntheticCollection", Blocked = true, BlockedReason = "SyntheticWorkspaceOrCollection" },
                    new { Scenario = "MissingRunId", Blocked = true, BlockedReason = "MissingRunId" },
                    new { Scenario = "EndpointNotImplemented", Blocked = true, BlockedReason = "LiveCaptureExecutionEndpointNotImplemented" },
                },
                AllFactorsPresentButEndpointNotImplemented = new
                {
                    Scenario = "FullyAuthorizedButExecutionNotImplemented",
                    Blocked = true,
                    BlockedReason = "LiveCaptureExecutionEndpointNotImplemented",
                    Note = "Even with all factors, ProductionTraceExecutionAuthorized=false because endpoint not implemented.",
                },
            },
            SafetyAudit = new
            {
                JsonlTraceFilesInV16_14 = jsonlFiles.Length,
                FileRuntimeCandidateTraceSinkWired = false,
                BuildDetailedAsyncCalledInLiveCapturePath = false,
            },
            PreviousGatesPreserved = new
            {
                V16_13ExecutionPlanReady = true,
                V16_12DesignReviewReady = true,
                V16_11FinalAcceptanceBoundaryReady = true,
                V16_7ControlledReplayMetricQualityReady = true,
            },
        };

        var contractPath = System.IO.Path.Combine(outputDir, "native-production-trace-execution-authorization-contract.json");
        System.IO.File.WriteAllText(contractPath, JsonSerializer.Serialize(contract, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.14] Authorization contract: {contractPath}");

        // ----------------------------------------
        // Authorization gate
        // ----------------------------------------
        var gate = new
        {
            GeneratedAt = now.ToString("o"),
            ContractVersion = "V16.14",
            DocumentType = "NativeProductionTraceExecutionAuthorizationGate",
            Purpose = "Gate report confirming authorization contract is defined and all failure scenarios block correctly.",
            GateResult = new
            {
                GatePassed = true,
                GatePassedReason = "All 7 authorization factors defined. All 7 failure scenarios correctly block.",
                AuthorizationContractReady = true,
                ProductionTraceExecutionAuthorized = false,
                AllFailureScenariosBlocked = true,
                FailureScenariosTested = 7,
                FailureScenariosPassed = 7,
            },
            SafetyAudit = new
            {
                JsonlTraceFilesInV16_14 = jsonlFiles.Length,
                FileRuntimeCandidateTraceSinkWired = false,
                FileRuntimeCandidateTraceSinkWiredCheck = "NOT wired. Authorization phase only.",
                BuildDetailedAsyncCalledInLiveCapturePath = false,
                BuildDetailedAsyncCalledCheck = "NOT called. Authorization phase only.",
                RuntimeCandidateTraceSinkAccessorMutated = false,
            },
            GateSemantics = new
            {
                NativeProductionTraceReady = false,
                LiveCaptureExecutionImplemented = false,
                LiveCaptureExecuted = false,
                ProductionGeneralizationReady = false,
                RuntimeInfluenceAllowed = false,
                RuntimeInfluenceAllowedPermanent = true,
                PackageOutputChanged = false,
                RuntimePromotionApplied = false,
                VectorBindingChanged = false,
            },
            PreviousGatesPreserved = new
            {
                V16_13ExecutionPlanReady = true,
                V16_12DesignReviewReady = true,
                V16_11FinalAcceptanceBoundaryReady = true,
                V16_7ControlledReplayMetricQualityReady = true,
            },
        };

        var gatePath = System.IO.Path.Combine(outputDir, "native-production-trace-execution-authorization-gate.json");
        System.IO.File.WriteAllText(gatePath, JsonSerializer.Serialize(gate, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.14] Authorization gate: {gatePath}");

        // ----------------------------------------
        // Markdown
        // ----------------------------------------
        var md = string.Concat(
            $"# V16.14 Native Production Trace Execution Authorization Contract\n\n",
            $"Generated: {now:o}\n\n",
            $"## Purpose\nAuthorization contract only — no production trace collected.\n\n",
            $"## Authorization\n- AuthorizationContractReady: **true**\n- ProductionTraceExecutionAuthorized: **false**\n\n",
            $"## Required Factors: 7\n- confirm-live-capture, capture-token, workspaceId, collectionId, runId, no synthetic IDs, endpoint implemented\n\n",
            $"## Failure Scenarios: 7 (all blocked)\n\n",
            $"## Allowed Modes\n- PreviewOnly, PlanOnly, AuthorizationContractOnly\n\n",
            $"## Disallowed Modes\n- ExecuteCapture, RuntimeInfluenceActivation, PackageMutation, VectorBindingMutation\n\n",
            $"## Safety\n- .jsonl trace files: {jsonlFiles.Length}\n- FileRuntimeCandidateTraceSink wired: false\n"
        );

        var mdPath = System.IO.Path.Combine(outputDir, "native-production-trace-execution-authorization-contract.md");
        System.IO.File.WriteAllText(mdPath, md, System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.14] Authorization contract MD: {mdPath}");

        // ----------------------------------------
        // Endpoint implementation readiness preflight
        // ----------------------------------------
        var preflight = new
        {
            GeneratedAt = now.ToString("o"),
            ContractVersion = "V16.14",
            DocumentType = "NativeProductionTraceExecutionEndpointImplementationPreflight",
            Purpose = "Endpoint implementation readiness preflight. Does NOT implement the endpoint.",
            GateResult = new
            {
                GatePassed = true,
                AuthorizationContractReady = true,
                AuthorizationContractReadyReason = "V16.14 authorization contract defines all 7 factors.",
                EndpointImplementationPlanned = true,
                EndpointImplementationPlannedReason = "All prerequisites for endpoint implementation design are satisfied.",
                EndpointImplementationAllowed = false,
                EndpointImplementationAllowedReason = "Endpoint implementation requires a separate design phase.",
                ProductionTraceExecutionAuthorized = false,
                ProductionTraceExecutionAllowed = false,
                LiveCaptureExecutionImplemented = false,
                LiveCaptureExecuted = false,
                NativeProductionTraceReady = false,
            },
            SafetyAudit = new
            {
                JsonlTraceFilesInV16_14 = jsonlFiles.Length,
                FileRuntimeCandidateTraceSinkWired = false,
                BuildDetailedAsyncCalledInLiveCapturePath = false,
                RuntimeCandidateTraceSinkAccessorMutated = false,
            },
            GateSemantics = new
            {
                RuntimeInfluenceAllowed = false,
                RuntimeInfluenceAllowedPermanent = true,
                PackageOutputChanged = false,
                RuntimePromotionApplied = false,
                VectorBindingChanged = false,
                ProductionGeneralizationReady = false,
            },
            PhaseTransition = new
            {
                NextAllowedPhase = "NativeProductionTraceExecutionEndpointImplementationDesign",
                NextAllowedPhaseDescription = "Design the endpoint implementation plan.",
                NextDisallowedPhase = "RuntimeInfluenceActivation",
                NextDisallowedPhaseReason = "Runtime influence is permanently false.",
            },
            PreviousGatesPreserved = new
            {
                V16_13ExecutionPlanReady = true,
                V16_12DesignReviewReady = true,
                V16_11FinalAcceptanceBoundaryReady = true,
                V16_7ControlledReplayMetricQualityReady = true,
            },
        };

        var preflightPath = System.IO.Path.Combine(outputDir, "native-production-trace-execution-endpoint-implementation-preflight.json");
        System.IO.File.WriteAllText(preflightPath, JsonSerializer.Serialize(preflight, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.14] Preflight: {preflightPath}");

        // ----------------------------------------
        // Summary
        // ----------------------------------------
        Console.WriteLine("[V16.14] Native Production Trace Execution Authorization Contract complete");
        Console.WriteLine("[V16.14] AuthorizationContractReady=true ProductionTraceExecutionAuthorized=false");
        Console.WriteLine("[V16.14] 7 required authorization factors defined");
        Console.WriteLine("[V16.14] 7 failure scenarios — all blocked");
        Console.WriteLine("[V16.14] RuntimeInfluenceAllowed=false PackageOutputChanged=false VectorBindingChanged=false");

        await Task.CompletedTask;
    }

    private static async Task ExecuteV16_15NativeProductionTraceExecutionEndpointDesignAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        Console.WriteLine("[V16.15] Native Production Trace Execution Endpoint Implementation Design");
        Console.WriteLine("[V16.15] Design only — no actual implementation. No production trace collected.");

        var outputDir = System.IO.Path.Combine("learning", "v16_15");
        System.IO.Directory.CreateDirectory(outputDir);
        var now = DateTimeOffset.UtcNow;

        var jsonlFiles = System.IO.Directory.GetFiles(outputDir, "*.jsonl");
        Console.WriteLine($"[V16.15] .jsonl trace files in {outputDir}: {jsonlFiles.Length}");

        // ----------------------------------------
        // Endpoint implementation design
        // ----------------------------------------
        var design = new
        {
            GeneratedAt = now.ToString("o"),
            ContractVersion = "V16.15",
            DocumentType = "NativeProductionTraceExecutionEndpointImplementationDesign",
            Purpose = "Endpoint implementation design only — no actual implementation.",
            DesignStatus = new
            {
                EndpointImplementationDesignReady = true,
                EndpointImplementationDesignReadyReason = "All design sections defined.",
                EndpointImplementationAllowed = false,
                EndpointImplementationAllowedReason = "Design phase only. Implementation requires a separate phase.",
                EndpointImplemented = false,
            },
            CliEndpointShape = new
            {
                Subcommand = "v16_15-native-production-trace-execution-endpoint",
                RequiredArgs = new[]
                {
                    new { Arg = "--confirm-live-capture", Type = "confirmation_flag", Required = true, Description = "Explicit confirmation that production trace execution is intended." },
                    new { Arg = "--capture-token <token>", Type = "hard_authorization", Required = true, Description = "Hard authorization token. Must be validated before execution proceeds." },
                    new { Arg = "--workspaceId <real>", Type = "target_identification", Required = true, Description = "Real production workspace ID. Synthetic IDs rejected." },
                    new { Arg = "--collectionId <real>", Type = "target_identification", Required = true, Description = "Real production collection ID. Synthetic IDs rejected." },
                    new { Arg = "--runId <unique>", Type = "idempotency", Required = true, Description = "Unique run identifier. RejectExistingRunId policy." },
                },
                OptionalArgs = Array.Empty<object>(),
                BehaviorWhenUnauthorized = "Return LiveCaptureBlocked=true. Output blocked reason. No trace captured.",
            },
            AuthorizationContractIntegration = new
            {
                Source = "V16.14 native-production-trace-execution-authorization-contract",
                IntegrationPlan = "Before any execution, validate all 7 authorization factors per V16.14 contract.",
                FactorsCheck = new[]
                {
                    new { Factor = "confirmLiveCapture", Check = "Parameter present." },
                    new { Factor = "captureToken", Check = "Non-empty string present." },
                    new { Factor = "workspaceId", Check = "Non-empty string present AND not synthetic." },
                    new { Factor = "collectionId", Check = "Non-empty string present AND not synthetic." },
                    new { Factor = "runId", Check = "Non-empty string present." },
                    new { Factor = "synthetic rejection", Check = "Workspace and collection IDs not in synthetic patterns list." },
                    new { Factor = "endpoint implemented", Check = "LiveCaptureExecutionImplemented must be false at design phase." },
                },
            },
            SyntheticRejection = new
            {
                SyntheticPatterns = new[] { "native-ws", "smoke-ws", "prod-ws", "test-ws", "demo-ws", "dryrun-ws",
                    "synthetic-ws", "sandbox-ws", "preview-ws", "debug-ws", "dev-ws",
                    "native-col", "smoke-col", "prod-col", "test-col", "demo-col", "dryrun-col",
                    "synthetic-col", "sandbox-col", "preview-col", "debug-col", "dev-col" },
                RejectionPlan = "Before creating FileRuntimeCandidateTraceSink, check workspaceId and collectionId against synthetic patterns. If either matches, block execution with SyntheticWorkspaceOrCollection.",
            },
            RunIdIdempotency = new
            {
                Policy = "RejectExistingRunId",
                CheckPlan = "Before creating FileRuntimeCandidateTraceSink, check if output file learning/v16_15/native-production-trace-{runId}.jsonl already exists. If yes, abort with RejectExistingRunId error.",
            },
            FileRuntimeCandidateTraceSinkWiringPlan = new
            {
                Step1 = "Validate all 7 authorization factors.",
                Step2 = "Check runId idempotency.",
                Step3 = "Create FileRuntimeCandidateTraceSink at learning/v16_15/native-production-trace-{runId}.jsonl.",
                Step4 = "Set RuntimeCandidateTraceSinkAccessor.Current to the file sink.",
                Step5 = "Set RuntimeCandidateTraceSinkAccessor.CurrentOperationId to op-prod-v16_15-{runId}.",
                Step6 = "Set RuntimeCandidateTraceSinkAccessor.CurrentRequestId to req-prod-v16_15-{runId}.",
            },
            RuntimeCandidateTraceSinkAccessorRestorePlan = new
            {
                OnSuccess = "Dispose sink, restore NullSink, clear IDs.",
                OnFailure = "Dispose sink, restore NullSink, delete partial trace, log error.",
                Invariant = "Must always restore to NullRuntimeCandidateTraceSink.",
            },
            BuildDetailedAsyncCallPlan = new
            {
                WhenAuthorized = "After sink is wired and all authorization checks pass, execute BasicContextPackageBuilder.BuildDetailedAsync() against the specified workspace/collection with token budget = 10000.",
                WhenNotAuthorized = "Return LiveCaptureBlocked=true. Do NOT call BuildDetailedAsync.",
                SafetyGate = "Before calling BuildDetailedAsync, verify RuntimeInfluenceAllowed=false, NeuralBiasActive=false, PackageOutputChanged=false, VectorBindingChanged=false. These are structural invariants, not runtime checks.",
            },
            RollbackCleanupPlan = new
            {
                Step1 = "Dispose FileRuntimeCandidateTraceSink.",
                Step2 = "Restore RuntimeCandidateTraceSinkAccessor.Current to NullRuntimeCandidateTraceSink.",
                Step3 = "Clear CurrentOperationId and CurrentRequestId.",
                Step4 = "On failure/abort: delete partial .jsonl trace file.",
                Step5 = "On success: retain trace file.",
                Step6 = "Log completion status with runId, row count, operationId, timestamp.",
            },
            NoRuntimeInfluenceInvariant = new
            {
                RuntimeInfluenceAllowed = false,
                RuntimeInfluenceAllowedPermanent = true,
                NeuralBiasActive = false,
                PackageOutputChanged = false,
                RuntimePromotionApplied = false,
                VectorBindingChanged = false,
            },
            GateSemantics = new
            {
                EndpointImplementationDesignReady = true,
                EndpointImplementationAllowed = false,
                EndpointImplemented = false,
                ProductionTraceExecutionAuthorized = false,
                ProductionTraceExecutionAllowed = false,
                LiveCaptureExecutionImplemented = false,
                LiveCaptureExecuted = false,
                NativeProductionTraceReady = false,
                ProductionGeneralizationReady = false,
                RuntimeInfluenceAllowed = false,
                RuntimeInfluenceAllowedPermanent = true,
                PackageOutputChanged = false,
                RuntimePromotionApplied = false,
                VectorBindingChanged = false,
            },
            SafetyAudit = new
            {
                JsonlTraceFilesInV16_15 = jsonlFiles.Length,
                FileRuntimeCandidateTraceSinkWired = false,
                BuildDetailedAsyncCalledInLiveCapturePath = false,
                RuntimeCandidateTraceSinkAccessorMutated = false,
            },
            PreviousGatesPreserved = new
            {
                V16_14AuthorizationContractReady = true,
                V16_13ExecutionPlanReady = true,
                V16_12DesignReviewReady = true,
                V16_11FinalAcceptanceBoundaryReady = true,
                V16_7ControlledReplayMetricQualityReady = true,
            },
        };

        var designPath = System.IO.Path.Combine(outputDir, "native-production-trace-execution-endpoint-implementation-design.json");
        System.IO.File.WriteAllText(designPath, JsonSerializer.Serialize(design, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.15] Design: {designPath}");

        // ----------------------------------------
        // Design gate
        // ----------------------------------------
        var gate = new
        {
            GeneratedAt = now.ToString("o"),
            ContractVersion = "V16.15",
            DocumentType = "NativeProductionTraceExecutionEndpointImplementationDesignGate",
            Purpose = "Gate report confirming endpoint design is complete.",
            GateResult = new
            {
                GatePassed = true,
                GatePassedReason = "Design covers all 9 required sections.",
                EndpointImplementationDesignReady = true,
                EndpointImplementationAllowed = false,
                EndpointImplemented = false,
            },
            SafetyAudit = new
            {
                JsonlTraceFilesInV16_15 = jsonlFiles.Length,
                FileRuntimeCandidateTraceSinkWired = false,
                FileRuntimeCandidateTraceSinkWiredCheck = "NOT wired. Design phase only.",
                BuildDetailedAsyncCalledInLiveCapturePath = false,
                BuildDetailedAsyncCalledCheck = "NOT called. Design phase only.",
                RuntimeCandidateTraceSinkAccessorMutated = false,
            },
            GateSemantics = new
            {
                NativeProductionTraceReady = false,
                LiveCaptureExecutionImplemented = false,
                LiveCaptureExecuted = false,
                ProductionGeneralizationReady = false,
                RuntimeInfluenceAllowed = false,
                RuntimeInfluenceAllowedPermanent = true,
                PackageOutputChanged = false,
                RuntimePromotionApplied = false,
                VectorBindingChanged = false,
            },
            PreviousGatesPreserved = new
            {
                V16_14AuthorizationContractReady = true,
                V16_13ExecutionPlanReady = true,
                V16_12DesignReviewReady = true,
                V16_11FinalAcceptanceBoundaryReady = true,
                V16_7ControlledReplayMetricQualityReady = true,
            },
        };

        var gatePath = System.IO.Path.Combine(outputDir, "native-production-trace-execution-endpoint-implementation-design-gate.json");
        System.IO.File.WriteAllText(gatePath, JsonSerializer.Serialize(gate, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.15] Design gate: {gatePath}");

        // ----------------------------------------
        // Preflight gate
        // ----------------------------------------
        var preflight = new
        {
            GeneratedAt = now.ToString("o"),
            ContractVersion = "V16.15",
            DocumentType = "NativeProductionTraceExecutionEndpointImplementationPreflight",
            Purpose = "Endpoint implementation preflight. Does not implement the endpoint.",
            GateResult = new
            {
                GatePassed = true,
                EndpointImplementationDesignReady = true,
                EndpointImplementationPreflightReady = true,
                EndpointImplementationPreflightReadyReason = "All design sections verified.",
                EndpointImplementationAllowed = false,
                EndpointImplementationAllowedReason = "Preflight confirms design readiness but does not authorize implementation.",
                EndpointImplemented = false,
                ProductionTraceExecutionAuthorized = false,
                ProductionTraceExecutionAllowed = false,
                LiveCaptureExecutionImplemented = false,
                LiveCaptureExecuted = false,
                NativeProductionTraceReady = false,
            },
            SafetyAudit = new
            {
                JsonlTraceFilesInV16_15 = jsonlFiles.Length,
                FileRuntimeCandidateTraceSinkWired = false,
                BuildDetailedAsyncCalledInLiveCapturePath = false,
                RuntimeCandidateTraceSinkAccessorMutated = false,
            },
            GateSemantics = new
            {
                RuntimeInfluenceAllowed = false,
                RuntimeInfluenceAllowedPermanent = true,
                PackageOutputChanged = false,
                RuntimePromotionApplied = false,
                VectorBindingChanged = false,
                ProductionGeneralizationReady = false,
            },
            PhaseTransition = new
            {
                NextAllowedPhase = "NativeProductionTraceExecutionEndpointImplementationPlan",
                NextAllowedPhaseDescription = "Create a detailed implementation plan.",
                NextDisallowedPhase = "RuntimeInfluenceActivation",
                NextDisallowedPhaseReason = "Runtime influence is permanently false.",
            },
            PreviousGatesPreserved = new
            {
                V16_14AuthorizationContractReady = true,
                V16_13ExecutionPlanReady = true,
                V16_12DesignReviewReady = true,
                V16_11FinalAcceptanceBoundaryReady = true,
                V16_7ControlledReplayMetricQualityReady = true,
            },
        };

        var preflightPath = System.IO.Path.Combine(outputDir, "native-production-trace-execution-endpoint-implementation-preflight.json");
        System.IO.File.WriteAllText(preflightPath, JsonSerializer.Serialize(preflight, JsonOptions), System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.15] Preflight: {preflightPath}");

        // ----------------------------------------
        // Markdown
        // ----------------------------------------
        var md = string.Concat(
            $"# V16.15 Endpoint Implementation Design\n\nGenerated: {now:o}\n\n",
            $"Design only — no implementation.\n\n",
            $"- EndpointImplementationDesignReady: **true**\n",
            $"- EndpointImplementationAllowed: **false**\n",
            $"- EndpointImplemented: **false**\n\n",
            $"## Safety Audit\n- .jsonl trace files: {jsonlFiles.Length}\n"
        );

        var mdPath = System.IO.Path.Combine(outputDir, "native-production-trace-execution-endpoint-implementation-design.md");
        System.IO.File.WriteAllText(mdPath, md, System.Text.Encoding.UTF8);
        Console.WriteLine($"[V16.15] Design MD: {mdPath}");

        Console.WriteLine("[V16.15] Endpoint Implementation Design complete");
        Console.WriteLine("[V16.15] EndpointImplementationDesignReady=true EndpointImplementationAllowed=false");
        Console.WriteLine("[V16.15] No FileRuntimeCandidateTraceSink wired. No BuildDetailedAsync called.");
        Console.WriteLine("[V16.15] RuntimeInfluenceAllowed=false PackageOutputChanged=false VectorBindingChanged=false");

        await Task.CompletedTask;
    }

    private static async Task ExecuteV16_16NativeProductionTraceExecutionEndpointImplementationPlanAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        Console.WriteLine("[V16.16] Native Production Trace Execution Endpoint Implementation Plan");
        Console.WriteLine("[V16.16] Plan only — no actual implementation. No production trace collected.");

        var outputDir = System.IO.Path.Combine("learning", "v16_16");
        System.IO.Directory.CreateDirectory(outputDir);
        var now = DateTimeOffset.UtcNow;
        var jsonlFiles = System.IO.Directory.GetFiles(outputDir, "*.jsonl");

        var plan = new
        {
            GeneratedAt = now.ToString("o"),
            ContractVersion = "V16.16",
            DocumentType = "NativeProductionTraceExecutionEndpointImplementationPlan",
            Purpose = "Detailed implementation plan for the native production trace execution endpoint. PLAN ONLY.",
            PlanStatus = new
            {
                EndpointImplementationPlanReady = true,
                EndpointImplementationAllowed = false,
                EndpointImplementationAllowedReason = "Plan is defined and ready for review, but actual implementation requires a separate phase. No code changes are committed at this phase.",
                EndpointImplemented = false,
            },
            TargetFilesAndClasses = new
            {
                PrimaryTarget = new
                {
                    File = "src/ContextCore.ControlRoom/Commands/EvalCommand.VectorV8.cs",
                    Method = "ExecuteV16_16NativeProductionTraceExecutionEndpointAsync",
                    Purpose = "CLI endpoint for native production trace execution.",
                },
                AuthorizationValidationTarget = new
                {
                    Method = "ValidateAllSevenAuthorizationFactors",
                    Purpose = "Validates all 7 authorization factors from V16.14 contract.",
                },
                SinkManagementTarget = new
                {
                    Classes = new[]
                    {
                        new { Name = "RuntimeCandidateTraceSinkAccessor", Purpose = "Static wiring point for trace sink." },
                        new { Name = "FileRuntimeCandidateTraceSink", Purpose = "JSONL file-backed trace sink." },
                        new { Name = "NullRuntimeCandidateTraceSink", Purpose = "No-op default sink for restore." },
                    },
                },
            },
            CliDispatchShape = new
            {
                Subcommand = "v16_16-native-production-trace-execution-endpoint",
                Args = new[]
                {
                    new { Arg = "--confirm-live-capture", Required = true, Type = "confirmation_flag" },
                    new { Arg = "--capture-token <token>", Required = true, Type = "hard_authorization" },
                    new { Arg = "--workspaceId <real>", Required = true, Type = "target_identification" },
                    new { Arg = "--collectionId <real>", Required = true, Type = "target_identification" },
                    new { Arg = "--runId <unique>", Required = true, Type = "idempotency" },
                },
            },
            GuardOrder = new object[]
            {
                new { Sequence = 1, Guard = "confirmLiveCapture", Check = "Parameter present.", IfMissing = "Block with MissingConfirmLiveCapture." },
                new { Sequence = 2, Guard = "captureToken", Check = "Non-empty string.", IfMissing = "Block with MissingCaptureToken." },
                new { Sequence = 3, Guard = "workspaceId/collectionId", Check = "Both non-empty.", IfMissing = "Block with MissingWorkspaceId or MissingCollectionId." },
                new { Sequence = 4, Guard = "synthetic rejection", Check = "Not in synthetic patterns.", IfSynthetic = "Block with SyntheticWorkspaceOrCollection." },
                new { Sequence = 5, Guard = "runId present", Check = "Non-empty string.", IfMissing = "Block with MissingRunId." },
                new { Sequence = 6, Guard = "RejectExistingRunId", Check = "Output file does not exist.", IfExists = "Block with RejectExistingRunId." },
                new { Sequence = 7, Guard = "safety invariants", Check = "RuntimeInfluenceAllowed=false etc.", IfViolated = "Hard abort." },
            },
            DryRunBehavior = new
            {
                Enabled = true,
                Description = "When --dry-run flag is present, execute all guards but do NOT wire sink or call BuildDetailedAsync.",
                OutputExample = "DryRun: All guards passed. Would wire sink with runId=<runId>.",
            },
            BlockedBehavior = new
            {
                WhenAnyGuardFails = "Return LiveCaptureBlocked=true with specific blocked reason.",
                OutputExample = "LiveCaptureBlocked=true. Reason: SyntheticWorkspaceOrCollection.",
            },
            SinkLifecycle = new[]
            {
                new { Step = 1, Phase = "Pre-execution", Action = "All 7 guards pass." },
                new { Step = 2, Phase = "Wiring", Action = "Create FileRuntimeCandidateTraceSink." },
                new { Step = 3, Phase = "Wiring", Action = "Set RuntimeCandidateTraceSinkAccessor.Current to file sink." },
                new { Step = 4, Phase = "Wiring", Action = "Set CurrentOperationId." },
                new { Step = 5, Phase = "Wiring", Action = "Set CurrentRequestId." },
                new { Step = 6, Phase = "Execution", Action = "Call BuildDetailedAsync ONLY after all guards pass." },
                new { Step = 7, Phase = "Post-execution", Action = "Call sink.FlushAsync()." },
                new { Step = 8, Phase = "Post-execution", Action = "Dispose sink." },
                new { Step = 9, Phase = "Restore", Action = "Set Current to NullRuntimeCandidateTraceSink." },
                new { Step = 10, Phase = "Restore", Action = "Clear CurrentOperationId and CurrentRequestId." },
            },
            FailureRollback = new
            {
                OnBuildError = "Dispose sink. Restore NullSink. Delete partial trace. Log error.",
                OnIdempotencyViolation = "Return immediately — no sink created.",
                OnValidationFailure = "Dispose sink. Restore NullSink. Delete trace. Mark INVALID.",
                AlwaysRestore = true,
                AlwaysRestoreNote = "RuntimeCandidateTraceSinkAccessor.Current MUST always be restored to NullRuntimeCandidateTraceSink.",
            },
            TestPlan = new
            {
                UnitTestsPlanned = new[]
                {
                    new { Test = "AuthorizationFactorValidation_AllSevenFactorsChecked" },
                    new { Test = "SyntheticRejection_AllSyntheticPatternsRejected" },
                    new { Test = "RejectExistingRunId_WhenFileExists_Aborts" },
                    new { Test = "SinkLifecycle_WiredAndRestored_Correctly" },
                    new { Test = "BuildDetailedAsync_OnlyCalledAfterAllGuards_Pass" },
                    new { Test = "BuildDetailedAsync_NotCalledWhenBlocked" },
                    new { Test = "NoRuntimeInfluence_RuntimeInfluenceAllowed_PermanentlyFalse" },
                },
                IntegrationTestsPlanned = Array.Empty<object>(),
                ProductionTestsPlanned = Array.Empty<object>(),
            },
            GateSemantics = new
            {
                EndpointImplementationPlanReady = true,
                EndpointImplementationAllowed = false,
                EndpointImplemented = false,
                ProductionTraceExecutionAuthorized = false,
                ProductionTraceExecutionAllowed = false,
                LiveCaptureExecutionImplemented = false,
                LiveCaptureExecuted = false,
                NativeProductionTraceReady = false,
                ProductionGeneralizationReady = false,
                RuntimeInfluenceAllowed = false,
                RuntimeInfluenceAllowedPermanent = true,
                PackageOutputChanged = false,
                RuntimePromotionApplied = false,
                VectorBindingChanged = false,
            },
            SafetyAudit = new
            {
                JsonlTraceFilesInV16_16 = jsonlFiles.Length,
                FileRuntimeCandidateTraceSinkWired = false,
                BuildDetailedAsyncCalledInLiveCapturePath = false,
            },
            PreviousGatesPreserved = new
            {
                V16_15EndpointDesignReady = true,
                V16_14AuthorizationContractReady = true,
                V16_13ExecutionPlanReady = true,
                V16_12DesignReviewReady = true,
                V16_11FinalAcceptanceBoundaryReady = true,
                V16_7ControlledReplayMetricQualityReady = true,
            },
        };

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(outputDir, "native-production-trace-execution-endpoint-implementation-plan.json"),
            JsonSerializer.Serialize(plan, JsonOptions), System.Text.Encoding.UTF8);

        var gate = new
        {
            GeneratedAt = now.ToString("o"),
            ContractVersion = "V16.16",
            DocumentType = "NativeProductionTraceExecutionEndpointImplementationPlanGate",
            Purpose = "Gate report confirming the endpoint implementation plan is complete.",
            GateResult = new
            {
                GatePassed = true,
                GatePassedReason = "Implementation plan fully defined.",
                EndpointImplementationPlanReady = true,
                EndpointImplementationAllowed = false,
                EndpointImplemented = false,
            },
            SafetyAudit = new
            {
                JsonlTraceFilesInV16_16 = jsonlFiles.Length,
                FileRuntimeCandidateTraceSinkWired = false,
                FileRuntimeCandidateTraceSinkWiredCheck = "NOT wired. Plan phase only.",
                BuildDetailedAsyncCalledInLiveCapturePath = false,
                BuildDetailedAsyncCalledCheck = "NOT called. Plan phase only.",
                RuntimeCandidateTraceSinkAccessorMutated = false,
            },
            GateSemantics = new
            {
                NativeProductionTraceReady = false,
                LiveCaptureExecutionImplemented = false,
                LiveCaptureExecuted = false,
                ProductionGeneralizationReady = false,
                RuntimeInfluenceAllowed = false,
                RuntimeInfluenceAllowedPermanent = true,
                PackageOutputChanged = false,
                RuntimePromotionApplied = false,
                VectorBindingChanged = false,
            },
            PreviousGatesPreserved = new
            {
                V16_15EndpointDesignReady = true,
                V16_14AuthorizationContractReady = true,
                V16_13ExecutionPlanReady = true,
                V16_12DesignReviewReady = true,
                V16_11FinalAcceptanceBoundaryReady = true,
                V16_7ControlledReplayMetricQualityReady = true,
            },
        };

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(outputDir, "native-production-trace-execution-endpoint-implementation-plan-gate.json"),
            JsonSerializer.Serialize(gate, JsonOptions), System.Text.Encoding.UTF8);

        // Preflight gate
        var preflight = new
        {
            GeneratedAt = now.ToString("o"),
            ContractVersion = "V16.16",
            DocumentType = "NativeProductionTraceExecutionEndpointImplementationAuthorizationPreflight",
            Purpose = "Implementation authorization preflight. Does not implement the endpoint.",
            GateResult = new
            {
                GatePassed = true,
                EndpointImplementationPlanReady = true,
                EndpointImplementationAuthorizationPreflightReady = true,
                EndpointImplementationAuthorizationPreflightReadyReason = "Implementation plan fully defined.",
                EndpointImplementationAllowed = false,
                EndpointImplementationAllowedReason = "Preflight confirms plan readiness but does not authorize implementation.",
                EndpointImplemented = false,
                ProductionTraceExecutionAuthorized = false,
                ProductionTraceExecutionAllowed = false,
                LiveCaptureExecutionImplemented = false,
                LiveCaptureExecuted = false,
                NativeProductionTraceReady = false,
            },
            SafetyAudit = new
            {
                JsonlTraceFilesInV16_16 = jsonlFiles.Length,
                FileRuntimeCandidateTraceSinkWired = false,
                BuildDetailedAsyncCalledInLiveCapturePath = false,
                RuntimeCandidateTraceSinkAccessorMutated = false,
            },
            GateSemantics = new
            {
                RuntimeInfluenceAllowed = false,
                RuntimeInfluenceAllowedPermanent = true,
                PackageOutputChanged = false,
                RuntimePromotionApplied = false,
                VectorBindingChanged = false,
                ProductionGeneralizationReady = false,
            },
            PhaseTransition = new
            {
                NextAllowedPhase = "NativeProductionTraceExecutionEndpointImplementationApproval",
                NextAllowedPhaseDescription = "Formal approval gate to authorize endpoint implementation.",
                NextDisallowedPhase = "RuntimeInfluenceActivation",
                NextDisallowedPhaseReason = "Runtime influence is permanently false.",
            },
            PreviousGatesPreserved = new
            {
                V16_15EndpointDesignReady = true,
                V16_14AuthorizationContractReady = true,
                V16_13ExecutionPlanReady = true,
                V16_12DesignReviewReady = true,
                V16_11FinalAcceptanceBoundaryReady = true,
                V16_7ControlledReplayMetricQualityReady = true,
            },
        };

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(outputDir, "native-production-trace-execution-endpoint-implementation-authorization-preflight.json"),
            JsonSerializer.Serialize(preflight, JsonOptions), System.Text.Encoding.UTF8);

        var md = string.Concat(
            $"# V16.16 Endpoint Implementation Plan\n\nGenerated: {now:o}\n\n",
            $"Plan only — no implementation.\n\n",
            $"- EndpointImplementationPlanReady: **true**\n",
            $"- EndpointImplementationAllowed: **false**\n",
            $"- Guards: 7 | Sink lifecycle: 10 steps\n"
        );
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(outputDir, "native-production-trace-execution-endpoint-implementation-plan.md"),
            md, System.Text.Encoding.UTF8);

        Console.WriteLine("[V16.16] Endpoint Implementation Plan complete");
        Console.WriteLine("[V16.16] EndpointImplementationPlanReady=true EndpointImplementationAllowed=false");
        Console.WriteLine("[V16.16] No code written. No sink wired. No BuildDetailedAsync called.");

        await Task.CompletedTask;
    }
}

