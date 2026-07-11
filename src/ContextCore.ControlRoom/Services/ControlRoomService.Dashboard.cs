using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Hosting;
using ContextCore.ControlRoom.Models;
using ContextCore.Client;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Attention;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Planning;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Core.Services.Storage;
using ContextCore.Embedding;
using ContextCore.Embedding.Providers;
using ContextCore.ModelGateway;
using ContextCore.ModelGateway.Infrastructure;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.ControlRoom.Services;

public sealed partial class ControlRoomService
{

    public async Task<DashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        if (_state.IsServiceMode)
        {
            return await GetServiceModeDashboardAsync(cancellationToken).ConfigureAwait(false);
        }

        // 仪表盘一次聚合多类数据，渲染层只负责展示，不再直接访问 Store。
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var globals = await _state.GlobalContextStore.QueryAsync(new ContextGlobalQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);
        var jobs = await QueryJobsAsync(null, int.MaxValue, cancellationToken).ConfigureAwait(false);
        var recentOperations = await ReadRecentOperationsAsync(10, cancellationToken).ConfigureAwait(false);
        var recentCompressionQuality = await GetRecentCompressionQualityAsync(5, cancellationToken).ConfigureAwait(false);
        var modelStatus = await GetModelStatusAsync(5, cancellationToken).ConfigureAwait(false);
        var discovery = DiscoverWorkspaces(_state.RootPath);

        var health = BuildSystemHealth(status, recentOperations, modelStatus);
        var jobsSummary = new JobsSummary
        {
            Queued = jobs.Count(job => job.State == ContextJobState.Queued),
            Running = jobs.Count(job => job.State == ContextJobState.Running),
            WaitingRetry = jobs.Count(job => job.State == ContextJobState.WaitingRetry),
            Failed = jobs.Count(job => job.State == ContextJobState.Failed),
            Succeeded = jobs.Count(job => job.State == ContextJobState.Succeeded),
            RequiresReview = jobs.Count(job => job.State == ContextJobState.RequiresReview)
        };

