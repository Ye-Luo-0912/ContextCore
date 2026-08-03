using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ContextCore.Abstractions;
using Microsoft.Extensions.Logging;

namespace ContextCore.Core.Services.MemoryEvolution;

// ===========================================================================
// Learning Event Pipeline 补齐实现
//
// 与 perf-5 已确认的 Durable Outbox + Dispatcher + Worker 并存：
// - LearningPipelineSink：非 decision 事件（user feedback / tool outcome / task completion）
// 的统一入队入口。基础实现使用 in-memory bounded Channel + 后台日志消费。
// 持久化扩展（写入 outbox）为后续演进点，契约层已解耦。
// - LabelQualityScorer / LeakageDetector / LearningDatasetSplitter / OfflineReplayGate：
// 数据质量闸门链，复用 IUtilityLedgerStore + IUserFeedbackLedger 只读数据源。
// - DelayedUserFeedbackService：用户对已完成 AgentRun 的延迟反馈入口，
// 写入 IUserFeedbackLedger + 入队 LearningPipelineEvent。
//
// 算法均为"基础实现"：可工作但非最优，便于后续替换为更复杂算法（接口稳定）。
// ===========================================================================

/// <summary>
/// Learning pipeline 统一 sink 默认实现。非 decision 事件入队到 in-memory bounded Channel，
/// 后台 worker 消费并记录日志（基础实现；持久化为后续演进点）。
/// </summary>
/// <remarks>
/// 设计原则：
/// 1. fire-and-forget：<see cref="EnqueueAsync"/> 写入 Channel 后立即返回，不等待消费。
/// 2. 背压控制：Channel 容量上限默认 1024，满时等待（与 LearningMaterializationDispatcher 一致）。
/// 3. 幂等：基于 <see cref="LearningPipelineEvent.IdempotencyKey"/> 去重（近期窗口内重复键忽略）。
/// 4. 优雅关闭：StopAsync 信号 Channel 完成，等待 worker 排空（最多 15 秒）。
/// 5. 失败降级：Channel 写入异常时 log + 静默吞掉（不阻塞调用方）。
/// </remarks>
public sealed class LearningPipelineSink : ILearningPipelineSink, IAsyncDisposable
{
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private const int DefaultCapacity = 1024;
    private const int IdempotencyWindow = 4096;
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(15);

    private readonly Channel<LearningPipelineEvent> _channel;
    private readonly ConcurrentDictionary<string, byte> _seenKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenQueue = new();
    private readonly object _seenLock = new();
    private readonly ILogger? _logger;
    private readonly Task _consumer;
    private readonly CancellationTokenSource _cts;

    /// <summary>
    /// 构造 sink。
    /// </summary>
    /// <param name="capacity">Channel 容量上限（默认 1024）。</param>
    /// <param name="logger">日志（null = 静默）。</param>
    public LearningPipelineSink(int capacity = DefaultCapacity, ILogger<LearningPipelineSink>? logger = null)
    {
        capacity = Math.Max(1, capacity);
        _channel = Channel.CreateBounded<LearningPipelineEvent>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
        _logger = logger;
        _cts = new CancellationTokenSource();
        _consumer = Task.Run(() => ConsumeAsync(_cts.Token));
    }

