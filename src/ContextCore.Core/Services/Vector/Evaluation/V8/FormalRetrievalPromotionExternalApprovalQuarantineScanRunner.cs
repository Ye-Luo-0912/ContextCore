using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class FormalRetrievalPromotionExternalApprovalQuarantineScanRunner
{
    public FormalRetrievalPromotionExternalApprovalQuarantineScanReport RunScan(
        bool evidenceCandidateExists, bool registryCandidateExists,
        string evidenceStatus, string registryStatus,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent,
        IReadOnlyList<string> candidateFiles,
        bool rtPassed, bool p15Passed,
        FormalRetrievalPromotionExternalApprovalQuarantineScanOptions? opt = null)
        => Build("scan", false, evidenceCandidateExists, registryCandidateExists, evidenceStatus, registryStatus,
            mainlineEvidencePresent, mainlineRegistryPresent, candidateFiles, rtPassed, p15Passed, opt);

    public FormalRetrievalPromotionExternalApprovalQuarantineScanReport RunGate(
        bool evidenceCandidateExists, bool registryCandidateExists,
        string evidenceStatus, string registryStatus,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent,
        IReadOnlyList<string> candidateFiles,
        bool rtPassed, bool p15Passed,
        FormalRetrievalPromotionExternalApprovalQuarantineScanOptions? opt = null)
        => Build("gate", true, evidenceCandidateExists, registryCandidateExists, evidenceStatus, registryStatus,
            mainlineEvidencePresent, mainlineRegistryPresent, candidateFiles, rtPassed, p15Passed, opt);

    private static FormalRetrievalPromotionExternalApprovalQuarantineScanReport Build(
        string stage, bool isGate,
        bool evidenceCandidateExists, bool registryCandidateExists,
        string evidenceStatus, string registryStatus,
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
        if (!rtPassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15Passed) blocked.Add("P15GateNotPassed");

        diag.Add($"stage={stage}");
        diag.Add($"evidenceCandidate={evidenceCandidateExists}");
        diag.Add($"registryCandidate={registryCandidateExists}");
        diag.Add($"evidenceStatus={evidenceStatus}");
        diag.Add($"registryStatus={registryStatus}");
        diag.Add($"mainlineEvidence={mainlineEvidencePresent}");
        diag.Add($"mainlineRegistry={mainlineRegistryPresent}");

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var scanPassed = distinctBlocked.Length == 0;
        var gatePassed = isGate && scanPassed;

        return new FormalRetrievalPromotionExternalApprovalQuarantineScanReport
        {
            OperationId = $"frp-quarantine-{stage}-{Guid.NewGuid():N}",
            CreatedAt = now,
            ScanPassed = scanPassed,
            GatePassed = gatePassed,
            Recommendation = scanPassed ? "QuarantineScanComplete" : "QuarantineScanBlocked",
            EvidenceCandidatePresent = evidenceCandidateExists,
            TrustRegistryCandidatePresent = registryCandidateExists,
            EvidenceStatus = evidenceStatus,
            TrustRegistryStatus = registryStatus,
            PromotionToMainlinePerformed = false,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            CandidateFiles = candidateFiles,
            FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false,
            FormalPackageWritten = false, GlobalDefaultOn = false,
            NoRuntimeMutationInvariant = true,
            BlockedReasons = distinctBlocked,
            Diagnostics = diag,
        };
    }

    public static string BuildMarkdown(string title, FormalRetrievalPromotionExternalApprovalQuarantineScanReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}` 操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- ScanPassed: `{r.ScanPassed}` GatePassed: `{r.GatePassed}`");
        b.AppendLine();
        b.AppendLine("## Quarantine Status");
        b.AppendLine($"- EvidenceCandidate: `{r.EvidenceCandidatePresent}` status=`{r.EvidenceStatus}`");
        b.AppendLine($"- TrustRegistryCandidate: `{r.TrustRegistryCandidatePresent}` status=`{r.TrustRegistryStatus}`");
        b.AppendLine($"- PromotionToMainline: `{r.PromotionToMainlinePerformed}`");
        b.AppendLine($"- MainlineEvidence: `{r.MainlineEvidencePresent}` MainlineRegistry: `{r.MainlineTrustRegistryPresent}`");
        b.AppendLine();
        b.AppendLine("## Safety");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine();
        b.AppendLine("V8.8 quarantine scan。不提升到主线，不启用 formal retrieval。");
        return b.ToString();
    }
}

public sealed class FormalRetrievalPromotionExternalApprovalQuarantineScanOptions
{
    public bool Enabled { get; init; } = true;
}
