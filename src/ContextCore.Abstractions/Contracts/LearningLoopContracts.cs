namespace ContextCore.Abstractions;

// ========================================================================================
// — Learning Loop V1 契约（Dataset / Model Registry / Canary / Rollback）
//
// 硬边界（来自 project memory）：
// - 学习闭环第一条：PromotionJudge 验证训练与部署基础设施（最小作用域）
// - 不使用单一质量总分决定上线；逐条 ExpectedGain + RollbackCondition 评估
// - Token budget / section quota / duplicate suppression 导致的 dropped 不能被当作不相关负样本
// → DatasetManifest 显式记录 PositiveLabels / NegativeLabels / UnlabeledItems 三类计数
// - 数据集 split 必须支持 GroupKeyed 策略以避免跨样本数据泄漏
//
// 本文件仅包含 Abstractions 层契约，不含任何实现逻辑。
// 实现层（InMemoryDatasetStore / InMemoryModelRegistry 等）将在后续阶段提供。
// ========================================================================================

// ---------- Dataset ----------

/// <summary>
/// 数据集 split 策略：决定 train/validation/test 的划分方式。
/// </summary>
/// <remarks>
/// <b>GroupKeyed</b> 是默认推荐策略：相同 group_key 的样本必须落在同一 split，
/// 防止同一 workspace/collection/session 的样本跨 split 导致训练-验证数据泄漏。
/// </remarks>
public enum DatasetSplitStrategy
{
    /// <summary>随机划分（适用于无相关性假设的样本）。</summary>
    Random,

    /// <summary>分层划分（按标签比例分层，适用于类别不平衡数据）。</summary>
    Stratified,

    /// <summary>时间序列划分（train 在前，validation/test 在后，适用于时间敏感样本）。</summary>
    TimeSeries,

    /// <summary>Group-keyed 划分（相同 group_key 的样本必须落在同一 split，推荐默认）。</summary>
    GroupKeyed
}

/// <summary>数据集审核状态：决定数据集是否可用于训练 / canary。</summary>
public enum DatasetReviewStatus
{
    /// <summary>未审核：刚从 runtime evidence 采集，尚未人工或自动审核。</summary>
    Unreviewed,

    /// <summary>自动审核通过：自动规则检查通过（如样本数、标签分布、特征完整性）。</summary>
    AutoReviewed,

    /// <summary>人工审核完成：标注员已确认标签与特征质量。</summary>
    HumanReviewed,

    /// <summary>已批准：可用于训练与 canary。</summary>
    Approved,

    /// <summary>已拒绝：标签质量不足或存在偏差，不可使用。</summary>
    Rejected
}

/// <summary>数据集来源类型：决定 provenance metadata 的解读方式。</summary>
public enum DatasetProvenance
{
    /// <summary>从运行时证据采集（telemetry sink / trace pipeline）。</summary>
    RuntimeEvidence,

    /// <summary>由人工审核修订（reviewer 调整标签 / 删除噪声样本）。</summary>
    ReviewedByHuman,

    /// <summary>从历史 trace 重放生成（用于离线回放验证）。</summary>
    Replay,

    /// <summary>合成数据（仅用于早期基础设施验证，不可用于生产 canary）。</summary>
    Synthetic
}

/// <summary>特征 schema 版本号：用于追踪特征列与编码方式的演进。</summary>
public sealed record FeatureSchemaVersion(int Major, int Minor)
{
    /// <summary>初始版本（1.0）。</summary>
    public static FeatureSchemaVersion Initial => new(1, 0);

    /// <summary>递增 Minor 版本（新增可选特征列，向后兼容）。</summary>
    public FeatureSchemaVersion BumpMinor() => new(Major, Minor + 1);

    /// <summary>递增 Major 版本（移除或重命名特征列，破坏兼容性）。</summary>
    public FeatureSchemaVersion BumpMajor() => new(Major + 1, 0);

    /// <inheritdoc />
    public override string ToString() => $"v{Major}.{Minor}";
}

/// <summary>数据集版本号：用于追踪同一 datasetId 的迭代版本。</summary>
public sealed record DatasetVersion(int Major, int Minor)
{
    /// <summary>初始版本（1.0）。</summary>
    public static DatasetVersion Initial => new(1, 0);

