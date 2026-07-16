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

        // 诊断 section（evidence/excluded/uncertainties）不携带候选 ID，
        // 此处返回空候选列表；候选级精确归属由 AddSectionFromSegments 处理。
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
    /// 预算感知的 segment 粒度 section 装配：逐段追加，达到预算即停止。
    /// 按 segment 边界截断，直接得到精确的 AcceptedCandidateIds /
    /// PartiallyAcceptedCandidateId / RejectedCandidateIds，无需 AddSectionDecisionsWithDedup 中的启发式猜测。
    /// 当 segments 为空时使用 fallbackContent（如"所有X已在此前去重包含"），此时无候选级归属。
    /// </summary>
    internal SectionPackingResult AddSectionFromSegments(
        ICollection<ContextPackageSection> sections,
        ISet<string> packageSourceRefs,
        string name,
        int priority,
        IReadOnlyList<CandidateSegment> segments,
        string? fallbackContent,
        ContextContentFormat contentFormat,
        IReadOnlyList<string> sectionSourceRefs,
        IReadOnlyList<string> sectionItemRefs,
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

        var acceptedIds = new List<string>();
        var rejectedIds = new List<string>();
        string? partiallyAcceptedId = null;

        if (segments.Count == 0)
        {
            // 无新候选需要格式化：使用 fallback 内容（如"所有X已在此前去重包含"）
            if (!string.IsNullOrWhiteSpace(fallbackContent))
            {
                var fallbackTokens = _estimateTokens(fallbackContent, tokenContext);
                if (fallbackTokens <= remainingBudget)
                {
                    builder.Append(fallbackContent);
                    approxTokens = fallbackTokens;
                    hasContent = true;
                }
                else
                {
                    var trimmed = TrimToTokenBudget(fallbackContent, remainingBudget, tokenContext);
                    if (!string.IsNullOrWhiteSpace(trimmed))
                    {
                        builder.Append(trimmed);
                        approxTokens = _estimateTokens(trimmed, tokenContext);
                        truncated = true;
                        hasContent = true;
                    }
                }
            }
        }
        else
        {
            for (int i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                if (string.IsNullOrWhiteSpace(segment.FormattedText))
                {
                    continue;
                }

                var blockTokens = _estimateTokens(segment.FormattedText, tokenContext);
                var withSeparator = hasContent ? separatorTokens + blockTokens : blockTokens;

                if (approxTokens + withSeparator <= remainingBudget)
                {
                    // 完整保留此 segment
                    if (hasContent)
                    {
                        builder.AppendLine();
                        builder.AppendLine();
                    }
                    builder.Append(segment.FormattedText);
                    approxTokens += withSeparator;
                    hasContent = true;
                    acceptedIds.Add(segment.CandidateId);
                }
                else
                {
                    // 预算不足：尝试截断当前 segment 的部分内容
                    var partialBudget = remainingBudget - approxTokens - (hasContent ? separatorTokens : 0);
                    if (partialBudget > 0)
                    {
                        var trimmed = TrimToTokenBudget(segment.FormattedText, partialBudget, tokenContext);
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
                            partiallyAcceptedId = segment.CandidateId;
                        }
                    }

                    // 当前及后续 segment 全部拒绝
                    for (int j = i; j < segments.Count; j++)
                    {
                        if (segments[j].CandidateId != partiallyAcceptedId)
                        {
                            rejectedIds.Add(segments[j].CandidateId);
                        }
                    }
                    break;
                }
            }
        }

        if (!hasContent)
        {
            return SectionPackingResult.Dropped("content is empty");
        }

        var sectionContent = builder.ToString();
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

        return SectionPackingResult.Selected(
            truncated ? "selected and truncated to fit token budget" : "selected for package section",
            sectionTokens,
            truncated,
            acceptedIds,
            rejectedIds,
            partiallyAcceptedId);
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
