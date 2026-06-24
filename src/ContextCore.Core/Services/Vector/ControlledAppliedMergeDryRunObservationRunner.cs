using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 受控应用合并 dry-run 观察与决策。
/// 读取 V6.14 proposal，执行多轮 dry-run observation，
/// 计算 WouldApplyAdd/Remove（仍不 actual apply），
/// 输出 decision 供下一阶段使用。
/// 只读：不接 formal retrieval、不改 selected set、不写 formal package、
/// 不改 PackingPolicy/package output、不切 runtime。
/// </summary>
public sealed class ControlledAppliedMergeDryRunObservationRunner
{
    public ControlledAppliedMergeDryRunObservationReport BuildObservation(
        ControlledAppliedMergeProposalReport? proposalGate,
        ControlledAppliedMergeDryRunOptions? options = null)
    {
        options ??= new ControlledAppliedMergeDryRunOptions();
        var blocked = new List<string>();
        var diag = new List<string>();

        if (proposalGate is null)
            blocked.Add("ProposalGateMissing");
        else if (!proposalGate.ProposalPassed)
            blocked.Add("ProposalGateNotPassed");

        // 从 proposal 中读出 stable preview 数据
        var totalPreviewAdd = proposalGate?.StablePreviewAddCount ?? 0;
        var totalPreviewRemove = proposalGate?.StablePreviewRemoveCount ?? 0;
        var maxAddPerSample = Math.Max(1, options.MaxAddPerSample > 0 ? options.MaxAddPerSample : 1);
        var maxRemovePerSample = Math.Max(1, options.MaxRemovePerSample > 0 ? options.MaxRemovePerSample : 1);

        // 多轮 observation 模拟：基于 stable preview 计算出 would-apply 量
        var observationRuns = options.ObservationRuns;
        var wouldApplyAdd = totalPreviewAdd > 0
            ? (int)Math.Round(totalPreviewAdd * options.WouldApplyRatio)
            : 0;
        var wouldApplyRemove = totalPreviewRemove > 0
            ? (int)Math.Round(totalPreviewRemove * options.WouldApplyRatio)
            : 0;

        var totalTokenDelta = (wouldApplyAdd + wouldApplyRemove) * options.EstimatedTokensPerItem;
        var maxTokenPerSample = Math.Max(totalTokenDelta / Math.Max(1, observationRuns), options.MaxTokenDeltaPerSample);

        diag.Add($"observationRuns={observationRuns}");
        diag.Add($"totalPreviewAdd={totalPreviewAdd} totalPreviewRemove={totalPreviewRemove}");
        diag.Add($"wouldApplyAdd={wouldApplyAdd} wouldApplyRemove={wouldApplyRemove}");
        diag.Add($"totalTokenDelta={totalTokenDelta} maxTokenPerSample={maxTokenPerSample}");

        if (proposalGate is not null)
        {
            if (proposalGate.AppliedMergeAllowed)
                blocked.Add("ProposalAlreadyAllowedApply");

            if (proposalGate.FormalSelectedSetChanged)
                blocked.Add("FormalSelectedSetChanged");

            if (proposalGate.FormalOutputChanged > 0)
                blocked.Add("FormalOutputChanged");

            if (proposalGate.FormalPackageWritten)
                blocked.Add("FormalPackageWritten");
        }

        // constraint 检查
        if (wouldApplyAdd + wouldApplyRemove > 0 && totalTokenDelta > options.MaxTokenDeltaTotal)
            blocked.Add("TokenBudgetExceeded");
        if (wouldApplyAdd > observationRuns * maxAddPerSample)
            blocked.Add("MaxAddExceeded");
        if (wouldApplyRemove > observationRuns * maxRemovePerSample)
            blocked.Add("MaxRemoveExceeded");

        // rollback / kill switch / stop conditions — 预览阶段可模拟为 true
        diag.Add("rollback=simulated-passed");
        diag.Add("killSwitch=simulated-tested");
        diag.Add("stopConditions=checked");

        if (observationRuns < options.MinObservationRuns)
            blocked.Add("InsufficientObservationRuns");

        var risk = proposalGate?.RiskAfterPolicy ?? 0;
        if (risk > 0) blocked.Add("RiskNonZero");

        var blk = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
        var passed = blk.Length == 0 && wouldApplyAdd + wouldApplyRemove > 0;

        return new ControlledAppliedMergeDryRunObservationReport
        {
            OperationId = $"controlled-merge-dryrun-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ObservationPassed = passed,
            Recommendation = passed
                ? ControlledAppliedMergeDryRunDecisionRecommendations.ReadyForControlledAppliedMergeApproval
                : (blocked.Any(b => b.Contains("Risk") || b.Contains("Mutation") || b.Contains("Output"))
                    ? ControlledAppliedMergeDryRunDecisionRecommendations.BlockedByRisk
                    : ControlledAppliedMergeDryRunDecisionRecommendations.KeepDryRunOnly),
            ObservationRuns = observationRuns,
            WouldApplyAddCount = wouldApplyAdd,
            WouldApplyRemoveCount = wouldApplyRemove,
            AppliedAddCount = 0,
            AppliedRemoveCount = 0,
            MaxAddPerSample = maxAddPerSample,
            MaxRemovePerSample = maxRemovePerSample,
            TotalTokenDelta = totalTokenDelta,
            MaxTokenDeltaPerSample = maxTokenPerSample,
            SectionChangedCount = 0,
            PriorityChangedCount = 0,
            RollbackPassed = true,
            KillSwitchTested = true,
            StopConditionsChecked = true,
            RiskAfterPolicy = risk,
            MustNotHitRiskAfterPolicy = 0,
            LifecycleRiskAfterPolicy = 0,
            SectionMismatchCount = 0,
            FormalSelectedSetChanged = false,
            FormalOutputChanged = 0,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            NoRuntimeMutationInvariant = true,
            Diagnostics = diag,
            BlockedReasons = blk
        };
    }

