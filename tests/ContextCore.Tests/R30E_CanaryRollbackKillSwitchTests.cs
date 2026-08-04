using ContextCore.Abstractions;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Evolution;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

// ===========================================================================
// WP-D1（P0-11）：Canary 自动回滚先写持久化 Kill Switch + fail-closed
//
// 背景：RollbackAsync 在 DB CAS 失败时只设置本地 LocalEmergencyRollback /
// PersistPending / OperatorAlertRequired，从不调用 _emergencyOverrideStore
// .TrySetOverrideAsync——节点重启后没有持久化 Override，旧百分比可能重新恢复。
//
// 覆盖范围（修复后语义）：
// - 安全顺序：Create Emergency Override → Local route = 0% → 尝试推进 Canary 真相源；
// - DB CAS 失败时 Kill Switch 已先持久化（重启后 RecoverFromStoreAsync 强制 0%）；
// - DB CAS 成功时保留 Override 等待人工确认清除，run 不进入 Consistent（推进被阻断）；
// - Kill Switch 写入失败时本地持续 fail-closed（不标记 Consistent）；
// - Operator 清除覆盖后 AdvanceAsync 解除阻断并重新同步为 Consistent。
// ===========================================================================

[TestClass]
[TestCategory("R30")]
[TestCategory("Canary")]
public sealed class R30E_CanaryRollbackKillSwitchTests
{
    private const string RunId = "run-rollback-killswitch-001";

    // ---------------------------------------------------------------------------
    // 1. DB CAS 失败：Kill Switch 先持久化；本地紧急状态含 PersistPending；推进被阻断
    // ---------------------------------------------------------------------------
    [TestMethod]
    public async Task Rollback_DbCasFails_PersistsDurableOverrideAndGatesProgression()
    {
        var store = new InMemoryPipelineRunStore();
        var cutover = new CutoverController(50);
        var time = new CanaryAcceptanceTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var overrideStore = new InMemoryCanaryEmergencyOverrideStore();
        var service = new CanaryProgressionService(
            store, cutover,
            CanaryAcceptanceHelpers.DefaultOptions, time,
            decisionApplier: new ConfigurableCanaryDecisionApplier(applyResult: null),
            emergencyOverrideStore: overrideStore);
        await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store, RunId);
        service.InitializeCanary(RunId);

        await service.RollbackAsync(
            RunId, RollbackReason.ModelPerformanceRegression, CancellationToken.None);

        // Kill Switch 已持久化（先于 DB CAS 写入）——重启后 RecoverFromStoreAsync 可强制 0%。
        var active = await overrideStore.GetActiveAsync(RunId);
        Assert.IsNotNull(active, "DB CAS 失败时也必须先持久化 Emergency Override（Kill Switch）。");
        Assert.AreEqual("system:automatic-rollback", active!.OperatorName,
            "自动回滚写入的覆盖应使用固定系统 Operator 标识。");

        // 本地 0% + 紧急状态（含 PersistPending：DB 仍记录旧百分比）
        Assert.AreEqual(0, service.GetCurrentPercentage(RunId), "本地路由必须无条件切到 0%。");
        var localState = service.GetLocalState(RunId);
        Assert.IsTrue(localState.HasFlag(CanaryLocalState.LocalEmergencyRollback));
        Assert.IsTrue(localState.HasFlag(CanaryLocalState.PersistPending));
        Assert.IsTrue(localState.HasFlag(CanaryLocalState.OperatorAlertRequired));

        // 推进被阻断（PersistPending 不可仅凭清除覆盖解除——DB 未修复前必须保持阻断）
        var blocked = await service.AdvanceAsync(
            RunId, "t-rbk-fail-001", null,
            CanaryAcceptanceHelpers.HealthyBaseline, CanaryAcceptanceHelpers.HealthyExperiment);
        Assert.IsFalse(blocked.Applied);
        Assert.AreEqual(CanaryProgressionDecision.Hold, blocked.Decision);

