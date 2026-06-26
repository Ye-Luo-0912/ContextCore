using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class FormalRetrievalPromotionExternalApprovalQuarantineNegativeMatrixRunner
{
    public FormalRetrievalPromotionExternalApprovalQuarantineNegativeMatrixReport Run(
        bool rtPassed, bool p15Passed,
        FormalRetrievalPromotionExternalApprovalQuarantineMatrixOptions? opt = null)
    {
        opt ??= new FormalRetrievalPromotionExternalApprovalQuarantineMatrixOptions();
        var now = DateTimeOffset.UtcNow;
        var isGate = opt.IsGate;
        var scanner = new FormalRetrievalPromotionExternalApprovalQuarantineScanRunner();
        var cases = new List<FormalRetrievalPromotionExternalApprovalQuarantineNegativeCase>();

        foreach (var scenario in NegativeScenarios)
        {
            var report = scanner.RunGate(
                scenario.EvExists, scenario.RegExists,
                scenario.EvStatus, scenario.RegStatus,
                scenario.EvValid, scenario.RegValid,
                scenario.EvSchemaValid, scenario.RegSchemaValid,
                scenario.MissingFields, scenario.InvalidFields,
                false, false, scenario.CandidateFiles,
                rtPassed, p15Passed);

            var failedAsExpected = !report.ScanPassed && report.BlockedReasons.Any(r =>
                string.Equals(r, scenario.ExpectedReason, StringComparison.OrdinalIgnoreCase));

            cases.Add(new FormalRetrievalPromotionExternalApprovalQuarantineNegativeCase
            {
                CaseName = scenario.CaseName,
                ExpectedBlockedReason = scenario.ExpectedReason,
                ActualBlockedReasons = report.BlockedReasons,
                FailedAsExpected = failedAsExpected,
            });
        }

        var passedCases = cases.Count(c => c.FailedAsExpected);
        var failedCases = cases.Count - passedCases;
        var matrixPassed = failedCases == 0;
        var gatePassed = isGate && matrixPassed;
        var blocked = new List<string>();
        if (!matrixPassed) blocked.Add("QuarantineNegativeMatrixFailed");

        return new FormalRetrievalPromotionExternalApprovalQuarantineNegativeMatrixReport
        {
            OperationId = $"frp-q-neg-matrix-{Guid.NewGuid():N}",
            CreatedAt = now,
            MatrixPassed = matrixPassed, GatePassed = gatePassed,
            TotalCases = cases.Count, PassedCases = passedCases, FailedCases = failedCases,
            Cases = cases,
            FormalRetrievalAllowed = false, NoRuntimeMutationInvariant = true,
            BlockedReasons = blocked,
        };
    }

    private static readonly (string CaseName, string ExpectedReason, bool EvExists, bool RegExists,
        string EvStatus, string RegStatus, bool EvValid, bool RegValid,
        bool EvSchemaValid, bool RegSchemaValid,
        IReadOnlyList<string> MissingFields, IReadOnlyList<string> InvalidFields,
        IReadOnlyList<string> CandidateFiles)[] NegativeScenarios =
    [
        ("EvidenceMissingField", "EvidenceCandidateSchemaInvalid",
            true, false, "Invalid", "Missing", true, false, false, false,
            new[]{"ApprovalId"}, new string[]{}, new[]{"q/evidence.candidate.json"}),
        ("EvidenceEmptyScopes", "EvidenceCandidateSchemaInvalid",
            true, false, "Invalid", "Missing", true, false, false, false,
            new string[]{}, new[]{"ApprovalScopes"}, new[]{"q/evidence.candidate.json"}),
        ("EvidenceDefaultTime", "EvidenceCandidateSchemaInvalid",
            true, false, "Invalid", "Missing", true, false, false, false,
            new string[]{}, new[]{"ApprovalTimestamp"}, new[]{"q/evidence.candidate.json"}),
        ("RegistryMissingRecords", "TrustRegistryCandidateSchemaInvalid",
            false, true, "Missing", "Invalid", false, true, false, false,
            new[]{"TrustedProvenanceRecords"}, new string[]{}, new[]{"q/registry.candidate.json"}),
        ("RegistryEmptySourceKinds", "TrustRegistryCandidateSchemaInvalid",
            false, true, "Missing", "Invalid", false, true, false, false,
            new string[]{}, new[]{"AllowedSourceKinds"}, new[]{"q/registry.candidate.json"}),
        ("RecordMissingChecksum", "TrustRegistryCandidateSchemaInvalid",
            false, true, "Missing", "Invalid", false, true, false, false,
            new[]{"TrustedProvenanceRecords[0].ApprovalEvidenceChecksum"}, new string[]{}, new[]{"q/registry.candidate.json"}),
        ("RecordMissingProvidedBy", "TrustRegistryCandidateSchemaInvalid",
            false, true, "Missing", "Invalid", false, true, false, false,
            new[]{"TrustedProvenanceRecords[1].ApprovalEvidenceProvidedBy"}, new string[]{}, new[]{"q/registry.candidate.json"}),
        ("RecordDefaultValidUntil", "TrustRegistryCandidateSchemaInvalid",
            false, true, "Missing", "Invalid", false, true, false, false,
            new string[]{}, new[]{"TrustedProvenanceRecords[1].ValidUntil"}, new[]{"q/registry.candidate.json"}),
    ];

    public static string BuildMarkdown(string title, FormalRetrievalPromotionExternalApprovalQuarantineNegativeMatrixReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- MatrixPassed: `{r.MatrixPassed}` GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Total: `{r.TotalCases}` Passed: `{r.PassedCases}` Failed: `{r.FailedCases}`");
        b.AppendLine();
        b.AppendLine("## Negative Cases");
        foreach (var c in r.Cases)
            b.AppendLine($"- `{c.CaseName}`: expected=`{c.ExpectedBlockedReason}` failedAsExpected=`{c.FailedAsExpected}` actual=`{string.Join(", ", c.ActualBlockedReasons)}`");
        b.AppendLine();
        b.AppendLine("V8.9 quarantine negative matrix。");
        return b.ToString();
    }
}

public sealed class FormalRetrievalPromotionExternalApprovalQuarantineNegativeCase
{
    public string CaseName { get; init; } = "";
    public string ExpectedBlockedReason { get; init; } = "";
    public IReadOnlyList<string> ActualBlockedReasons { get; init; } = Array.Empty<string>();
    public bool FailedAsExpected { get; init; }
}

public sealed class FormalRetrievalPromotionExternalApprovalQuarantineNegativeMatrixReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool MatrixPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public IReadOnlyList<FormalRetrievalPromotionExternalApprovalQuarantineNegativeCase> Cases { get; init; } = Array.Empty<FormalRetrievalPromotionExternalApprovalQuarantineNegativeCase>();
    public bool FormalRetrievalAllowed { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed class FormalRetrievalPromotionExternalApprovalQuarantineMatrixOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
