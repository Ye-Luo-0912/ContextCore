namespace ContextCore.Abstractions.Models;

/// <summary>压缩任务的操作类型。</summary>
public enum CompressionTaskKind
{
    /// <summary>摘要提取。</summary>
    Summarize,
    /// <summary>内容缩减。</summary>
    Reduce,
    /// <summary>关键信息抽取。</summary>
    Extract,
    /// <summary>标准化处理。</summary>
    Normalize,
    /// <summary>合并多条内容。</summary>
    Merge,
    /// <summary>刷新已有摘要。</summary>
    Refresh,
    /// <summary>重建索引。</summary>
    RebuildIndex,
    /// <summary>验证内容质量。</summary>
    Validate,
    /// <summary>自定义任务。</summary>
    Custom
}

/// <summary>压缩处理的深度等级。</summary>
public enum CompressionDepth
{
    /// <summary>轻度压缩，保留较多原始信息。</summary>
    Light,
    /// <summary>常规压缩。</summary>
    Normal,
    /// <summary>深度压缩，大幅缩减 Token。</summary>
    Deep,
    /// <summary>审计级别，会记录所有变更细节。</summary>
    Audit
}

/// <summary>压缩任务的执行结果状态。</summary>
public enum CompressionStatus
{
    /// <summary>完全成功。</summary>
    Succeeded,
    /// <summary>部分成功，存在警告。</summary>
    PartiallySucceeded,
    /// <summary>失败。</summary>
    Failed,
    /// <summary>需要人工审核。</summary>
    RequiresReview
}

/// <summary>发起压缩操作的请求参数。</summary>
public sealed class CompressionRequest
{
    /// <summary>操作唯一标识符，用于关联响应。</summary>
    public string OperationId { get; init; } = string.Empty;

    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>所属集合 ID。</summary>
    public string CollectionId { get; init; } = string.Empty;

    /// <summary>压缩任务类型。</summary>
    public CompressionTaskKind TaskKind { get; init; } = CompressionTaskKind.Summarize;

    /// <summary>子类型（可选，用于自定义任务的进一步区分）。</summary>
    public string? SubKind { get; init; }

    /// <summary>待压缩的输入条目列表。</summary>
    public IReadOnlyList<ContextItem> Inputs { get; init; } = Array.Empty<ContextItem>();

    /// <summary>压缩行为配置。</summary>
    public CompressionOptions Options { get; init; } = new();

    /// <summary>附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>扩展载荷（JSON 格式，可选）。</summary>
    public string? ExtensionPayloadJson { get; init; }
}

/// <summary>压缩行为的细粒度控制选项。</summary>
public sealed class CompressionOptions
{
    /// <summary>压缩深度。</summary>
    public CompressionDepth Depth { get; init; } = CompressionDepth.Normal;

    /// <summary>目标 Token 预算上限（可选）。</summary>
    public int? TargetTokenBudget { get; init; }

    /// <summary>思考模式：fast、balanced、deep、audit；为空时由 Depth 自动推导。</summary>
    public string? ThinkingMode { get; init; }

    /// <summary>是否生成索引提示。</summary>
    public bool GenerateIndexHints { get; init; }

    /// <summary>是否在输出中保留来源引用。</summary>
    public bool PreserveSourceRefs { get; init; } = true;

    /// <summary>是否将生成结果合并回存储。</summary>
    public bool MergeIntoStore { get; init; }

    /// <summary>指定使用的模型角色（可选）。</summary>
    public string? ModelRole { get; init; }
}

/// <summary>
/// 单次 LLM 压缩调用的执行跟踪记录（§6.1 质量指标 + §6.2 来源证据绑定）。
/// 供调试、可观测性和人工复核使用，不影响主响应字段的语义。
/// </summary>
public sealed class CompressionTrace
{
    /// <summary>实际调用的模型名称（来自 ModelResponse.ModelName）。</summary>
    public string ModelName { get; init; } = string.Empty;

