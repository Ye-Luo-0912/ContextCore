using System.Text;
using ContextCore.Abstractions;

namespace ContextCore.Core;

/// <summary>保留旧版“字符数 / 2”的粗略估算器，用作 fallback。</summary>
public sealed class LegacyCharacterTokenizer : IContextTokenizer
{
    public const string TokenizerName = "legacy-char-half-v1";

    public string Name => TokenizerName;

    public bool SupportsModel(string? modelName)
    {
        return true;
    }

    public ContextTokenEstimate Estimate(string? content, string? modelName = null)
    {
        return new ContextTokenEstimate
        {
            TokenCount = EstimateTokenCount(content),
            Source = Name,
            ModelName = modelName,
            IsFallback = true
        };
    }

    /// <summary>
    /// 字符估算下 token = (length+1)/2，直接反推最大字符长度，O(1) 截断。
    /// </summary>
    public TokenTruncationResult TruncateForTokenBudget(string content, int tokenBudget, string? modelName = null)
    {
        if (tokenBudget <= 0 || string.IsNullOrEmpty(content))
        {
            return new TokenTruncationResult { TruncatedContent = string.Empty, TokenCount = 0, WasTruncated = false };
        }

        var totalTokens = EstimateTokenCount(content);
        if (totalTokens <= tokenBudget)
        {
            return new TokenTruncationResult { TruncatedContent = content, TokenCount = totalTokens, WasTruncated = false };
        }

        // token = Math.Max(1, (length+1)/2) → max_length = tokenBudget * 2 - 1
        var maxLength = tokenBudget * 2 - 1;
        if (maxLength <= 0)
        {
            return new TokenTruncationResult { TruncatedContent = string.Empty, TokenCount = 0, WasTruncated = true };
        }

        // 修正 UTF-16 高代理项边界
        if (maxLength < content.Length && char.IsHighSurrogate(content[maxLength - 1]))
        {
            maxLength--;
        }

        if (maxLength <= 0)
        {
            return new TokenTruncationResult { TruncatedContent = string.Empty, TokenCount = 0, WasTruncated = true };
        }

        var truncated = content[..maxLength].TrimEnd();
        var truncatedTokens = EstimateTokenCount(truncated);
        return new TokenTruncationResult { TruncatedContent = truncated, TokenCount = truncatedTokens, WasTruncated = true };
    }

    public static int EstimateTokenCount(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return 0;
        }

        return Math.Max(1, (content.Length + 1) / 2);
    }
}

/// <summary>
/// 面向中文上下文的 Unicode tokenizer：中日韩字符按单字符计数，拉丁文本按近似 BPE 分块计数。
/// </summary>
public sealed class UnicodeAwareContextTokenizer : IContextTokenizer
{
    private readonly IReadOnlyList<string> _modelHints;
    private readonly bool _supportsUnknownModel;

