using System.Collections.Concurrent;
using System.Diagnostics;
using ContextCore.Abstractions;

namespace ContextCore.Inference.Onnx;

// ===========================================================================
// Model Activation Manager 实现
//
// 目标：
//   1. 权威编排模型激活流程：IModelArtifactRegistry 读取 descriptor
//      → ICalibrationValidator 验证校准参数（P0-8）
//      → IFeatureSchemaValidator 验证 schema 存在性（P0-8）
//      → IOnnxInferenceSessionFactory 创建 session
//      → OnnxInferenceEngine 激活。
//   2. 作为 IBatchInferenceEngine 代理：激活前委托给 fallback（Deterministic），
//      激活后委托给 OnnxInferenceEngine，让消费方无需感知激活切换。
//   3. 线程安全：ActivateAsync 可在运行时调用，通过原子 ActiveModelHandle 切换引擎。
//
// 设计原则：
//   1. fail-safe：激活失败不影响现有推理（继续使用 fallback）。
//   2. 校准验证不通过时拒绝激活（Error 级违规）；Warning 级违规允许激活。
//   3. schema 不存在时拒绝激活（防止推理时 schema drift）。
//   4. 不捕获 OperationCanceledException（与项目约束一致）。
//
// 原子 Active Handle
//   - 用单个 volatile ActiveModelHandle 替换分开的 _activeEngine / _activeCounter，
//     推理时只读取一次 handle（Volatile.Read），确保 engine 与 counter 来自同一世代，
//     避免热切换发生在两次读取之间导致旧引擎请求被计入新 counter。
//   - 激活时创建新 handle（Generation+1），旧 handle 注册到延迟清理队列；
//     不在激活时立即 Dispose 旧引擎。
//
// LoadAndWarmupAsync
//   - 加载并 warmup 模型但不发布为 active；返回 StagedModelHandle。
//   - 用于 /warmup 端点：预热不应替换当前 active 模型。
//   - PromoteStagedAsync 将 Staged Handle 原子发布为 active。
// ===========================================================================

/// <summary>
/// 模型槽状态机。
/// Loading → Staged → Active → Retired → Draining → Disposed
///   - Loading：session 创建中（瞬时，构造完成即离开）
///   - Staged：已 warmup 但未发布为 Active（由 LoadAndWarmupAsync 产生）
///   - Active：已发布为当前推理引擎，新请求可 Increment counter
///   - Retired：已被新 Active 替换，counter 仍可能有 in-flight 请求递减
///   - Draining：等待 counter 归零的过渡态（延迟 Dispose 任务进入）
///   - Disposed：引擎已 Dispose，不可再使用
/// </summary>
internal enum ModelSlotState : byte
{
    Loading = 0,
    Staged = 1,
    Active = 2,
    Retired = 3,
    Draining = 4,
    Disposed = 5
}

/// <summary>
/// 原子 Active Handle — 把引擎、descriptor、引用计数器、世代号与槽状态绑定为单个可变对象。
/// 推理路径通过 Volatile.Read 一次性读取整个 handle，确保 engine 与 counter 始终来自同一世代，
/// 避免热切换发生在两次读取之间导致旧引擎请求被计入新 counter（进而让旧 Session 被提前释放）。
/// </summary>
/// <remarks>
/// State 字段使用 <see cref="Interlocked"/> 原子更新，实现 Loading → Staged → Active →
/// Retired → Draining → Disposed 状态机。TransitionTo 仅允许合法转换，非法转换返回 false。
/// </remarks>
internal sealed class ActiveModelHandle
{
    /// <summary>引擎实例。</summary>
    public IBatchInferenceEngine Engine { get; }

    /// <summary>模型工件描述符。</summary>
    public ModelArtifactDescriptor Descriptor { get; }

    /// <summary>引用计数器（跟踪 in-flight 请求数）。</summary>
    public ModelReferenceCounter Counter { get; }

    /// <summary>世代号（每次激活自增）。</summary>
    public long Generation { get; }

    private int _state;
    /// <summary>当前槽状态（原子读写）。</summary>
    public ModelSlotState State => (ModelSlotState)Volatile.Read(ref _state);

    /// <summary>构造 handle，初始状态为 <see cref="ModelSlotState.Loading"/>。</summary>
    public ActiveModelHandle(
        IBatchInferenceEngine engine,
        ModelArtifactDescriptor descriptor,
        ModelReferenceCounter counter,
        long generation)
    {
        Engine = engine;
        Descriptor = descriptor;
        Counter = counter;
        Generation = generation;
        Volatile.Write(ref _state, (int)ModelSlotState.Loading);
    }

    /// <summary>
    /// 原子状态转换。仅允许合法转换：
    /// Loading → Staged / Active；Staged → Active / Retired / Disposed；
    /// Active → Retired；Retired → Draining；Draining → Disposed。
    /// 非法转换返回 false（调用方据此处理并发冲突）。
    /// </summary>
    public bool TransitionTo(ModelSlotState newState)
    {
        while (true)
        {
            var oldState = (ModelSlotState)Volatile.Read(ref _state);
            if (!IsValidTransition(oldState, newState))
            {
                return false;
            }
            if (Interlocked.CompareExchange(ref _state, (int)newState, (int)oldState) == (int)oldState)
            {
                return true;
            }
            // CAS 失败：其他线程已修改状态，重试。
        }
    }

    private static bool IsValidTransition(ModelSlotState from, ModelSlotState to)
    {
        // 同状态不算转换（幂等）
        if (from == to) return true;
        return (from, to) switch
        {
            (ModelSlotState.Loading, ModelSlotState.Staged) => true,
            (ModelSlotState.Loading, ModelSlotState.Active) => true,
            (ModelSlotState.Loading, ModelSlotState.Disposed) => true,
            (ModelSlotState.Staged, ModelSlotState.Active) => true,
            (ModelSlotState.Staged, ModelSlotState.Retired) => true,
            (ModelSlotState.Staged, ModelSlotState.Disposed) => true,
            (ModelSlotState.Active, ModelSlotState.Retired) => true,
            (ModelSlotState.Retired, ModelSlotState.Draining) => true,
            (ModelSlotState.Retired, ModelSlotState.Disposed) => true,
            (ModelSlotState.Draining, ModelSlotState.Disposed) => true,
            _ => false
        };
    }
}