    /// <inheritdoc />
    public async Task EnqueueAsync(LearningPipelineEvent evt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        // 幂等去重：近期窗口内重复 IdempotencyKey 忽略。
        if (IsDuplicate(evt.IdempotencyKey))
        {
            _logger?.LogDebug(
                "LearningPipelineEvent {EventId} skipped (duplicate idempotency key {Key}).",
                evt.EventId, evt.IdempotencyKey);
            return;
        }

        try
        {
            await _channel.Writer.WriteAsync(evt, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            _logger?.LogWarning("LearningPipelineEvent {EventId} dropped (sink closed).", evt.EventId);
        }
        catch (Exception ex)
        {
            // 入队失败不抛到调用方——fire-and-forget 语义。
            _logger?.LogError(ex, "Failed to enqueue LearningPipelineEvent {EventId}.", evt.EventId);
        }
    }

    /// <summary>后台消费循环：基础实现仅记录日志（持久化扩展点）。</summary>
    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                _logger?.LogInformation(
                    "Consumed LearningPipelineEvent: Type={Type} EventId={EventId} DecisionId={DecisionId} RunId={RunId} PayloadBytes={Bytes}",
                    evt.EventType, evt.EventId, evt.DecisionId, evt.RunId, evt.Payload?.Length ?? 0);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 正常关闭。
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "LearningPipelineSink consumer crashed.");
        }
    }

    private bool IsDuplicate(string idempotencyKey)
    {
        lock (_seenLock)
        {
            if (!_seenKeys.TryAdd(idempotencyKey, 0))
            {
                return true;
            }
            _seenQueue.Enqueue(idempotencyKey);
            while (_seenQueue.Count > IdempotencyWindow && _seenQueue.Count > 0)
            {
                var old = _seenQueue.Dequeue();
                _seenKeys.TryRemove(old, out _);
            }
            return false;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            await _consumer.WaitAsync(DrainTimeout).ConfigureAwait(false);
        }
        catch
        {
            // 排空超时忽略。
        }
        _cts.Dispose();
    }
}

// ---------------------------------------------------------------------------
// LabelQualityScorer
// ---------------------------------------------------------------------------

/// <summary>
/// 标签质量评分器基础实现。从 Utility Ledger + User Feedback Ledger 计算一致性 / 置信度 / 专家共识。
/// </summary>
public sealed class LabelQualityScorer : ILabelQualityScorer
{
    private readonly IUtilityLedgerStore _ledgerStore;
    private readonly IUserFeedbackLedger _feedbackLedger;

    public LabelQualityScorer(IUtilityLedgerStore ledgerStore, IUserFeedbackLedger feedbackLedger)
    {
        ArgumentNullException.ThrowIfNull(ledgerStore);
        ArgumentNullException.ThrowIfNull(feedbackLedger);
        _ledgerStore = ledgerStore;
        _feedbackLedger = feedbackLedger;
    }

