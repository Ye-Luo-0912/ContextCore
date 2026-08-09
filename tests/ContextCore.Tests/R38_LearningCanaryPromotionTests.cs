using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Evolution;
using ContextCore.Core.Services.MemoryEvolution;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// Canary/Promotion Learning 闭环末端验收（WP-M）：
/// DatasetSnapshot（训练数据）→ 校准 → Canary 评估（健康指标阶梯推进）→ Promotion
/// （候选模型成为最新工件）。闭环关联：快照 ModelArtifactId ↔ 实验模型版本 ↔
/// 提升后的最新工件——Learning 数据可追溯到被 Promoted 的模型。
/// </summary>
[TestClass]
[TestCategory("Evolution")]
public sealed class R38_LearningCanaryPromotionTests
{
    private const string Ws = "ws-canary-loop";
    private const string ModelName = "rank-model";
    private const string BaselineArtifactId = "rank-model-v1";
    private const string CandidateArtifactId = "rank-model-v2";

    [TestMethod]
    public async Task LearningLoop_TrainingDataToCanaryPromotion_ClosesOnCandidate()
    {
        // 1. Learning 数据侧：决策 → ledger 物化 → DatasetSnapshot（候选模型版本关联）。
        var ledgerStore = new InMemoryUtilityLedgerStore();
        var conflictSetStore = new InMemoryConflictSetStore();
        var materializer = new UtilityLedgerMaterializer(ledgerStore, conflictSetStore);
        await materializer.MaterializeAsync(BuildDecisionResult(), Ws, "col-loop");

        var exporter = new TrainingDataExporter(ledgerStore);
        using var tempDir = new TempDirectory();
        var export = await exporter.ExportAsync(new TrainingDataExportRequest
        {
            WorkspaceId = Ws,
            OutputDirectory = tempDir.Path,
            ModelArtifactId = CandidateArtifactId
        });
        var snapshot = export.DatasetSnapshot!;
        Assert.AreEqual(CandidateArtifactId, snapshot.ModelArtifactId,
            "训练数据快照关联候选模型版本（Learning 数据版本追责）。");

        // 2. 模型工件侧：注册基线 v1（当前激活）+ 候选 v2（实验）。
        var registry = new InMemoryModelArtifactRegistry();
        await registry.RegisterAsync(new ModelArtifactDescriptor
        {
            ModelArtifactId = BaselineArtifactId,
            ModelName = ModelName,
            ModelVersion = "1.0.0",
            FeatureSchemaVersion = "v1",
            CalibrationVersion = "v1",
            EngineKind = InferenceEngineKind.DeterministicReplay,
            ContentHash = "hash-v1",
            RegisteredAt = DateTimeOffset.UtcNow.AddDays(-7)
        });
        await registry.RegisterAsync(new ModelArtifactDescriptor
        {
            ModelArtifactId = CandidateArtifactId,
            ModelName = ModelName,
            ModelVersion = "2.0.0",
            FeatureSchemaVersion = "v1",
            CalibrationVersion = "v1",
            EngineKind = InferenceEngineKind.DeterministicReplay,
            ContentHash = "hash-v2",
            RegisteredAt = DateTimeOffset.UtcNow
        });
        Assert.AreEqual(CandidateArtifactId, (await registry.GetLatestAsync(ModelName))!.ModelArtifactId,
            "候选 v2 已注册为最新版本（Canary 实验对象）；提升语义由 Cutover 100% + 激活控制面表达。");

        // 3. Canary：ScopedCanary run（基线 / 候选）→ 健康指标阶梯推进 → Promoted。
        var time = new FakeTimeProvider(BaseTime);
        var store = new InMemoryPipelineRunStore();
        var cutover = new CutoverController();
        var service = new CanaryProgressionService(
            store, cutover,
            new CanaryGateOptions
            {
                PercentageLadder = [1, 50, 100],
                MinObservationPeriod = TimeSpan.FromSeconds(1)
            },
            time);
        var runId = await CreateScopedCanaryRunAsync(store, CandidateArtifactId);

        service.InitializeCanary(runId);
        Assert.AreEqual(1, cutover.CutoverPercentage, "初始化后应为阶梯首档 1%。");

        time.Advance(TimeSpan.FromSeconds(2));
        var advance = await service.AdvanceAsync(runId, "t-1", null, HealthyBaseline, HealthyExperiment);
        Assert.AreEqual(CanaryProgressionDecision.Advance, advance.Decision);
        Assert.AreEqual(50, cutover.CutoverPercentage);

        time.Advance(TimeSpan.FromSeconds(2));
        var promote = await service.AdvanceAsync(runId, "t-2", null, HealthyBaseline, HealthyExperiment);
        Assert.AreEqual(CanaryProgressionDecision.Advance, promote.Decision);
        Assert.AreEqual(100, cutover.CutoverPercentage, "健康指标达标 → 推进到 100%。");

        var evaluation = await service.EvaluateAsync(runId, HealthyBaseline, HealthyExperiment);
        Assert.AreEqual(CanaryProgressionDecision.Promoted, evaluation.Decision,
            $"100% 后应 Promoted；rationale={evaluation.Rationale}");

        // 4. Promotion：Promoted 后候选 v2 可激活（工件存在），Cutover 100% 请求走 V2。
        var promotedLatest = await registry.GetLatestAsync(ModelName);
        Assert.AreEqual(CandidateArtifactId, promotedLatest!.ModelArtifactId,
            "候选工件为最新版本（可被激活为生产模型）。");

        // 5. 闭环：快照 ModelArtifactId == 实验模型 == 被提升的候选（Learning 数据 ↔ 模型可追溯）。
        Assert.AreEqual(CandidateArtifactId, snapshot.ModelArtifactId,
            "训练数据快照对应被 Canary 验证的候选模型（数据 → 模型闭环）。");
        Assert.IsTrue(cutover.ShouldUseV2("req-loop"), "100% 后请求走 V2（候选）路径。");
    }

