namespace ContextCore.Abstractions;

// ===========================================================================
// Learning Event Pipeline 补齐契约
//
// 目标：在 perf-5 已确认的 Durable Outbox + Dispatcher + Worker 基础上，
// 补齐 Learning Event Pipeline 的缺失环节（label quality / leakage detection /
// dataset split / offline replay gate / 统一 learning pipeline event）。
//
// 设计原则：
// 1. 仅定义 Abstractions 层契约，不含存储 I/O；实现层在 Core/Services/MemoryEvolution。
// 2. 复用已有 IUtilityLedgerStore / IUserFeedbackLedger / ITrainingDataExporter 数据源。
// 3. 算法为"基础实现"——不做完整 ML 流水线，仅提供 contract 与可工作的最简实现，
// 便于后续替换为更复杂算法（接口稳定）。
// 4. 与 LearningEventOutboxRecord 区别：outbox 是 per-decision 持久化记录；
// 本契约定义统一的 LearningPipelineEvent（含 lineage）用于在 pipeline 内传递
// user feedback / tool outcome / task completion 等非 decision 事件。
// ===========================================================================

/// <summary>
/// Learning pipeline 事件类型。区分不同来源的学习信号，便于下游分别处理。
/// </summary>
public enum LearningPipelineEventType : byte
{
    /// <summary>决策物化事件（来自 LearningMaterializationDispatcher；默认主路径）。</summary>
    Decision = 0,

    /// <summary>延迟用户反馈事件（用户对已完成 AgentRun 的评分 / 修正）。</summary>
    UserFeedback = 1,

    /// <summary>Tool 执行结果事件（tool outcome；成功 / 失败 / 耗时）。</summary>
    ToolOutcome = 2,

    /// <summary>AgentRun 任务完成事件（最终答案 / 是否成功 / 耗时 / cost）。</summary>
    TaskCompletion = 3
}

/// <summary>
/// 统一的 Learning Pipeline 事件。承载完整 lineage（decision_id → run_id → session_id → tool_calls），
/// 用于在 pipeline 内传递非 decision 类学习信号。
/// </summary>
/// <remarks>
/// 设计原则：
/// 1. Lineage 完整性：每条事件可追溯到原始 decision、run、session 与触发的 tool calls。
/// 2. Payload 透明：调用方序列化任意 DTO 为 JSON；消费者按 <see cref="EventType"/> 反序列化。
/// 3. 幂等：IdempotencyKey 由调用方提供，重复入队由 sink 保证覆盖或忽略。
/// 4. 与 LearningEventOutboxRecord 解耦：decision 路径仍走 outbox；
/// 非 decision 事件通过本契约的 sink 入队，由下游消费者异步处理。
/// </remarks>
public sealed record LearningPipelineEvent
{
    /// <summary>事件唯一 ID（如 "learn-event-{guid}"）。</summary>
    public required string EventId { get; init; }

    /// <summary>事件类型。</summary>
    public required LearningPipelineEventType EventType { get; init; }

    /// <summary>workspace 作用域（空字符串 = 全局/默认）。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>collection 作用域（空字符串 = 全局/默认）。</summary>
    public string CollectionId { get; init; } = string.Empty;

    /// <summary>关联的原始 Decision ID（与 ContextDecisionResult.RequestId / UtilityLedgerEntry.DecisionId 对应）。</summary>
    /// <remarks>延迟用户反馈与 tool outcome 都必须关联到原始 decision，作为 lineage 锚点。</remarks>
    public string DecisionId { get; init; } = string.Empty;

    /// <summary>关联的 AgentRun ID（可空；decision 路径通常无 run 上下文）。</summary>
    public string? RunId { get; init; }

    /// <summary>关联的会话 ID（可空；用于 group-keyed split 防泄漏）。</summary>
    public string? SessionId { get; init; }

    /// <summary>本次事件触发的 tool call RequestId 列表（可空；仅 ToolOutcome / TaskCompletion 非空）。</summary>
    public IReadOnlyList<string> ToolCallIds { get; init; } = Array.Empty<string>();

