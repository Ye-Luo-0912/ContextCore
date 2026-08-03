namespace ContextCore.Abstractions;

/// <summary>
/// 校准数据导出器接口。
/// </summary>
/// <remarks>
/// 设计原则（对齐校准数据导出目标）：
/// 1. 导出器是只读边界：从 <see cref="IUtilityLedgerStore"/> 查询 ledger 条目，
/// 按 model artifact 版本聚合为校准集（predicted / observed / weight）。
/// 2. 校准数据用于拟合 Platt / Temperature / Isotonic 校准参数：
/// - predicted = <see cref="UtilityLedgerEntry.ModelScore"/>（模型原始推理分数）
/// - observed = <see cref="UtilityLedgerEntry.IsSelected"/>（二分类实际结果）
/// - weight = <see cref="UtilityLedgerEntry.UtilityContribution"/>（Expert 贡献权重，可选）
/// 3. 输出格式为 JSONL（与 TrainingDataExporter 一致），每行一条样本。
/// 4. 仅导出 <see cref="UtilityLedgerEntry.ModelScore"/> 非 null 的条目（无模型分数无法用于校准）。
/// 5. 导出过程不修改 ledger 状态；可重复执行（幂等）。
/// 6. 生产路径注入 Postgres-backed IUtilityLedgerStore；开发 / 测试路径注入 InMemory 实现。
/// </remarks>
public interface ICalibrationDataExporter
{
    /// <summary>
    /// 导出校准数据集。
    /// </summary>
    /// <param name="request">导出请求（过滤条件 + 输出目录 + 可选 model artifact 版本）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>导出结果（含输出路径、样本计数、正负样本比例、清单路径）。</returns>
    Task<CalibrationDataExportResult> ExportAsync(
        CalibrationDataExportRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 校准数据导出请求。
/// </summary>
public sealed record CalibrationDataExportRequest
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
    /// 输出目录（必填）。不存在时自动创建。
    /// 导出文件：<c>{OutputDirectory}/calibration-data.jsonl</c> + <c>calibration-data.manifest.json</c>。
    /// </summary>
    public required string OutputDirectory { get; init; }

    /// <summary>
    /// 关联的 ModelArtifactId（可空）。写入 manifest 用于追溯模型版本。
    /// 校准数据按 model artifact 版本切分时必填。
    /// </summary>
    public string? ModelArtifactId { get; init; }

    /// <summary>
    /// 关联的 ModelName（可空）。用于按模型名过滤 ledger 条目（与 ModelArtifactId 二选一或同时指定）。
    /// 若指定，仅导出 ModelScore 非 null 的条目（校准必须有模型预测分数）。
    /// </summary>
    public string? ModelName { get; init; }

    /// <summary>
    /// 是否仅导出 ModelScore 非 null 的条目（默认 true）。
    /// 校准数据集必须包含模型预测分数；关闭此选项仅用于诊断/审计目的。
    /// </summary>
    public bool RequireModelScore { get; init; } = true;

    /// <summary>
    /// 最大导出条目数（默认 0 = 不限制）。
    /// 大规模 ledger 导出时用于限制单次导出规模。
    /// </summary>
    public int Take { get; init; } = 0;
}

/// <summary>
/// 校准数据导出结果。
/// </summary>
public sealed record CalibrationDataExportResult
{
    /// <summary>导出时间（UTC）。</summary>
    public required DateTimeOffset ExportedAt { get; init; }

    /// <summary>输出目录（绝对路径）。</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>校准数据 JSONL 文件路径。</summary>
    public required string DataFilePath { get; init; }

    /// <summary>清单文件路径（含 SHA-256 与 model artifact 追溯）。</summary>
    public required string ManifestFilePath { get; init; }

    /// <summary>导出的样本条目数。</summary>
    public required int EntryCount { get; init; }

    /// <summary>正样本数（IsSelected=true）。</summary>
    public required int PositiveCount { get; init; }

    /// <summary>负样本数（IsSelected=false）。</summary>
    public required int NegativeCount { get; init; }

    /// <summary>正样本比例（PositiveCount / EntryCount；空集时为 0）。</summary>
    public required double PositiveRatio { get; init; }