    public UnicodeAwareContextTokenizer(
        string name,
        IReadOnlyList<string> modelHints,
        bool supportsUnknownModel = false)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "unicode-cjk-v1" : name;
        _modelHints = modelHints;
        _supportsUnknownModel = supportsUnknownModel;
    }

    public string Name { get; }

    public bool SupportsModel(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return _supportsUnknownModel;
        }

        return _modelHints.Any(hint =>
            modelName.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    public ContextTokenEstimate Estimate(string? content, string? modelName = null)
    {
        return new ContextTokenEstimate
        {
            TokenCount = EstimateTokenCount(content),
            Source = Name,
            ModelName = modelName,
            IsFallback = false
        };
    }

    /// <summary>
    /// 一次 rune 遍历增量计算 token，超过预算即停。O(n) 截断，消除二分重算。
    /// Latin run 按 4 字符批量计 token，超预算时反推可保留字符数。
    /// </summary>
    public TokenTruncationResult TruncateForTokenBudget(string content, int tokenBudget, string? modelName = null)
    {
        if (tokenBudget <= 0 || string.IsNullOrEmpty(content))
        {
            return new TokenTruncationResult { TruncatedContent = string.Empty, TokenCount = 0, WasTruncated = false };
        }

        var count = 0;
        var latinRunLength = 0;
        var latinRunStart = 0; // latin run 起始的 UTF-16 index
        var safeLength = 0; // 最后一个 token 数 <= budget 的 UTF-16 长度
        var charIndex = 0;

        foreach (var rune in content.EnumerateRunes())
        {
            var runeLen = rune.Utf16SequenceLength;

            if (Rune.IsWhiteSpace(rune))
            {
                if (latinRunLength > 0)
                {
                    if (!TryFlushLatin(ref count, ref latinRunLength, tokenBudget))
                    {
                        safeLength = TruncateLatinRun(content, latinRunStart, latinRunLength, count, tokenBudget, out var partialTokens);
                        count = partialTokens;
                        goto done;
                    }
                    latinRunLength = 0;
                }
                safeLength = charIndex + runeLen;
                charIndex += runeLen;
                continue;
            }

            if (IsAsciiWordRune(rune))
            {
                if (latinRunLength == 0) latinRunStart = charIndex;
                latinRunLength++;
                charIndex += runeLen;
                continue;
            }

            // 非 ASCII rune：先 flush latin run
            if (latinRunLength > 0)
            {
                if (!TryFlushLatin(ref count, ref latinRunLength, tokenBudget))
                {
                    safeLength = TruncateLatinRun(content, latinRunStart, latinRunLength, count, tokenBudget, out var partialTokens);
                    count = partialTokens;
                    goto done;
                }
                latinRunLength = 0;
            }

            if (count + 1 > tokenBudget) goto done;
            count += 1;
            safeLength = charIndex + runeLen;
            charIndex += runeLen;
        }

        // flush 末尾 latin run
        if (latinRunLength > 0)
        {
            if (!TryFlushLatin(ref count, ref latinRunLength, tokenBudget))
            {
                safeLength = TruncateLatinRun(content, latinRunStart, latinRunLength, count, tokenBudget, out var partialTokens);
                count = partialTokens;
                goto done;
            }
            safeLength = content.Length;
        }

        return new TokenTruncationResult
        {
            TruncatedContent = content,
            TokenCount = Math.Max(1, count),
            WasTruncated = false
        };

    done:
        if (safeLength <= 0)
        {
            return new TokenTruncationResult { TruncatedContent = string.Empty, TokenCount = 0, WasTruncated = true };
        }
        var truncated = content[..safeLength].TrimEnd();
        var truncatedTokens = string.IsNullOrEmpty(truncated) ? 0 : Math.Max(1, count);
        return new TokenTruncationResult { TruncatedContent = truncated, TokenCount = truncatedTokens, WasTruncated = true };
    }

    /// <summary>尝试 flush latin run 的 token，返回是否在预算内。成功时 count 已更新、latinRunLength 清零。</summary>
    private static bool TryFlushLatin(ref int count, ref int latinRunLength, int tokenBudget)
    {
        var add = Math.Max(1, (latinRunLength + 3) / 4);
        if (count + add > tokenBudget) return false;
        count += add;
        latinRunLength = 0;
        return true;
    }

    /// <summary>Latin run 超预算时，反推可保留的最大字符数，返回截断后的 UTF-16 长度。</summary>
    private static int TruncateLatinRun(string content, int runStart, int runLength, int currentCount, int tokenBudget, out int partialTokens)
    {
        var remaining = tokenBudget - currentCount;
        if (remaining <= 0)
        {
            partialTokens = currentCount;
            return runStart; // 不保留任何 latin 字符
        }
        // (n + 3) / 4 <= remaining → n <= remaining * 4 - 3
        var maxLatin = remaining * 4 - 3;
        if (maxLatin <= 0)
        {
            partialTokens = currentCount;
            return runStart;
        }
        var kept = Math.Min(runLength, maxLatin);
        partialTokens = currentCount + Math.Max(1, (kept + 3) / 4);
        return runStart + kept;
    }

    private static int EstimateTokenCount(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return 0;
        }

        var count = 0;
        var latinRunLength = 0;

        foreach (var rune in content.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                FlushLatinRun(ref count, ref latinRunLength);
                continue;
            }

            if (IsAsciiWordRune(rune))
            {
                latinRunLength++;
                continue;
            }

            FlushLatinRun(ref count, ref latinRunLength);
            count += EstimateNonAsciiRune(rune);
        }

        FlushLatinRun(ref count, ref latinRunLength);
        return Math.Max(1, count);
    }

    private static bool IsAsciiWordRune(Rune rune)
    {
        return rune.Value is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_'
            or '-';
    }

    private static int EstimateNonAsciiRune(Rune rune)
    {
        // 中日韩、假名、谚文和常见符号在主流 BPE tokenizer 中通常接近单 Token。
        return IsCjkOrEastAsianRune(rune) ? 1 : 1;
    }

    private static bool IsCjkOrEastAsianRune(Rune rune)
    {
        var value = rune.Value;
        return value is >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF
            or >= 0x3040 and <= 0x30FF
            or >= 0xAC00 and <= 0xD7AF;
    }

    private static void FlushLatinRun(ref int count, ref int latinRunLength)
    {
        if (latinRunLength <= 0)
        {
            return;
        }

        count += Math.Max(1, (latinRunLength + 3) / 4);
        latinRunLength = 0;
    }
}

/// <summary>默认 tokenizer resolver，按模型族选择估算器，异常时回退到旧算法。</summary>
public sealed class DefaultContextTokenizerResolver : IContextTokenizerResolver
{
    private readonly IReadOnlyList<IContextTokenizer> _tokenizers;
    private readonly IContextTokenizer _fallback;

    public DefaultContextTokenizerResolver()
    {
        _fallback = new LegacyCharacterTokenizer();
        _tokenizers =
        [
            new UnicodeAwareContextTokenizer(
                "openai-cl100k-compatible-v1",
                ["gpt", "openai", "o1", "o3", "o4", "o5"]),
            new UnicodeAwareContextTokenizer(
                "deepseek-compatible-v1",
                ["deepseek"]),
            new UnicodeAwareContextTokenizer(
                "qwen-compatible-v1",
                ["qwen", "tongyi"]),
            new UnicodeAwareContextTokenizer(
                "unicode-cjk-v1",
                [],
                supportsUnknownModel: true)
        ];
    }

    public IContextTokenizer Resolve(string? modelName)
    {
        return _tokenizers.FirstOrDefault(tokenizer => tokenizer.SupportsModel(modelName))
            ?? _tokenizers.Last();
    }

    public ContextTokenEstimate Estimate(string? content, string? modelName = null)
    {
        try
        {
            return Resolve(modelName).Estimate(content, modelName);
        }
        catch (Exception)
        {
            return _fallback.Estimate(content, modelName);
        }
    }

    public TokenTruncationResult TruncateForTokenBudget(string content, int tokenBudget, string? modelName = null)
    {
        try
        {
            return Resolve(modelName).TruncateForTokenBudget(content, tokenBudget, modelName);
        }
        catch (Exception)
        {
            return _fallback.TruncateForTokenBudget(content, tokenBudget, modelName);
        }
    }
}