    /// <summary>事件 payload（JSON 字符串；消费者按 EventType 反序列化）。</summary>
    public required string Payload { get; init; }

    /// <summary>事件产生时间（UTC）。</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>幂等键（调用方提供；同键重复入队由 sink 保证覆盖或忽略）。</summary>
    public required string IdempotencyKey { get; init; }
}

/// <summary>
/// Learning pipeline 统一入口：将任意 <see cref="LearningPipelineEvent"/> 入队到 pipeline。
/// </summary>
/// <remarks>
/// 设计原则：
/// 1. Decision 路径已由 LearningMaterializationDispatcher 处理；本 sink 主要服务非 decision 事件。
/// 2. 实现可选用 in-memory Channel（非持久）或扩展 outbox（持久）——契约层不强制。
/// 3. 入队不阻塞调用方；失败由 sink 内部降级（log + metric），不抛异常到调用方。
/// </remarks>
public interface ILearningPipelineSink
{
    /// <summary>
    /// 入队一条 learning pipeline 事件。fire-and-forget 语义：调用方不等待消费完成。
    /// </summary>
    /// <param name="evt">学习事件（必填）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task EnqueueAsync(
        LearningPipelineEvent evt,
        CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Label Quality（标签质量评分）
// ---------------------------------------------------------------------------

/// <summary>
/// 标签质量评分报告。基于 Utility Ledger 条目与用户反馈计算一致性 / 置信度 / 专家共识。
/// </summary>
public sealed record LabelQualityReport
{
    /// <summary>评分时间（UTC）。</summary>
    public required DateTimeOffset EvaluatedAt { get; init; }

    /// <summary>评估的 workspace 作用域。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>评估的 collection 作用域（跨集合时为 null）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>评估的样本总数。</summary>
    public required int TotalSamples { get; init; }

    /// <summary>有用户反馈的样本数（参与一致性计算）。</summary>
    public required int LabeledWithFeedback { get; init; }

    /// <summary>
    /// 标签一致性分数 [0.0, 1.0]。
    /// 计算：用户反馈与 ledger IsSelected 标签一致的比例（ThumbsUp 与 IsSelected=true 一致 / ThumbsDown 与 IsSelected=false 一致）。
    /// 1.0 = 完全一致；0.0 = 完全冲突。
    /// </summary>
    public required double ConsistencyScore { get; init; }

    /// <summary>
    /// 平均置信度 [0.0, 1.0]。
    /// 计算：有反馈样本中 FeedbackValue 绝对值的平均（ThumbsUp/Down=1.0；ScoreCorrection=提供的值）。
    /// 无反馈样本返回 0.0。
    /// </summary>
    public required double AverageConfidence { get; init; }

    /// <summary>
    /// 专家共识分数 [0.0, 1.0]。
    /// 计算：同一 candidate 的多 Expert 贡献方向（是否一致指向 selected/dropped）的一致比例。
    /// 单 Expert 样本视为 1.0。
    /// </summary>
    public required double ExpertConsensusScore { get; init; }

    /// <summary>质量告警列表（如样本数不足 / 一致性过低 / 反馈缺失等）。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 标签质量评分器。从 Utility Ledger + User Feedback Ledger 计算标签质量报告。
/// </summary>
public interface ILabelQualityScorer
{
    /// <summary>
    /// 评估指定 workspace / collection 的标签质量。
    /// </summary>
    /// <param name="workspaceId">workspace 作用域（必填）。</param>
    /// <param name="collectionId">collection 作用域（可空 = 跨集合）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [StoreOperation(StoreOperationKind.Read)]
    Task<LabelQualityReport> EvaluateAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Leakage Detection（数据泄露检测）
// ---------------------------------------------------------------------------

/// <summary>数据泄露类型。</summary>
public enum LeakageKind : byte
{
    /// <summary>时间戳顺序异常（如训练样本时间晚于其引用的反馈时间）。</summary>
    TimestampOrderViolation = 0,