    /// <summary>导出请求的 workspace 作用域。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>导出请求的 collection 作用域（跨集合时为 null）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>关联的 ModelArtifactId（未指定时为 null）。</summary>
    public string? ModelArtifactId { get; init; }

    /// <summary>关联的 ModelName（未指定时为 null）。</summary>
    public string? ModelName { get; init; }

    /// <summary>导出文件 SHA-256 哈希（用于校验文件完整性）。</summary>
    public required string Sha256Hash { get; init; }

    /// <summary>导出 schema 版本（用于下游消费者识别格式）。</summary>
    public string SchemaVersion { get; init; } = "calibration-data-export/v1";
}

/// <summary>
/// 校准数据样本记录（JSONL 每行一条）。
/// </summary>
/// <remarks>
/// 字段分类对齐 ML 校准流水线（Platt / Temperature / Isotonic）：
/// - predicted: 模型原始推理分数（用于拟合校准函数的输入）
/// - observed : 实际结果（二分类标签，用于拟合校准函数的目标）
/// - weight : 样本权重（默认 1.0；可使用 UtilityContribution 加权）
/// - metadata : 追溯与分组信息（用于按决策/模型版本分组校准）
/// </remarks>
public sealed record CalibrationDataRecord
{
    // --- predicted ---

    /// <summary>预测：模型原始推理分数（校准函数输入；null 表示模型未启用，但 RequireModelScore=true 时不会出现）。</summary>
    public double? ModelScore { get; init; }

    /// <summary>预测：确定性基线分数（不依赖模型；用于参考与对比）。</summary>
    public double DeterministicScore { get; init; }

    /// <summary>预测：最终聚合分数（融合后；用于评估校准后整体性能）。</summary>
    public double FinalScore { get; init; }

    // --- observed ---

    /// <summary>观测：是否被选入最终上下文（二分类实际结果；true=正样本）。</summary>
    public bool IsSelected { get; init; }

    /// <summary>观测：拒绝原因码（null = 选中；非空 = 被拒绝原因，可用于分层校准）。</summary>
    public string? DropReasonCode { get; init; }

    // --- weight ---

    /// <summary>样本权重（默认 1.0；可使用 UtilityContribution 反映 Expert 贡献度）。</summary>
    public double Weight { get; init; } = 1.0;

    // --- metadata ---

    /// <summary>追溯：决策 ID（用于按决策分组校准）。</summary>
    public string DecisionId { get; init; } = string.Empty;

    /// <summary>追溯：候选条目 ID。</summary>
    public string CandidateItemId { get; init; } = string.Empty;

    /// <summary>追溯：workspace 作用域。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>追溯：collection 作用域。</summary>
    public string CollectionId { get; init; } = string.Empty;

    /// <summary>追溯：召回 Expert 类型（枚举字符串，用于按 Expert 分层校准）。</summary>
    public string Expert { get; init; } = string.Empty;

    /// <summary>追溯：物化时间（UTC）。</summary>
    public DateTimeOffset MaterializedAt { get; init; }

    /// <summary>追溯：策略版本。</summary>
    public string PolicyVersion { get; init; } = string.Empty;
}

/// <summary>
/// 校准数据导出清单（sidecar JSON，含校验信息与正负样本统计）。
/// </summary>
public sealed record CalibrationDataExportManifest
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

    /// <summary>关联的 ModelName（未指定时为 null）。</summary>
    public string? ModelName { get; init; }

    /// <summary>导出的样本条目数。</summary>
    public required int EntryCount { get; init; }

    /// <summary>正样本数（IsSelected=true）。</summary>
    public required int PositiveCount { get; init; }

    /// <summary>负样本数（IsSelected=false）。</summary>
    public required int NegativeCount { get; init; }

    /// <summary>正样本比例。</summary>
    public required double PositiveRatio { get; init; }

    /// <summary>导出文件 SHA-256 哈希。</summary>
    public required string Sha256Hash { get; init; }

    /// <summary>导出文件名（相对于 OutputDirectory）。</summary>
    public required string DataFileName { get; init; }
}
