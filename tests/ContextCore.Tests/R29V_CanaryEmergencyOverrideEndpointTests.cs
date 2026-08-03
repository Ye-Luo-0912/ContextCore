using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Service.Endpoints;
using ContextCore.Storage.InMemory.Stores;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Tests;

// ===========================================================================
// Canary Emergency Kill Switch API —— 集群级紧急覆盖操作面验收测试
//
// 覆盖范围（直接调用 CanaryEmergencyEndpoints 的 internal handler + 执行 IResult）：
//   1. 触发（kill）：未注册存储 503 / 空 runId 400 / 空 reason 400 /
//      设置成功 200 并返回覆盖记录 / 已存在活跃覆盖 409；
//   2. 清除（clear）：未注册存储 503 / 无活跃覆盖 404 / 清除成功 200 并返回
//      带清除字段的覆盖记录 / 并发清除竞争 409；
//   3. 查询（list）：未注册存储 503 / 无活跃覆盖空列表 / 返回全部活跃覆盖；
//   4. 生命周期：触发 → 清除 → 再次触发成功（持久化覆盖可重复使用）。
//
// 存储语义（活跃覆盖唯一性 / CAS 清除）由 R29H_CanaryEmergencyOverrideTests 覆盖；
// 本文件聚焦端点处理器的状态码与响应形状，不依赖真实数据库。
// ===========================================================================

[TestClass]
[TestCategory("Storage")]
[TestCategory("R29")]
public sealed class R29V_CanaryEmergencyOverrideEndpointTests
{
    private const string RunId = "run-canary-emergency-001";

    // 服务器响应为 camelCase（Results.Ok 使用 web 默认序列化选项）。
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    // =========================================================================
    // 触发（kill）
    // =========================================================================