        await overrideStore.TryClearOverrideAsync(RunId, "ops");
        var stillBlocked = await service.AdvanceAsync(
            RunId, "t-rbk-fail-002", null,
            CanaryAcceptanceHelpers.HealthyBaseline, CanaryAcceptanceHelpers.HealthyExperiment);
        Assert.AreEqual(CanaryProgressionDecision.Hold, stillBlocked.Decision,
            "DB CAS 失败（PersistPending）时清除覆盖不得解除阻断。");
    }

    // ---------------------------------------------------------------------------
    // 2. DB CAS 成功：覆盖保留（等待人工确认清除）；run 不进入 Consistent；推进被阻断
    // ---------------------------------------------------------------------------
    [TestMethod]
    public async Task Rollback_DbCasSucceeds_KeepsOverrideAndGatesProgression()
    {
        var store = new InMemoryPipelineRunStore();
        var cutover = new CutoverController(50);
        var time = new CanaryAcceptanceTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var overrideStore = new InMemoryCanaryEmergencyOverrideStore();
        var service = new CanaryProgressionService(
            store, cutover,
            CanaryAcceptanceHelpers.DefaultOptions, time,
            decisionApplier: new ConfigurableCanaryDecisionApplier(SuccessResult),
            emergencyOverrideStore: overrideStore);
        await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store, RunId);
        service.InitializeCanary(RunId);

        await service.RollbackAsync(
            RunId, RollbackReason.ModelPerformanceRegression, CancellationToken.None);

        // 覆盖保留：等待人工确认清除（P0-11：成功后可选择保留 Override）
        Assert.IsNotNull(await overrideStore.GetActiveAsync(RunId),
            "DB CAS 成功后应保留 Kill Switch 覆盖等待人工确认清除。");
        Assert.AreEqual(0, service.GetCurrentPercentage(RunId));

        // 不进入 Consistent（与 RecoverFromStoreAsync 语义一致：活跃覆盖期间推进被阻断）
        var localState = service.GetLocalState(RunId);
        Assert.AreNotEqual(CanaryLocalState.Consistent, localState);
        Assert.IsTrue(localState.HasFlag(CanaryLocalState.LocalEmergencyRollback));
        Assert.IsTrue(localState.HasFlag(CanaryLocalState.OperatorAlertRequired));
        Assert.IsFalse(localState.HasFlag(CanaryLocalState.PersistPending),
            "DB 已回滚 0%，不应标记 PersistPending。");

        var blocked = await service.AdvanceAsync(
            RunId, "t-rbk-succ-001", null,
            CanaryAcceptanceHelpers.HealthyBaseline, CanaryAcceptanceHelpers.HealthyExperiment);
        Assert.AreEqual(CanaryProgressionDecision.Hold, blocked.Decision,
            "活跃覆盖期间推进必须被阻断（不能绕过 Kill Switch 重新推进）。");
    }

    // ---------------------------------------------------------------------------
    // 3. Kill Switch 写入失败：fail-closed（本地紧急状态，不标记 Consistent）
    // ---------------------------------------------------------------------------
    [TestMethod]
    public async Task Rollback_OverrideWriteFails_DbCasFails_FailsClosed()
    {
        var store = new InMemoryPipelineRunStore();
        var cutover = new CutoverController(50);
        var time = new CanaryAcceptanceTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var service = new CanaryProgressionService(
            store, cutover,
            CanaryAcceptanceHelpers.DefaultOptions, time,
            decisionApplier: new ConfigurableCanaryDecisionApplier(applyResult: null),
            emergencyOverrideStore: new ThrowingOverrideStore());
        await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store, RunId);
        service.InitializeCanary(RunId);

        await service.RollbackAsync(
            RunId, RollbackReason.ModelPerformanceRegression, CancellationToken.None);

        // 本地 0% + 紧急状态（含 PersistPending：Kill Switch 与 DB 均未持久化）
        Assert.AreEqual(0, service.GetCurrentPercentage(RunId));
        var localState = service.GetLocalState(RunId);
        Assert.AreNotEqual(CanaryLocalState.Consistent, localState,
            "Kill Switch 写入失败时不得标记 Consistent（fail-closed）。");
        Assert.IsTrue(localState.HasFlag(CanaryLocalState.LocalEmergencyRollback));
        Assert.IsTrue(localState.HasFlag(CanaryLocalState.PersistPending));
        Assert.IsTrue(localState.HasFlag(CanaryLocalState.OperatorAlertRequired));
    }

    [TestMethod]
    public async Task Rollback_OverrideWriteFails_DbCasSucceeds_KeepsFailClosedUntilResync()
    {
        var store = new InMemoryPipelineRunStore();
        var cutover = new CutoverController(50);
        var time = new CanaryAcceptanceTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var service = new CanaryProgressionService(
            store, cutover,
            CanaryAcceptanceHelpers.DefaultOptions, time,
            decisionApplier: new ConfigurableCanaryDecisionApplier(SuccessResult),
            emergencyOverrideStore: new ThrowingOverrideStore());
        await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store, RunId);
        service.InitializeCanary(RunId);

        await service.RollbackAsync(
            RunId, RollbackReason.ModelPerformanceRegression, CancellationToken.None);

        // DB 已持久化 0%（可恢复的权威回滚）；但 Kill Switch 未持久化 → 回滚后立即处于
        // fail-closed 紧急状态（不标记 Consistent），且不叠加 PersistPending。
        Assert.AreEqual(0, service.GetCurrentPercentage(RunId));
        var localState = service.GetLocalState(RunId);
        Assert.AreNotEqual(CanaryLocalState.Consistent, localState,
            "Kill Switch 写入失败后本地必须立即 fail-closed（不得标记 Consistent）。");
        Assert.IsTrue(localState.HasFlag(CanaryLocalState.LocalEmergencyRollback));
        Assert.IsFalse(localState.HasFlag(CanaryLocalState.PersistPending),
            "DB 已持久化 0%，不应标记 PersistPending。");

        // 无活跃覆盖且 DB 已持久化 → 后续推进不受阻断（DB 0% 为可恢复的权威回滚）。
        time.Advance(TimeSpan.FromSeconds(2));
        var resumed = await service.AdvanceAsync(
            RunId, "t-rbk-fc-001", null,
            CanaryAcceptanceHelpers.HealthyBaseline, CanaryAcceptanceHelpers.HealthyExperiment);
        Assert.AreEqual(CanaryLocalState.Consistent, service.GetLocalState(RunId),
            "无活跃覆盖且 DB 已一致时，推进应重新同步为 Consistent。");
        Assert.AreEqual(CanaryProgressionDecision.Advance, resumed.Decision,
            "DB 已持久化 0% 时推进不应被阻断。");
    }

    // ---------------------------------------------------------------------------
    // 4. 无 Kill Switch 存储：DB CAS 成功 → Consistent（原语义保留，向后兼容）
    // ---------------------------------------------------------------------------
    [TestMethod]
    public async Task Rollback_NoOverrideStore_DbSuccess_KeepsConsistent()
    {
        var store = new InMemoryPipelineRunStore();
        var cutover = new CutoverController(50);
        var time = new CanaryAcceptanceTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var service = new CanaryProgressionService(
            store, cutover,
            CanaryAcceptanceHelpers.DefaultOptions, time,
            decisionApplier: new ConfigurableCanaryDecisionApplier(SuccessResult));
        await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store, RunId);
        service.InitializeCanary(RunId);

        await service.RollbackAsync(
            RunId, RollbackReason.ModelPerformanceRegression, CancellationToken.None);

        Assert.AreEqual(0, service.GetCurrentPercentage(RunId));
        Assert.AreEqual(CanaryLocalState.Consistent, service.GetLocalState(RunId),
            "无 Kill Switch 存储时维持原语义（DB 成功 → Consistent）。");
    }

    // ---------------------------------------------------------------------------
    // 5. 已存在人工覆盖：不覆盖原因；状态照常阻断
    // ---------------------------------------------------------------------------
    [TestMethod]
    public async Task Rollback_OverrideAlreadyExists_DoesNotOverwrite()
    {
        var store = new InMemoryPipelineRunStore();
        var cutover = new CutoverController(50);
        var time = new CanaryAcceptanceTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var overrideStore = new InMemoryCanaryEmergencyOverrideStore();
        await overrideStore.TrySetOverrideAsync(RunId, "人工 Kill：V2 P95 恶化", "ops-oncall");
        var service = new CanaryProgressionService(
            store, cutover,
            CanaryAcceptanceHelpers.DefaultOptions, time,
            decisionApplier: new ConfigurableCanaryDecisionApplier(SuccessResult),
            emergencyOverrideStore: overrideStore);
        await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store, RunId);
        service.InitializeCanary(RunId);

        await service.RollbackAsync(
            RunId, RollbackReason.ModelPerformanceRegression, CancellationToken.None);

        var active = await overrideStore.GetActiveAsync(RunId);
        Assert.IsNotNull(active);
        Assert.AreEqual("人工 Kill：V2 P95 恶化", active!.Reason,
            "已存在的人工覆盖不应被自动回滚覆盖。");
        Assert.AreEqual("ops-oncall", active.OperatorName);
    }

    // ---------------------------------------------------------------------------
    // 6. 生命周期：清除覆盖后推进解除阻断并重新同步为 Consistent
    // ---------------------------------------------------------------------------
    [TestMethod]
    public async Task Advance_KillSwitchCleared_ResumesProgression()
    {
        var store = new InMemoryPipelineRunStore();
        var cutover = new CutoverController(50);
        var time = new CanaryAcceptanceTimeProvider(CanaryAcceptanceHelpers.BaseTime);
        var overrideStore = new InMemoryCanaryEmergencyOverrideStore();
        var service = new CanaryProgressionService(
            store, cutover,
            CanaryAcceptanceHelpers.DefaultOptions, time,
            decisionApplier: new ConfigurableCanaryDecisionApplier(SuccessResult),
            emergencyOverrideStore: overrideStore);
        await CanaryAcceptanceHelpers.CreateScopedCanaryRunAsync(store, RunId);
        service.InitializeCanary(RunId);

        // 自动回滚 → 覆盖保留，推进被阻断
        await service.RollbackAsync(
            RunId, RollbackReason.ModelPerformanceRegression, CancellationToken.None);
        var blocked = await service.AdvanceAsync(
            RunId, "t-rbk-life-001", null,
            CanaryAcceptanceHelpers.HealthyBaseline, CanaryAcceptanceHelpers.HealthyExperiment);
        Assert.AreEqual(CanaryProgressionDecision.Hold, blocked.Decision,
            "覆盖期间推进必须被阻断。");

        // Operator 清除覆盖 → 推进解除阻断并重新同步
        Assert.IsTrue(await overrideStore.TryClearOverrideAsync(RunId, "ops"),
            "清除活跃覆盖应成功。");
        time.Advance(TimeSpan.FromSeconds(2));
        var resumed = await service.AdvanceAsync(
            RunId, "t-rbk-life-002", null,
            CanaryAcceptanceHelpers.HealthyBaseline, CanaryAcceptanceHelpers.HealthyExperiment);
        Assert.AreEqual(CanaryLocalState.Consistent, service.GetLocalState(RunId),
            "清除覆盖后应重新同步为 Consistent（DB 为权威真相源）。");
        Assert.AreEqual(CanaryProgressionDecision.Advance, resumed.Decision,
            "清除覆盖后推进应恢复（健康指标 + 观察期已过）。");
    }

    // ---------------------------------------------------------------------------
    // 辅助
    // ---------------------------------------------------------------------------

    private static readonly CanaryDecisionResult SuccessResult = new()
    {
        Applied = true,
        PreviousPercentage = 50,
        CurrentPercentage = 0,
        NewRevision = 1,
        NewEpoch = 1,
        FailureReason = string.Empty
    };

    /// <summary>可配置的决策应用器：applyResult 为 null 时模拟 DB CAS 失败。</summary>
    private sealed class ConfigurableCanaryDecisionApplier : ICanaryDecisionApplier
    {
        private readonly CanaryDecisionResult? _applyResult;

        public ConfigurableCanaryDecisionApplier(CanaryDecisionResult? applyResult) => _applyResult = applyResult;

        public ValueTask<CanaryDecisionResult> ApplyCanaryDecisionAsync(
            CanaryDecisionRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_applyResult ?? FailResult);

        public ValueTask<CanaryDecisionResult> ApplyCanaryDecisionLocalAsync(
            CanaryDecisionRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_applyResult ?? FailResult);

        public ValueTask<CanaryPipelineState> GetCanaryPipelineStateAsync(
            string runId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new CanaryPipelineState { RunId = runId, Revision = 0, Percentage = 0 });

        public ValueTask<long> GetCurrentEpochAsync(
            string runId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(0L);

        public ValueTask<IReadOnlyList<CanaryPipelineState>> GetAllActivePipelineStatesAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<CanaryPipelineState>>(Array.Empty<CanaryPipelineState>());

        private static readonly CanaryDecisionResult FailResult = new()
        {
            Applied = false,
            PreviousPercentage = 0,
            CurrentPercentage = 0,
            NewRevision = 0,
            NewEpoch = 0,
            FailureReason = "CAS failed（测试注入）"
        };
    }

    /// <summary>TrySetOverrideAsync 抛异常的 Kill Switch 存储（模拟存储故障）。</summary>
    private sealed class ThrowingOverrideStore : ICanaryEmergencyOverrideStore
    {
        public ValueTask<CanaryEmergencyOverride?> GetActiveAsync(
            string runId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<CanaryEmergencyOverride?>(null);

        public ValueTask<IReadOnlyList<CanaryEmergencyOverride>> GetActiveOverridesAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<CanaryEmergencyOverride>>(Array.Empty<CanaryEmergencyOverride>());

        public ValueTask<bool> TrySetOverrideAsync(
            string runId, string reason, string operatorName, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Kill Switch store down（测试注入）");

        public ValueTask<bool> TryClearOverrideAsync(
            string runId, string operatorName, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }
}