    /// <summary>递增 Minor 版本（追加样本 / 修正少量标签）。</summary>
    public DatasetVersion BumpMinor() => new(Major, Minor + 1);

    /// <summary>递增 Major 版本（重新 split / 改变 split 策略 / 特征 schema 升级）。</summary>
    public DatasetVersion BumpMajor() => new(Major + 1, 0);

    /// <inheritdoc />
    public override string ToString() => $"v{Major}.{Minor}";
}

/// <summary>
/// 数据集 manifest：记录数据集元信息、split 策略、样本 ID 列表、特征 schema 版本、内容 hash。
/// </summary>
/// <remarks>
/// Manifest 是不可变快照：一旦创建不允许修改样本 ID 列表；要变更 split 必须生成新版本。
/// </remarks>
public sealed record DatasetManifest
{
    /// <summary>构造数据集 manifest。</summary>
    public DatasetManifest(
        string datasetId,
        string name,
        string sourceCorpusDescription,
        int itemCount,
        string hashSha256,
        FeatureSchemaVersion featureSchemaVersion,
        DatasetProvenance provenance,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCorpusDescription);
        ArgumentException.ThrowIfNullOrWhiteSpace(hashSha256);
        ArgumentNullException.ThrowIfNull(featureSchemaVersion);
        if (itemCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(itemCount), "样本数必须 >= 0");
        }
        DatasetId = datasetId;
        Name = name;
        SourceCorpusDescription = sourceCorpusDescription;
        ItemCount = itemCount;
        HashSha256 = hashSha256;
        FeatureSchemaVersion = featureSchemaVersion;
        Provenance = provenance;
        CreatedAt = createdAt;
    }

    /// <summary>数据集 ID（跨版本不变）。</summary>
    public string DatasetId { get; }

    /// <summary>数据集名称（人类可读）。</summary>
    public string Name { get; }

    /// <summary>源语料描述（如 "telemetry:package-build-v2"、"trace:runtime-candidate-v3"）。</summary>
    public string SourceCorpusDescription { get; }

    /// <summary>总样本数。</summary>
    public int ItemCount { get; }

    /// <summary>SHA-256 内容 hash（用于完整性校验，覆盖所有样本的特征+标签）。</summary>
    public string HashSha256 { get; }

    /// <summary>特征 schema 版本（用于模型兼容性检查）。</summary>
    public FeatureSchemaVersion FeatureSchemaVersion { get; }

    /// <summary>数据来源类型。</summary>
    public DatasetProvenance Provenance { get; }

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Split 策略（默认 Random）。</summary>
    public DatasetSplitStrategy SplitStrategy { get; init; } = DatasetSplitStrategy.Random;

    /// <summary>Group key 字段名（仅当 SplitStrategy=GroupKeyed 时必填）。</summary>
    public string? GroupKeyField { get; init; }

    /// <summary>Train 比例 [0, 1]（默认 0.7）。</summary>
    public double TrainRatio { get; init; } = 0.7;

    /// <summary>Validation 比例 [0, 1]（默认 0.15）。</summary>
    public double ValidationRatio { get; init; } = 0.15;

    /// <summary>Test 比例 [0, 1]（默认 0.15）。</summary>
    public double TestRatio { get; init; } = 0.15;

    /// <summary>Train split 样本 ID 列表。</summary>
    public IReadOnlyList<string> TrainItemIds { get; init; } = Array.Empty<string>();

    /// <summary>Validation split 样本 ID 列表。</summary>
    public IReadOnlyList<string> ValidationItemIds { get; init; } = Array.Empty<string>();

    /// <summary>Test split 样本 ID 列表。</summary>
    public IReadOnlyList<string> TestItemIds { get; init; } = Array.Empty<string>();

    /// <summary>审核状态（默认 Unreviewed；Approved 后才可用于训练 / canary）。</summary>
    public DatasetReviewStatus ReviewStatus { get; init; } = DatasetReviewStatus.Unreviewed;

    /// <summary>审核员 ID（仅当 ReviewStatus >= HumanReviewed 时非空）。</summary>
    public string? ReviewerId { get; init; }

    /// <summary>审核时间。</summary>
    public DateTimeOffset? ReviewedAt { get; init; }

    /// <summary>审核备注。</summary>
    public string? ReviewNotes { get; init; }
}