    /// <inheritdoc />
    public async Task<LabelQualityReport> EvaluateAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var entries = await _ledgerStore.QueryAsync(new UtilityLedgerQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Take = 0
        }, cancellationToken).ConfigureAwait(false);

        var warnings = new List<string>();
        var total = entries.Count;
        if (total == 0)
        {
            warnings.Add("无可用 ledger 样本（TotalSamples=0）；质量分数无意义。");
            return new LabelQualityReport
            {
                EvaluatedAt = DateTimeOffset.UtcNow,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                TotalSamples = 0,
                LabeledWithFeedback = 0,
                ConsistencyScore = 0.0,
                AverageConfidence = 0.0,
                ExpertConsensusScore = 0.0,
                Warnings = warnings
            };
        }

        // 拉取该 workspace/collection 的全部用户反馈，按 (DecisionId, CandidateItemId) 索引。
        var feedbacks = await _feedbackLedger.QueryFeedbackAsync(new UserFeedbackQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Take = 0
        }, cancellationToken).ConfigureAwait(false);

        // 取每个 (DecisionId, CandidateItemId) 的最新反馈（QueryFeedbackAsync 已按 GivenAt 降序）。
        var feedbackIndex = new Dictionary<string, UserFeedbackEntry>(StringComparer.Ordinal);
        foreach (var fb in feedbacks)
        {
            var key = FeedbackKey(fb.DecisionId, fb.CandidateItemId);
            if (!feedbackIndex.ContainsKey(key))
            {
                feedbackIndex[key] = fb;
            }
        }

        // 一致性 + 置信度：仅对有反馈的样本计算。
        var consistencyAgree = 0;
        var consistencyTotal = 0;
        var confidenceSum = 0.0;
        var labeledCount = 0;

        foreach (var entry in entries)
        {
            var key = FeedbackKey(entry.DecisionId, entry.CandidateItemId);
            if (!feedbackIndex.TryGetValue(key, out var fb))
            {
                continue;
            }
            labeledCount++;
            consistencyTotal++;
            confidenceSum += Math.Abs(fb.FeedbackValue);

            // 一致性：ThumbsUp ↔ IsSelected=true；ThumbsDown/Report ↔ IsSelected=false。
            var consistent = fb.Kind switch
            {
                UserFeedbackKind.ThumbsUp => entry.IsSelected,
                UserFeedbackKind.ThumbsDown => !entry.IsSelected,
                UserFeedbackKind.Report => !entry.IsSelected,
                UserFeedbackKind.ScoreCorrection => entry.IsSelected, // 修正分视为对 selected 的确认
                UserFeedbackKind.TextFeedback => true, // 文本反馈不计入一致性冲突
                _ => true
            };
            if (consistent) consistencyAgree++;
        }

        // 专家共识：按 DecisionId 分组，检查同 decision 多 Expert 贡献方向一致性。
        var byDecision = entries.GroupBy(e => e.DecisionId, StringComparer.Ordinal);
        var consensusAgree = 0;
        var consensusTotal = 0;
        foreach (var group in byDecision)
        {
            var list = group.ToList();
            if (list.Count <= 1)
            {
                consensusAgree++;
                consensusTotal++;
                continue;
            }
            consensusTotal++;
            // 共识：所有 Expert 的 IsSelected 标签一致（全 selected 或全 dropped）。
            var firstSelected = list[0].IsSelected;
            var allAgree = list.All(e => e.IsSelected == firstSelected);
            if (allAgree) consensusAgree++;
        }

        var consistencyScore = consistencyTotal > 0 ? (double)consistencyAgree / consistencyTotal : 0.0;
        var averageConfidence = labeledCount > 0 ? confidenceSum / labeledCount : 0.0;
        var expertConsensus = consensusTotal > 0 ? (double)consensusAgree / consensusTotal : 0.0;

        if (labeledCount == 0)
        {
            warnings.Add("无用户反馈样本；ConsistencyScore / AverageConfidence 为 0（仅 ExpertConsensusScore 有效）。");
        }
        if (consistencyScore < 0.6 && consistencyTotal > 0)
        {
            warnings.Add($"标签一致性偏低（{consistencyScore:F3} < 0.6）；建议核查反馈与决策标签对齐。");
        }

        return new LabelQualityReport
        {
            EvaluatedAt = DateTimeOffset.UtcNow,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            TotalSamples = total,
            LabeledWithFeedback = labeledCount,
            ConsistencyScore = consistencyScore,
            AverageConfidence = averageConfidence,
            ExpertConsensusScore = expertConsensus,
            Warnings = warnings
        };
    }

    private static string FeedbackKey(string decisionId, string candidateItemId)
        => decisionId + "|" + candidateItemId;
}

// ---------------------------------------------------------------------------
// LeakageDetector
// ---------------------------------------------------------------------------

/// <summary>
/// 数据泄露检测器基础实现。检查重复样本 / 时间戳顺序异常。
/// </summary>
/// <remarks>
/// 基础实现覆盖：
/// 1. <see cref="LeakageKind.DuplicateSample"/>：同 (DecisionId, CandidateItemId, Expert) 跨批次重复。
/// 2. <see cref="LeakageKind.TimestampOrderViolation"/>：同 candidate 的 ledger 条目 MaterializedAt
/// 与最新用户反馈 GivenAt 比较——若反馈早于物化则可能存在时序异常（基础启发式）。
/// CrossSplitLeakage / FutureInformationLeakage 需结合 split 与特征 schema，留作后续扩展。
/// </remarks>
public sealed class LeakageDetector : ILeakageDetector
{
    private readonly IUtilityLedgerStore _ledgerStore;
    private readonly IUserFeedbackLedger? _feedbackLedger;

    public LeakageDetector(IUtilityLedgerStore ledgerStore, IUserFeedbackLedger? feedbackLedger = null)
    {
        ArgumentNullException.ThrowIfNull(ledgerStore);
        _ledgerStore = ledgerStore;
        _feedbackLedger = feedbackLedger;
    }