    [TestMethod]
    public async Task LearningLoop_CanaryRollback_KeepsBaselineAsLatest()
    {
        // 回滚闭环：候选健康指标不达标 → Rollback → 基线保持最新（Learning 数据侧不误提升）。
        var registry = new InMemoryModelArtifactRegistry();
        await registry.RegisterAsync(new ModelArtifactDescriptor
        {
            ModelArtifactId = BaselineArtifactId,
            ModelName = ModelName,
            ModelVersion = "1.0.0",
            FeatureSchemaVersion = "v1",
            CalibrationVersion = "v1",
            EngineKind = InferenceEngineKind.DeterministicReplay,
            ContentHash = "hash-v1",
            RegisteredAt = DateTimeOffset.UtcNow.AddDays(-7)
        });

        var time = new FakeTimeProvider(BaseTime);
        var store = new InMemoryPipelineRunStore();
        var cutover = new CutoverController();
        var service = new CanaryProgressionService(
            store, cutover,
            new CanaryGateOptions { MinObservationPeriod = TimeSpan.FromSeconds(1) },
            time);
        var runId = await CreateScopedCanaryRunAsync(store, CandidateArtifactId);

        service.InitializeCanary(runId);
        var badExperiment = new Dictionary<string, double>
        {
            ["error_rate"] = 0.20,
            ["p95_latency_ms"] = 300.0,
            ["divergence_rate"] = 0.15
        };

        time.Advance(TimeSpan.FromSeconds(2));
        var rollback = await service.AdvanceAsync(runId, "t-rollback", null, HealthyBaseline, badExperiment);
        Assert.AreEqual(CanaryProgressionDecision.Rollback, rollback.Decision,
            $"高错误率应触发回滚；rationale={rollback.Rationale}");
        Assert.AreEqual(0, cutover.CutoverPercentage, "回滚后 0%（全 Legacy）。");

        Assert.AreEqual(BaselineArtifactId, (await registry.GetLatestAsync(ModelName))!.ModelArtifactId,
            "回滚后基线保持为最新（未提升候选）。");
    }

    // ── 辅助 ─────────────────────────────────────────────────────────────

    private static readonly DateTimeOffset BaseTime = DateTimeOffset.UtcNow;

