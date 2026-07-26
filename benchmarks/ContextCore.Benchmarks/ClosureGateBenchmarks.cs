using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Benchmarks;

// ===========================================================================
// R28-B.6 Closure Gate 性能基准
//
// 测量目标（对应任务 §1~§5）：
//   §1 Provider/Store-call 计数：通过计数包装 store/router/provider，在每次 op
//      结束时拍快照写入静态字典，再由自定义 IColumn 展示到 summary。
//   §2 p50/p95 latency：BenchmarkDotNet 默认统计已提供 Percentile 列，无需额外配置。
//   §3 allocated bytes/op + Gen0/Gen1 GC：[MemoryDiagnoser] 自动收集。
//   §4 100% V2 vs Legacy 差分：LegacyRetrieval / LegacyPackageBuild（基线）
//      对比 V2Retrieval_100Percent / V2PackageBuild_100Percent。
//   §5 sampled shadow 额外开销门：V2Retrieval_100Percent（无 shadow）
//      对比 SampledShadowRetrieval_Rate0 / SampledShadowRetrieval_Rate100。
// ===========================================================================

// ---------------------------------------------------------------------------
// §0 文件计数快照存储 — 跨进程传递计数（BenchmarkDotNet 在子进程中运行 benchmark，
//    静态字典不回传宿主进程，故用文件中转）
// ---------------------------------------------------------------------------

internal static class ClosureGateCounters
{
    // 环境变量名：宿主进程在 Config 构造时设置，子进程继承
    private const string CountersDirEnvVar = "CLOSURE_GATE_COUNTERS_DIR";

    // 计数文件目录：优先从环境变量读取（宿主进程设置），回退到相对路径计算
    private static readonly string CountersDir = ResolveCountersDir();

    // 四个计数维度的键名（与文件内行格式一致）
    public const string StoreQueryCallsKey = "StoreQueryCalls";
    public const string StoreGetCallsKey = "StoreGetCalls";
    public const string RouterCallsKey = "RouterCalls";
    public const string ProviderCallsKey = "ProviderCalls";

    private static string ResolveCountersDir()
    {
        // 优先从环境变量读取（由宿主进程的 Config 构造函数设置，子进程继承）
        var envDir = Environment.GetEnvironmentVariable(CountersDirEnvVar);
        if (!string.IsNullOrWhiteSpace(envDir))
            return envDir;

        // 回退：相对路径计算（同一进程内的 fallback）
        var resultsPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.AppContext.BaseDirectory, "..", "..", "..", "..", "results"));
        return System.IO.Path.Combine(resultsPath, ".counters");
    }

    /// <summary>由 Config 构造函数调用，将计数目录路径通过环境变量传递给子进程。</summary>
    public static string PrepareCountersDir()
    {
        var dir = ResolveCountersDir();
        Environment.SetEnvironmentVariable(CountersDirEnvVar, dir);
        // 清理上一次运行残留的计数文件
        if (System.IO.Directory.Exists(dir))
        {
            foreach (var f in System.IO.Directory.GetFiles(dir, "*.txt"))
            {
                System.IO.File.Delete(f);
            }
        }
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>将一次 benchmark op 的计数值写入文件（benchmark 子进程调用）。</summary>
    public static void WriteCounters(string methodName, int itemCount,
        int storeQuery, int storeGet, int router, int provider)
    {
        System.IO.Directory.CreateDirectory(CountersDir);
        var path = System.IO.Path.Combine(CountersDir, $"{methodName}_{itemCount}.txt");
        // 覆盖写入：每次 op 的最后一次迭代值即为展示值
        System.IO.File.WriteAllText(path,
            $"{StoreQueryCallsKey}={storeQuery}\n" +
            $"{StoreGetCallsKey}={storeGet}\n" +
            $"{RouterCallsKey}={router}\n" +
            $"{ProviderCallsKey}={provider}\n");
    }

    /// <summary>从文件读取指定计数维度的值（IColumn 宿主进程调用）。</summary>
    public static string ReadCounter(string methodName, int itemCount, string counterKey)
    {
        var path = System.IO.Path.Combine(CountersDir, $"{methodName}_{itemCount}.txt");
        if (!System.IO.File.Exists(path))
            return "-";

        foreach (var line in System.IO.File.ReadAllLines(path))
        {
            var parts = line.Split('=', 2);
            if (parts.Length == 2 && parts[0] == counterKey)
                return parts[1];
        }
        return "-";
    }
}