/// <summary>数据集统计信息：标签分布与 split 计数。</summary>
public sealed record DatasetStatistics
{
    /// <summary>构造数据集统计。</summary>
    public DatasetStatistics(
        int totalItems,
        int trainItems,
        int validationItems,
        int testItems,
        int positiveLabels,
        int negativeLabels,
        int unlabeledItems)
    {
        if (totalItems < 0) throw new ArgumentOutOfRangeException(nameof(totalItems));
        if (trainItems < 0) throw new ArgumentOutOfRangeException(nameof(trainItems));
        if (validationItems < 0) throw new ArgumentOutOfRangeException(nameof(validationItems));
        if (testItems < 0) throw new ArgumentOutOfRangeException(nameof(testItems));
        if (positiveLabels < 0) throw new ArgumentOutOfRangeException(nameof(positiveLabels));
        if (negativeLabels < 0) throw new ArgumentOutOfRangeException(nameof(negativeLabels));
        if (unlabeledItems < 0) throw new ArgumentOutOfRangeException(nameof(unlabeledItems));
        TotalItems = totalItems;
        TrainItems = trainItems;
        ValidationItems = validationItems;
        TestItems = testItems;
        PositiveLabels = positiveLabels;
        NegativeLabels = negativeLabels;
        UnlabeledItems = unlabeledItems;
    }

    /// <summary>总样本数。</summary>
    public int TotalItems { get; }

    /// <summary>Train split 样本数。</summary>
    public int TrainItems { get; }

    /// <summary>Validation split 样本数。</summary>
    public int ValidationItems { get; }

    /// <summary>Test split 样本数。</summary>
    public int TestItems { get; }

    /// <summary>正样本数（selected with intent = positive signal）。</summary>
    public int PositiveLabels { get; }

    /// <summary>负样本数（仅 explicit 不相关才计入；token budget / quota 导致的 dropped 不计）。</summary>
    public int NegativeLabels { get; }

    /// <summary>未标注样本数（仅作为参考，不参与监督学习）。</summary>
    public int UnlabeledItems { get; }
}

/// <summary>版本化数据集：manifest + 统计 + 版本号。</summary>
public sealed class VersionedDataset
{
    /// <summary>构造版本化数据集。</summary>
    public VersionedDataset(
        DatasetManifest manifest,
        DatasetVersion version,
        DatasetStatistics statistics,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(statistics);
        Manifest = manifest;
        Version = version;
        Statistics = statistics;
        CreatedAt = createdAt;
    }

    /// <summary>数据集 manifest。</summary>
    public DatasetManifest Manifest { get; }

    /// <summary>数据集版本。</summary>
    public DatasetVersion Version { get; }

    /// <summary>数据集统计信息。</summary>
    public DatasetStatistics Statistics { get; }

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; }
}

// ---------- Model Registry ----------

/// <summary>模型 artifact 状态：决定 artifact 是否可用于 staging / production。</summary>
public enum ModelArtifactStatus
{
    /// <summary>草稿：刚训练完成，尚未验证。</summary>
    Draft,

    /// <summary>已验证：离线指标对比通过，可进入 staging。</summary>
    Validated,

    /// <summary>已 staging：已上传到 model registry，等待 promotion。</summary>
    Staged,

    /// <summary>已激活：在 pipeline 中被晋升为基线。</summary>
    Active,

    /// <summary>已弃用：被新版本替代，但仍可查询。</summary>
    Deprecated,

    /// <summary>已退役：从 registry 中移除（保留元数据）。</summary>
    Retired
}

/// <summary>模型兼容性级别：决定 artifact 是否可热替换 active 模型。</summary>
public enum ModelCompatibilityLevel
{
    /// <summary>破坏性：特征 schema 不兼容或运行时接口变化，必须重新部署。</summary>
    Breaking,

    /// <summary>兼容：特征 schema 向后兼容，可热替换。</summary>
    Compatible,

    /// <summary>仅追加：仅新增可选特征列，旧运行时仍可使用。</summary>
    AdditiveOnly
}

