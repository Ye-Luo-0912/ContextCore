namespace ContextCore.Abstractions;

/// <summary>
/// Tokenizer 画像：描述一组内容形态（脚本/语言）适用的 tokenizer 与 CJK 判定参数。
/// <para>
/// 检索侧在模型未知或未显式指定 tokenizer 时，可先按内容脚本分类（CJK 主导 vs 拉丁/默认），
/// 再选择对应的画像与 tokenizer，避免对所有内容套用同一估算策略。
/// </para>
/// </summary>
public sealed record TokenizerProfile
{
    /// <summary>画像唯一标识（如 "cjk-v1" / "latin-v1"）。</summary>
    public required string ProfileId { get; init; }

    /// <summary>展示名称。</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>画像描述。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>推荐 tokenizer 名称（与 <see cref="IContextTokenizer.Name"/> 对应，如 "unicode-cjk-v1"）。</summary>
    public string TokenizerName { get; init; } = string.Empty;

    /// <summary>语言类别（"cjk" / "latin"）。</summary>
    public string LanguageCategory { get; init; } = "latin";

    /// <summary>
    /// CJK 主导判定阈值：内容中 CJK rune 占比 ≥ 该值时判定为 CJK 主导。
    /// 供 <see cref="ITokenizerProfileResolver.ResolveForContent"/> 使用。
    /// </summary>
    public double CjkDominanceThreshold { get; init; } = 0.2;
}

/// <summary>
/// 按内容脚本分类选择 <see cref="TokenizerProfile"/> 的解析器。
/// </summary>
public interface ITokenizerProfileResolver
{
    /// <summary>解析画像；未知或空 profileId 时返回默认画像（latin）。</summary>
    TokenizerProfile Resolve(string? profileId);

    /// <summary>返回全部已注册画像（按注册顺序）。</summary>
    IReadOnlyList<TokenizerProfile> GetAll();

    /// <summary>按内容脚本判定 CJK 主导，返回对应画像（CJK 主导 → cjk；否则 → 默认 latin）。</summary>
    TokenizerProfile ResolveForContent(string? content);
}
