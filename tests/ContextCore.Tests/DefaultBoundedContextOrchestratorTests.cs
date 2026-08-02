using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.BoundedContext;

namespace ContextCore.Tests;

/// <summary>
/// DefaultBoundedContextOrchestrator 实现测试。
///
/// 覆盖：
///   1. null 输入处理（decision/qualityReport/budget）
///   2. CancellationToken 传递
///   3. 无异常检测到 → WasRepaired=false，不调用 executor
///   4. 异常检测到但预算全 0 → WasRepaired=false，不调用 executor
///   5. 异常检测到 + 预算允许 → 调用 executor 一次（仅取第一条 Diagnosis）
///   6. Executor 返回 WasRepaired=true → FinalDecision=RepairedDecision
///   7. Executor 返回 WasRepaired=false → FinalDecision=原始（不变）
///   8. FinalQualityReport 映射逻辑（repaired/response null/未修复）
///   9. 多异常只触发一次修复（仅第一条 Diagnosis）
///  10. Duration / OrchestrationId / IsSuccess 字段验证
///  11. Detector 和 Executor 接收正确的输入参数
///  12. CancellationToken 传递到 detector 和 executor
/// </summary>
[TestClass]
[TestCategory("R22")]
public sealed class DefaultBoundedContextOrchestratorTests
{
    // =========================================================================
    // 1. null 输入处理
    // =========================================================================

