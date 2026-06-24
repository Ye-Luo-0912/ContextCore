using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 运行时特征推导失败冻结报告：汇总 V5.7/V5.8 结果，锁定失败状态与后续路线。
/// 确认 CanonicalRuntimeAnchorResolver 可复用，RuntimeRelationIntentDeriver
/// 不可用于 formal scoring，结合修复仅作评估上限参考。
/// 禁用关系 boost 推广、结合修复正式评分、hub 展开关系 envelope 评分。
/// 推荐下一步：图枢纽/关系噪声控制预览、输入证据/来源契约执行。
/// </summary>
public sealed class RuntimeFeatureDerivationFailureFreezeRunner
{
    private static readonly IReadOnlyList<string> DisabledCapabilities = new[]
    {
        "relation boost promotion: hub-expanded relation envelope causes uniform multiplier across all candidates",
        "combined-repair formal scoring: relation boost degrades recall on hub-and-spoke relation graphs",
        "hub-expanded relation envelope scoring: hub items flood envelope with non-discriminative relations",
    };

    private static readonly IReadOnlyList<string> RecommendedNextPhases = new[]
    {
        "RuntimeRetrievalFeatureDerivationFreeze: graph hub / relation noise control preview",
        "RuntimeRetrievalFeatureDerivationFreeze: input evidence / provenance contract enforcement",
    };

