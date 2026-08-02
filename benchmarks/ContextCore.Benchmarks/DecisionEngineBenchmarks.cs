using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Policy;

namespace ContextCore.Benchmarks;

// ===========================================================================
// Decision Engine 微基准
//
// 覆盖：
//   DefaultGlobalAllocator.Allocate（V2.0 基线）
//   DefaultAllocatorV2_1.AllocateWithDiversity（V2.1 主链：section rollover + MMR）
//   MmrDiversityScorer.RerankWithMmr（MMR 重排序微基准）
//   DefaultCanonicalCandidateMerger.Merge（跨 Expert 合并 + 去重）
//   RetrievalResultProjector.Project（V2 → Legacy Retrieval DTO 投影）
//   PackageResultProjector.Project（V2 → Legacy Package DTO 投影）
//
// 数据规模：[Params(10, 100, 1000)] 覆盖小/中/大候选集
// 指标：Mean / Median / StdDev / P95（BenchmarkDotNet 默认）+ Allocated bytes（[MemoryDiagnoser]）
//
// 依赖：MmrDiversityScorer 为 internal static，通过 ContextCore.Core 的
//       InternalsVisibleTo("ContextCore.Benchmarks") 暴露。
// ===========================================================================

/// <summary>
/// Allocator 微基准（V2.0 / V2.1 / MMR）。
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class AllocatorBenchmarks
{
    private const string WorkspaceId = "bench-ws";
    private const string CollectionId = "bench-col";

    [Params(10, 100, 1000)]
    public int CandidateCount { get; set; }

    private IReadOnlyList<ContextCandidateEnvelope> _envelopes = null!;
    private EffectivePolicySnapshot _snapshot = null!;
    private AllocationContext _allocationContext = null!;
    private DiversityOptions _diversityOptions = null!;

    private DefaultGlobalAllocator _v2Allocator = null!;
    private DefaultAllocatorV2_1 _v21Allocator = null!;

    [GlobalSetup]
    public void Setup()
    {
        _envelopes = BuildEnvelopes(CandidateCount);
        _snapshot = BuildSnapshot();
        _allocationContext = new AllocationContext
        {
            Purpose = ContextDecisionPurpose.Retrieval,
            Budget = new BudgetProfile
            {
                ProfileId = "bench-budget",
                DefaultTokenBudget = 8000,
                DefaultTopK = Math.Max(10, CandidateCount)
            },
            MandatoryOverflowPolicy = MandatoryOverflowPolicy.AllowOverflowWithDiagnostic
        };
        _diversityOptions = new DiversityOptions { Lambda = 0.5 };

        _v2Allocator = new DefaultGlobalAllocator();
        _v21Allocator = new DefaultAllocatorV2_1(_v2Allocator);
    }

    // Allocator 基线
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("V20")]
    public AllocationResult Allocate_V20()
        => _v2Allocator.Allocate(_envelopes, _snapshot, _allocationContext);

    // Allocator 主链（section rollover + MMR diversity）
    [Benchmark]
    [BenchmarkCategory("V21")]
    public AllocationResult Allocate_V21_WithDiversity()
        => _v21Allocator.AllocateWithDiversity(_envelopes, _allocationContext, _diversityOptions);

    // MMR 直接调用（绕过 Allocator 测纯 MMR 性能）
    [Benchmark]
    [BenchmarkCategory("MMR")]
    public IReadOnlyList<ContextCandidateEnvelope> Mmr_Rerank()
        => MmrDiversityScorer.RerankWithMmr(_envelopes, lambda: 0.5, topK: Math.Max(10, CandidateCount));

    // === 数据生成 ===

    /// <summary>
    /// 生成 CandidateCount 个候选：前 10% 为 mandatory，其余按 Source 分布。
    /// 候选间存在部分 CanonicalKey 重复（同 entity 不同来源），触发 merger 去重路径。
    /// </summary>
    private static IReadOnlyList<ContextCandidateEnvelope> BuildEnvelopes(int count)
    {
        var envelopes = new List<ContextCandidateEnvelope>(count);
        var rand = new Random(20260725); // 固定种子保证可复现

        for (int i = 0; i < count; i++)
        {
            var isMandatory = i < Math.Max(1, count / 10);
            var source = isMandatory
                ? ContextCandidateSource.Mandatory
                : (i % 4) switch
                {
                    0 => ContextCandidateSource.Lexical,
                    1 => ContextCandidateSource.Semantic,
                    2 => ContextCandidateSource.WorkingMemory,
                    _ => ContextCandidateSource.StableMemory
                };

            // 让 30% 候选共享 entity 模拟跨 channel 重复
            var entityId = (i % 3 == 0 && i > 0) ? $"entity-{i - 1}" : $"entity-{i}";

            var tokens = 50 + rand.Next(0, 300);
            var score = 0.3 + rand.NextDouble() * 0.7;

            envelopes.Add(new ContextCandidateEnvelope
            {
                CandidateId = $"cand-{i}",
                Source = source,
                Type = "bench-type",
                CanonicalKey = CanonicalCandidateKey.Create(
                    workspaceId: WorkspaceId,
                    collectionId: CollectionId,
                    entityKind: source.ToString().ToLowerInvariant(),
                    entityId: entityId,
                    entityVersion: "v1"),
                EstimatedTokens = tokens,
                Safety = new CandidateSafetyState
                {
                    IsMandatory = isMandatory,
                    PassesSafetyGate = true
                },
                Utility = new CandidateUtilityScore
                {
                    DeterministicScore = score,
                    FinalScore = score,
                    ReasonCode = isMandatory ? "mandatory" : "test"
                }
            });
        }

        return envelopes;
    }

    private static EffectivePolicySnapshot BuildSnapshot()
    {
        var bundle = DefaultPolicyBundleFactory.Create();
        return new EffectivePolicySnapshot
        {
            Reference = new ResolvedPolicyReference
            {
                BundleId = bundle.BundleId,
                BundleVersion = bundle.Version,
                BundleContentHash = DefaultResolvedPolicyProvider.DefaultContentHash,
                ActivationEpoch = DefaultResolvedPolicyProvider.DefaultActivationEpoch
            },
            Safety = bundle.Safety,
            Budget = bundle.Budget,
            Routing = bundle.Routing,
            FeatureSchemaVersion = bundle.Policies.DecisionSchemaVersion,
            ResolutionScope = new ContextDecisionScope(WorkspaceId, CollectionId)
        };
    }
}

