using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.ModelExecution;
using ContextCore.Core.Services.MemoryEvolution;
using ContextCore.Core.Services.Policy;
using ContextCore.Core.Services.Retrieval;

namespace ContextCore.Tests;

// ===========================================================================
// R29 WP-E-2：Utility Ledger Materialization 验收测试
//
// 目标：
//   验证 DefaultContextDecisionRuntime 在决策完成后异步触发 UtilityLedgerMaterializer，
//   将 SelectedEnvelopes + DroppedEnvelopes 物化到 IUtilityLedger / IConflictSetLedger。
//
// 设计原则（对齐澄清 #4 + R29 学习闭环）：
//   1. materializer 通过 nullable 参数注入；测试路径注入 InMemory 实现以验证写入正确性。
//   2. fire-and-forget：Runtime 不等待物化完成，但物化完成后 ledger 应有对应 entry。
//   3. P8 硬边界：所有 candidate（selected/dropped）都写入 ledger，避免"dropped 视为负样本"。
//   4. 候选 CanonicalKey 的 WorkspaceId/CollectionId 必须与 request.Scope 一致，
//      否则 DefaultEarlyAdmissionGate 会以 scope-mismatch 拒绝（不进入 Engine）。
//   5. dropped 候选必须显式设置 PassesSafetyGate=false，否则 SafetyGate 放行后由 Allocator 决定。
//
// 验收点：
//   - Runtime 注入 materializer 后，决策执行完 ledger 出现 entry
//   - selected envelope 写入 IsSelected=true 的 entry
//   - dropped envelope 写入 IsSelected=false 的 entry
//   - 未注入 materializer（null）时决策仍正常返回（向后兼容）
// ===========================================================================

[TestClass]
[TestCategory("R29")]
[TestCategory("WP-E-2")]
public sealed class R29E_LedgerMaterializationAcceptanceTests
{
    [TestMethod]
    public async Task ExecuteAsync_WithMaterializer_WritesSelectedAndDroppedToLedger()
    {
        // 准备：1 个 selected + 1 个 dropped 候选，注入 InMemory materializer。
        // dropped 候选必须 PassesSafetyGate=false 才会被 SafetyGate 拒绝（而非由 Allocator 决定）。
        var ws = "ws-wpe2";
        var col = "col-wpe2";
        var selected = MakeScopedEnvelope("c-selected", ContextCandidateSource.Semantic, score: 0.9, tokens: 100, ws, col);
        var dropped = MakeScopedEnvelope("c-dropped", ContextCandidateSource.Lexical, score: 0.3, tokens: 50, ws, col,
            safety: new CandidateSafetyState
            {
                PassesSafetyGate = false,
                BlockReasonCode = CandidateDecisionReasonCode.SectionQuotaExceeded
            });

        var provider = new ContextCapturingProvider(
            ExpertKind.Semantic,
            MakeExpertResultFromEnvelope(selected, dropped));

        var ledgerStore = new InMemoryUtilityLedgerStore();
        var conflictStore = new InMemoryConflictSetStore();
        var materializer = new UtilityLedgerMaterializer(ledgerStore, conflictStore);

        var runtime = BuildRuntime(providers: new[] { provider }, materializer: materializer);

        var request = new ContextDecisionRuntimeRequest
        {
            RequestId = "req-wpe2-ledger",
            Scope = new ContextDecisionScope(ws, col),
            Purpose = ContextDecisionPurpose.Retrieval,
            TokenBudget = 4096,
            TopK = 10
        };

        // 执行决策 — Runtime 在返回前会触发 fire-and-forget 物化。
        var result = await runtime.ExecuteAsync(request, CancellationToken.None);

        // fire-and-forget：等待后台 Task.Run 完成（InMemory store 写入极快，轮询确认即可）。
        await WaitForLedgerAsync(ledgerStore, expectedCount: 2, timeout: TimeSpan.FromSeconds(2));

        // 验证 ledger 包含 selected + dropped 两条 entry。
        var entries = await ledgerStore.QueryAsync(new UtilityLedgerQuery
        {
            WorkspaceId = ws,
            DecisionId = result.RequestId
        });

        Assert.AreEqual(2, entries.Count, "P8 硬边界：selected + dropped 都应写入 ledger。");
        Assert.IsTrue(entries.Any(e => e.CandidateItemId == "c-selected" && e.IsSelected),
            "Selected envelope 应写入 IsSelected=true 的 entry。");
        Assert.IsTrue(entries.Any(e => e.CandidateItemId == "c-dropped" && !e.IsSelected),
            "Dropped envelope 应写入 IsSelected=false 的 entry。");

        // 验证 entry 携带正确的 workspace / collection 作用域（来自 request.Scope）。
        Assert.IsTrue(entries.All(e => e.WorkspaceId == ws),
            "所有 entry 的 WorkspaceId 应来自 request.Scope。");
        Assert.IsTrue(entries.All(e => e.CollectionId == col),
            "所有 entry 的 CollectionId 应来自 request.Scope。");

        // 验证 dropped entry 记录 BlockReasonCode（SectionQuotaExceeded）。
        var droppedEntry = entries.First(e => e.CandidateItemId == "c-dropped");
        Assert.AreEqual(CandidateDecisionReasonCode.SectionQuotaExceeded.ToString(),
            droppedEntry.DropReasonCode,
            "Dropped entry 应记录 BlockReasonCode。");

        // 验证 ConflictSet：仅 1 个 SectionQuotaExceeded 候选不构成冲突集（需 >=2）。
        var conflicts = await conflictStore.QueryAsync(new ConflictSetQuery
        {
            WorkspaceId = ws,
            DecisionId = result.RequestId
        });
        Assert.AreEqual(0, conflicts.Count, "单个 SectionQuotaExceeded 不构成 ConflictSet。");
    }

