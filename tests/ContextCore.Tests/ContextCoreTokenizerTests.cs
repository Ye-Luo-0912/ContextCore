using ContextCore.Abstractions;
using ContextCore.Core;

namespace ContextCore.Tests;

/// <summary>
/// P0 4.3 边界测试：验证 Legacy / Unicode tokenizer 的截断公式在预算边界处
/// 既不超预算也不系统性浪费 budget。
/// 不变量：Estimate(TruncatedContent) <= budget，且保留字符数为满足该不等式的最大值。
/// </summary>
[TestClass]
[TestCategory("Tokenizer")]
public sealed class ContextCoreTokenizerTests
{
    // ── LegacyCharacterTokenizer ───────────────────────────────────────────
    // 估算 tokens = Max(1, (length+1)/2)；预算 B 下最大可保留 length = 2B。

    [TestMethod]
    public void Legacy_Budget1_BoundaryLength2_NotTruncated()
    {
        var tokenizer = new LegacyCharacterTokenizer();
        var content = "ab"; // length=2, tokens=Max(1,3/2)=1
        var result = tokenizer.TruncateForTokenBudget(content, tokenBudget: 1);

        Assert.IsFalse(result.WasTruncated);
        Assert.AreEqual(2, result.TruncatedContent.Length);
        Assert.AreEqual("ab", result.TruncatedContent);
        Assert.IsTrue(result.TokenCount <= 1);
    }

    [TestMethod]
    public void Legacy_Budget1_Length3_TruncatesKeeps2()
    {
        var tokenizer = new LegacyCharacterTokenizer();
        var content = "abc"; // length=3, tokens=Max(1,4/2)=2 > 1
        var result = tokenizer.TruncateForTokenBudget(content, tokenBudget: 1);

        Assert.IsTrue(result.WasTruncated);
        Assert.AreEqual(2, result.TruncatedContent.Length);
        Assert.AreEqual("ab", result.TruncatedContent);
        Assert.AreEqual(1, result.TokenCount);
    }

    [TestMethod]
    public void Legacy_Budget2_BoundaryLength4_NotTruncated()
    {
        var tokenizer = new LegacyCharacterTokenizer();
        var content = "abcd"; // length=4, tokens=Max(1,5/2)=2
        var result = tokenizer.TruncateForTokenBudget(content, tokenBudget: 2);

        Assert.IsFalse(result.WasTruncated);
        Assert.AreEqual(4, result.TruncatedContent.Length);
        Assert.IsTrue(result.TokenCount <= 2);
    }

    [TestMethod]
    public void Legacy_Budget2_Length5_TruncatesKeeps4()
    {
        var tokenizer = new LegacyCharacterTokenizer();
        var content = "abcde"; // length=5, tokens=Max(1,6/2)=3 > 2
        var result = tokenizer.TruncateForTokenBudget(content, tokenBudget: 2);

        Assert.IsTrue(result.WasTruncated);
        Assert.AreEqual(4, result.TruncatedContent.Length);
        Assert.IsTrue(result.TokenCount <= 2);
    }

    [TestMethod]
    public void Legacy_Budget10_BoundaryLength20_NotTruncated()
    {
        var tokenizer = new LegacyCharacterTokenizer();
        var content = new string('a', 20); // tokens=Max(1,21/2)=10
        var result = tokenizer.TruncateForTokenBudget(content, tokenBudget: 10);

        Assert.IsFalse(result.WasTruncated);
        Assert.AreEqual(20, result.TruncatedContent.Length);
        Assert.AreEqual(10, result.TokenCount);
    }

    [TestMethod]
    public void Legacy_Budget10_Length21_TruncatesKeeps20()
    {
        var tokenizer = new LegacyCharacterTokenizer();
        var content = new string('a', 21); // tokens=Max(1,22/2)=11 > 10
        var result = tokenizer.TruncateForTokenBudget(content, tokenBudget: 10);

        Assert.IsTrue(result.WasTruncated);
        Assert.AreEqual(20, result.TruncatedContent.Length);
        Assert.IsTrue(result.TokenCount <= 10);
    }

    [TestMethod]
    public void Legacy_CJK_Budget2_KeepsFourChars()
    {
        var tokenizer = new LegacyCharacterTokenizer();
        var content = "中文字符"; // length=4 (BMP CJK 各 1 UTF-16 unit), tokens=Max(1,5/2)=2
        var result = tokenizer.TruncateForTokenBudget(content, tokenBudget: 2);

        Assert.IsFalse(result.WasTruncated);
        Assert.AreEqual(4, result.TruncatedContent.Length);
        Assert.IsTrue(result.TokenCount <= 2);
    }

