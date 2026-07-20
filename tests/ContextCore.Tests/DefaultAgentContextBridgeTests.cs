using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Agent;

namespace ContextCore.Tests;

/// <summary>
/// R24-1：DefaultAgentContextBridge 实现测试。
///
/// 覆盖：
///   1. 构造函数 null packageBuilder 抛异常
///   2. BuildSnapshotAsync null request 抛异常
///   3. BuildSnapshotAsync TokenBudget <= 0 抛异常
///   4. BuildSnapshotAsync 调用 IContextPackageBuilder.BuildDetailedAsync
///   5. BuildSnapshotAsync 映射 Sections（Name/Content/Source/SortOrder）
///   6. BuildSnapshotAsync 映射 DecisionRequestIds（从 SelectedItems.ItemId）
///   7. BuildSnapshotAsync 映射 SnapshotId（agent-bridge-{packageId}）
///   8. BuildSnapshotAsync 映射 ActualTokens（package.EstimatedTokens）
///   9. BuildSnapshotAsync 映射 Metadata（buildId/packageId/counts）
///  10. BuildSnapshotAsync 返回 BuildResult（供审计）
///  11. BuildSnapshotAsync 返回 Duration（> 0）
///  12. CancellationToken 传递
///  13. ContextCore 构建失败 → 异常直接抛出（fail-closed）
///  14. 空 Sections → 空 snapshot.Sections
///  15. 空 SelectedItems → 空 DecisionRequestIds
/// </summary>
[TestClass]
[TestCategory("R24")]
public sealed class DefaultAgentContextBridgeTests
{
    // =========================================================================
    // 1. 构造函数
    // =========================================================================

