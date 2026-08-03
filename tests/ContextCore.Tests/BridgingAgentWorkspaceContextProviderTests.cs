using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Agent;

namespace ContextCore.Tests;

/// <summary>
/// BridgingAgentWorkspaceContextProvider 实现测试。
///
/// 覆盖：
/// 1. 构造函数 null bridge / null inner 抛异常
/// 2. 构造函数 contextCoreBudgetRatio 边界（0、1、负数）
/// 3. GetContextSnapshotAsync null session / tokenBudget <= 0 抛异常
/// 4. 成功路径：Bridge + inner provider 都调用
/// 5. 合并后 Sections 顺序（ContextCore 在前 + inner 在后）
/// 6. 合并后 SortOrder 连续
/// 7. 合并后 DecisionRequestIds / ConstraintIds / ToolCallRefs 合并去重
/// 8. 合并后 ActualTokens = cc + inner
/// 9. Token 预算分配（70/30 默认）
/// 10. Bridge 失败 fail-open（仍调用 inner provider，bridgeFailed=true）
/// 11. Bridge 抛 OperationCanceledException 透传
/// 12. InjectAsync / IngestToolResultAsync 委托给 inner provider
/// 13. CancellationToken 传递
/// </summary>
[TestClass]
[TestCategory("R25")]
public sealed class BridgingAgentWorkspaceContextProviderTests
{
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // =========================================================================
    // 1. 构造函数
    // =========================================================================

    [TestMethod]
    public void Constructor_NullBridge_Throws()
    {
        var inner = new DefaultAgentWorkspaceContextProvider(new GenericToolAgentAdapter());
        Assert.ThrowsException<ArgumentNullException>(
            () => new BridgingAgentWorkspaceContextProvider(null!, inner));
    }

    [TestMethod]
    public void Constructor_NullInner_Throws()
    {
        var bridge = new DefaultAgentContextBridge(new StubPackageBuilder());
        Assert.ThrowsException<ArgumentNullException>(
            () => new BridgingAgentWorkspaceContextProvider(bridge, null!));
    }

