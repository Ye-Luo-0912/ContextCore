using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;
using ContextCore.Service.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Tests;

/// <summary>
/// AgentRuntimeServiceCollectionExtensions DI 测试。
///
/// 覆盖：
/// 1. AddAgentRuntimeRegistry 注册 IAgentRuntimeRegistry
/// 2. AddGenericToolAgentRuntime 注册 IAgentRuntime + GenericToolAgentAdapter
/// 3. AddCodexAgentRuntime 注册 IAgentRuntime + CodexAgentRuntimeAdapter
/// 4. AddClaudeCodeAgentRuntime 注册 IAgentRuntime + ClaudeCodeAgentRuntimeAdapter
/// 5. AddAgentWorkspaceContextProvider 注册 IAgentWorkspaceContextProvider
/// 6. AddAgentWorkspaceContextProvider 未注册 runtime 抛异常
/// 7. AddAgentContextDeltaCalculator 注册 IAgentContextDeltaCalculator
/// 8. AddInMemoryAgentCheckpointStore 注册 IAgentCheckpointStore
/// 9. AddAgentRuntimeDefaults 一键注册全部
/// 10. null services 抛异常
/// 11. 多次注册不冲突
/// </summary>
[TestClass]
[TestCategory("R23")]
public sealed class AgentRuntimeServiceCollectionExtensionsTests
{
    // =========================================================================
    // 1. AddAgentRuntimeRegistry
    // =========================================================================

    [TestMethod]
    public void AddAgentRuntimeRegistry_RegistersRegistry()
    {
        var services = new ServiceCollection();
        services.AddAgentRuntimeRegistry();

        var sp = services.BuildServiceProvider();
        var registry = sp.GetService<IAgentRuntimeRegistry>();
        Assert.IsNotNull(registry);
        Assert.IsInstanceOfType<DefaultAgentRuntimeRegistry>(registry);
    }

    // =========================================================================
    // 2. AddGenericToolAgentRuntime
    // =========================================================================

    [TestMethod]
    public void AddGenericToolAgentRuntime_RegistersRuntimeAndAdapter()
    {
        var services = new ServiceCollection();
        services.AddGenericToolAgentRuntime();

        var sp = services.BuildServiceProvider();
        var runtime = sp.GetService<IAgentRuntime>();
        var adapter = sp.GetService<GenericToolAgentAdapter>();

        Assert.IsNotNull(runtime);
        Assert.IsNotNull(adapter);
        Assert.AreSame(runtime, adapter);
        Assert.AreEqual(AgentRuntimeKind.GenericTool, runtime!.RuntimeKind);
        Assert.AreEqual("generic-v1", runtime.RuntimeId);
    }

    // =========================================================================
    // 3. AddCodexAgentRuntime
    // =========================================================================

    [TestMethod]
    public void AddCodexAgentRuntime_RegistersRuntimeAndAdapter()
    {
        var services = new ServiceCollection();
        services.AddCodexAgentRuntime();

        var sp = services.BuildServiceProvider();
        var runtime = sp.GetService<IAgentRuntime>();
        var adapter = sp.GetService<CodexAgentRuntimeAdapter>();

        Assert.IsNotNull(runtime);
        Assert.IsNotNull(adapter);
        Assert.AreSame(runtime, adapter);
        Assert.AreEqual(AgentRuntimeKind.Codex, runtime!.RuntimeKind);
    }

    // =========================================================================
    // 4. AddClaudeCodeAgentRuntime
    // =========================================================================

    [TestMethod]
    public void AddClaudeCodeAgentRuntime_RegistersRuntimeAndAdapter()
    {
        var services = new ServiceCollection();
        services.AddClaudeCodeAgentRuntime();

        var sp = services.BuildServiceProvider();
        var runtime = sp.GetService<IAgentRuntime>();
        var adapter = sp.GetService<ClaudeCodeAgentRuntimeAdapter>();

        Assert.IsNotNull(runtime);
        Assert.IsNotNull(adapter);
        Assert.AreSame(runtime, adapter);
        Assert.AreEqual(AgentRuntimeKind.ClaudeCode, runtime!.RuntimeKind);
    }

    // =========================================================================
    // 5. AddAgentWorkspaceContextProvider
    // =========================================================================

    [TestMethod]
    public void AddAgentWorkspaceContextProvider_RegistersProvider()
    {
        var services = new ServiceCollection();
        services.AddGenericToolAgentRuntime();
        services.AddAgentWorkspaceContextProvider();

        var sp = services.BuildServiceProvider();
        var provider = sp.GetService<IAgentWorkspaceContextProvider>();

        Assert.IsNotNull(provider);
        Assert.IsInstanceOfType<DefaultAgentWorkspaceContextProvider>(provider);
    }

    // =========================================================================
    // 6. AddAgentWorkspaceContextProvider 未注册 runtime
    // =========================================================================

