using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 读取 decision trace 并生成审计报告（V17.0）。
/// 校验非激活契约（所有 Risk 标志位恒为 false）和投影 ID 保留性。
/// 报告输出为 JSON 和 Markdown 双格式。
/// </summary>
public sealed class ContextDecisionAuditRunner
{
    private readonly IDecisionTraceStore _store;

    public ContextDecisionAuditRunner(IDecisionTraceStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>执行审计并返回报告对象。</summary>
    public async Task<ContextDecisionAuditReport> RunAsync(
        string workspaceId,
        string collectionId,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        var records = await _store.QueryRecentAsync(workspaceId, collectionId, take, cancellationToken)
            .ConfigureAwait(false);

        var samples = new List<ContextDecisionAuditSample>();
        var violations = new List<string>();
        var totalSelected = 0;
        var totalDropped = 0;
        var packageCount = 0;
        var retrievalCount = 0;
        var allPreserveIds = true;

        foreach (var record in records)
        {
            var recordViolations = AuditNonActivationContract(record.Risk);
            var preserveIds = AuditIdPreservation(record);

            samples.Add(new ContextDecisionAuditSample
            {
                DecisionId = record.DecisionId,
                Source = record.Source.ToString(),
                WorkspaceId = record.WorkspaceId,
                CollectionId = record.CollectionId,
                SelectedCount = record.Outcome.SelectedCount,
                DroppedCount = record.Outcome.DroppedCount,
                EstimatedTokens = record.Outcome.EstimatedTokens,
                NonActivationContractHolds = recordViolations.Count == 0,
                ContractViolations = recordViolations
            });

            violations.AddRange(recordViolations);
            totalSelected += record.Outcome.SelectedCount;
            totalDropped += record.Outcome.DroppedCount;

            if (record.Source == ContextDecisionSource.Package) packageCount++;
            else retrievalCount++;

            if (!preserveIds) allPreserveIds = false;
        }

        var contractHolds = violations.Count == 0;

        return new ContextDecisionAuditReport
        {
            OperationId = Guid.NewGuid().ToString("N"),
            GeneratedAt = DateTimeOffset.UtcNow,
            TraceCount = records.Count,
            PackageDecisionCount = packageCount,
            RetrievalDecisionCount = retrievalCount,
            TotalSelectedCount = totalSelected,
            TotalDroppedCount = totalDropped,
            NonActivationContractHolds = contractHolds,
            ContractViolations = violations.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ProjectionPreservesIds = allPreserveIds,
            Samples = samples,
            PolicyVersion = ContextDecisionPolicyVersions.V17_0
        };
    }

    /// <summary>执行审计并把报告写入指定目录（JSON + Markdown）。</summary>
    public async Task<ContextDecisionAuditReport> RunAndWriteAsync(
        string workspaceId,
        string collectionId,
        string outputDirectory,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        var report = await RunAsync(workspaceId, collectionId, take, cancellationToken)
            .ConfigureAwait(false);

        Directory.CreateDirectory(outputDirectory);
        var basePath = Path.Combine(outputDirectory, $"decision-audit-report-{report.OperationId[..8]}");

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        });
        await File.WriteAllTextAsync(basePath + ".json", json, cancellationToken).ConfigureAwait(false);

        var md = RenderMarkdown(report);
        await File.WriteAllTextAsync(basePath + ".md", md, cancellationToken).ConfigureAwait(false);

        return report;
    }

    /// <summary>校验非激活契约：所有 Risk 标志位必须为 false。</summary>
    public static IReadOnlyList<string> AuditNonActivationContract(ContextDecisionRisk risk)
    {
        ArgumentNullException.ThrowIfNull(risk);
        var violations = new List<string>(9);

        if (risk.FormalRetrievalAllowed) violations.Add(nameof(risk.FormalRetrievalAllowed));
        if (risk.RuntimeSwitchAllowed) violations.Add(nameof(risk.RuntimeSwitchAllowed));
        if (risk.FormalVectorStoreBinding) violations.Add(nameof(risk.FormalVectorStoreBinding));
        if (risk.FormalPackageWrite) violations.Add(nameof(risk.FormalPackageWrite));
        if (risk.PackageOutputChanged) violations.Add(nameof(risk.PackageOutputChanged));
        if (risk.PackingPolicyChanged) violations.Add(nameof(risk.PackingPolicyChanged));
        if (risk.GraphApplyFormalChanged) violations.Add(nameof(risk.GraphApplyFormalChanged));
        if (risk.LearningPolicyApplied) violations.Add(nameof(risk.LearningPolicyApplied));
        if (risk.ModelTrainingStarted) violations.Add(nameof(risk.ModelTrainingStarted));

        return violations;
    }

    /// <summary>校验投影 ID 保留性：selected/dropped 的 ItemId 不应为空。</summary>
    public static bool AuditIdPreservation(ContextDecisionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var selectedCount = record.Outcome.SelectedCount;
        var droppedCount = record.Outcome.DroppedCount;

        var actualSelected = record.Candidates.Count(c => c.Outcome == ContextDecisionCandidateOutcome.Selected);
        var actualDropped = record.Candidates.Count(c => c.Outcome == ContextDecisionCandidateOutcome.Dropped);

        if (actualSelected != selectedCount || actualDropped != droppedCount)
        {
            return false;
        }

        return record.Candidates.All(c => !string.IsNullOrWhiteSpace(c.ItemId));
    }

    private static string RenderMarkdown(ContextDecisionAuditReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Context Decision Audit Report (V17.0)");
        sb.AppendLine();
        sb.AppendLine($"- **OperationId**: `{report.OperationId}`");
        sb.AppendLine($"- **GeneratedAt**: {report.GeneratedAt:O}");
        sb.AppendLine($"- **PolicyVersion**: `{report.PolicyVersion}`");
        sb.AppendLine($"- **TraceCount**: {report.TraceCount}");
        sb.AppendLine($"- **PackageDecisionCount**: {report.PackageDecisionCount}");
        sb.AppendLine($"- **RetrievalDecisionCount**: {report.RetrievalDecisionCount}");
        sb.AppendLine($"- **TotalSelectedCount**: {report.TotalSelectedCount}");
        sb.AppendLine($"- **TotalDroppedCount**: {report.TotalDroppedCount}");
        sb.AppendLine();
        sb.AppendLine("## Non-Activation Contract");
        sb.AppendLine();
        sb.AppendLine($"- **NonActivationContractHolds**: `{report.NonActivationContractHolds}`");
        if (report.ContractViolations.Count > 0)
        {
            sb.AppendLine($"- **ContractViolations**: {string.Join(", ", report.ContractViolations)}");
        }
        else
        {
            sb.AppendLine("- **ContractViolations**: (none)");
        }
        sb.AppendLine($"- **ProjectionPreservesIds**: `{report.ProjectionPreservesIds}`");
        sb.AppendLine();
        sb.AppendLine("## Samples");
        sb.AppendLine();
        sb.AppendLine("| DecisionId | Source | Selected | Dropped | Tokens | ContractHolds |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var sample in report.Samples)
        {
            sb.AppendLine($"| {sample.DecisionId[..Math.Min(12, sample.DecisionId.Length)]} | {sample.Source} | {sample.SelectedCount} | {sample.DroppedCount} | {sample.EstimatedTokens} | {sample.NonActivationContractHolds} |");
        }

        return sb.ToString();
    }
}
