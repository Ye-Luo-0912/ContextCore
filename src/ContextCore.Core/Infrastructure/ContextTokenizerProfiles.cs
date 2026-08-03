using System.Text;
using ContextCore.Abstractions;

namespace ContextCore.Core;

/// <summary>
/// 脚本分类纯函数：按 CJK rune 占比判定内容是否 CJK 主导。
/// 复用 <see cref="UnicodeAwareContextTokenizer.IsCjkOrEastAsianRune"/> 的脚本判定，
/// 避免画像解析器与 tokenizer 维护两份分类逻辑。
/// </summary>
public static class CjkScriptClassifier
{
    /// <summary>
    /// 计算内容中 CJK rune 占比（0～1）。空白 rune 不计入分母；空内容返回 0。
    /// </summary>
    public static double ComputeCjkRatio(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return 0;
        }

        var total = 0;
        var cjk = 0;
        foreach (var rune in content.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                continue;
            }
            total++;
            if (UnicodeAwareContextTokenizer.IsCjkOrEastAsianRune(rune))
            {
                cjk++;
            }
        }
        return total == 0 ? 0 : (double)cjk / total;
    }

    /// <summary>判定内容是否 CJK 主导（占比 ≥ 阈值，默认 0.2）。</summary>
    public static bool IsCjkDominant(string? content, double threshold = 0.2)
    {
        return ComputeCjkRatio(content) >= threshold;
    }
}

/// <summary>
/// 默认 Tokenizer 画像解析器。
/// <para>
/// 注册两个画像：cjk-v1（中文/CJK，推荐 unicode-cjk-v1 估算器）与 latin-v1（拉丁/默认）。
/// <see cref="ResolveForContent"/> 复用 <see cref="CjkScriptClassifier"/> 做内容脚本分类；
/// 未知模型或未显式指定画像时使用 latin-v1（与 resolver 的默认回退行为一致）。
/// </para>
/// </summary>
public sealed class DefaultTokenizerProfileResolver : ITokenizerProfileResolver
{
    /// <summary>CJK 画像 ID。</summary>
    public const string CjkProfileId = "cjk-v1";

    /// <summary>拉丁/默认画像 ID。</summary>
    public const string LatinProfileId = "latin-v1";

    /// <summary>默认 CJK 主导判定阈值（与 unicode-cjk-v1 的 CJK 语义对齐）。</summary>
    public const double DefaultCjkDominanceThreshold = 0.2;

    private readonly IReadOnlyList<TokenizerProfile> _profiles;
    private readonly TokenizerProfile _default;

    public DefaultTokenizerProfileResolver()
    {
        _profiles =
        [
            new TokenizerProfile
            {
                ProfileId = CjkProfileId,
                DisplayName = "中文 / CJK",
                Description = "中日韩脚本主导内容，推荐 unicode-cjk-v1 估算器（单字符计数 + Latin run 近似 BPE）。",
                TokenizerName = "unicode-cjk-v1",
                LanguageCategory = "cjk",
                CjkDominanceThreshold = DefaultCjkDominanceThreshold
            },
            new TokenizerProfile
            {
                ProfileId = LatinProfileId,
                DisplayName = "拉丁 / 默认",
                Description = "拉丁脚本或混合内容，使用兼容估算器。",
                TokenizerName = "openai-cl100k-compatible-v1",
                LanguageCategory = "latin",
                CjkDominanceThreshold = DefaultCjkDominanceThreshold
            }
        ];
        _default = _profiles.First(p => p.ProfileId == LatinProfileId);
    }

    /// <inheritdoc />
    public TokenizerProfile Resolve(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return _default;
        }
        return _profiles.FirstOrDefault(p => p.ProfileId == profileId, _default);
    }

    /// <inheritdoc />
    public IReadOnlyList<TokenizerProfile> GetAll() => _profiles;

    /// <inheritdoc />
    public TokenizerProfile ResolveForContent(string? content)
    {
        return CjkScriptClassifier.IsCjkDominant(content, DefaultCjkDominanceThreshold)
            ? Resolve(CjkProfileId)
            : _default;
    }
}
