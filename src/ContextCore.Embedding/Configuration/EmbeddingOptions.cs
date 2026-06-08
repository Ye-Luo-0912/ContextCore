namespace ContextCore.Embedding;

/// <summary>ONNX embedding 输出池化策略。</summary>
public enum EmbeddingPoolingStrategy
{
    /// <summary>对有效 token 做平均池化。</summary>
    Mean,

    /// <summary>使用第一个 CLS token 表示整段文本。</summary>
    Cls
}

/// <summary>Embedding 提供商运行配置。</summary>
public sealed class EmbeddingOptions
{
    /// <summary>默认模型名称。</summary>
    public string ModelName { get; init; } = EmbeddingModelPaths.DefaultModelName;

    /// <summary>输出向量维度；小于等于 0 时，ONNX provider 会优先从 config.json 推断。</summary>
    public int Dimensions { get; init; }

    /// <summary>每批最多处理的输入数量。</summary>
    public int MaxBatchSize { get; init; } = 32;

    /// <summary>是否启用 contentHash 缓存。</summary>
    public bool EnableContentHashCache { get; init; } = true;

    /// <summary>是否默认对向量做单位化。</summary>
    public bool Normalize { get; init; } = true;

    /// <summary>ONNX 模型文件路径。</summary>
    public string? ModelPath { get; init; }

    /// <summary>ONNX 模型对应的 BERT WordPiece 词表路径。</summary>
    public string? VocabularyPath { get; init; }

    /// <summary>单条文本最大 token 长度，超过后截断。</summary>
    public int MaxSequenceLength { get; init; } = 256;

    /// <summary>tokenizer 是否按 uncased BERT 规则转小写；未配置时会从 tokenizer_config.json 推断。</summary>
    public bool? TokenizerLowercase { get; init; }

    /// <summary>ONNX 输出池化策略；未配置时会按模型名称推断。</summary>
    public EmbeddingPoolingStrategy? PoolingStrategy { get; init; }

    /// <summary>模型存在 token_type_ids 输入时是否传入全零 segment。</summary>
    public bool UseTokenTypeIds { get; init; } = true;

    /// <summary>ONNX Runtime 单算子线程数，0 表示使用运行时默认值。</summary>
    public int OnnxIntraOpNumThreads { get; init; }

    /// <summary>ONNX Runtime 算子间线程数，0 表示使用运行时默认值。</summary>
    public int OnnxInterOpNumThreads { get; init; }

    /// <summary>空闲后卸载模型的时间。</summary>
    public TimeSpan IdleUnloadAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Query 向量生成时附加到输入文本前的指令前缀。
    /// 仅对 <see cref="EmbeddingInputKind.Query"/> 生效；为空则不附加。
    /// bge-small-zh-v1.5 推荐设置为：「为这个句子生成表示以用于检索相关文章：」
    /// </summary>
    public string QueryInstruction { get; init; } = string.Empty;
}

/// <summary>bge 系列中文模型的默认 query instruction 常量。</summary>
public static class BgeQueryInstructions
{
    /// <summary>bge-small/base/large-zh-v1.5 检索任务 query instruction。</summary>
    public const string BgeZhV15 = "为这个句子生成表示以用于检索相关文章：";
}
