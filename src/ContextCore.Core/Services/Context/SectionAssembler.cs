using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// Section 装配阶段：负责 token 预算分配、内容裁剪与 section 打包。
/// 从 <see cref="BasicContextPackageBuilder"/> 提取 AddSection / TrimToTokenBudget / AlignToScalarBoundary，
/// 保持 ref estimatedTokens 语义与精确候选接受/拒绝判定逻辑不变。
/// </summary>
internal sealed class SectionAssembler
{
    private readonly Func<string?, TokenEstimationContext, int> _estimateTokens;
    private readonly Func<string, int, TokenEstimationContext, string> _truncateForTokenBudget;

    internal SectionAssembler(
        Func<string?, TokenEstimationContext, int> estimateTokens,
        Func<string, int, TokenEstimationContext, string> truncateForTokenBudget)
    {
        _estimateTokens = estimateTokens;
        _truncateForTokenBudget = truncateForTokenBudget;
    }

    internal SectionPackingResult AddSection(
        ICollection<ContextPackageSection> sections,
        ISet<string> packageSourceRefs,
        string name,
        int priority,
        string content,
        ContextContentFormat contentFormat,
        IReadOnlyList<string> sectionSourceRefs,
        IReadOnlyList<string> sectionItemRefs,
        IReadOnlyList<string> candidateIds,
        int tokenBudget,
        int sectionTokenBudget,
        TokenEstimationContext tokenContext,
        ref int estimatedTokens)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return SectionPackingResult.Dropped("content is empty");
        }

        return AddSectionFromBlocks(
            sections,
            packageSourceRefs,
            name,
            priority,
            new[] { content },
            contentFormat,
            sectionSourceRefs,
            sectionItemRefs,
            candidateIds,
            tokenBudget,
            sectionTokenBudget,
            tokenContext,
            ref estimatedTokens);
    }

    /// <summary>
    /// 预算感知的流式 section 装配：逐项追加 block，达到预算即停止，
    /// 只对最后一个条目做截断。避免将所有候选格式化为大字符串后再二分裁剪。
    /// </summary>
    internal SectionPackingResult AddSectionFromBlocks(
        ICollection<ContextPackageSection> sections,
        ISet<string> packageSourceRefs,
        string name,
        int priority,
        IEnumerable<string> contentBlocks,
        ContextContentFormat contentFormat,
        IReadOnlyList<string> sectionSourceRefs,
        IReadOnlyList<string> sectionItemRefs,
        IReadOnlyList<string> candidateIds,
        int tokenBudget,
        int sectionTokenBudget,
        TokenEstimationContext tokenContext,
        ref int estimatedTokens)
    {
        var remainingBudget = tokenBudget - estimatedTokens;
        if (remainingBudget <= 0)
        {
            return SectionPackingResult.Dropped("token budget exhausted");
        }

        if (sectionTokenBudget > 0)
        {
            remainingBudget = Math.Min(remainingBudget, sectionTokenBudget);
        }

        var builder = new StringBuilder();
        var approxTokens = 0;
        var truncated = false;
        var hasContent = false;
        var separatorTokens = _estimateTokens("\n\n", tokenContext);

        foreach (var block in contentBlocks)
        {
            if (string.IsNullOrWhiteSpace(block))
            {
                continue;
            }

            var blockTokens = _estimateTokens(block, tokenContext);
            var withSeparator = hasContent ? separatorTokens + blockTokens : blockTokens;

            if (approxTokens + withSeparator > remainingBudget)
            {
                // 预算不足：尝试截断当前 block 的部分内容
                var partialBudget = remainingBudget - approxTokens - (hasContent ? separatorTokens : 0);
                if (partialBudget > 0)
                {
                    var trimmed = TrimToTokenBudget(block, partialBudget, tokenContext);
                    if (!string.IsNullOrWhiteSpace(trimmed))
                    {
                        if (hasContent)
                        {
                            builder.AppendLine();
                            builder.AppendLine();
                        }
                        builder.Append(trimmed);
                        truncated = true;
                        hasContent = true;
                    }
                }
                break;
            }

            if (hasContent)
            {
                builder.AppendLine();
                builder.AppendLine();
            }
            builder.Append(block);
            approxTokens += withSeparator;
            hasContent = true;
        }

        if (!hasContent)
        {
            return SectionPackingResult.Dropped("content is empty");
        }

        var sectionContent = builder.ToString();
        // 最终一次精确 token 估算（替代旧实现的两次估算 + 二分搜索）
        var sectionTokens = _estimateTokens(sectionContent, tokenContext);

        // 安全兜底：若近似值偏差导致仍超预算，对完整内容做一次裁剪
        if (sectionTokens > remainingBudget)
        {
            sectionContent = TrimToTokenBudget(sectionContent, remainingBudget, tokenContext);
            if (string.IsNullOrWhiteSpace(sectionContent))
            {
                return SectionPackingResult.Dropped("token budget exhausted");
            }
            sectionTokens = _estimateTokens(sectionContent, tokenContext);
            truncated = true;
        }

        foreach (var sourceRef in sectionSourceRefs)
        {
            packageSourceRefs.Add(sourceRef);
        }

        sections.Add(new ContextPackageSection
        {
            Name = name,
            Priority = priority,
            Content = sectionContent,
            ContentFormat = contentFormat,
            SourceRefs = sectionSourceRefs,
            ItemRefs = sectionItemRefs
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            EstimatedTokens = sectionTokens
        });

        estimatedTokens += sectionTokens;

        // 精确候选接受/拒绝判定：
        // - Section 被加入 package 时，所有候选均标记为 accepted。
        //   Truncated 标志指示内容是否因 token 预算被裁剪。
        //   裁剪时的精确归属由 AddSectionDecisionsWithDedup 根据 Truncated 标志处理
        //   （仅保留首个新候选，避免低价值候选取代 MustHit 项）。
        // - Section 未被加入时，所有候选均标记为 rejected。
        // 这取代了旧的字符串前缀猜测（7.2），并提供精确的候选 ID 列表（6.2）。
        var validCandidateIds = candidateIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        IReadOnlyList<string> acceptedIds = validCandidateIds;
        IReadOnlyList<string> rejectedIds = Array.Empty<string>();

        return SectionPackingResult.Selected(
            truncated ? "selected and truncated to fit token budget" : "selected for package section",
            sectionTokens,
            truncated,
            acceptedIds,
            rejectedIds);
    }

    /// <summary>
    /// 一次 tokenize 截断到 token 预算内，委托到 tokenizer 的 TruncateForTokenBudget。
    /// 消除旧实现的二分查找中每步重新 tokenize 的 O(L·log L) 开销。
    /// </summary>
    internal string TrimToTokenBudget(
        string content,
        int tokenBudget,
        TokenEstimationContext tokenContext)
    {
        if (tokenBudget <= 0 || string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        if (_estimateTokens(content, tokenContext) <= tokenBudget)
        {
            return content;
        }

        return _truncateForTokenBudget(content, tokenBudget, tokenContext);
    }
}
