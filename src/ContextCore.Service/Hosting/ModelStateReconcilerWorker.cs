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
/// 周期性从 <see cref="IDesiredModelStateStore"/> 拉取期望状态（单一 HA 真相源），
/// 并与本地 <see cref="IModelActivationManager"/> 的实际状态对比：
/// <list type="bullet">
/// <item>Generation 更高且 DesiredState=Active：激活模型（若 ContentHash 不匹配也重新激活，修复漂移）。</item>
/// <item>Generation 更高且 DesiredState=Inactive：停用模型，回退到 fallback 引擎。</item>
/// <item>Generation 相同但 ContentHash 不匹配：记录漂移告警（检测跨节点内容不一致）。</item>
/// </list>
///
/// <b>启动恢复</b>：首次执行全量同步，从 store 重建本地 _lastKnownGeneration，
/// 避免进程重启后重复应用已生效的期望状态。
///
/// <b>并发控制</b>：Generation 字段用于乐观并发控制（CAS），仅当远端 Generation > 本地 Generation 时才应用。
/// HA 场景下由 <see cref="IDesiredModelStateStore.SetAsync"/> 的 CAS 保证单写者不回滚。
/// </remarks>
internal sealed class ModelStateReconcilerWorker : BackgroundService
{
    private readonly IDesiredModelStateStore _desiredStateStore;
    private readonly IModelActivationManager _activationManager;
    private readonly IOptionsMonitor<ModelStateReconcilerOptions> _options;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ModelStateReconcilerWorker> _logger;
    private readonly string _instanceId;

    // 本地已知的最新 Generation（modelId → lastKnownGeneration）
    private readonly Dictionary<string, long> _lastKnownGeneration = new(StringComparer.Ordinal);
    private int _initialSyncDone;

    public ModelStateReconcilerWorker(
        IDesiredModelStateStore desiredStateStore,
        IModelActivationManager activationManager,
        IOptionsMonitor<ModelStateReconcilerOptions> options,
        IConfiguration configuration,
        ILogger<ModelStateReconcilerWorker> logger)
    {
        _desiredStateStore = desiredStateStore;
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
        var desiredStates = await _desiredStateStore.GetAllAsync(ct).ConfigureAwait(false);

        // P0-9：首次同步时从 store 重建 _lastKnownGeneration，避免重启后重复应用。
        // 后续轮询仅处理 Generation 更高的变更。
        if (Interlocked.Exchange(ref _initialSyncDone, 1) == 0)
        {
            foreach (var desiredState in desiredStates)
            {
                _lastKnownGeneration[desiredState.ModelId] = desiredState.Generation;
            }

            // 用本地 ActiveGeneration 初始化当前 active 模型的已知 Generation，
            // 避免重启后对同一模型重复 Activate（Generation 相同 → 跳过）。
            var activeId = _activationManager.ActiveDescriptor?.ModelArtifactId;
            if (activeId is not null && _activationManager.ActiveGeneration is { } localGen)
            {
                _lastKnownGeneration[activeId] = Math.Max(
                    _lastKnownGeneration.GetValueOrDefault(activeId),
                    localGen);
            }

            _logger.LogInformation("ModelStateReconcilerWorker 初始同步完成，已知 {Count} 个期望状态。", desiredStates.Count);
            return;
        }

        foreach (var desiredState in desiredStates)
        {
            // 乐观并发控制：仅当远端 Generation > 本地 Generation 时才应用变更
            if (_lastKnownGeneration.TryGetValue(desiredState.ModelId, out var localGeneration)
                && desiredState.Generation <= localGeneration)
            {
                // P0-9：Generation 相同但 ContentHash 不匹配 → 记录漂移告警
                if (desiredState.Generation == localGeneration
                    && string.Equals(desiredState.DesiredState, "Active", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(desiredState.ContentHash)
                    && !string.Equals(desiredState.ContentHash, _activationManager.ContentHash, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "检测到 ContentHash 漂移：模型 {ModelId} 期望 {DesiredHash}，实际 {ActualHash}。" +
                        "可能跨节点加载了不同内容的模型。",
                        desiredState.ModelId, desiredState.ContentHash, _activationManager.ContentHash);
                }
                continue;
            }

            try
            {
                if (string.Equals(desiredState.DesiredState, "Active", StringComparison.OrdinalIgnoreCase))
                {
                    // P0-9：ContentHash 匹配检查 —— 若本地已激活同 ContentHash 的模型则跳过重复激活。
                    if (string.Equals(_activationManager.ActiveDescriptor?.ModelArtifactId, desiredState.ModelId, StringComparison.Ordinal)
                        && string.Equals(_activationManager.ContentHash, desiredState.ContentHash, StringComparison.Ordinal))
                    {
                        _logger.LogDebug(
                            "模型 {ModelId} 已激活且 ContentHash 匹配，跳过重复激活（Generation {Generation}）。",
                            desiredState.ModelId, desiredState.Generation);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "应用期望状态：激活模型 {ModelId}（Generation {Generation}）",
                            desiredState.ModelId, desiredState.Generation);
                        var onnxOptions = CreateDefaultOnnxOptions(enableWarmup: true);
                        var result = await _activationManager.ActivateAsync(desiredState.ModelId, onnxOptions, ct).ConfigureAwait(false);
                        if (!result.Success)
                        {
                            _logger.LogError(
                                "激活模型 {ModelId} 失败：{Error}（Generation {Generation}）",
                                desiredState.ModelId, result.Error, desiredState.Generation);
                            // 不更新 _lastKnownGeneration，下次轮询重试
                            continue;
                        }
                    }
                }
                else if (string.Equals(desiredState.DesiredState, "Inactive", StringComparison.OrdinalIgnoreCase))
                {
                    // P0-9：调用 DeactivateAsync 停用模型，回退到 fallback。
                    _logger.LogInformation(
                        "应用期望状态：停用模型 {ModelId}（Generation {Generation}）",
                        desiredState.ModelId, desiredState.Generation);
                    var result = await _activationManager.DeactivateAsync(ct).ConfigureAwait(false);
                    if (!result.Success)
                    {
                        _logger.LogError(
                            "停用模型 {ModelId} 失败：{Error}（Generation {Generation}）",
                            desiredState.ModelId, result.Error, desiredState.Generation);
                        continue;
                    }
                }

                _lastKnownGeneration[desiredState.ModelId] = desiredState.Generation;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "应用期望状态失败：模型 {ModelId}，Generation {Generation}",
                    desiredState.ModelId, desiredState.Generation);
            }
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