using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;

namespace ContextCore.Tests;

/// <summary>
/// R23-5：DefaultAgentRuntimeRegistry 实现测试。
///
/// 覆盖：
///   1. RegisterAsync null 抛异常
///   2. RegisterAsync 新增返回 true + Count 增长
///   3. RegisterAsync 覆盖返回 false + Count 不变
///   4. UnregisterAsync 存在/不存在
///   5. Resolve 存在/不存在
///   6. GetAll 按 RuntimeKind 排序
///   7. Count 属性
///   8. 三种 adapter（GenericTool/Codex/Claude）共存
///   9. CancellationToken 传递
/// </summary>
[TestClass]
[TestCategory("R23")]
public sealed class DefaultAgentRuntimeRegistryTests
{
    // =========================================================================
    // 1. RegisterAsync
    // =========================================================================

    [TestMethod]
    public async Task RegisterAsync_NullRuntime_Throws()
    {
        var registry = new DefaultAgentRuntimeRegistry();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => registry.RegisterAsync(null!));
    }

    [TestMethod]
    public async Task RegisterAsync_NewRuntime_ReturnsTrueAndIncrementsCount()
    {
        var registry = new DefaultAgentRuntimeRegistry();

        var result = await registry.RegisterAsync(new GenericToolAgentAdapter());

        Assert.IsTrue(result);
        Assert.AreEqual(1, registry.Count);
    }

    [TestMethod]
    public async Task RegisterAsync_SameKind_OverwritesAndReturnsFalse()
    {
        var registry = new DefaultAgentRuntimeRegistry();
        var first = new GenericToolAgentAdapter();
        var second = new GenericToolAgentAdapter();

        await registry.RegisterAsync(first);
        var result = await registry.RegisterAsync(second);

        Assert.IsFalse(result);
        Assert.AreEqual(1, registry.Count);
        // 应该是 second（后注册覆盖）
        Assert.AreSame(second, registry.Resolve(AgentRuntimeKind.GenericTool));
    }

    [TestMethod]
    public async Task RegisterAsync_DifferentKinds_AllRegistered()
    {
        var registry = new DefaultAgentRuntimeRegistry();

        await registry.RegisterAsync(new GenericToolAgentAdapter());
        await registry.RegisterAsync(new CodexAgentRuntimeAdapter());
        await registry.RegisterAsync(new ClaudeCodeAgentRuntimeAdapter());

        Assert.AreEqual(3, registry.Count);
    }

    // =========================================================================
    // 2. UnregisterAsync
    // =========================================================================

    [TestMethod]
    public async Task UnregisterAsync_Existing_ReturnsTrue()
    {
        var registry = new DefaultAgentRuntimeRegistry();
        await registry.RegisterAsync(new GenericToolAgentAdapter());

        var result = await registry.UnregisterAsync(AgentRuntimeKind.GenericTool);

        Assert.IsTrue(result);
        Assert.AreEqual(0, registry.Count);
        Assert.IsNull(registry.Resolve(AgentRuntimeKind.GenericTool));
    }

    [TestMethod]
    public async Task UnregisterAsync_Nonexistent_ReturnsFalse()
    {
        var registry = new DefaultAgentRuntimeRegistry();

        var result = await registry.UnregisterAsync(AgentRuntimeKind.Codex);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task UnregisterAsync_DoesNotAffectOthers()
    {
        var registry = new DefaultAgentRuntimeRegistry();
        await registry.RegisterAsync(new GenericToolAgentAdapter());
        await registry.RegisterAsync(new CodexAgentRuntimeAdapter());

        await registry.UnregisterAsync(AgentRuntimeKind.GenericTool);

        Assert.AreEqual(1, registry.Count);
        Assert.IsNotNull(registry.Resolve(AgentRuntimeKind.Codex));
    }

    // =========================================================================
    // 3. Resolve
    // =========================================================================

    [TestMethod]
    public void Resolve_EmptyRegistry_ReturnsNull()
    {
        var registry = new DefaultAgentRuntimeRegistry();
        Assert.IsNull(registry.Resolve(AgentRuntimeKind.GenericTool));
    }

    [TestMethod]
    public async Task Resolve_AfterRegister_ReturnsRuntime()
    {
        var registry = new DefaultAgentRuntimeRegistry();
        var adapter = new CodexAgentRuntimeAdapter();
        await registry.RegisterAsync(adapter);

        var resolved = registry.Resolve(AgentRuntimeKind.Codex);

        Assert.AreSame(adapter, resolved);
        Assert.AreEqual("codex-v1", resolved!.RuntimeId);
    }

    // =========================================================================
    // 4. GetAll
    // =========================================================================

    [TestMethod]
    public void GetAll_EmptyRegistry_ReturnsEmptyList()
    {
        var registry = new DefaultAgentRuntimeRegistry();
        var all = registry.GetAll();
        Assert.AreEqual(0, all.Count);
    }

    [TestMethod]
    public async Task GetAll_ReturnsAllRegisteredSortedByKind()
    {
        var registry = new DefaultAgentRuntimeRegistry();
        // 按 byte 值排序：Unknown(0) < GenericTool(1) < Codex(2) < ClaudeCode(3) < Custom(4)
        // 故意按反序注册，验证 GetAll 排序
        var claude = new ClaudeCodeAgentRuntimeAdapter();
        var codex = new CodexAgentRuntimeAdapter();
        var generic = new GenericToolAgentAdapter();
        await registry.RegisterAsync(claude);
        await registry.RegisterAsync(codex);
        await registry.RegisterAsync(generic);

        var all = registry.GetAll();

        Assert.AreEqual(3, all.Count);
        // 按 RuntimeKind byte 值升序
        Assert.AreEqual(AgentRuntimeKind.GenericTool, all[0].RuntimeKind);
        Assert.AreEqual(AgentRuntimeKind.Codex, all[1].RuntimeKind);
        Assert.AreEqual(AgentRuntimeKind.ClaudeCode, all[2].RuntimeKind);
    }

    // =========================================================================
    // 5. Count
    // =========================================================================

    [TestMethod]
    public void Count_StartsAtZero()
    {
        var registry = new DefaultAgentRuntimeRegistry();
        Assert.AreEqual(0, registry.Count);
    }

    // =========================================================================
    // 6. 三种 adapter 共存
    // =========================================================================

    [TestMethod]
    public async Task ThreeAdapters_CoexistInRegistry()
    {
        var registry = new DefaultAgentRuntimeRegistry();
        var generic = new GenericToolAgentAdapter();
        var codex = new CodexAgentRuntimeAdapter();
        var claude = new ClaudeCodeAgentRuntimeAdapter();

        await registry.RegisterAsync(generic);
        await registry.RegisterAsync(codex);
        await registry.RegisterAsync(claude);

        Assert.AreSame(generic, registry.Resolve(AgentRuntimeKind.GenericTool));
        Assert.AreSame(codex, registry.Resolve(AgentRuntimeKind.Codex));
        Assert.AreSame(claude, registry.Resolve(AgentRuntimeKind.ClaudeCode));
    }

    [TestMethod]
    public async Task Registry_DoesNotHoldSessionState()
    {
        // 验证：注册 adapter 后，session 状态仍由 adapter 自身管理
        var registry = new DefaultAgentRuntimeRegistry();
        var adapter = new GenericToolAgentAdapter();
        await registry.RegisterAsync(adapter);

        var resolved = registry.Resolve(AgentRuntimeKind.GenericTool)!;
        var sessionId = await resolved.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });

        // 验证：session 状态在原 adapter 中（通过强转访问）
        var concreteAdapter = (GenericToolAgentAdapter)resolved;
        Assert.AreEqual(1, concreteAdapter.SessionCount);
    }

    // =========================================================================
    // 7. CancellationToken
    // =========================================================================

    [TestMethod]
    public async Task RegisterAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var registry = new DefaultAgentRuntimeRegistry();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => registry.RegisterAsync(new GenericToolAgentAdapter(), cts.Token));
    }

    [TestMethod]
    public async Task UnregisterAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var registry = new DefaultAgentRuntimeRegistry();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => registry.UnregisterAsync(AgentRuntimeKind.GenericTool, cts.Token));
    }
}