        var snapshot = new DashboardSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            StorageKind = _state.StorageKind,
            RootPath = Path.GetFullPath(_state.RootPath),
            WorkspaceDataFound = discovery.Workspaces.Count > 0,
            Health = health,
            Memory = new MemoryLayerSummary
            {
                RawItems = status.RawItemCount,
                WorkingMemory = status.WorkingMemoryCount,
                CandidateMemory = status.CandidateMemoryCount,
                StableMemory = status.StableMemoryCount,
                GlobalItems = globals.Count,
                Constraints = status.ConstraintCount,
                Relations = status.RelationCount,
                IndexEntries = status.IndexEntryCount,
                Packages = status.LastPackage is null ? 0 : 1
            },
            RecentOperations = recentOperations,
            RecentCompressionQuality = recentCompressionQuality,
            Jobs = jobsSummary,
            LatestPackage = status.LastPackage is null
                ? null
                : PackageSummary.FromPackage(status.LastPackage),
            Alerts = []
        };

        snapshot.Alerts = BuildAlerts(snapshot, status, modelStatus);
        return snapshot;
    }

    public async Task<ControlRoomStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_state.IsServiceMode)
        {
            var runtimeStatus = await GetRuntimeStatusAsync(cancellationToken).ConfigureAwait(false);
            return new ControlRoomStatus
            {
                Mode = ControlRoomMode.Service,
                WorkspaceId = _state.WorkspaceId,
                CollectionId = _state.CollectionId,
                StorageKind = runtimeStatus.Storage.Provider,
                RootPath = runtimeStatus.Storage.RootPath ?? string.Empty,
                ServiceBaseUrl = _state.ServiceBaseUrl,
                ReadinessState = runtimeStatus.Readiness.Status,
                ReadinessMessage = runtimeStatus.Readiness.Message,
                ProviderState = runtimeStatus.Readiness.ProviderState,
                ProductionReady = runtimeStatus.Readiness.ProductionReady,
                QueuedJobCount = runtimeStatus.Jobs.Queued,
                RunningJobCount = runtimeStatus.Jobs.Running,
                RetrievalBaseline = runtimeStatus.RetrievalBaseline,
                RuntimeFromCache = runtimeStatus.Readiness.FromCache,
                RuntimeCacheTtlSeconds = runtimeStatus.Readiness.CacheTtlSeconds,
                RuntimeWarningCount = runtimeStatus.Readiness.Warnings.Count
            };
        }

        var rawItems = await QueryRawAsync(int.MaxValue, cancellationToken).ConfigureAwait(false);
        var working = await QueryMemoryAsync(ContextMemoryLayer.Working, null, int.MaxValue, cancellationToken).ConfigureAwait(false);
        var candidates = await QueryMemoryAsync(null, ContextMemoryStatus.Candidate, int.MaxValue, cancellationToken).ConfigureAwait(false);
        var stable = await QueryMemoryAsync(ContextMemoryLayer.Stable, ContextMemoryStatus.Stable, int.MaxValue, cancellationToken).ConfigureAwait(false);
        var constraints = await QueryConstraintsAsync(null, int.MaxValue, cancellationToken).ConfigureAwait(false);
        var relations = await QueryRelationsAsync(int.MaxValue, cancellationToken).ConfigureAwait(false);
        var indexEntries = await _state.Index.SearchAsync(new IndexQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);
        var jobs = await QueryJobsAsync(null, int.MaxValue, cancellationToken).ConfigureAwait(false);
        var readiness = BuildLocalReadiness(_state.StorageKind, _state.RootPath);

        return new ControlRoomStatus
        {
            Mode = ControlRoomMode.Direct,
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            StorageKind = _state.StorageKind,
            RootPath = _state.RootPath,
            ReadinessState = readiness.State,
            ReadinessMessage = readiness.Message,
            ProviderState = readiness.ProviderState,
            ProductionReady = readiness.ProductionReady,
            RawItemCount = rawItems.Count,
            WorkingMemoryCount = working.Count,
            CandidateMemoryCount = candidates.Count,
            StableMemoryCount = stable.Count,
            ConstraintCount = constraints.Count,
            RelationCount = relations.Count,
            IndexEntryCount = indexEntries.Count,
            QueuedJobCount = jobs.Count(job => job.State == ContextJobState.Queued),
            RunningJobCount = jobs.Count(job => job.State == ContextJobState.Running),
            FailedJobCount = jobs.Count(job => job.State == ContextJobState.Failed),
            SucceededJobCount = jobs.Count(job => job.State == ContextJobState.Succeeded),
            LastPackage = _state.LastPackage
        };
    }

    public Task<RuntimeStatusResponse> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetStatusAsync(cancellationToken);
    }

    public Task<RuntimeReadinessResponse> GetRuntimeReadinessAsync(CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetReadinessAsync(cancellationToken);
    }

    public Task<RuntimeReadinessResponse> GetRuntimeDeepStatusAsync(
        bool refresh,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetDeepStatusAsync(refresh, cancellationToken);
    }

    private static LocalReadiness BuildLocalReadiness(string storageKind, string rootPath)
    {
        // ControlRoom 当前主要是 Direct File Mode，本地 readiness 只做低成本判断。
        // 深度读写探针后续会进入 Service /api/health/ready，避免控制台刷新时产生重 IO。
        if (string.Equals(storageKind, "memory", StringComparison.OrdinalIgnoreCase))
        {
            return new LocalReadiness(
                "NotProductionReady",
                "memory 存储仅用于测试、Demo 和临时验证，进程重启后数据会丢失。",
                "TestOnly",
                ProductionReady: false);
        }

        if (string.Equals(storageKind, "filesystem", StringComparison.OrdinalIgnoreCase))
        {
            var rootReady = !string.IsNullOrWhiteSpace(rootPath) && Directory.Exists(rootPath);
            return rootReady
                ? new LocalReadiness(
                    "Ready",
                    "FileSystem 存储目录存在；当前为 Alpha 推荐持久化模式。",
                    "ServiceReadyAlpha",
                    ProductionReady: false)
                : new LocalReadiness(
                    "Degraded",
                    "FileSystem 存储目录不存在或尚未初始化。",
                    "ServiceReadyAlpha",
                    ProductionReady: false);
        }

        if (string.Equals(storageKind, "postgres", StringComparison.OrdinalIgnoreCase)
            || string.Equals(storageKind, "postgresql", StringComparison.OrdinalIgnoreCase))
        {
            return new LocalReadiness(
                "ExperimentalProvider",
                "PostgreSQL provider 当前仍为 Experimental/Partial，不能作为完整 Service 后端。",
                "ExperimentalPartial",
                ProductionReady: false);
        }

        return new LocalReadiness(
            "Degraded",
            $"未知存储类型：{storageKind}",
            "Unsupported",
            ProductionReady: false);
    }

    private async Task<DashboardSnapshot> GetServiceModeDashboardAsync(CancellationToken cancellationToken)
    {
        var runtimeStatus = await GetRuntimeStatusAsync(cancellationToken).ConfigureAwait(false);
        var runtimeReadiness = await GetRuntimeReadinessAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = new DashboardSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            Mode = ControlRoomMode.Service,
            ServiceBaseUrl = _state.ServiceBaseUrl,
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            StorageKind = $"service/{runtimeStatus.Storage.Provider}",
            RootPath = runtimeStatus.Storage.RootPath ?? string.Empty,
            WorkspaceDataFound = true,
            Health = BuildServiceSystemHealth(runtimeReadiness),
            Memory = new MemoryLayerSummary(),
            Jobs = new JobsSummary
            {
                Queued = runtimeStatus.Jobs.Queued,
                Running = runtimeStatus.Jobs.Running
            },
            RecentOperations = [],
            RecentCompressionQuality = [],
            LatestPackage = null,
            Alerts = BuildServiceAlerts(runtimeReadiness)
        };

        return snapshot;
    }

    private static IReadOnlyList<SystemHealthItem> BuildServiceSystemHealth(RuntimeReadinessResponse readiness)
    {
        return
        [
            HealthFromProbe("storage", readiness.Checks, "storage-root"),
            HealthFromProbe("operation logs", readiness.Checks, "event-sink"),
            HealthFromProbe("index", readiness.Checks, "retrieval-baseline"),
            HealthFromProbe("job queue", readiness.Checks, "job-queue"),
            HealthFromProbe("model gateway", readiness.Checks, "model-gateway")
        ];
    }

    private static IReadOnlyList<string> BuildServiceAlerts(RuntimeReadinessResponse readiness)
    {
        var alerts = new List<string>();

        if (!string.Equals(readiness.Status, "ready", StringComparison.OrdinalIgnoreCase))
        {
            alerts.Add($"Service readiness={readiness.Status}");
        }

        alerts.AddRange(readiness.Warnings);
        return alerts.Count == 0 ? [] : alerts.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static SystemHealthItem HealthFromProbe(
        string name,
        IReadOnlyList<RuntimeProbeCheckResponse> checks,
        string probeName)
    {
        var check = checks.FirstOrDefault(item =>
            string.Equals(item.Name, probeName, StringComparison.OrdinalIgnoreCase));

        return new SystemHealthItem
        {
            Name = name,
            Status = check?.Status ?? "missing",
            Detail = check?.Message ?? "无对应探针"
        };
    }

    public async Task<ControlRoomModelStatus> GetModelStatusAsync(
        int recentTake = 20,
        CancellationToken cancellationToken = default)
    {
        var modelOptions = ModelGatewayOptionsMaterializer.Materialize(_state.ModelGatewayOptions);
        var health = new List<ModelHealthResult>();
        foreach (var model in modelOptions.Models)
        {
            health.Add(await _state.ModelHealthService.CheckAsync(model.Name, cancellationToken)
                .ConfigureAwait(false));
        }

        var usageLogs = await _state.ModelUsageLogStore.QueryRecentAsync(recentTake, cancellationToken)
            .ConfigureAwait(false);
        var apiKeyResolver = new ApiKeyResolver();
        var configuration = ModelGatewayConfigurationInspector.Inspect(modelOptions, apiKeyResolver);

        return new ControlRoomModelStatus
        {
            Options = modelOptions,
            Configuration = configuration,
            Health = health,
            UsageLogs = usageLogs,
            FallbackCount = usageLogs.Count(log => log.FallbackUsed)
        };
    }

    public async Task<string> BuildMarkdownReportAsync(CancellationToken cancellationToken = default)
    {
        var dashboard = await GetDashboardAsync(cancellationToken).ConfigureAwait(false);
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var candidateMemory = await QueryMemoryAsync(null, ContextMemoryStatus.Candidate, 50, cancellationToken).ConfigureAwait(false);
        var stableMemory = await QueryMemoryAsync(ContextMemoryLayer.Stable, ContextMemoryStatus.Stable, 50, cancellationToken).ConfigureAwait(false);
        var constraints = await QueryConstraintsAsync(null, 100, cancellationToken).ConfigureAwait(false);
        var relations = await QueryRelationsAsync(100, cancellationToken).ConfigureAwait(false);
        var validation = await new CollectionValidationService(_state.ContextStore, _state.RelationStore)
            .ValidateAsync(_state.WorkspaceId, _state.CollectionId, cancellationToken)
            .ConfigureAwait(false);
        var failedJobs = await QueryJobsAsync(ContextJobState.Failed, 50, cancellationToken).ConfigureAwait(false);
        var indexEntries = await _state.Index.SearchAsync(new IndexQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Take = 100
        }, cancellationToken).ConfigureAwait(false);

        return Rendering.MarkdownReportRenderer.Render(
            dashboard,
            status,
            candidateMemory,
            stableMemory,
            constraints,
            relations,
            validation,
            failedJobs,
            indexEntries);
    }

    private IReadOnlyList<SystemHealthItem> BuildSystemHealth(
        ControlRoomStatus status,
        IReadOnlyList<RecentOperation> recentOperations,
        ControlRoomModelStatus modelStatus)
    {
        var rootExists = Directory.Exists(_state.RootPath);
        var logsPath = Path.Combine(_state.RootPath, "logs");
        var modelAvailable = modelStatus.Health.Any(item => item.Availability == ModelAvailability.Available);

        var isPostgres = string.Equals(_state.StorageKind, "postgres", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_state.StorageKind, "postgresql", StringComparison.OrdinalIgnoreCase);

        var storageStatus = "missing";
        var storageDetail = _state.StorageKind == "memory" ? "in-memory" : Path.GetFullPath(_state.RootPath);

        if (_state.StorageKind == "memory" || rootExists)
        {
            storageStatus = "ok";
        }
        else if (isPostgres)
        {
            storageStatus = "ok";
            storageDetail = "PostgreSQL Database";

            // 尝试通过反射进行 ping 探测
            var prop = _state.ContextStore.GetType().GetProperty("ConnectionFactory", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            if (prop != null)
            {
                var factory = prop.GetValue(_state.ContextStore);
                if (factory != null)
                {
                    var pingMethod = factory.GetType().GetMethod("PingAsync", new[] { typeof(CancellationToken) });
                    if (pingMethod != null)
                    {
                        try
                        {
                            var pingTask = pingMethod.Invoke(factory, new object?[] { CancellationToken.None }) as Task;
                            if (pingTask != null)
                            {
                                pingTask.GetAwaiter().GetResult();
                                var result = pingTask.GetType().GetProperty("Result")?.GetValue(pingTask);
                                if (result != null)
                                {
                                    var okProp = result.GetType().GetProperty("Item1"); // ValueTuple<bool, string>
                                    var errProp = result.GetType().GetProperty("Item2");
                                    var ok = (bool?)okProp?.GetValue(result) ?? false;
                                    var err = errProp?.GetValue(result) as string;
                                    if (ok)
                                    {
                                        storageStatus = "ok";
                                        var optionsProp = factory.GetType().GetProperty("Options");
                                        var options = optionsProp?.GetValue(factory);
                                        if (options != null)
                                        {
                                            var connStrProp = options.GetType().GetProperty("ConnectionString");
                                            var connStr = connStrProp?.GetValue(options) as string;
                                            if (!string.IsNullOrWhiteSpace(connStr))
                                            {
                                                var host = "localhost";
                                                var db = "default";
                                                foreach (var part in connStr.Split(';', StringSplitOptions.RemoveEmptyEntries))
                                                {
                                                    var kv = part.Split('=', 2);
                                                    if (kv.Length == 2)
                                                    {
                                                        var k = kv[0].Trim().ToLowerInvariant();
                                                        var v = kv[1].Trim();
                                                        if (k == "host" || k == "server") host = v;
                                                        else if (k == "database" || k == "db") db = v;
                                                    }
                                                }
                                                storageDetail = $"pg://{host}/{db}";
                                            }
                                        }
                                    }
                                    else
                                    {
                                        storageStatus = "error";
                                        storageDetail = $"pg connection failed: {err}";
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // 忽略探测异常
                        }
                    }
                }
            }
        }

        return
        [
            new SystemHealthItem
            {
                Name = "storage",
                Status = storageStatus,
                Detail = storageDetail
            },
            new SystemHealthItem
            {
                Name = "operation logs",
                Status = recentOperations.Count > 0 ? "ok" : "empty",
                Detail = isPostgres ? "cc_context_operation_events Table" : Directory.Exists(logsPath) ? logsPath : "logs directory not found"
            },
            new SystemHealthItem
            {
                Name = "index",
                Status = status.IndexEntryCount > 0 ? "ok" : "empty",
                Detail = $"{status.IndexEntryCount} entries"
            },
            new SystemHealthItem
            {
                Name = "job queue",
                Status = status.FailedJobCount > 0 ? "attention" : "ok",
                Detail = $"{status.QueuedJobCount} queued, {status.RunningJobCount} running, {status.FailedJobCount} failed"
            },
            new SystemHealthItem
            {
                Name = "model gateway",
                Status = modelAvailable ? "ok" : "unavailable",
                Detail = modelAvailable
                    ? "at least one configured model is available"
                    : "no configured model responded successfully"
            }
        ];
    }

    private async Task<IReadOnlyList<RecentOperation>> ReadRecentOperationsAsync(
        int take,
        CancellationToken cancellationToken)
    {
        if (_state.StorageKind == "memory")
        {
            return [];
        }

        var isPostgres = string.Equals(_state.StorageKind, "postgres", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_state.StorageKind, "postgresql", StringComparison.OrdinalIgnoreCase);

        if (isPostgres)
        {
            var prop = _state.ContextStore.GetType().GetProperty("ConnectionFactory", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            if (prop != null)
            {
                var factory = prop.GetValue(_state.ContextStore);
                if (factory != null)
                {
                    var optionsProp = factory.GetType().GetProperty("Options");
                    var tablePrefix = "cc_";
                    if (optionsProp != null)
                    {
                        var options = optionsProp.GetValue(factory);
                        if (options != null)
                        {
                            var prefixProp = options.GetType().GetProperty("TablePrefix");
                            if (prefixProp != null)
                            {
                                tablePrefix = prefixProp.GetValue(options) as string ?? "cc_";
                            }
                        }
                    }

                    var openMethod = factory.GetType().GetMethod("OpenConnectionAsync", new[] { typeof(CancellationToken) });
                    if (openMethod != null)
                    {
                        try
                        {
                            var connTask = openMethod.Invoke(factory, new object?[] { cancellationToken }) as Task;
                            if (connTask != null)
                            {
                                await connTask.ConfigureAwait(false);
                                var conn = connTask.GetType().GetProperty("Result")?.GetValue(connTask) as IDisposable;
                                if (conn != null)
                                {
                                    using (conn)
                                    {
                                        var createCmdMethod = conn.GetType().GetMethod("CreateCommand");
                                        var cmd = createCmdMethod?.Invoke(conn, null) as IDisposable;
                                        if (cmd != null)
                                        {
                                            using (cmd)
                                            {
                                                var cmdTextProp = cmd.GetType().GetProperty("CommandText");
                                                if (cmdTextProp != null)
                                                {
                                                    cmdTextProp.SetValue(cmd, $"SELECT data FROM {tablePrefix}context_operation_events WHERE workspace_id = @workspace_id ORDER BY created_at DESC LIMIT {take};");
                                                }

                                                var paramsProp = cmd.GetType().GetProperty("Parameters");
                                                var parameters = paramsProp?.GetValue(cmd);
                                                if (parameters != null)
                                                {
                                                    var addMethod = parameters.GetType().GetMethod("AddWithValue", new[] { typeof(string), typeof(object) });
                                                    addMethod?.Invoke(parameters, new object?[] { "workspace_id", _state.WorkspaceId });
                                                }

                                                var execReaderMethod = cmd.GetType().GetMethod("ExecuteReaderAsync", new[] { typeof(CancellationToken) });
                                                var readerTask = execReaderMethod?.Invoke(cmd, new object?[] { cancellationToken }) as Task;
                                                if (readerTask != null)
                                                {
                                                    await readerTask.ConfigureAwait(false);
                                                    var reader = readerTask.GetType().GetProperty("Result")?.GetValue(readerTask) as IDisposable;
                                                    if (reader != null)
                                                    {
                                                        using (reader)
                                                        {
                                                            var readMethod = reader.GetType().GetMethod("ReadAsync", new[] { typeof(CancellationToken) });
                                                            var getStringMethod = reader.GetType().GetMethod("GetString", new[] { typeof(int) });
                                                            var list = new List<RecentOperation>();

                                                            while (true)
                                                            {
                                                                var readTask = readMethod?.Invoke(reader, new object?[] { cancellationToken }) as Task<bool>;
                                                                if (readTask == null) break;
                                                                var hasRow = await readTask.ConfigureAwait(false);
                                                                if (!hasRow) break;

                                                                var json = getStringMethod?.Invoke(reader, new object[] { 0 }) as string;
                                                                if (json != null)
                                                                {
                                                                    var operation = JsonSerializer.Deserialize<ContextOperationEvent>(json, JsonOptions);
                                                                    if (operation is not null)
                                                                    {
                                                                        list.Add(new RecentOperation
                                                                        {
                                                                            Time = operation.CreatedAt,
                                                                            OperationName = operation.OperationName,
                                                                            Level = operation.Level.ToString(),
                                                                            Duration = operation.Duration,
                                                                            Message = operation.Message
                                                                        });
                                                                    }
                                                                }
                                                            }
                                                            return list;
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // 降级返回空
                        }
                    }
                }
            }
        }

        var logsPath = Path.Combine(_state.RootPath, "logs");
        if (!Directory.Exists(logsPath))
        {
            return [];
        }

        var logFiles = new List<string>();
        var operationsPath = Path.Combine(logsPath, "operations.jsonl");
        if (File.Exists(operationsPath))
        {
            logFiles.Add(operationsPath);
        }

        logFiles.AddRange(Directory.EnumerateFiles(logsPath, "*.jsonl", SearchOption.AllDirectories));

        var operations = new List<RecentOperation>();
        foreach (var file in logFiles
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(20))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var line in lines.Where(line => !string.IsNullOrWhiteSpace(line)))
            {
                try
                {
                    var operation = JsonSerializer.Deserialize<ContextOperationEvent>(line, JsonOptions);
                    if (operation is null)
                    {
                        continue;
                    }

                    operations.Add(new RecentOperation
                    {
                        Time = operation.CreatedAt,
                        OperationName = operation.OperationName,
                        Level = operation.Level.ToString(),
                        Duration = operation.Duration,
                        Message = operation.Message
                    });
                }
                catch (JsonException)
                {
                }
            }
        }

        return operations
            .OrderByDescending(operation => operation.Time)
            .Take(take > 0 ? take : 10)
            .ToArray();
    }

    public async Task<IReadOnlyList<CompressionQualityReport>> GetRecentCompressionQualityAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        var items = await _state.ContextStore.QueryAsync(new ContextQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Types = ["summary", "compressed", "key_points", "merged", "normalized", "audit"],
            IncludeDerived = true,
            IncludeContent = false,
            Take = 200
        }, cancellationToken).ConfigureAwait(false);

        return items
            .Select(item => CompressionQualityEvaluator.TryReadFromMetadata(item, out var report) ? report : null)
            .Where(report => report is not null)
            .Cast<CompressionQualityReport>()
            .OrderByDescending(report => report.CreatedAt)
            .Take(take > 0 ? take : 5)
            .ToArray();
    }

    private static IReadOnlyList<string> BuildAlerts(
        DashboardSnapshot snapshot,
        ControlRoomStatus status,
        ControlRoomModelStatus modelStatus)
    {
        var alerts = new List<string>();

        if (!Directory.Exists(snapshot.RootPath))
        {
            alerts.Add("存储根目录不存在");
        }

        if (!snapshot.WorkspaceDataFound)
        {
            alerts.Add("当前根目录下没有工作区数据");
        }

        if (snapshot.Memory.RawItems == 0)
        {
            alerts.Add("没有原始条目");
        }

        if (status.FailedJobCount > 0)
        {
            alerts.Add("存在失败任务");
        }

        if (snapshot.Memory.IndexEntries == 0)
        {
            alerts.Add("没有索引项");
        }

        if (snapshot.Memory.Relations == 0)
        {
            alerts.Add("没有关系数据");
        }

        if (snapshot.LatestPackage is null)
        {
            alerts.Add("缺少最近上下文包");
        }

        if (snapshot.RecentCompressionQuality.Any(report => report.RequiresReview))
        {
            alerts.Add("压缩质量需要复核");
        }

        if (!modelStatus.Health.Any(item => item.Availability == ModelAvailability.Available))
        {
            alerts.Add("模型网关不可用");
        }

        return alerts;
    }
}
