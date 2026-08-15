using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.DecisionEngine.FlowDiagnostics;
using ContextCore.Evaluation.Hosting;

namespace ContextCore.Evaluation.Commands;

public static partial class EvalCommand
{
    /// <summary>
    /// 候选流诊断冒烟：内置固定演示场景覆盖全部漏失归因分类，
    /// 连续计算两次逐位比对验证可复现性；净化报告写入 artifacts/flow-diagnostics/。
    /// 报告只含 ID/通道/结局/分数/token，不泄露正文。
    /// </summary>
    private static async Task ExecuteFlowDiagnosticsSmokeAsync(
        IEvalHost service,
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("artifacts", "flow-diagnostics", "flow-diagnostics-smoke.json");
        var required = CommandHelpers.GetOption(args, "--required") ?? "req-a,req-b,req-c,excluded-cand,req-gate,req-budget,req-notrecalled,req-notgenerated";
        var forbidden = CommandHelpers.GetOption(args, "--forbidden") ?? "forbidden-cand";

        var (request, result) = BuildDemoExecution();
        var requiredIds = required.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var forbiddenIds = forbidden.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var report = CandidatesFlowDiagnosticBuilder.Build(request, result, requiredIds, forbiddenIds);
        var repeat = CandidatesFlowDiagnosticBuilder.Build(request, result, requiredIds, forbiddenIds);
        var determinismPassed = JsonSerializer.Serialize(report) == JsonSerializer.Serialize(repeat);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[FlowDiagnostics] JSON: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[FlowDiagnostics] 确定性（两次计算逐位一致）: {determinismPassed}");
        Console.WriteLine($"[FlowDiagnostics] 候选={report.CandidateCount}（选中 {report.SelectedCount} / 丢弃 {report.DroppedCount}），" +
            $"degraded={report.IsDegraded}，重复候选={report.Duplicates.Count}，语义破坏={report.Violations.Count}");
        foreach (var evidence in report.RequiredEvidence)
        {
            Console.WriteLine($"[FlowDiagnostics]   required {evidence.EvidenceId}: {evidence.Outcome}" +
                (string.IsNullOrEmpty(evidence.ReasonCode) ? string.Empty : $" ({evidence.ReasonCode})") +
                (evidence.Channels.Count > 0 ? $" 通道={string.Join("+", evidence.Channels)}" : string.Empty));
        }
        foreach (var violation in report.Violations)
        {
            Console.WriteLine($"[FlowDiagnostics]   语义破坏 {violation.Kind}: {violation.EvidenceId}");
        }
    }