    [TestMethod]
    public void Constructor_ZeroBudgetRatio_Throws()
    {
        var (bridge, inner) = MakeDeps();
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new BridgingAgentWorkspaceContextProvider(bridge, inner, contextCoreBudgetRatio: 0));
    }

    [TestMethod]
    public void Constructor_OneBudgetRatio_Throws()
    {
        var (bridge, inner) = MakeDeps();
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new BridgingAgentWorkspaceContextProvider(bridge, inner, contextCoreBudgetRatio: 1.0));
    }

    [TestMethod]
    public void Constructor_NegativeBudgetRatio_Throws()
    {
        var (bridge, inner) = MakeDeps();
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new BridgingAgentWorkspaceContextProvider(bridge, inner, contextCoreBudgetRatio: -0.1));
    }

    [TestMethod]
    public void Constructor_ValidRatio_Preserved()
    {
        var (bridge, inner) = MakeDeps();
        var provider = new BridgingAgentWorkspaceContextProvider(bridge, inner, contextCoreBudgetRatio: 0.5);
        Assert.AreEqual(0.5, provider.ContextCoreBudgetRatio);
    }

    // =========================================================================
    // 2. GetContextSnapshotAsync — 错误路径
    // =========================================================================

    [TestMethod]
    public async Task GetContextSnapshotAsync_NullSession_Throws()
    {
        var provider = MakeProvider();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => provider.GetContextSnapshotAsync(null!, 1000));
    }

    [TestMethod]
    public async Task GetContextSnapshotAsync_ZeroBudget_Throws()
    {
        var provider = MakeProvider();
        var session = MakeSession();
        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(
            () => provider.GetContextSnapshotAsync(session, 0));
    }

    // =========================================================================
    // 3. 成功路径：两个 provider 都调用
    // =========================================================================

    [TestMethod]
    public async Task GetContextSnapshotAsync_CallsBothBridgeAndInner()
    {
        var stubBridge = new StubBridge();
        var stubInner = new StubInnerProvider();
        var provider = new BridgingAgentWorkspaceContextProvider(stubBridge, stubInner);

        var session = MakeSession();
        var ref1 = await provider.GetContextSnapshotAsync(session, 1000);

        Assert.IsTrue(stubBridge.BuildCalled);
        Assert.IsTrue(stubInner.GetSnapshotCalled);
    }

    // =========================================================================
    // 4. 合并后 Sections 顺序
    // =========================================================================

    [TestMethod]
    public async Task GetContextSnapshotAsync_MergesSectionsCcFirstInnerSecond()
    {
        var stubBridge = new StubBridge();
        stubBridge.SnapshotToReturn = MakeSnapshot("cc-snap",
            sections: new[]
            {
                MakeSection("cc-1", "cc content"),
                MakeSection("cc-2", "cc content 2")
            });
        var stubInner = new StubInnerProvider();
        stubInner.SnapshotToReturn = MakeSnapshot("inner-snap",
            sections: new[]
            {
                MakeSection("inj-1", "injection content")
            });
        var provider = new BridgingAgentWorkspaceContextProvider(stubBridge, stubInner);

        var session = MakeSession();
        var ref1 = await provider.GetContextSnapshotAsync(session, 10000);

        var snapshot = Deserialize(ref1.ContentJson);
        Assert.AreEqual(3, snapshot.Sections.Count);
        Assert.AreEqual("cc-1", snapshot.Sections[0].SectionName);
        Assert.AreEqual("cc-2", snapshot.Sections[1].SectionName);
        Assert.AreEqual("inj-1", snapshot.Sections[2].SectionName);
    }

    // =========================================================================
    // 5. SortOrder 连续
    // =========================================================================

    [TestMethod]
    public async Task GetContextSnapshotAsync_SortOrdersAreConsecutive()
    {
        var stubBridge = new StubBridge();
        stubBridge.SnapshotToReturn = MakeSnapshot("cc-snap",
            sections: new[]
            {
                MakeSection("cc-1", "content", sortOrder: 5),
                MakeSection("cc-2", "content", sortOrder: 10)
            });
        var stubInner = new StubInnerProvider();
        stubInner.SnapshotToReturn = MakeSnapshot("inner-snap",
            sections: new[]
            {
                MakeSection("inj-1", "content", sortOrder: 3)
            });
        var provider = new BridgingAgentWorkspaceContextProvider(stubBridge, stubInner);

        var session = MakeSession();
        var ref1 = await provider.GetContextSnapshotAsync(session, 10000);

        var snapshot = Deserialize(ref1.ContentJson);
        Assert.AreEqual(0, snapshot.Sections[0].SortOrder);
        Assert.AreEqual(1, snapshot.Sections[1].SortOrder);
        Assert.AreEqual(2, snapshot.Sections[2].SortOrder);
    }

    // =========================================================================
    // 6. DecisionRequestIds / ConstraintIds / ToolCallRefs 合并去重
    // =========================================================================

    [TestMethod]
    public async Task GetContextSnapshotAsync_MergesDecisionIdsWithDedup()
    {
        var stubBridge = new StubBridge();
        stubBridge.SnapshotToReturn = MakeSnapshot("cc-snap",
            decisionIds: new[] { "dec-1", "dec-2" });
        var stubInner = new StubInnerProvider();
        stubInner.SnapshotToReturn = MakeSnapshot("inner-snap",
            decisionIds: new[] { "dec-2", "dec-3" });
        var provider = new BridgingAgentWorkspaceContextProvider(stubBridge, stubInner);

        var session = MakeSession();
        var ref1 = await provider.GetContextSnapshotAsync(session, 10000);

        var snapshot = Deserialize(ref1.ContentJson);
        CollectionAssert.AreEquivalent(new[] { "dec-1", "dec-2", "dec-3" },
            snapshot.DecisionRequestIds.ToList());
    }

    [TestMethod]
    public async Task GetContextSnapshotAsync_MergesConstraintIdsWithDedup()
    {
        var stubBridge = new StubBridge();
        stubBridge.SnapshotToReturn = MakeSnapshot("cc-snap",
            constraintIds: new[] { "con-1" });
        var stubInner = new StubInnerProvider();
        stubInner.SnapshotToReturn = MakeSnapshot("inner-snap",
            constraintIds: new[] { "con-1", "con-2" });
        var provider = new BridgingAgentWorkspaceContextProvider(stubBridge, stubInner);

        var session = MakeSession();
        var ref1 = await provider.GetContextSnapshotAsync(session, 10000);

        var snapshot = Deserialize(ref1.ContentJson);
        CollectionAssert.AreEquivalent(new[] { "con-1", "con-2" },
            snapshot.ConstraintIds.ToList());
    }

    [TestMethod]
    public async Task GetContextSnapshotAsync_MergesToolCallRefs()
    {
        var stubBridge = new StubBridge();
        stubBridge.SnapshotToReturn = MakeSnapshot("cc-snap",
            toolCallRefs: new Dictionary<string, string> { ["call-1"] = "search" });
        var stubInner = new StubInnerProvider();
        stubInner.SnapshotToReturn = MakeSnapshot("inner-snap",
            toolCallRefs: new Dictionary<string, string>
            {
                ["call-2"] = "execute",
                ["call-1"] = "search" // 重复（应被 cc 覆盖）
            });
        var provider = new BridgingAgentWorkspaceContextProvider(stubBridge, stubInner);

        var session = MakeSession();
        var ref1 = await provider.GetContextSnapshotAsync(session, 10000);

        var snapshot = Deserialize(ref1.ContentJson);
        Assert.AreEqual(2, snapshot.ToolCallRefs.Count);
        Assert.AreEqual("search", snapshot.ToolCallRefs["call-1"]);
        Assert.AreEqual("execute", snapshot.ToolCallRefs["call-2"]);
    }

    // =========================================================================
    // 7. ActualTokens = cc + inner
    // =========================================================================

    [TestMethod]
    public async Task GetContextSnapshotAsync_SumsActualTokens()
    {
        var stubBridge = new StubBridge();
        stubBridge.SnapshotToReturn = MakeSnapshot("cc-snap", actualTokens: 200);
        var stubInner = new StubInnerProvider();
        stubInner.SnapshotToReturn = MakeSnapshot("inner-snap", actualTokens: 100);
        var provider = new BridgingAgentWorkspaceContextProvider(stubBridge, stubInner);

        var session = MakeSession();
        var ref1 = await provider.GetContextSnapshotAsync(session, 10000);

        Assert.AreEqual(300, ref1.ActualTokens);
    }

    // =========================================================================
    // 8. Token 预算分配
    // =========================================================================

    [TestMethod]
    public async Task GetContextSnapshotAsync_SplitsBudgetByRatio()
    {
        var stubBridge = new StubBridge();
        var stubInner = new StubInnerProvider();
        var provider = new BridgingAgentWorkspaceContextProvider(stubBridge, stubInner, contextCoreBudgetRatio: 0.7);

        var session = MakeSession();
        await provider.GetContextSnapshotAsync(session, 1000);

        // 70% 给 cc = 700；30% 给 inner = 300
        Assert.AreEqual(700, stubBridge.LastTokenBudget);
        Assert.AreEqual(300, stubInner.LastTokenBudget);
    }

    [TestMethod]
    public async Task GetContextSnapshotAsync_FiftyFiftySplit()
    {
        var stubBridge = new StubBridge();
        var stubInner = new StubInnerProvider();
        var provider = new BridgingAgentWorkspaceContextProvider(stubBridge, stubInner, contextCoreBudgetRatio: 0.5);

        var session = MakeSession();
        await provider.GetContextSnapshotAsync(session, 1000);

        Assert.AreEqual(500, stubBridge.LastTokenBudget);
        Assert.AreEqual(500, stubInner.LastTokenBudget);
    }

    // =========================================================================
    // 9. Bridge 失败 fail-open
    // =========================================================================

    [TestMethod]
    public async Task GetContextSnapshotAsync_BridgeFails_FailsOpenWithMetadata()
    {
        var stubBridge = new StubBridge();
        stubBridge.ThrowException = new InvalidOperationException("bridge down");
        var stubInner = new StubInnerProvider();
        stubInner.SnapshotToReturn = MakeSnapshot("inner-snap",
            sections: new[] { MakeSection("inner-1", "content") });
        var provider = new BridgingAgentWorkspaceContextProvider(stubBridge, stubInner);

        var session = MakeSession();
        var ref1 = await provider.GetContextSnapshotAsync(session, 1000);

        // 仍调用 inner provider
        Assert.IsTrue(stubInner.GetSnapshotCalled);
        // Metadata 标记 bridgeFailed=true
        Assert.AreEqual("true", ref1.Metadata["bridgeFailed"]);
        // snapshot 仅含 inner section
        var snapshot = Deserialize(ref1.ContentJson);
        Assert.AreEqual(1, snapshot.Sections.Count);
        Assert.AreEqual("inner-1", snapshot.Sections[0].SectionName);
    }

    // =========================================================================
    // 10. Bridge 抛 OperationCanceledException 透传
    // =========================================================================

    [TestMethod]
    public async Task GetContextSnapshotAsync_BridgeThrowsCancellation_Propagates()
    {
        var stubBridge = new StubBridge();
        stubBridge.ThrowException = new OperationCanceledException("cancelled");
        var stubInner = new StubInnerProvider();
        var provider = new BridgingAgentWorkspaceContextProvider(stubBridge, stubInner);

        var session = MakeSession();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => provider.GetContextSnapshotAsync(session, 1000));
    }

    // =========================================================================
    // 11. InjectAsync / IngestToolResultAsync 委托给 inner
    // =========================================================================

    [TestMethod]
    public async Task InjectAsync_DelegatesToInner()
    {
        var stubBridge = new StubBridge();
        var stubInner = new StubInnerProvider();
        var provider = new BridgingAgentWorkspaceContextProvider(stubBridge, stubInner);

        var session = MakeSession();
        await provider.InjectAsync(session, new AgentContextInjection
        {
            InjectionId = "inj-1",
            InjectedAt = DateTimeOffset.UtcNow
        });

        Assert.IsTrue(stubInner.InjectCalled);
    }

    [TestMethod]
    public async Task IngestToolResultAsync_DelegatesToInner()
    {
        var stubBridge = new StubBridge();
        var stubInner = new StubInnerProvider();
        var provider = new BridgingAgentWorkspaceContextProvider(stubBridge, stubInner);

        var session = MakeSession();
        await provider.IngestToolResultAsync(session, "call-1", "tool", "{}");

        Assert.IsTrue(stubInner.IngestToolCalled);
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static AgentSessionId MakeSession() => new()
    {
        Value = "session-test",
        RuntimeKind = AgentRuntimeKind.GenericTool,
        WorkspaceId = "ws-1",
        CollectionId = "col-1",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static (DefaultAgentContextBridge bridge, DefaultAgentWorkspaceContextProvider inner) MakeDeps()
    {
        var bridge = new DefaultAgentContextBridge(new StubPackageBuilder());
        var inner = new DefaultAgentWorkspaceContextProvider(new GenericToolAgentAdapter());
        return (bridge, inner);
    }

    private static BridgingAgentWorkspaceContextProvider MakeProvider()
    {
        var (bridge, inner) = MakeDeps();
        return new BridgingAgentWorkspaceContextProvider(bridge, inner);
    }

    private static AgentContextSnapshot MakeSnapshot(
        string snapshotId,
        AgentSessionId? session = null,
        IReadOnlyList<AgentContextSection>? sections = null,
        IReadOnlyList<string>? decisionIds = null,
        IReadOnlyList<string>? constraintIds = null,
        IReadOnlyDictionary<string, string>? toolCallRefs = null,
        int actualTokens = 0) => new()
        {
            SnapshotId = snapshotId,
            Session = session ?? MakeSession(),
            CreatedAt = DateTimeOffset.UtcNow,
            TokenBudget = 1000,
            ActualTokens = actualTokens,
            Sections = sections ?? Array.Empty<AgentContextSection>(),
            DecisionRequestIds = decisionIds ?? Array.Empty<string>(),
            ConstraintIds = constraintIds ?? Array.Empty<string>(),
            ToolCallRefs = toolCallRefs ?? new Dictionary<string, string>(StringComparer.Ordinal)
        };

    private static AgentContextSection MakeSection(
        string name,
        string content,
        int sortOrder = 0) => new()
        {
            SectionName = name,
            SortOrder = sortOrder,
            TokenBudget = 1000,
            ActualTokens = content.Length / 4,
            Content = content,
            Source = "test"
        };

    private static AgentContextSnapshot Deserialize(string json)
    {
        return JsonSerializer.Deserialize<AgentContextSnapshot>(json, DeserializeOptions)
            ?? throw new InvalidOperationException("Failed to deserialize");
    }

    // ===== Stub IAgentContextBridge =====

    private sealed class StubBridge : IAgentContextBridge
    {
        public bool BuildCalled { get; private set; }
        public int LastTokenBudget { get; private set; }
        public AgentContextSnapshot? SnapshotToReturn { get; set; }
        public Exception? ThrowException { get; set; }

        public Task<AgentContextBridgeResponse> BuildSnapshotAsync(
            AgentContextBridgeRequest request,
            CancellationToken cancellationToken = default)
        {
            BuildCalled = true;
            LastTokenBudget = request.TokenBudget;
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowException is not null)
            {
                throw ThrowException;
            }
            var snapshot = SnapshotToReturn ?? MakeSnapshot("cc-default");
            return Task.FromResult(new AgentContextBridgeResponse
            {
                Snapshot = snapshot,
                BuildResult = new ContextPackageBuildResult
                {
                    BuildId = "stub-build",
                    Package = new ContextPackage { PackageId = "stub-pkg" }
                }
            });
        }
    }

    // ===== Stub IAgentWorkspaceContextProvider =====

    private sealed class StubInnerProvider : IAgentWorkspaceContextProvider
    {
        public bool GetSnapshotCalled { get; private set; }
        public int LastTokenBudget { get; private set; }
        public AgentContextSnapshot? SnapshotToReturn { get; set; }
        public bool InjectCalled { get; private set; }
        public bool IngestToolCalled { get; private set; }

        public Task<AgentContextSnapshotRef> GetContextSnapshotAsync(
            AgentSessionId sessionId,
            int tokenBudget,
            CancellationToken cancellationToken = default)
        {
            GetSnapshotCalled = true;
            LastTokenBudget = tokenBudget;
            var snapshot = SnapshotToReturn ?? MakeSnapshot("inner-default");
            var json = JsonSerializer.Serialize(snapshot, DeserializeOptions);
            return Task.FromResult(new AgentContextSnapshotRef
            {
                SnapshotId = snapshot.SnapshotId,
                Session = sessionId,
                CreatedAt = DateTimeOffset.UtcNow,
                ActualTokens = snapshot.ActualTokens,
                TokenBudget = tokenBudget,
                ContentJson = json
            });
        }

        public Task InjectAsync(
            AgentSessionId sessionId,
            AgentContextInjection injection,
            CancellationToken cancellationToken = default)
        {
            InjectCalled = true;
            return Task.CompletedTask;
        }

        public Task IngestToolResultAsync(
            AgentSessionId sessionId,
            string toolCallId,
            string toolName,
            string resultJson,
            CancellationToken cancellationToken = default)
        {
            IngestToolCalled = true;
            return Task.CompletedTask;
        }
    }

    // ===== Stub IContextPackageBuilder =====

    private sealed class StubPackageBuilder : IContextPackageBuilder
    {
        public Task<ContextPackage> BuildAsync(ContextPackageRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ContextPackageBuildResult> BuildDetailedAsync(ContextPackageRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ContextPackageBuildResult
            {
                BuildId = "stub",
                Package = new ContextPackage { PackageId = "stub" }
            });
    }
}