    /// <summary>重复样本（同 IdempotencyKey 或同 (DecisionId, CandidateItemId) 跨批次重复）。</summary>
    DuplicateSample = 1,

    /// <summary>未来信息泄露（特征引用了决策时尚未产生的数据，如决策后的反馈被当作特征）。</summary>
    FutureInformationLeakage = 2,

    /// <summary>跨 split 泄漏（同一 group_key 样本出现在 train 与 validation/test）。</summary>
    CrossSplitLeakage = 3
}

/// <summary>单条泄露发现。</summary>
public sealed record LeakageFinding
{
    /// <summary>泄露类型。</summary>
    public required LeakageKind Kind { get; init; }

    /// <summary>受影响的样本标识（DecisionId / CandidateItemId 或 IdempotencyKey）。</summary>
    public required string SampleRef { get; init; }

    /// <summary>诊断详情（如时间戳对比、重复键值）。</summary>
    public required string Detail { get; init; }
}

/// <summary>
/// 泄露检测报告。汇总检测到的所有泄露条目。
/// </summary>
public sealed record LeakageReport
{
    /// <summary>检测时间（UTC）。</summary>
    public required DateTimeOffset DetectedAt { get; init; }

    /// <summary>评估的 workspace 作用域。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>评估的 collection 作用域（跨集合时为 null）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>检测的样本总数。</summary>
    public required int TotalSamples { get; init; }

    /// <summary>检测到的泄露条目列表（空 = 无泄露）。</summary>
    public IReadOnlyList<LeakageFinding> Findings { get; init; } = Array.Empty<LeakageFinding>();

    /// <summary>是否通过泄露检测（Findings 为空时为 true）。</summary>
    public bool Passed => Findings.Count == 0;
}

/// <summary>
/// 数据泄露检测器。检查训练数据中是否存在时间戳异常 / 重复样本 / 未来信息 / 跨 split 泄漏。
/// </summary>
public interface ILeakageDetector
{
    /// <summary>
    /// 检测指定 workspace / collection 的训练数据泄露。
    /// </summary>
    /// <param name="workspaceId">workspace 作用域（必填）。</param>
    /// <param name="collectionId">collection 作用域（可空 = 跨集合）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [StoreOperation(StoreOperationKind.Read)]
    Task<LeakageReport> DetectAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Training / Calibration Split（训练 / 校准集划分）
// ---------------------------------------------------------------------------

/// <summary>
/// 训练 / 校准集划分选项。控制 <see cref="ILearningDatasetSplitter"/> 的划分比例与策略。
/// </summary>
public sealed class TrainingCalibrationSplitOptions
{
    /// <summary>训练集比例 [0.0, 1.0]（默认 0.7）。</summary>
    public double TrainRatio { get; set; } = 0.7;

    /// <summary>校准集比例 [0.0, 1.0]（默认 0.15）。剩余自动归入 holdout（test）。</summary>
    public double CalibrationRatio { get; set; } = 0.15;

    /// <summary>
    /// 是否按 group_key（workspace_id / collection_id / session_id）划分以避免跨 split 泄漏。
    /// 默认 true（推荐）。false 时退化为按 candidate hash 随机划分。
    /// </summary>
    public bool GroupKeyed { get; set; } = true;

    /// <summary>划分种子（确保可复现；默认 0）。</summary>
    public int Seed { get; set; } = 0;
}

/// <summary>
/// 数据集划分结果。承载 train / calibration / holdout 三段样本标识。
/// </summary>
public sealed record DatasetSplitResult
{
    /// <summary>划分时间（UTC）。</summary>
    public required DateTimeOffset SplitAt { get; init; }

    /// <summary>评估的 workspace 作用域。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>评估的 collection 作用域（跨集合时为 null）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>使用的划分选项快照。</summary>
    public required TrainingCalibrationSplitOptions Options { get; init; }