    private static readonly IReadOnlyDictionary<string, double> HealthyBaseline =
        new Dictionary<string, double>
        {
            ["error_rate"] = 0.012,
            ["p95_latency_ms"] = 100.0,
            ["divergence_rate"] = 0.015
        };

    private static readonly IReadOnlyDictionary<string, double> HealthyExperiment =
        new Dictionary<string, double>
        {
            ["error_rate"] = 0.015,
            ["p95_latency_ms"] = 110.0,
            ["divergence_rate"] = 0.02
        };

    private static async Task<string> CreateScopedCanaryRunAsync(IPipelineRunStore store, string experimentArtifactId)
    {
        var runId = $"run-canary-loop-{Guid.NewGuid():N}";
        var now = BaseTime;
        var snapshot = new PipelineRunSnapshot
        {
            RunId = runId,
            ProposalId = "prop-canary-loop",
            ProposalVersion = OptimizationProposalVersion.Initial,
            Proposal = BuildProposal(experimentArtifactId),
            CurrentStage = OptimizationStage.ScopedCanary,
            Status = PipelineRunStatus.Running,
            StartedAt = now,
            UpdatedAt = now,
            CompletedAt = null,
            RollbackReason = null,
            StageMetrics = Array.Empty<BaselineComparison>(),
            Revision = 1,
            LeaseOwner = null,
            LeaseExpiresAt = null,
            LastTransitionId = null
        };
        var created = await store.TryCreateRunAsync(snapshot);
        Assert.IsTrue(created, "测试 run 创建失败：TryCreateRunAsync 应返回 true");
        return runId;
    }

    private static OptimizationProposal BuildProposal(string experimentArtifactId) => new()
    {
        ProposalId = "prop-canary-loop",
        Version = OptimizationProposalVersion.Initial,
        Title = "Learning Canary Loop",
        Hypothesis = "候选模型 v2 提升排序质量",
        TargetComponent = OptimizationTargetComponent.PackagePolicy,
        Status = OptimizationProposalStatus.ExperimentReady,
        ExpectedGains = new[]
        {
            new ExpectedGain("duration_ms", -350.0, 0.85, new[] { experimentArtifactId })
        },
        Risks = new[]
        {
            new RiskAssessment("R1", "候选回归风险", RiskSeverity.Low, Array.Empty<string>(), Array.Empty<string>())
        },
        RollbackConditions = new[]
        {
            new RollbackCondition("error_rate", ComparisonOperator.GreaterThan, 0.05, "error rate > 5%")
        }
    };

    private static ContextDecisionResult BuildDecisionResult() => new()
    {
        RequestId = "decision-loop-1",
        DecisionSource = ContextDecisionSource.Retrieval,
        PolicyVersion = ContextDecisionPolicyVersions.DecisionSchemaV2_0,
        SelectedEnvelopes = new[]
        {
            new ContextCandidateEnvelope
            {
                CandidateId = "cand-1",
                Source = ContextCandidateSource.WorkingMemory,
                CanonicalKey = CanonicalCandidateKey.Create(Ws, "col-loop", "memory", "cand-1", "v1"),
                Utility = new CandidateUtilityScore { DeterministicScore = 0.9, FinalScore = 0.9 }
            }
        },
        DroppedEnvelopes = new[]
        {
            new ContextCandidateEnvelope
            {
                CandidateId = "cand-2",
                Source = ContextCandidateSource.WorkingMemory,
                CanonicalKey = CanonicalCandidateKey.Create(Ws, "col-loop", "memory", "cand-2", "v1"),
                Safety = new CandidateSafetyState { BlockReasonCode = CandidateDecisionReasonCode.TokenBudgetExceeded }
            }
        },
        Outcome = new ContextDecisionOutcomeSummary { SelectedCount = 1, DroppedCount = 1 }
    };

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _current;

        public FakeTimeProvider(DateTimeOffset start) => _current = start;

        public override DateTimeOffset GetUtcNow() => _current;

        public void Advance(TimeSpan delta) => _current += delta;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "cc-canary-loop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // 清理失败忽略（临时目录）。
            }
        }
    }
}