/// <summary>
/// CanonicalCandidateMerger 微基准。
/// </summary>
[MemoryDiagnoser]
public class CanonicalMergerBenchmarks
{
    private const string WorkspaceId = "bench-ws";
    private const string CollectionId = "bench-col";

    [Params(10, 100, 1000)]
    public int CandidateCount { get; set; }

    private List<ExpertExecutionResult> _expertOutputs = null!;
    private DefaultCanonicalCandidateMerger _merger = null!;

    [GlobalSetup]
    public void Setup()
    {
        _merger = new DefaultCanonicalCandidateMerger();
        _expertOutputs = BuildExpertOutputs(CandidateCount);
    }

    [Benchmark]
    public CandidateWorkingSet Merge()
        => _merger.Merge(_expertOutputs);

    /// <summary>
    /// 构造 7 个 Expert 的输出（对齐生产 Provider 数量），每个 Expert 产出
    /// CandidateCount/7 个候选，其中 20% 跨 Expert 共享 CanonicalKey（触发 union 路径）。
    /// </summary>
    private static List<ExpertExecutionResult> BuildExpertOutputs(int total)
    {
        var perExpert = Math.Max(1, total / 7);
        var results = new List<ExpertExecutionResult>(7);
        var sources = new[]
        {
            ContextCandidateSource.Mandatory,
            ContextCandidateSource.Constraint,
            ContextCandidateSource.Lexical,
            ContextCandidateSource.Semantic,
            ContextCandidateSource.WorkingMemory,
            ContextCandidateSource.StableMemory,
            ContextCandidateSource.Graph
        };

        for (int e = 0; e < 7; e++)
        {
            var envelopes = new List<ContextCandidateEnvelope>(perExpert);
            var materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>();

            for (int i = 0; i < perExpert; i++)
            {
                var globalIdx = e * perExpert + i;
                // 20% 跨 Expert 共享 entity
                var entityId = (globalIdx % 5 == 0 && globalIdx > 0)
                    ? $"shared-entity-{globalIdx % 13}"
                    : $"entity-{globalIdx}";

                var key = CanonicalCandidateKey.Create(
                    WorkspaceId, CollectionId,
                    entityKind: sources[e].ToString().ToLowerInvariant(),
                    entityId: entityId,
                    entityVersion: "v1");

                var tokens = 50 + (globalIdx % 200);
                var score = 0.3 + ((globalIdx % 70) / 100.0);

                var envelope = new ContextCandidateEnvelope
                {
                    CandidateId = $"cand-{globalIdx}",
                    Source = sources[e],
                    Type = "bench-type",
                    CanonicalKey = key,
                    EstimatedTokens = tokens,
                    Safety = new CandidateSafetyState
                    {
                        IsMandatory = sources[e] == ContextCandidateSource.Mandatory,
                        PassesSafetyGate = true
                    },
                    Utility = new CandidateUtilityScore
                    {
                        DeterministicScore = score,
                        FinalScore = score,
                        ReasonCode = "bench"
                    }
                };

                envelopes.Add(envelope);
                materials[key] = new CandidateMaterial
                {
                    Key = key,
                    Content = $"content for {entityId} from {sources[e]}",
                    NativeKind = sources[e].ToString()
                };
            }

            results.Add(new ExpertExecutionResult(envelopes, materials));
        }

        return results;
    }
}

