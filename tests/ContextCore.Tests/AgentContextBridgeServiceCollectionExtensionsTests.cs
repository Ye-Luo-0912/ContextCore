using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Agent;
using ContextCore.Service.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Tests;

/// <summary>
/// DI 扩展方法测试。
///
/// 覆盖：
/// 1. AddAgentContextBridge 注册 IAgentContextBridge
/// 2. AddAgentContextBridge 未注册 IContextPackageBuilder 抛异常
/// 3. AddInMemoryAgentTaskStateStore 注册 IAgentTaskStateStore
/// 4. AddAgentRuntimeAndBridgeDefaults 一键注册全部
/// 5. null services 抛异常
/// 6. AddAgentContextBridge 端到端可调用（注入 stub IContextPackageBuilder）
/// </summary>
[TestClass]
[TestCategory("R24")]
public sealed class AgentContextBridgeServiceCollectionExtensionsTests
{
    // =========================================================================
    // 1. AddAgentContextBridge
    // =========================================================================

    [TestMethod]
    public void AddAgentContextBridge_WithPackageBuilder_RegistersBridge()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IContextPackageBuilder, StubPackageBuilder>();
        services.AddAgentContextBridge();

        var sp = services.BuildServiceProvider();
        var bridge = sp.GetService<IAgentContextBridge>();

        Assert.IsNotNull(bridge);
        Assert.IsInstanceOfType<DefaultAgentContextBridge>(bridge);
    }

    [TestMethod]
    public void AddAgentContextBridge_WithoutPackageBuilder_ThrowsOnResolve()
    {
        var services = new ServiceCollection();
        services.AddAgentContextBridge();

        var sp = services.BuildServiceProvider();
        Assert.ThrowsException<InvalidOperationException>(
            () => sp.GetService<IAgentContextBridge>());
    }

    // =========================================================================
    // 2. AddInMemoryAgentTaskStateStore
    // =========================================================================

    [TestMethod]
    public void AddInMemoryAgentTaskStateStore_RegistersStore()
    {
        var services = new ServiceCollection();
        services.AddInMemoryAgentTaskStateStore();

        var sp = services.BuildServiceProvider();
        var store = sp.GetService<IAgentTaskStateStore>();

        Assert.IsNotNull(store);
        Assert.IsInstanceOfType<InMemoryAgentTaskStateStore>(store);
    }

    // =========================================================================
    // 3. AddAgentRuntimeAndBridgeDefaults
    // =========================================================================

    [TestMethod]
    public void AddAgentRuntimeAndBridgeDefaults_WithPackageBuilder_RegistersAll()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IContextPackageBuilder, StubPackageBuilder>();
        services.AddAgentRuntimeAndBridgeDefaults();

        var sp = services.BuildServiceProvider();
        Assert.IsNotNull(sp.GetService<IAgentRuntimeRegistry>());
        Assert.IsNotNull(sp.GetService<IAgentRuntime>());
        Assert.IsNotNull(sp.GetService<IAgentWorkspaceContextProvider>());
        Assert.IsNotNull(sp.GetService<IAgentContextDeltaCalculator>());
        Assert.IsNotNull(sp.GetService<IAgentCheckpointStore>());
        Assert.IsNotNull(sp.GetService<IAgentContextBridge>());
        Assert.IsNotNull(sp.GetService<IAgentTaskStateStore>());
    }

    [TestMethod]
    public void AddAgentRuntimeAndBridgeDefaults_WithoutPackageBuilder_ThrowsOnBridgeResolve()
    {
        var services = new ServiceCollection();
        services.AddAgentRuntimeAndBridgeDefaults();

        var sp = services.BuildServiceProvider();
        // 其他服务可正常解析
        Assert.IsNotNull(sp.GetService<IAgentRuntimeRegistry>());
        Assert.IsNotNull(sp.GetService<IAgentRuntime>());
        // bridge 解析抛异常
        Assert.ThrowsException<InvalidOperationException>(
            () => sp.GetService<IAgentContextBridge>());
    }

    // =========================================================================
    // 4. null services
    // =========================================================================

    [TestMethod]
    public void R24Extensions_NullServices_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            ((IServiceCollection)null!).AddAgentContextBridge());
        Assert.ThrowsException<ArgumentNullException>(() =>
            ((IServiceCollection)null!).AddInMemoryAgentTaskStateStore());
        Assert.ThrowsException<ArgumentNullException>(() =>
            ((IServiceCollection)null!).AddAgentRuntimeAndBridgeDefaults());
    }

    // =========================================================================
    // 5. 端到端：Bridge 通过 DI 解析并调用
    // =========================================================================

    [TestMethod]
    public async Task AddAgentContextBridge_EndToEnd_BridgeWorks()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IContextPackageBuilder, StubPackageBuilder>();
        services.AddAgentContextBridge();

        var sp = services.BuildServiceProvider();
        var bridge = sp.GetRequiredService<IAgentContextBridge>();

        var session = new AgentSessionId
        {
            Value = "session-test",
            RuntimeKind = AgentRuntimeKind.GenericTool,
            WorkspaceId = "ws-1",
            CollectionId = "col-1",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var request = new AgentContextBridgeRequest
        {
            Session = session,
            TokenBudget = 1000,
            QueryText = "test query"
        };

        var response = await bridge.BuildSnapshotAsync(request);

        Assert.IsNotNull(response.Snapshot);
        Assert.IsFalse(string.IsNullOrEmpty(response.Snapshot.SnapshotId));
        Assert.IsNotNull(response.BuildResult);
    }

    // ===== Stub IContextPackageBuilder =====

    private sealed class StubPackageBuilder : IContextPackageBuilder
    {
        public Task<ContextPackage> BuildAsync(
            ContextPackageRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Use BuildDetailedAsync in tests");
        }

        public Task<ContextPackageBuildResult> BuildDetailedAsync(
            ContextPackageRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ContextPackageBuildResult
            {
                BuildId = "stub-build",
                Package = new ContextPackage
                {
                    PackageId = "stub-pkg",
                    WorkspaceId = request.WorkspaceId,
                    CollectionId = request.CollectionId,
                    Sections = new[]
                    {
                        new ContextPackageSection
                        {
                            Name = "stub-section",
                            Content = "stub content",
                            Priority = 1,
                            SourceRefs = new[] { "stub-src" }
                        }
                    },
                    EstimatedTokens = 50,
                    CreatedAt = DateTimeOffset.UtcNow
                },
                SelectedItems = new[]
                {
                    new ContextPackageDecision { ItemId = "item-1" }
                }
            });
        }
    }
}
