namespace ContextCore.Evaluation.Quality;

/// <summary>
/// 评测集覆盖维度（固定标签）。数据集构建器要求全部维度至少有一条样本。
/// </summary>
public static class EvalCoverageDimensions
{
    /// <summary>精确实体与关键词（精确词元可命中）。</summary>
    public const string ExactKeyword = "exact-keyword";

    /// <summary>同义改写、语义匹配与 hard negatives（无共享关键词但语义等价；干扰项共享词元但不是答案）。</summary>
    public const string SemanticHardNegative = "semantic-hard-negative";

    /// <summary>多问句、工具观察与忘掉再找回（多条子问句、工具输出证据、跨轮遗忘后需重新召回）。</summary>
    public const string MultiQueryTool = "multi-query-tool";

    /// <summary>生命周期、时效性、排除与权限边界（过期/已删除/越权证据不应出现）。</summary>
    public const string LifecyclePermission = "lifecycle-permission";

    /// <summary>图关系、多跳证据与证据冲突（关系链多跳；相互矛盾的证据只取正确一条）。</summary>
    public const string GraphEvidence = "graph-evidence";

    /// <summary>FileSystem/Postgres/InMemory provider parity（同一查询各存储通道应一致命中）。</summary>
    public const string ProviderParity = "provider-parity";

    /// <summary>全部固定维度，构建器的覆盖门按此清单校验。</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ExactKeyword,
        SemanticHardNegative,
        MultiQueryTool,
        LifecyclePermission,
        GraphEvidence,
        ProviderParity
    ];
}

/// <summary>
/// 声明文件中的一条样本（数据集构建器的输入）。
/// 与质量指标契约对齐：证据按 Required / Relevant / Forbidden 声明。
/// </summary>
public sealed class DeclaredEvalSample
{
    /// <summary>样本唯一标识（跨版本稳定，用于确定性划分 train/dev/test）。</summary>
    public required string SampleId { get; init; }

    /// <summary>查询文本。</summary>
    public required string Query { get; init; }

    /// <summary>来源（声明文件路径或批次、标注人）。</summary>
    public required string Source { get; init; }

    /// <summary>标注理由（为什么这些证据是期望/禁止的）。</summary>
    public required string AnnotationReason { get; init; }

    /// <summary>期望证据（质量契约）。</summary>
    public required QualityEvidenceExpectation Evidence { get; init; }

    /// <summary>覆盖维度标签（必须是非空子集，且至少覆盖全部固定维度之一）。</summary>
    public IReadOnlyList<string> CoverageDimensions { get; init; } = Array.Empty<string>();

    /// <summary>附加元数据（如 corpus 关联、provider 列表、时间语义）。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>
/// 版本化数据集中的一条样本（构建器输出，写入 split jsonl）。
/// 每条记录携带来源、期望证据、标注理由和版本，满足可追溯要求。
/// </summary>
public sealed class VersionedEvalSample
{
    public string SampleId { get; init; } = string.Empty;

    /// <summary>所属数据集版本（如 "v1"）。</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>划分：train / dev / test。</summary>
    public string Split { get; init; } = string.Empty;

    public string Query { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string AnnotationReason { get; init; } = string.Empty;

    public QualityEvidenceExpectation Evidence { get; init; } = new();

    public IReadOnlyList<string> CoverageDimensions { get; init; } = Array.Empty<string>();

    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>
/// 数据集版本清单（机器契约，写入 dataset.json）。
/// 版本不可变：同一版本已存在时构建失败，除非显式 --force。
/// </summary>
public sealed class EvalDatasetManifest
{
    /// <summary>数据集版本（"v1" 等）。</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>清单/样本结构 schema 版本。</summary>
    public string SchemaVersion { get; init; } = "1";

    /// <summary>划分哈希算法。</summary>
    public string HashAlgorithm { get; init; } = "sha256";

    /// <summary>train / dev / test 比例（整数百分比，和为 100）。</summary>
    public int TrainRatio { get; init; } = 70;

    public int DevRatio { get; init; } = 15;

    public int TestRatio { get; init; } = 15;

    /// <summary>样本总数。</summary>
    public int SampleCount { get; init; }

    /// <summary>各划分样本数（train/dev/test）。</summary>
    public Dictionary<string, int> SplitCounts { get; init; } = new();

    /// <summary>各覆盖维度样本数。</summary>
    public Dictionary<string, int> CoverageCounts { get; init; } = new();

    /// <summary>全部固定维度是否都有样本（覆盖门结果）。</summary>
    public bool CoverageComplete { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>数据集校验结果（dataset-verify / 构建时校验）。</summary>
public sealed class EvalDatasetVerifyResult
{
    public bool Ok { get; init; }

    public EvalDatasetManifest? Manifest { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}