// ---------------------------------------------------------------------------
// §1 计数包装 Store — 记录 QueryAsync / GetAsync 调用次数
// ---------------------------------------------------------------------------

internal sealed class CountingContextStore : IContextStore
{
    private readonly IContextStore _inner;
    public int QueryCallCount;
    public int GetCallCount;

    public CountingContextStore(IContextStore inner) => _inner = inner;

    public void Reset()
    {
        QueryCallCount = 0;
        GetCallCount = 0;
    }

    public Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default)
        => _inner.SaveAsync(item, cancellationToken);

    public async Task<ContextItem?> GetAsync(
        string workspaceId, string collectionId, string id,
        CancellationToken cancellationToken = default)
    {
        GetCallCount++;
        return await _inner.GetAsync(workspaceId, collectionId, id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextItem>> QueryAsync(
        ContextQuery query, CancellationToken cancellationToken = default)
    {
        QueryCallCount++;
        return await _inner.QueryAsync(query, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(
        string workspaceId, string collectionId, string id,
        CancellationToken cancellationToken = default)
        => _inner.DeleteAsync(workspaceId, collectionId, id, cancellationToken);
}

// ---------------------------------------------------------------------------
// §2 计数包装 Router — 记录 RouteAsync 调用次数
// ---------------------------------------------------------------------------

internal sealed class CountingRouter : IRouter
{
    private readonly IRouter _inner;
    public int RouteCallCount;

    public CountingRouter(IRouter inner) => _inner = inner;

    public void Reset() => RouteCallCount = 0;

    public ValueTask<ExpertRoutingDecisionSet> RouteAsync(
        ContextDecisionRuntimeRequest request,
        EffectivePolicySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        RouteCallCount++;
        return _inner.RouteAsync(request, snapshot, cancellationToken);
    }
}

// ---------------------------------------------------------------------------
// §3 计数包装 CandidateProvider — 记录 ExecuteAsync 调用次数
// ---------------------------------------------------------------------------

internal sealed class CountingCandidateProvider : ICandidateProvider
{
    private readonly ICandidateProvider _inner;
    public int ExecuteCallCount;

    public CountingCandidateProvider(ICandidateProvider inner) => _inner = inner;

    public ExpertKind Kind => _inner.Kind;

    public void Reset() => ExecuteCallCount = 0;

    public ValueTask<ExpertExecutionResult> ExecuteAsync(
        CandidateProviderContext context,
        CancellationToken cancellationToken = default)
    {
        ExecuteCallCount++;
        return _inner.ExecuteAsync(context, cancellationToken);
    }
}

// ---------------------------------------------------------------------------
// §4 自定义 IColumn — 从计数文件读取快照展示到 summary
//    BenchmarkDotNet 在子进程中执行 benchmark 方法，IColumn 在宿主进程中渲染 summary，
//    故通过文件中转计数（见 ClosureGateCounters）。
// ---------------------------------------------------------------------------

internal sealed class CounterColumn : IColumn
{
    private readonly string _id;
    private readonly string _columnName;
    private readonly string _counterKey;

    public CounterColumn(string id, string columnName, string counterKey)
    {
        _id = id;
        _columnName = columnName;
        _counterKey = counterKey;
    }

    public string Id => _id;
    public string ColumnName => _columnName;
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => 0;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Dimensionless;
    public string Legend => $"{_columnName}（每次 op 的调用次数）";

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        // 从 BenchmarkCase 提取方法名和 ItemCount 参数，构造文件查找键
        var methodName = benchmarkCase.Descriptor.WorkloadMethod.Name;
        var itemCount = ExtractItemCount(benchmarkCase);
        return ClosureGateCounters.ReadCounter(methodName, itemCount, _counterKey);
    }

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
        => GetValue(summary, benchmarkCase, SummaryStyle.Default);

    public bool IsAvailable(Summary summary) => true;
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    // 从 BenchmarkCase.Parameters 提取 ItemCount 值；未找到时返回 0
    private static int ExtractItemCount(BenchmarkCase benchmarkCase)
    {
        try
        {
            var value = benchmarkCase.Parameters["ItemCount"];
            return value is int i ? i : 0;
        }
        catch
        {
            return 0;
        }
    }
}

// ---------------------------------------------------------------------------
// §5 Benchmark 配置 — 复用 BenchmarkOutputConfig 的导出器 + 追加计数列
// ---------------------------------------------------------------------------

public sealed class ClosureGateBenchmarksConfig : ManualConfig
{
    public ClosureGateBenchmarksConfig()
    {
        // 固定输出路径：benchmarks/results/（与 BenchmarkOutputConfig 一致）
        var resultsPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.AppContext.BaseDirectory, "..", "..", "..", "..", "results"));
        ArtifactsPath = resultsPath;

        // 初始化计数文件目录并通过环境变量传递给子进程（§0 跨进程计数）
        ClosureGateCounters.PrepareCountersDir();

        // JSON 全量导出（含所有统计指标），用于 baseline/current 对比
        AddExporter(JsonExporter.Full);
        // Markdown GitHub 格式导出，便于 review
        AddExporter(MarkdownExporter.GitHub);
        // CSV 导出，便于脚本处理
        AddExporter(CsvExporter.Default);

        AddLogger(ConsoleLogger.Default);
        AddColumnProvider(DefaultColumnProviders.Instance);

        // 追加自定义计数列（§1 Provider/Store-call 计数）
        AddColumn(
            new CounterColumn("StoreQueryCalls", "StoreQueryCalls", ClosureGateCounters.StoreQueryCallsKey),
            new CounterColumn("StoreGetCalls", "StoreGetCalls", ClosureGateCounters.StoreGetCallsKey),
            new CounterColumn("RouterCalls", "RouterCalls", ClosureGateCounters.RouterCallsKey),
            new CounterColumn("ProviderCalls", "ProviderCalls", ClosureGateCounters.ProviderCallsKey));
    }
}