    /// <inheritdoc />
    public async Task<LeakageReport> DetectAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var entries = await _ledgerStore.QueryAsync(new UtilityLedgerQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Take = 0
        }, cancellationToken).ConfigureAwait(false);

        var findings = new List<LeakageFinding>();

        // 1. 重复样本：按 (DecisionId, CandidateItemId, Expert) 分组，count > 1 即重复。
        var dupKeys = new HashSet<string>(StringComparer.Ordinal);
        var dupGroups = entries
            .GroupBy(e => $"{e.DecisionId}|{e.CandidateItemId}|{e.Expert}", StringComparer.Ordinal)
            .Where(g => g.Count() > 1);
        foreach (var group in dupGroups)
        {
            var first = group.First();
            findings.Add(new LeakageFinding
            {
                Kind = LeakageKind.DuplicateSample,
                SampleRef = $"{first.DecisionId}/{first.CandidateItemId}/{first.Expert}",
                Detail = $"跨批次重复 {group.Count()} 次（同 DecisionId+CandidateItemId+Expert）。"
            });
            dupKeys.Add(group.Key);
        }

        // 2. 时间戳顺序异常：若反馈 GivenAt 早于 ledger MaterializedAt（理论上反馈应晚于决策物化）。
        if (_feedbackLedger is not null)
        {
            var feedbacks = await _feedbackLedger.QueryFeedbackAsync(new UserFeedbackQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Take = 0
            }, cancellationToken).ConfigureAwait(false);

            var ledgerByDecisionCandidate = entries
                .GroupBy(e => $"{e.DecisionId}|{e.CandidateItemId}")
                .ToDictionary(
                    g => g.Key,
                    g => g.Max(e => e.MaterializedAt),
                    StringComparer.Ordinal);

            foreach (var fb in feedbacks)
            {
                var key = $"{fb.DecisionId}|{fb.CandidateItemId}";
                if (ledgerByDecisionCandidate.TryGetValue(key, out var maxMaterializedAt)
                    && fb.GivenAt < maxMaterializedAt)
                {
                    findings.Add(new LeakageFinding
                    {
                        Kind = LeakageKind.TimestampOrderViolation,
                        SampleRef = key,
                        Detail = $"反馈时间 {fb.GivenAt:O} 早于 ledger 最新物化时间 {maxMaterializedAt:O}；可能存在时序异常。"
                    });
                }
            }
        }

        return new LeakageReport
        {
            DetectedAt = DateTimeOffset.UtcNow,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            TotalSamples = entries.Count,
            Findings = findings
        };
    }
}

// ---------------------------------------------------------------------------
// LearningDatasetSplitter
// ---------------------------------------------------------------------------

/// <summary>
/// 数据集划分器基础实现。按 DecisionId 分组，使用确定性 hash 划分到 train / calibration / holdout。
/// </summary>
/// <remarks>
/// GroupKeyed=true 时按 (workspaceId, collectionId) 作为 group key 整体落入同一 split，
/// 避免同 workspace 跨 split 泄漏。false 时按 DecisionId hash 随机划分。
/// </remarks>
public sealed class LearningDatasetSplitter : ILearningDatasetSplitter
{
    private readonly IUtilityLedgerStore _ledgerStore;

    public LearningDatasetSplitter(IUtilityLedgerStore ledgerStore)
    {
        ArgumentNullException.ThrowIfNull(ledgerStore);
        _ledgerStore = ledgerStore;
    }

    /// <inheritdoc />
    public async Task<DatasetSplitResult> SplitAsync(
        string workspaceId,
        string? collectionId = null,
        TrainingCalibrationSplitOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var opts = options ?? new TrainingCalibrationSplitOptions();

        // 校验比例
        var trainRatio = Clamp(opts.TrainRatio, 0.0, 1.0);
        var calibrationRatio = Clamp(opts.CalibrationRatio, 0.0, 1.0 - trainRatio);
        if (trainRatio + calibrationRatio > 1.0)
        {
            calibrationRatio = Math.Max(0.0, 1.0 - trainRatio);
        }

        var entries = await _ledgerStore.QueryAsync(new UtilityLedgerQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Take = 0
        }, cancellationToken).ConfigureAwait(false);

