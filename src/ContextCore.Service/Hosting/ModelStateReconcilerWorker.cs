using ContextCore.Abstractions;
using ContextCore.Inference.Onnx;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContextCore.Service.Hosting;

/// <summary>
/// Model State Reconciler Worker（HA 模式）。
/// </summary>
/// <remarks>
/// 周期性从 <see cref="IClusterModelSlotStore"/> 拉取单一 Champion 槽位（"primary"）期望状态，
/// 并与本地 <see cref="IModelActivationManager"/> 的实际状态对比：
/// <list type="bullet">
/// <item>Revision 更高且 DesiredStatus=Active：激活 ActiveModelArtifactId 指定的模型（若 ContentHash 不匹配也重新激活，修复漂移）。</item>
/// <item>Revision 更高且 DesiredStatus=Inactive：停用模型，回退到 fallback 引擎。</item>
/// <item>Revision 相同但 ContentHash 不匹配：记录漂移告警（检测跨节点内容不一致）。</item>
/// </list>
///
/// <b>启动恢复</b>：首次执行全量同步时立即应用期望状态（激活/停用），
/// 而非仅记录 Revision 等待后续变更——全新节点启动后马上加载集群 Champion 模型。
/// 本地已激活同一 ContentHash 的模型（同进程内重复同步）由匹配检查跳过。
///
/// <b>并发控制</b>：Revision 字段用于乐观并发控制（CAS），仅当远端 Revision > 本地 Revision 时才应用。
/// HA 场景下由 <see cref="IClusterModelSlotStore.TryUpdateAsync"/> 的 CAS 保证单写者不回滚。
/// </remarks>
internal sealed class ModelStateReconcilerWorker : BackgroundService
{
    private readonly IClusterModelSlotStore _clusterSlotStore;
    private readonly IModelActivationManager _activationManager;
    private readonly IModelNodeAppliedStateStore? _appliedStateStore;
    private readonly IOptionsMonitor<ModelStateReconcilerOptions> _options;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ModelStateReconcilerWorker> _logger;
    private readonly string _instanceId;

    // 节点标识：机器名是跨进程重启保持稳定的节点身份，用于 model_node_applied_state 的 node_id；
    // _instanceId 仅用于日志区分同一机器上的多个进程。
    private readonly string _nodeId;

    // 本地已应用的最新集群槽位 Revision（AppliedClusterSlotRevision）。
    // 与本地引擎代次（ActiveGeneration，LocalEngineGeneration）是独立计数空间——
    // 本地代次跟踪"本地激活次数"，集群 Revision 跟踪"槽位期望变更"，两者不可混用，
    // 否则本地代次较高时会错误跳过集群期望状态（期望模型从未被加载）。
    private long _appliedClusterSlotRevision = -1;
    private int _initialSyncDone;

    public ModelStateReconcilerWorker(
        IClusterModelSlotStore clusterSlotStore,
        IModelActivationManager activationManager,
        IOptionsMonitor<ModelStateReconcilerOptions> options,
        IConfiguration configuration,
        ILogger<ModelStateReconcilerWorker> logger,
        IModelNodeAppliedStateStore? appliedStateStore = null)
    {
        _clusterSlotStore = clusterSlotStore;
        _activationManager = activationManager;
        _appliedStateStore = appliedStateStore;
        _options = options;
        _configuration = configuration;
        _logger = logger;
        _instanceId = Environment.MachineName + "-" + Guid.NewGuid().ToString("N")[..8];
        _nodeId = Environment.MachineName;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            _logger.LogInformation("ModelStateReconcilerWorker 已禁用（单节点模式）。");
            return;
        }

        _logger.LogInformation("ModelStateReconcilerWorker 已启动，实例 ID：{InstanceId}", _instanceId);

        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var succeeded = false;
            try
            {
                succeeded = await ReconcileAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ModelStateReconcilerWorker 轮询异常。");
            }