    /// <summary>模型提供商名称（来自 ModelResponse.Provider）。</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>是否使用了回退模型（FallbackUsed）。</summary>
    public bool FallbackUsed { get; init; }

    /// <summary>端到端调用延迟（毫秒，包含提示词构建和结果解析）。</summary>
    public long LatencyMs { get; init; }

    /// <summary>压缩提示词版本（用于追踪 prompt 迭代）。</summary>
    public string PromptVersion { get; init; } = string.Empty;

    /// <summary>本次压缩所依据的来源条目 ID 列表（§6.2 source chunk ids 证据绑定）。</summary>
    public IReadOnlyList<string> SourceItemIds { get; init; } = Array.Empty<string>();

    /// <summary>模型是否返回了无效 JSON。</summary>
    public bool InvalidJsonReturned { get; init; }

    /// <summary>结果是否未通过 schema 校验。</summary>
    public bool SchemaValidationFailed { get; init; }

    /// <summary>是否需要人工复核（来自 QualityReport.RequiresReview）。</summary>
    public bool RequiresReview { get; init; }

    /// <summary>综合质量分（来自 QualityReport，0–1）。</summary>
    public double QualityScore { get; init; }

    /// <summary>压缩输出结构的 schema 版本（用于追踪结构化输出格式迭代）。</summary>
    public string SchemaVersion { get; init; } = string.Empty;

    /// <summary>本次压缩的重试次数（0 表示首次成功，不含重试）。</summary>
    public int RetryCount { get; init; }

    /// <summary>本次压缩是否因超时而失败或降级。</summary>
    public bool TimedOut { get; init; }

    /// <summary>基于模型 token 价格估算的本次压缩成本（美元）。</summary>
    public double EstimatedCost { get; init; }

    /// <summary>来源内容的 SHA-256 哈希（用于验证来源未被篡改，§6.2 证据绑定）。</summary>
    public string SourceHash { get; init; } = string.Empty;

    /// <summary>来源条目的最大版本号（可选，§6.2 证据绑定）。</summary>
    public int? SourceVersion { get; init; }

    /// <summary>生成此压缩结果的模型与提示词版本组合（如 "gpt-4o/cc-compress-v1"）。</summary>
    public string GeneratedBy { get; init; } = string.Empty;

    /// <summary>审核状态：pending（待审核）、approved（已通过）、rejected（已拒绝）。</summary>
    public string ReviewStatus { get; init; } = "approved";

    /// <summary>跟踪记录生成时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>压缩输出与来源内容的绑定记录（§6.2 压缩输出证据绑定）。</summary>
public sealed class CompressionEvidenceBinding
{
    /// <summary>来源条目 ID 列表。</summary>
    public IReadOnlyList<string> SourceChunkIds { get; init; } = Array.Empty<string>();

    /// <summary>来源内容的 SHA-256 哈希（用于验证来源未被篡改）。</summary>
    public string SourceHash { get; init; } = string.Empty;

    /// <summary>来源条目的最大版本号（可选）。</summary>
    public int? SourceVersion { get; init; }

    /// <summary>压缩结果生成时间。</summary>
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>生成此结果的模型与 prompt 版本组合。</summary>
    public string GeneratedBy { get; init; } = string.Empty;

    /// <summary>结果置信度（0–1）。</summary>
    public double Confidence { get; init; }

    /// <summary>审核状态：pending/approved/rejected。</summary>
    public string ReviewStatus { get; init; } = "pending";
}

/// <summary>压缩操作的响应结果。</summary>
public sealed class CompressionResponse
{
    /// <summary>对应请求的操作 ID。</summary>
    public string OperationId { get; init; } = string.Empty;

    /// <summary>执行结果状态。</summary>
    public CompressionStatus Status { get; init; } = CompressionStatus.Succeeded;

    /// <summary>压缩后生成的新条目列表。</summary>
    public IReadOnlyList<ContextItem> GeneratedItems { get; init; } = Array.Empty<ContextItem>();

    /// <summary>建议对现有条目进行的补丁操作列表。</summary>
    public IReadOnlyList<ContextPatch> Patches { get; init; } = Array.Empty<ContextPatch>();