    /// <summary>训练集样本标识（DecisionId 列表）。</summary>
    public IReadOnlyList<string> TrainDecisionIds { get; init; } = Array.Empty<string>();

    /// <summary>校准集样本标识（DecisionId 列表）。</summary>
    public IReadOnlyList<string> CalibrationDecisionIds { get; init; } = Array.Empty<string>();

    /// <summary>Holdout / test 集样本标识（DecisionId 列表；剩余比例）。</summary>
    public IReadOnlyList<string> HoldoutDecisionIds { get; init; } = Array.Empty<string>();

    /// <summary>训练集样本数。</summary>
    public int TrainCount => TrainDecisionIds.Count;

    /// <summary>校准集样本数。</summary>
    public int CalibrationCount => CalibrationDecisionIds.Count;

    /// <summary>Holdout 集样本数。</summary>
    public int HoldoutCount => HoldoutDecisionIds.Count;

    /// <summary>总样本数。</summary>
    public int TotalCount => TrainCount + CalibrationCount + HoldoutCount;
}

/// <summary>
/// 数据集划分器。将 Utility Ledger 数据按可配置比例划分为 train / calibration / holdout。
/// </summary>
public interface ILearningDatasetSplitter
{
    /// <summary>
    /// 划分指定 workspace / collection 的 ledger 数据。
    /// </summary>
    /// <param name="workspaceId">workspace 作用域（必填）。</param>
    /// <param name="collectionId">collection 作用域（可空 = 跨集合）。</param>
    /// <param name="options">划分选项（可空 = 默认 0.7/0.15 + GroupKeyed）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [StoreOperation(StoreOperationKind.Read)]
    Task<DatasetSplitResult> SplitAsync(
        string workspaceId,
        string? collectionId = null,
        TrainingCalibrationSplitOptions? options = null,
        CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Offline Replay Gate（离线回放闸门）
// ---------------------------------------------------------------------------

/// <summary>
/// 离线回放闸门选项。在使用学习数据训练 / 校准前必须通过的门控条件。
/// </summary>
public sealed class ReplayGateOptions
{
    /// <summary>最小样本数（默认 50；不足则 gate 拒绝）。</summary>
    public int MinSampleCount { get; set; } = 50;

    /// <summary>最低标签一致性分数 [0.0, 1.0]（默认 0.6）。</summary>
    public double MinConsistencyScore { get; set; } = 0.6;

    /// <summary>是否强制泄露检测通过（默认 true）。</summary>
    public bool RequireNoLeakage { get; set; } = true;

    /// <summary>是否强制数据完整性（SHA-256 校验，默认 true；基础实现仅检查样本数 &gt; 0）。</summary>
    public bool RequireIntegrity { get; set; } = true;
}

/// <summary>
/// 离线回放闸门结果。
/// </summary>
public sealed record ReplayGateResult
{
    /// <summary>评估时间（UTC）。</summary>
    public required DateTimeOffset EvaluatedAt { get; init; }

    /// <summary>评估的 workspace 作用域。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>评估的 collection 作用域（跨集合时为 null）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>是否通过闸门（true = 可用于训练 / 校准）。</summary>
    public required bool Passed { get; init; }

    /// <summary>使用的闸门选项快照。</summary>
    public required ReplayGateOptions Options { get; init; }

    /// <summary>引用的标签质量报告（null = 未评估）。</summary>
    public LabelQualityReport? LabelQuality { get; init; }

    /// <summary>引用的泄露检测报告（null = 未评估）。</summary>
    public LeakageReport? Leakage { get; init; }

    /// <summary>引用的数据集划分结果（null = 未划分）。</summary>
    public DatasetSplitResult? Split { get; init; }

    /// <summary>阻塞原因列表（Passed=false 时非空）。</summary>
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 离线回放闸门。组合 label quality + leakage detection + min sample count + split 完整性，
/// 在使用学习数据训练 / 校准前做一次性门控。
/// </summary>
/// <remarks>
/// 设计原则：
/// 1. 组合而非替代：复用 <see cref="ILabelQualityScorer"/> / <see cref="ILeakageDetector"/> /
/// <see cref="ILearningDatasetSplitter"/>，gate 仅做聚合判定。
/// 2. 失败不抛异常：返回 <see cref="ReplayGateResult"/>，由调用方决定是否阻断。
/// 3. 可配置阈值：通过 <see cref="ReplayGateOptions"/> 调整门控严格度。
/// </remarks>
public interface IOfflineReplayGate
{
    /// <summary>
    /// 评估指定 workspace / collection 是否通过离线回放闸门。
    /// </summary>
    /// <param name="workspaceId">workspace 作用域（必填）。</param>
    /// <param name="collectionId">collection 作用域（可空 = 跨集合）。</param>
    /// <param name="options">闸门选项（可空 = 默认阈值）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [StoreOperation(StoreOperationKind.Read)]
    Task<ReplayGateResult> EvaluateAsync(
        string workspaceId,
        string? collectionId = null,
        ReplayGateOptions? options = null,
        CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Delayed User Feedback（延迟用户反馈 API 入口 DTO）
// ---------------------------------------------------------------------------

/// <summary>
/// 延迟用户反馈提交请求（针对已完成的 AgentRun）。
/// </summary>
/// <remarks>
/// 与 <see cref="UserFeedbackSubmitRequest"/> 区别：
/// 1. 关联到 AgentRun（RunId 必填）+ 原始 DecisionId（lineage 锚点）。
/// 2. 提交后除写入 IUserFeedbackLedger 外，同时作为 delayed learning event 入队 pipeline。
/// 3. 用于用户在 run 完成后异步提供评分 / 修正，作为校准标签信号。
/// </remarks>
public sealed class DelayedUserFeedbackRequest
{
    /// <summary>workspace 作用域（必填）。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>collection 作用域（必填）。</summary>
    public string CollectionId { get; init; } = string.Empty;

    /// <summary>关联的原始 DecisionId（必填；lineage 锚点）。</summary>
    public string DecisionId { get; init; } = string.Empty;

    /// <summary>关联的已完成 AgentRun ID（必填）。</summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>关联的会话 ID（可空；用于 group-keyed split 防泄漏）。</summary>
    public string? SessionId { get; init; }

    /// <summary>反馈类型（必填）。</summary>
    public UserFeedbackKind Kind { get; init; }

    /// <summary>
    /// 反馈数值（可选；不填时由 Kind 推导）。
    /// - ThumbsUp / Report：忽略，自动设为 +1.0 / -1.0
    /// - ThumbsDown：忽略，自动设为 -1.0
    /// - ScoreCorrection：必填，范围 [0.0, 1.0]
    /// - TextFeedback：忽略，自动设为 0.0
    /// </summary>
    public double? FeedbackValue { get; init; }

    /// <summary>反馈文本（可空；TextFeedback 必填；其他类型可选）。</summary>
    public string? FeedbackText { get; init; }

    /// <summary>反馈者标识（用户 ID 或会话 ID；可空表示匿名反馈）。</summary>
    public string? GivenBy { get; init; }

    /// <summary>幂等键（可选；不填时自动生成 "delayed-fb-idem-{guid}"）。</summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>
/// 延迟用户反馈提交结果。
/// </summary>
public sealed class DelayedUserFeedbackResult
{
    /// <summary>写入的反馈条目 ID（UserFeedbackLedger 侧）。</summary>
    public string FeedbackEntryId { get; init; } = string.Empty;

    /// <summary>入队的 learning pipeline 事件 ID。</summary>
    public string PipelineEventId { get; init; } = string.Empty;

    /// <summary>是否成功写入 UserFeedbackLedger。</summary>
    public bool FeedbackPersisted { get; init; }

    /// <summary>是否成功入队 learning pipeline。</summary>
    public bool PipelineEnqueued { get; init; }

    /// <summary>警告列表。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
