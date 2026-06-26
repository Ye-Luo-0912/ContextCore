using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class FormalRetrievalPromotionExternalApprovalQuarantineScanRunner
{
    public FormalRetrievalPromotionExternalApprovalQuarantineScanReport RunScan(
        bool evCandidateExists, bool regCandidateExists,
        string evStatus, string regStatus,
        bool evValid, bool regValid,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent,
        IReadOnlyList<string> candidateFiles,
        bool rtPassed, bool p15Passed,
        FormalRetrievalPromotionExternalApprovalQuarantineScanOptions? opt = null)
        => Build("scan", false, evCandidateExists, regCandidateExists, evStatus, regStatus, evValid, regValid,
            mainlineEvidencePresent, mainlineRegistryPresent, candidateFiles, rtPassed, p15Passed, opt);

    public FormalRetrievalPromotionExternalApprovalQuarantineScanReport RunGate(
        bool evCandidateExists, bool regCandidateExists,
        string evStatus, string regStatus,
        bool evValid, bool regValid,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent,
        IReadOnlyList<string> candidateFiles,
        bool rtPassed, bool p15Passed,
        FormalRetrievalPromotionExternalApprovalQuarantineScanOptions? opt = null)
        => Build("gate", true, evCandidateExists, regCandidateExists, evStatus, regStatus, evValid, regValid,
            mainlineEvidencePresent, mainlineRegistryPresent, candidateFiles, rtPassed, p15Passed, opt);

    private static FormalRetrievalPromotionExternalApprovalQuarantineScanReport Build(
        string stage, bool isGate,
        bool evCandidateExists, bool regCandidateExists,
        string evStatus, string regStatus,
        bool evValid, bool regValid,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent,
        IReadOnlyList<string> candidateFiles,
        bool rtPassed, bool p15Passed,
        FormalRetrievalPromotionExternalApprovalQuarantineScanOptions? opt)
    {
        opt ??= new FormalRetrievalPromotionExternalApprovalQuarantineScanOptions();
        var blocked = new List<string>();
        var diag = new List<string>();
        var now = DateTimeOffset.UtcNow;

        if (!opt.Enabled) blocked.Add("ScanDisabled");
        if (mainlineEvidencePresent) blocked.Add("MainlineEvidencePresent");
        if (mainlineRegistryPresent) blocked.Add("MainlineTrustRegistryPresent");
        if (!rtPassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15Passed) blocked.Add("P15GateNotPassed");

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var scanPassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && scanPassed;

        diag.Add($"stage={stage}");
        diag.Add($"evCandidate={evCandidateExists} status={evStatus} valid={evValid}");
        diag.Add($"regCandidate={regCandidateExists} status={regStatus} valid={regValid}");
        diag.Add($"mainlineEv={mainlineEvidencePresent} mainlineReg={mainlineRegistryPresent}");
        diag.Add($"scanPassed={scanPassed} gatePassed={gatePassed}");

        return new FormalRetrievalPromotionExternalApprovalQuarantineScanReport
        {
            OperationId = $"frp-quarantine-{stage}-{Guid.NewGuid():N}",
            CreatedAt = now,
            ScanPassed = scanPassed, GatePassed = gatePassed,
            Recommendation = scanPassed ? "QuarantineScanComplete" : "QuarantineScanBlocked",
            EvidenceCandidatePresent = evCandidateExists,
            TrustRegistryCandidatePresent = regCandidateExists,
            EvidenceStatus = evStatus, TrustRegistryStatus = regStatus,
            PromotionToMainlinePerformed = false,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            CandidateFiles = candidateFiles,
            FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false, FormalPackageWritten = false,
            PackageOutputChanged = false, PackingPolicyChanged = false, VectorStoreBindingChanged = false,
            GlobalDefaultOn = false, ConfigPatchWritten = false, RuntimeActivation = false,
            NoRuntimeMutationInvariant = true,
            BlockedReasons = distinctBlocked, Diagnostics = diag,
        };
    }

    public static string BuildMarkdown(string title, FormalRetrievalPromotionExternalApprovalQuarantineScanReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- ScanPassed: `{r.ScanPassed}` GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Evidence: present=`{r.EvidenceCandidatePresent}` status=`{r.EvidenceStatus}`");
        b.AppendLine($"- Registry: present=`{r.TrustRegistryCandidatePresent}` status=`{r.TrustRegistryStatus}`");
        b.AppendLine($"- PromotionToMainline: `{r.PromotionToMainlinePerformed}`");
        b.AppendLine();
        b.AppendLine("## Safety");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine($"- ConfigPatchWritten: `{r.ConfigPatchWritten}`");
        b.AppendLine();
        b.AppendLine("V8.8R quarantine scan。Candidate validation + mainline blocking。");
        return b.ToString();
    }
}

public sealed class FormalRetrievalPromotionExternalApprovalQuarantineScanOptions
{
    public bool Enabled { get; init; } = true;
}
