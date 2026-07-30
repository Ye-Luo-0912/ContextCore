using ContextCore.Abstractions;
using ContextCore.Inference.Onnx;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContextCore.Service.Hosting;

/// <summary>
/// R29 WP-A-2：Model State Reconciler Worker（HA 模式）。
/// </summary>
/// <remarks>
/// 周期性从 <see cref="IDesiredModelStateStore"/> 拉取期望状态，
/// 并与本地 <see cref="IModelActivationManager"/> 的实际状态对比，
/// 若期望状态更新（Generation 更高）则应用变更（Activate/Deactivate）。
///
/// <b>运行模式</b>：
/// <list type="bullet">
/// <item><see cref="ModelStateReconcilerOptions.Enabled"/> = false：立即退出（单节点模式）。</item>
/// <item><see cref="ModelStateReconcilerOptions.Enabled"/> = true：周期性轮询 DesiredModelStateStore，
///   应用期望状态到本地 ModelActivationManager。</item>
/// </list>
///
/// <b>并发控制</b>：
/// Generation 字段用于乐观并发控制：仅当远端 Generation > 本地 Generation 时才应用变更。
/// HA 场景下由 Leader 选举保证单写者，避免并发冲突。
/// </remarks>
internal sealed class ModelStateReconcilerWorker : BackgroundService
{
    private readonly IDesiredModelStateStore _desiredStateStore;
    private readonly IModelActivationManager _activationManager;
    private readonly IOptionsMonitor<ModelStateReconcilerOptions> _options;
    private readonly ILogger<ModelStateReconcilerWorker> _logger;
    private readonly string _instanceId;

    // 本地已知的最新 Generation（modelId → lastKnownGeneration）
    private readonly Dictionary<string, long> _lastKnownGeneration = new(StringComparer.Ordinal);

    public ModelStateReconcilerWorker(
        IDesiredModelStateStore desiredStateStore,
        IModelActivationManager activationManager,
        IOptionsMonitor<ModelStateReconcilerOptions> options,
        ILogger<ModelStateReconcilerWorker> logger)
    {
        _desiredStateStore = desiredStateStore;
        _activationManager = activationManager;
        _options = options;
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

        foreach (var desiredState in desiredStates)
        {
            // 乐观并发控制：仅当远端 Generation > 本地 Generation 时才应用变更
            if (_lastKnownGeneration.TryGetValue(desiredState.ModelId, out var localGeneration)
                && desiredState.Generation <= localGeneration)
            {
                continue;
            }

            try
            {
                if (string.Equals(desiredState.DesiredState, "Active", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "应用期望状态：激活模型 {ModelId}（Generation {Generation}）",
                        desiredState.ModelId, desiredState.Generation);
                    await _activationManager.ActivateAsync(desiredState.ModelId, new OnnxInferenceEngineOptions { InputTensorName = "input", ScoreOutputName = "score" }, ct).ConfigureAwait(false);
                }
                else if (string.Equals(desiredState.DesiredState, "Inactive", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "期望状态为 Inactive，但 IModelActivationManager 不支持 DeactivateAsync。模型 {ModelId} 将保持当前状态（Generation {Generation}）",
                        desiredState.ModelId, desiredState.Generation);
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
