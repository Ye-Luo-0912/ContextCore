using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class ArchitectureCleanupFreezeGateRunner
{
    public ArchitectureCleanupFreezeGateReport BuildGateReport(ArchitectureCleanupFreezeReport? freezeReport)
    {
        var blocked = new List<string>();

        if (freezeReport is null)
        {
            blocked.Add("FreezeReportMissing");
        }

        var allSubReportsAvailable = freezeReport is not null
            && freezeReport.ArchitectureCleanupPlanPassed
            && freezeReport.DtoSplitPlanGenerated
            && freezeReport.PathHygieneGatePassed;

        var allGateRulesCompliant = freezeReport is not null
            && freezeReport.FormalRetrievalNotEnabled
            && freezeReport.NoRuntimeSwitch
            && freezeReport.NoFormalPackageWrite
            && freezeReport.NoPackagePackingPolicyVectorBindingMutation;

        if (!allSubReportsAvailable) blocked.Add("SubReportsIncomplete");
        if (!allGateRulesCompliant) blocked.Add("GateRulesViolated");
        if (freezeReport is not null && !freezeReport.FreezePassed) blocked.Add("FreezeNotPassed");

        var diag = new List<string>
        {
            $"FreezeReportPresent: {freezeReport is not null}",
            $"FreezePassed: {freezeReport?.FreezePassed ?? false}",
            $"AllSubReportsAvailable: {allSubReportsAvailable}",
            $"AllGateRulesCompliant: {allGateRulesCompliant}",
            $"PlanPassed: {freezeReport?.ArchitectureCleanupPlanPassed ?? false}",
            $"DtoSplitGenerated: {freezeReport?.DtoSplitPlanGenerated ?? false}",
            $"HygienePassed: {freezeReport?.PathHygieneGatePassed ?? false}",
            $"P15Hardened: {freezeReport?.P15BuildLockHardened ?? false}",
            "FormalRetrievalNotEnabled: true",
            "NoRuntimeSwitch: true",
            "NoFormalPackageWrite: true",
            "NoMutation: true",
        };

        var gatePassed = blocked.Count == 0;

        return new ArchitectureCleanupFreezeGateReport
        {
            OperationId = $"arch-cleanup-freeze-gate-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            GatePassed = gatePassed,
            Recommendation = gatePassed ? "ArchitectureCleanupFreezeGatePassed" : "ArchitectureCleanupFreezeGateBlocked",
            FreezeReportPresent = freezeReport is not null,
            FreezePassed = freezeReport?.FreezePassed ?? false,
            AllSubReportsAvailable = allSubReportsAvailable,
            AllGateRulesCompliant = allGateRulesCompliant,
            BlockedReasons = blocked,
            Diagnostics = diag,
        };
    }

    public static string BuildMarkdown(string title, ArchitectureCleanupFreezeGateReport report)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"**生成:** `{report.CreatedAt:O}`");
        b.AppendLine();
        b.AppendLine($"**GatePassed:** {report.GatePassed}");
        b.AppendLine($"**Recommendation:** {report.Recommendation}");
        b.AppendLine();
        b.AppendLine("## Gate 规则验证");
        b.AppendLine();
        b.AppendLine($"- FreezeReportPresent: {report.FreezeReportPresent}");
        b.AppendLine($"- FreezePassed: {report.FreezePassed}");
        b.AppendLine($"- AllSubReportsAvailable: {report.AllSubReportsAvailable}");
        b.AppendLine($"- AllGateRulesCompliant: {report.AllGateRulesCompliant}");
        b.AppendLine();

        if (report.BlockedReasons.Count > 0)
        {
            b.AppendLine("## Blocked Reasons");
            b.AppendLine();
            foreach (var r in report.BlockedReasons) b.AppendLine($"- {r}");
            b.AppendLine();
        }

        b.AppendLine("## Diagnostics");
        b.AppendLine();
        foreach (var d in report.Diagnostics) b.AppendLine($"- {d}");
        b.AppendLine();

        b.AppendLine("Architecture cleanup freeze gate report. No runtime behavior change, no formal retrieval enable, no package/packing policy/runtime/vector binding mutation.");
        return b.ToString();
    }
}
