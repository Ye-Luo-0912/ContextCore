namespace ContextCore.Abstractions;

/// <summary>
/// 训练数据导出器接口。
/// </summary>
/// <remarks>
/// 设计原则：
/// 1. 导出器是只读边界：从 <see cref="IUtilityLedgerStore"/> 查询 ledger 条目，
/// 按 model artifact 版本聚合为训练集（feature / label / metadata）。
/// 2. 输出格式为 JSONL（与 LearningFeatureDatasetService 一致），每行一条样本。
/// 3. 导出过程不修改 ledger 状态；可重复执行（幂等）。
/// 4. 生产路径注入 Postgres-backed IUtilityLedgerStore；开发 / 测试路径注入 InMemory 实现。
/// </remarks>
public interface ITrainingDataExporter
{
    /// <summary>
    /// 导出训练数据集。
    /// </summary>
    /// <param name="request">导出请求（过滤条件 + 输出目录 + 可选 model artifact 版本）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>导出结果（含输出路径、样本计数、清单路径）。</returns>
    Task<TrainingDataExportResult> ExportAsync(
        TrainingDataExportRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 训练数据导出请求。
/// </summary>
public sealed record TrainingDataExportRequest
{
    /// <summary>workspace 作用域（必填）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>collection 作用域（可空 = 跨集合导出）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>仅导出 MaterializedAt &gt;= Since 的条目（可空 = 不限制）。</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>仅导出 MaterializedAt &lt;= Until 的条目（可空 = 不限制）。</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>仅导出指定 DecisionId 的条目（可空 = 不限制）。</summary>
    public string? DecisionId { get; init; }

    /// <summary>
    /// 仅导出选中（true）或未选中（false）的条目；null = 全部。
    /// 训练正样本（IsSelected=true）与负样本（IsSelected=false）通常一起导出。
    /// </summary>
    public bool? IsSelected { get; init; }

    /// <summary>
    /// 输出目录（必填）。不存在时自动创建。
    /// 导出文件：<c>{OutputDirectory}/training-data.jsonl</c> + <c>training-data.manifest.json</c>。
    /// </summary>
    public required string OutputDirectory { get; init; }

    /// <summary>
    /// 关联的 ModelArtifactId（可空）。写入 manifest 用于追溯模型版本。
    /// 训练数据按 model artifact 版本切分时必填。
    /// </summary>
    public string? ModelArtifactId { get; init; }

    /// <summary>
    /// 最大导出条目数（默认 0 = 不限制）。
    /// 大规模 ledger 导出时用于限制单次导出规模。
    /// </summary>
    public int Take { get; init; } = 0;
}

/// <summary>
/// 数据集快照报告：Learning 数据可被严格证明"完整、可重建、可追责"。
/// </summary>
/// <remarks>
/// 每个导出对应一个快照：
/// - <see cref="SnapshotId"/> 由输入条件确定性派生（workspace + collection + 时间窗 +
///   model artifact + schema 版本哈希）——相同输入重现相同快照 ID（Reproducibility）；
/// - <see cref="InputEvidenceCount"/> 为过滤前的候选 evidence 总数（输入集规模），
///   <see cref="MaterializedCount"/> 为实际物化导出的样本数，
///   <see cref="CompletenessRatio"/> = 物化 / 输入（完整率报告）；
/// - <see cref="MissingCount"/> = 输入 - 物化（未进入快照的样本数），
///   <see cref="MissingReasons"/> 为样本级拒绝/缺失原因去重列表（Dropped/missing reason）；
/// - <see cref="ContentHash"/> 为数据文件 SHA-256（内容完整性），
///   <see cref="PolicyVersion"/> / <see cref="ModelArtifactId"/> / <see cref="SchemaVersion"/>
///   记录生产该快照的策略 / 模型 / schema 版本（可追责）。
/// </remarks>
public sealed record DatasetSnapshotReport
{
    /// <summary>快照唯一 ID（由输入条件确定性派生；相同输入 → 相同 ID，可重现）。</summary>
    public required string SnapshotId { get; init; }

    /// <summary>导出 schema 版本。</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>快照生产时间（UTC）。</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>workspace 作用域。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>collection 作用域（跨集合时为 null）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>时间范围下界（Since 过滤条件）。</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>时间范围上界（Until 过滤条件）。</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>关联的 ModelArtifactId（未指定时为 null）。</summary>
    public string? ModelArtifactId { get; init; }

    /// <summary>输入 evidence 集规模（过滤前的候选总数；null = 无法确定）。</summary>
    public int? InputEvidenceCount { get; init; }

    /// <summary>实际物化导出的样本数。</summary>
    public required int MaterializedCount { get; init; }

    /// <summary>完整率 = 物化 / 输入（0.0-1.0；输入不可确定时为 null）。</summary>
    public double? CompletenessRatio { get; init; }

    /// <summary>缺失（未进入快照）样本数 = 输入 - 物化。</summary>
    public int? MissingCount { get; init; }

    /// <summary>样本级缺失/拒绝原因去重列表（如 dropped-by-threshold / filtered-out）。</summary>
    public IReadOnlyList<string> MissingReasons { get; init; } = [];

    /// <summary>数据文件 SHA-256（内容完整性；与 manifest 一致）。</summary>
    public required string ContentHash { get; init; }

    /// <summary>快照中样本携带的策略版本集合（去重；空 = 样本未记录策略版本）。</summary>
    public IReadOnlyList<string> PolicyVersions { get; init; } = [];

    /// <summary>样本血缘（DecisionId / CandidateItemId / MaterializedAt）覆盖的决策数。</summary>
    public required int LineageDecisionCount { get; init; }
}

/// <summary>
/// 训练数据导出结果。
/// </summary>
public sealed record TrainingDataExportResult
{
    /// <summary>导出时间（UTC）。</summary>
    public required DateTimeOffset ExportedAt { get; init; }

    /// <summary>输出目录（绝对路径）。</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>训练数据 JSONL 文件路径。</summary>
    public required string DataFilePath { get; init; }

    /// <summary>清单文件路径（含 SHA-256 与 model artifact 追溯）。</summary>
    public required string ManifestFilePath { get; init; }

    /// <summary>导出的样本条目数。</summary>
    public required int EntryCount { get; init; }

    /// <summary>导出请求的 workspace 作用域。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>导出请求的 collection 作用域（跨集合时为 null）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>关联的 ModelArtifactId（未指定时为 null）。</summary>
    public string? ModelArtifactId { get; init; }

    /// <summary>导出文件 SHA-256 哈希（用于校验文件完整性）。</summary>
    public required string Sha256Hash { get; init; }

    /// <summary>导出 schema 版本（用于下游消费者识别格式）。</summary>
    public string SchemaVersion { get; init; } = "training-data-export/v1";

    /// <summary>
    /// 数据集快照报告（完整性 / 血缘 / 可重现性；null = 未启用快照能力）。
    /// </summary>
    public DatasetSnapshotReport? DatasetSnapshot { get; init; }
}

/// <summary>
/// 训练数据样本记录（JSONL 每行一条）。
/// </summary>
/// <remarks>
/// 字段分类对齐 ML 训练流水线：
/// - feature: 模型推理输入特征（DeterministicScore / ModelScore / UtilityContribution / Expert）
/// - label: 训练标签（IsSelected 二分类 + DropReasonCode 拒绝原因）
/// - metadata: 追溯与分组信息（DecisionId / CandidateItemId / 作用域 / 时间戳 / PolicyVersion）
/// </remarks>
public sealed record TrainingDataRecord
{
    // --- feature ---
    /// <summary>特征：确定性分数（来自 IUtilityScorer）。</summary>
    public double DeterministicScore { get; init; }

    /// <summary>特征：模型推理分数（null 表示模型未启用或推理失败）。</summary>
    public double? ModelScore { get; init; }

    /// <summary>特征：Expert 对该候选的 utility 贡献比例（0.0-1.0）。</summary>
    public double UtilityContribution { get; init; }

    /// <summary>特征：召回 Expert 类型（枚举字符串，用于 embedding 或 one-hot）。</summary>
    public string Expert { get; init; } = string.Empty;

    // --- label ---
    /// <summary>标签：是否被选中进入最终上下文（二分类正样本 = true）。</summary>
    public bool IsSelected { get; init; }

    /// <summary>标签：拒绝原因码（null = 选中；非空 = 被拒绝原因）。</summary>
    public string? DropReasonCode { get; init; }

    // --- metadata ---
    /// <summary>追溯：决策 ID（用于按决策分组训练 / 评估）。</summary>
    public string DecisionId { get; init; } = string.Empty;

    /// <summary>追溯：候选条目 ID。</summary>
    public string CandidateItemId { get; init; } = string.Empty;

    /// <summary>追溯：workspace 作用域。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>追溯：collection 作用域。</summary>
    public string CollectionId { get; init; } = string.Empty;

    /// <summary>追溯：物化时间（UTC）。</summary>
    public DateTimeOffset MaterializedAt { get; init; }

    /// <summary>追溯：策略版本。</summary>
    public string PolicyVersion { get; init; } = string.Empty;
}

/// <summary>
/// 训练数据导出清单（sidecar JSON，含校验信息）。
/// </summary>
public sealed record TrainingDataExportManifest
{
    /// <summary>导出时间（UTC）。</summary>
    public required DateTimeOffset ExportedAt { get; init; }

    /// <summary>导出 schema 版本。</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>导出的 workspace 作用域。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>导出的 collection 作用域（跨集合时为 null）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>时间范围下界（Since 过滤条件）。</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>时间范围上界（Until 过滤条件）。</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>关联的 ModelArtifactId（未指定时为 null）。</summary>
    public string? ModelArtifactId { get; init; }

    /// <summary>导出的样本条目数。</summary>
    public required int EntryCount { get; init; }

    /// <summary>导出文件 SHA-256 哈希。</summary>
    public required string Sha256Hash { get; init; }

    /// <summary>导出文件名（相对于 OutputDirectory）。</summary>
    public required string DataFileName { get; init; }

    /// <summary>数据集快照 ID（由输入条件确定性派生；相同输入 → 相同 ID，可重现）。</summary>
    public string? SnapshotId { get; init; }

    /// <summary>输入 evidence 集规模（过滤前的候选总数；null = 无法确定）。</summary>
    public int? InputEvidenceCount { get; init; }

    /// <summary>完整率 = 物化样本数 / 输入 evidence 数（0.0-1.0）。</summary>
    public double? CompletenessRatio { get; init; }

    /// <summary>样本级缺失/拒绝原因去重列表。</summary>
    public IReadOnlyList<string> MissingReasons { get; init; } = [];

    /// <summary>快照中样本携带的策略版本集合（去重）。</summary>
    public IReadOnlyList<string> PolicyVersions { get; init; } = [];

    /// <summary>样本血缘覆盖的决策数。</summary>
    public int? LineageDecisionCount { get; init; }
}
