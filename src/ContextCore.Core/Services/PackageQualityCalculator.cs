using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// R14-2：Package Quality 指标计算器。基于 <see cref="ContextPackageBuildResult"/> 在投影过程中
/// 一次性计算 8 个确定性指标，不触发任何运行时变更。
/// </summary>
/// <remarks>
/// 计算原则：
/// <list type="bullet">
/// <item>所有指标为 [0,1] 区间的归一化分数（1.0 = 最优）。</item>
/// <item>分母为 0 时返回中性分数（1.0 表示"无要求/完美"，避免惩罚无约束场景）。</item>
/// <item>仅使用 build result 已暴露的数据，不重新查询 store，不破坏只读投影契约。</item>
/// <item>ReasonCode 通过 <see cref="CandidateDecisionReasonCodeMapper.MapFromReason"/> 重新映射，
/// 避免依赖 Projector 内部的 enrichment 顺序。</item>
/// </list>
/// </remarks>
internal static class PackageQualityCalculator
{
    private const double WeightAnchorCoverage = 0.15;
    private const double WeightHardConstraintSatisfaction = 0.20;
    private const double WeightRequiredItemCoverage = 0.15;
    private const double WeightRedundancy = 0.10;
    private const double WeightProvenanceCompleteness = 0.10;
    private const double WeightLifecycleRisk = 0.15;
    private const double WeightTokenEfficiency = 0.10;
    private const double WeightSectionBalance = 0.05;