            // 指数退避：成功复位；连续失败按 BackoffBaseDelay × 2^n 增长，
            // 上限 BackoffMaxDelay / MaxRetryCount，避免故障风暴下高频空转。
            if (succeeded)
            {
                consecutiveFailures = 0;
            }
            else
            {
                consecutiveFailures++;
                if (consecutiveFailures == 1)
                {
                    _logger.LogWarning(
                        "ModelStateReconcilerWorker 应用期望状态失败，进入退避重试（连续失败 {ConsecutiveFailures} 次）。",
                        consecutiveFailures);
                }
                else
                {
                    _logger.LogWarning(
                        "ModelStateReconcilerWorker 应用期望状态仍失败（连续失败 {ConsecutiveFailures} 次，下次退避 {NextDelay}）。",
                        consecutiveFailures, ComputeBackoffDelay(options, consecutiveFailures));
                }
            }

            var delay = consecutiveFailures == 0
                ? options.PollInterval
                : ComputeBackoffDelay(options, consecutiveFailures);
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 计算退避延迟：连续失败 n 次 → min(BackoffBaseDelay × 2^(n-1), BackoffMaxDelay)；
    /// 指数在 MaxRetryCount 后封顶（保持 BackoffMaxDelay，不继续增长）。internal static 供单元测试。
    /// </summary>
    internal static TimeSpan ComputeBackoffDelay(ModelStateReconcilerOptions options, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return options.PollInterval;
        }

        var exponent = Math.Min(consecutiveFailures - 1, Math.Max(0, options.MaxRetryCount - 1));
        var baseMs = options.BackoffBaseDelay.TotalMilliseconds;
        var candidateMs = baseMs * Math.Pow(2.0, exponent);
        var cappedMs = Math.Min(candidateMs, options.BackoffMaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(cappedMs);
    }