    /// <summary>建议写入索引的条目列表。</summary>
    public IReadOnlyList<ContextIndexEntry> IndexHints { get; init; } = Array.Empty<ContextIndexEntry>();

    /// <summary>执行过程中产生的警告。</summary>
    public IReadOnlyList<ContextWarning> Warnings { get; init; } = Array.Empty<ContextWarning>();

    /// <summary>执行过程中发生的错误。</summary>
    public IReadOnlyList<ContextError> Errors { get; init; } = Array.Empty<ContextError>();

    /// <summary>Token 与模型调用用量统计。</summary>
    public ContextOperationUsage Usage { get; init; } = new();

    /// <summary>本次压缩结果的质量评估报告。</summary>
    public CompressionQualityReport? QualityReport { get; init; }

    /// <summary>本次 LLM 调用的执行跟踪记录（model profile、延迟、来源证据等）。</summary>
    public CompressionTrace? Trace { get; init; }

    /// <summary>压缩输出与来源内容的证据绑定记录（§6.2）。</summary>
    public CompressionEvidenceBinding? EvidenceBinding { get; init; }

    /// <summary>响应创建时间。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>操作完成时间（可选）。</summary>
    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>压缩质量报告，用于判断压缩结果是否足够完整、可用以及是否需要人工复核。</summary>
public sealed class CompressionQualityReport
{
    /// <summary>对应的压缩操作 ID。</summary>
    public string OperationId { get; init; } = string.Empty;

    /// <summary>被评估的生成条目 ID。</summary>
    public string GeneratedItemId { get; init; } = string.Empty;

    /// <summary>完整性评分，范围 0-1。</summary>
    public double CompletenessScore { get; init; }

    /// <summary>一致性评分，范围 0-1。</summary>
    public double ConsistencyScore { get; init; }

    /// <summary>可用性评分，范围 0-1。</summary>
    public double UsabilityScore { get; init; }

    /// <summary>输出 Token / 输入 Token，越低代表压缩越强。</summary>
    public double CompressionRatio { get; init; }

    /// <summary>风险评分，范围 0-1。</summary>
    public double RiskScore { get; init; }

    /// <summary>是否需要人工复核。</summary>
    public bool RequiresReview { get; init; }

    /// <summary>输入 Token 数。</summary>
    public int InputTokens { get; init; }

    /// <summary>输出 Token 数。</summary>
    public int OutputTokens { get; init; }

    /// <summary>压缩结果状态。</summary>
    public CompressionStatus Status { get; init; } = CompressionStatus.Succeeded;

    /// <summary>参与评分的主要信号。</summary>
    public IReadOnlyList<string> Signals { get; init; } = Array.Empty<string>();

    /// <summary>报告生成时间。</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>操作过程中产生的非致命性警告信息。</summary>
public sealed class ContextWarning
{
    /// <summary>警告代码。</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>警告描述。</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>操作失败时的错误信息。</summary>
public sealed class ContextError
{
    /// <summary>错误代码。</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>错误描述。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>详细错误信息（可选）。</summary>
    public string? Detail { get; init; }
}

/// <summary>记录本次操作消耗的 Token 和模型调用次数。</summary>
public sealed class ContextOperationUsage
{
    /// <summary>输入 Token 数量。</summary>
    public int InputTokens { get; init; }

    /// <summary>输出 Token 数量。</summary>
    public int OutputTokens { get; init; }

    /// <summary>模型调用次数。</summary>
    public int ModelCalls { get; init; }

    /// <summary>基于模型 token 价格估算的本次操作成本（美元）。</summary>
    public double EstimatedCost { get; init; }
}

/// <summary>上下文压缩器接口，负责将原始条目压缩为更精简的表示。</summary>
public interface IContextCompressor
{
    /// <summary>执行压缩操作并返回结果。</summary>
    Task<CompressionResponse> CompressAsync(
        CompressionRequest request,
        CancellationToken cancellationToken = default);
}
