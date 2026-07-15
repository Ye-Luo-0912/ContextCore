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

    internal SectionAssembler(Func<string?, TokenEstimationContext, int> estimateTokens)
    {
        _estimateTokens = estimateTokens;
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

        var remainingBudget = tokenBudget - estimatedTokens;
        if (remainingBudget <= 0)
        {
            return SectionPackingResult.Dropped("token budget exhausted");
        }

        if (sectionTokenBudget > 0)
        {
            remainingBudget = Math.Min(remainingBudget, sectionTokenBudget);
        }

        var sectionContent = content;
        var sectionTokens = _estimateTokens(sectionContent, tokenContext);
        var truncated = false;
        if (sectionTokens > remainingBudget)
        {
            sectionContent = TrimToTokenBudget(sectionContent, remainingBudget, tokenContext);
            if (string.IsNullOrWhiteSpace(sectionContent))
            {
                return SectionPackingResult.Dropped("token budget exhausted");
            }

            sectionTokens = _estimateTokens(sectionContent, tokenContext);
            if (sectionTokens > remainingBudget)
            {
                return SectionPackingResult.Dropped("token budget exhausted");
            }

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

        var low = 0;
        var high = content.Length;
        var best = 0;
        while (low <= high)
        {
            var middle = AlignToScalarBoundary(content, (low + high) / 2);
            var candidate = middle <= 0 ? string.Empty : content[..middle];
            var candidateTokens = _estimateTokens(candidate, tokenContext);
            if (candidateTokens <= tokenBudget)
            {
                best = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return best <= 0 ? string.Empty : content[..best].TrimEnd();
    }

    private static int AlignToScalarBoundary(string content, int length)
    {
        if (length <= 0 || length >= content.Length)
        {
            return Math.Clamp(length, 0, content.Length);
        }

        return char.IsHighSurrogate(content[length - 1]) ? length - 1 : length;
    }
}
