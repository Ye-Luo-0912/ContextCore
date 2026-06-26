using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class FormalRetrievalPromotionExternalApprovalSubmissionPackRunner
{
    public FormalRetrievalPromotionExternalApprovalSubmissionPackReport RunPack(
        bool evidenceSchemaExists, bool trustSchemaExists,
        bool evidenceTemplateExists, bool trustTemplateExists,
        bool mainlineIntakeBlocked, bool noRealEvidence, bool noRealRegistry,
        bool templatesContainPlaceholders,
        bool rtPassed, bool p15Passed,
        FormalRetrievalPromotionExternalApprovalSubmissionPackOptions? opt = null)
        => Build("pack", false, evidenceSchemaExists, trustSchemaExists, evidenceTemplateExists, trustTemplateExists,
            mainlineIntakeBlocked, noRealEvidence, noRealRegistry, templatesContainPlaceholders, rtPassed, p15Passed, opt);

    public FormalRetrievalPromotionExternalApprovalSubmissionPackReport RunGate(
        bool evidenceSchemaExists, bool trustSchemaExists,
        bool evidenceTemplateExists, bool trustTemplateExists,
        bool mainlineIntakeBlocked, bool noRealEvidence, bool noRealRegistry,
        bool templatesContainPlaceholders,
        bool rtPassed, bool p15Passed,
        FormalRetrievalPromotionExternalApprovalSubmissionPackOptions? opt = null)
        => Build("gate", true, evidenceSchemaExists, trustSchemaExists, evidenceTemplateExists, trustTemplateExists,
            mainlineIntakeBlocked, noRealEvidence, noRealRegistry, templatesContainPlaceholders, rtPassed, p15Passed, opt);

    private static FormalRetrievalPromotionExternalApprovalSubmissionPackReport Build(
        string stage, bool isGate,
        bool evidenceSchemaExists, bool trustSchemaExists,
        bool evidenceTemplateExists, bool trustTemplateExists,
        bool mainlineIntakeBlocked, bool noRealEvidence, bool noRealRegistry,
        bool templatesContainPlaceholders,
        bool rtPassed, bool p15Passed,
        FormalRetrievalPromotionExternalApprovalSubmissionPackOptions? opt)
    {
        opt ??= new FormalRetrievalPromotionExternalApprovalSubmissionPackOptions();
        var blocked = new List<string>();
        var diag = new List<string>();
        var now = DateTimeOffset.UtcNow;

        if (!opt.Enabled) blocked.Add("PackDisabled");
        if (!evidenceSchemaExists || !trustSchemaExists) blocked.Add("SchemasMissing");
        if (!evidenceTemplateExists || !trustTemplateExists) blocked.Add("TemplatesMissing");
        if (!mainlineIntakeBlocked) blocked.Add("MainlineIntakeNotBlocked");
        if (!noRealEvidence) blocked.Add("RealApprovalEvidencePresent");
        if (!noRealRegistry) blocked.Add("RealTrustRegistryPresent");
        if (!templatesContainPlaceholders) blocked.Add("TemplatesMissingPlaceholders");
        if (!rtPassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15Passed) blocked.Add("P15GateNotPassed");

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var packPassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && packPassed;

        diag.Add($"stage={stage}");
        diag.Add($"schemasComplete={evidenceSchemaExists && trustSchemaExists}");
        diag.Add($"templatesComplete={evidenceTemplateExists && trustTemplateExists}");
        diag.Add($"templatesContainPlaceholders={templatesContainPlaceholders}");
        diag.Add($"mainlineIntakeBlocked={mainlineIntakeBlocked}");
        diag.Add($"noRealEvidence={noRealEvidence}");
        diag.Add($"noRealRegistry={noRealRegistry}");
        diag.Add($"packPassed={packPassed} gatePassed={gatePassed}");
        if (!isGate) diag.Add("GatePassed=false is expected for non-gate artifact");

        return new FormalRetrievalPromotionExternalApprovalSubmissionPackReport
        {
            OperationId = $"frp-pack-{stage}-{Guid.NewGuid():N}",
            CreatedAt = now,
            PackPassed = packPassed,
            GatePassed = gatePassed,
            Recommendation = packPassed
                ? FormalRetrievalPromotionExternalApprovalSubmissionPackRecommendations.SubmissionPackReady
                : FormalRetrievalPromotionExternalApprovalSubmissionPackRecommendations.BlockedBySchemasMissing,
            NextAllowedPhase = packPassed ? "SubmissionPackReady" : "KeepPreviewOnly",
            EvidenceSchemaPresent = evidenceSchemaExists,
            TrustRegistrySchemaPresent = trustSchemaExists,
            EvidenceTemplatePresent = evidenceTemplateExists,
            TrustRegistryTemplatePresent = trustTemplateExists,
            TemplatesContainPlaceholders = templatesContainPlaceholders,
            MainlineIntakeStillBlocked = mainlineIntakeBlocked,
            NoRealEvidencePresent = noRealEvidence,
            NoRealTrustRegistryPresent = noRealRegistry,
            P15GatePassed = p15Passed,
            RuntimeChangeGatePassed = rtPassed,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            VectorStoreBindingChanged = false,
            GlobalDefaultOn = false,
            ConfigPatchWritten = false,
            RuntimeActivation = false,
            NoRuntimeMutationInvariant = true,
            BlockedReasons = distinctBlocked,
            Diagnostics = diag,
        };
    }

    public static string BuildMarkdown(string title, FormalRetrievalPromotionExternalApprovalSubmissionPackReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- PackPassed: `{r.PackPassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- NextAllowedPhase: `{r.NextAllowedPhase}`");
        b.AppendLine();
        b.AppendLine("## Submission Pack");
        b.AppendLine($"- Schemas present: `{r.EvidenceSchemaPresent && r.TrustRegistrySchemaPresent}`");
        b.AppendLine($"- Templates present: `{r.EvidenceTemplatePresent && r.TrustRegistryTemplatePresent}`");
        b.AppendLine($"- TemplatesContainPlaceholders: `{r.TemplatesContainPlaceholders}`");
        b.AppendLine($"- Mainline blocked: `{r.MainlineIntakeStillBlocked}`");
        b.AppendLine($"- NoRealEvidence: `{r.NoRealEvidencePresent}`");
        b.AppendLine($"- NoRealRegistry: `{r.NoRealTrustRegistryPresent}`");
        b.AppendLine();
        b.AppendLine("## Safety");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine();
        b.AppendLine("V8.5R external approval submission pack。Real placeholder validation + no-real-input guard。");
        return b.ToString();
    }
}
