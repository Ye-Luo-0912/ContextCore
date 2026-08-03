using ContextCore.Abstractions.Models;
using ContextCore.Core;

namespace ContextCore.Tests;

/// <summary>
/// + 验收测试：Section 装配的候选归属精度。
/// 6.1: 安全兜底截断后重新计算 accepted/partial/rejected attribution。
/// 6.2: Section SourceRefs/ItemRefs 只从 accepted + partially accepted segments 聚合。
/// </summary>
[TestClass]
[TestCategory("Package")]
public sealed class ContextCoreSectionAssemblerTests
{
    /// <summary>
    /// 构造使用确定性 token 估算器的 SectionAssembler：
    /// 每 2 字符 = 1 token（与 CJK 近似估算一致），截断按字符数 * 2。
    /// </summary>
    private static SectionAssembler CreateAssembler()
    {
        return new SectionAssembler(
            estimateTokens: (text, _) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 1) / 2,
            truncateForTokenBudget: (text, budget, _) =>
            {
                if (budget <= 0 || string.IsNullOrEmpty(text)) return string.Empty;
                var maxChars = budget * 2;
                return text.Length <= maxChars ? text : text[..maxChars];
            });
    }

    /// <summary>
    /// section SourceRefs/ItemRefs 只从 accepted segments 聚合，被拒绝候选的 refs 不应出现。
    /// 构造 3 个 segment，预算仅容纳前 2 个，验证第 3 个的 refs 不在 section.SourceRefs/ItemRefs 中。
    /// </summary>
    [TestMethod]
    public void AddSectionFromSegments_RejectedCandidateRefs_NotInSectionRefs()
    {
        var assembler = CreateAssembler();
        var sections = new List<ContextPackageSection>();
        var packageSourceRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var estimatedTokens = 0;

        var segments = new[]
        {
            new CandidateSegment("item-1", "内容一内容一", new[] { "src-1" }, new[] { "item-1" }),
            new CandidateSegment("item-2", "内容二内容二", new[] { "src-2" }, new[] { "item-2" }),
            new CandidateSegment("item-3", "内容三内容三", new[] { "src-3" }, new[] { "item-3" }),
        };

        var result = assembler.AddSectionFromSegments(
            sections,
            packageSourceRefs,
            name: "working_memory",
            priority: 50,
            segments: segments,
            fallbackContent: null,
            contentFormat: ContextContentFormat.Markdown,
            sectionSourceRefs: new[] { "section-level-ref" },
            sectionItemRefs: new[] { "section-level-item" },
            tokenBudget: 100,
            sectionTokenBudget: 8, // 每个 segment 约 3 token + 分隔符 1 token，容纳 2 个后第 3 个 partialBudget=0 被拒
            tokenContext: new TokenEstimationContext("test", "test", false),
            estimatedTokens: ref estimatedTokens);

        Assert.IsTrue(result.Added, "section 应被加入");
        Assert.IsTrue(result.AcceptedCandidateIds.Contains("item-1"), "item-1 应被接受");
        Assert.IsTrue(result.AcceptedCandidateIds.Contains("item-2"), "item-2 应被接受");
        Assert.IsTrue(result.RejectedCandidateIds.Contains("item-3"), "item-3 应被拒绝");

        var section = sections.Single();
        // section SourceRefs 只包含 accepted segments 的 refs
        CollectionAssert.Contains(section.SourceRefs.ToArray(), "src-1");
        CollectionAssert.Contains(section.SourceRefs.ToArray(), "src-2");
        CollectionAssert.DoesNotContain(section.SourceRefs.ToArray(), "src-3",
            "被拒绝候选的 SourceRef 不应出现在 section.SourceRefs 中");
        CollectionAssert.DoesNotContain(section.SourceRefs.ToArray(), "section-level-ref",
            "section 级 SourceRef 在有 segments 时不应直接写入（应从 segments 聚合）");

        // section ItemRefs 只包含 accepted segments 的 refs
        CollectionAssert.Contains(section.ItemRefs.ToArray(), "item-1");
        CollectionAssert.Contains(section.ItemRefs.ToArray(), "item-2");
        CollectionAssert.DoesNotContain(section.ItemRefs.ToArray(), "item-3",
            "被拒绝候选的 ItemRef 不应出现在 section.ItemRefs 中");
    }

    /// <summary>
    /// 安全兜底截断后重新计算 attribution。
    /// 构造不一致估算器：构建阶段（逐 block）返回偏小值让全部 segment 通过，
    /// 安全兜底（整体估算，含 \n）返回真实值触发截断。
    /// 验证被安全截断切掉尾部的 segment 被降级为 partial，其后的 segment 移入 rejected。
    /// </summary>
    [TestMethod]
    public void AddSectionFromSegments_SafetyTrim_RecomputesAttribution()
    {
        // 估算器：含 \n 的合并内容按 1 char = 1 token（偏大），单个 segment 按 3 char = 1 token（偏小）
        // 截断器：匹配估算器比例
        var assembler = new SectionAssembler(
            estimateTokens: (text, _) =>
            {
                if (string.IsNullOrEmpty(text)) return 0;
                return text.Contains('\n') ? text.Length : text.Length / 3;
            },
            truncateForTokenBudget: (text, budget, _) =>
            {
                if (budget <= 0 || string.IsNullOrEmpty(text)) return string.Empty;
                var maxChars = text.Contains('\n') ? budget : budget * 3;
                return text.Length <= maxChars ? text : text[..maxChars];
            });

        var sections = new List<ContextPackageSection>();
        var packageSourceRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var estimatedTokens = 0;

        var segments = new[]
        {
            new CandidateSegment("item-A", "AAAAAA", new[] { "src-A" }, new[] { "item-A" }),
            new CandidateSegment("item-B", "BBBBBB", new[] { "src-B" }, new[] { "item-B" }),
            new CandidateSegment("item-C", "CCCCCC", new[] { "src-C" }, new[] { "item-C" }),
        };

        var result = assembler.AddSectionFromSegments(
            sections,
            packageSourceRefs,
            name: "working_memory",
            priority: 50,
            segments: segments,
            fallbackContent: null,
            contentFormat: ContextContentFormat.Markdown,
            sectionSourceRefs: Array.Empty<string>(),
            sectionItemRefs: Array.Empty<string>(),
            tokenBudget: 100,
            sectionTokenBudget: 12, // 构建阶段每个 segment 2 token，3 个共 10 token <= 12 全通过；安全估算合并内容 > 12 触发截断
            tokenContext: new TokenEstimationContext("test", "test", false),
            estimatedTokens: ref estimatedTokens);

        Assert.IsTrue(result.Added, "section 应被加入");
        Assert.IsTrue(result.Truncated, "应发生截断");

        // 安全截断后应重新计算 attribution
        // item-A 在截断点之前，应被完整接受
        Assert.IsTrue(result.AcceptedCandidateIds.Contains("item-A"),
            "item-A 在截断点之前，应被完整接受");

        // item-B 被安全截断切掉尾部，应降级为 partial
        Assert.AreEqual("item-B", result.PartiallyAcceptedCandidateId,
            "item-B 尾部被安全截断切掉，应降级为 partial");
        Assert.IsFalse(result.AcceptedCandidateIds.Contains("item-B"),
            "item-B 不应仍在 accepted 列表中");

        // item-C 完全在截断点之后，应被 rejected
        Assert.IsTrue(result.RejectedCandidateIds.Contains("item-C"),
            "item-C 完全在截断点之后，应被 rejected");

        // 安全兜底截断后 PartiallyAcceptedIncludedTokens 必须反映实际保留的 token 数。
        // item-B 的 builder 边界为 start=8（"AAAAAA\n\n" 之后）、end=14（"BBBBBB" 之后）。
        // 安全截断后 contentLength=12，retainedLength = min(14,12) - 8 = 4 → 保留 "BBBB"。
        // estimate("BBBB") = 4/3 = 1（不含 \n，按 length/3 估算）。
        Assert.AreEqual(1, result.PartiallyAcceptedIncludedTokens,
            $"PartiallyAcceptedIncludedTokens 应等于保留子串 'BBBB' 的 token 估算（1），实际 {result.PartiallyAcceptedIncludedTokens}");

        // 安全兜底截断后 section refs 只引用真正进入输出的 segment。
        // item-A（accepted）和 item-B（partially accepted）的 SourceRef 应在 section.SourceRefs 中，
        // item-C（rejected after safety trim）的 SourceRef 不应在 section.SourceRefs 中。
        var section = sections.Single();
        CollectionAssert.Contains(section.SourceRefs.ToArray(), "src-A",
            "accepted segment 的 SourceRef 应在 section.SourceRefs 中");
        CollectionAssert.Contains(section.SourceRefs.ToArray(), "src-B",
            "partially accepted segment 的 SourceRef 应在 section.SourceRefs 中（其内容部分保留进输出）");
        CollectionAssert.DoesNotContain(section.SourceRefs.ToArray(), "src-C",
            "safety trim 后 rejected 的 segment 的 SourceRef 不应在 section.SourceRefs 中");
    }

    /// <summary>
    /// 构建阶段 partial（逐 segment 截断）且无安全兜底截断时，
    /// PartiallyAcceptedIncludedTokens 必须等于构建阶段保留的截断子串的 token 估算。
    /// </summary>
    [TestMethod]
    public void AddSectionFromSegments_BuildPhasePartial_ReportsCorrectIncludedTokens()
    {
        var assembler = CreateAssembler(); // 每 2 字符 = 1 token
        var sections = new List<ContextPackageSection>();
        var packageSourceRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var estimatedTokens = 0;

        var segments = new[]
        {
            new CandidateSegment("item-1", "AAAA", new[] { "src-1" }, new[] { "item-1" }),       // 2 tokens
            new CandidateSegment("item-2", "BBBBBB", new[] { "src-2" }, new[] { "item-2" }),     // 3 tokens
        };

        var result = assembler.AddSectionFromSegments(
            sections,
            packageSourceRefs,
            name: "working_memory",
            priority: 50,
            segments: segments,
            fallbackContent: null,
            contentFormat: ContextContentFormat.Markdown,
            sectionSourceRefs: Array.Empty<string>(),
            sectionItemRefs: Array.Empty<string>(),
            tokenBudget: 100,
            // item-1 (2 token) 通过；item-2 需 1(分隔符)+3=4，总 6 > 4 → partialBudget=1 → 截断到 2 字符 "BB" = 1 token
            sectionTokenBudget: 4,
            tokenContext: new TokenEstimationContext("test", "test", false),
            estimatedTokens: ref estimatedTokens);

        Assert.IsTrue(result.Added, "section 应被加入");
        Assert.IsTrue(result.Truncated, "应发生构建阶段截断");
        Assert.AreEqual("item-2", result.PartiallyAcceptedCandidateId,
            "item-2 在构建阶段被截断，应为 partial");
        // 构建阶段保留 "BB"（2 字符），estimate("BB") = (2+1)/2 = 1
        Assert.AreEqual(1, result.PartiallyAcceptedIncludedTokens,
            $"PartiallyAcceptedIncludedTokens 应等于构建阶段保留子串 'BB' 的 token 估算（1），实际 {result.PartiallyAcceptedIncludedTokens}");
    }

    /// <summary>
    /// 当 segments 为空（fallback 内容）时，section refs 使用传入的 section 级 refs。
    /// </summary>
    [TestMethod]
    public void AddSectionFromSegments_EmptySegments_FallbackContentUsesSectionRefs()
    {
        var assembler = CreateAssembler();
        var sections = new List<ContextPackageSection>();
        var packageSourceRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var estimatedTokens = 0;

        var result = assembler.AddSectionFromSegments(
            sections,
            packageSourceRefs,
            name: "constraints",
            priority: 90,
            segments: Array.Empty<CandidateSegment>(),
            fallbackContent: "所有约束已在此前去重包含。",
            contentFormat: ContextContentFormat.Markdown,
            sectionSourceRefs: new[] { "fallback-ref" },
            sectionItemRefs: new[] { "fallback-item" },
            tokenBudget: 100,
            sectionTokenBudget: 50,
            tokenContext: new TokenEstimationContext("test", "test", false),
            estimatedTokens: ref estimatedTokens);

        Assert.IsTrue(result.Added);
        var section = sections.Single();
        CollectionAssert.Contains(section.SourceRefs.ToArray(), "fallback-ref",
            "fallback 内容应使用 section 级 SourceRefs");
        CollectionAssert.Contains(section.ItemRefs.ToArray(), "fallback-item",
            "fallback 内容应使用 section 级 ItemRefs");
    }

    /// <summary>
    /// packageSourceRefs 也应只从 accepted segments 聚合，被拒绝候选的 ref 不应进入。
    /// </summary>
    [TestMethod]
    public void AddSectionFromSegments_PackageSourceRefs_OnlyFromAcceptedSegments()
    {
        var assembler = CreateAssembler();
        var sections = new List<ContextPackageSection>();
        var packageSourceRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var estimatedTokens = 0;

        var segments = new[]
        {
            new CandidateSegment("item-1", "内容一", new[] { "pkg-src-1" }, new[] { "item-1" }),
            new CandidateSegment("item-2", "内容二内容二内容二", new[] { "pkg-src-2" }, new[] { "item-2" }),
        };

        var result = assembler.AddSectionFromSegments(
            sections,
            packageSourceRefs,
            name: "working_memory",
            priority: 50,
            segments: segments,
            fallbackContent: null,
            contentFormat: ContextContentFormat.Markdown,
            sectionSourceRefs: Array.Empty<string>(),
            sectionItemRefs: Array.Empty<string>(),
            tokenBudget: 100,
            sectionTokenBudget: 3, // item-1 约 2 token 通过，item-2 partialBudget=0 被拒
            tokenContext: new TokenEstimationContext("test", "test", false),
            estimatedTokens: ref estimatedTokens);

        Assert.IsTrue(result.AcceptedCandidateIds.Contains("item-1"));
        Assert.IsTrue(result.RejectedCandidateIds.Contains("item-2"));

        // packageSourceRefs 应只包含 item-1 的 ref，不包含 item-2 的
        CollectionAssert.Contains(packageSourceRefs.ToArray(), "pkg-src-1");
        CollectionAssert.DoesNotContain(packageSourceRefs.ToArray(), "pkg-src-2",
            "被拒绝候选的 SourceRef 不应进入 packageSourceRefs");
    }
}
