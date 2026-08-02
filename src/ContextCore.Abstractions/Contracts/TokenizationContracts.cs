namespace ContextCore.Abstractions;

/// <summary>一次文本 Token 估算结果，包含数量与估算来源。</summary>
public sealed class ContextTokenEstimate
{
    /// <summary>估算出的 Token 数量。</summary>
    public int TokenCount { get; init; }

    /// <summary>估算器来源名称，例如 unicode-cjk-v1 或 legacy-char-half-v1。</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>用于选择 tokenizer 的模型名称，可为空。</summary>
    public string? ModelName { get; init; }

    /// <summary>是否使用了 fallback 粗略估算。</summary>
    public bool IsFallback { get; init; }
}

/// <summary>按 Token 预算截断文本的结果。</summary>
public sealed class TokenTruncationResult
{
    /// <summary>截断后的文本（已 TrimEnd）。</summary>
    public string TruncatedContent { get; init; } = string.Empty;

    /// <summary>截断后文本的实际 Token 数量。</summary>
    public int TokenCount { get; init; }

    /// <summary>是否发生了截断。</summary>
    public bool WasTruncated { get; init; }
}

/// <summary>上下文 Tokenizer，负责按模型或兼容模型族估算文本 Token 数。</summary>
public interface IContextTokenizer
{
    /// <summary>估算器名称。</summary>
    string Name { get; }

    /// <summary>判断当前 tokenizer 是否适用于指定模型。</summary>
    bool SupportsModel(string? modelName);

    /// <summary>估算文本 Token 数量。</summary>
    ContextTokenEstimate Estimate(string? content, string? modelName = null);

    /// <summary>
    /// 一次扫描截断文本到 Token 预算内，避免二分查找中重复 tokenize。
    /// 返回不超过 <paramref name="tokenBudget"/> token 的最大前缀。
    /// </summary>
    TokenTruncationResult TruncateForTokenBudget(string content, int tokenBudget, string? modelName = null);
}

/// <summary>根据模型名称选择具体 tokenizer，并在失败时回退到粗略估算。</summary>
public interface IContextTokenizerResolver
{
    /// <summary>根据模型名称解析 tokenizer。</summary>
    IContextTokenizer Resolve(string? modelName);

    /// <summary>估算文本 Token 数量。</summary>
    ContextTokenEstimate Estimate(string? content, string? modelName = null);

    /// <summary>
    /// 一次扫描截断文本到 Token 预算内，委托到具体 tokenizer 实现。
    /// </summary>
    TokenTruncationResult TruncateForTokenBudget(string content, int tokenBudget, string? modelName = null);
}

/// <summary>上下文包中记录 Token 估算来源的元数据键名。</summary>
public static class ContextTokenizationMetadataKeys
{
    public const string Source = "tokenEstimate.source";

    public const string Model = "tokenEstimate.model";

    public const string IsFallback = "tokenEstimate.isFallback";
}

/// <summary>
/// ContextItem.Metadata 中用于在 Store 与 Provider 之间传递持久化内容指标的键名。
/// 摄取阶段（BasicContextIngestionService）计算 content_hash / content_token_cost 并写入 Metadata，
/// Store 提取到专用列；Provider 读取后跳过在线 SHA-256 + tokenizer 调用。
/// </summary>
public static class ContentMetadataKeys
{
    /// <summary>SHA-256 小写 hex（与 ContextItem.Checksum 一致）。Provider 派生 EntityVersion 时复用。</summary>
    public const string ContentHash = "__content_hash";

    /// <summary>精确 token 数（由 IContextTokenizer 在摄取时计算）。Provider 读取后跳过在线 tokenize。</summary>
    public const string ContentTokenCost = "__content_token_cost";

    /// <summary>Postgres ts_rank_cd 返回的相关度分数 × 100。Lexical Provider 读取后作为 Provider score。</summary>
    public const string TsRank = "__ts_rank";

    /// <summary>内容字节长度（UTF-8）。Store 写入专用列，Provider 读取后用于诊断与回退估算。</summary>
    public const string ContentLength = "__content_length";

    /// <summary>Tokenizer 标识符（如 unicode-cjk-v1）。Store 写入专用列，Provider 读取后验证 tokenizer 一致性。</summary>
    public const string TokenizerId = "__tokenizer_id";

    /// <summary>Tokenizer 版本 / 模型名称。Store 写入专用列，Provider 读取后验证模型兼容性。</summary>
    public const string TokenizerVersion = "__tokenizer_version";

    /// <summary>Token 计算时间（ISO 8601）。Store 写入专用列，用于判断是否需要重新计算。</summary>
    public const string CountedAt = "__counted_at";
}

/// <summary>
/// 内容的 tokenization metadata 中间表示。
/// 由 PostgresStoreBase.ComputeTokenizationMetadata 产出，写入 ContextItem.Metadata（专用列）。
/// SHA-256 总是计算（无外部依赖）；token_count 在 tokenizer 可用时计算，否则为 0。
/// </summary>
public sealed class TokenizationMetadata
{
    /// <summary>内容的 SHA-256 小写 hex（与 ContentMetadataKeys.ContentHash 一致）。</summary>
    public string ContentHash { get; init; } = string.Empty;

    /// <summary>内容字节长度（UTF-8）。</summary>
    public int ContentLength { get; init; }

    /// <summary>Tokenizer 标识符（如 unicode-cjk-v1）；tokenizer 不可用时为 null。</summary>
    public string? TokenizerId { get; init; }

    /// <summary>Tokenizer 版本 / 模型名称；tokenizer 不可用时为 null。</summary>
    public string? TokenizerVersion { get; init; }

    /// <summary>精确 token 数（tokenizer 可用时）；不可用时为 0。</summary>
    public int TokenCount { get; init; }

    /// <summary>Token 计算时间（UTC）；tokenizer 不可用时为 null。</summary>
    public DateTimeOffset? CountedAt { get; init; }
}