/// <summary>模型 artifact 版本号：Major.Minor，每次训练或微调递增。</summary>
public sealed record ModelArtifactVersion(int Major, int Minor)
{
    /// <summary>初始版本（1.0）。</summary>
    public static ModelArtifactVersion Initial => new(1, 0);

    /// <summary>递增 Minor 版本（微调或重训练，特征 schema 不变）。</summary>
    public ModelArtifactVersion BumpMinor() => new(Major, Minor + 1);

    /// <summary>递增 Major 版本（特征 schema 升级或重新训练）。</summary>
    public ModelArtifactVersion BumpMajor() => new(Major + 1, 0);

    /// <inheritdoc />
    public override string ToString() => $"v{Major}.{Minor}";
}

/// <summary>模型兼容性契约：约束 artifact 与运行时的兼容性。</summary>
public sealed record ModelCompatibilityContract
{
    /// <summary>构造兼容性契约。</summary>
    public ModelCompatibilityContract(
        FeatureSchemaVersion requiredFeatureSchemaVersion,
        ModelCompatibilityLevel compatibilityLevel,
        string? minRuntimeVersion = null,
        string? maxRuntimeVersion = null,
        string? breakingChangeNotes = null)
    {
        ArgumentNullException.ThrowIfNull(requiredFeatureSchemaVersion);
        RequiredFeatureSchemaVersion = requiredFeatureSchemaVersion;
        CompatibilityLevel = compatibilityLevel;
        MinRuntimeVersion = minRuntimeVersion;
        MaxRuntimeVersion = maxRuntimeVersion;
        BreakingChangeNotes = breakingChangeNotes;
    }

    /// <summary>要求的特征 schema 版本（与 DatasetManifest.FeatureSchemaVersion 匹配）。</summary>
    public FeatureSchemaVersion RequiredFeatureSchemaVersion { get; }

    /// <summary>兼容性级别。</summary>
    public ModelCompatibilityLevel CompatibilityLevel { get; }

    /// <summary>最低运行时版本（可选，如 "decision-schema/2.0"）。</summary>
    public string? MinRuntimeVersion { get; }

    /// <summary>最高运行时版本（可选；null 表示无上限）。</summary>
    public string? MaxRuntimeVersion { get; }

    /// <summary>破坏性变更说明（仅当 CompatibilityLevel=Breaking 时非空）。</summary>
    public string? BreakingChangeNotes { get; }
}

/// <summary>模型 artifact：训练输出的不可变快照。</summary>
public sealed record ModelArtifact
{
    /// <summary>构造模型 artifact。</summary>
    public ModelArtifact(
        string modelId,
        ModelArtifactVersion version,
        OptimizationTargetComponent targetCapability,
        string artifactUri,
        DateTimeOffset createdAt,
        ModelArtifactStatus status = ModelArtifactStatus.Draft)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactUri);
        ArgumentNullException.ThrowIfNull(version);
        ModelId = modelId;
        Version = version;
        TargetCapability = targetCapability;
        ArtifactUri = artifactUri;
        CreatedAt = createdAt;
        Status = status;
    }

    /// <summary>模型 ID（跨版本不变）。</summary>
    public string ModelId { get; }

    /// <summary>模型版本。</summary>
    public ModelArtifactVersion Version { get; }

    /// <summary>目标能力（与 OptimizationProposal.TargetComponent 对齐）。</summary>
    public OptimizationTargetComponent TargetCapability { get; }

    /// <summary>Artifact URI（如 "s3://bucket/model.bin" 或 "file://models/router-v1.bin"）。</summary>
    public string ArtifactUri { get; }

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>状态。</summary>
    public ModelArtifactStatus Status { get; init; }

    /// <summary>训练所用数据集 ID（与 VersionedDataset.Manifest.DatasetId 对齐）。</summary>
    public string? TrainedOnDatasetId { get; init; }

    /// <summary>训练所用数据集版本。</summary>
    public DatasetVersion? TrainedOnDatasetVersion { get; init; }

    /// <summary>训练所用特征 schema 版本。</summary>
    public FeatureSchemaVersion? FeatureSchemaVersion { get; init; }

    /// <summary>超参数 JSON（可选，用于复现）。</summary>
    public string? HyperparametersJson { get; init; }

    /// <summary>离线指标 JSON（可选）。</summary>
    public string? MetricsJson { get; init; }

    /// <summary>兼容性契约。</summary>
    public ModelCompatibilityContract? CompatibilityContract { get; init; }

    /// <summary>训练备注（人工备注或自动生成的训练摘要）。</summary>
    public string? TrainingNotes { get; init; }
}