    [TestMethod]
    public void Legacy_SurrogatePair_Budget1_KeepsOneEmojiIntact()
    {
        var tokenizer = new LegacyCharacterTokenizer();
        var content = "😀😀"; // 2 emojis = 4 UTF-16 units, tokens=Max(1,5/2)=2 > 1
        var result = tokenizer.TruncateForTokenBudget(content, tokenBudget: 1);

        Assert.IsTrue(result.WasTruncated);
        // 保留 1 个 emoji = 2 UTF-16 units，不得切断代理对
        Assert.AreEqual(2, result.TruncatedContent.Length);
        Assert.AreEqual("😀", result.TruncatedContent);
        Assert.AreEqual(1, result.TokenCount);
    }

    // ── UnicodeAwareContextTokenizer (Latin run) ──────────────────────────
    // 估算 tokens = Max(1, ceil(n/4))；预算 remaining 下最大可保留 n = 4*remaining。

    [TestMethod]
    public void Unicode_LatinRun_Budget1_Length8_TruncatesKeeps4()
    {
        var tokenizer = CreateUnicodeTokenizer();
        var content = "abcdefgh"; // 单个 latin run, tokens=ceil(8/4)=2 > 1
        var result = tokenizer.TruncateForTokenBudget(content, tokenBudget: 1);

        Assert.IsTrue(result.WasTruncated);
        Assert.AreEqual(4, result.TruncatedContent.Length);
        Assert.AreEqual("abcd", result.TruncatedContent);
        Assert.AreEqual(1, result.TokenCount);
    }

    [TestMethod]
    public void Unicode_LatinRun_Budget2_BoundaryLength8_NotTruncated()
    {
        var tokenizer = CreateUnicodeTokenizer();
        var content = "abcdefgh"; // tokens=ceil(8/4)=2
        var result = tokenizer.TruncateForTokenBudget(content, tokenBudget: 2);

        Assert.IsFalse(result.WasTruncated);
        Assert.AreEqual(8, result.TruncatedContent.Length);
        Assert.AreEqual(2, result.TokenCount);
    }

    [TestMethod]
    public void Unicode_LatinRun_Budget2_Length9_TruncatesKeeps8()
    {
        var tokenizer = CreateUnicodeTokenizer();
        var content = "abcdefghi"; // tokens=ceil(9/4)=3 > 2
        var result = tokenizer.TruncateForTokenBudget(content, tokenBudget: 2);

        Assert.IsTrue(result.WasTruncated);
        Assert.AreEqual(8, result.TruncatedContent.Length);
        Assert.AreEqual("abcdefgh", result.TruncatedContent);
        Assert.IsTrue(result.TokenCount <= 2);
    }

    [TestMethod]
    public void Unicode_CJK_Budget2_TruncatesKeeps2Runes()
    {
        var tokenizer = CreateUnicodeTokenizer();
        var content = "中文字符"; // 4 CJK runes, each 1 token → 4 tokens > 2
        var result = tokenizer.TruncateForTokenBudget(content, tokenBudget: 2);

        Assert.IsTrue(result.WasTruncated);
        Assert.AreEqual(2, result.TruncatedContent.Length); // 2 BMP CJK chars
        Assert.AreEqual("中文", result.TruncatedContent);
        Assert.IsTrue(result.TokenCount <= 2);
    }

    [TestMethod]
    public void Unicode_SurrogatePair_Budget1_KeepsOneEmojiRune()
    {
        var tokenizer = CreateUnicodeTokenizer();
        var content = "😀😀"; // 2 runes, each 1 token → 2 tokens > 1
        var result = tokenizer.TruncateForTokenBudget(content, tokenBudget: 1);

        Assert.IsTrue(result.WasTruncated);
        // 保留 1 个 rune = 2 UTF-16 units（不得切断代理对）
        Assert.AreEqual(2, result.TruncatedContent.Length);
        Assert.AreEqual("😀", result.TruncatedContent);
        Assert.AreEqual(1, result.TokenCount);
    }