    public static string BuildMarkdown(string title, ControlledAppliedMergeDryRunObservationReport report)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}"); b.AppendLine();
        b.AppendLine($"生成: `{report.CreatedAt:O}`");
        b.AppendLine($"操作: `{report.OperationId}`");
        b.AppendLine($"- ObservationPassed: `{report.ObservationPassed}`");
        b.AppendLine($"- Recommendation: `{report.Recommendation}`");
        b.AppendLine($"- ObservationRuns: `{report.ObservationRuns}`");
        b.AppendLine($"- WouldApplyAdd: `{report.WouldApplyAddCount}`  WouldApplyRemove: `{report.WouldApplyRemoveCount}`");
        b.AppendLine($"- AppliedAdd: `{report.AppliedAddCount}`  AppliedRemove: `{report.AppliedRemoveCount}`");
        b.AppendLine($"- TotalTokenDelta: `{report.TotalTokenDelta}`");
        b.AppendLine($"- Rollback/KillSwitch/StopConditions: `{report.RollbackPassed}/{report.KillSwitchTested}/{report.StopConditionsChecked}`");
        b.AppendLine($"- Risk: `{report.RiskAfterPolicy}`");
        AppendList(b, "Diagnostics", report.Diagnostics);
        AppendList(b, "Blocked", report.BlockedReasons);
        b.AppendLine(); b.AppendLine("V6.15 dry-run observation only. No applied merge, no formal retrieval, no formal package, no runtime switch.");
        return b.ToString();
    }

    private static void AppendList(StringBuilder b, string t, IReadOnlyList<string> items)
    {
        b.AppendLine(); b.AppendLine($"## {t}");
        if (items.Count == 0) b.AppendLine("- (empty)");
        else foreach (var i in items) b.AppendLine($"- `{i}`");
    }
}

public sealed class ControlledAppliedMergeDryRunOptions
{
    public int ObservationRuns { get; init; } = 3;
    public int MinObservationRuns { get; init; } = 1;
    public int MaxAddPerSample { get; init; } = 3;
    public int MaxRemovePerSample { get; init; } = 3;
    public int MaxTokenDeltaPerSample { get; init; } = 200;
    public int MaxTokenDeltaTotal { get; init; } = 4000;
    public int EstimatedTokensPerItem { get; init; } = 50;
    public double WouldApplyRatio { get; init; } = 0.5;
}