    [TestMethod]
    public void Constructor_NullPackageBuilder_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(
            () => new DefaultAgentContextBridge(null!));
    }

    // =========================================================================
    // 2. BuildSnapshotAsync — 错误路径
    // =========================================================================

    [TestMethod]
    public async Task BuildSnapshotAsync_NullRequest_Throws()
    {
        var bridge = new DefaultAgentContextBridge(new StubPackageBuilder());
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => bridge.BuildSnapshotAsync(null!));
    }

    [TestMethod]
    public async Task BuildSnapshotAsync_ZeroTokenBudget_Throws()
    {
        var bridge = new DefaultAgentContextBridge(new StubPackageBuilder());
        var request = MakeRequest(tokenBudget: 0);

        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(
            () => bridge.BuildSnapshotAsync(request));
    }

    [TestMethod]
    public async Task BuildSnapshotAsync_NegativeTokenBudget_Throws()
    {
        var bridge = new DefaultAgentContextBridge(new StubPackageBuilder());
        var request = MakeRequest(tokenBudget: -1);

        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(
            () => bridge.BuildSnapshotAsync(request));
    }

    // =========================================================================
    // 3. BuildSnapshotAsync — 调用 IContextPackageBuilder
    // =========================================================================

    [TestMethod]
    public async Task BuildSnapshotAsync_CallsBuildDetailedAsync()
    {
        var builder = new StubPackageBuilder();
        var bridge = new DefaultAgentContextBridge(builder);

        await bridge.BuildSnapshotAsync(MakeRequest());

        Assert.AreEqual(1, builder.BuildDetailedCallCount);
    }

    [TestMethod]
    public async Task BuildSnapshotAsync_PassesCorrectWorkspaceId()
    {
        var builder = new StubPackageBuilder();
        var bridge = new DefaultAgentContextBridge(builder);
        var request = MakeRequest(workspaceId: "ws-custom");

        await bridge.BuildSnapshotAsync(request);

        Assert.AreEqual("ws-custom", builder.LastRequest?.WorkspaceId);
    }

    [TestMethod]
    public async Task BuildSnapshotAsync_PassesQueryText()
    {
        var builder = new StubPackageBuilder();
        var bridge = new DefaultAgentContextBridge(builder);
        var request = MakeRequest(queryText: "find me");

        await bridge.BuildSnapshotAsync(request);

        Assert.AreEqual("find me", builder.LastRequest?.QueryText);
    }

    [TestMethod]
    public async Task BuildSnapshotAsync_PassesTokenBudget()
    {
        var builder = new StubPackageBuilder();
        var bridge = new DefaultAgentContextBridge(builder);
        var request = MakeRequest(tokenBudget: 5000);

        await bridge.BuildSnapshotAsync(request);

        Assert.AreEqual(5000, builder.LastRequest?.TokenBudget);
    }

    // =========================================================================
    // 4. Section 映射
    // =========================================================================

    [TestMethod]
    public async Task BuildSnapshotAsync_MapsSections()
    {
        var builder = new StubPackageBuilder();
        builder.ResultToReturn = MakeBuildResult(sections: new[]
        {
            new ContextPackageSection { Name = "intro", Content = "Hello", Priority = 1, SourceRefs = new[] { "src-1" } },
            new ContextPackageSection { Name = "body", Content = "World", Priority = 2, SourceRefs = new[] { "src-2" } }
        });
        var bridge = new DefaultAgentContextBridge(builder);

        var response = await bridge.BuildSnapshotAsync(MakeRequest());

        Assert.AreEqual(2, response.Snapshot.Sections.Count);
        Assert.AreEqual("intro", response.Snapshot.Sections[0].SectionName);
        Assert.AreEqual("Hello", response.Snapshot.Sections[0].Content);
        Assert.AreEqual("src-1", response.Snapshot.Sections[0].Source);
        Assert.AreEqual(1, response.Snapshot.Sections[0].SortOrder);
        Assert.AreEqual("body", response.Snapshot.Sections[1].SectionName);
    }

    [TestMethod]
    public async Task BuildSnapshotAsync_EmptySections_EmptySnapshotSections()
    {
        var builder = new StubPackageBuilder();
        builder.ResultToReturn = MakeBuildResult(sections: Array.Empty<ContextPackageSection>());
        var bridge = new DefaultAgentContextBridge(builder);

        var response = await bridge.BuildSnapshotAsync(MakeRequest());

        Assert.AreEqual(0, response.Snapshot.Sections.Count);
    }

    // =========================================================================
    // 5. DecisionRequestIds 映射
    // =========================================================================

    [TestMethod]
    public async Task BuildSnapshotAsync_MapsDecisionRequestIds()
    {
        var builder = new StubPackageBuilder();
        builder.ResultToReturn = MakeBuildResult(selectedItems: new[]
        {
            new ContextPackageDecision { ItemId = "item-1" },
            new ContextPackageDecision { ItemId = "item-2" }
        });
        var bridge = new DefaultAgentContextBridge(builder);

        var response = await bridge.BuildSnapshotAsync(MakeRequest());

        CollectionAssert.AreEquivalent(new[] { "item-1", "item-2" }, response.Snapshot.DecisionRequestIds.ToList());
    }

    [TestMethod]
    public async Task BuildSnapshotAsync_EmptySelectedItems_EmptyDecisionIds()
    {
        var builder = new StubPackageBuilder();
        builder.ResultToReturn = MakeBuildResult(selectedItems: Array.Empty<ContextPackageDecision>());
        var bridge = new DefaultAgentContextBridge(builder);

        var response = await bridge.BuildSnapshotAsync(MakeRequest());

        Assert.AreEqual(0, response.Snapshot.DecisionRequestIds.Count);
    }

    [TestMethod]
    public async Task BuildSnapshotAsync_DedupDecisionIds()
    {
        var builder = new StubPackageBuilder();
        builder.ResultToReturn = MakeBuildResult(selectedItems: new[]
        {
            new ContextPackageDecision { ItemId = "item-1" },
            new ContextPackageDecision { ItemId = "item-1" }, // 重复
            new ContextPackageDecision { ItemId = "item-2" }
        });
        var bridge = new DefaultAgentContextBridge(builder);

        var response = await bridge.BuildSnapshotAsync(MakeRequest());

        Assert.AreEqual(2, response.Snapshot.DecisionRequestIds.Count);
    }

    // =========================================================================
    // 6. SnapshotId / ActualTokens / Metadata 映射
    // =========================================================================

    [TestMethod]
    public async Task BuildSnapshotAsync_MapsSnapshotId()
    {
        var builder = new StubPackageBuilder();
        builder.ResultToReturn = MakeBuildResult(packageId: "pkg-123");
        var bridge = new DefaultAgentContextBridge(builder);

        var response = await bridge.BuildSnapshotAsync(MakeRequest());

        Assert.AreEqual("agent-bridge-pkg-123", response.Snapshot.SnapshotId);
    }

    [TestMethod]
    public async Task BuildSnapshotAsync_MapsActualTokens()
    {
        var builder = new StubPackageBuilder();
        builder.ResultToReturn = MakeBuildResult(estimatedTokens: 750);
        var bridge = new DefaultAgentContextBridge(builder);

        var response = await bridge.BuildSnapshotAsync(MakeRequest());

        Assert.AreEqual(750, response.Snapshot.ActualTokens);
    }

    [TestMethod]
    public async Task BuildSnapshotAsync_MapsMetadata()
    {
        var builder = new StubPackageBuilder();
        builder.ResultToReturn = MakeBuildResult(buildId: "b-1", packageId: "p-1");
        var bridge = new DefaultAgentContextBridge(builder);

        var response = await bridge.BuildSnapshotAsync(MakeRequest());

        Assert.AreEqual("b-1", response.Snapshot.Metadata["buildId"]);
        Assert.AreEqual("p-1", response.Snapshot.Metadata["packageId"]);
        Assert.AreEqual("DefaultAgentContextBridge", response.Snapshot.Metadata["source"]);
        Assert.IsTrue(response.Snapshot.Metadata.ContainsKey("selectedCount"));
        Assert.IsTrue(response.Snapshot.Metadata.ContainsKey("droppedCount"));
        Assert.IsTrue(response.Snapshot.Metadata.ContainsKey("sectionCount"));
    }

    // =========================================================================
    // 7. BuildResult + Duration
    // =========================================================================

    [TestMethod]
    public async Task BuildSnapshotAsync_ReturnsBuildResult()
    {
        var builder = new StubPackageBuilder();
        var expectedResult = MakeBuildResult();
        builder.ResultToReturn = expectedResult;
        var bridge = new DefaultAgentContextBridge(builder);

        var response = await bridge.BuildSnapshotAsync(MakeRequest());

        Assert.AreSame(expectedResult, response.BuildResult);
    }

    [TestMethod]
    public async Task BuildSnapshotAsync_ReturnsDuration()
    {
        var builder = new StubPackageBuilder();
        builder.ResultToReturn = MakeBuildResult();
        var bridge = new DefaultAgentContextBridge(builder);

        var response = await bridge.BuildSnapshotAsync(MakeRequest());

        Assert.IsTrue(response.Duration >= TimeSpan.Zero);
    }

    // =========================================================================
    // 8. CancellationToken + fail-closed
    // =========================================================================

    [TestMethod]
    public async Task BuildSnapshotAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var bridge = new DefaultAgentContextBridge(new StubPackageBuilder());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => bridge.BuildSnapshotAsync(MakeRequest(), cts.Token));
    }

    [TestMethod]
    public async Task BuildSnapshotAsync_PackageBuilderThrows_PropagatesException()
    {
        var builder = new StubPackageBuilder();
        builder.ThrowException = new InvalidOperationException("ContextCore build failed");
        var bridge = new DefaultAgentContextBridge(builder);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => bridge.BuildSnapshotAsync(MakeRequest()));
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static AgentSessionId MakeSession(string workspaceId = "ws-1") => new()
    {
        Value = "session-test",
        RuntimeKind = AgentRuntimeKind.GenericTool,
        WorkspaceId = workspaceId,
        CollectionId = "col-1",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static AgentContextBridgeRequest MakeRequest(
        string workspaceId = "ws-1",
        string? queryText = null,
        int tokenBudget = 1000) => new()
        {
            Session = MakeSession(workspaceId),
            QueryText = queryText,
            TokenBudget = tokenBudget
        };

    private static ContextPackageBuildResult MakeBuildResult(
        string packageId = "pkg-1",
        string buildId = "build-1",
        int estimatedTokens = 100,
        ContextPackageSection[]? sections = null,
        ContextPackageDecision[]? selectedItems = null) => new()
        {
            BuildId = buildId,
            Package = new ContextPackage
            {
                PackageId = packageId,
                WorkspaceId = "ws-1",
                CollectionId = "col-1",
                Sections = sections ?? Array.Empty<ContextPackageSection>(),
                EstimatedTokens = estimatedTokens,
                CreatedAt = DateTimeOffset.UtcNow
            },
            SelectedItems = selectedItems ?? Array.Empty<ContextPackageDecision>()
        };

    // ===== Stub IContextPackageBuilder =====

    private sealed class StubPackageBuilder : IContextPackageBuilder
    {
        public ContextPackageBuildResult? ResultToReturn { get; set; }
        public Exception? ThrowException { get; set; }
        public int BuildDetailedCallCount { get; private set; }
        public ContextPackageRequest? LastRequest { get; private set; }

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
            BuildDetailedCallCount++;
            LastRequest = request;
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowException is not null)
            {
                throw ThrowException;
            }
            return Task.FromResult(ResultToReturn ?? new ContextPackageBuildResult
            {
                BuildId = "stub-build",
                Package = new ContextPackage { PackageId = "stub-pkg" }
            });
        }
    }
}