    [TestMethod]
    public void Unicode_NeverExceedsBudget_AcrossMixedContent()
    {
        var tokenizer = CreateUnicodeTokenizer();
        // 混合 ASCII run + CJK + 空格 + emoji
        var content = "hello 世界 😀abcd 中文字符";

        for (var budget = 1; budget <= 10; budget++)
        {
            var result = tokenizer.TruncateForTokenBudget(content, tokenBudget: budget);
            Assert.IsTrue(result.TokenCount <= budget,
                $"budget={budget}: TokenCount {result.TokenCount} exceeds budget");
        }
    }

    // R12.4A #3: Tokenizer 截断公式 — Latin run flush 后 safeLength 必须推进。
    // 旧 bug：非 ASCII rune 前的 latin run 成功 flush 并计入 count 后，若该 rune 超预算 goto done，
    // safeLength 未更新导致 latin run 被系统性丢弃（返回空串而非已 flush 的 latin 文本）。

    [TestMethod]
    public void Unicode_LatinRunFollowedByCJK_Budget1_RetainsLatinRun()
    {
        var tokenizer = CreateUnicodeTokenizer();
        // "abc" = 1 token (ceil(3/4)=1)，"世" = 1 token。budget=1 只能容纳 "abc"。
        var content = "abc世";
        var result = tokenizer.TruncateForTokenBudget(content, tokenBudget: 1);

        Assert.IsTrue(result.WasTruncated);
        Assert.AreEqual("abc", result.TruncatedContent,
            $"应保留已 flush 的 latin run 'abc'，实际 '{result.TruncatedContent}'");
        Assert.AreEqual(1, result.TokenCount);
    }

    [TestMethod]
    public void Unicode_LatinRunFollowedByCJK_Budget2_RetainsLatinAndOneCJK()
    {
        var tokenizer = CreateUnicodeTokenizer();
        // "abcd" = 1 token，"世" = 1 token，"界" = 1 token。budget=2 可容纳 "abcd世"。
        var content = "abcd世界";
        var result = tokenizer.TruncateForTokenBudget(content, tokenBudget: 2);

        Assert.IsTrue(result.WasTruncated);
        Assert.AreEqual("abcd世", result.TruncatedContent,
            $"应保留 latin run 'abcd' + 1 个 CJK rune '世'，实际 '{result.TruncatedContent}'");
        Assert.AreEqual(2, result.TokenCount);
    }

    [TestMethod]
    public void Unicode_LatinRunFollowedByEmoji_Budget1_RetainsLatinRun()
    {
        var tokenizer = CreateUnicodeTokenizer();
        // "abc" = 1 token，😀 = 1 token (surrogate pair)。budget=1 只能容纳 "abc"。
        var content = "abc😀";
        var result = tokenizer.TruncateForTokenBudget(content, tokenBudget: 1);

        Assert.IsTrue(result.WasTruncated);
        Assert.AreEqual("abc", result.TruncatedContent,
            $"应保留 latin run 'abc'（emoji 超预算被丢弃），实际 '{result.TruncatedContent}'");
        Assert.AreEqual(1, result.TokenCount);
    }

    [TestMethod]
    public void Unicode_MixedLatinCJK_RetainsMaximumContentWithinBudget()
    {
        var tokenizer = CreateUnicodeTokenizer();
        // "hello" = 2 tokens，"世" = 1，"界" = 1。budget=3 可容纳 "hello世"（3 tokens）。
        var content = "hello世界";
        var result = tokenizer.TruncateForTokenBudget(content, tokenBudget: 3);

        Assert.IsTrue(result.WasTruncated);
        StringAssert.StartsWith(result.TruncatedContent, "hello",
            "latin run 'hello' 必须被保留（2 tokens 已 flush）");
        Assert.IsTrue(result.TokenCount <= 3);
        Assert.IsTrue(result.TruncatedContent.Length >= 5,
            $"至少应保留 5 字符（'hello'），实际 {result.TruncatedContent.Length} 字符");
    }

    [TestMethod]
    public void Legacy_NeverExceedsBudget_AcrossLengths()
    {
        var tokenizer = new LegacyCharacterTokenizer();
        var content = "0123456789ABCDEF";

        for (var budget = 1; budget <= 10; budget++)
        {
            var result = tokenizer.TruncateForTokenBudget(content, tokenBudget: budget);
            Assert.IsTrue(result.TokenCount <= budget,
                $"budget={budget}: TokenCount {result.TokenCount} exceeds budget");
        }
    }

    private static UnicodeAwareContextTokenizer CreateUnicodeTokenizer()
        => new("test-unicode", [], supportsUnknownModel: true);
}