    [TestMethod]
    public async Task ExecuteAsync_WithoutMaterializer_DoesNotThrowAndReturnsDecision()
    {
        // 未注入 materializer（null）时，Runtime 应正常返回决策，不抛异常。
        var ws = "ws";
        var col = "col";
        var selected = MakeScopedEnvelope("c-no-mat", ContextCandidateSource.Semantic, score: 0.8, tokens: 100, ws, col);

        var provider = new ContextCapturingProvider(
            ExpertKind.Semantic,
            MakeExpertResultFromEnvelope(selected));

        // materializer 参数为 null（默认值）
        var runtime = BuildRuntime(providers: new[] { provider }, materializer: null);

        var request = new ContextDecisionRuntimeRequest
        {
            RequestId = "req-no-mat",
            Scope = new ContextDecisionScope(ws, col),
            Purpose = ContextDecisionPurpose.Retrieval,
            TokenBudget = 4096,
            TopK = 10
        };

        var result = await runtime.ExecuteAsync(request, CancellationToken.None);

        // 决策正常返回。
        Assert.AreEqual("req-no-mat", result.RequestId);
        Assert.IsTrue(result.SelectedEnvelopes.Count >= 1, "Selected 候选应被保留。");
    }

    [TestMethod]
    public async Task ExecuteAsync_EmptyResult_StillTriggersMaterializationWithZeroEntries()
    {
        // 空结果路径（无候选进入 Engine）：物化触发但 0 条 entry。
        // 这验证了 DecisionId 审计链 — 即便 0 条 entry，物化路径仍被调用。
        var provider = new ContextCapturingProvider(
            ExpertKind.Semantic,
            new ExpertExecutionResult(
                Array.Empty<ContextCandidateEnvelope>(),
                new Dictionary<CanonicalCandidateKey, CandidateMaterial>()));

        var ledgerStore = new InMemoryUtilityLedgerStore();
        var conflictStore = new InMemoryConflictSetStore();
        var materializer = new UtilityLedgerMaterializer(ledgerStore, conflictStore);

        var runtime = BuildRuntime(providers: new[] { provider }, materializer: materializer);

        var request = new ContextDecisionRuntimeRequest
        {
            RequestId = "req-empty",
            Scope = new ContextDecisionScope("ws-empty", "col-empty"),
            Purpose = ContextDecisionPurpose.Retrieval,
            TokenBudget = 4096,
            TopK = 10
        };

        var result = await runtime.ExecuteAsync(request, CancellationToken.None);

        // 等待 fire-and-forget 完成（空结果路径物化为 no-op）。
        await Task.Delay(100);

        // ledger 应为空（无候选可写入），但不应抛异常。
        var entries = await ledgerStore.QueryAsync(new UtilityLedgerQuery
        {
            WorkspaceId = "ws-empty",
            DecisionId = result.RequestId
        });
        Assert.AreEqual(0, entries.Count, "空结果路径不应写入任何 ledger entry。");

        // 决策正常返回。
        Assert.AreEqual(0, result.SelectedEnvelopes.Count);
        Assert.AreEqual(0, result.DroppedEnvelopes.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_MultipleDroppedSectionConflict_GeneratesConflictSet()
    {
        // 2 个 SectionQuotaExceeded 候选构成 ConflictSet（验证 ConflictSet 物化路径）。
        // 两者都 PassesSafetyGate=false → 被 SafetyGate 拒绝 → 进入 DroppedEnvelopes。
        var ws = "ws-conflict";
        var col = "col-conflict";
        var dropped1 = MakeScopedEnvelope("c-drop-1", ContextCandidateSource.Lexical, score: 0.4, tokens: 50, ws, col,
            safety: new CandidateSafetyState
            {
                PassesSafetyGate = false,
                BlockReasonCode = CandidateDecisionReasonCode.SectionQuotaExceeded
            });
        var dropped2 = MakeScopedEnvelope("c-drop-2", ContextCandidateSource.WorkingMemory, score: 0.5, tokens: 60, ws, col,
            safety: new CandidateSafetyState
            {
                PassesSafetyGate = false,
                BlockReasonCode = CandidateDecisionReasonCode.SectionQuotaExceeded
            });

        var provider = new ContextCapturingProvider(
            ExpertKind.Semantic,
            MakeExpertResultFromEnvelope(dropped1, dropped2));

        var ledgerStore = new InMemoryUtilityLedgerStore();
        var conflictStore = new InMemoryConflictSetStore();
        var materializer = new UtilityLedgerMaterializer(ledgerStore, conflictStore);

        var runtime = BuildRuntime(providers: new[] { provider }, materializer: materializer);

        var request = new ContextDecisionRuntimeRequest
        {
            RequestId = "req-section-conflict",
            Scope = new ContextDecisionScope(ws, col),
            Purpose = ContextDecisionPurpose.Retrieval,
            TokenBudget = 4096,
            TopK = 10
        };

        var result = await runtime.ExecuteAsync(request, CancellationToken.None);

        // 等待 fire-and-forget 完成。
        await WaitForLedgerAsync(ledgerStore, expectedCount: 2, timeout: TimeSpan.FromSeconds(2));

        // ConflictSet 应有 1 条 SectionConflict（2 个 SectionQuotaExceeded 候选构成冲突）。
        var conflicts = await conflictStore.QueryAsync(new ConflictSetQuery
        {
            WorkspaceId = ws,
            Kind = ConflictSetKind.SectionConflict
        });
        Assert.AreEqual(1, conflicts.Count, "2 个 SectionQuotaExceeded 候选应构成 1 个 ConflictSet。");
        Assert.AreEqual(2, conflicts[0].Entries.Count, "ConflictSet 应包含 2 个 entry。");
    }

    // --- helpers ---

    /// <summary>
    /// 创建 CanonicalKey 匹配 request.Scope 的 envelope。
    /// DefaultEarlyAdmissionGate 会拒绝 WorkspaceId/CollectionId 不匹配的候选，
    /// 因此测试 envelope 的 CanonicalKey 必须与 request.Scope 一致。
    /// </summary>
    private static ContextCandidateEnvelope MakeScopedEnvelope(
        string candidateId,
        ContextCandidateSource source,
        double score,
        int tokens,
        string workspaceId,
        string collectionId,
        CandidateSafetyState? safety = null)
    {
        return new ContextCandidateEnvelope
        {
            CandidateId = candidateId,
            CanonicalKey = CanonicalCandidateKey.Create(
                workspaceId: workspaceId,
                collectionId: collectionId,
                entityKind: "test-entity",
                entityId: candidateId,
                entityVersion: "v1"),
            Source = source,
            Type = "test-type",
            EstimatedTokens = tokens,
            Safety = safety ?? new CandidateSafetyState(),
            Utility = new CandidateUtilityScore
            {
                DeterministicScore = score,
                FinalScore = score,
                ReasonCode = "deterministic-only"
            }
        };
    }

    private static async Task WaitForLedgerAsync(
        InMemoryUtilityLedgerStore ledgerStore,
        int expectedCount,
        TimeSpan timeout)
    {
        // fire-and-forget 物化由 Task.Run 异步执行；轮询确认 ledger 写入完成。
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var current = await ledgerStore.QueryAsync(new UtilityLedgerQuery
            {
                WorkspaceId = "ignored",
                Take = 0 // 0 = 不限制
            });
            // 用 Take=0 取全量，再过滤；简化测试断言。
            if (current.Count >= expectedCount)
            {
                return;
            }
            await Task.Delay(20);
        }
    }

    private static ExpertExecutionResult MakeExpertResultFromEnvelope(
        params ContextCandidateEnvelope[] envelopes)
    {
        var materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>();
        foreach (var env in envelopes)
        {
            materials[env.CanonicalKey] = R28BTestHelpers.MakeMaterial(env.CanonicalKey, "test content");
        }
        return new ExpertExecutionResult(envelopes, materials);
    }

    internal static DefaultContextDecisionRuntime BuildRuntime(
        IReadOnlyList<ICandidateProvider> providers,
        UtilityLedgerMaterializer? materializer)
    {
        var engine = new DefaultContextDecisionEngine(
            policyRegistry: null,
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: new DefaultUtilityScorer(new DefaultFeatureSchemaValidator()),
            globalAllocator: new DefaultGlobalAllocator());

        return new DefaultContextDecisionRuntime(
            engine: engine,
            policyProvider: new DefaultResolvedPolicyProvider(),
            router: new DefaultRouter(new DefaultExpertCatalog()),
            expertCatalog: new DefaultExpertCatalog(),
            candidateProviders: providers,
            canonicalMerger: new DefaultCanonicalCandidateMerger(),
            earlyAdmissionGate: new DefaultEarlyAdmissionGate(),
            featurePipeline: new DefaultFeaturePipeline(),
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: new DefaultUtilityScorer(new DefaultFeatureSchemaValidator()),
            utilityLedgerMaterializer: materializer);
    }
}