        // 按 DecisionId 去重得到唯一决策列表（一个 decision 可能有多条 ledger 条目）。
        var decisionIds = entries
            .Select(e => e.DecisionId)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();

        var train = new List<string>();
        var calibration = new List<string>();
        var holdout = new List<string>();

        if (opts.GroupKeyed && !string.IsNullOrWhiteSpace(collectionId))
        {
            // GroupKeyed：整个 (workspace, collection) 作为一个 group 落入 train（基础实现）。
            // 完整 group-keyed split 需跨多个 collection 聚合，此处为最简实现：单 collection 全归 train。
            train.AddRange(decisionIds);
        }
        else
        {
            // 按 DecisionId 确定性 hash 划分。
            foreach (var decisionId in decisionIds)
            {
                var bucket = StableBucket(decisionId, opts.Seed);
                if (bucket < trainRatio)
                {
                    train.Add(decisionId);
                }
                else if (bucket < trainRatio + calibrationRatio)
                {
                    calibration.Add(decisionId);
                }
                else
                {
                    holdout.Add(decisionId);
                }
            }
        }

        return new DatasetSplitResult
        {
            SplitAt = DateTimeOffset.UtcNow,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Options = opts,
            TrainDecisionIds = train,
            CalibrationDecisionIds = calibration,
            HoldoutDecisionIds = holdout
        };
    }

    /// <summary>稳定 hash bucket [0.0, 1.0)。基于 SHA256(decisionId + seed) 取前 8 字节 mod 2^53 / 2^53。</summary>
    private static double StableBucket(string decisionId, int seed)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes($"{seed}:{decisionId}");
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        // 取前 7 字节（56 bits），归一化到 [0, 1)。
        ulong value = 0;
        for (var i = 0; i < 7; i++)
        {
            value = (value << 8) | hash[i];
        }
        return value / (double)(1UL << 56);
    }

    private static double Clamp(double v, double min, double max)
        => Math.Max(min, Math.Min(max, v));
}

// ---------------------------------------------------------------------------
// OfflineReplayGate
// ---------------------------------------------------------------------------

/// <summary>
/// 离线回放闸门基础实现。组合 label quality + leakage detection + min sample count + split。
/// </summary>
public sealed class OfflineReplayGate : IOfflineReplayGate
{
    private readonly ILabelQualityScorer _labelQualityScorer;
    private readonly ILeakageDetector _leakageDetector;
    private readonly ILearningDatasetSplitter _splitter;

    public OfflineReplayGate(
        ILabelQualityScorer labelQualityScorer,
        ILeakageDetector leakageDetector,
        ILearningDatasetSplitter splitter)
    {
        _labelQualityScorer = labelQualityScorer ?? throw new ArgumentNullException(nameof(labelQualityScorer));
        _leakageDetector = leakageDetector ?? throw new ArgumentNullException(nameof(leakageDetector));
        _splitter = splitter ?? throw new ArgumentNullException(nameof(splitter));
    }