// ---------------------------------------------------------------------------
// §6 ClosureGateBenchmarks — 主基准类
// ---------------------------------------------------------------------------

[MemoryDiagnoser]
[Config(typeof(ClosureGateBenchmarksConfig))]
public class ClosureGateBenchmarks
{
    private const string WorkspaceId = "closure-gate-ws";
    private const string CollectionId = "closure-gate-col";

    // 数据规模：ContextItem 数量。其余 store 按比例缩放（复用 PackageBuildBenchmarks 策略）。
    [Params(10, 50, 200)]
    public int ItemCount { get; set; }

    // === 原始 InMemory stores（填充数据后供 Legacy 路径直接使用）===
    private InMemoryContextStore _contextStore = null!;
    private InMemoryMemoryStore _memoryStore = null!;
    private InMemoryConstraintStore _constraintStore = null!;
    private InMemoryGlobalContextStore _globalContextStore = null!;
    private InMemoryRelationStore _relationStore = null!;

    // === 计数包装（仅 V2 路径使用）===
    private CountingContextStore _countingStore = null!;
    private CountingRouter _countingRouter = null!;
    private List<CountingCandidateProvider> _countingProviders = null!;

    // === Legacy 路径 ===
    private HybridContextRetriever _legacyRetriever = null!;
    private BasicContextPackageBuilder _legacyPackageBuilder = null!;

    // === V2 共享组件 ===
    private IContextDecisionRuntime _v2Runtime = null!;
    private ShadowDecisionRuntime _shadowRuntime = null!;
    private RetrievalResultProjector _retrievalProjector = null!;
    private PackageResultProjector _packageProjector = null!;
    private CutoverController _cutover100 = null!;

    // === V2 Retrieval 实例（不同 shadow 配置）===
    private AuthoritativeRetrievalRuntime _v2Retriever100 = null!;          // cutover=100, 无 shadow
    private AuthoritativeRetrievalRuntime _sampledShadowRetriever0 = null!;  // shadow rate=0
    private AuthoritativeRetrievalRuntime _sampledShadowRetriever100 = null!; // shadow rate=1.0

    // === V2 Package 实例 ===
    private AuthoritativePackageRuntime _v2PackageBuilder100 = null!;

    // === 实验平面集成（实现 IAsyncDisposable，需在 Cleanup 中释放）===
    private DecisionExperimentPlaneIntegration _experimentPlaneRate0 = null!;
    private DecisionExperimentPlaneIntegration _experimentPlaneRate100 = null!;

    // === 请求 ===
    private ContextRetrievalRequest _retrievalRequest = null!;
    private ContextPackageRequest _packageRequest = null!;

