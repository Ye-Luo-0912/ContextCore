using ContextCore.Abstractions;
using ContextCore.Inference.Onnx;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContextCore.Service.Hosting;

/// <summary>
/// R29 WP-A-2 / P0-9：Model State Reconciler Worker（HA 模式）。
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
/// <b>启动恢复</b>：首次执行全量同步，从 slot 重建本地 _lastKnownRevision，
/// 避免进程重启后重复应用已生效的期望状态。
///
/// <b>并发控制</b>：Revision 字段用于乐观并发控制（CAS），仅当远端 Revision > 本地 Revision 时才应用。
/// HA 场景下由 <see cref="IClusterModelSlotStore.TryUpdateAsync"/> 的 CAS 保证单写者不回滚。
/// </remarks>
internal sealed class ModelStateReconcilerWorker : BackgroundService
{
    private readonly IClusterModelSlotStore _clusterSlotStore;
    private readonly IModelActivationManager _activationManager;
    private readonly IOptionsMonitor<ModelStateReconcilerOptions> _options;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ModelStateReconcilerWorker> _logger;
    private readonly string _instanceId;

    // 本地已知的最新 Revision（单 slot "primary"）
    private long _lastKnownRevision = -1;
    private int _initialSyncDone;

    public ModelStateReconcilerWorker(
        IClusterModelSlotStore clusterSlotStore,
        IModelActivationManager activationManager,
        IOptionsMonitor<ModelStateReconcilerOptions> options,
        IConfiguration configuration,
        ILogger<ModelStateReconcilerWorker> logger)
    {
        _clusterSlotStore = clusterSlotStore;
        _activationManager = activationManager;
        _options = options;
        _configuration = configuration;
        _logger = logger;
        _instanceId = Environment.MachineName + "-" + Guid.NewGuid().ToString("N")[..8];
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

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ModelStateReconcilerWorker 轮询异常。");
            }

            await Task.Delay(options.PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        // P0-9：从单一 Champion 槽位（"primary"）读取期望状态。
        var slot = await _clusterSlotStore.GetAsync("primary", ct).ConfigureAwait(false);

        // slot 为 null 表示槽位尚未初始化（无任何 activate/retire 操作），无需同步。
        if (slot is null)
        {
            return;
        }

        // P0-9：首次同步时从 slot 重建 _lastKnownRevision，避免重启后重复应用。
        // 后续轮询仅处理 Revision 更高的变更。
        if (Interlocked.Exchange(ref _initialSyncDone, 1) == 0)
        {
            _lastKnownRevision = slot.Revision;

            // 用本地 ActiveGeneration 初始化已知 Revision，
            // 避免重启后对同一模型重复 Activate（Revision 相同 → 跳过）。
            if (_activationManager.ActiveDescriptor is not null
                && _activationManager.ActiveGeneration is { } localGen)
            {
                _lastKnownRevision = Math.Max(_lastKnownRevision, localGen);
            }

            _logger.LogInformation(
                "ModelStateReconcilerWorker 初始同步完成，当前 slot Revision={Revision}，DesiredStatus={DesiredStatus}。",
                slot.Revision, slot.DesiredStatus);
            return;
        }

        // 乐观并发控制：仅当远端 Revision > 本地 Revision 时才应用变更
        if (slot.Revision <= _lastKnownRevision)
        {
            // P0-9：Revision 相同但 ContentHash 不匹配 → 记录漂移告警
            if (slot.Revision == _lastKnownRevision
                && string.Equals(slot.DesiredStatus, "Active", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(slot.ContentHash)
                && !string.IsNullOrEmpty(slot.ActiveModelArtifactId)
                && !string.Equals(slot.ContentHash, _activationManager.ContentHash, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "检测到 ContentHash 漂移：模型 {ModelId} 期望 {DesiredHash}，实际 {ActualHash}。" +
                    "可能跨节点加载了不同内容的模型。",
                    slot.ActiveModelArtifactId, slot.ContentHash, _activationManager.ContentHash);
            }
            return;
        }

        try
        {
            if (string.Equals(slot.DesiredStatus, "Active", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(slot.ActiveModelArtifactId))
            {
                var modelId = slot.ActiveModelArtifactId!;

                // P0-9：ContentHash 匹配检查 —— 若本地已激活同 ContentHash 的模型则跳过重复激活。
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
                        // 不更新 _lastKnownRevision，下次轮询重试
                        return;
                    }
                }
            }
            else if (string.Equals(slot.DesiredStatus, "Inactive", StringComparison.OrdinalIgnoreCase))
            {
                // P0-9：调用 DeactivateAsync 停用模型，回退到 fallback。
                _logger.LogInformation(
                    "应用期望状态：停用模型（Revision {Revision}）",
                    slot.Revision);
                var result = await _activationManager.DeactivateAsync(ct).ConfigureAwait(false);
                if (!result.Success)
                {
                    _logger.LogError(
                        "停用模型失败：{Error}（Revision {Revision}）",
                        result.Error, slot.Revision);
                    return;
                }
            }

            _lastKnownRevision = slot.Revision;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "应用期望状态失败：Revision {Revision}",
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
/// R29 WP-A-2：Model State Reconciler 配置选项。
/// </summary>
public sealed class ModelStateReconcilerOptions
{
    /// <summary>是否启用 Reconciler（HA 模式）。</summary>
    public bool Enabled { get; set; }

    /// <summary>轮询间隔。</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);
}