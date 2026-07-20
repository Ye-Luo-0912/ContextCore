using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 读取 decision trace 并生成审计报告（V17.0）。
/// 校验非激活契约（所有 Risk 标志位恒为 false）、投影 ID 保留性，以及证据完整性（当接入证据提供者时）。
/// 报告输出为 JSON 和 Markdown 双格式。
/// </summary>
public sealed class ContextDecisionAuditRunner
{
    private readonly IDecisionTraceStore _store;
    private readonly IDecisionEvidenceProvider? _evidenceProvider;

    public ContextDecisionAuditRunner(IDecisionTraceStore store)
        : this(store, evidenceProvider: null)
    {
    }

    /// <param name="evidenceProvider">证据提供者；为 null 时跳过证据完整性审计。</param>
    public ContextDecisionAuditRunner(IDecisionTraceStore store, IDecisionEvidenceProvider? evidenceProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _evidenceProvider = evidenceProvider;
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
        var evidenceIncompleteIds = new List<string>();
        var totalSelected = 0;
        var totalDropped = 0;
        var totalEvidenceResolved = 0;
        var totalEvidenceMissing = 0;
        var packageCount = 0;
        var retrievalCount = 0;
        var allPreserveIds = true;
        // 聚合状态：无 provider → NotConfigured；任一 trace Failed → Failed；
        // 任一 trace Incomplete → Incomplete；全部 Complete → Complete。
        var aggregateStatus = _evidenceProvider is null
            ? EvidenceAuditStatus.NotConfigured
            : EvidenceAuditStatus.Complete;

        foreach (var record in records)
        {
            var recordViolations = AuditNonActivationContract(record.Risk);
            var preserveIds = AuditIdPreservation(record);

            var evidenceResolved = 0;
            var evidenceMissing = 0;
            var evidenceComplete = false;
            var sampleStatus = EvidenceAuditStatus.NotConfigured;

            if (_evidenceProvider is not null)
            {
                sampleStatus = EvidenceAuditStatus.Complete;
                try
                {
                    var evidenceResult = await _evidenceProvider.ResolveEvidenceAsync(record, cancellationToken)
                        .ConfigureAwait(false);
                    evidenceResolved = evidenceResult.Evidence.Count;
                    evidenceMissing = evidenceResult.MissingItemIds.Count;
                    evidenceComplete = evidenceResult.IsComplete;

                    if (!evidenceComplete)
                    {
                        sampleStatus = EvidenceAuditStatus.Incomplete;
                        evidenceIncompleteIds.Add(record.DecisionId);
                    }

                    totalEvidenceResolved += evidenceResolved;
                    totalEvidenceMissing += evidenceMissing;
                }
                catch (Exception)
                {
                    sampleStatus = EvidenceAuditStatus.Failed;
                    evidenceComplete = false;
                }
            }

            // 聚合状态收敛：Failed 优先于 Incomplete 优先于 Complete
            aggregateStatus = CombineStatus(aggregateStatus, sampleStatus);

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
                ContractViolations = recordViolations,
                EvidenceComplete = evidenceComplete,
                EvidenceStatus = sampleStatus,
                EvidenceResolvedCount = evidenceResolved,
                EvidenceMissingCount = evidenceMissing
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
            EvidenceComplete = aggregateStatus == EvidenceAuditStatus.Complete,
            EvidenceStatus = aggregateStatus,
            EvidenceIncompleteDecisionIds = evidenceIncompleteIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            EvidenceResolvedCount = totalEvidenceResolved,
            EvidenceMissingCount = totalEvidenceMissing,
            Samples = samples,
            PolicyVersion = ContextDecisionPolicyVersions.DecisionSchemaV2_0
        };
    }

    /// <summary>聚合状态收敛：Failed &gt; Incomplete &gt; NotConfigured &gt; Complete。</summary>
    private static EvidenceAuditStatus CombineStatus(EvidenceAuditStatus current, EvidenceAuditStatus sample)
    {
        return (current, sample) switch
        {
            (_, EvidenceAuditStatus.Failed) => EvidenceAuditStatus.Failed,
            (EvidenceAuditStatus.Failed, _) => EvidenceAuditStatus.Failed,
            (_, EvidenceAuditStatus.Incomplete) => EvidenceAuditStatus.Incomplete,
            (EvidenceAuditStatus.Incomplete, _) => EvidenceAuditStatus.Incomplete,
            (_, EvidenceAuditStatus.NotConfigured) when current == EvidenceAuditStatus.Complete => EvidenceAuditStatus.NotConfigured,
            _ => current
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
        sb.AppendLine("## Evidence Completeness");
        sb.AppendLine();
        sb.AppendLine($"- **EvidenceComplete**: `{report.EvidenceComplete}`");
        sb.AppendLine($"- **EvidenceStatus**: `{report.EvidenceStatus}`");
        sb.AppendLine($"- **EvidenceResolvedCount**: {report.EvidenceResolvedCount}");
        sb.AppendLine($"- **EvidenceMissingCount**: {report.EvidenceMissingCount}");
        if (report.EvidenceIncompleteDecisionIds.Count > 0)
        {
            sb.AppendLine($"- **EvidenceIncompleteDecisionIds**: {string.Join(", ", report.EvidenceIncompleteDecisionIds.Take(20))}");
        }
        else
        {
            sb.AppendLine("- **EvidenceIncompleteDecisionIds**: (none)");
        }
        sb.AppendLine();
        sb.AppendLine("## Samples");
        sb.AppendLine();
        sb.AppendLine("| DecisionId | Source | Selected | Dropped | Tokens | ContractHolds | EvidenceComplete | EvidenceResolved | EvidenceMissing |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var sample in report.Samples)
        {
            sb.AppendLine($"| {sample.DecisionId[..Math.Min(12, sample.DecisionId.Length)]} | {sample.Source} | {sample.SelectedCount} | {sample.DroppedCount} | {sample.EstimatedTokens} | {sample.NonActivationContractHolds} | {sample.EvidenceComplete} | {sample.EvidenceResolvedCount} | {sample.EvidenceMissingCount} |");
        }

        return sb.ToString();
    }
}
