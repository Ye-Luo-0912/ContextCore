using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Evolution;
using ContextCore.Core.Services.MemoryEvolution;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// Learning 闭环端到端黄金路径（WP-Z）：一条测试覆盖 R31-R43 全部交付——
/// 决策 → 物化 ledger → DatasetSnapshot 快照 → 质量闸门（通过）→ 工件落库 →
/// SnapshotId 重建（Replay）→ Canary 阶梯推进 → Promoted → 模型版本关联。
/// </summary>
[TestClass]
[TestCategory("Learning-Event")]
[TestCategory("Evolution")]
public sealed class R44_LearningGoldenPathTests
{
    private const string Ws = "ws-golden";
    private const string ModelName = "golden-model";
    private const string CandidateArtifactId = "golden-model-v2";

    [TestMethod]
    public async Task GoldenPath_DecisionToPromotion_ClosesEndToEnd()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // 1. 决策 → Learning 物化（ledger + conflict set）。
        var ledgerStore = new InMemoryUtilityLedgerStore();
        var conflictSetStore = new InMemoryConflictSetStore();
        var materializer = new UtilityLedgerMaterializer(ledgerStore, conflictSetStore);
        await materializer.MaterializeAsync(BuildDecisionResult(), Ws, "col-golden", cts.Token);

        var entries = await ledgerStore.QueryAsync(new UtilityLedgerQuery { WorkspaceId = Ws }, cts.Token);
        Assert.AreEqual(2, entries.Count, "决策 2 候选全部物化。");

        // 2. 快照导出 → DatasetSnapshot（完整性 / 内容哈希 / 模型版本关联）。
        var exporter = new TrainingDataExporter(ledgerStore);
        using var tempDir = new TempDirectory();
        var export = await exporter.ExportAsync(new TrainingDataExportRequest
        {
            WorkspaceId = Ws,
            CollectionId = "col-golden",
            OutputDirectory = tempDir.Path,
            ModelArtifactId = CandidateArtifactId
        }, cts.Token);

        var snapshot = export.DatasetSnapshot!;
        Assert.AreEqual(2, snapshot.MaterializedCount);
        Assert.AreEqual(1.0, snapshot.CompletenessRatio!.Value, 0.0001);
        Assert.AreEqual(CandidateArtifactId, snapshot.ModelArtifactId, "训练数据关联候选模型（版本追责）。");

        // 3. 质量闸门：健康数据集 → 通过（可导出/使用）。
        var gate = new LearningDataQualityGate();
        var quality = gate.Evaluate(snapshot, export.PositiveCount, export.NegativeCount);
        Assert.AreEqual(LearningDataQualityVerdict.Passed, quality.Verdict, "健康数据集通过质量闸门。");

        // 4. 工件落库 → SnapshotId 重建（Replay 入口）。
        var artifactStore = new InMemoryLearningArtifactStore();
        await artifactStore.SaveAsync(new DatasetSnapshotArtifact
        {
            Snapshot = snapshot,
            DataFilePath = export.DataFilePath,
            ManifestFilePath = export.ManifestFilePath,
            StoredAt = DateTimeOffset.UtcNow
        }, cts.Token);

        var rebuilt = await artifactStore.GetAsync(Ws, snapshot.SnapshotId, cts.Token);
        Assert.IsNotNull(rebuilt, "按 SnapshotId 重建命中。");
        Assert.AreEqual(snapshot.ContentHash, rebuilt!.Snapshot.ContentHash, "重建内容哈希一致。");

        // 5. Canary：候选模型健康指标阶梯推进 → Promoted（Cutover 100%）。
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var runStore = new InMemoryPipelineRunStore();
        var cutover = new CutoverController();
        var canary = new CanaryProgressionService(
            runStore, cutover,
            new CanaryGateOptions { PercentageLadder = [1, 100], MinObservationPeriod = TimeSpan.FromSeconds(1) },
            time);
        var runId = await CreateScopedCanaryRunAsync(runStore, CandidateArtifactId);