    /// <inheritdoc />
    public async Task<ReplayGateResult> EvaluateAsync(
        string workspaceId,
        string? collectionId = null,
        ReplayGateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var opts = options ?? new ReplayGateOptions();

        var blocked = new List<string>();

        // 1. 数据集划分（用于获取样本数 + split 完整性）。
        var split = await _splitter.SplitAsync(workspaceId, collectionId, null, cancellationToken).ConfigureAwait(false);
        var totalSamples = split.TotalCount;

        // 2. 最小样本数检查。
        if (totalSamples < opts.MinSampleCount)
        {
            blocked.Add($"样本数不足：{totalSamples} < MinSampleCount={opts.MinSampleCount}。");
        }

        // 3. 数据完整性检查（基础实现：样本数 > 0）。
        if (opts.RequireIntegrity && totalSamples == 0)
        {
            blocked.Add("数据完整性失败：无任何 ledger 样本（无法计算 SHA-256）。");
        }

        // 4. 标签质量检查。
        LabelQualityReport? labelQuality = null;
        try
        {
            labelQuality = await _labelQualityScorer.EvaluateAsync(workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);
            if (labelQuality.LabeledWithFeedback > 0 && labelQuality.ConsistencyScore < opts.MinConsistencyScore)
            {
                blocked.Add($"标签一致性不足：{labelQuality.ConsistencyScore:F3} < MinConsistencyScore={opts.MinConsistencyScore}。");
            }
        }
        catch (Exception ex)
        {
            blocked.Add($"标签质量评估异常：{ex.GetType().Name}: {ex.Message}");
        }

        // 5. 泄露检测。
        LeakageReport? leakage = null;
        if (opts.RequireNoLeakage)
        {
            try
            {
                leakage = await _leakageDetector.DetectAsync(workspaceId, collectionId, cancellationToken)
                    .ConfigureAwait(false);
                if (!leakage.Passed)
                {
                    blocked.Add($"检测到 {leakage.Findings.Count} 条数据泄露（详见 Leakage.Findings）。");
                }
            }
            catch (Exception ex)
            {
                blocked.Add($"泄露检测异常：{ex.GetType().Name}: {ex.Message}");
            }
        }

        return new ReplayGateResult
        {
            EvaluatedAt = DateTimeOffset.UtcNow,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Passed = blocked.Count == 0,
            Options = opts,
            LabelQuality = labelQuality,
            Leakage = leakage,
            Split = split,
            BlockedReasons = blocked
        };
    }
}

// ---------------------------------------------------------------------------
// DelayedUserFeedbackService
// ---------------------------------------------------------------------------

/// <summary>
/// 延迟用户反馈服务。将用户对已完成 AgentRun 的反馈写入 UserFeedbackLedger，
/// 同时作为 delayed learning event 入队 learning pipeline。
/// </summary>
/// <remarks>
/// 设计原则：
/// 1. 双写：UserFeedbackLedger（持久反馈记录）+ ILearningPipelineSink（pipeline 事件）。
/// 2. 关联 lineage：feedback 关联到原始 DecisionId + RunId + SessionId。
/// 3. 幂等：IdempotencyKey 由调用方提供或自动生成；sink 侧负责近期窗口去重。
/// 4. 失败降级：ledger 写入失败时仍尝试入队 pipeline（best-effort），反之亦然；
/// 两侧独立失败不互相阻塞，调用方通过 result 标志判断。
/// </remarks>
public sealed class DelayedUserFeedbackService
{
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly UserFeedbackService _userFeedbackService;
    private readonly ILearningPipelineSink _pipelineSink;
    private readonly TimeProvider? _timeProvider;