    /// <summary>
    /// 执行一轮同步。返回 true 表示本轮成功（含无变更/已收敛/漂移已隔离等已处理情形）；
    /// 返回 false 表示应用期望状态失败（激活/停用失败或异常），由调用方驱动退避重试。
    /// </summary>
    private async Task<bool> ReconcileAsync(CancellationToken ct)
    {
        // 从单一 Champion 槽位（"primary"）读取期望状态。
        var slot = await _clusterSlotStore.GetAsync("primary", ct).ConfigureAwait(false);

        // slot 为 null 表示槽位尚未初始化（无任何 activate/retire 操作），无需同步。
        if (slot is null)
        {
            return true;
        }

        // 首次启动立即应用期望状态：不在此提前 return，而是继续走下方 apply 逻辑。
        // 全新节点（无本地激活）会立即激活 slot 中的 Champion 模型；
        // 同进程内已激活同一模型（ContentHash 匹配）由下方检查跳过，不会重复激活。
        // 注意：不用本地 ActiveGeneration 初始化 AppliedClusterSlotRevision——
        // 本地引擎代次与集群槽位 Revision 是独立计数空间，混用会导致
        // "本地代次高 → 跳过集群期望"的错误跳过（期望模型从未被加载）。
        if (Interlocked.Exchange(ref _initialSyncDone, 1) == 0)
        {
            // 从节点已应用状态恢复：仅当本地引擎与已应用记录一致（同宿主内 Reconciler 重建）时
            // 才从 AppliedRevision 继续收敛，避免重复应用已生效的期望状态；
            // 引擎为空（进程重启，冷启动）时绝不据此跳过——冷启动必须重新应用当前期望状态。
            if (_appliedStateStore is not null)
            {
                var applied = await _appliedStateStore.GetAsync(_nodeId, "primary", ct).ConfigureAwait(false);
                if (applied is not null)
                {
                    var engineMatchesApplied =
                        _activationManager.ActiveDescriptor is not null
                        && string.Equals(applied.ModelArtifactId, _activationManager.ActiveDescriptor.ModelArtifactId, StringComparison.Ordinal)
                        && string.Equals(applied.ContentHash, _activationManager.ContentHash, StringComparison.Ordinal);
                    if (engineMatchesApplied)
                    {
                        _appliedClusterSlotRevision = applied.AppliedRevision;
                    }

                    _logger.LogInformation(
                        "ModelStateReconcilerWorker 节点 {NodeId} 上次已应用 Revision={AppliedRevision}（模型 {ModelId}），引擎状态一致={EngineMatchesApplied}。",
                        _nodeId, applied.AppliedRevision, applied.ModelArtifactId, engineMatchesApplied);
                }
            }

            _logger.LogInformation(
                "ModelStateReconcilerWorker 初始同步，当前 slot Revision={Revision}，DesiredStatus={DesiredStatus}，立即应用。",
                slot.Revision, slot.DesiredStatus);
        }

        // 乐观并发控制：仅当远端 Revision > 本地已应用 Revision 时才应用变更
        if (slot.Revision <= _appliedClusterSlotRevision)
        {
            // Revision 相同但 ContentHash 不匹配 → 漂移：记录告警并将本节点标记为隔离
            // （漂移自动隔离）。隔离事实持久化到节点已应用状态，集群注册表据此
            // 计算 DriftedNodeCount / IsRolloutReady，使"Slot=A、Engine=B"错位可见、不可伪装收敛。
            if (slot.Revision == _appliedClusterSlotRevision
                && slot.DesiredStatus == ClusterModelSlotDesiredStatus.Active
                && !string.IsNullOrEmpty(slot.ContentHash)
                && !string.IsNullOrEmpty(slot.ActiveModelArtifactId)
                && !string.Equals(slot.ContentHash, _activationManager.ContentHash, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "检测到 ContentHash 漂移：模型 {ModelId} 期望 {DesiredHash}，实际 {ActualHash}。" +
                    "可能跨节点加载了不同内容的模型，节点 {NodeId} 已自动隔离。",
                    slot.ActiveModelArtifactId, slot.ContentHash, _activationManager.ContentHash, _nodeId);

                if (_appliedStateStore is not null)
                {
                    try
                    {
                        await _appliedStateStore.MarkIsolatedAsync(
                            _nodeId,
                            "primary",
                            $"ContentHash 漂移：期望 {slot.ContentHash}，实际 {_activationManager.ContentHash}。",
                            ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "标记节点 {NodeId} 漂移隔离失败（不影响本轮结果）：Revision {Revision}",
                            _nodeId, slot.Revision);
                    }
                }
            }
            return true;
        }

        try
        {
            if (slot.DesiredStatus == ClusterModelSlotDesiredStatus.Active
                && !string.IsNullOrEmpty(slot.ActiveModelArtifactId))
            {
                var modelId = slot.ActiveModelArtifactId!;

                // ContentHash 匹配检查 —— 若本地已激活同 ContentHash 的模型则跳过重复激活。
                if (string.Equals(_activationManager.ActiveDescriptor?.ModelArtifactId, modelId, StringComparison.Ordinal)
                    && string.Equals(_activationManager.ContentHash, slot.ContentHash, StringComparison.Ordinal))
                {
                    _logger.LogDebug(
                        "模型 {ModelId} 已激活且 ContentHash 匹配，跳过重复激活（Revision {Revision}）。",
                        modelId, slot.Revision);
                }
                else
                {
                    _logger.LogInformation(
                        "应用期望状态：激活模型 {ModelId}（Revision {Revision}）",
                        modelId, slot.Revision);
                    var onnxOptions = CreateDefaultOnnxOptions(enableWarmup: true);
                    var result = await _activationManager.ActivateAsync(modelId, onnxOptions, ct).ConfigureAwait(false);
                    if (!result.Success)
                    {
                        _logger.LogError(
                            "激活模型 {ModelId} 失败：{Error}（Revision {Revision}）",
                            modelId, result.Error, slot.Revision);
                        // 不更新 AppliedClusterSlotRevision，下次轮询重试（退避）
                        return false;
                    }
                }
            }
            else if (slot.DesiredStatus == ClusterModelSlotDesiredStatus.Inactive)
            {
                // 调用 DeactivateAsync 停用模型，回退到 fallback。
                _logger.LogInformation(
                    "应用期望状态：停用模型（Revision {Revision}）",
                    slot.Revision);
                var result = await _activationManager.DeactivateAsync(ct).ConfigureAwait(false);
                if (!result.Success)
                {
                    _logger.LogError(
                        "停用模型失败：{Error}（Revision {Revision}）",
                        result.Error, slot.Revision);
                    return false;
                }
            }

            _appliedClusterSlotRevision = slot.Revision;
            await RecordAppliedStateAsync(slot, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "应用期望状态失败：Revision {Revision}",
                slot.Revision);
            return false;
        }
    }