/// <summary>
/// 引用计数器 — 跟踪某个引擎实例上的 in-flight 推理请求数。
/// 每个 ActiveModelHandle 关联一个独立 counter；请求开始时捕获 handle 引用并 Increment，
/// 结束时 Decrement 同一 counter。热切换后旧请求递减的是旧 handle 的 counter（引用已捕获），
/// 新请求递增新 handle 的 counter，互不干扰。Dispose 任务等待旧 counter 归零后才释放旧引擎。
/// </summary>
internal sealed class ModelReferenceCounter
{
    private int _count;

    /// <summary>当前 in-flight 请求数（≥0）。</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>原子递增（请求开始时调用）。</summary>
    public void Increment() => Interlocked.Increment(ref _count);

    /// <summary>原子递减（请求结束时调用，包括异常路径）。</summary>
    public void Decrement() => Interlocked.Decrement(ref _count);
}

/// <summary>
/// 权威模型激活管理器实现。
/// 编排模型工件加载 → 验证 → ONNX 引擎激活的完整流程。
/// </summary>
/// <remarks>
/// 同时实现 <see cref="IBatchInferenceEngine"/> 作为代理：
/// 未激活时委托给 fallback（通常为 DeterministicBatchInferenceEngine），
/// 激活后委托给 OnnxInferenceEngine。
/// </remarks>
public sealed class ModelActivationManager : IModelActivationManager
{
    private readonly IModelArtifactRegistry _registry;
    private readonly ICalibrationValidator _calibrationValidator;
    private readonly IFeatureRegistry _featureRegistry;
    private readonly IOnnxInferenceSessionFactory _sessionFactory;
    private readonly IFallbackInferenceEngine _fallbackEngine;

    // fallback 引擎的永久租约（Generation=0，Dispose 为 no-op）。
    // 在构造时一次性创建并复用，避免每次 AcquireFallbackEngineLease 分配。
    private readonly FallbackEngineLease _fallbackLease;
    private readonly ICalibrationService? _calibrationService;

    // 原子 Active Handle — 单个 volatile 字段同时持有 engine + counter + descriptor + generation。
    // 推理路径通过 Volatile.Read 一次性读取整个 handle，保证 engine 与 counter 来自同一世代。
    private volatile ActiveModelHandle? _activeHandle;
    private long _generation;

    // Retired Handle 列表 — 所有已被新 Active 替换但仍在等待 in-flight 引用归零的旧 handle。
    // 替代原先的单一 _previousHandle：快速连续激活时更早的 oldPrevious 不再被立即 Dispose，
    // 而是加入此列表，由各自独立的延迟 Dispose 任务等待 counter 归零后再清理。
    private readonly List<ActiveModelHandle> _retiredHandles = new();
    private readonly object _retiredLock = new();

    private readonly object _activationLock = new();

    // 统一追踪后台 Dispose Task。DisposeAsync 时 await 全部完成，避免请求泄漏。
    private readonly List<Task> _backgroundDisposeTasks = new();
    private readonly object _backgroundTasksLock = new();

    // Dispose 取消令牌 — 用于取消所有后台 Dispose 任务的等待循环。
    private readonly CancellationTokenSource _disposedCts = new();
    private int _disposed;

    // Staged Handle 配置。
    //   - MaxStagedHandles：暂存表容量上限（防止 warmup 端点被滥用导致 OOM）。
    //   - StagedHandleTtl：Staged Handle 生存时间；超过后自动从暂存表移除并 Dispose。
    private const int MaxStagedHandles = 2;
    private static readonly TimeSpan StagedHandleTtl = TimeSpan.FromMinutes(5);

    // Staged Handle 暂存表 — LoadAndWarmupAsync 把已 warmup 的 handle 存入此表，
    // 调用方可通过 PromoteStagedAsync(handleId) 原子发布为 active。
    // 包含容量上限与 TTL 自动清理。
    private readonly ConcurrentDictionary<string, StagedModelHandle> _stagedHandles = new();
    private readonly object _stagedHandlesLock = new();

    // TTL 定时器 — 主动清理过期 Staged Handle，避免依赖被动调用 EvictExpiredStagedHandles。
    private readonly Timer? _stagedHandleTtlTimer;
    private static readonly TimeSpan StagedHandleTtlCheckInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 构造 ModelActivationManager。
    /// </summary>
    /// <param name="registry">模型工件注册表（从中读取 descriptor）。</param>
    /// <param name="calibrationValidator">校准参数验证器（加载时验证校准有效性）。</param>
    /// <param name="featureRegistry">特征注册表（从中查询 schema；加载时验证 schema 存在性）。</param>
    /// <param name="sessionFactory">ONNX session 工厂（创建推理会话）。</param>
    /// <param name="fallbackEngine">降级引擎（未激活时使用，通常为 DeterministicBatchInferenceEngine）。
    /// 子问题1：通过 IFallbackInferenceEngine 标记接口注入，避免 DI 容器解析 IBatchInferenceEngine
    /// 时回到 ModelActivationManager 自身（循环依赖）。</param>
    /// <param name="calibrationService">可选的校准服务（提供待验证的校准参数；为 null 时跳过校准验证）。</param>
    public ModelActivationManager(
        IModelArtifactRegistry registry,
        ICalibrationValidator calibrationValidator,
        IFeatureRegistry featureRegistry,
        IOnnxInferenceSessionFactory sessionFactory,
        IFallbackInferenceEngine fallbackEngine,
        ICalibrationService? calibrationService = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(calibrationValidator);
        ArgumentNullException.ThrowIfNull(featureRegistry);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(fallbackEngine);

        _registry = registry;
        _calibrationValidator = calibrationValidator;
        _featureRegistry = featureRegistry;
        _sessionFactory = sessionFactory;
        _fallbackEngine = fallbackEngine;
        _fallbackLease = new FallbackEngineLease(fallbackEngine);
        _calibrationService = calibrationService;

        // 启动 TTL 定时器，周期性清理过期 Staged Handle。
        _stagedHandleTtlTimer = new Timer(
            _ => EvictExpiredStagedHandles(),
            null,
            StagedHandleTtlCheckInterval,
            StagedHandleTtlCheckInterval);
    }

    /// <inheritdoc />
    public IBatchInferenceEngine? ActiveEngine => _activeHandle?.Engine;

    /// <inheritdoc />
    public ModelArtifactDescriptor? ActiveDescriptor => _activeHandle?.Descriptor;

