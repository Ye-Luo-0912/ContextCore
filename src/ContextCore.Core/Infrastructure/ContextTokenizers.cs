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
}
