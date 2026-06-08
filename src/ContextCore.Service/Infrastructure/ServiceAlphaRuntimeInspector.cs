using System.Diagnostics;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using Microsoft.Extensions.Options;

namespace ContextCore.Service.Infrastructure;

/// <summary>
/// 统一管理 Service Alpha 的 status / ready / deep probe。
/// 设计原则：
/// <list type="bullet">
///   <item><c>/api/status</c> 只做轻量只读检查。</item>
///   <item><c>/api/health/ready</c> 做中等强度检查，允许极低副作用（如根目录临时文件探针），默认不写业务数据。</item>
///   <item><c>/api/status/deep</c> 做深度写探针，统一落在 <c>__system__/__health__</c> 作用域，并使用固定 ID 防止无限增长。</item>
/// </list>
/// </summary>
internal sealed class ServiceAlphaRuntimeInspector
{
    internal const string RetrievalBaselineName = "retrieval-orchestration-baseline-v1";

    private const string SystemWorkspaceId = "__system__";
    private const string HealthCollectionId = "__health__";
    private const string DeepContextId = "deep-probe-context";
    private const string DeepMemoryId = "deep-probe-memory";
    private const string DeepRelationId = "deep-probe-relation";
    private const string DeepConstraintId = "deep-probe-constraint";
    private const string DeepJobId = "deep-probe-job";
    private const string DeepTraceId = "deep-probe-trace";
    private const string DeepEventId = "deep-probe-event";
    private const string DeepOperationId = "service-alpha-deep-probe";

    private static readonly TimeSpan ReadyCacheDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DeepCacheDuration = TimeSpan.FromSeconds(8);

    private readonly StorageOptions _storage;
    private readonly IOptions<JobWorkerOptions> _jobWorkerOptions;
    private readonly IOptions<ShortTermMaintenanceOptions> _shortTermMaintenanceOptions;
    private readonly ShortTermMaintenanceRuntimeState _shortTermMaintenanceState;
    private readonly IServiceProvider _services;
    private readonly SemaphoreSlim _readyCacheGate = new(1, 1);
    private readonly SemaphoreSlim _deepCacheGate = new(1, 1);

    private CachedRuntimeSnapshot? _cachedReadySnapshot;
    private CachedRuntimeSnapshot? _cachedDeepSnapshot;

    public ServiceAlphaRuntimeInspector(
        StorageOptions storage,
        IOptions<JobWorkerOptions> jobWorkerOptions,
        IOptions<ShortTermMaintenanceOptions> shortTermMaintenanceOptions,
        ShortTermMaintenanceRuntimeState shortTermMaintenanceState,
        IServiceProvider services)
    {
        _storage = storage;
        _jobWorkerOptions = jobWorkerOptions;
        _shortTermMaintenanceOptions = shortTermMaintenanceOptions;
        _shortTermMaintenanceState = shortTermMaintenanceState;
        _services = services;
    }