    /// <inheritdoc />
    /// <remarks>
    /// 暴露当前 Active Handle 的世代号，让 <see cref="InferenceScheduler"/> 等上层组件
    /// 感知模型热切换。世代号变化时，已攒批的请求不应与新请求合并到同一 BatchKey。
    /// </remarks>
    public long? ActiveGeneration => _activeHandle?.Generation;

    /// <inheritdoc />
    /// <remarks>
    /// 捕获当前 Active Handle 的引擎并递增引用计数。返回的 lease 持有 counter 引用，
    /// Dispose 时递减。这保证捕获的引擎在 lease 存活期间不会被 Dispose（drain 任务等待 counter 归零）。
    /// <see cref="InferenceScheduler"/> 在入队时调用本方法捕获引擎，确保请求在入队时的世代上执行，
    /// 避免热切换后请求在新引擎上执行（cross-generation execution）。
    /// </remarks>
    public IInferenceEngineLease? AcquireEngineLease()
    {
        // 与 InferBatchAsync 一致 — 单次 Volatile.Read 捕获 handle（_activeHandle 字段已 volatile）。
        var handle = _activeHandle;
        if (handle is null)
        {
            return null;
        }
        handle.Counter.Increment();
        return new EngineLease(handle);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 返回 fallback 引擎的永久租约（Generation=0，Dispose 为 no-op）。
    /// fallback 引擎由 DI 容器管理生命周期，无需引用计数；调用方可任意次数 Dispose
    /// （<see cref="FallbackEngineLease.Dispose"/> 幂等安全）。
    /// 用于 <see cref="InferenceScheduler"/> 入队时无 Active Engine 的场景，
    /// 固定请求在 fallback 引擎上执行，避免排队期间模型激活后 cross-generation execution。
    /// </remarks>
    public IInferenceEngineLease AcquireFallbackEngineLease() => _fallbackLease;

    /// <inheritdoc />
    public string ModelVersion => (_activeHandle?.Engine ?? _fallbackEngine).ModelVersion;

    /// <inheritdoc />
    public InferenceEngineKind Kind => (_activeHandle?.Engine ?? _fallbackEngine).Kind;

    /// <inheritdoc />
    public string ContentHash => (_activeHandle?.Engine ?? _fallbackEngine).ContentHash;

    /// <inheritdoc />
    public string CalibrationVersion => (_activeHandle?.Engine ?? _fallbackEngine).CalibrationVersion;

    /// <inheritdoc />
    public async ValueTask<ModelActivationResult> ActivateAsync(
        string modelArtifactId,
        OnnxInferenceEngineOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelArtifactId);
        ArgumentNullException.ThrowIfNull(options);

        var descriptor = await _registry.GetAsync(modelArtifactId, cancellationToken).ConfigureAwait(false);
        if (descriptor is null)
        {
            return ModelActivationResult.Failed(
                $"未在 IModelArtifactRegistry 中找到 ModelArtifactId='{modelArtifactId}'。");
        }

        return await ActivateCoreAsync(descriptor, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ModelActivationResult> ActivateLatestAsync(
        string modelName,
        OnnxInferenceEngineOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(options);

        var descriptor = await _registry.GetLatestAsync(modelName, cancellationToken).ConfigureAwait(false);
        if (descriptor is null)
        {
            return ModelActivationResult.Failed(
                $"未在 IModelArtifactRegistry 中找到 ModelName='{modelName}' 的最新版本。");
        }

        return await ActivateCoreAsync(descriptor, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<StagedModelHandle> LoadAndWarmupAsync(
        string modelArtifactId,
        OnnxInferenceEngineOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelArtifactId);
        ArgumentNullException.ThrowIfNull(options);

        // Dispose 后拒绝新请求。
        if (Volatile.Read(ref _disposed) != 0)
        {
            return StagedModelHandle.Failed(
                Guid.NewGuid().ToString("N"),
                descriptor: null,
                "ModelActivationManagerDisposed：管理器已 Dispose，拒绝新的 warmup 请求。");
        }

        var handleId = Guid.NewGuid().ToString("N");

        var descriptor = await _registry.GetAsync(modelArtifactId, cancellationToken).ConfigureAwait(false);
        if (descriptor is null)
        {
            return StagedModelHandle.Failed(
                handleId,
                descriptor: null,
                $"未在 IModelArtifactRegistry 中找到 ModelArtifactId='{modelArtifactId}'。");
        }

        // 复用 ActivateCoreAsync 的加载 + 验证 + warmup 流程，但不发布为 active。
        var loaded = await LoadAndWarmupCoreAsync(descriptor, options, cancellationToken).ConfigureAwait(false);
        if (!loaded.Success)
        {
            return StagedModelHandle.Failed(handleId, descriptor, loaded.Error!, loaded.CalibrationValidation);
        }

        var staged = StagedModelHandle.Succeeded(
            handleId,
            loaded.Engine!,
            descriptor,
            loaded.CalibrationValidation);

        // 容量限制 + TTL 清理后再插入。
        // 先清理过期 Staged Handle（释放槽位），再检查容量；超出上限时 Dispose 新加载的引擎并返回失败。
        // check-then-insert 必须在锁内原子完成，防止并发 Warmup 超过 MaxStagedHandles。
        EvictExpiredStagedHandles();
        lock (_stagedHandlesLock)
        {
            if (_stagedHandles.Count >= MaxStagedHandles)
            {
                // 锁内同步 Dispose 不安全（可能阻塞）；标记后在锁外 Dispose。
            }
            else
            {
                _stagedHandles[handleId] = staged;
                return staged;
            }
        }

        await SafeDisposeEngineAsync(loaded.Engine!).ConfigureAwait(false);
        return StagedModelHandle.Failed(
            handleId,
            descriptor,
            $"StagedHandleCapacityExceeded：暂存表已满（上限 {MaxStagedHandles}），" +
            "请先 Promote 或丢弃现有 Staged Handle。");
    }

    /// <inheritdoc />
    public ValueTask<ModelActivationResult> PromoteStagedAsync(
        string stagedHandleId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedHandleId);

        // 原子移除 —— 使用 TryRemove 返回值确保并发 Promote 只有一个成功。
        // 两个并发 PromoteStagedAsync 都可能先 TryGetValue 读到同一个 staged，
        // 但 TryRemove 是原子的：只有一个返回 true，另一个返回 false 后立即失败返回。
        if (!_stagedHandles.TryRemove(stagedHandleId, out var staged) || !staged.Success || staged.Engine is null)
        {
            return new ValueTask<ModelActivationResult>(ModelActivationResult.Failed(
                $"未找到 Staged Handle '{stagedHandleId}'、该 handle 已失效或已被并发 Promote 取走。"));
        }

        // 原子发布：复用 PublishAtomic 把已 warmup 的引擎切到 active。
        // PromoteStaged 路径使用默认 grace period（无 options 上下文）。
        // staged.Descriptor 在 Success=true 时由 LoadAndWarmupAsync 保证非空，这里防御性校验。
        if (staged.Descriptor is null)
        {
            return new ValueTask<ModelActivationResult>(ModelActivationResult.Failed(
                $"Staged Handle '{stagedHandleId}' 的 Descriptor 为 null，无法发布。"));
        }
        var published = PublishAtomicWithGracePeriod(staged.Engine, staged.Descriptor, staged.CalibrationValidation, 30000);
        return new ValueTask<ModelActivationResult>(published);
    }

    /// <inheritdoc />
    /// <summary>
    /// 停用当前 Active Engine，回退到 fallback。
    /// 复用 PublishAtomicWithGracePeriod 的 Retired/drain 机制确保 in-flight 请求安全完成。
    /// 无 Active Engine 时返回 Success（幂等）。
    /// </summary>
    public ValueTask<ModelActivationResult> DeactivateAsync(CancellationToken cancellationToken = default)
    {
        // Dispose 后拒绝停用。
        if (Volatile.Read(ref _disposed) != 0)
        {
            return new ValueTask<ModelActivationResult>(ModelActivationResult.Failed(
                "ModelActivationManagerDisposed：管理器已 Dispose，拒绝停用。"));
        }

        ActiveModelHandle? oldActive;
        lock (_activationLock)
        {
            oldActive = _activeHandle;
            _activeHandle = null; // 回退到 fallback 引擎

            if (oldActive is not null)
            {
                oldActive.TransitionTo(ModelSlotState.Retired);
                lock (_retiredLock)
                {
                    _retiredHandles.Add(oldActive);
                }
            }
        }

        // 调度延迟 Dispose oldActive（与 ActivateAsync 切换使用相同的 drain 机制）。
        // fallback engine 由外部 DI 容器管理，不 Dispose。
        if (oldActive is not null && !ReferenceEquals(oldActive.Engine, _fallbackEngine))
        {
            ScheduleRetiredHandleDrain(oldActive, 30000);
        }

        return new ValueTask<ModelActivationResult>(ModelActivationResult.Deactivated(oldActive?.Descriptor));
    }

    /// <summary>
    /// 清理过期的 Staged Handle（StagedAt + TTL 已过）。
    /// 移除后 best-effort Dispose 其引擎，避免资源泄漏。
    /// </summary>
    private void EvictExpiredStagedHandles()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _stagedHandles)
        {
            if (now - kv.Value.StagedAt > StagedHandleTtl)
            {
                if (_stagedHandles.TryRemove(kv.Key, out var removed) && removed.Success && removed.Engine is not null)
                {
                    var engineRef = removed.Engine;
                    var task = Task.Run(() => SafeDisposeEngineAsync(engineRef));
                    TrackBackgroundDisposeTask(task);
                }
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<BatchInferenceResult> InferAsync(
        BatchInferenceRequest request,
        CancellationToken ct = default)
    {
        // 通过 Volatile.Read 一次性捕获整个 ActiveModelHandle（engine + counter + generation）。
        // 热切换发生在读取之后时，本次请求继续使用旧 handle 的 engine 与 counter，
        // 旧 counter 仅被本次请求递减，不会污染新 counter；新 counter 也不会被本次请求递减。
        var handle = _activeHandle;
        if (handle is null)
        {
            return await _fallbackEngine.InferAsync(request, ct).ConfigureAwait(false);
        }

        handle.Counter.Increment();
        try
        {
            return await handle.Engine.InferAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            handle.Counter.Decrement();
        }
    }

    /// <inheritdoc />
    public async ValueTask<BatchInferenceResult> InferBatchAsync(
        FeatureBatch batch,
        CancellationToken ct = default)
    {
        // 与 InferAsync 一致 — 单次 Volatile.Read 捕获 handle，避免 engine/counter 错配。
        var handle = _activeHandle;
        if (handle is null)
        {
            return await _fallbackEngine.InferBatchAsync(batch, ct).ConfigureAwait(false);
        }

        handle.Counter.Increment();
        try
        {
            return await handle.Engine.InferBatchAsync(batch, ct).ConfigureAwait(false);
        }
        finally
        {
            handle.Counter.Decrement();
        }
    }

    // -----------------------------------------------------------------------
    // 激活核心流程：descriptor → 校准验证 → schema 验证 → session 创建 → Golden Probe → 原子发布
    // -----------------------------------------------------------------------

    private async ValueTask<ModelActivationResult> ActivateCoreAsync(
        ModelArtifactDescriptor descriptor,
        OnnxInferenceEngineOptions options,
        CancellationToken cancellationToken)
    {
        // Step 1：校准验证
        // 精确 CalibrationVersion 绑定 + fail-closed。
        //   - descriptor.CalibrationVersion == "default-v1"：保留兼容路径（calibrationService 缺失或参数未注册时跳过严格校验）
        //   - 非 default-v1：必须找到 Version 精确匹配的参数；未命中即拒绝激活（fail-closed）
        var calValidation = ValidateCalibrationForDescriptor(descriptor);
        if (calValidation is { IsFailed: true } failed)
        {
            return ModelActivationResult.Failed(
                failed.Error!,
                descriptor,
                failed.Result,
                schemaError: null);
        }
        var calResult = calValidation?.Result;

        // Step 2：schema 存在性验证（descriptor.FeatureSchemaVersion 必须在 IFeatureRegistry 中已注册）
        var schema = _featureRegistry.Get(descriptor.FeatureSchemaVersion);
        if (schema is null)
        {
            var schemaError = $"特征 schema 版本 '{descriptor.FeatureSchemaVersion}' 未在 IFeatureRegistry 中注册；" +
                              "无法激活模型（推理时会触发 schema drift）。";
            return ModelActivationResult.Failed(
                schemaError,
                descriptor,
                calResult,
                schemaError);
        }

        // Step 3：创建 ONNX session
        IOnnxInferenceSession session;
        try
        {
            session = await _sessionFactory.CreateAsync(options, descriptor, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            return ModelActivationResult.Failed(
                $"ONNX 模型文件未找到：{ex.Message}",
                descriptor,
                calResult);
        }
        catch (InvalidOperationException ex)
        {
            return ModelActivationResult.Failed(
                $"ONNX session 创建失败：{ex.Message}",
                descriptor,
                calResult);
        }
        catch (Microsoft.ML.OnnxRuntime.OnnxRuntimeException ex)
        {
            // hash 校验通过但文件非有效 ONNX（如损坏、非 ONNX 格式）时，InferenceSession 构造抛出
            // OnnxRuntimeException。将其转为 Failed 结果（错误不含 ModelFileHashMismatch），
            // 让激活流程 fail-safe（不影响现有推理），而非让异常向上传播。
            return ModelActivationResult.Failed(
                $"ONNX session 创建失败：{ex.Message}",
                descriptor,
                calResult);
        }

        // Step 4：构造 OnnxInferenceEngine
        var engine = new OnnxInferenceEngine(
            session,
            options,
            calibrationVersion: descriptor.CalibrationVersion);

        // 子问题2：Golden Probe — 发布引擎前用 schema.FeatureCount 宽度的全 0 batch 执行一次推理，
        // 验证模型可用性（输出非空、值有限、confidence 在 [0,1]）。失败时不发布引擎，返回 Failed。
        // 同时把引擎标记为已 warmup（_warmedUp=1），后续 InferBatchAsync 跳过 lazy warmup。
        var goldenProbeError = await RunGoldenProbeAsync(engine, schema, options, cancellationToken).ConfigureAwait(false);
        if (goldenProbeError is not null)
        {
            // Golden Probe 失败：Dispose 已创建的 session/engine，避免资源泄漏。
            await SafeDisposeEngineAsync(engine).ConfigureAwait(false);
            return ModelActivationResult.Failed(
                $"Golden Probe 失败：{goldenProbeError}",
                descriptor,
                calResult);
        }

        return PublishAtomic(engine, descriptor, calResult, options);
    }

    /// <summary>
    /// LoadAndWarmupCoreAsync — 执行校准 + schema + session + Golden Probe warmup，
    /// 但不发布为 active。返回 (Success, Engine, Error, CalibrationValidation) 元组式结果。
    /// </summary>
    private async ValueTask<(bool Success, IBatchInferenceEngine? Engine, string? Error, CalibrationValidationResult? CalibrationValidation)> LoadAndWarmupCoreAsync(
        ModelArtifactDescriptor descriptor,
        OnnxInferenceEngineOptions options,
        CancellationToken cancellationToken)
    {
        // Step 1：校准验证（精确 CalibrationVersion 绑定 + fail-closed）
        var calValidation = ValidateCalibrationForDescriptor(descriptor);
        if (calValidation is { IsFailed: true } failed)
        {
            return (false, null, failed.Error!, failed.Result);
        }
        var calResult = calValidation?.Result;

        // Step 2：schema 存在性验证
        var schema = _featureRegistry.Get(descriptor.FeatureSchemaVersion);
        if (schema is null)
        {
            return (false, null,
                $"特征 schema 版本 '{descriptor.FeatureSchemaVersion}' 未在 IFeatureRegistry 中注册；无法 warmup 模型。",
                calResult);
        }

        // Step 3：创建 ONNX session
        IOnnxInferenceSession session;
        try
        {
            session = await _sessionFactory.CreateAsync(options, descriptor, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            return (false, null, $"ONNX 模型文件未找到：{ex.Message}", calResult);
        }
        catch (InvalidOperationException ex)
        {
            return (false, null, $"ONNX session 创建失败：{ex.Message}", calResult);
        }
        catch (Microsoft.ML.OnnxRuntime.OnnxRuntimeException ex)
        {
            return (false, null, $"ONNX session 创建失败：{ex.Message}", calResult);
        }

        var engine = new OnnxInferenceEngine(
            session,
            options,
            calibrationVersion: descriptor.CalibrationVersion);

        // Step 4：Golden Probe warmup（不发布为 active）
        var goldenProbeError = await RunGoldenProbeAsync(engine, schema, options, cancellationToken).ConfigureAwait(false);
        if (goldenProbeError is not null)
        {
            await SafeDisposeEngineAsync(engine).ConfigureAwait(false);
            return (false, null, $"Golden Probe 失败：{goldenProbeError}", calResult);
        }

        return (true, engine, null, calResult);
    }

    /// <summary>
    /// 校准验证辅助方法 — 精确 CalibrationVersion 绑定 + fail-closed。
    /// 返回 Failed=true 表示校准失败（应拒绝激活）；
    /// 返回 Failed=false + Result 非空表示校验通过（含 Warning/Info 级违规但仍允许激活）。
    /// </summary>
    /// <remarks>
    /// 真正 fail-closed —— 不再做 default-v1 例外。
    /// <list type="bullet">
    /// <item>calibrationService 为 null → 拒绝激活（不再因 default-v1 跳过）。</item>
    /// <item>GetParametersForVersion 未命中 → 拒绝激活（不再因 default-v1 跳过）。</item>
    /// <item>命中精确版本：执行统计有效性校验，不通过则拒绝激活。</item>
    /// </list>
    /// descriptor.CalibrationVersion 为空/空白时按 default-v1 处理（向后兼容旧 descriptor 的版本解析）。
    /// </remarks>
    private CalibrationValidationOutcome? ValidateCalibrationForDescriptor(ModelArtifactDescriptor descriptor)
    {
        const string defaultVersion = "default-v1";
        var expectedVersion = string.IsNullOrWhiteSpace(descriptor.CalibrationVersion)
            ? defaultVersion
            : descriptor.CalibrationVersion;

        // fail-closed —— calibrationService 缺失即拒绝激活，不再为 default-v1 开例外。
        if (_calibrationService is null)
        {
            return CalibrationValidationOutcome.Failed(
                $"校准验证失败：ICalibrationService 未注册；fail-closed 拒绝激活" +
                $"（expectedVersion='{expectedVersion}'）。" +
                "请在 DI 中注册 ICalibrationService，或显式注册匹配版本的校准参数。",
                calResult: null);
        }

        // 精确版本绑定 — 通过 GetParametersForVersion 按 modelName + version 精确查找
        var parameters = _calibrationService.GetParametersForVersion(descriptor.ModelArtifactId, expectedVersion)
            ?? _calibrationService.GetParametersForVersion(descriptor.ModelName, expectedVersion);

        // fail-closed —— 参数未命中即拒绝激活，不再为 default-v1 开例外。
        if (parameters is null)
        {
            return CalibrationValidationOutcome.Failed(
                $"校准验证失败：未找到 ModelArtifactId='{descriptor.ModelArtifactId}' / ModelName='{descriptor.ModelName}' " +
                $"且 Version='{expectedVersion}' 精确匹配的校准参数；fail-closed 拒绝激活。" +
                "请在 ICalibrationService 中注册匹配版本的参数。",
                calResult: null);
        }

        // 命中精确版本：执行统计有效性校验
        var calResult = _calibrationValidator.Validate(parameters, descriptor.ModelArtifactId);
        if (!calResult.IsValid)
        {
            return CalibrationValidationOutcome.Failed(
                $"校准验证失败：{calResult.Error}",
                calResult);
        }
        return CalibrationValidationOutcome.Succeeded(calResult);
    }

    /// <summary>校准验证结果容器。</summary>
    private sealed class CalibrationValidationOutcome
    {
        public bool IsFailed { get; init; }
        public string? Error { get; init; }
        public CalibrationValidationResult? Result { get; init; }

        public static CalibrationValidationOutcome Failed(string error, CalibrationValidationResult? calResult)
            => new() { IsFailed = true, Error = error, Result = calResult };

        public static CalibrationValidationOutcome Succeeded(CalibrationValidationResult calResult)
            => new() { IsFailed = false, Error = null, Result = calResult };
    }

    /// <summary>
    /// 原子发布 — 把已 warmup 的引擎切到 _activeHandle（Generation+1），
    /// 旧 handle 加入 <see cref="_retiredHandles"/> 并标记 Retired，调度延迟 Dispose（等待 in-flight 引用归零）。
    /// </summary>
    private ModelActivationResult PublishAtomic(
        IBatchInferenceEngine engine,
        ModelArtifactDescriptor descriptor,
        CalibrationValidationResult? calResult,
        OnnxInferenceEngineOptions options)
    {
        return PublishAtomicWithGracePeriod(engine, descriptor, calResult, options.PreviousEngineGracePeriodMs);
    }

    /// <summary>原子发布的内部实现，直接传入 gracePeriodMs。</summary>
    /// <remarks>
    /// 使用 <see cref="_retiredHandles"/> 列表管理所有被替换的旧 handle，
    /// 每个 handle 独立等待其 counter 归零后再 Dispose。快速连续激活时更早的 oldPrevious
    /// 不再被立即 Dispose，而是各自走自己的 drain 流程，避免误删仍有 in-flight 请求的引擎。
    /// </remarks>
    private ModelActivationResult PublishAtomicWithGracePeriod(
        IBatchInferenceEngine engine,
        ModelArtifactDescriptor descriptor,
        CalibrationValidationResult? calResult,
        int gracePeriodMs)
    {
        // Dispose 后拒绝激活。
        if (Volatile.Read(ref _disposed) != 0)
        {
            return ModelActivationResult.Failed(
                "ModelActivationManagerDisposed：管理器已 Dispose，拒绝激活。",
                descriptor,
                calResult);
        }

        ActiveModelHandle? oldActive;
        ActiveModelHandle newHandle;

        // 在 lock 内原子递增 Generation 并切换 _activeHandle，确保 Generation 与 handle 切换的原子性。
        lock (_activationLock)
        {
            var newGeneration = unchecked(Interlocked.Increment(ref _generation));
            newHandle = new ActiveModelHandle(
                engine,
                descriptor,
                new ModelReferenceCounter(),
                newGeneration);

            oldActive = _activeHandle;

            // 新 handle 转为 Active（从 Loading）。
            newHandle.TransitionTo(ModelSlotState.Active);
            // 用 volatile 写保证 _activeHandle 的写入对其他线程可见：
            // _activeHandle 字段本身已声明 volatile，赋值即 release 屏障。
            _activeHandle = newHandle;

            // 把 oldActive 加入 Retired 列表并标记 Retired 状态。
            // 每个 Retired handle 独立 drain，不再用单一 _previousHandle 互相覆盖。
            if (oldActive is not null)
            {
                oldActive.TransitionTo(ModelSlotState.Retired);
                lock (_retiredLock)
                {
                    _retiredHandles.Add(oldActive);
                }
            }
        }

        // 调度延迟 Dispose oldActive（等待 in-flight 引用归零 + grace period）。
        // fallback engine 由外部 DI 容器管理，这里不 Dispose。
        // 复用 ScheduleRetiredHandleDrain（与 DeactivateAsync 共享 drain 逻辑）。
        if (oldActive is not null && !ReferenceEquals(oldActive.Engine, _fallbackEngine))
        {
            ScheduleRetiredHandleDrain(oldActive, gracePeriodMs);
        }

        return ModelActivationResult.Succeeded(descriptor, engine, calResult ?? new CalibrationValidationResult
        {
            IsValid = true,
            Error = null,
            Violations = Array.Empty<CalibrationViolation>()
        });
    }

    /// <summary>
    /// 调度 Retired Handle 的延迟 Dispose（等待 grace period + in-flight 引用归零）。
    /// 从 PublishAtomicWithGracePeriod 提取，供 DeactivateAsync 复用，确保 drain 行为一致。
    /// </summary>
    /// <param name="oldHandle">已标记 Retired 的旧 handle。</param>
    /// <param name="gracePeriodMs">grace period 毫秒数（先等待再 drain）。</param>
    private void ScheduleRetiredHandleDrain(ActiveModelHandle oldHandle, int gracePeriodMs)
    {
        var oldHandleForClosure = oldHandle;
        var drainTask = Task.Run(async () =>
        {
            try
            {
                var disposedToken = _disposedCts.Token;

                // 先等待 grace period（时间兜底），再检查旧 handle 引用计数。
                try
                {
                    await Task.Delay(gracePeriodMs, disposedToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (disposedToken.IsCancellationRequested)
                {
                    // Manager 已 Dispose：跳过 grace 等待，进入 drain。
                }

                // 状态机进入 Draining（Retired → Draining）。
                if (!oldHandleForClosure.TransitionTo(ModelSlotState.Draining))
                {
                    return;
                }

                // 自旋等待旧 counter 归零（最多再等 gracePeriodMs，避免无限等待）。
                var drainDeadline = Stopwatch.GetTimestamp();
                var drainTimeoutTicks = TimeSpan.FromMilliseconds(gracePeriodMs).Ticks;
                var drainTimedOut = false;
                while (oldHandleForClosure.Counter.Count > 0)
                {
                    if (disposedToken.IsCancellationRequested)
                    {
                        break;
                    }
                    if (Stopwatch.GetElapsedTime(drainDeadline).Ticks > drainTimeoutTicks)
                    {
                        drainTimedOut = true;
                        break;
                    }
                    try
                    {
                        await Task.Delay(10, disposedToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (disposedToken.IsCancellationRequested)
                    {
                        break;
                    }
                }

                if (drainTimedOut && oldHandleForClosure.Counter.Count > 0)
                {
                    System.Diagnostics.Trace.TraceWarning(
                        "[ModelActivationManager] Retired handle drain timeout: {0} in-flight requests still referencing old engine (gen {1}). " +
                        "Keeping old engine alive to avoid ORT corruption. It will be disposed when references release.",
                        oldHandleForClosure.Counter.Count,
                        oldHandleForClosure.Generation);
                    oldHandleForClosure.TransitionTo(ModelSlotState.Retired);
                    return;
                }

                await RetireAndDisposeHandleAsync(oldHandleForClosure).ConfigureAwait(false);
            }
            catch
            {
                // best-effort：调度失败不影响激活/停用流程。
            }
        });
        TrackBackgroundDisposeTask(drainTask);
    }

    /// <summary>
    /// 从 <see cref="_retiredHandles"/> 移除指定 handle 并 Dispose 其引擎（Draining → Disposed）。
    /// 幂等：若 handle 已 Disposed 则直接返回。
    /// </summary>
    private async ValueTask RetireAndDisposeHandleAsync(ActiveModelHandle handle)
    {
        lock (_retiredLock)
        {
            _retiredHandles.Remove(handle);
        }
        // Draining → Disposed（或 Retired → Disposed 兜底）。
        if (!handle.TransitionTo(ModelSlotState.Disposed))
        {
            // 已 Disposed：幂等返回。
            return;
        }
        await SafeDisposeHandleAsync(handle).ConfigureAwait(false);
    }

    /// <summary>
    /// 追踪后台 Dispose Task，DisposeAsync 时统一 await。
    /// 清理已完成任务，防止列表无界增长。
    /// </summary>
    private void TrackBackgroundDisposeTask(Task t)
    {
        lock (_backgroundTasksLock)
        {
            for (var i = _backgroundDisposeTasks.Count - 1; i >= 0; i--)
            {
                if (_backgroundDisposeTasks[i].IsCompleted)
                {
                    _backgroundDisposeTasks.RemoveAt(i);
                }
            }
            _backgroundDisposeTasks.Add(t);
        }
    }

    private Task[] SnapshotBackgroundDisposeTasks()
    {
        lock (_backgroundTasksLock)
        {
            return _backgroundDisposeTasks.ToArray();
        }
    }

    /// <summary>
    /// 子问题2：Golden Probe — 用 schema.FeatureCount 宽度的全 0 warmup batch 调用引擎推理，
    /// 验证输出可用性。同时把引擎标记为已 warmup，跳过后续 lazy warmup。
    /// </summary>
    /// <param name="engine">待验证的 ONNX 引擎。</param>
    /// <param name="schema">模型对应的特征 schema（用于确定 warmup batch 宽度）。</param>
    /// <param name="options">引擎配置（用于 warmup 超时）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>验证失败时返回错误消息；成功时返回 null。</returns>
    private static async ValueTask<string?> RunGoldenProbeAsync(
        OnnxInferenceEngine engine,
        FeatureSchema schema,
        OnnxInferenceEngineOptions options,
        CancellationToken cancellationToken)
    {
        var featureCount = schema.Features.Count;
        if (featureCount <= 0)
        {
            // schema 无特征列：跳过 Golden Probe（无法构造 warmup batch），不视为失败。
            // 标记引擎为已 warmup，跳过后续 lazy warmup。
            // await WarmupAsync 确保 warmup 完成，避免 fire-and-forget 导致的资源泄漏。
            await engine.WarmupAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        // 非 float 输入模型（如 embedding 模型的 int64 input_ids）：跳过 float Golden Probe。
        // Golden Probe 构造的是 float warmup batch，对 int64 输入模型必然触发类型不匹配错误。
        // 跳过 float probe 让激活成功，真实推理时由 ONNX Runtime 优雅报告类型不匹配（Succeeded=false）。
        // 基本 WarmupAsync 会失败但被静默吞掉（重置 _warmedUp=0），不影响激活与后续推理的优雅降级。
        if (!engine.SupportsFloatInput)
        {
            await engine.WarmupAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        // 构造 1 行 × FeatureCount 列全 0 的 warmup batch。
        var featureNames = new string[featureCount];
        for (var j = 0; j < featureCount; j++)
        {
            featureNames[j] = schema.Features[j].Name;
        }

        var warmupBatch = new FeatureBatch
        {
            SchemaVersion = schema.Version,
            Values = new float[featureCount], // 全 0
            RowCount = 1,
            FeatureCount = featureCount,
            FeatureNames = featureNames
        };

        BatchInferenceResult probeResult;
        try
        {
            // 子问题2：调用 WarmupAsync(batch) 重载，原子设置 _warmedUp=1 并执行推理。
            probeResult = await engine.WarmupAsync(warmupBatch, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"warmup 推理抛出异常：{ex.GetType().Name}: {ex.Message}";
        }

        if (!probeResult.Succeeded)
        {
            return $"warmup 推理报告失败：{probeResult.Error}";
        }

        // 验证 1：Output count > 0
        if (probeResult.Outputs.Count == 0)
        {
            return "warmup 输出为空（Outputs.Count == 0）";
        }

        // 验证 2 & 3：每个输出值有限（非 NaN/Infinity），confidence 在 [0,1]
        for (var i = 0; i < probeResult.Outputs.Count; i++)
        {
            var output = probeResult.Outputs[i];
            if (double.IsNaN(output.Score) || double.IsInfinity(output.Score))
            {
                return $"warmup 输出[{i}].Score 非有限（Score={output.Score}）";
            }
            if (double.IsNaN(output.Confidence) || double.IsInfinity(output.Confidence))
            {
                return $"warmup 输出[{i}].Confidence 非有限（Confidence={output.Confidence}）";
            }
            if (output.Confidence < 0.0 || output.Confidence > 1.0)
            {
                return $"warmup 输出[{i}].Confidence 越界 [0,1]（Confidence={output.Confidence}）";
            }
        }

        // 可选：latency threshold 检查（warmup 耗时 < 配置阈值的 2 倍，留余量）
        var latencyThresholdMs = options.InferenceTimeoutMs > 0
            ? options.InferenceTimeoutMs * 2
            : 10000;
        if (probeResult.Duration.TotalMilliseconds > latencyThresholdMs)
        {
            return $"warmup 耗时 {probeResult.Duration.TotalMilliseconds:F0}ms 超过阈值 {latencyThresholdMs}ms";
        }

        return null; // 验证通过
    }

    /// <summary>
    /// 子问题3：best-effort Dispose 引擎实例。
    /// 同时支持 IAsyncDisposable 与 IDisposable；失败时吞异常（不影响主流程）。
    /// </summary>
    /// <param name="engine">待 Dispose 的引擎。</param>
    private static async ValueTask SafeDisposeEngineAsync(IBatchInferenceEngine engine)
    {
        try
        {
            if (engine is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (engine is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch
        {
            // best-effort：Dispose 失败不影响激活流程。
        }
    }

    /// <summary>
    /// best-effort Dispose 整个 ActiveModelHandle（实际仅 Dispose Engine；counter 无需释放）。
    /// </summary>
    private static ValueTask SafeDisposeHandleAsync(ActiveModelHandle handle)
        => SafeDisposeEngineAsync(handle.Engine);

    /// <summary>
    /// Dispose ModelActivationManager。
    /// 取消所有后台 Dispose Task 的等待，等待所有 Retired Handle 引用归零后 Dispose，
    /// 最后 Dispose 当前 Active Handle 与 fallback（若可 Dispose）。
    /// 幂等：多次调用安全。
    /// </summary>
    /// <remarks>
    /// 实现步骤：
    /// 1. 标记 _disposed=1，拒绝后续激活/warmup 请求。
    /// 2. 触发 _disposedCts 取消所有后台 drain 任务的 grace 等待。
    /// 3. 快照当前 Active Handle 与所有 Retired Handle，逐一等待 counter 归零（带超时）后 Dispose。
    /// 4. await 所有后台 Dispose Task 完成（已被取消的任务会快速结束）。
    /// 5. 清理 Staged Handles（Dispose 其引擎）。
    /// 6. Dispose _disposedCts。
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // 停止 TTL 定时器。
        if (_stagedHandleTtlTimer is not null)
        {
            await _stagedHandleTtlTimer.DisposeAsync().ConfigureAwait(false);
        }

        // 1. 取消所有后台 drain 任务的 grace 等待。
        _disposedCts.Cancel();

        // 2. 快照需要清理的 handles。
        ActiveModelHandle? activeSnapshot;
        List<ActiveModelHandle> retiredSnapshot;
        lock (_activationLock)
        {
            activeSnapshot = _activeHandle;
            _activeHandle = null;
        }
        lock (_retiredLock)
        {
            retiredSnapshot = new List<ActiveModelHandle>(_retiredHandles);
            _retiredHandles.Clear();
        }

        // 3. 逐一等待 counter 归零（带超时）后 Dispose。
        //    Active handle 上的 in-flight 请求需要先完成；Retired handles 同理。
        var allHandles = new List<ActiveModelHandle>(retiredSnapshot.Count + 1);
        if (activeSnapshot is not null && !ReferenceEquals(activeSnapshot.Engine, _fallbackEngine))
        {
            allHandles.Add(activeSnapshot);
        }
        foreach (var h in retiredSnapshot)
        {
            if (!ReferenceEquals(h.Engine, _fallbackEngine))
            {
                allHandles.Add(h);
            }
        }

        foreach (var handle in allHandles)
        {
            // 等待引用归零（最多 30s，避免无限挂起）。
            var drainDeadline = Stopwatch.GetTimestamp();
            var drainTimeoutTicks = TimeSpan.FromSeconds(30).Ticks;
            while (handle.Counter.Count > 0)
            {
                if (Stopwatch.GetElapsedTime(drainDeadline).Ticks > drainTimeoutTicks)
                {
                    break; // 超时：best-effort Dispose。
                }
                await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
            }
            // 任意状态 → Disposed（幂等）。
            handle.TransitionTo(ModelSlotState.Disposed);
            await SafeDisposeHandleAsync(handle).ConfigureAwait(false);
        }

        // 4. await 所有后台 Dispose Task 完成。
        var bgTasks = SnapshotBackgroundDisposeTasks();
        if (bgTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(bgTasks).ConfigureAwait(false);
            }
            catch
            {
                // best-effort：各任务内部已吞异常。
            }
        }

        // 5. 清理 Staged Handles（Dispose 其引擎）。
        foreach (var kv in _stagedHandles)
        {
            if (kv.Value.Success && kv.Value.Engine is not null)
            {
                await SafeDisposeEngineAsync(kv.Value.Engine).ConfigureAwait(false);
            }
        }
        _stagedHandles.Clear();

        // 6. Dispose _disposedCts。
        _disposedCts.Dispose();
    }

    /// <summary>
    /// 引擎租约实现 —— 捕获 ActiveModelHandle 的引擎与 counter 引用。
    /// Dispose 时递减 counter，允许 drain 任务在引用归零后 Dispose 引擎。
    /// 幂等：多次 Dispose 安全（仅第一次递减）。
    /// </summary>
    private sealed class EngineLease : IInferenceEngineLease
    {
        private readonly ModelReferenceCounter _counter;
        private int _disposed;

        public EngineLease(ActiveModelHandle handle)
        {
            Engine = handle.Engine;
            Generation = handle.Generation;
            _counter = handle.Counter;
        }

        public IBatchInferenceEngine Engine { get; }

        public long Generation { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _counter.Decrement();
        }
    }

    /// <summary>
    /// fallback 引擎的永久租约 —— Engine 指向 fallback 引擎，Generation=0，
    /// Dispose 为 no-op（fallback 由 DI 容器管理生命周期，无需引用计数）。
    /// 幂等：多次 Dispose 安全。
    /// </summary>
    private sealed class FallbackEngineLease : IInferenceEngineLease
    {
        public FallbackEngineLease(IBatchInferenceEngine fallbackEngine)
        {
            Engine = fallbackEngine;
        }

        public IBatchInferenceEngine Engine { get; }

        public long Generation => 0L;

        public void Dispose()
        {
            // no-op：fallback 引擎由 DI 容器管理生命周期。
        }
    }
}