/// <summary>模型 registry 接口：管理 artifact 的注册、查询、状态推进。</summary>
[Obsolete("R28-B: 无实现无使用，将在后续版本移除。")]
public interface IModelRegistry
{
    /// <summary>注册新 artifact。</summary>
    Task<ModelArtifact> RegisterAsync(
        ModelArtifact artifact,
        CancellationToken cancellationToken = default);

    /// <summary>查询指定 modelId + version 的 artifact。</summary>
    Task<ModelArtifact?> GetAsync(
        string modelId,
        ModelArtifactVersion version,
        CancellationToken cancellationToken = default);

    /// <summary>列出 artifact（可按 modelId 过滤）。</summary>
    Task<IReadOnlyList<ModelArtifact>> ListAsync(
        string? modelIdFilter = null,
        ModelArtifactStatus? statusFilter = null,
        CancellationToken cancellationToken = default);

    /// <summary>推进 artifact 状态（如 Draft → Validated → Staged → Active）。</summary>
    Task<ModelArtifact> PromoteAsync(
        string modelId,
        ModelArtifactVersion version,
        ModelArtifactStatus newStatus,
        CancellationToken cancellationToken = default);
}

// ---------- Canary & Rollback ----------

/// <summary>Canary assignment 策略：决定哪些流量 / 工作区进入 canary。</summary>
public enum CanaryAssignmentStrategy
{
    /// <summary>随机分配（按比例随机选择）。</summary>
    Random,

    /// <summary>基于 hash（对 workspace_id 或 collection_id 取模，确保同一对象始终落在同一 split）。</summary>
    HashBased,

    /// <summary>按比例分配（如 5% 流量进入 canary）。</summary>
    PercentageBased,

    /// <summary>白名单分配（仅指定 workspace/collection 进入 canary）。</summary>
    Whitelist
}

/// <summary>回滚原因：决定回滚是自动还是人工触发。</summary>
public enum RollbackReason
{
    /// <summary>RollbackCondition 触发：自动回滚。</summary>
    RollbackConditionTriggered,

    /// <summary>人工干预：操作员手动触发回滚。</summary>
    ManualIntervention,

    /// <summary>模型性能回退：canary 期间指标显著低于基线。</summary>
    ModelPerformanceRegression,

    /// <summary>系统错误：canary 期间出现异常（非 RollbackCondition 范围）。</summary>
    SystemError,

    /// <summary>Canary 时长到期：canary 阶段未达成 ExpectedGain 阈值，自动回滚到基线。</summary>
    CanaryDurationExpired
}

/// <summary>Canary assignment：记录哪些流量 / 工作区进入 canary 阶段。</summary>
public sealed record CanaryAssignment
{
    /// <summary>构造 canary assignment。</summary>
    public CanaryAssignment(
        string assignmentId,
        string proposalId,
        string runId,
        CanaryAssignmentStrategy strategy,
        DateTimeOffset assignedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assignmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        AssignmentId = assignmentId;
        ProposalId = proposalId;
        RunId = runId;
        Strategy = strategy;
        AssignedAt = assignedAt;
    }

    /// <summary>Assignment ID。</summary>
    public string AssignmentId { get; }

    /// <summary>关联的 proposal ID。</summary>
    public string ProposalId { get; }

    /// <summary>关联的 pipeline run ID。</summary>
    public string RunId { get; }

    /// <summary>分配策略。</summary>
    public CanaryAssignmentStrategy Strategy { get; }

    /// <summary>分配时间。</summary>
    public DateTimeOffset AssignedAt { get; }

    /// <summary>受影响的 workspace IDs（仅 Whitelist 策略时必填）。</summary>
    public IReadOnlyList<string> AffectedWorkspaceIds { get; init; } = Array.Empty<string>();

