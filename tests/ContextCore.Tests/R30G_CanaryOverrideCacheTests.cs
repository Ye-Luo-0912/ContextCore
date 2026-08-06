using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Evolution;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Service.Extensions;
using ContextCore.Storage.InMemory.Stores;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Tests;

// ===========================================================================
// WP-D3（P0-13）：Canary Kill Switch 查询 fail-safe + TTL 缓存
//
// 背景：Authoritative Runtime 在决定是否走 V2 时同步查询 Emergency Override Store。
// 该查询位于在线请求路径：无本地缓存（每个 Canary 请求一次 DB 往返），且无异常降级
// （Store/DB 异常直接使 Retrieval/Package 请求失败，而不是安全回退 V1）。
//
// 修复后语义：
// - Override Active       → V1
// - Override Store Error  → V1 + 告警日志（请求不失败）
// - Override Inactive     → 按正常百分比路由
// - 进程内 TTL 缓存（默认 5 秒，可配置 CanaryOverrideCache:Ttl）避免每请求 DB 往返；
// - 本地 TrySet/TryClear 写穿后立即失效；存储异常原样传播（由运行时 fail-safe）。
// ===========================================================================

[TestClass]
[TestCategory("R30")]
[TestCategory("Canary")]
public sealed class R30G_CanaryOverrideCacheTests
{
    private const string RunId = "run-override-cache-001";

    // ---------------------------------------------------------------------------
    // 1. TTL 缓存：TTL 内命中不访问内层存储
    // ---------------------------------------------------------------------------
    [TestMethod]
    public async Task GetActive_WithinTtl_ServesCachedValue_WithoutInnerCall()
    {
        var inner = new ScriptedOverrideStore { ActiveResult = MakeOverride(RunId) };
        var time = new MutableTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var cache = new CachedCanaryEmergencyOverrideStore(inner, options: null, timeProvider: time);

        var first = await cache.GetActiveAsync(RunId);
        Assert.IsNotNull(first);
        Assert.AreEqual(1, inner.GetActiveCallCount);

        // TTL 内再次查询：命中缓存，不访问内层存储
        var second = await cache.GetActiveAsync(RunId);
        Assert.IsNotNull(second);
        Assert.AreEqual(1, inner.GetActiveCallCount, "TTL 内必须命中缓存，不得再次访问内层存储。");
    }

    // ---------------------------------------------------------------------------
    // 2. TTL 过期后重新读取真实存储
    // ---------------------------------------------------------------------------
    [TestMethod]
    public async Task GetActive_AfterTtl_RefreshesFromInner()
    {
        var inner = new ScriptedOverrideStore { ActiveResult = MakeOverride(RunId) };
        var time = new MutableTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var cache = new CachedCanaryEmergencyOverrideStore(inner, options: null, timeProvider: time);

        await cache.GetActiveAsync(RunId);
        Assert.AreEqual(1, inner.GetActiveCallCount);

        // TTL 未过期前：命中缓存（正覆盖缓存）
        time.Advance(TimeSpan.FromSeconds(4));
        await cache.GetActiveAsync(RunId);
        Assert.AreEqual(1, inner.GetActiveCallCount);

        // TTL 过期后：重新读取
        time.Advance(TimeSpan.FromSeconds(2)); // 累计 6 秒 > 默认 5 秒
        await cache.GetActiveAsync(RunId);
        Assert.AreEqual(2, inner.GetActiveCallCount, "TTL 过期后必须重新读取真实存储。");
    }

    // ---------------------------------------------------------------------------
    // 3. 无覆盖结果不缓存（负缓存会放大 Kill Switch 跨节点传播窗口）
    // ---------------------------------------------------------------------------
    [TestMethod]
    public async Task GetActive_NegativeResult_IsNotCached()
    {
        var inner = new ScriptedOverrideStore(); // ActiveResult = null
        var time = new MutableTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var cache = new CachedCanaryEmergencyOverrideStore(inner, options: null, timeProvider: time);

        var first = await cache.GetActiveAsync(RunId);
        Assert.IsNull(first);
        Assert.AreEqual(1, inner.GetActiveCallCount);

        // 无覆盖不缓存：第二次查询必须重新访问真实存储——
        // 若另一节点刚触发 Kill Switch，本节点下一请求即可感知（无 TTL 传播窗口）。
        var second = await cache.GetActiveAsync(RunId);
        Assert.IsNull(second);
        Assert.AreEqual(2, inner.GetActiveCallCount, "无覆盖结果不得缓存，必须重新访问真实存储。");
    }