    /// <summary>构建固定演示场景：覆盖选中/门控/排序/预算/未召回/未生成/排除矛盾/禁止命中/重复/持有丢弃。</summary>
    private static (ContextDecisionRuntimeRequest Request, ContextDecisionExecutionResult Result) BuildDemoExecution()
    {
        const string ws = "ws-demo";
        const string coll = "col-demo";
        var scope = new ContextDecisionScope(ws, coll);

        ContextCandidateEnvelope Env(string id, double score, ContextCandidateSource source,
            CandidateDecisionReasonCode? drop = null, int tokens = 100) =>
            new()
            {
                CandidateId = id,
                Source = source,
                CanonicalKey = CanonicalCandidateKey.Create(ws, coll, "note", id, "v1"),
                Utility = new CandidateUtilityScore { DeterministicScore = score, FinalScore = score },
                TokenCost = new CandidateTokenCost { ContentTokens = tokens, TokenizerId = "unicode-cjk-v1", IsEstimated = false },
                Safety = drop is null
                    ? new CandidateSafetyState()
                    : new CandidateSafetyState { PassesSafetyGate = false, BlockReasonCode = drop.Value }
            };

        var held = Env("held-1", 95, ContextCandidateSource.WorkingMemory, CandidateDecisionReasonCode.TokenBudgetExceeded, 800);
        var lexical = new[]
        {
            Env("req-a", 90, ContextCandidateSource.Lexical),
            Env("req-b", 80, ContextCandidateSource.Lexical, CandidateDecisionReasonCode.ScoreBelowThreshold),
            Env("dup", 70, ContextCandidateSource.Lexical, CandidateDecisionReasonCode.DuplicateSuppressed),
            Env("excluded-cand", 60, ContextCandidateSource.Lexical, CandidateDecisionReasonCode.LifecycleBlocked),
            Env("req-gate", 55, ContextCandidateSource.Lexical, CandidateDecisionReasonCode.LifecycleBlocked),
            Env("req-budget", 88, ContextCandidateSource.Lexical, CandidateDecisionReasonCode.TokenBudgetExceeded, 2000)
        };
        var semantic = new[]
        {
            Env("req-a", 95, ContextCandidateSource.Semantic),
            Env("dup", 85, ContextCandidateSource.Semantic, CandidateDecisionReasonCode.DuplicateSuppressed),
            Env("req-c", 75, ContextCandidateSource.Semantic)
        };
        var forbidden = Env("forbidden-cand", 99, ContextCandidateSource.Mandatory);

        var workingSetEnvelopes = lexical.Concat(semantic).Append(held).Append(forbidden).ToArray();

        var request = new ContextDecisionRuntimeRequest
        {
            RequestId = "demo-flow-1",
            Scope = scope,
            Purpose = ContextDecisionPurpose.Retrieval,
            QueryText = "演示查询：候选流漏失归因",
            TokenBudget = 4000,
            TopK = 10,
            RetrievalInput = new RetrievalInput { ExcludedIds = ["excluded-cand"] },
            SeedCandidates = [held]
        };

        var result = new ContextDecisionExecutionResult
        {
            Decision = new ContextDecisionResult
            {
                RequestId = request.RequestId,
                DecisionSource = ContextDecisionSource.Retrieval,
                Purpose = ContextDecisionPurpose.Retrieval,
                RuntimeKind = ContextDecisionRuntimeKind.UnifiedV2,
                SelectedEnvelopes = [semantic[0], semantic[2], forbidden],
                DroppedEnvelopes = [lexical[1], lexical[2], lexical[3], lexical[4], lexical[5], held]
            },
            WorkingSet = new CandidateWorkingSet
            {
                Envelopes = workingSetEnvelopes,
                Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>()
            },
            Policy = new EffectivePolicySnapshot
            {
                Reference = new ResolvedPolicyReference
                {
                    BundleId = "demo",
                    BundleVersion = "v1",
                    BundleContentHash = "demo-hash",
                    ActivationEpoch = 1
                },
                Safety = new SafetyProfile { ProfileId = "safety-demo" },
                Budget = new BudgetProfile { ProfileId = "budget-demo" },
                Routing = new RoutingProfile { ProfileId = "routing-demo" },
                FeatureSchemaVersion = "v1",
                ResolutionScope = scope
            },
            Routing = new ExpertRoutingDecisionSet { Decisions = Array.Empty<ExpertRoutingDecision>() },
            NormalizedRequest = request,
            RequestSemanticHash = "demo-hash",
            Scope = scope,
            FeatureSchemaVersion = "v1",
            AllocatorVersion = "v2.1",
            IsDegraded = true,
            ProviderReports =
            [
                new ProviderExecutionReport { Kind = ExpertKind.Lexical, Succeeded = true, TimedOut = false, Duration = TimeSpan.FromMilliseconds(3), CandidateCount = lexical.Length },
                new ProviderExecutionReport { Kind = ExpertKind.Semantic, Succeeded = true, TimedOut = false, Duration = TimeSpan.FromMilliseconds(5), CandidateCount = semantic.Length },
                new ProviderExecutionReport { Kind = ExpertKind.Graph, Succeeded = false, TimedOut = true, Duration = TimeSpan.FromSeconds(30), CandidateCount = 0, ErrorCode = "timeout" }
            ],
            ProviderOutputSnapshots =
            [
                new ProviderOutputSnapshot { Kind = ExpertKind.Lexical, Envelopes = lexical, Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(), Succeeded = true, Duration = TimeSpan.FromMilliseconds(3) },
                new ProviderOutputSnapshot { Kind = ExpertKind.Semantic, Envelopes = semantic, Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(), Succeeded = true, Duration = TimeSpan.FromMilliseconds(5) },
                new ProviderOutputSnapshot { Kind = ExpertKind.Graph, Envelopes = Array.Empty<ContextCandidateEnvelope>(), Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(), Succeeded = false, Duration = TimeSpan.FromSeconds(30), ErrorCode = "timeout" }
            ],
            FinalTokenCost = new FinalArtifactTokenCost
            {
                Sections = Array.Empty<SectionTokenCost>(),
                TotalTokens = 320,
                TokenizerId = "unicode-cjk-v1",
                WithinBudget = true,
                BudgetLimit = 4000
            }
        };
        return (request, result);
    }
}