    [GlobalSetup]
    public void Setup()
    {
        // 1. 创建并填充 InMemory stores
        _contextStore = new InMemoryContextStore();
        _memoryStore = new InMemoryMemoryStore();
        _constraintStore = new InMemoryConstraintStore();
        _globalContextStore = new InMemoryGlobalContextStore();
        _relationStore = new InMemoryRelationStore();

        PopulateStores();

        // 2. 创建计数 store（包装同一份 InMemory 数据，供 V2 Provider 使用）
        _countingStore = new CountingContextStore(_contextStore);

        // 3. 构造 Legacy retriever（使用原始 store，避免 fanout 类型推断失效）
        _legacyRetriever = new HybridContextRetriever(
            _contextStore,
            _memoryStore,
            _relationStore,
            embeddingProvider: null,
            vectorStore: null,
            traceStore: null,
            decisionTraceStore: null,
            fanoutOptions: new RetrievalFanoutOptions { MaxReadFanout = 16 });

        // 4. 构造 Legacy package builder
        _legacyPackageBuilder = new BasicContextPackageBuilder(
            _contextStore,
            _constraintStore,
            _globalContextStore,
            _memoryStore,
            _relationStore,
            workingMemoryService: _memoryStore);

        // 5. 构造 V2 共享组件
        var safetyGate = new DefaultSafetyGate();
        var lifecycleGate = new DefaultLifecycleGate();
        var utilityScorer = new DefaultUtilityScorer();
        var globalAllocator = new DefaultGlobalAllocator();

        var engine = new DefaultContextDecisionEngine(
            policyRegistry: null,
            safetyGate,
            lifecycleGate,
            utilityScorer,
            globalAllocator);

        var policyProvider = new DefaultResolvedPolicyProvider();
        var expertCatalog = new DefaultExpertCatalog();

        // 计数 Router（包装 DefaultRouter）
        _countingRouter = new CountingRouter(new DefaultRouter(expertCatalog));

        // 计数 Provider 列表（V2 路径使用计数 store）
        // Semantic Provider 不注入 IVectorStore（返回空），避免构造复杂依赖。
        _countingProviders = new List<CountingCandidateProvider>
        {
            new(new MandatoryCandidateProvider(_countingStore)),
            new(new ConstraintCandidateProvider(_constraintStore)),
            new(new LexicalCandidateProvider(_countingStore)),
            new(new SemanticCandidateProvider(_countingStore, _memoryStore, embeddingProvider: null, vectorStore: null)),
            new(new WorkingMemoryCandidateProvider(_memoryStore)),
            new(new StableMemoryCandidateProvider(_memoryStore)),
            new(new GraphCandidateProvider(_countingStore, _relationStore, _memoryStore)),
        };

        var canonicalMerger = new DefaultCanonicalCandidateMerger();
        var earlyAdmissionGate = new DefaultEarlyAdmissionGate();
        var featurePipeline = new DefaultFeaturePipeline();

        // V2 pure Runtime（注入计数 Router + 计数 Provider）
        _v2Runtime = new DefaultContextDecisionRuntime(
            engine,
            policyProvider,
            _countingRouter,
            expertCatalog,
            _countingProviders,
            canonicalMerger,
            earlyAdmissionGate,
            featurePipeline,
            safetyGate,
            lifecycleGate,
            utilityScorer);

        // Shadow 编排器（V2-only 路径不实际调用，但构造函数要求非 null）
        _shadowRuntime = new ShadowDecisionRuntime(_v2Runtime, new DecisionExperimentPlane());

        // Projector（默认截断器）
        _retrievalProjector = new RetrievalResultProjector();
        _packageProjector = new PackageResultProjector();

        // Cutover 控制器：100% V2
        _cutover100 = new CutoverController(100);

        // 6. 构造 V2-only Retrieval（cutover=100, 无 shadow）
        _v2Retriever100 = new AuthoritativeRetrievalRuntime(
            _legacyRetriever,
            _v2Runtime,
            _shadowRuntime,
            _retrievalProjector,
            _cutover100,
            shadowGate: null,
            experimentPlane: null);

        // 7. 构造 sampled shadow Retrieval（rate=0 — 不执行 Legacy 对照）
        _experimentPlaneRate0 = new DecisionExperimentPlaneIntegration(
            new DecisionExperimentPlane(),
            new ShadowGateEvaluator(),
            new CutoverConfiguration
            {
                CutoverPercentage = 100,
                EnableSampledShadow = true,
                ShadowSampleRate = 0.0
            });

        _sampledShadowRetriever0 = new AuthoritativeRetrievalRuntime(
            _legacyRetriever,
            _v2Runtime,
            _shadowRuntime,
            _retrievalProjector,
            _cutover100,
            shadowGate: null,
            experimentPlane: _experimentPlaneRate0);

        // 8. 构造 sampled shadow Retrieval（rate=1.0 — 每次都执行 Legacy 对照）
        _experimentPlaneRate100 = new DecisionExperimentPlaneIntegration(
            new DecisionExperimentPlane(),
            new ShadowGateEvaluator(),
            new CutoverConfiguration
            {
                CutoverPercentage = 100,
                EnableSampledShadow = true,
                ShadowSampleRate = 1.0
            });

        _sampledShadowRetriever100 = new AuthoritativeRetrievalRuntime(
            _legacyRetriever,
            _v2Runtime,
            _shadowRuntime,
            _retrievalProjector,
            _cutover100,
            shadowGate: null,
            experimentPlane: _experimentPlaneRate100);

        // 9. 构造 V2-only Package（cutover=100, 无 shadow）
        _v2PackageBuilder100 = new AuthoritativePackageRuntime(
            _legacyPackageBuilder,
            _v2Runtime,
            _shadowRuntime,
            _packageProjector,
            _cutover100,
            shadowGate: null,
            experimentPlane: null);

        // 10. 构建 retrieval request
        _retrievalRequest = new ContextRetrievalRequest
        {
            OperationId = "closure-gate-retrieval-op",
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            QueryText = "上下文包构建基准测试 package build benchmark",
            RequiredTags = ["task"],
            RequiredIds = ["ctx-0", "ctx-1"], // 触发 MandatoryCandidateProvider.GetAsync 路径
            TopK = 10,
            TokenBudget = 4000,
            IncludeKeywordRecall = true,
            IncludeVectorRecall = false, // 无 IVectorStore，禁用语义召回
            IncludeRelationExpansion = true,
            IncludeWorkingMemory = true,
            IncludeStableMemory = true,
            IncludeContent = true
        };

        // 11. 构建 package request（含全 section 策略，触发 Legacy 6 次 store 查询）
        var policy = new ContextPackagePolicy
        {
            Id = "closure-gate-policy-all",
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Name = "ClosureGateAllSections",
            Description = "Closure Gate 基准策略：启用全部 Include 标志以触发完整 store 查询。",
            Mode = ContextPackageMode.None,
            TokenBudget = 4000,
            IncludeRecentRawContext = true,
            IncludeHardConstraints = true,
            IncludeSoftConstraints = true,
            IncludeWorkingMemory = true,
            IncludeStableMemory = true,
            IncludeGlobalContext = true,
            MaxRecentItems = 20
        };

        _packageRequest = new ContextPackageRequest
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            QueryText = "上下文包构建基准测试 package build benchmark",
            RequiredTags = ["task"],
            TokenBudget = 4000,
            Mode = ContextPackageMode.None,
            Policy = policy,
            RequestId = "closure-gate-package-op"
        };
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        // 释放实验平面集成（停止后台 consumer 任务）
        await _experimentPlaneRate0.DisposeAsync().ConfigureAwait(false);
        await _experimentPlaneRate100.DisposeAsync().ConfigureAwait(false);
    }

    // === 数据填充（复用 PackageBuildBenchmarks 的填充逻辑）===

    private void PopulateStores()
    {
        var now = DateTimeOffset.UtcNow;
        var rand = new Random(20260715); // 固定种子保证可复现

        // 1) ContextItems：ItemCount 条，内容 200-800 字符（中英混合）
        // 前两条标记 mandatory，触发 MandatoryCandidateProvider 召回路径。
        for (int i = 0; i < ItemCount; i++)
        {
            var createdAt = now.AddDays(-rand.Next(0, 90)).AddMinutes(-rand.Next(0, 1440));
            var content = BuildItemContent(i, rand);
            var tags = i < 2
                ? new[] { "task", "mandatory" }
                : new[] { "task", "package" };
            var item = new ContextItem
            {
                Id = $"ctx-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Type = "note",
                Title = $"上下文条目 {i} / context item {i}",
                Content = content,
                ContentFormat = ContextContentFormat.Markdown,
                Tags = tags,
                Importance = 0.3 + rand.NextDouble() * 0.7,
                CreatedAt = createdAt,
                UpdatedAt = createdAt.AddMinutes(rand.Next(1, 600))
            };
            _contextStore.SaveAsync(item).GetAwaiter().GetResult();
        }

        // 2) ContextMemoryItems：Stable 层 ItemCount/5 条
        var stableCount = Math.Max(1, ItemCount / 5);
        for (int i = 0; i < stableCount; i++)
        {
            var createdAt = now.AddDays(-rand.Next(0, 180));
            var memory = new ContextMemoryItem
            {
                Id = $"mem-stable-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Layer = ContextMemoryLayer.Stable,
                Status = ContextMemoryStatus.Stable,
                Type = "fact",
                Content = $"稳定记忆 stable memory fact #{i}：项目基线与长期约定 {BuildItemContent(i, rand)}",
                ContentFormat = ContextContentFormat.Markdown,
                Tags = ["task"],
                Importance = 0.5 + rand.NextDouble() * 0.5,
                CreatedAt = createdAt,
                UpdatedAt = createdAt
            };
            _memoryStore.SaveAsync(memory).GetAwaiter().GetResult();
        }

        // 3) ContextMemoryItems：Working 层 ItemCount/10 条
        var workingCount = Math.Max(1, ItemCount / 10);
        for (int i = 0; i < workingCount; i++)
        {
            var createdAt = now.AddHours(-rand.Next(1, 48));
            var memory = new ContextMemoryItem
            {
                Id = $"mem-working-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Layer = ContextMemoryLayer.Working,
                Status = ContextMemoryStatus.Active,
                Type = "note",
                Content = $"工作记忆 working memory #{i}：当前会话活跃信息 {BuildItemContent(i, rand)}",
                ContentFormat = ContextContentFormat.Markdown,
                Tags = ["task"],
                Importance = 0.6 + rand.NextDouble() * 0.4,
                CreatedAt = createdAt,
                UpdatedAt = createdAt
            };
            _memoryStore.SaveAsync(memory).GetAwaiter().GetResult();
        }

        // 4) 约束：10 hard + 10 soft（Status=Active 以保证被计入包，而非丢弃）
        for (int i = 0; i < 10; i++)
        {
            var hard = new ContextConstraint
            {
                Id = $"con-hard-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Scope = ContextScope.Collection,
                Level = ConstraintLevel.Hard,
                Content = $"硬约束 hard constraint #{i}：必须遵守的输出格式与安全边界。",
                Status = ContextMemoryStatus.Active,
                Confidence = 0.9,
                CreatedAt = now,
                UpdatedAt = now
            };
            _constraintStore.SaveAsync(hard).GetAwaiter().GetResult();

            var soft = new ContextConstraint
            {
                Id = $"con-soft-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Scope = ContextScope.Collection,
                Level = ConstraintLevel.Soft,
                Content = $"软约束 soft constraint #{i}：尽量遵守的风格与简洁性偏好。",
                Status = ContextMemoryStatus.Active,
                Confidence = 0.7,
                CreatedAt = now,
                UpdatedAt = now
            };
            _constraintStore.SaveAsync(soft).GetAwaiter().GetResult();
        }

        // 5) 全局上下文：5 条
        for (int i = 0; i < 5; i++)
        {
            var global = new ContextGlobalItem
            {
                Id = $"global-{i}",
                WorkspaceId = WorkspaceId,
                CollectionId = null,
                Scope = ContextScope.Workspace,
                Type = "preference",
                Content = $"全局上下文 global context #{i}：跨集合共享的用户偏好与项目设定。",
                ContentFormat = ContextContentFormat.Markdown,
                Tags = ["task"],
                Importance = 0.5 + rand.NextDouble() * 0.5,
                CreatedAt = now,
                UpdatedAt = now
            };
            _globalContextStore.SaveAsync(global).GetAwaiter().GetResult();
        }
    }

    // 生成 200-800 字符的中英混合内容。
    private static string BuildItemContent(int seed, Random rand)
    {
        var length = 200 + rand.Next(0, 600);
        var sb = new System.Text.StringBuilder(length + 16);
        var phrases = new[]
        {
            "上下文包构建", "package build", "基准测试", "benchmark",
            "记忆召回", "memory recall", "约束注入", "constraint injection",
            "全局偏好", "global preference", "工作记忆", "working memory",
            "稳定记忆", "stable memory", "近期上下文", "recent context"
        };
        while (sb.Length < length)
        {
            sb.Append(phrases[rand.Next(phrases.Length)]);
            sb.Append(' ');
            sb.Append('#');
            sb.Append(seed);
            sb.Append(' ');
        }
        return sb.ToString(0, Math.Min(sb.Length, length));
    }

    // === 计数辅助：重置所有计数器 ===
    private void ResetCounters()
    {
        _countingStore.Reset();
        _countingRouter.Reset();
        foreach (var p in _countingProviders)
        {
            p.Reset();
        }
    }

    // === 计数辅助：拍快照写入文件（供 IColumn 跨进程读取）===
    private void SnapshotCounters(string methodName)
    {
        ClosureGateCounters.WriteCounters(
            methodName, ItemCount,
            _countingStore.QueryCallCount,
            _countingStore.GetCallCount,
            _countingRouter.RouteCallCount,
            _countingProviders.Sum(p => p.ExecuteCallCount));
    }

    // ===================================================================
    // §4 Legacy 基线
    // ===================================================================

    // Legacy 检索路径（HybridContextRetriever.RetrieveAsync）
    [Benchmark(Baseline = true)]
    public async Task LegacyRetrieval()
    {
        var result = await _legacyRetriever.RetrieveAsync(_retrievalRequest, CancellationToken.None);
        // 防止死代码消除
        _ = result.SelectedItems.Count;
    }

    // Legacy 打包路径（BasicContextPackageBuilder.BuildDetailedAsync）
    [Benchmark]
    public async Task LegacyPackageBuild()
    {
        var result = await _legacyPackageBuilder.BuildDetailedAsync(_packageRequest, CancellationToken.None);
        // 防止死代码消除
        _ = result.Package.Sections.Count;
    }

    // ===================================================================
    // §4 100% V2-only 路径（cutover=100, 无 shadow）
    // ===================================================================

    // V2 检索路径（AuthoritativeRetrievalRuntime, cutover=100）
    [Benchmark]
    public async Task V2Retrieval_100Percent()
    {
        ResetCounters();
        var result = await _v2Retriever100.RetrieveAsync(_retrievalRequest, CancellationToken.None);
        // 防止死代码消除
        _ = result.SelectedItems.Count;
        SnapshotCounters(nameof(V2Retrieval_100Percent));
    }

    // V2 打包路径（AuthoritativePackageRuntime, cutover=100）
    [Benchmark]
    public async Task V2PackageBuild_100Percent()
    {
        ResetCounters();
        var result = await _v2PackageBuilder100.BuildAsync(_packageRequest, CancellationToken.None);
        // 防止死代码消除
        _ = result.Sections.Count;
        SnapshotCounters(nameof(V2PackageBuild_100Percent));
    }

    // ===================================================================
    // §5 sampled shadow 额外开销门
    // ===================================================================

    // shadow rate=0：experimentPlane 已注入但采样率为 0，不执行 Legacy 对照。
    // 测量 experimentPlane 空检查的额外开销（应近似 V2Retrieval_100Percent）。
    [Benchmark]
    public async Task SampledShadowRetrieval_Rate0()
    {
        ResetCounters();
        var result = await _sampledShadowRetriever0.RetrieveAsync(_retrievalRequest, CancellationToken.None);
        // 防止死代码消除
        _ = result.SelectedItems.Count;
        SnapshotCounters(nameof(SampledShadowRetrieval_Rate0));
    }

    // shadow rate=1.0：每次都执行 Legacy 对照 + parity 报告。
    // 测量 sampled shadow 全量开启的额外开销（V2 + Legacy + parity）。
    [Benchmark]
    public async Task SampledShadowRetrieval_Rate100()
    {
        ResetCounters();
        var result = await _sampledShadowRetriever100.RetrieveAsync(_retrievalRequest, CancellationToken.None);
        // 防止死代码消除
        _ = result.SelectedItems.Count;
        SnapshotCounters(nameof(SampledShadowRetrieval_Rate100));
    }
}