    // ---------------------------------------------------------------------------
    // 3b. 无覆盖不缓存 → 另一节点触发覆盖后立即被感知（Kill Switch 无传播窗口）
    // ---------------------------------------------------------------------------
    [TestMethod]
    public async Task GetActive_KillSwitchTriggeredElsewhere_IsObservedImmediately()
    {
        var inner = new ScriptedOverrideStore(); // 初始无覆盖
        var time = new MutableTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var cache = new CachedCanaryEmergencyOverrideStore(inner, options: null, timeProvider: time);

        Assert.IsNull(await cache.GetActiveAsync(RunId));
        Assert.AreEqual(1, inner.GetActiveCallCount);

        // 另一节点触发 Kill Switch（本节点无写穿失效机会）
        inner.ActiveResult = MakeOverride(RunId);
        var observed = await cache.GetActiveAsync(RunId);
        Assert.IsNotNull(observed, "无覆盖不缓存时，新触发的 Kill Switch 应立即被感知。");
        Assert.AreEqual(2, inner.GetActiveCallCount);
    }

    // ---------------------------------------------------------------------------
    // 4. 内层存储异常：原样传播（不吞、不以过期缓存充当无覆盖应答）
    // ---------------------------------------------------------------------------
    [TestMethod]
    public async Task GetActive_InnerThrows_PropagatesToCaller()
    {
        var inner = new ScriptedOverrideStore { GetActiveException = new InvalidOperationException("store down") };
        var time = new MutableTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var cache = new CachedCanaryEmergencyOverrideStore(inner, options: null, timeProvider: time);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => cache.GetActiveAsync(RunId).AsTask());
        Assert.AreEqual(1, inner.GetActiveCallCount);
    }

    // ---------------------------------------------------------------------------
    // 5. 写穿：TrySetOverrideAsync 成功后立即失效本地缓存
    // ---------------------------------------------------------------------------
    [TestMethod]
    public async Task TrySetOverride_Success_InvalidatesCache()
    {
        var inner = new ScriptedOverrideStore(); // 初始无覆盖
        var time = new MutableTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var cache = new CachedCanaryEmergencyOverrideStore(inner, options: null, timeProvider: time);

        // 预热负缓存
        Assert.IsNull(await cache.GetActiveAsync(RunId));
        Assert.AreEqual(1, inner.GetActiveCallCount);

        // 写穿设置覆盖（模拟另一节点/运维设置 Kill Switch），本地立即失效
        inner.ActiveResult = MakeOverride(RunId);
        Assert.IsTrue(await cache.TrySetOverrideAsync(RunId, "v2 异常", "ops-oncall"));
        Assert.AreEqual(1, inner.SetCallCount);

        // 失效后再次查询必须读取真实存储（而非命中旧的负缓存）
        var refreshed = await cache.GetActiveAsync(RunId);
        Assert.IsNotNull(refreshed, "写穿后本地缓存必须失效并重新读取真实存储。");
        Assert.AreEqual(2, inner.GetActiveCallCount);
    }

    // ---------------------------------------------------------------------------
    // 6. 写穿：TrySetOverrideAsync 返回 false（已存在覆盖）也必须失效缓存
    // ---------------------------------------------------------------------------
    [TestMethod]
    public async Task TrySetOverride_AlreadyActive_ReturnsFalse_StillInvalidatesCache()
    {
        var inner = new ScriptedOverrideStore { SetResult = false }; // 已存在活跃覆盖 → 不覆盖
        var time = new MutableTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var cache = new CachedCanaryEmergencyOverrideStore(inner, options: null, timeProvider: time);

        // 预热负缓存（本地认为无覆盖，但真实存储其实已有覆盖——另一节点写入）
        Assert.IsNull(await cache.GetActiveAsync(RunId));
        Assert.AreEqual(1, inner.GetActiveCallCount);

        // 设置失败（false 意味着真相已变化），必须失效缓存
        Assert.IsFalse(await cache.TrySetOverrideAsync(RunId, "v2 异常", "ops-oncall"));
        inner.ActiveResult = MakeOverride(RunId); // 模拟真实存储中的既有覆盖

        var refreshed = await cache.GetActiveAsync(RunId);
        Assert.IsNotNull(refreshed, "TrySet 返回 false（已存在覆盖）时必须失效缓存，避免把过期负缓存当真相。");
    }

    // ---------------------------------------------------------------------------
    // 7. 写穿：TryClearOverrideAsync 成功后立即失效本地缓存
    // ---------------------------------------------------------------------------
    [TestMethod]
    public async Task TryClearOverride_Success_InvalidatesCache()
    {
        var inner = new ScriptedOverrideStore { ActiveResult = MakeOverride(RunId) };
        var time = new MutableTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var cache = new CachedCanaryEmergencyOverrideStore(inner, options: null, timeProvider: time);

        Assert.IsNotNull(await cache.GetActiveAsync(RunId));
        Assert.AreEqual(1, inner.GetActiveCallCount);

        // 清除覆盖（模拟运维解除 Kill Switch），本地立即失效
        inner.ActiveResult = null;
        Assert.IsTrue(await cache.TryClearOverrideAsync(RunId, "ops-oncall"));
        Assert.AreEqual(1, inner.ClearCallCount);

        var refreshed = await cache.GetActiveAsync(RunId);
        Assert.IsNull(refreshed, "清除覆盖后本地缓存必须失效并重新读取真实存储。");
        Assert.AreEqual(2, inner.GetActiveCallCount);
    }

    // ---------------------------------------------------------------------------
    // 8. GetActiveOverridesAsync 绕过缓存（运维 / 对账路径读全量，非热路径）
    // ---------------------------------------------------------------------------
    [TestMethod]
    public async Task GetActiveOverridesAsync_BypassesCache()
    {
        var inner = new ScriptedOverrideStore { ActiveResult = MakeOverride(RunId) };
        var time = new MutableTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var cache = new CachedCanaryEmergencyOverrideStore(inner, options: null, timeProvider: time);

        await cache.GetActiveOverridesAsync();
        await cache.GetActiveOverridesAsync();
        Assert.AreEqual(2, inner.GetActiveOverridesCallCount,
            "GetActiveOverridesAsync 必须绕过缓存，每次读取全量真实数据。");
    }

    // ---------------------------------------------------------------------------
    // 9. 运行时 fail-safe：Override Store 查询抛异常 → 回退 V1，请求不失败
    // ---------------------------------------------------------------------------
    [TestMethod]
    public async Task RetrievalRuntime_OverrideStoreGetActiveThrows_FallsBackV1_NoRequestFailure()
    {
        var trackingStore = new CallTrackingContextStore();
        var legacyRetriever = new HybridContextRetriever(trackingStore);
        var stubV2 = new RecordingDecisionRuntime(R28BTestHelpers.MakeResult("op-kill-throw"));
        var shadowRuntime = new ShadowDecisionRuntime(stubV2, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();
        var throwingStore = new ThrowingGetActiveOverrideStore();

        var runtime = new AuthoritativeRetrievalRuntime(
            legacyRetriever, stubV2, shadowRuntime, projector,
            new CutoverController(cutoverPercentage: 100),
            canaryMetricsCollector: null,
            emergencyOverrideStore: throwingStore);

        var request = new ContextRetrievalRequest
        {
            OperationId = "op-kill-throw",
            WorkspaceId = "ws-kill-throw",
            CollectionId = "col-kill-throw",
            Metadata = new Dictionary<string, string>
            {
                [CanaryRunIdResolver.RunIdMetadataKey] = RunId
            }
        };

        // 修复前：GetActiveAsync 异常直接使请求失败；修复后：fail-closed 回退 V1。
        await runtime.RetrieveAsync(request, CancellationToken.None);

        Assert.AreEqual(1, throwingStore.GetActiveCallCount, "Kill Switch 查询必须被调用（canary 命中 V2 后）。");
        Assert.AreEqual(0, stubV2.ExecuteCallCount, "存储故障必须按「覆盖活跃」处理，强制回退 V1。");
        Assert.AreEqual(1, trackingStore.QueryCallCount, "应走 Legacy 检索路径，请求不失败。");
    }

    [TestMethod]
    public async Task PackageRuntime_OverrideStoreGetActiveThrows_FallsBackV1_NoRequestFailure()
    {
        var trackingStore = new CallTrackingContextStore();
        var legacyBuilder = new BasicContextPackageBuilder(trackingStore);
        var stubV2 = new RecordingDecisionRuntime(R28BTestHelpers.MakeResult("op-kill-pkg-throw"));
        var shadowRuntime = new ShadowDecisionRuntime(stubV2, new DecisionExperimentPlane());
        var projector = new PackageResultProjector();
        var throwingStore = new ThrowingGetActiveOverrideStore();

        var runtime = new AuthoritativePackageRuntime(
            legacyBuilder, stubV2, shadowRuntime, projector,
            new CutoverController(cutoverPercentage: 100),
            canaryMetricsCollector: null,
            emergencyOverrideStore: throwingStore);

        var request = new ContextPackageRequest
        {
            WorkspaceId = "ws-kill-pkg-throw",
            CollectionId = "col-kill-pkg-throw",
            QueryText = "存储故障回退测试",
            TokenBudget = 4096,
            Metadata = new Dictionary<string, string>
            {
                [CanaryRunIdResolver.RunIdMetadataKey] = RunId
            }
        };

        await runtime.BuildDetailedAsync(request, CancellationToken.None);

        Assert.AreEqual(0, stubV2.ExecuteCallCount, "存储故障必须按「覆盖活跃」处理，强制回退 V1。");
        Assert.IsTrue(trackingStore.QueryCallCount >= 1, "应走 Legacy 构建路径，请求不失败。");
    }

    // ---------------------------------------------------------------------------
    // 10. 端到端：TTL 缓存 + 写穿失效 → Kill Switch 立即生效
    // ---------------------------------------------------------------------------
    [TestMethod]
    public async Task RetrievalRuntime_CachedStore_WriteThroughSet_ImmediatelyForcesV1()
    {
        var trackingStore = new CallTrackingContextStore();
        var legacyRetriever = new HybridContextRetriever(trackingStore);
        var stubV2 = new RecordingDecisionRuntime(R28BTestHelpers.MakeResult("op-cache-wt"));
        var shadowRuntime = new ShadowDecisionRuntime(stubV2, new DecisionExperimentPlane());
        var projector = new RetrievalResultProjector();
        var inner = new InMemoryCanaryEmergencyOverrideStore();
        var cached = new CachedCanaryEmergencyOverrideStore(inner, options: null, timeProvider: new MutableTimeProvider(CanaryAcceptanceHelpers.BaseTime));

        var runtime = new AuthoritativeRetrievalRuntime(
            legacyRetriever, stubV2, shadowRuntime, projector,
            new CutoverController(cutoverPercentage: 100),
            canaryMetricsCollector: null,
            emergencyOverrideStore: cached);

        var request = new ContextRetrievalRequest
        {
            OperationId = "op-cache-wt",
            WorkspaceId = "ws-cache-wt",
            CollectionId = "col-cache-wt",
            Metadata = new Dictionary<string, string>
            {
                [CanaryRunIdResolver.RunIdMetadataKey] = RunId
            }
        };

        // 请求 1：无覆盖 → 走 V2（无覆盖不缓存，写穿后下一请求即可感知）
        await runtime.RetrieveAsync(request, CancellationToken.None);
        Assert.AreEqual(1, stubV2.ExecuteCallCount);
        Assert.AreEqual(0, trackingStore.QueryCallCount);

        // 通过缓存装饰器写穿设置 Kill Switch（等价于运维 API 在本地节点触发）
        Assert.IsTrue(await cached.TrySetOverrideAsync(RunId, "v2 异常", "ops-oncall"));

        // 请求 2：写穿失效后立即读取真实存储 → 强制回退 V1（无需等 TTL）
        var secondRequest = new ContextRetrievalRequest
        {
            OperationId = "op-cache-wt-2",
            WorkspaceId = "ws-cache-wt",
            CollectionId = "col-cache-wt",
            Metadata = new Dictionary<string, string>
            {
                [CanaryRunIdResolver.RunIdMetadataKey] = RunId
            }
        };
        await runtime.RetrieveAsync(secondRequest, CancellationToken.None);
        Assert.AreEqual(1, stubV2.ExecuteCallCount, "Kill Switch 生效后不得再走 V2。");
        Assert.AreEqual(1, trackingStore.QueryCallCount, "必须立即回退 Legacy（写穿失效不依赖 TTL 过期）。");
    }

    // ---------------------------------------------------------------------------
    // 11. 组合根：AddContextCoreRuntime 注册单一 TTL 缓存装饰器 + 配置绑定
    // ---------------------------------------------------------------------------
    [TestMethod]
    public void AddContextCoreRuntime_DevelopmentProfile_RegistersSingleCachedOverrideStore()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "filesystem",
            ["ContextCoreRuntime:Profile"] = "Development",
            ["CanaryOverrideCache:Ttl"] = "00:00:03"
        });

        var services = new ServiceCollection();
