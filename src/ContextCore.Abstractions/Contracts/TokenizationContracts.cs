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

/// <summary>上下文 Tokenizer，负责按模型或兼容模型族估算文本 Token 数。</summary>
public interface IContextTokenizer
{
    /// <summary>估算器名称。</summary>
    string Name { get; }

    /// <summary>判断当前 tokenizer 是否适用于指定模型。</summary>
    bool SupportsModel(string? modelName);

    /// <summary>估算文本 Token 数量。</summary>
    ContextTokenEstimate Estimate(string? content, string? modelName = null);
}

/// <summary>根据模型名称选择具体 tokenizer，并在失败时回退到粗略估算。</summary>
public interface IContextTokenizerResolver
{
    /// <summary>根据模型名称解析 tokenizer。</summary>
    IContextTokenizer Resolve(string? modelName);

    /// <summary>估算文本 Token 数量。</summary>
    ContextTokenEstimate Estimate(string? content, string? modelName = null);
}

/// <summary>上下文包中记录 Token 估算来源的元数据键名。</summary>
public static class ContextTokenizationMetadataKeys
{
    public const string Source = "tokenEstimate.source";

    public const string Model = "tokenEstimate.model";

    public const string IsFallback = "tokenEstimate.isFallback";
}