    [TestMethod]
    public void AddAgentWorkspaceContextProvider_WithoutRuntime_ThrowsOnResolve()
    {
        var services = new ServiceCollection();
        services.AddAgentWorkspaceContextProvider();

        var sp = services.BuildServiceProvider();
        Assert.ThrowsException<InvalidOperationException>(
            () => sp.GetService<IAgentWorkspaceContextProvider>());
    }

    // =========================================================================
    // 7. AddAgentContextDeltaCalculator
    // =========================================================================

    [TestMethod]
    public void AddAgentContextDeltaCalculator_RegistersCalculator()
    {
        var services = new ServiceCollection();
        services.AddAgentContextDeltaCalculator();

        var sp = services.BuildServiceProvider();
        var calc = sp.GetService<IAgentContextDeltaCalculator>();

        Assert.IsNotNull(calc);
        Assert.IsInstanceOfType<DefaultAgentContextDeltaCalculator>(calc);
    }

    // =========================================================================
    // 8. AddInMemoryAgentCheckpointStore
    // =========================================================================

    [TestMethod]
    public void AddInMemoryAgentCheckpointStore_RegistersStore()
    {
        var services = new ServiceCollection();
        services.AddInMemoryAgentCheckpointStore();

        var sp = services.BuildServiceProvider();
        var store = sp.GetService<IAgentCheckpointStore>();

        Assert.IsNotNull(store);
        Assert.IsInstanceOfType<InMemoryAgentCheckpointStore>(store);
    }

    // =========================================================================
    // 9. AddAgentRuntimeDefaults
    // =========================================================================

    [TestMethod]
    public void AddAgentRuntimeDefaults_RegistersAllServices()
    {
        var services = new ServiceCollection();
        services.AddAgentRuntimeDefaults();

        var sp = services.BuildServiceProvider();
        Assert.IsNotNull(sp.GetService<IAgentRuntimeRegistry>());
        Assert.IsNotNull(sp.GetService<IAgentRuntime>());
        Assert.IsNotNull(sp.GetService<GenericToolAgentAdapter>());
        Assert.IsNotNull(sp.GetService<IAgentWorkspaceContextProvider>());
        Assert.IsNotNull(sp.GetService<IAgentContextDeltaCalculator>());
        Assert.IsNotNull(sp.GetService<IAgentCheckpointStore>());
    }

    [TestMethod]
    public async Task AddAgentRuntimeDefaults_EndToEndWorks()
    {
        var services = new ServiceCollection();
        services.AddAgentRuntimeDefaults();
        var sp = services.BuildServiceProvider();

        var runtime = sp.GetRequiredService<IAgentRuntime>();
        var provider = sp.GetRequiredService<IAgentWorkspaceContextProvider>();

        var sessionId = await runtime.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        await provider.InjectAsync(sessionId, new AgentContextInjection
        {
            InjectionId = "inj-1",
            FreeText = "di test",
            InjectedAt = DateTimeOffset.UtcNow
        });

        var snap = await provider.GetContextSnapshotAsync(sessionId, 10000);
        Assert.IsNotNull(snap);
        Assert.IsFalse(string.IsNullOrEmpty(snap.ContentJson));
    }

    // =========================================================================
    // 10. null services
    // =========================================================================

    [TestMethod]
    public void AllExtensions_NullServices_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            ((IServiceCollection)null!).AddAgentRuntimeRegistry());
        Assert.ThrowsException<ArgumentNullException>(() =>
            ((IServiceCollection)null!).AddGenericToolAgentRuntime());
        Assert.ThrowsException<ArgumentNullException>(() =>
            ((IServiceCollection)null!).AddCodexAgentRuntime());
        Assert.ThrowsException<ArgumentNullException>(() =>
            ((IServiceCollection)null!).AddClaudeCodeAgentRuntime());
        Assert.ThrowsException<ArgumentNullException>(() =>
            ((IServiceCollection)null!).AddAgentWorkspaceContextProvider());
        Assert.ThrowsException<ArgumentNullException>(() =>
            ((IServiceCollection)null!).AddAgentContextDeltaCalculator());
        Assert.ThrowsException<ArgumentNullException>(() =>
            ((IServiceCollection)null!).AddInMemoryAgentCheckpointStore());
        Assert.ThrowsException<ArgumentNullException>(() =>
            ((IServiceCollection)null!).AddAgentRuntimeDefaults());
    }

    // =========================================================================
    // 11. 多次注册不冲突
    // =========================================================================

    [TestMethod]
    public void AddAgentRuntimeDefaults_CalledMultipleTimes_NoConflict()
    {
        var services = new ServiceCollection();
        services.AddAgentRuntimeDefaults();
        services.AddAgentRuntimeDefaults();

        var sp = services.BuildServiceProvider();
        Assert.IsNotNull(sp.GetService<IAgentRuntime>());
        Assert.IsNotNull(sp.GetService<IAgentRuntimeRegistry>());
    }
}