    [TestMethod]
    public async Task OrchestrateAsync_NullDecision_Throws()
    {
        var orchestrator = MakeOrchestrator();
        var report = MakeQualityReport();
        var budget = MakeBudget();

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => orchestrator.OrchestrateAsync(null!, report, budget));
    }

    [TestMethod]
    public async Task OrchestrateAsync_NullQualityReport_Throws()
    {
        var orchestrator = MakeOrchestrator();
        var decision = MakeDecisionResult();
        var budget = MakeBudget();

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => orchestrator.OrchestrateAsync(decision, null!, budget));
    }

    [TestMethod]
    public async Task OrchestrateAsync_NullBudget_Throws()
    {
        var orchestrator = MakeOrchestrator();
        var decision = MakeDecisionResult();
        var report = MakeQualityReport();

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => orchestrator.OrchestrateAsync(decision, report, null!));
    }

    // =========================================================================
    // 2. CancellationToken 传递
    // =========================================================================

    [TestMethod]
    public async Task OrchestrateAsync_PreCancelledToken_Throws()
    {
        var orchestrator = MakeOrchestrator();
        var decision = MakeDecisionResult();
        var report = MakeQualityReport();
        var budget = MakeBudget();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => orchestrator.OrchestrateAsync(decision, report, budget, cts.Token));
    }

    // =========================================================================
    // 3. 无异常检测到 → WasRepaired=false，不调用 executor
    // =========================================================================

    [TestMethod]
    public async Task OrchestrateAsync_NoAnomaliesDetected_NoRepair()
    {
        var detector = new StubDetector(Array.Empty<ContextRepairDiagnosis>());
        var executor = new StubExecutor();
        var orchestrator = new DefaultBoundedContextOrchestrator(detector, executor);

        var decision = MakeDecisionResult();
        var report = MakeQualityReport();
        var budget = MakeBudget();

        var result = await orchestrator.OrchestrateAsync(decision, report, budget);

        Assert.IsFalse(result.WasRepaired);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(0, result.Diagnoses.Count);
        Assert.IsNull(result.RepairResponse);
        Assert.AreSame(decision, result.FinalDecision);
        Assert.AreSame(report, result.FinalQualityReport);
        Assert.AreEqual(0, executor.CallCount, "Executor should not be called when no anomalies detected");
        Assert.AreEqual(1, detector.CallCount, "Detector should be called exactly once");
    }

    // =========================================================================
    // 4. 异常检测到但预算全 0 → WasRepaired=false，不调用 executor
    // =========================================================================

    [TestMethod]
    public async Task OrchestrateAsync_AnomaliesDetectedButZeroBudget_NoRepair()
    {
        var diagnoses = new[] { MakeDiagnosis(ContextRepairReason.PrimaryAnchorUncovered) };
        var detector = new StubDetector(diagnoses);
        var executor = new StubExecutor();
        var orchestrator = new DefaultBoundedContextOrchestrator(detector, executor);

        var decision = MakeDecisionResult();
        var report = MakeQualityReport();
        var zeroBudget = new ContextRepairBudget(); // all zeros

        var result = await orchestrator.OrchestrateAsync(decision, report, zeroBudget);

        Assert.IsFalse(result.WasRepaired);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, result.Diagnoses.Count, "Diagnoses should still be populated");
        Assert.AreEqual(ContextRepairReason.PrimaryAnchorUncovered, result.Diagnoses[0].Reason);
        Assert.IsNull(result.RepairResponse);
        Assert.AreSame(decision, result.FinalDecision);
        Assert.AreEqual(0, executor.CallCount, "Executor should not be called when budget is zero");
    }

    // =========================================================================
    // 5. 异常检测到 + 预算允许 → 调用 executor 一次
    // =========================================================================

    [TestMethod]
    public async Task OrchestrateAsync_AnomaliesAndBudget_CallsExecutorOnce()
    {
        var diagnoses = new[] { MakeDiagnosis(ContextRepairReason.PrimaryAnchorUncovered) };
        var detector = new StubDetector(diagnoses);
        var repairedDecision = MakeDecisionResult(requestId: "req-repaired");
        var executor = new StubExecutor(repairedDecision: repairedDecision, wasRepaired: true);
        var orchestrator = new DefaultBoundedContextOrchestrator(detector, executor);

        var decision = MakeDecisionResult();
        var report = MakeQualityReport();
        var budget = MakeBudget();

        var result = await orchestrator.OrchestrateAsync(decision, report, budget);

        Assert.IsTrue(result.WasRepaired);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, executor.CallCount);
        Assert.AreSame(repairedDecision, result.FinalDecision);
        Assert.IsNotNull(result.RepairResponse);
        Assert.IsTrue(result.RepairResponse!.WasRepaired);
    }

    // =========================================================================
    // 6. Executor 返回 WasRepaired=true → FinalDecision=RepairedDecision
    // =========================================================================

    [TestMethod]
    public async Task OrchestrateAsync_ExecutorRepaired_FinalDecisionIsRepaired()
    {
        var diagnoses = new[] { MakeDiagnosis(ContextRepairReason.HardConstraintMissing) };
        var detector = new StubDetector(diagnoses);
        var repaired = MakeDecisionResult(requestId: "req-after-repair");
        var executor = new StubExecutor(repairedDecision: repaired, wasRepaired: true);
        var orchestrator = new DefaultBoundedContextOrchestrator(detector, executor);

        var original = MakeDecisionResult(requestId: "req-original");
        var result = await orchestrator.OrchestrateAsync(original, MakeQualityReport(), MakeBudget());

        Assert.AreSame(repaired, result.FinalDecision);
        Assert.AreNotSame(original, result.FinalDecision);
    }

    // =========================================================================
    // 7. Executor 返回 WasRepaired=false → FinalDecision=原始（不变）
    // =========================================================================

    [TestMethod]
    public async Task OrchestrateAsync_ExecutorNotRepaired_FinalDecisionUnchanged()
    {
        var diagnoses = new[] { MakeDiagnosis(ContextRepairReason.MustHitMissing) };
        var detector = new StubDetector(diagnoses);
        var executor = new StubExecutor(wasRepaired: false);
        var orchestrator = new DefaultBoundedContextOrchestrator(detector, executor);

        var original = MakeDecisionResult(requestId: "req-original");
        var result = await orchestrator.OrchestrateAsync(original, MakeQualityReport(), MakeBudget());

        Assert.IsFalse(result.WasRepaired);
        Assert.AreSame(original, result.FinalDecision);
        Assert.AreEqual(1, executor.CallCount);
    }

    // =========================================================================
    // 8. FinalQualityReport 映射逻辑
    // =========================================================================

    [TestMethod]
    public async Task OrchestrateAsync_FinalQualityReport_FromExecutorResponse_WhenRepaired()
    {
        var diagnoses = new[] { MakeDiagnosis(ContextRepairReason.SevereRedundancy) };
        var detector = new StubDetector(diagnoses);
        var repairedReport = new PackageQualityReport { ComputedAt = DateTimeOffset.UtcNow };
        var executor = new StubExecutor(
            wasRepaired: true,
            repairedQualityReport: repairedReport);
        var orchestrator = new DefaultBoundedContextOrchestrator(detector, executor);

        var originalReport = MakeQualityReport();
        var result = await orchestrator.OrchestrateAsync(MakeDecisionResult(), originalReport, MakeBudget());

        Assert.AreSame(repairedReport, result.FinalQualityReport);
        Assert.AreNotSame(originalReport, result.FinalQualityReport);
    }

    [TestMethod]
    public async Task OrchestrateAsync_FinalQualityReport_Original_WhenExecutorReturnsNoReport()
    {
        var diagnoses = new[] { MakeDiagnosis(ContextRepairReason.SectionSqueezeAnomaly) };
        var detector = new StubDetector(diagnoses);
        // Executor returns WasRepaired=true but RepairedQualityReport=null
        var executor = new StubExecutor(wasRepaired: true, repairedQualityReport: null);
        var orchestrator = new DefaultBoundedContextOrchestrator(detector, executor);

        var originalReport = MakeQualityReport();
        var result = await orchestrator.OrchestrateAsync(MakeDecisionResult(), originalReport, MakeBudget());

        Assert.AreSame(originalReport, result.FinalQualityReport);
    }

    [TestMethod]
    public async Task OrchestrateAsync_FinalQualityReport_Original_WhenNotRepaired()
    {
        var diagnoses = new[] { MakeDiagnosis(ContextRepairReason.TokenUtilizationTooLow) };
        var detector = new StubDetector(diagnoses);
        var executor = new StubExecutor(wasRepaired: false);
        var orchestrator = new DefaultBoundedContextOrchestrator(detector, executor);

        var originalReport = MakeQualityReport();
        var result = await orchestrator.OrchestrateAsync(MakeDecisionResult(), originalReport, MakeBudget());

        Assert.AreSame(originalReport, result.FinalQualityReport);
    }

    // =========================================================================
    // 9. 多异常只触发一次修复（仅第一条 Diagnosis）
    // =========================================================================

    [TestMethod]
    public async Task OrchestrateAsync_MultipleAnomalies_OnlyFirstTriggersRepair()
    {
        var diagnoses = new[]
        {
            MakeDiagnosis(ContextRepairReason.PrimaryAnchorUncovered),
            MakeDiagnosis(ContextRepairReason.HardConstraintMissing),
            MakeDiagnosis(ContextRepairReason.MustHitMissing)
        };
        var detector = new StubDetector(diagnoses);
        var executor = new StubExecutor(wasRepaired: true);
        var orchestrator = new DefaultBoundedContextOrchestrator(detector, executor);

        var result = await orchestrator.OrchestrateAsync(MakeDecisionResult(), MakeQualityReport(), MakeBudget());

        Assert.AreEqual(1, executor.CallCount, "Executor should be called only ONCE (bounded repair)");
        Assert.AreEqual(3, result.Diagnoses.Count, "All diagnoses should be reported in result");
        Assert.IsNotNull(executor.LastRequest);
        Assert.AreEqual(ContextRepairReason.PrimaryAnchorUncovered, executor.LastRequest!.Diagnosis.Reason,
            "First diagnosis should be passed to executor (priority order)");
    }

    // =========================================================================
    // 10. Duration / OrchestrationId / IsSuccess 字段验证
    // =========================================================================

    [TestMethod]
    public async Task OrchestrateAsync_OrchestrationId_HasCorrectPrefix()
    {
        var orchestrator = MakeOrchestrator();
        var result = await orchestrator.OrchestrateAsync(
            MakeDecisionResult(), MakeQualityReport(), MakeBudget());

        Assert.IsTrue(result.OrchestrationId.StartsWith("orch-", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OrchestrateAsync_Duration_NonNegative()
    {
        var orchestrator = MakeOrchestrator();
        var result = await orchestrator.OrchestrateAsync(
            MakeDecisionResult(), MakeQualityReport(), MakeBudget());

        Assert.IsTrue(result.Duration >= TimeSpan.Zero);
        Assert.IsTrue(result.CompletedAt >= result.StartedAt);
    }

    [TestMethod]
    public async Task OrchestrateAsync_IsSuccess_TrueWhenNoRepairNeeded()
    {
        var detector = new StubDetector(Array.Empty<ContextRepairDiagnosis>());
        var orchestrator = new DefaultBoundedContextOrchestrator(detector, new StubExecutor());

        var result = await orchestrator.OrchestrateAsync(
            MakeDecisionResult(), MakeQualityReport(), MakeBudget());

        Assert.IsTrue(result.IsSuccess);
    }

    [TestMethod]
    public async Task OrchestrateAsync_IsSuccess_FalseWhenExecutorFails()
    {
        var diagnoses = new[] { MakeDiagnosis(ContextRepairReason.LifecycleConflictUnresolved) };
        var detector = new StubDetector(diagnoses);
        var executor = new StubExecutor(isSuccess: false, wasRepaired: false);
        var orchestrator = new DefaultBoundedContextOrchestrator(detector, executor);

        var result = await orchestrator.OrchestrateAsync(
            MakeDecisionResult(), MakeQualityReport(), MakeBudget());

        Assert.IsFalse(result.IsSuccess);
        Assert.IsFalse(result.WasRepaired);
        Assert.IsNotNull(result.RepairResponse);
        Assert.IsFalse(result.RepairResponse!.IsSuccess);
    }

    // =========================================================================
    // 11. Detector 和 Executor 接收正确的输入参数
    // =========================================================================

    [TestMethod]
    public async Task OrchestrateAsync_DetectorReceivesOriginalDecisionAndReport()
    {
        var detector = new StubDetector(Array.Empty<ContextRepairDiagnosis>());
        var orchestrator = new DefaultBoundedContextOrchestrator(detector, new StubExecutor());

        var decision = MakeDecisionResult(requestId: "req-detector-test");
        var report = MakeQualityReport();
        await orchestrator.OrchestrateAsync(decision, report, MakeBudget());

        Assert.IsNotNull(detector.LastDecision);
        Assert.AreSame(decision, detector.LastDecision);
        Assert.IsNotNull(detector.LastQualityReport);
        Assert.AreSame(report, detector.LastQualityReport);
    }

    [TestMethod]
    public async Task OrchestrateAsync_ExecutorReceivesBudgetAndOriginalDecision()
    {
        var diagnoses = new[] { MakeDiagnosis(ContextRepairReason.PrimaryAnchorUncovered) };
        var detector = new StubDetector(diagnoses);
        var executor = new StubExecutor(wasRepaired: true);
        var orchestrator = new DefaultBoundedContextOrchestrator(detector, executor);

        var decision = MakeDecisionResult(requestId: "req-budget-test");
        var budget = new ContextRepairBudget
        {
            MaxAdditionalStoreCalls = 3,
            MaxAdditionalCandidates = 5,
            MaxAdditionalTokens = 1000,
            MaxAdditionalLatency = TimeSpan.FromSeconds(2)
        };
        await orchestrator.OrchestrateAsync(decision, MakeQualityReport(), budget);

        Assert.IsNotNull(executor.LastRequest);
        Assert.AreSame(decision, executor.LastRequest!.OriginalDecision);
        Assert.AreEqual(3, executor.LastRequest.Budget.MaxAdditionalStoreCalls);
        Assert.AreEqual(5, executor.LastRequest.Budget.MaxAdditionalCandidates);
        Assert.AreEqual(1000, executor.LastRequest.Budget.MaxAdditionalTokens);
        Assert.AreEqual(TimeSpan.FromSeconds(2), executor.LastRequest.Budget.MaxAdditionalLatency);
        Assert.AreEqual(ContextRepairReason.PrimaryAnchorUncovered, executor.LastRequest.Diagnosis.Reason);
    }

    // =========================================================================
    // 12. CancellationToken 传递到 detector 和 executor
    // =========================================================================

    [TestMethod]
    public async Task OrchestrateAsync_CancellationToken_PassedToDetectorAndExecutor()
    {
        var detectorToken = new CancellationTokenSource();
        var executorToken = new CancellationTokenSource();
        var diagnoses = new[] { MakeDiagnosis(ContextRepairReason.PrimaryAnchorUncovered) };
        var detector = new StubDetector(diagnoses, onCall: ct =>
        {
            detectorToken.Token.Register(() => { });
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        });
        var executor = new StubExecutor(wasRepaired: true, onCall: ct =>
        {
            executorToken.Token.Register(() => { });
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        });
        var orchestrator = new DefaultBoundedContextOrchestrator(detector, executor);

        using var cts = new CancellationTokenSource();
        await orchestrator.OrchestrateAsync(
            MakeDecisionResult(), MakeQualityReport(), MakeBudget(), cts.Token);

        // 未取消 → 应正常完成
        Assert.AreEqual(1, detector.CallCount);
        Assert.AreEqual(1, executor.CallCount);
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static DefaultBoundedContextOrchestrator MakeOrchestrator()
    {
        var detector = new StubDetector(Array.Empty<ContextRepairDiagnosis>());
        var executor = new StubExecutor();
        return new DefaultBoundedContextOrchestrator(detector, executor);
    }

    private static ContextDecisionResult MakeDecisionResult(string requestId = "req-1")
    {
        return new ContextDecisionResult
        {
            RequestId = requestId,
            DecisionSource = ContextDecisionSource.Package,
            SelectedEnvelopes = new[]
            {
                new ContextCandidateEnvelope
                {
                    CandidateId = "c1",
                    CanonicalKey = CanonicalCandidateKey.Create(
                        workspaceId: "test-ws",
                        collectionId: "test-col",
                        entityKind: "test-entity",
                        entityId: "c1",
                        entityVersion: "v1"),
                    Source = ContextCandidateSource.Semantic,
                    WorkspaceId = "ws-test",
                    CollectionId = "col-test"
                }
            },
            PolicyVersion = ContextDecisionPolicyVersions.DecisionSchemaV2_0,
            ModelEnabled = false
        };
    }

    private static PackageQualityReport MakeQualityReport()
    {
        return new PackageQualityReport
        {
            AnchorCoverage = new PackageQualityMetric { Name = "AnchorCoverage", Score = 1.0 },
            HardConstraintSatisfaction = new PackageQualityMetric { Name = "HardConstraintSatisfaction", Score = 1.0 },
            RequiredItemCoverage = new PackageQualityMetric { Name = "RequiredItemCoverage", Score = 1.0 },
            Redundancy = new PackageQualityMetric { Name = "Redundancy", Score = 1.0 },
            ProvenanceCompleteness = new PackageQualityMetric { Name = "ProvenanceCompleteness", Score = 1.0 },
            LifecycleRisk = new PackageQualityMetric { Name = "LifecycleRisk", Score = 1.0 },
            TokenEfficiency = new PackageQualityMetric { Name = "TokenEfficiency", Score = 1.0 },
            SectionBalance = new PackageQualityMetric { Name = "SectionBalance", Score = 1.0 },
            OverallScore = 1.0,
            ComputedAt = DateTimeOffset.UtcNow
        };
    }

    private static ContextRepairBudget MakeBudget()
    {
        return new ContextRepairBudget
        {
            MaxAdditionalStoreCalls = 2,
            MaxAdditionalCandidates = 5,
            MaxAdditionalTokens = 500,
            MaxAdditionalLatency = TimeSpan.FromSeconds(1)
        };
    }

    private static ContextRepairDiagnosis MakeDiagnosis(ContextRepairReason reason)
    {
        return new ContextRepairDiagnosis
        {
            DiagnosisId = $"diag-{Guid.NewGuid():N}",
            DecisionRequestId = "req-1",
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            Reason = reason,
            ReasonDetail = $"{reason}=triggered",
            DiagnosedAt = DateTimeOffset.UtcNow
        };
    }

    // =========================================================================
    // Stub detector / executor
    // =========================================================================

    private sealed class StubDetector : IContextRepairDetector
    {
        private readonly IReadOnlyList<ContextRepairDiagnosis> _diagnoses;
        private readonly Action<CancellationToken>? _onCall;

        public int CallCount { get; private set; }
        public ContextDecisionResult? LastDecision { get; private set; }
        public PackageQualityReport? LastQualityReport { get; private set; }

        public StubDetector(IReadOnlyList<ContextRepairDiagnosis> diagnoses, Action<CancellationToken>? onCall = null)
        {
            _diagnoses = diagnoses;
            _onCall = onCall;
        }

        public Task<IReadOnlyList<ContextRepairDiagnosis>> DetectAsync(
            ContextDecisionResult decision,
            PackageQualityReport? qualityReport,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastDecision = decision;
            LastQualityReport = qualityReport;
            _onCall?.Invoke(cancellationToken);
            return Task.FromResult(_diagnoses);
        }
    }

    private sealed class StubExecutor : IContextRepairExecutor
    {
        private readonly ContextDecisionResult _repairedDecision;
        private readonly PackageQualityReport? _repairedQualityReport;
        private readonly bool _wasRepaired;
        private readonly bool _isSuccess;
        private readonly Action<CancellationToken>? _onCall;

        public int CallCount { get; private set; }
        public ContextRepairRequest? LastRequest { get; private set; }

        public StubExecutor(
            ContextDecisionResult? repairedDecision = null,
            bool wasRepaired = false,
            bool isSuccess = true,
            PackageQualityReport? repairedQualityReport = null,
            Action<CancellationToken>? onCall = null)
        {
            _repairedDecision = repairedDecision ?? new ContextDecisionResult
            {
                RequestId = "req-stub-repaired",
                DecisionSource = ContextDecisionSource.Package,
                PolicyVersion = ContextDecisionPolicyVersions.DecisionSchemaV2_0
            };
            _wasRepaired = wasRepaired;
            _isSuccess = isSuccess;
            _repairedQualityReport = repairedQualityReport;
            _onCall = onCall;
        }

        public Task<ContextRepairResponse> ExecuteAsync(
            ContextRepairRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            _onCall?.Invoke(cancellationToken);
            return Task.FromResult(new ContextRepairResponse
            {
                RepairRequestId = request.RepairRequestId,
                IsSuccess = _isSuccess,
                WasRepaired = _wasRepaired,
                RepairedDecision = _repairedDecision,
                RepairedQualityReport = _repairedQualityReport,
                ConsumedBudget = request.Budget,
                CompletedAt = DateTimeOffset.UtcNow
            });
        }
    }
}
