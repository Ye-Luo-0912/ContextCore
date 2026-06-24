using ContextCore.Abstractions;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Commands;

/// <summary>执行混合检索调试并展示 trace、候选、选中项、丢弃项和最终包。</summary>
public static class RetrievalCommand
{
    public static async Task ExecuteAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        if (args.Count == 0
            || !string.Equals(args[0], "debug", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("retrieval 支持：debug --query <文本> [--rewritten <文本>] [--top-k 10] [--budget 1200] [--vector 1,0,0]");
            return;
        }

        var query = CommandHelpers.GetOption(args, "--query")
            ?? (args.Count > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null);
        if (string.IsNullOrWhiteSpace(query))
        {
            Console.WriteLine("缺少 --query 参数。");
            return;
        }

        var topK = CommandHelpers.GetIntOption(args, "--top-k", 10);
        var tokenBudget = CommandHelpers.GetIntOption(args, "--budget", 1200);
        tokenBudget = CommandHelpers.GetIntOption(args, "--token-budget", tokenBudget);
        var candidateTake = CommandHelpers.GetIntOption(args, "--candidate-take", 50);
        var vectorTopK = CommandHelpers.GetIntOption(args, "--vector-top-k", 20);
        var details = await service.BuildRetrievalDebugAsync(
            query,
            rewrittenQueryText: CommandHelpers.GetOption(args, "--rewritten"),
            queryVector: ParseVector(CommandHelpers.GetOption(args, "--vector")),
            topK: topK,
            tokenBudget: tokenBudget,
            candidateTake: candidateTake,
            vectorTopK: vectorTopK,
            includeKeywordRecall: !CommandHelpers.HasFlag(args, "--no-keyword"),
            includeVectorRecall: !CommandHelpers.HasFlag(args, "--no-vector"),
            includeRelationExpansion: !CommandHelpers.HasFlag(args, "--no-relations"),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        Render(details);
    }

    private static void Render(RetrievalDebugDetails details)
    {
        var result = details.Result;
        Console.WriteLine();
        Console.WriteLine($"检索调试 {result.OperationId}");
        Console.WriteLine(new string('=', 12 + result.OperationId.Length));
        Console.WriteLine($"原始查询   : {result.Trace.QueryText}");
        Console.WriteLine($"重写查询   : {result.Trace.RewrittenQueryText ?? ""}");
        Console.WriteLine($"成功       : {result.Succeeded}");
        Console.WriteLine($"候选数     : {result.Trace.Candidates.Count}");
        Console.WriteLine($"选中数     : {result.SelectedItems.Count}");
        Console.WriteLine($"丢弃数     : {result.DroppedItems.Count}");
        Console.WriteLine($"Token      : {result.EstimatedTokens}");
        Console.WriteLine($"查询向量   : {result.Metadata.GetValueOrDefault("queryVectorSource", "")}");

        TableRenderer.Render(
            "检索阶段",
            ["阶段", "候选数", "信息"],
            [.. result.Trace.Stages.Select(stage => new[]
            {
                stage.Name,
                stage.CandidateCount.ToString(),
                FormatMetadata(stage.Metadata)
            })]);

        TableRenderer.Render(
            "候选项",
            ["Id", "来源", "类型", "分数", "Token", "原因"],
            [.. result.Trace.Candidates.Select(item => new[]
            {
                item.SourceId,
                FormatKind(item.Kind),
                item.Type,
                item.Score.ToString("0.00"),
                item.EstimatedTokens.ToString(),
                string.Join(" | ", item.Reasons)
            })]);

        TableRenderer.Render(
            "Attention Shadow Trace",
            ["Id", "当前", "Attention", "分数", "Breakdown", "原因"],
            [.. result.Trace.AttentionScores.Select(score => new[]
            {
                score.SourceId,
                score.CurrentRank.ToString(),
                score.AttentionRank.ToString(),
                score.FinalAttentionScore.ToString("0.000"),
                FormatAttentionBreakdown(score),
                string.Join(" | ", score.Reasons)
            })]);

        Console.WriteLine(
            $"Attention Shadow Summary: candidates={result.Trace.AttentionShadowReport.CandidateCount}, selected={result.Trace.AttentionShadowReport.SelectedCount}, wouldChange={FormatBool(result.Trace.AttentionShadowReport.WouldChangeSelectedSet)}, changeRatio={result.Trace.AttentionShadowReport.SelectedSetChangeRatio:P1}, mustNotHitPromoted={result.Trace.AttentionShadowReport.MustNotHitPromotedCount}");
        if (result.Trace.AttentionShadowReport.Warnings.Count > 0)
        {
            Console.WriteLine($"Attention Shadow Warnings: {string.Join(", ", result.Trace.AttentionShadowReport.Warnings)}");
        }

        TableRenderer.Render(
            "Attention Rerank Status",
            ["Mode", "Profile", "Applied", "SelectedSet", "OrderChanges", "GuardViolation"],
            [
                [
                    result.Trace.AttentionRerankComparison.AttentionRerankMode,
                    result.Trace.AttentionRerankComparison.AttentionProfile,
                    FormatBool(result.Trace.AttentionRerankComparison.AttentionApplied),
                    FormatBool(result.Trace.AttentionRerankComparison.SelectedSetPreserved),
                    result.Trace.AttentionRerankComparison.OrderChangedCount.ToString(),
                    result.Trace.AttentionRerankComparison.GuardViolation
                ]
            ]);

        TableRenderer.Render(
            "Planning Execution Status",
            ["Mode", "Intent", "Status", "OptIn", "FallbackUsed", "FallbackReason"],
            [
                [
                    result.Trace.Metadata.GetValueOrDefault("planningMode", "Off"),
                    result.Trace.Metadata.GetValueOrDefault("planningIntent", ""),
                    result.Trace.Metadata.GetValueOrDefault("planningExecutionStatus", "Legacy"),
                    result.Trace.Metadata.GetValueOrDefault("planningOptInMatched", "false"),
                    result.Trace.Metadata.GetValueOrDefault("planningFallbackUsed", "false"),
                    result.Trace.Metadata.GetValueOrDefault("planningFallbackReason", "")
                ]
            ]);

        var graphMetadata = details.Package.Metadata.Count > 0
            ? details.Package.Metadata
            : result.Trace.Metadata;
        TableRenderer.Render(
            "Graph Expansion Status",
            ["Mode", "Applied", "FallbackUsed", "FallbackReason", "Profiles", "AddedItems", "TargetSections", "ExpectedDelta", "UnexpectedWarn", "RiskChecks"],
            [
                [
                    graphMetadata.GetValueOrDefault("graphExpansionMode", result.Trace.Metadata.GetValueOrDefault("graphExpansionMode", "Off")),
                    graphMetadata.GetValueOrDefault("graphExpansionApplied", result.Trace.Metadata.GetValueOrDefault("graphExpansionApplied", "false")),
                    graphMetadata.GetValueOrDefault("graphExpansionFallbackUsed", result.Trace.Metadata.GetValueOrDefault("graphExpansionFallbackUsed", "false")),
                    graphMetadata.GetValueOrDefault("graphExpansionFallbackReason", result.Trace.Metadata.GetValueOrDefault("graphExpansionFallbackReason", "")),
                    graphMetadata.GetValueOrDefault("graphExpansionProfiles", result.Trace.Metadata.GetValueOrDefault("graphExpansionProfiles", "")),
                    graphMetadata.GetValueOrDefault("graphExpansionAddedItems", result.Trace.Metadata.GetValueOrDefault("graphExpansionAddedItems", "")),
                    graphMetadata.GetValueOrDefault("graphExpansionTargetSections", result.Trace.Metadata.GetValueOrDefault("graphExpansionTargetSections", "")),
                    graphMetadata.GetValueOrDefault("graphExpansionExpectedGraphSectionDelta", result.Trace.Metadata.GetValueOrDefault("graphExpansionExpectedGraphSectionDelta", "0")),
                    graphMetadata.GetValueOrDefault("graphExpansionUnexpectedWarningDelta", result.Trace.Metadata.GetValueOrDefault("graphExpansionUnexpectedWarningDelta", "0")),
                    graphMetadata.GetValueOrDefault("graphExpansionRiskChecks", result.Trace.Metadata.GetValueOrDefault("graphExpansionRiskChecks", ""))
                ]
            ]);

        TableRenderer.Render(
            "Attention Shadow Diff",
            ["Id", "当前", "Attention", "Delta", "选中", "Would", "原分", "Attn分", "标签", "原因"],
            [.. result.Trace.AttentionShadowReport.Ranks
                .OrderBy(rank => rank.AttentionRank)
                .Take(20)
                .Select(rank => new[]
                {
                    rank.SourceId,
                    rank.CurrentRank.ToString(),
                    rank.AttentionRank.ToString(),
                    rank.RankDelta.ToString("+0;-0;0"),
                    FormatBool(rank.SelectedByCurrentPolicy),
                    FormatBool(rank.WouldBeSelectedByAttention),
                    rank.CurrentScore.ToString("0.00"),
                    rank.AttentionScore.ToString("0.000"),
                    FormatAttentionLabels(rank),
                    string.Join(" | ", rank.Reasons)
                })]);

        if (result.Trace.AttentionProfileComparison.Profiles.Count > 0)
        {
            TableRenderer.Render(
                "Attention Profile Comparison",
                ["Profile", "Candidates", "WouldChange", "ChangeRatio", "MustNotHitPromoted", "Top Promoted", "Top Demoted"],
                [.. result.Trace.AttentionProfileComparison.Profiles.Select(profile => new[]
                {
                    profile.ProfileId,
                    profile.ShadowReport.CandidateCount.ToString(),
                    FormatBool(profile.ShadowReport.WouldChangeSelectedSet),
                    profile.ShadowReport.SelectedSetChangeRatio.ToString("P1"),
                    profile.ShadowReport.MustNotHitPromotedCount.ToString(),
                    FormatProfileTop(profile.ShadowReport.TopPromotedCandidates),
                    FormatProfileTop(profile.ShadowReport.TopDemotedCandidates)
                })]);

            TableRenderer.Render(
                "Attention Profile Rank Details",
                ["Profile", "Id", "当前", "Attention", "Delta", "选中", "Would", "Breakdown", "原因"],
                [.. result.Trace.AttentionProfileComparison.Profiles
                    .SelectMany(profile =>
                    {
                        var scoresById = profile.AttentionScores
                            .GroupBy(score => score.CandidateId, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

                        return profile.ShadowReport.Ranks
                            .Where(rank => rank.RankDelta != 0 || rank.WouldBeSelectedByAttention != rank.SelectedByCurrentPolicy)
                            .OrderByDescending(rank => Math.Abs(rank.RankDelta))
                            .ThenBy(rank => rank.AttentionRank)
                            .Take(5)
                            .Select(rank => new[]
                            {
                                profile.ProfileId,
                                rank.SourceId,
                                rank.CurrentRank.ToString(),
                                rank.AttentionRank.ToString(),
                                rank.RankDelta.ToString("+0;-0;0"),
                                FormatBool(rank.SelectedByCurrentPolicy),
                                FormatBool(rank.WouldBeSelectedByAttention),
                                scoresById.TryGetValue(rank.CandidateId, out var score) ? FormatAttentionBreakdown(score) : string.Empty,
                                string.Join(" | ", rank.Reasons)
                            });
                    })]);
        }

        TableRenderer.Render(
            "选中项",
            ["Id", "来源", "类型", "分数", "Token", "原因"],
            [.. result.Trace.SelectedItems.Select(item => new[]
            {
                item.SourceId,
                FormatKind(item.Kind),
                item.Type,
                item.Score.ToString("0.00"),
                item.EstimatedTokens.ToString(),
                item.Reason
            })]);

        TableRenderer.Render(
            "丢弃项",
            ["Id", "来源", "类型", "分数", "Token", "原因"],
            [.. result.DroppedItems.Select(item => new[]
            {
                item.SourceId,
                FormatKind(item.Kind),
                item.Type,
                item.Score.ToString("0.00"),
                item.EstimatedTokens.ToString(),
                item.Reason
            })]);

        DetailRenderer.RenderPackage(details.Package);

        TableRenderer.Render(
            "最近检索记录",
            ["检索Id", "查询", "候选", "选中", "时间"],
            [.. details.RecentTraces.Select(trace => new[]
            {
                trace.RetrievalId,
                trace.QueryText ?? "",
                trace.Candidates.Count.ToString(),
                trace.SelectedItems.Count.ToString(),
                trace.CreatedAt.ToString("u")
            })]);
    }

    private static string FormatKind(object? kind)
    {
        return kind?.ToString() switch
        {
            "ContextItem" => "上下文条目",
            "MemoryItem" => "记忆条目",
            null or "" => "",
            var value => value
        };
    }

    private static string FormatMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        return string.Join("; ", metadata.Select(pair => $"{FormatMetadataKey(pair.Key)}={FormatMetadataValue(pair.Value)}"));
    }

    private static string FormatMetadataKey(string key)
    {
        return key switch
        {
            "rawItems" => "原始条目",
            "memoryItems" => "记忆条目",
            "vectorHits" => "向量命中",
            "skipped" => "跳过原因",
            "queryInstruction" => "查询指令",
            "queryEmbeddingModelCalls" => "Embedding 调用次数",
            "mandatory" => "强制",
            _ => key
        };
    }

    private static string FormatMetadataValue(string value)
    {
        return value switch
        {
            "true" => "是",
            "false" => "否",
            _ => value
        };
    }

    private static string FormatAttentionBreakdown(ContextAttentionScore score)
    {
        return string.Join("; ",
        [
            $"query={score.QueryMatchScore:0.00}",
            $"learning={score.LearningFeedbackScore:0.00}",
            $"noise={score.NoiseRiskScore:0.00}",
            $"short={score.ShortTermMatchScore:0.00}",
            $"relation={score.RelationScore:0.00}",
            $"recency={score.RecencyScore:0.00}",
            $"importance={score.ImportanceScore:0.00}",
            $"channel={score.ChannelScore:0.00}",
            $"lifecyclePenalty={score.LifecyclePenalty:0.00}",
            $"scopePenalty={score.ScopePenalty:0.00}"
        ]);
    }

    private static string FormatBool(bool value) => value ? "是" : "否";

    private static string FormatAttentionLabels(AttentionShadowRank rank)
    {
        if (rank.IsMustHit && rank.IsMustNotHit)
        {
            return "MustHit,MustNotHit";
        }

        if (rank.IsMustHit)
        {
            return "MustHit";
        }

        return rank.IsMustNotHit ? "MustNotHit" : string.Empty;
    }

    private static string FormatProfileTop(IReadOnlyList<AttentionShadowRank> ranks)
    {
        return string.Join("; ", ranks.Take(3).Select(rank =>
            $"{rank.SourceId}({rank.RankDelta:+0;-0;0})"));
    }

    private static IReadOnlyList<float>? ParseVector(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var values = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var vector = new List<float>();
        foreach (var item in values)
        {
            if (!float.TryParse(item, out var parsed))
            {
                return null;
            }

            vector.Add(parsed);
        }

        return vector.Count == 0 ? null : vector;
    }
}