    /// <summary>
    /// 记录节点已应用状态（最佳努力持久化）：写入本节点对当前 slot 最后成功应用的 Revision
    /// 与本地引擎实际生效的模型内容。记录失败不影响已应用的期望状态。
    /// </summary>
    private async ValueTask RecordAppliedStateAsync(ClusterModelSlot slot, CancellationToken ct)
    {
        if (_appliedStateStore is null)
        {
            return;
        }

        try
        {
            var applied = new ModelNodeAppliedState
            {
                NodeId = _nodeId,
                SlotName = slot.SlotName,
                AppliedRevision = slot.Revision,
                ModelArtifactId = _activationManager.ActiveDescriptor?.ModelArtifactId,
                ContentHash = _activationManager.ContentHash,
                // 应用时刻本地引擎代次：与集群槽位 Revision 分离（Slot=A、Engine=B 错位可审计）。
                EngineGeneration = _activationManager.ActiveGeneration,
                AppliedAt = DateTimeOffset.UtcNow
            };
            await _appliedStateStore.UpsertAsync(applied, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "记录节点已应用状态失败（不影响已应用的期望状态）：Revision {Revision}",
                slot.Revision);
        }
    }

    /// <summary>
    /// 从 IConfiguration 读取默认 tensor 名，避免硬编码（与 ModelControlPlaneEndpoints 一致）。
    /// </summary>
    private OnnxInferenceEngineOptions CreateDefaultOnnxOptions(bool enableWarmup)
    {
        var inputTensorName = _configuration["ModelArtifact:DefaultInputTensorName"];
        if (string.IsNullOrWhiteSpace(inputTensorName))
        {
            inputTensorName = "input";
        }

        var scoreOutputName = _configuration["ModelArtifact:DefaultScoreOutputName"];
        if (string.IsNullOrWhiteSpace(scoreOutputName))
        {
            scoreOutputName = "score";
        }

        return new OnnxInferenceEngineOptions
        {
            InputTensorName = inputTensorName,
            ScoreOutputName = scoreOutputName,
            EnableWarmup = enableWarmup
        };
    }
}

/// <summary>
/// Model State Reconciler 配置选项。
/// </summary>
public sealed class ModelStateReconcilerOptions
{
    /// <summary>是否启用 Reconciler（HA 模式）。</summary>
    public bool Enabled { get; set; }

    /// <summary>轮询间隔（成功轮询后的正常等待时间）。</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>失败退避基准延迟（连续失败时第 1 次重试的等待时间）。</summary>
    public TimeSpan BackoffBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>失败退避最大延迟（指数增长的上限，防止长时间无界等待）。</summary>
    public TimeSpan BackoffMaxDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>退避指数上限（连续失败超过该次数后保持 BackoffMaxDelay，不继续指数增长）。</summary>
    public int MaxRetryCount { get; set; } = 8;
}