    /// <summary>受影响的 collection IDs（仅 Whitelist 策略时必填）。</summary>
    public IReadOnlyList<string> AffectedCollectionIds { get; init; } = Array.Empty<string>();

    /// <summary>Canary 流量比例 [0, 1]（仅 PercentageBased 策略时有效，默认 0.05 = 5%）。</summary>
    public double Percentage { get; init; } = 0.05;

    /// <summary>白名单 hash（仅 Whitelist 策略时有效，记录白名单内容 hash 以便审计）。</summary>
    public string? WhitelistHash { get; init; }

    /// <summary>策略配置 JSON（额外配置，如 hash seed、分层规则）。</summary>
    public string? StrategyConfigJson { get; init; }
}

/// <summary>回滚记录：记录 pipeline 回滚事件。</summary>
public sealed record RollbackRecord
{
    /// <summary>构造回滚记录。</summary>
    public RollbackRecord(
        string recordId,
        string runId,
        string proposalId,
        RollbackReason reason,
        DateTimeOffset triggeredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);
        RecordId = recordId;
        RunId = runId;
        ProposalId = proposalId;
        Reason = reason;
        TriggeredAt = triggeredAt;
    }

    /// <summary>记录 ID。</summary>
    public string RecordId { get; }

    /// <summary>关联的 pipeline run ID。</summary>
    public string RunId { get; }

    /// <summary>关联的 proposal ID。</summary>
    public string ProposalId { get; }

    /// <summary>回滚原因。</summary>
    public RollbackReason Reason { get; }

    /// <summary>触发时间。</summary>
    public DateTimeOffset TriggeredAt { get; }

    /// <summary>触发的 RollbackCondition metric 名（仅当 Reason=RollbackConditionTriggered 时非空）。</summary>
    public string? TriggeredConditionMetricName { get; init; }

    /// <summary>触发的 RollbackCondition 阈值。</summary>
    public double? TriggeredConditionThreshold { get; init; }

    /// <summary>触发时的实际 metric 值。</summary>
    public double? TriggeredConditionValue { get; init; }

    /// <summary>触发时所在 stage。</summary>
    public OptimizationStage? TriggeredAtStage { get; init; }

    /// <summary>操作员备注（仅当 Reason=ManualIntervention 时非空）。</summary>
    public string? OperatorNotes { get; init; }
}

/// <summary>基线对比：记录 canary / shadow 期间的基线 vs 实验指标。</summary>
public sealed record BaselineComparison
{
    /// <summary>构造基线对比。</summary>
    public BaselineComparison(
        string comparisonId,
        string proposalId,
        IReadOnlyDictionary<string, double> baselineMetrics,
        IReadOnlyDictionary<string, double> experimentMetrics,
        DateTimeOffset comparedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(comparisonId);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);
        ArgumentNullException.ThrowIfNull(baselineMetrics);
        ArgumentNullException.ThrowIfNull(experimentMetrics);
        ComparisonId = comparisonId;
        ProposalId = proposalId;
        BaselineMetrics = baselineMetrics;
        ExperimentMetrics = experimentMetrics;
        ComparedAt = comparedAt;
    }

    /// <summary>对比 ID。</summary>
    public string ComparisonId { get; }

    /// <summary>关联的 proposal ID。</summary>
    public string ProposalId { get; }

    /// <summary>基线指标。</summary>
    public IReadOnlyDictionary<string, double> BaselineMetrics { get; }

    /// <summary>实验指标。</summary>
    public IReadOnlyDictionary<string, double> ExperimentMetrics { get; }

    /// <summary>对比时间。</summary>
    public DateTimeOffset ComparedAt { get; }

    /// <summary>当前 stage 采集的指标（如 canary 期间的 P99 latency / error rate）。</summary>
    public IReadOnlyDictionary<string, double> StageMetrics { get; init; } = new Dictionary<string, double>();

    /// <summary>Judge 裁决结果（可选；未裁决时为空）。</summary>
    public PromotionDecision? JudgeDecision { get; init; }

    /// <summary>Judge 裁决理由（可选）。</summary>
    public string? JudgeRationale { get; init; }
}
