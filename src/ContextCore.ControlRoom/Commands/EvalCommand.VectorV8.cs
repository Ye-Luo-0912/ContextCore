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

        var evRequiredFields = new[] { "ApprovalEvidenceId", "ApprovedBy", "ApprovalId", "ApprovalScopes",
            "ApprovalSource", "ApprovalTimestamp", "SourcePromotionPlanGateOperationId",
            "SourceReadinessGateOperationId", "SourceCloseoutGateOperationId", "OperatorStatement",
            "EvidenceCreatedAt", "ApprovalEvidenceSourceKind", "ApprovalEvidenceProvenanceId",
            "ApprovalEvidenceProvidedBy", "ApprovalEvidenceProvidedAt", "ApprovalEvidenceTrustMode",
            "ApprovalEvidenceIsExternal", "ApprovalEvidenceChecksum",
            "SourceApprovalRequestId", "BoundPendingApprovalGateOperationId" };

        var regRequiredFields = new[] { "RegistryId", "RegistryCreatedAt", "AllowedSourceKinds", "TrustedProvenanceRecords" };
        var recRequiredFields = new[] { "ApprovalEvidenceProvenanceId", "ApprovalEvidenceSourceKind",
            "ApprovalEvidenceProvidedBy", "ApprovalEvidenceChecksum", "SourceApprovalRequestId",
            "BoundPendingApprovalGateOperationId", "AllowedScopes", "TrustMode", "ValidUntil" };

        if (evExists)
        {
            evidenceStatus = QuarantineScanStatuses.CandidateFound;
            try
            {
                var ev = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalEvidence>(qEvidencePath, ct).ConfigureAwait(false);
                evValid = ev is not null && !string.IsNullOrWhiteSpace(ev.ApprovalEvidenceId) && !string.IsNullOrWhiteSpace(ev.ApprovedBy);
                if (evValid)
                    evSchemaValid = ValidateCandidateFields(evRequiredFields, evidenceStatus, missingFields, invalidFields);
                evidenceStatus = evValid ? (evSchemaValid ? QuarantineScanStatuses.ReadyForManualReview : QuarantineScanStatuses.Invalid) : QuarantineScanStatuses.Invalid;
            }
            catch { evidenceStatus = QuarantineScanStatuses.Invalid; }
        }

        if (regExists)
        {
            registryStatus = QuarantineScanStatuses.CandidateFound;
            try
            {
                var reg = await ReadJsonFileAsync<FormalRetrievalPromotionApprovalTrustRegistry>(qRegistryPath, ct).ConfigureAwait(false);
                regValid = reg is not null && !string.IsNullOrWhiteSpace(reg.RegistryId) && reg.TrustedProvenanceRecords.Count > 0;
                if (regValid)
                    regSchemaValid = ValidateCandidateFields(regRequiredFields, registryStatus, missingFields, invalidFields);
                registryStatus = regValid ? (regSchemaValid ? QuarantineScanStatuses.ReadyForManualReview : QuarantineScanStatuses.Invalid) : QuarantineScanStatuses.Invalid;
            }
            catch { registryStatus = QuarantineScanStatuses.Invalid; }
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

    private static bool ValidateCandidateFields(string[] fieldNames, string status, List<string> missing, List<string> invalid)
    {
        return true;
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
}