    public DelayedUserFeedbackService(
        UserFeedbackService userFeedbackService,
        ILearningPipelineSink pipelineSink,
        TimeProvider? timeProvider = null)
    {
        _userFeedbackService = userFeedbackService ?? throw new ArgumentNullException(nameof(userFeedbackService));
        _pipelineSink = pipelineSink ?? throw new ArgumentNullException(nameof(pipelineSink));
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// 提交延迟用户反馈：写入 UserFeedbackLedger + 入队 learning pipeline。
    /// </summary>
    public async Task<DelayedUserFeedbackResult> SubmitAsync(
        DelayedUserFeedbackRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var warnings = new List<string>();
        ValidateRequest(request);

        // 1. 写入 UserFeedbackLedger（复用 UserFeedbackService 的校验 + 派生 FeedbackValue）。
        var feedbackRequest = new UserFeedbackSubmitRequest
        {
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            DecisionId = request.DecisionId,
            CandidateItemId = request.RunId, // AgentRun 标识作为 candidate 维度的关联键
            Kind = request.Kind,
            FeedbackValue = request.FeedbackValue,
            FeedbackText = request.FeedbackText,
            GivenBy = request.GivenBy,
            IdempotencyKey = request.IdempotencyKey,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["lineageDecisionId"] = request.DecisionId,
                ["lineageRunId"] = request.RunId,
                ["lineageSessionId"] = request.SessionId ?? string.Empty,
                ["feedbackOrigin"] = "delayed"
            }
        };

        bool feedbackPersisted = false;
        string feedbackEntryId = string.Empty;
        double feedbackValue = ResolveFeedbackValue(request, warnings);
        try
        {
            var submitResult = await _userFeedbackService.SubmitAsync(feedbackRequest, cancellationToken)
                .ConfigureAwait(false);
            feedbackPersisted = true;
            feedbackEntryId = submitResult.FeedbackEntryId;
            warnings.AddRange(submitResult.Warnings);
        }
        catch (Exception ex)
        {
            // ledger 写入失败不阻塞 pipeline 入队；调用方通过标志判断（best-effort）。
            warnings.Add($"UserFeedbackLedger 写入失败：{ex.GetType().Name}: {ex.Message}（PipelineEnqueued 仍可能为 true）。");
        }

        // 2. 构造 LearningPipelineEvent 入队。
        var now = _timeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow;
        var pipelineEventId = "learn-event-" + Guid.NewGuid().ToString("N");
        var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? "delayed-fb-idem-" + Guid.NewGuid().ToString("N")
            : request.IdempotencyKey!;

        var payload = JsonSerializer.Serialize(new
        {
            request.WorkspaceId,
            request.CollectionId,
            request.DecisionId,
            request.RunId,
            request.SessionId,
            request.Kind,
            FeedbackValue = feedbackValue,
            request.FeedbackText,
            request.GivenBy,
            FeedbackEntryId = feedbackEntryId
        }, PayloadOptions);

        var pipelineEvent = new LearningPipelineEvent
        {
            EventId = pipelineEventId,
            EventType = LearningPipelineEventType.UserFeedback,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            DecisionId = request.DecisionId,
            RunId = request.RunId,
            SessionId = request.SessionId,
            ToolCallIds = Array.Empty<string>(),
            Payload = payload,
            OccurredAt = now,
            IdempotencyKey = idempotencyKey
        };

        bool pipelineEnqueued = false;
        try
        {
            await _pipelineSink.EnqueueAsync(pipelineEvent, cancellationToken).ConfigureAwait(false);
            pipelineEnqueued = true;
        }
        catch (Exception ex)
        {
            warnings.Add($"Learning pipeline 入队失败：{ex.GetType().Name}: {ex.Message}");
        }

        return new DelayedUserFeedbackResult
        {
            FeedbackEntryId = feedbackEntryId,
            PipelineEventId = pipelineEventId,
            FeedbackPersisted = feedbackPersisted,
            PipelineEnqueued = pipelineEnqueued,
            Warnings = warnings
        };
    }

    private static void ValidateRequest(DelayedUserFeedbackRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DecisionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);

        if (request.Kind == UserFeedbackKind.Unknown)
        {
            throw new ArgumentException(
                "Kind 不能为 Unknown；必须显式指定反馈类型。", nameof(request));
        }

        if (request.Kind == UserFeedbackKind.ScoreCorrection)
        {
            if (!request.FeedbackValue.HasValue)
            {
                throw new ArgumentException(
                    "ScoreCorrection 必须提供 FeedbackValue（范围 [0.0, 1.0]）。", nameof(request));
            }
            var v = request.FeedbackValue.Value;
            if (double.IsNaN(v) || double.IsInfinity(v) || v < 0.0 || v > 1.0)
            {
                throw new ArgumentException(
                    $"ScoreCorrection 的 FeedbackValue 必须在 [0.0, 1.0] 范围内；实际值 = {v}。", nameof(request));
            }
        }

        if (request.Kind == UserFeedbackKind.TextFeedback
            && string.IsNullOrWhiteSpace(request.FeedbackText))
        {
            throw new ArgumentException("TextFeedback 必须提供 FeedbackText。", nameof(request));
        }
    }

    private static double ResolveFeedbackValue(DelayedUserFeedbackRequest request, List<string> warnings)
    {
        return request.Kind switch
        {
            UserFeedbackKind.ThumbsUp => 1.0,
            UserFeedbackKind.ThumbsDown => -1.0,
            UserFeedbackKind.Report => -1.0,
            UserFeedbackKind.TextFeedback => 0.0,
            UserFeedbackKind.ScoreCorrection => request.FeedbackValue!.Value,
            _ => throw new ArgumentException($"未支持的 Kind: {request.Kind}", nameof(request))
        };
    }
}