    public static PackageQualityReport Compute(ContextPackageBuildResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var anchorCoverage = ComputeAnchorCoverage(result);
        var hardConstraint = ComputeHardConstraintSatisfaction(result);
        var requiredItem = ComputeRequiredItemCoverage(result);
        var redundancy = ComputeRedundancy(result);
        var provenance = ComputeProvenanceCompleteness(result);
        var lifecycle = ComputeLifecycleRisk(result);
        var tokenEff = ComputeTokenEfficiency(result);
        var sectionBalance = ComputeSectionBalance(result);

        var overall =
            anchorCoverage.Score * WeightAnchorCoverage +
            hardConstraint.Score * WeightHardConstraintSatisfaction +
            requiredItem.Score * WeightRequiredItemCoverage +
            redundancy.Score * WeightRedundancy +
            provenance.Score * WeightProvenanceCompleteness +
            lifecycle.Score * WeightLifecycleRisk +
            tokenEff.Score * WeightTokenEfficiency +
            sectionBalance.Score * WeightSectionBalance;

        return new PackageQualityReport
        {
            AnchorCoverage = anchorCoverage,
            HardConstraintSatisfaction = hardConstraint,
            RequiredItemCoverage = requiredItem,
            Redundancy = redundancy,
            ProvenanceCompleteness = provenance,
            LifecycleRisk = lifecycle,
            TokenEfficiency = tokenEff,
            SectionBalance = sectionBalance,
            OverallScore = Clamp01(overall),
            ComputedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Anchor 覆盖率：被选入候选覆盖到的 anchor 比例。
    /// 通过 anchor 名是否出现在 section content 中判断覆盖（确定性字符串匹配）。
    /// </summary>
    private static PackageQualityMetric ComputeAnchorCoverage(ContextPackageBuildResult result)
    {
        var metadata = result.Package.Metadata;
        var anchorNames = ParseCommaSeparated(metadata, "anchor.names");
        if (anchorNames.Count == 0)
        {
            return new PackageQualityMetric
            {
                Name = "AnchorCoverage",
                Score = 1.0,
                Numerator = 0,
                Denominator = 0,
                Detail = "no anchors extracted"
            };
        }

        var sectionContents = result.Package.Sections
            .Where(s => !string.IsNullOrWhiteSpace(s.Content))
            .Select(s => s.Content)
            .ToList();

        var semanticSet = new HashSet<string>(
            ParseCommaSeparated(metadata, "anchor.semanticAnchors"),
            StringComparer.OrdinalIgnoreCase);
        var rawSet = new HashSet<string>(
            ParseCommaSeparated(metadata, "anchor.rawSearchTokens"),
            StringComparer.OrdinalIgnoreCase);

        var covered = 0;
        var coveredSemantic = 0;
        var coveredRaw = 0;
        var totalSemantic = 0;
        var totalRaw = 0;

        foreach (var name in anchorNames)
        {
            var isSemantic = semanticSet.Contains(name);
            var isRaw = rawSet.Contains(name);
            if (isSemantic) totalSemantic++;
            if (isRaw) totalRaw++;

            var matched = false;
            foreach (var content in sectionContents)
            {
                if (content.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                    break;
                }
            }

            if (matched)
            {
                covered++;
                if (isSemantic) coveredSemantic++;
                if (isRaw) coveredRaw++;
            }
        }

        var score = (double)covered / anchorNames.Count;

        return new PackageQualityMetric
        {
            Name = "AnchorCoverage",
            Score = Clamp01(score),
            Numerator = covered,
            Denominator = anchorNames.Count,
            Detail = $"covered={covered}/{anchorNames.Count} anchors (semantic={coveredSemantic}/{totalSemantic}, raw={coveredRaw}/{totalRaw})"
        };
    }

    /// <summary>
    /// Hard constraint 满足度：active hard constraints 中被选入 package 的比例。
    /// 通过 Kind == "hard_constraint" 或 SectionName == "hard_constraints" 识别。
    /// </summary>
    private static PackageQualityMetric ComputeHardConstraintSatisfaction(ContextPackageBuildResult result)
    {
        var selectedHard = result.SelectedItems
            .Count(IsHardConstraintDecision);
        var droppedHard = result.DroppedItems
            .Count(IsHardConstraintDropped);

        var total = selectedHard + droppedHard;
        if (total == 0)
        {
            return new PackageQualityMetric
            {
                Name = "HardConstraintSatisfaction",
                Score = 1.0,
                Numerator = 0,
                Denominator = 0,
                Detail = "no hard constraints encountered"
            };
        }

        var score = (double)selectedHard / total;
        return new PackageQualityMetric
        {
            Name = "HardConstraintSatisfaction",
            Score = Clamp01(score),
            Numerator = selectedHard,
            Denominator = total,
            Detail = $"satisfied={selectedHard}/{total} (dropped={droppedHard})"
        };
    }

    /// <summary>
    /// MustHit / Required IDs 覆盖率：mustHit IDs 中落入选中项的比例。
    /// 从 package metadata 中重新解析 mustHit keys（与 PackagePolicyResolver 逻辑一致）。
    /// </summary>
    private static PackageQualityMetric ComputeRequiredItemCoverage(ContextPackageBuildResult result)
    {
        var mustHitIds = ResolveMustHitIds(result.Package.Metadata);
        if (mustHitIds.Count == 0)
        {
            return new PackageQualityMetric
            {
                Name = "RequiredItemCoverage",
                Score = 1.0,
                Numerator = 0,
                Denominator = 0,
                Detail = "no mustHit IDs configured"
            };
        }

        var selectedIds = result.SelectedItems
            .Select(s => s.ItemId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var covered = mustHitIds.Count(id => selectedIds.Contains(id));
        var missing = mustHitIds.Except(selectedIds, StringComparer.OrdinalIgnoreCase).ToList();
        var score = (double)covered / mustHitIds.Count;

        var missingDetail = missing.Count > 0
            ? $", missing=[{string.Join(",", missing.Take(5))}{(missing.Count > 5 ? "..." : "")}]"
            : string.Empty;

        return new PackageQualityMetric
        {
            Name = "RequiredItemCoverage",
            Score = Clamp01(score),
            Numerator = covered,
            Denominator = mustHitIds.Count,
            Detail = $"covered={covered}/{mustHitIds.Count} mustHit{missingDetail}"
        };
    }

    /// <summary>
    /// 冗余度：所有候选中无重复内容的比例（1.0 = 无冗余）。
    /// 通过 DroppedItems 中 duplicate-suppressed 原因码计数识别。
    /// </summary>
    private static PackageQualityMetric ComputeRedundancy(ContextPackageBuildResult result)
    {
        var duplicateDropped = 0;
        foreach (var dropped in result.DroppedItems)
        {
            var code = CandidateDecisionReasonCodeMapper.MapFromReason(dropped.Reason);
            if (code == CandidateDecisionReasonCode.DuplicateSuppressed
                || code == CandidateDecisionReasonCode.DuplicateSectionReference)
            {
                duplicateDropped++;
            }
        }

        var totalCandidates = result.SelectedItems.Count + result.DroppedItems.Count;
        if (totalCandidates == 0)
        {
            return new PackageQualityMetric
            {
                Name = "Redundancy",
                Score = 1.0,
                Numerator = 0,
                Denominator = 0,
                Detail = "no candidates encountered"
            };
        }

        // Score = 1 - redundancyRate（duplicateDropped 越多，score 越低）
        var redundancyRate = (double)duplicateDropped / totalCandidates;
        var score = 1.0 - redundancyRate;

        return new PackageQualityMetric
        {
            Name = "Redundancy",
            Score = Clamp01(score),
            Numerator = duplicateDropped,
            Denominator = totalCandidates,
            Detail = $"duplicates_suppressed={duplicateDropped}/{totalCandidates} ({redundancyRate:P1} suppressed)"
        };
    }

    /// <summary>
    /// Provenance 完整性：选中项中携带至少一个 SourceRef 的比例。
    /// </summary>
    private static PackageQualityMetric ComputeProvenanceCompleteness(ContextPackageBuildResult result)
    {
        var total = result.SelectedItems.Count;
        if (total == 0)
        {
            return new PackageQualityMetric
            {
                Name = "ProvenanceCompleteness",
                Score = 1.0,
                Numerator = 0,
                Denominator = 0,
                Detail = "no selected items"
            };
        }

        var withRefs = result.SelectedItems.Count(s => s.SourceRefs.Count > 0);
        var score = (double)withRefs / total;

        return new PackageQualityMetric
        {
            Name = "ProvenanceCompleteness",
            Score = Clamp01(score),
            Numerator = withRefs,
            Denominator = total,
            Detail = $"items_with_refs={withRefs}/{total}"
        };
    }

    /// <summary>
    /// Lifecycle 风险：选中项中无 lifecycle 风险的比例（1.0 = 全部 active）。
    /// 通过 ReasonCode 判断：DeprecatedUsedByActiveChain / LifecycleBlocked / DeprecatedBlocked / SupersededByCurrentVersion 视为风险。
    /// </summary>
    private static PackageQualityMetric ComputeLifecycleRisk(ContextPackageBuildResult result)
    {
        var total = result.SelectedItems.Count;
        if (total == 0)
        {
            return new PackageQualityMetric
            {
                Name = "LifecycleRisk",
                Score = 1.0,
                Numerator = 0,
                Denominator = 0,
                Detail = "no selected items"
            };
        }

        var deprecated = 0;
        var superseded = 0;
        var lifecycleBlocked = 0;

        foreach (var selected in result.SelectedItems)
        {
            // 优先读 Metadata["lifecycleStatus"]（SectionCollectors 对 dropped deprecated 项写入）
            if (selected.Metadata.TryGetValue("lifecycleStatus", out var status)
                && !string.IsNullOrWhiteSpace(status)
                && !string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
            {
                deprecated++;
                continue;
            }

            // 回退到 ReasonCode（selected 项的 Reason 仍可能含 deprecated-used-by-active 等信号）
            var code = CandidateDecisionReasonCodeMapper.MapFromReason(selected.Reason);
            if (code == CandidateDecisionReasonCode.DeprecatedUsedByActiveChain
                || code == CandidateDecisionReasonCode.DeprecatedBlocked)
            {
                deprecated++;
            }
            else if (code == CandidateDecisionReasonCode.SupersededByCurrentVersion)
            {
                superseded++;
            }
            else if (code == CandidateDecisionReasonCode.LifecycleBlocked)
            {
                lifecycleBlocked++;
            }
        }

        var riskCount = deprecated + superseded + lifecycleBlocked;
        var riskRate = (double)riskCount / total;
        var score = 1.0 - riskRate;

        return new PackageQualityMetric
        {
            Name = "LifecycleRisk",
            Score = Clamp01(score),
            Numerator = riskCount,
            Denominator = total,
            Detail = $"deprecated={deprecated}, superseded={superseded}, blocked={lifecycleBlocked} / {total} selected"
        };
    }

    /// <summary>
    /// Token 预算利用效率：UsedTokens / TokenBudget（clamped to [0,1]，超支记 0）。
    /// </summary>
    private static PackageQualityMetric ComputeTokenEfficiency(ContextPackageBuildResult result)
    {
        var used = result.Budget.UsedTokens;
        var budget = result.Budget.TokenBudget;

        if (budget <= 0)
        {
            return new PackageQualityMetric
            {
                Name = "TokenEfficiency",
                Score = 0.0,
                Numerator = used,
                Denominator = budget,
                Detail = $"no budget configured (used={used})"
            };
        }

        if (used > budget)
        {
            return new PackageQualityMetric
            {
                Name = "TokenEfficiency",
                Score = 0.0,
                Numerator = used,
                Denominator = budget,
                Detail = $"overrun: used={used}/budget={budget}"
            };
        }

        var ratio = (double)used / budget;
        return new PackageQualityMetric
        {
            Name = "TokenEfficiency",
            Score = Clamp01(ratio),
            Numerator = used,
            Denominator = budget,
            Detail = $"used={used}/{budget} ({ratio:P1})"
        };
    }

    /// <summary>
    /// Section 预算均衡度：基于各 section UsageRatio 的标准差。
    /// Score = 1 - stddev（clamped to [0,1]），stddev=0 表示完全均衡。
    /// </summary>
    private static PackageQualityMetric ComputeSectionBalance(ContextPackageBuildResult result)
    {
        var sections = result.Budget.Sections;
        if (sections.Count == 0)
        {
            return new PackageQualityMetric
            {
                Name = "SectionBalance",
                Score = 1.0,
                Numerator = 0,
                Denominator = 0,
                Detail = "no sections in budget report"
            };
        }

        if (sections.Count == 1)
        {
            return new PackageQualityMetric
            {
                Name = "SectionBalance",
                Score = 1.0,
                Numerator = 1,
                Denominator = 1,
                Detail = "single section (trivially balanced)"
            };
        }

        var ratios = sections.Select(s => Math.Clamp(s.UsageRatio, 0.0, 1.0)).ToList();
        var mean = ratios.Average();
        var variance = ratios.Sum(r => (r - mean) * (r - mean)) / ratios.Count;
        var stddev = Math.Sqrt(variance);
        var score = Clamp01(1.0 - stddev);

        var min = ratios.Min();
        var max = ratios.Max();

        return new PackageQualityMetric
        {
            Name = "SectionBalance",
            Score = score,
            Numerator = sections.Count,
            Denominator = sections.Count,
            Detail = $"sections={sections.Count}, mean={mean:F2}, stddev={stddev:F3}, min={min:F2}, max={max:F2}"
        };
    }

    private static bool IsHardConstraintDecision(ContextPackageDecision decision)
    {
        return string.Equals(decision.Kind, "hard_constraint", StringComparison.OrdinalIgnoreCase)
            || string.Equals(decision.SectionName, "hard_constraints", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHardConstraintDropped(DroppedContextItem item)
    {
        return string.Equals(item.Kind, "hard_constraint", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> ParseCommaSeparated(IReadOnlyDictionary<string, string> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return new List<string>();
        }
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    /// <summary>
    /// 从 metadata 重新解析 mustHit IDs，与 PackagePolicyResolver.ResolvePackageMustHitIds 逻辑一致。
    /// 支持 keys: eval.mustHit, package.mustHit, mustHit, attention.mustHit。
    /// </summary>
    private static IReadOnlySet<string> ResolveMustHitIds(IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new[] { "eval.mustHit", "package.mustHit", "mustHit", "attention.mustHit" };
        var separators = new[] { ',', ';', '，', '；', '|', '\r', '\n', '\t', ' ' };

        foreach (var key in keys)
        {
            if (!metadata.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }
            foreach (var value in raw.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static double Clamp01(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 0.0;
        if (value < 0.0) return 0.0;
        if (value > 1.0) return 1.0;
        return value;
    }
}