    [TestMethod]
    public async Task Kill_NoStore_Returns503()
    {
        var (status, body) = await ExecuteAsync(await CanaryEmergencyEndpoints.KillAsync(
            store: null, RunId, new KillCanaryOverrideRequest { Reason = "P95 恶化" }, Http(), CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, status);
        Assert.IsTrue(body.Contains("未注册", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Kill_BlankRunId_Returns400()
    {
        var (status, _) = await ExecuteAsync(await CanaryEmergencyEndpoints.KillAsync(
            new InMemoryCanaryEmergencyOverrideStore(), "  ", new KillCanaryOverrideRequest { Reason = "P95 恶化" }, Http(), CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status400BadRequest, status);
    }

    [TestMethod]
    public async Task Kill_BlankReason_Returns400()
    {
        var (status, _) = await ExecuteAsync(await CanaryEmergencyEndpoints.KillAsync(
            new InMemoryCanaryEmergencyOverrideStore(), RunId, new KillCanaryOverrideRequest { Reason = "  " }, Http(), CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status400BadRequest, status);
    }

    [TestMethod]
    public async Task Kill_SetsOverride_Returns200WithRecord()
    {
        var store = new InMemoryCanaryEmergencyOverrideStore();

        var (status, body) = await ExecuteAsync(await CanaryEmergencyEndpoints.KillAsync(
            store, RunId, new KillCanaryOverrideRequest { Reason = "v2 P95 恶化", OperatorName = "ops-oncall" }, Http(), CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status200OK, status);
        var response = JsonSerializer.Deserialize<CanaryEmergencyOverrideResponse>(body, JsonWeb);
        Assert.IsNotNull(response);
        Assert.AreEqual(RunId, response!.RunId);
        Assert.AreEqual("v2 P95 恶化", response.Reason);
        Assert.AreEqual("ops-oncall", response.OperatorName);
        Assert.IsTrue(response.IsActive);
        Assert.IsNull(response.ClearedAt);

        var active = await store.GetActiveAsync(RunId);
        Assert.IsNotNull(active, "触发成功后存储中应存在活跃覆盖。");
    }

    [TestMethod]
    public async Task Kill_AlreadyActive_Returns409()
    {
        var store = new InMemoryCanaryEmergencyOverrideStore();
        await store.TrySetOverrideAsync(RunId, "P95 恶化", "ops-oncall");

        var (status, _) = await ExecuteAsync(await CanaryEmergencyEndpoints.KillAsync(
            store, RunId, new KillCanaryOverrideRequest { Reason = "再次触发" }, Http(), CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status409Conflict, status, "已存在活跃覆盖时重复触发应返回 409。");
    }

    // =========================================================================
    // 清除（clear）
    // =========================================================================

    [TestMethod]
    public async Task Clear_NoStore_Returns503()
    {
        var (status, _) = await ExecuteAsync(await CanaryEmergencyEndpoints.ClearAsync(
            store: null, RunId, "ops-oncall", Http(), CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, status);
    }

    [TestMethod]
    public async Task Clear_NoActiveOverride_Returns404()
    {
        var (status, _) = await ExecuteAsync(await CanaryEmergencyEndpoints.ClearAsync(
            new InMemoryCanaryEmergencyOverrideStore(), RunId, "ops-oncall", Http(), CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status404NotFound, status);
    }

    [TestMethod]
    public async Task Clear_ActiveOverride_Returns200WithClearedFields()
    {
        var store = new InMemoryCanaryEmergencyOverrideStore();
        await store.TrySetOverrideAsync(RunId, "P95 恶化", "ops-oncall");

        var (status, body) = await ExecuteAsync(await CanaryEmergencyEndpoints.ClearAsync(
            store, RunId, "ops-oncall", Http(), CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status200OK, status);
        var response = JsonSerializer.Deserialize<CanaryEmergencyOverrideResponse>(body, JsonWeb);
        Assert.IsNotNull(response);
        Assert.AreEqual(RunId, response!.RunId);
        Assert.IsFalse(response.IsActive, "清除后 IsActive 应为 false。");
        Assert.AreEqual("ops-oncall", response.ClearedBy);
        Assert.IsNotNull(response.ClearedAt);

        var active = await store.GetActiveAsync(RunId);
        Assert.IsNull(active, "清除后存储中不应再有活跃覆盖。");
    }

    [TestMethod]
    public async Task Clear_ConcurrentClear_Returns409()
    {
        var (status, _) = await ExecuteAsync(await CanaryEmergencyEndpoints.ClearAsync(
            new FailingClearStore(), RunId, "ops-oncall", Http(), CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status409Conflict, status, "清除竞争失败（已被并发清除）应返回 409。");
    }

    // =========================================================================
    // 查询（list）
    // =========================================================================

    [TestMethod]
    public async Task List_NoStore_Returns503()
    {
        var (status, _) = await ExecuteAsync(await CanaryEmergencyEndpoints.ListAsync(
            store: null, Http(), CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, status);
    }

    [TestMethod]
    public async Task List_NoActiveOverrides_ReturnsEmptyList()
    {
        var (status, body) = await ExecuteAsync(await CanaryEmergencyEndpoints.ListAsync(
            new InMemoryCanaryEmergencyOverrideStore(), Http(), CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status200OK, status);
        var response = JsonSerializer.Deserialize<CanaryEmergencyOverrideListResponse>(body, JsonWeb);
        Assert.IsNotNull(response);
        Assert.AreEqual(0, response!.Count);
        Assert.AreEqual(0, response.Overrides.Count);
    }

    [TestMethod]
    public async Task List_ReturnsActiveOverrides()
    {
        var store = new InMemoryCanaryEmergencyOverrideStore();
        await store.TrySetOverrideAsync("run-a", "原因 A", "ops-1");
        await store.TrySetOverrideAsync("run-b", "原因 B", "ops-2");

        var (status, body) = await ExecuteAsync(await CanaryEmergencyEndpoints.ListAsync(
            store, Http(), CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status200OK, status);
        var response = JsonSerializer.Deserialize<CanaryEmergencyOverrideListResponse>(body, JsonWeb);
        Assert.IsNotNull(response);
        Assert.AreEqual(2, response!.Count);
        Assert.AreEqual(2, response.Overrides.Count);
        Assert.IsTrue(response.Overrides.All(o => o.IsActive));
    }

    // =========================================================================
    // 生命周期
    // =========================================================================

    [TestMethod]
    public async Task Kill_Clear_KillAgain_AllowsNewOverride()
    {
        var store = new InMemoryCanaryEmergencyOverrideStore();

        await ExecuteAsync(await CanaryEmergencyEndpoints.KillAsync(
            store, RunId, new KillCanaryOverrideRequest { Reason = "首次触发" }, Http(), CancellationToken.None));
        await ExecuteAsync(await CanaryEmergencyEndpoints.ClearAsync(
            store, RunId, "ops-oncall", Http(), CancellationToken.None));

        var (status, body) = await ExecuteAsync(await CanaryEmergencyEndpoints.KillAsync(
            store, RunId, new KillCanaryOverrideRequest { Reason = "再次触发" }, Http(), CancellationToken.None));

        Assert.AreEqual(StatusCodes.Status200OK, status, "清除后再次触发应成功（覆盖生命周期可重复）。");
        var response = JsonSerializer.Deserialize<CanaryEmergencyOverrideResponse>(body, JsonWeb);
        Assert.AreEqual("再次触发", response!.Reason);
        Assert.IsTrue(response.IsActive);
    }

    // =========================================================================
    // 辅助
    // =========================================================================

    private static DefaultHttpContext Http()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.TraceIdentifier = "test-trace";
        httpContext.Response.Body = new MemoryStream();
        // .NET 10 的 Ok<T>/JsonHttpResult<T>.ExecuteAsync 需要从 RequestServices 解析 ILoggerFactory。
        httpContext.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        return httpContext;
    }

    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result)
    {
        var httpContext = Http();
        await result.ExecuteAsync(httpContext);
        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body);
        var body = await reader.ReadToEndAsync();
        return ((int)httpContext.Response.StatusCode, body);
    }

    /// <summary>清除必然失败的 fake：模拟并发清除竞争（Get 返回活跃覆盖但 TryClear 返回 false）。</summary>
    private sealed class FailingClearStore : ICanaryEmergencyOverrideStore
    {
        public ValueTask<CanaryEmergencyOverride?> GetActiveAsync(string runId, CancellationToken cancellationToken = default)
            => new(new CanaryEmergencyOverride
            {
                RunId = runId,
                Reason = "P95 恶化",
                OperatorName = "ops-oncall",
                CreatedAt = DateTimeOffset.UtcNow
            });

        public ValueTask<IReadOnlyList<CanaryEmergencyOverride>> GetActiveOverridesAsync(CancellationToken cancellationToken = default)
            => new(Array.Empty<CanaryEmergencyOverride>());

        public ValueTask<bool> TrySetOverrideAsync(string runId, string reason, string operatorName, CancellationToken cancellationToken = default)
            => new(true);

        public ValueTask<bool> TryClearOverrideAsync(string runId, string operatorName, CancellationToken cancellationToken = default)
            => new(false);
    }
}