    /// <summary>
    /// 轻量状态快照，只执行只读检查，不引入任何写入副作用。
    /// </summary>
    public async Task<ServiceAlphaRuntimeSnapshot> GetStatusSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var checks = await BuildStatusChecksAsync(cancellationToken).ConfigureAwait(false);
        var maintenance = await GetShortTermMaintenanceStatusAsync(cancellationToken).ConfigureAwait(false);
        return BuildRuntimeSnapshot(checks, maintenance);
    }

    /// <summary>
    /// 就绪快照，允许低副作用探针，并对结果做短时间缓存。
    /// </summary>
    public async Task<ServiceAlphaRuntimeSnapshot> GetReadySnapshotAsync(
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (!refresh
            && _cachedReadySnapshot is { } cached
            && now - cached.CheckedAt <= ReadyCacheDuration)
        {
            return CopySnapshot(cached.Snapshot, fromCache: true, cacheTtlSeconds: (int)ReadyCacheDuration.TotalSeconds);
        }

        await _readyCacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (!refresh
                && _cachedReadySnapshot is { } innerCached
                && now - innerCached.CheckedAt <= ReadyCacheDuration)
            {
                return CopySnapshot(innerCached.Snapshot, fromCache: true, cacheTtlSeconds: (int)ReadyCacheDuration.TotalSeconds);
            }

            var checks = await BuildReadyChecksAsync(cancellationToken).ConfigureAwait(false);
            var maintenance = await GetShortTermMaintenanceStatusAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = BuildRuntimeSnapshot(checks, maintenance, cacheTtlSeconds: (int)ReadyCacheDuration.TotalSeconds);
            _cachedReadySnapshot = new CachedRuntimeSnapshot
            {
                CheckedAt = snapshot.CheckedAt,
                Snapshot = snapshot
            };
            return snapshot;
        }
        finally
        {
            _readyCacheGate.Release();
        }
    }

    /// <summary>
    /// 深度探针，允许写入隔离的系统健康命名空间；支持 refresh 强制重跑。
    /// </summary>
    public async Task<ServiceAlphaRuntimeSnapshot> GetDeepSnapshotAsync(
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (!refresh
            && _cachedDeepSnapshot is { } cached
            && now - cached.CheckedAt <= DeepCacheDuration)
        {
            return CopySnapshot(cached.Snapshot, fromCache: true, cacheTtlSeconds: (int)DeepCacheDuration.TotalSeconds);
        }

        await _deepCacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (!refresh
                && _cachedDeepSnapshot is { } innerCached
                && now - innerCached.CheckedAt <= DeepCacheDuration)
            {
                return CopySnapshot(innerCached.Snapshot, fromCache: true, cacheTtlSeconds: (int)DeepCacheDuration.TotalSeconds);
            }

            var snapshot = await BuildDeepSnapshotAsync(cancellationToken).ConfigureAwait(false);
            _cachedDeepSnapshot = new CachedRuntimeSnapshot
            {
                CheckedAt = snapshot.CheckedAt,
                Snapshot = snapshot
            };
            return snapshot;
        }
        finally
        {
            _deepCacheGate.Release();
        }
    }

    private async Task<IReadOnlyList<RuntimeProbeCheckResponse>> BuildStatusChecksAsync(CancellationToken cancellationToken)
    {
        var contextStore = _services.GetRequiredService<IContextStore>();
        var memoryStore = _services.GetRequiredService<IMemoryStore>();
        var relationStore = _services.GetRequiredService<IRelationStore>();
        var jobQueryStore = _services.GetRequiredService<IContextJobQueryStore>();
        var eventSink = _services.GetService<IContextEventSink>();

        return
        [
            await CheckStorageRootExistsAsync(cancellationToken).ConfigureAwait(false),
            await RunServiceCheckAsync("context-store", hasSideEffect: false, ProbeContextStoreReadableAsync, cancellationToken).ConfigureAwait(false),
            await RunServiceCheckAsync("memory-store", hasSideEffect: false, ProbeMemoryStoreReadableAsync, cancellationToken).ConfigureAwait(false),
            await RunServiceCheckAsync("relation-store", hasSideEffect: false, ProbeRelationStoreReadableAsync, cancellationToken).ConfigureAwait(false),
            await RunServiceCheckAsync("job-queue", hasSideEffect: false, token => ProbeJobQueueReadableAsync(jobQueryStore, token), cancellationToken).ConfigureAwait(false),
            CheckEventSinkAvailability(eventSink),
            CheckModelGatewayStatus(),
            CheckJobWorker(),
            await CheckShortTermMaintenanceAsync(cancellationToken).ConfigureAwait(false),
            CheckRetrievalBaseline()
        ];

        async Task<ProbeExecutionResult> ProbeContextStoreReadableAsync(CancellationToken token)
        {
            _ = await contextStore.QueryAsync(new ContextQuery
            {
                WorkspaceId = SystemWorkspaceId,
                CollectionId = HealthCollectionId,
                Take = 1
            }, token).ConfigureAwait(false);

            return ProbeExecutionResult.Ok("上下文存储读路径可用。", detail: "通过 QueryAsync 验证只读路径。");
        }

        async Task<ProbeExecutionResult> ProbeMemoryStoreReadableAsync(CancellationToken token)
        {
            _ = await memoryStore.QueryAsync(new ContextMemoryQuery
            {
                WorkspaceId = SystemWorkspaceId,
                CollectionId = HealthCollectionId,
                Take = 1
            }, token).ConfigureAwait(false);

            return ProbeExecutionResult.Ok("记忆存储读路径可用。", detail: "通过 QueryAsync 验证只读路径。");
        }

        async Task<ProbeExecutionResult> ProbeRelationStoreReadableAsync(CancellationToken token)
        {
            _ = await relationStore.QueryAsync(new ContextRelationQuery
            {
                WorkspaceId = SystemWorkspaceId,
                CollectionId = HealthCollectionId,
                Take = 1
            }, token).ConfigureAwait(false);

            return ProbeExecutionResult.Ok("关系存储读路径可用。", detail: "通过 QueryAsync 验证只读路径。");
        }
    }

    private async Task<IReadOnlyList<RuntimeProbeCheckResponse>> BuildReadyChecksAsync(CancellationToken cancellationToken)
    {
        var contextStore = _services.GetRequiredService<IContextStore>();
        var memoryStore = _services.GetRequiredService<IMemoryStore>();
        var relationStore = _services.GetRequiredService<IRelationStore>();
        var jobQueryStore = _services.GetRequiredService<IContextJobQueryStore>();
        var eventSink = _services.GetService<IContextEventSink>();

        return
        [
            await CheckStorageRootWritableAsync(cancellationToken).ConfigureAwait(false),
            await RunServiceCheckAsync("context-store", hasSideEffect: false, ProbeContextStoreReadableAsync, cancellationToken).ConfigureAwait(false),
            await RunServiceCheckAsync("memory-store", hasSideEffect: false, ProbeMemoryStoreReadableAsync, cancellationToken).ConfigureAwait(false),
            await RunServiceCheckAsync("relation-store", hasSideEffect: false, ProbeRelationStoreReadableAsync, cancellationToken).ConfigureAwait(false),
            await RunServiceCheckAsync("job-queue", hasSideEffect: false, token => ProbeJobQueueReadableAsync(jobQueryStore, token), cancellationToken).ConfigureAwait(false),
            CheckEventSinkAvailability(eventSink),
            CheckModelGatewayStatus(),
            CheckJobWorker(),
            await CheckShortTermMaintenanceAsync(cancellationToken).ConfigureAwait(false),
            CheckRetrievalBaseline()
        ];

        async Task<ProbeExecutionResult> ProbeContextStoreReadableAsync(CancellationToken token)
        {
            _ = await contextStore.QueryAsync(new ContextQuery
            {
                WorkspaceId = SystemWorkspaceId,
                CollectionId = HealthCollectionId,
                Take = 1
            }, token).ConfigureAwait(false);

            return ProbeExecutionResult.Ok("上下文存储读路径可用。", detail: "默认 readiness 不执行写入式 context store probe。");
        }

        async Task<ProbeExecutionResult> ProbeMemoryStoreReadableAsync(CancellationToken token)
        {
            _ = await memoryStore.QueryAsync(new ContextMemoryQuery
            {
                WorkspaceId = SystemWorkspaceId,
                CollectionId = HealthCollectionId,
                Take = 1
            }, token).ConfigureAwait(false);

            return ProbeExecutionResult.Ok("记忆存储读路径可用。", detail: "默认 readiness 不执行写入式 memory store probe。");
        }

        async Task<ProbeExecutionResult> ProbeRelationStoreReadableAsync(CancellationToken token)
        {
            _ = await relationStore.QueryAsync(new ContextRelationQuery
            {
                WorkspaceId = SystemWorkspaceId,
                CollectionId = HealthCollectionId,
                Take = 1
            }, token).ConfigureAwait(false);

            return ProbeExecutionResult.Ok("关系存储读路径可用。", detail: "默认 readiness 不执行写入式 relation store probe。");
        }
    }

    private async Task<ServiceAlphaRuntimeSnapshot> BuildDeepSnapshotAsync(CancellationToken cancellationToken)
    {
        var contextStore = _services.GetRequiredService<IContextStore>();
        var memoryStore = _services.GetRequiredService<IMemoryStore>();
        var relationStore = _services.GetRequiredService<IRelationStore>();
        var constraintStore = _services.GetRequiredService<IConstraintStore>();
        var jobQueue = _services.GetRequiredService<IContextJobQueue>();
        var jobQueryStore = _services.GetRequiredService<IContextJobQueryStore>();
        var retrievalTraceStore = _services.GetRequiredService<IRetrievalTraceStore>();
        var eventSink = _services.GetRequiredService<IContextEventSink>();
        var now = DateTimeOffset.UtcNow;
        var maintenance = await GetShortTermMaintenanceStatusAsync(cancellationToken).ConfigureAwait(false);

        var checks = new List<RuntimeProbeCheckResponse>
        {
            await RunDeepCheckAsync("context-store", async token =>
            {
                var item = new ContextItem
                {
                    Id = DeepContextId,
                    WorkspaceId = SystemWorkspaceId,
                    CollectionId = HealthCollectionId,
                    Type = "deep-probe",
                    Content = "service alpha deep context probe",
                    CreatedAt = now,
                    UpdatedAt = now
                };
                await contextStore.SaveAsync(item, token).ConfigureAwait(false);
                var readBack = await contextStore.GetAsync(SystemWorkspaceId, HealthCollectionId, DeepContextId, token).ConfigureAwait(false);
                if (readBack is null || readBack.Id != DeepContextId)
                {
                    throw new InvalidOperationException("deep context probe 写入后未能读回。");
                }

                return ProbeExecutionResult.Ok(
                    "上下文存储深度写探针成功。",
                    detail: "写入作用域：__system__/__health__，使用固定 ID 覆盖。");
            }, cancellationToken).ConfigureAwait(false),

            await RunDeepCheckAsync("memory-store", async token =>
            {
                await memoryStore.SaveAsync(new ContextMemoryItem
                {
                    Id = DeepMemoryId,
                    WorkspaceId = SystemWorkspaceId,
                    CollectionId = HealthCollectionId,
                    Layer = ContextMemoryLayer.Working,
                    Status = ContextMemoryStatus.Candidate,
                    Type = "deep-probe",
                    Content = "service alpha deep memory probe",
                    CreatedAt = now,
                    UpdatedAt = now
                }, token).ConfigureAwait(false);
                var readBack = await memoryStore.GetAsync(SystemWorkspaceId, HealthCollectionId, DeepMemoryId, token).ConfigureAwait(false);
                if (readBack is null || readBack.Id != DeepMemoryId)
                {
                    throw new InvalidOperationException("deep memory probe 写入后未能读回。");
                }

                return ProbeExecutionResult.Ok(
                    "记忆存储深度写探针成功。",
                    detail: "写入作用域：__system__/__health__，使用固定 ID 覆盖。");
            }, cancellationToken).ConfigureAwait(false),

            await RunDeepCheckAsync("relation-store", async token =>
            {
                await relationStore.SaveAsync(new ContextRelation
                {
                    Id = DeepRelationId,
                    WorkspaceId = SystemWorkspaceId,
                    CollectionId = HealthCollectionId,
                    SourceId = DeepContextId,
                    TargetId = DeepMemoryId,
                    RelationType = "deep-probe",
                    CreatedAt = now
                }, token).ConfigureAwait(false);
                var readBack = await relationStore.QueryBySourceAsync(SystemWorkspaceId, HealthCollectionId, DeepContextId, token).ConfigureAwait(false);
                if (!readBack.Any(item => item.Id == DeepRelationId))
                {
                    throw new InvalidOperationException("deep relation probe 写入后未能按 source 查询到。");
                }

                return ProbeExecutionResult.Ok(
                    "关系存储深度写探针成功。",
                    detail: "写入作用域：__system__/__health__，使用固定 ID 覆盖。");
            }, cancellationToken).ConfigureAwait(false),

            await RunDeepCheckAsync("constraint-store", async token =>
            {
                await constraintStore.SaveAsync(new ContextConstraint
                {
                    Id = DeepConstraintId,
                    WorkspaceId = SystemWorkspaceId,
                    CollectionId = HealthCollectionId,
                    Content = "service alpha deep constraint probe",
                    Level = ConstraintLevel.Soft,
                    Scope = ContextScope.Collection,
                    Status = ContextMemoryStatus.Candidate,
                    CreatedAt = now,
                    UpdatedAt = now
                }, token).ConfigureAwait(false);
                var readBack = await constraintStore.QueryAsync(new ContextConstraintQuery
                {
                    WorkspaceId = SystemWorkspaceId,
                    CollectionId = HealthCollectionId,
                    Take = 20
                }, token).ConfigureAwait(false);
                if (!readBack.Any(item => item.Id == DeepConstraintId))
                {
                    throw new InvalidOperationException("deep constraint probe 写入后未能查询到。");
                }

                return ProbeExecutionResult.Ok(
                    "约束存储深度写探针成功。",
                    detail: "写入作用域：__system__/__health__，使用固定 ID 覆盖。");
            }, cancellationToken).ConfigureAwait(false),

            await RunDeepCheckAsync("job-queue", async token =>
            {
                await jobQueue.EnqueueAsync(new ContextJob
                {
                    JobId = DeepJobId,
                    WorkspaceId = SystemWorkspaceId,
                    CollectionId = HealthCollectionId,
                    Kind = ContextJobKind.Custom,
                    PayloadJson = "{}",
                    State = ContextJobState.Queued,
                    Priority = 0,
                    MaxRetryCount = 0,
                    CreatedAt = now
                }, token).ConfigureAwait(false);
                var readBack = await jobQueryStore.QueryAsync(new ContextJobQuery
                {
                    WorkspaceId = SystemWorkspaceId,
                    CollectionId = HealthCollectionId,
                    Take = 20
                }, token).ConfigureAwait(false);
                if (!readBack.Any(job => string.Equals(job.JobId, DeepJobId, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("deep job probe 入队后未能查询到固定探针作业。");
                }

                return ProbeExecutionResult.Ok(
                    "作业队列深度写探针成功。",
                    warning: "探针作业会停留在 __system__/__health__ 作用域，使用固定 ID 避免无限增长。",
                    detail: "若启用 worker，该探针作业可能被消费，但不会污染业务工作空间。");
            }, cancellationToken).ConfigureAwait(false),

            await RunDeepCheckAsync("event-sink", async token =>
            {
                await eventSink.EmitAsync(new ContextOperationEvent
                {
                    EventId = DeepEventId,
                    OperationId = DeepOperationId,
                    OperationName = "service.alpha.deep-probe",
                    WorkspaceId = SystemWorkspaceId,
                    CollectionId = HealthCollectionId,
                    Level = ContextEventLevel.Information,
                    Message = "Service Alpha deep probe event.",
                    CreatedAt = now,
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["probe"] = "deep",
                        ["scope"] = "__system__/__health__"
                    }
                }, token).ConfigureAwait(false);

                return ProbeExecutionResult.Ok(
                    "事件接收器深度写探针成功。",
                    detail: "事件写入使用固定 eventId 与 system health scope。");
            }, cancellationToken).ConfigureAwait(false),

            await RunDeepCheckAsync("retrieval-trace", async token =>
            {
                await retrievalTraceStore.SaveAsync(new ContextRetrievalTrace
                {
                    RetrievalId = DeepTraceId,
                    WorkspaceId = SystemWorkspaceId,
                    CollectionId = HealthCollectionId,
                    QueryText = "service alpha deep probe",
                    CreatedAt = now
                }, token).ConfigureAwait(false);
                var readBack = await retrievalTraceStore.QueryRecentAsync(SystemWorkspaceId, HealthCollectionId, 20, token).ConfigureAwait(false);
                if (!readBack.Any(trace => trace.RetrievalId == DeepTraceId))
                {
                    throw new InvalidOperationException("deep retrieval trace probe 写入后未能查询到。");
                }

                return ProbeExecutionResult.Ok(
                    "检索 trace 深度写探针成功。",
                    detail: "写入作用域：__system__/__health__，使用固定 ID 覆盖。");
            }, cancellationToken).ConfigureAwait(false)
        };

        var hasError = checks.Any(check => string.Equals(check.Status, "error", StringComparison.OrdinalIgnoreCase));
        var hasWarning = checks.Any(check => string.Equals(check.Status, "warning", StringComparison.OrdinalIgnoreCase));
        var state = hasError ? "Degraded" : hasWarning ? "Warning" : "Ready";

        return new ServiceAlphaRuntimeSnapshot
        {
            CheckedAt = DateTimeOffset.UtcNow,
            StorageProvider = _storage.Provider,
            State = state,
            Message = state == "Degraded"
                ? "深度运行时探针存在失败项。"
                : state == "Warning"
                    ? "深度运行时探针存在告警项。"
                    : "深度运行时探针全部通过。",
            ProductionReady = false,
            ProviderState = ResolveProviderState(hasError),
            RetrievalBaseline = RetrievalBaselineName,
            Capabilities = BuildCapabilities(),
            Checks = checks,
            ProbeScope = $"{SystemWorkspaceId}/{HealthCollectionId}",
            FromCache = false,
            CacheTtlSeconds = (int)DeepCacheDuration.TotalSeconds,
            Warnings = CollectWarnings(checks),
            ShortTermMaintenance = maintenance
        };
    }

    private async Task<RuntimeProbeCheckResponse> CheckStorageRootExistsAsync(CancellationToken cancellationToken)
    {
        if (!_storage.IsFileSystem)
        {
            return CreateServiceCheck(
                name: "storage-root",
                status: "warning",
                message: "当前 provider 非 filesystem，root path 只读检查不适用。",
                hasSideEffect: false,
                warning: "该检查仅对 filesystem provider 有意义。",
                detail: null,
                durationMs: 0d);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var rootPath = _storage.ResolvedRootPath;
            var exists = Directory.Exists(rootPath);
            return exists
                ? CreateServiceCheck(
                    name: "storage-root",
                    status: "ok",
                    message: $"文件系统根目录存在：{rootPath}",
                    hasSideEffect: false,
                    warning: null,
                    detail: "status 仅检查目录存在性，不做写探针。",
                    durationMs: sw.Elapsed.TotalMilliseconds)
                : CreateServiceCheck(
                    name: "storage-root",
                    status: "error",
                    message: $"文件系统根目录不存在：{rootPath}",
                    hasSideEffect: false,
                    warning: "status 不会尝试自动创建根目录。",
                    detail: "ready 会将缺失根目录视为未就绪。",
                    durationMs: sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            return CreateServiceCheck(
                name: "storage-root",
                status: "error",
                message: "文件系统根目录只读检查失败。",
                hasSideEffect: false,
                warning: null,
                detail: $"{ex.GetType().Name}: {ex.Message}",
                durationMs: sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<RuntimeProbeCheckResponse> CheckStorageRootWritableAsync(CancellationToken cancellationToken)
    {
        if (!_storage.IsFileSystem)
        {
            return CreateServiceCheck(
                name: "storage-root",
                status: "warning",
                message: "当前 provider 非 filesystem，root path 写探针不适用。",
                hasSideEffect: false,
                warning: "该检查仅对 filesystem provider 有意义。",
                detail: null,
                durationMs: 0d);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var rootPath = _storage.ResolvedRootPath;
            if (!Directory.Exists(rootPath))
            {
                return CreateServiceCheck(
                    name: "storage-root",
                    status: "error",
                    message: $"文件系统根目录不存在：{rootPath}",
                    hasSideEffect: false,
                    warning: null,
                    detail: "ready 将缺失根目录视为未就绪。",
                    durationMs: sw.Elapsed.TotalMilliseconds);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            var probePath = Path.Combine(rootPath, ".contextcore-ready-probe.tmp");
            await File.WriteAllTextAsync(probePath, DateTimeOffset.UtcNow.ToString("O"), timeoutCts.Token).ConfigureAwait(false);
            File.Delete(probePath);

            return CreateServiceCheck(
                name: "storage-root",
                status: "ok",
                message: $"文件系统根目录可写：{rootPath}",
                hasSideEffect: true,
                warning: null,
                detail: "仅创建并删除根目录下的临时探针文件，不写入业务数据。",
                durationMs: sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateServiceCheck(
                name: "storage-root",
                status: "error",
                message: "文件系统根目录写探针超时（>3s）。",
                hasSideEffect: true,
                warning: null,
                detail: null,
                durationMs: sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            return CreateServiceCheck(
                name: "storage-root",
                status: "error",
                message: "文件系统根目录写探针失败。",
                hasSideEffect: true,
                warning: null,
                detail: $"{ex.GetType().Name}: {ex.Message}",
                durationMs: sw.Elapsed.TotalMilliseconds);
        }
    }

    private RuntimeProbeCheckResponse CheckEventSinkAvailability(IContextEventSink? eventSink)
    {
        if (eventSink is null)
        {
            return CreateServiceCheck(
                name: "event-sink",
                status: "warning",
                message: "IContextEventSink 未注册。",
                hasSideEffect: false,
                warning: "ready 默认不执行 emit，当前仅检查依赖是否存在。",
                detail: null,
                durationMs: 0d);
        }

        return CreateServiceCheck(
            name: "event-sink",
            status: "ok",
            message: $"事件接收器已注册：{eventSink.GetType().Name}",
            hasSideEffect: false,
            warning: null,
            detail: "默认 readiness 仅做依赖存在性检查，不写入事件日志。",
            durationMs: 0d);
    }

    private RuntimeProbeCheckResponse CheckModelGatewayStatus()
    {
        var options = _services.GetService<ModelGatewayOptions>();
        if (options is null)
        {
            return CreateServiceCheck(
                name: "model-gateway",
                status: "warning",
                message: "ModelGatewayOptions 未注册。",
                hasSideEffect: false,
                warning: "模型网关不可用会降级，但不阻断 ready。",
                detail: null,
                durationMs: 0d);
        }

        var enabledCount = options.Models.Count(model => model.Enabled);
        return enabledCount > 0
            ? CreateServiceCheck(
                name: "model-gateway",
                status: "ok",
                message: $"模型网关已加载，启用模型数 {enabledCount}/{options.Models.Count}。",
                hasSideEffect: false,
                warning: null,
                detail: null,
                durationMs: 0d)
            : CreateServiceCheck(
                name: "model-gateway",
                status: "warning",
                message: "模型网关已注册，但没有启用模型。",
                hasSideEffect: false,
                warning: "模型网关不可用会降级，但不阻断 ready。",
                detail: "当前服务仍可运行 mock/非模型依赖路径。",
                durationMs: 0d);
    }

    private RuntimeProbeCheckResponse CheckJobWorker()
    {
        var options = _jobWorkerOptions.Value;
        return options.Enabled
            ? CreateServiceCheck(
                name: "job-worker",
                status: "ok",
                message: $"后台作业 worker 已启用（轮询间隔 {options.PollIntervalMilliseconds}ms，并发 {options.Concurrency}）。",
                hasSideEffect: false,
                warning: null,
                detail: null,
                durationMs: 0d)
            : CreateServiceCheck(
                name: "job-worker",
                status: "warning",
                message: "后台作业 worker 已禁用，作业不会自动消费。",
                hasSideEffect: false,
                warning: "不会影响同步 API，但会影响后台作业闭环。",
                detail: null,
                durationMs: 0d);
    }

    private async Task<RuntimeProbeCheckResponse> CheckShortTermMaintenanceAsync(CancellationToken cancellationToken)
    {
        var options = _shortTermMaintenanceOptions.Value;
        var state = await GetShortTermMaintenanceStatusAsync(cancellationToken).ConfigureAwait(false);

        if (!options.Enabled)
        {
            return CreateServiceCheck(
                name: "short-term-maintenance",
                status: "warning",
                message: "短期记忆维护 worker 已禁用，当前依赖手动 compact。",
                hasSideEffect: false,
                warning: "不会阻断服务运行，但 archive/compaction 不会自动执行。",
                detail: $"RunOnStartup={options.RunOnStartup}; Interval={Math.Max(1, options.IntervalSeconds)}s",
                durationMs: 0d);
        }

        if (!string.IsNullOrWhiteSpace(state.LastError))
        {
            return CreateServiceCheck(
                name: "short-term-maintenance",
                status: "warning",
                message: "短期记忆维护 worker 已启用，但最近一次运行出现错误。",
                hasSideEffect: false,
                warning: state.LastError,
                detail: $"RunOnStartup={options.RunOnStartup}; Interval={Math.Max(1, options.IntervalSeconds)}s",
                durationMs: 0d);
        }

        return CreateServiceCheck(
            name: "short-term-maintenance",
            status: "ok",
            message: state.LastRun is null
                ? "短期记忆维护 worker 已启用，尚无历史运行记录。"
                : "短期记忆维护 worker 已启用，最近运行成功。",
            hasSideEffect: false,
            warning: null,
            detail: state.LastRun is null
                ? $"RunOnStartup={options.RunOnStartup}; Interval={Math.Max(1, options.IntervalSeconds)}s"
                : $"LastRun={state.LastRun.CompletedAt:O}; Interval={Math.Max(1, options.IntervalSeconds)}s",
            durationMs: 0d);
    }

    private static RuntimeProbeCheckResponse CheckRetrievalBaseline()
    {
        return CreateServiceCheck(
            name: "retrieval-baseline",
            status: "ok",
            message: $"当前固定 retrieval baseline：{RetrievalBaselineName}",
            hasSideEffect: false,
            warning: null,
            detail: "本轮 runtime stabilization 不修改 retrieval 语义。",
            durationMs: 0d);
    }

    private IReadOnlyList<ProviderCapabilityResponse> BuildCapabilities()
    {
        var vectorStore = _services.GetService<IVectorStore>();
        var embeddingProvider = _services.GetService<IEmbeddingProvider>();

        return
        [
            new ProviderCapabilityResponse
            {
                Name = "filesystem",
                State = "AlphaSupported",
                Active = _storage.IsFileSystem,
                Message = _storage.IsFileSystem
                    ? "当前实例使用 filesystem provider，属于 Service Alpha 推荐持久化后端。"
                    : "filesystem provider 可作为当前 Service Alpha 推荐持久化后端。"
            },
            new ProviderCapabilityResponse
            {
                Name = "memory",
                State = "TestOnly",
                Active = _storage.IsMemory,
                Message = _storage.IsMemory
                    ? "当前实例使用内存 provider，仅适合测试、Demo 和临时运行。"
                    : "memory provider 可用，但仅适合测试、Demo 和临时运行。"
            },
            new ProviderCapabilityResponse
            {
                Name = "postgres",
                State = "Experimental",
                Active = _storage.IsPostgres,
                Message = _storage.IsPostgres
                    ? "当前实例启用了 PostgreSQL provider。该能力仍标记为 Experimental。"
                    : "PostgreSQL provider 已有实现，但当前仍按 Experimental 能力对待。"
            },
            new ProviderCapabilityResponse
            {
                Name = "vector-store",
                State = ResolveVectorCapabilityState(vectorStore, embeddingProvider),
                Active = vectorStore is not null && embeddingProvider is not null,
                Message = ResolveVectorCapabilityMessage(vectorStore, embeddingProvider)
            }
        ];
    }

    private ServiceAlphaRuntimeSnapshot BuildRuntimeSnapshot(
        IReadOnlyList<RuntimeProbeCheckResponse> checks,
        ShortTermMaintenanceStatusResponse maintenance,
        int cacheTtlSeconds = 0)
    {
        var hasError = checks.Any(check => string.Equals(check.Status, "error", StringComparison.OrdinalIgnoreCase));

        return new ServiceAlphaRuntimeSnapshot
        {
            CheckedAt = DateTimeOffset.UtcNow,
            StorageProvider = _storage.Provider,
            State = ResolveState(hasError),
            Message = ResolveMessage(hasError),
            ProductionReady = false,
            ProviderState = ResolveProviderState(hasError),
            RetrievalBaseline = RetrievalBaselineName,
            Capabilities = BuildCapabilities(),
            Checks = checks,
            ProbeScope = null,
            FromCache = false,
            CacheTtlSeconds = cacheTtlSeconds,
            Warnings = CollectWarnings(checks),
            ShortTermMaintenance = maintenance
        };
    }

    private async Task<ShortTermMaintenanceStatusResponse> GetShortTermMaintenanceStatusAsync(CancellationToken cancellationToken)
    {
        var snapshot = _shortTermMaintenanceState.Snapshot();
        var store = _services.GetService<IShortTermMemoryStore>();
        if (store is null)
        {
            return snapshot;
        }

        var scopes = await store.QueryScopesAsync(cancellationToken).ConfigureAwait(false);
        ShortTermCompactionRun? lastRun = null;
        foreach (var scope in scopes)
        {
            var runs = await store.QueryCompactionRunsAsync(new ShortTermCompactionRunQuery
            {
                WorkspaceId = scope.WorkspaceId,
                CollectionId = scope.CollectionId,
                Take = 1
            }, cancellationToken).ConfigureAwait(false);
            var candidate = runs.FirstOrDefault();
            if (candidate is not null && (lastRun is null || candidate.StartedAt > lastRun.StartedAt))
            {
                lastRun = candidate;
            }
        }

        if (lastRun is null)
        {
            return snapshot;
        }

        if (snapshot.LastRun is not null && snapshot.LastRun.StartedAt > lastRun.StartedAt)
        {
            lastRun = snapshot.LastRun;
        }

        return new ShortTermMaintenanceStatusResponse
        {
            Enabled = snapshot.Enabled,
            IsRunning = snapshot.IsRunning,
            RunOnStartup = snapshot.RunOnStartup,
            IntervalSeconds = snapshot.IntervalSeconds,
            LastError = snapshot.LastError,
            LastRun = lastRun
        };
    }

    private static IReadOnlyList<string> CollectWarnings(IReadOnlyList<RuntimeProbeCheckResponse> checks)
    {
        return checks
            .SelectMany(check =>
            {
                if (!string.IsNullOrWhiteSpace(check.Warning))
                {
                    return new[] { check.Warning! };
                }

                return string.Equals(check.Status, "warning", StringComparison.OrdinalIgnoreCase)
                    ? new[] { check.Message }
                    : Array.Empty<string>();
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static ServiceAlphaRuntimeSnapshot CopySnapshot(
        ServiceAlphaRuntimeSnapshot snapshot,
        bool fromCache,
        int cacheTtlSeconds)
    {
        return new ServiceAlphaRuntimeSnapshot
        {
            CheckedAt = snapshot.CheckedAt,
            StorageProvider = snapshot.StorageProvider,
            State = snapshot.State,
            Message = snapshot.Message,
            ProductionReady = snapshot.ProductionReady,
            ProviderState = snapshot.ProviderState,
            RetrievalBaseline = snapshot.RetrievalBaseline,
            Capabilities = snapshot.Capabilities,
            Checks = snapshot.Checks,
            FromCache = fromCache,
            CacheTtlSeconds = cacheTtlSeconds,
            ProbeScope = snapshot.ProbeScope,
            Warnings = snapshot.Warnings,
            ShortTermMaintenance = snapshot.ShortTermMaintenance
        };
    }

    private string ResolveProviderState(bool hasError)
    {
        if (hasError)
        {
            return "Degraded";
        }

        if (_storage.IsFileSystem)
        {
            return "ServiceReadyAlpha";
        }

        if (_storage.IsMemory)
        {
            return "TestOnly";
        }

        if (_storage.IsPostgres)
        {
            return "Experimental";
        }

        return "Unsupported";
    }

    private string ResolveState(bool hasError)
    {
        if (hasError)
        {
            return "Degraded";
        }

        return _storage.IsMemory
            ? "NotProductionReady"
            : "Ready";
    }

    private string ResolveMessage(bool hasError)
    {
        if (hasError)
        {
            return "Service runtime probe 未全部通过，当前实例处于降级状态。";
        }

        if (_storage.IsMemory)
        {
            return "Service runtime 可运行，但当前使用 memory provider，仅适合测试。";
        }

        if (_storage.IsPostgres)
        {
            return "Service runtime 已通过当前探针，但 PostgreSQL 仍标记为 Experimental。";
        }

        return "Service runtime 已就绪。";
    }

    private async Task<RuntimeProbeCheckResponse> RunServiceCheckAsync(
        string name,
        bool hasSideEffect,
        Func<CancellationToken, Task<ProbeExecutionResult>> probe,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            var result = await probe(timeoutCts.Token).ConfigureAwait(false);
            return CreateServiceCheck(
                name: name,
                status: result.Status,
                message: result.Message,
                hasSideEffect: hasSideEffect,
                warning: result.Warning,
                detail: result.Detail,
                durationMs: sw.Elapsed.TotalMilliseconds,
                severity: result.Severity);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateServiceCheck(
                name: name,
                status: "error",
                message: "检查超时（>5s）。",
                hasSideEffect: hasSideEffect,
                warning: null,
                detail: null,
                durationMs: sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            return CreateServiceCheck(
                name: name,
                status: "error",
                message: "检查失败。",
                hasSideEffect: hasSideEffect,
                warning: null,
                detail: $"{ex.GetType().Name}: {ex.Message}",
                durationMs: sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<RuntimeProbeCheckResponse> RunDeepCheckAsync(
        string name,
        Func<CancellationToken, Task<ProbeExecutionResult>> probe,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            var result = await probe(timeoutCts.Token).ConfigureAwait(false);
            return new RuntimeProbeCheckResponse
            {
                Name = name,
                Status = result.Status,
                Message = result.Message,
                Severity = result.Severity,
                HasSideEffect = true,
                DurationMs = sw.Elapsed.TotalMilliseconds,
                Warning = result.Warning,
                Detail = result.Detail
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RuntimeProbeCheckResponse
            {
                Name = name,
                Status = "error",
                Message = "检查超时（>5s）",
                Severity = "error",
                HasSideEffect = true,
                DurationMs = sw.Elapsed.TotalMilliseconds
            };
        }
        catch (Exception ex)
        {
            return new RuntimeProbeCheckResponse
            {
                Name = name,
                Status = "error",
                Message = "检查失败。",
                Severity = "error",
                HasSideEffect = true,
                DurationMs = sw.Elapsed.TotalMilliseconds,
                Detail = $"{ex.GetType().Name}: {ex.Message}"
            };
        }
    }

    private static async Task<ProbeExecutionResult> ProbeJobQueueReadableAsync(
        IContextJobQueryStore jobQueryStore,
        CancellationToken cancellationToken)
    {
        _ = await jobQueryStore.QueryAsync(new ContextJobQuery
        {
            WorkspaceId = SystemWorkspaceId,
            CollectionId = HealthCollectionId,
            Take = 1
        }, cancellationToken).ConfigureAwait(false);

        return ProbeExecutionResult.Ok("作业查询路径可用。", detail: "默认 readiness 不执行入队探针。");
    }

    private static string ResolveVectorCapabilityState(
        IVectorStore? vectorStore,
        IEmbeddingProvider? embeddingProvider)
    {
        if (vectorStore is null)
        {
            return "Missing";
        }

        return embeddingProvider is null
            ? "NotConfigured"
            : "Configured";
    }

    private static string ResolveVectorCapabilityMessage(
        IVectorStore? vectorStore,
        IEmbeddingProvider? embeddingProvider)
    {
        if (vectorStore is null)
        {
            return "IVectorStore 未注册，向量存储能力缺失。";
        }

        if (embeddingProvider is null)
        {
            return $"已注册 {vectorStore.GetType().Name}，但 IEmbeddingProvider 未配置，向量链路仍视为未配置。";
        }

        return $"向量存储 {vectorStore.GetType().Name} 与 Embedding provider 已注册。";
    }

    private static RuntimeProbeCheckResponse CreateServiceCheck(
        string name,
        string status,
        string message,
        bool hasSideEffect,
        string? warning,
        string? detail,
        double durationMs,
        string? severity = null)
    {
        return new RuntimeProbeCheckResponse
        {
            Name = name,
            Status = status,
            Message = message,
            Severity = severity ?? status switch
            {
                "warning" => "warning",
                "error" => "error",
                _ => "info"
            },
            HasSideEffect = hasSideEffect,
            DurationMs = durationMs,
            Warning = warning,
            Detail = detail
        };
    }

    private sealed class CachedRuntimeSnapshot
    {
        public DateTimeOffset CheckedAt { get; init; }

        public required ServiceAlphaRuntimeSnapshot Snapshot { get; init; }
    }

    private sealed class ProbeExecutionResult
    {
        public string Status { get; init; } = "ok";

        public string Message { get; init; } = string.Empty;

        public string Severity { get; init; } = "info";

        public string? Warning { get; init; }

        public string? Detail { get; init; }

        public static ProbeExecutionResult Ok(string message, string? warning = null, string? detail = null)
            => new()
            {
                Status = warning is null ? "ok" : "warning",
                Severity = warning is null ? "info" : "warning",
                Message = message,
                Warning = warning,
                Detail = detail
            };
    }
}

internal sealed class ServiceAlphaRuntimeSnapshot
{
    public DateTimeOffset CheckedAt { get; init; }

    public string StorageProvider { get; init; } = string.Empty;

    public string State { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public bool ProductionReady { get; init; }

    public string ProviderState { get; init; } = string.Empty;

    public string RetrievalBaseline { get; init; } = string.Empty;

    public IReadOnlyList<ProviderCapabilityResponse> Capabilities { get; init; } = Array.Empty<ProviderCapabilityResponse>();

    public IReadOnlyList<RuntimeProbeCheckResponse> Checks { get; init; } = Array.Empty<RuntimeProbeCheckResponse>();

    public bool FromCache { get; init; }

    public int CacheTtlSeconds { get; init; }

    public string? ProbeScope { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public ShortTermMaintenanceStatusResponse ShortTermMaintenance { get; init; } = new();
}
