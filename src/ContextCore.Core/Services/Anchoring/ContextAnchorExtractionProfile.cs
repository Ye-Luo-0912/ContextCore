using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>锚点提取策略配置；集中管理阈值、权重和通用类型规则，避免服务实现散落硬编码。</summary>
public sealed class ContextAnchorExtractionProfile
{
    public int MaxAnchors { get; init; } = 32;

    public int MaxRecentItemsForAnchor { get; init; } = 8;

    public int MaxQueryTerms { get; init; } = 20;

    public int MaxRecentTermsPerItem { get; init; } = 3;

    public int MinTermLength { get; init; } = 2;

    public int MaxTermLength { get; init; } = 32;

    public int MaxChineseGramLength { get; init; } = 4;

    public double WorkspaceAnchorWeight { get; init; } = 0.70;

    public double CollectionAnchorWeight { get; init; } = 0.70;

    public double RequiredTagWeight { get; init; } = 0.65;

    public double RequiredTypeWeight { get; init; } = 0.60;

    public double QueryTermWeight { get; init; } = 0.55;

    public double RecentRelevanceWeight { get; init; } = 0.50;

    public double RecentRecencyWeight { get; init; } = 0.20;

    public double RecentMinWeight { get; init; } = 0.10;

    public double RecentMaxWeight { get; init; } = 0.70;

    public IReadOnlyList<ContextAnchorMetadataRule> MetadataRules { get; init; } =
        Array.Empty<ContextAnchorMetadataRule>();

    public IReadOnlyList<ContextAnchorTypeRule> TypeRules { get; init; } =
        Array.Empty<ContextAnchorTypeRule>();

    public static ContextAnchorExtractionProfile CreateDefault()
    {
        return new ContextAnchorExtractionProfile
        {
            MetadataRules =
            [
                new("mode", AnchorType.Mode, 1.00),
                new("taskKind", AnchorType.Task, 0.95),
                new("intent", AnchorType.Intent, 0.95),
                new("project", AnchorType.Project, 0.90),
                new("desiredOutputFormat", AnchorType.Intent, 0.75),
                new("timeRange", AnchorType.TimeRange, 0.75)
            ],
            TypeRules =
            [
                new(
                    AnchorType.Constraint,
                    ["constraint", "required", "requirement", "must", "hard", "rule", "约束", "规则", "必须", "禁止"]),
                new(
                    AnchorType.Task,
                    ["task", "todo", "work", "任务", "计划", "步骤"]),
                new(
                    AnchorType.Mode,
                    ["mode", "模式"]),
                new(
                    AnchorType.Intent,
                    ["intent", "goal", "目标", "意图"])
            ]
        };
    }
}

/// <summary>从请求 metadata 中提取锚点的规则。</summary>
public sealed record ContextAnchorMetadataRule(
    string Key,
    AnchorType AnchorType,
    double Weight);

/// <summary>根据通用信号词推断锚点类型的规则。</summary>
public sealed record ContextAnchorTypeRule(
    AnchorType AnchorType,
    IReadOnlyList<string> Signals);