    public RuntimeFeatureDerivationFailureFreezeReport BuildFreeze(
        RuntimeRetrievalFeatureDerivationRepairReport? repairGate,
        RuntimeRetrievalFeatureDerivationReport? derivationGate,
        RuntimeFeatureDerivationFailureFreezeOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
    {
        options ??= new RuntimeFeatureDerivationFailureFreezeOptions();
        var blocked = new List<string>();

        if (repairGate is null)
        {
            blocked.Add("RepairGateMissing");
        }
        else if (!repairGate.PreviewPassed && options.RequireRepairGateFrozen)
        {
            // V5.8 repair gate failed as expected — this is the frozen state.
        }

        var repairSource = sourceReports?.TryGetValue("repairGate", out var rs) == true ? rs : string.Empty;
        var derivationSource = sourceReports?.TryGetValue("derivationGate", out var ds) == true ? ds : string.Empty;

        var freezePassed = blocked.Count == 0;
        var operationId = $"runtime-feature-derivation-failure-freeze-{Guid.NewGuid():N}";

        return new RuntimeFeatureDerivationFailureFreezeReport
        {
            OperationId = operationId,
            CreatedAt = DateTimeOffset.UtcNow,
            FreezePassed = freezePassed,
            Recommendation = freezePassed
                ? RuntimeFeatureDerivationFailureFreezeRecommendations.ReadyForGraphHubNoiseControlPreview
                : RuntimeFeatureDerivationFailureFreezeRecommendations.BlockedByMissingRepairGate,
            FrozenStatus = "BlockedByHubRelationNoise",
            RepairGateSourcePath = repairSource,
            DerivationGateSourcePath = derivationSource,
            CanonicalAnchorResolverReusable = true,
            RuntimeRelationIntentDeriverReady = false,
            CombinedRepairEvalUpperBoundOnly = true,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            V57DerivedRecall = derivationGate?.DerivedRecall ?? 0,
            V57DerivedMrr = derivationGate?.DerivedMeanReciprocalRank ?? 0,
            V58TrainBaselineRecall = repairGate?.TrainBaselineRecall ?? 0,
            V58TrainDerivedRecall = repairGate?.TrainDerivedRecall ?? 0,
            V58TrainBaselineMrr = repairGate?.TrainBaselineMrr ?? 0,
            V58TrainDerivedMrr = repairGate?.TrainDerivedMrr ?? 0,
            V58HoldoutBaselineRecall = repairGate?.HoldoutBaselineRecall ?? 0,
            V58HoldoutDerivedRecall = repairGate?.HoldoutDerivedRecall ?? 0,
            V58HoldoutBaselineMrr = repairGate?.HoldoutBaselineMrr ?? 0,
            V58HoldoutDerivedMrr = repairGate?.HoldoutDerivedMrr ?? 0,
            CanonicalRelationCoverageRate = repairGate?.CanonicalRequiredRelationCoverageRate ?? 0,
            CanonicalEvidenceCoverageRate = repairGate?.CanonicalEvidenceAnchorCoverageRate ?? 0,
            CanonicalSourceCoverageRate = repairGate?.CanonicalSourceAnchorCoverageRate ?? 0,
            FailureReasons = repairGate?.BlockedReasons ?? Array.Empty<string>(),
            DisabledCapabilities = DisabledCapabilities,
            RecommendedNextPhases = RecommendedNextPhases,
            FrozenArtifactPaths = new[]
            {
                "vector/v5/runtime-feature-derivation-preview.json/.md",
                "vector/v5/runtime-feature-derivation-gate.json/.md",
                "vector/v5/runtime-feature-derivation-repair.json/.md",
                "vector/v5/runtime-feature-derivation-repair-gate.json/.md",
                "vector/v5/runtime-feature-derivation-failure-freeze.json/.md",
            },
        };
    }

    public static string BuildMarkdown(string title, RuntimeFeatureDerivationFailureFreezeReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"生成时间: `{report.CreatedAt:O}`");
        builder.AppendLine($"操作标识: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine("## 冻结状态");
        builder.AppendLine();
        builder.AppendLine($"- FreezePassed: `{report.FreezePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- FrozenStatus: `{report.FrozenStatus}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine();
        builder.AppendLine("## 组件状态");
        builder.AppendLine();
        builder.AppendLine($"- CanonicalAnchorResolverReusable: `{report.CanonicalAnchorResolverReusable}`");
        builder.AppendLine($"- RuntimeRelationIntentDeriverReady: `{report.RuntimeRelationIntentDeriverReady}`");
        builder.AppendLine($"- CombinedRepairEvalUpperBoundOnly: `{report.CombinedRepairEvalUpperBoundOnly}`");
        builder.AppendLine();
        builder.AppendLine("## 失败摘要");
        builder.AppendLine();
        builder.AppendLine($"- V5.7 推导召回率/MRR: `{report.V57DerivedRecall:F4}/{report.V57DerivedMrr:F4}`");
        builder.AppendLine($"- V5.8 训练集 baseline/derived 召回率: `{report.V58TrainBaselineRecall:F4}/{report.V58TrainDerivedRecall:F4}`");
        builder.AppendLine($"- V5.8 训练集 baseline/derived MRR: `{report.V58TrainBaselineMrr:F4}/{report.V58TrainDerivedMrr:F4}`");
        builder.AppendLine($"- V5.8 留出集 baseline/derived 召回率: `{report.V58HoldoutBaselineRecall:F4}/{report.V58HoldoutDerivedRecall:F4}`");
        builder.AppendLine($"- V5.8 留出集 baseline/derived MRR: `{report.V58HoldoutBaselineMrr:F4}/{report.V58HoldoutDerivedMrr:F4}`");
        builder.AppendLine($"- 规范关系覆盖率: `{report.CanonicalRelationCoverageRate:F4}`");
        builder.AppendLine($"- 规范证据覆盖率: `{report.CanonicalEvidenceCoverageRate:F4}`");
        builder.AppendLine($"- 规范来源覆盖率: `{report.CanonicalSourceCoverageRate:F4}`");
        AppendList(builder, "失败原因", report.FailureReasons);
        AppendList(builder, "已禁用能力", report.DisabledCapabilities);
        AppendList(builder, "推荐后续阶段", report.RecommendedNextPhases);
        AppendList(builder, "已冻结产物路径", report.FrozenArtifactPaths);
        builder.AppendLine();
        builder.AppendLine("失败已冻结。在解决图枢纽/关系噪声控制问题前，不做 formal retrieval、package write、PackingPolicy mutation、runtime switch、vector store binding change。");
        return builder.ToString();
    }

    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var value in values)
        {
            builder.AppendLine($"- `{value}`");
        }
    }
}

/// <summary>运行时特征推导失败冻结选项。</summary>
public sealed class RuntimeFeatureDerivationFailureFreezeOptions
{
    public bool RequireRepairGateFrozen { get; init; } = true;
}