/// <summary>
/// Projector 微基准（Retrieval / Package）。
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ProjectorBenchmarks
{
    private const string WorkspaceId = "bench-ws";
    private const string CollectionId = "bench-col";

    [Params(10, 100, 1000)]
    public int CandidateCount { get; set; }

    private ContextDecisionResult _decisionResult = null!;
    private CandidateWorkingSet _workingSet = null!;

    private RetrievalResultProjector _retrievalProjector = null!;
    private PackageResultProjector _packageProjector = null!;

    [GlobalSetup]
    public void Setup()
    {
        _retrievalProjector = new RetrievalResultProjector();
        _packageProjector = new PackageResultProjector();

        var envelopes = BuildEnvelopes(CandidateCount);
        _workingSet = BuildWorkingSet(envelopes);
        _decisionResult = BuildDecisionResult(envelopes);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Retrieval")]
    public ContextRetrievalResult Project_Retrieval()
        => _retrievalProjector.Project(_decisionResult, _workingSet);

    [Benchmark]
    [BenchmarkCategory("Package")]
    public ContextPackageBuildResult Project_Package()
        => _packageProjector.Project(
            _decisionResult,
            _workingSet,
            new ContextDecisionScope(WorkspaceId, CollectionId));

    // === 数据生成 ===

    private static IReadOnlyList<ContextCandidateEnvelope> BuildEnvelopes(int count)
    {
        var envelopes = new List<ContextCandidateEnvelope>(count);
        var rand = new Random(20260725);

        for (int i = 0; i < count; i++)
        {
            var tokens = 50 + rand.Next(0, 300);
            var score = 0.3 + rand.NextDouble() * 0.7;
            var key = CanonicalCandidateKey.Create(
                WorkspaceId, CollectionId,
                entityKind: "bench",
                entityId: $"entity-{i}",
                entityVersion: "v1");

            envelopes.Add(new ContextCandidateEnvelope
            {
                CandidateId = $"cand-{i}",
                Source = i == 0 ? ContextCandidateSource.Mandatory : ContextCandidateSource.Lexical,
                Type = "bench-type",
                CanonicalKey = key,
                EstimatedTokens = tokens,
                Safety = new CandidateSafetyState { PassesSafetyGate = true },
                Utility = new CandidateUtilityScore
                {
                    DeterministicScore = score,
                    FinalScore = score,
                    ReasonCode = "bench"
                }
            });
        }

        return envelopes;
    }

    private static CandidateWorkingSet BuildWorkingSet(IReadOnlyList<ContextCandidateEnvelope> envelopes)
    {
        var materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(envelopes.Count);
        foreach (var env in envelopes)
        {
            materials[env.CanonicalKey] = new CandidateMaterial
            {
                Key = env.CanonicalKey,
                Content = $"content body for {env.CandidateId}",
                NativeKind = env.Source.ToString()
            };
        }
        return new CandidateWorkingSet
        {
            Envelopes = envelopes,
            Materials = materials
        };
    }

    private static ContextDecisionResult BuildDecisionResult(IReadOnlyList<ContextCandidateEnvelope> envelopes)
    {
        // 选取前一半作为 selected，其余 dropped，模拟真实决策输出
        var selectedCount = envelopes.Count / 2;
        var selected = envelopes.Take(selectedCount).ToList();
        var dropped = envelopes.Skip(selectedCount).ToList();

        var totalTokens = selected.Sum(e => e.EstimatedTokens);

        return new ContextDecisionResult
        {
            RequestId = "bench-request",
            SelectedEnvelopes = selected,
            DroppedEnvelopes = dropped,
            Outcome = new ContextDecisionOutcomeSummary
            {
                SelectedCount = selected.Count,
                DroppedCount = dropped.Count,
                EstimatedTokens = totalTokens,
                TokenBudget = 8000
            },
            PolicyVersion = "bench-policy-v1",
            DecidedAt = DateTimeOffset.UtcNow,
            Purpose = ContextDecisionPurpose.Retrieval,
            RuntimeKind = ContextDecisionRuntimeKind.UnifiedV2
        };
    }
}

// ===========================================================================
// 共享配置：Decision Engine 基准使用 BenchmarkOutputConfig 的导出器集合
// （复用 PackageBuildBenchmarks.Program 的 BenchmarkSwitcher 自动发现本类）
// ===========================================================================