#pragma warning disable CS0618 // AddContextCore(IServiceCollection) 已过时；为与 Program.cs 组合顺序保持一致而保留
        services.AddContextCore();
#pragma warning restore CS0618
        services.AddContextCoreRuntime(config);

        // 必须保持单注册（避免组合测试中的 enumerable 重复）
        var registrations = services
            .Where(s => s.ServiceType == typeof(ICanaryEmergencyOverrideStore))
            .ToList();
        Assert.AreEqual(1, registrations.Count, "ICanaryEmergencyOverrideStore 必须保持单注册（TTL 缓存装饰器）。");

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<ICanaryEmergencyOverrideStore>();
        Assert.IsInstanceOfType<CachedCanaryEmergencyOverrideStore>(resolved,
            "组合根必须用 TTL 缓存装饰器包装内层实现。");

        // 配置绑定：CanaryOverrideCache:Ttl
        var options = provider.GetRequiredService<CanaryOverrideCacheOptions>();
        Assert.AreEqual(TimeSpan.FromSeconds(3), options.Ttl, "CanaryOverrideCache:Ttl 必须绑定到缓存选项。");

        // 装饰器可正常代理到内层（InMemory 默认实现，无覆盖 → null）
        Assert.IsNull(resolved.GetActiveAsync("run-unknown").AsTask().GetAwaiter().GetResult());
    }

    // ---------------------------------------------------------------------------
    // 辅助
    // ---------------------------------------------------------------------------

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static CanaryEmergencyOverride MakeOverride(string runId) => new()
    {
        RunId = runId,
        Reason = "v2 异常（测试）",
        OperatorName = "ops-oncall",
        CreatedAt = CanaryAcceptanceHelpers.BaseTime
    };

    /// <summary>可推进时钟：测试 TTL 过期行为。</summary>
    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public MutableTimeProvider(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    /// <summary>
    /// 脚本化 Override Store：可配置 GetActive 结果 / 抛异常 / Set/Clear 返回值，并统计调用次数。
    /// </summary>
    private sealed class ScriptedOverrideStore : ICanaryEmergencyOverrideStore
    {
        public CanaryEmergencyOverride? ActiveResult { get; set; }
        public Exception? GetActiveException { get; set; }
        public bool SetResult { get; set; } = true;
        public bool ClearResult { get; set; } = true;

        public int GetActiveCallCount { get; private set; }
        public int GetActiveOverridesCallCount { get; private set; }
        public int SetCallCount { get; private set; }
        public int ClearCallCount { get; private set; }

        public ValueTask<CanaryEmergencyOverride?> GetActiveAsync(
            string runId, CancellationToken cancellationToken = default)
        {
            GetActiveCallCount++;
            if (GetActiveException is not null)
            {
                throw GetActiveException;
            }
            return ValueTask.FromResult(ActiveResult);
        }

        public ValueTask<IReadOnlyList<CanaryEmergencyOverride>> GetActiveOverridesAsync(
            CancellationToken cancellationToken = default)
        {
            GetActiveOverridesCallCount++;
            var list = ActiveResult is null
                ? Array.Empty<CanaryEmergencyOverride>()
                : new[] { ActiveResult };
            return ValueTask.FromResult<IReadOnlyList<CanaryEmergencyOverride>>(list);
        }

        public ValueTask<bool> TrySetOverrideAsync(
            string runId, string reason, string operatorName, CancellationToken cancellationToken = default)
        {
            SetCallCount++;
            return ValueTask.FromResult(SetResult);
        }

        public ValueTask<bool> TryClearOverrideAsync(
            string runId, string operatorName, CancellationToken cancellationToken = default)
        {
            ClearCallCount++;
            return ValueTask.FromResult(ClearResult);
        }
    }

    /// <summary>GetActiveAsync 抛异常的 Override Store（模拟 Kill Switch 存储故障）。</summary>
    private sealed class ThrowingGetActiveOverrideStore : ICanaryEmergencyOverrideStore
    {
        public int GetActiveCallCount { get; private set; }

        public ValueTask<CanaryEmergencyOverride?> GetActiveAsync(
            string runId, CancellationToken cancellationToken = default)
        {
            GetActiveCallCount++;
            throw new InvalidOperationException("Kill Switch store down（P0-13 测试注入）");
        }

        public ValueTask<IReadOnlyList<CanaryEmergencyOverride>> GetActiveOverridesAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<CanaryEmergencyOverride>>(Array.Empty<CanaryEmergencyOverride>());

        public ValueTask<bool> TrySetOverrideAsync(
            string runId, string reason, string operatorName, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<bool> TryClearOverrideAsync(
            string runId, string operatorName, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }
}