        canary.InitializeCanary(runId);
        time.Advance(TimeSpan.FromSeconds(2));
        var advance = await canary.AdvanceAsync(runId, "t-golden-1", null, HealthyBaseline, HealthyExperiment);
        Assert.AreEqual(CanaryProgressionDecision.Advance, advance.Decision);
        Assert.AreEqual(100, cutover.CutoverPercentage, "健康指标达标 → 推进到 100%。");

        var evaluation = await canary.EvaluateAsync(runId, HealthyBaseline, HealthyExperiment);
        Assert.AreEqual(CanaryProgressionDecision.Promoted, evaluation.Decision, "100% 后 Promoted。");
        Assert.IsTrue(cutover.ShouldUseV2("req-golden"), "Promoted 后请求走 V2（候选）。");

        // 6. 模型版本关联：候选工件存在（可激活为生产模型）——Learning 数据 ↔ 模型闭环。
        var registry = new InMemoryModelArtifactRegistry();
        await registry.RegisterAsync(new ModelArtifactDescriptor
        {
            ModelArtifactId = CandidateArtifactId,
            ModelName = ModelName,
            ModelVersion = "2.0.0",
            FeatureSchemaVersion = "v1",
            CalibrationVersion = "v1",
            EngineKind = InferenceEngineKind.DeterministicReplay,
            ContentHash = "golden-hash",
            RegisteredAt = DateTimeOffset.UtcNow
        }, cts.Token);
        var latest = await registry.GetLatestAsync(ModelName, cts.Token);
        Assert.AreEqual(CandidateArtifactId, latest!.ModelArtifactId,
            "候选为最新工件（Promoted 后可激活）——与快照 ModelArtifactId 一致：{0}。");

        // 黄金路径闭环：快照模型 == Canary 实验模型 == 可激活候选。
        Assert.AreEqual(CandidateArtifactId, snapshot.ModelArtifactId, "黄金路径全链模型版本一致。");
    }

    // ── 辅助 ─────────────────────────────────────────────────────────────

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

    private static ContextDecisionResult BuildDecisionResult() => new()
    {
        RequestId = "decision-golden-1",
        DecisionSource = ContextDecisionSource.Retrieval,
        PolicyVersion = ContextDecisionPolicyVersions.DecisionSchemaV2_0,
        SelectedEnvelopes = new[]
        {
            new ContextCandidateEnvelope
            {
                CandidateId = "cand-sel",
                Source = ContextCandidateSource.WorkingMemory,
                CanonicalKey = CanonicalCandidateKey.Create(Ws, "col-golden", "memory", "cand-sel", "v1"),
                Utility = new CandidateUtilityScore { DeterministicScore = 0.9, FinalScore = 0.9 }
            }
        },
        DroppedEnvelopes = new[]
        {
            new ContextCandidateEnvelope
            {
                CandidateId = "cand-drop",
                Source = ContextCandidateSource.WorkingMemory,
                CanonicalKey = CanonicalCandidateKey.Create(Ws, "col-golden", "memory", "cand-drop", "v1"),
                Safety = new CandidateSafetyState { BlockReasonCode = CandidateDecisionReasonCode.TokenBudgetExceeded }
            }
        },
        Outcome = new ContextDecisionOutcomeSummary { SelectedCount = 1, DroppedCount = 1 }
    };

    private static async Task<string> CreateScopedCanaryRunAsync(IPipelineRunStore store, string experimentArtifactId)
    {
        var runId = $"run-golden-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var snapshot = new PipelineRunSnapshot
        {
            RunId = runId,
            ProposalId = "prop-golden",
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
        Assert.IsTrue(created, "测试 run 创建失败。");
        return runId;
    }

    private static OptimizationProposal BuildProposal(string experimentArtifactId) => new()
    {
        ProposalId = "prop-golden",
        Version = OptimizationProposalVersion.Initial,
        Title = "Golden Path",
        Hypothesis = "候选模型提升排序质量",
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
                System.IO.Path.GetTempPath(), "cc-golden-" + Guid.NewGuid().ToString("N"));
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
