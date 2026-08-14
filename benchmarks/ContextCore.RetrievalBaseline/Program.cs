using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Policy;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.RetrievalBaseline;

// ===========================================================================
// RF-5：重建多问句召回性能基线
//
// 测量 QueryTexts 多问句召回链路的固定维度扫描：
//   QueryTexts 数量：1 / 4 / 8
//   TopK：10 / 50 / 100
//   Held ID 数量：0 / 10 / 100
//   Provider：InMemory / FileSystem
//   Mode：lexical-only / semantic-only / combined
// 记录：p50 / p95 / 吞吐(ops/s) / 分配字节 / embedding 次数 / vector search 次数 /
//       存储 roundtrip 次数 / 返回有效候选数 / 欠召回数。
// Postgres 维度需要真实数据库与连接串，本基线的可复现运行只覆盖内存与文件系统
// 两个 provider（SQL 路径的 roundtrip 与连接池行为需在集成环境另行测量）。
// 用法：dotnet run -c Release --project benchmarks/ContextCore.RetrievalBaseline
// ===========================================================================

internal static class Program
{
    private const string WorkspaceId = "bench-ws";
    private const string CollectionId = "bench-col";
    private const int ItemCount = 1200;
    private const int KeywordCount = 8;

    private static readonly int[] QueryCounts = [1, 4, 8];
    private static readonly int[] TopKs = [10, 50, 100];
    private static readonly int[] HeldCounts = [0, 10, 100];
    private static readonly string[] Providers = ["InMemory", "FileSystem"];
    private static readonly string[] Modes = ["lexical-only", "semantic-only", "combined"];

    private static async Task<int> Main()
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var resultsDir = Path.Combine("benchmarks", "results", "results");
        Directory.CreateDirectory(resultsDir);
        var csvPath = Path.Combine(resultsDir, $"multiquery-recall-baseline-{timestamp}.csv");

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine($"# 多问句召回性能基线 {timestamp}");
        Console.WriteLine($"# 数据集：{ItemCount} 条上下文 / {KeywordCount} 组关键词 / 8 维确定性向量");
        Console.WriteLine("# 机器：" + Environment.MachineName + " / " + RuntimeInformation.OSDescription);
        Console.WriteLine();

        var sb = new StringBuilder();
        sb.AppendLine("provider,query_count,top_k,held,mode,p50_ms,p95_ms,mean_ms,ops_per_sec,alloc_bytes_per_op,embedding_calls_per_op,vector_search_calls_per_op,storage_roundtrips_per_op,valid_candidates_per_op,under_recall_per_op");

        foreach (var provider in Providers)
        {
            var stores = await BuildStoresAsync(provider);
            try
            {
                foreach (var queryCount in QueryCounts)
                {
                    foreach (var topK in TopKs)
                    {
                        foreach (var held in HeldCounts)
                        {
                            foreach (var mode in Modes)
                            {
                                var row = await MeasureAsync(provider, queryCount, topK, held, mode, stores);
                                sb.AppendLine(row.ToCsv());
                                Console.WriteLine(row.ToTable());
                            }
                        }
                    }
                }
            }
            finally
            {
                stores.Dispose();
            }
        }

        await File.WriteAllTextAsync(csvPath, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine();
        Console.WriteLine($"CSV 已写入：{csvPath}");
        return 0;
    }

    // ------------------------------------------------------------------
    // 数据集与存储
    // ------------------------------------------------------------------

    private sealed class BenchStores : IDisposable
    {
        public required IContextStore Context;
        public required IVectorStore Vector;
        public required CountingContextStore CountingContext;
        public required CountingVectorStore CountingVector;
        public required Action DisposeInner;
        public void Dispose() => DisposeInner();
    }

    private static async Task<BenchStores> BuildStoresAsync(string provider)
    {
        if (provider == "InMemory")
        {
            var context = new InMemoryContextStore();
            var vector = new InMemoryVectorStore();
            await PopulateAsync(context, vector);
            return new BenchStores
            {
                Context = context,
                Vector = vector,
                CountingContext = new CountingContextStore(context),
                CountingVector = new CountingVectorStore(vector),
                DisposeInner = () => { }
            };
        }

        var root = Path.Combine(
            Path.GetTempPath(), "contextcore-retrieval-baseline", Guid.NewGuid().ToString("N"));
        var fileContext = new FileContextStore(new FileStorageOptions { RootPath = root });
        var fileVector = new FileVectorStore(new FileStorageOptions { RootPath = root });
        await PopulateAsync(fileContext, fileVector);
        return new BenchStores
        {
            Context = fileContext,
            Vector = fileVector,
            CountingContext = new CountingContextStore(fileContext),
            CountingVector = new CountingVectorStore(fileVector),
            DisposeInner = () =>
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                catch
                {
                    // 清理失败不阻断基线输出
                }
            }
        };
    }

    private static async Task PopulateAsync(IContextStore context, IVectorStore vector)
    {
        var baseTime = DateTimeOffset.UtcNow.AddDays(-30);
        for (var i = 0; i < ItemCount; i++)
        {
            var id = $"doc-{i:D5}";
            await context.SaveAsync(new ContextItem
            {
                Id = id,
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                Title = $"文档 alpha-{i % KeywordCount} 主题",
                Content = string.Empty,
                Type = "note",
                Metadata = new Dictionary<string, string>
                {
                    [ContentMetadataKeys.ContentLength] = "400"
                },
                CreatedAt = baseTime.AddSeconds(i),
                UpdatedAt = baseTime.AddSeconds(i)
            });

            await vector.UpsertAsync(new VectorRecord
            {
                Id = $"vec-{i:D5}",
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                SourceId = id,
                SourceKind = "context",
                ModelName = "baseline-fixed",
                Dimensions = 8,
                Vector = BasisVector(i % KeywordCount),
                ContentHash = id,
                CreatedAt = baseTime.AddSeconds(i),
                UpdatedAt = baseTime.AddSeconds(i)
            });
        }
    }

    /// <summary>关键字索引对应的 8 维单位基向量（one-hot），保证同一关键词的向量相似度最高。</summary>
    private static float[] BasisVector(int index)
    {
        var v = new float[8];
        v[index] = 1f;
        return v;
    }

    // ------------------------------------------------------------------
    // 单组合测量
    // ------------------------------------------------------------------

    private static async Task<Row> MeasureAsync(
        string provider,
        int queryCount,
        int topK,
        int held,
        string mode,
        BenchStores stores)
    {
        var queryTexts = Enumerable.Range(0, queryCount).Select(k => $"alpha-{k}").ToArray();
        var heldIds = Enumerable.Range(0, held)
            .Select(i => $"doc-{i:D5}")
            .ToArray();
        var heldSet = new HashSet<string>(heldIds, StringComparer.OrdinalIgnoreCase);

        var lexical = new LexicalCandidateProvider(stores.CountingContext, tokenizerResolver: null);
        var embedding = new FixedEmbeddingProvider();
        var semantic = new SemanticCandidateProvider(
            stores.CountingContext, memoryStore: null, embeddingProvider: embedding,
            vectorStore: stores.CountingVector, tokenizerResolver: null);

        // 预热：让 JIT / PGO / 文件缓存进入稳定状态。
        for (var i = 0; i < 30; i++)
        {
            await ExecuteOnceAsync(mode, queryCount, topK, heldIds, lexical, semantic, stores);
        }
        stores.CountingContext.Reset();
        stores.CountingVector.Reset();
        embedding.Reset();

        var iterations = provider == "InMemory" ? 120 : 40;
        var timings = new List<double>(iterations);
        var allocs = new List<long>(iterations);
        var validSamples = new List<int>(iterations);
        var underSamples = new List<int>(iterations);

        for (var i = 0; i < iterations; i++)
        {
            var beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            var result = await ExecuteOnceAsync(mode, queryCount, topK, heldIds, lexical, semantic, stores);
            sw.Stop();

            timings.Add(sw.Elapsed.TotalMilliseconds);
            allocs.Add(GC.GetAllocatedBytesForCurrentThread() - beforeAlloc);

            var validNew = result.Envelopes.Count(e => !heldSet.Contains(e.CanonicalKey.EntityId));
            validSamples.Add(validNew);
            var expected = Math.Min(topK, ItemCount - heldSet.Count);
            underSamples.Add(Math.Max(0, expected - validNew));
        }

        var calls = await CaptureCallCountsAsync(stores, embedding, iterations);
        return new Row
        {
            Provider = provider,
            QueryCount = queryCount,
            TopK = topK,
            Held = held,
            Mode = mode,
            P50Ms = Percentile(timings, 0.50),
            P95Ms = Percentile(timings, 0.95),
            MeanMs = timings.Average(),
            OpsPerSec = 1000.0 / timings.Average(),
            AllocBytesPerOp = allocs.Average(),
            EmbeddingCallsPerOp = calls.Embedding,
            VectorSearchCallsPerOp = calls.VectorSearch,
            StorageRoundtripsPerOp = calls.Storage,
            ValidCandidatesPerOp = validSamples.Average(),
            UnderRecallPerOp = underSamples.Average()
        };
    }

    private static async Task<ExpertExecutionResult> ExecuteOnceAsync(
        string mode,
        int queryCount,
        int topK,
        IReadOnlyList<string> heldIds,
        LexicalCandidateProvider lexical,
        SemanticCandidateProvider semantic,
        BenchStores stores)
    {
        var request = BuildRequest(queryCount, topK, heldIds);
        if (mode == "lexical-only")
        {
            return await lexical.ExecuteAsync(BuildContext(request, RetrievalExpert.Lexical));
        }
        if (mode == "semantic-only")
        {
            return await semantic.ExecuteAsync(BuildContext(request, RetrievalExpert.Semantic));
        }

        // combined：两条通道分别召回后按 CanonicalKey 合并去重。
        var l = await lexical.ExecuteAsync(BuildContext(request, RetrievalExpert.Lexical));
        var s = await semantic.ExecuteAsync(BuildContext(request, RetrievalExpert.Semantic));
        var merged = new List<ContextCandidateEnvelope>(l.Envelopes.Count + s.Envelopes.Count);
        var seen = new HashSet<CanonicalCandidateKey>();
        foreach (var envelope in l.Envelopes.Concat(s.Envelopes))
        {
            if (seen.Add(envelope.CanonicalKey))
            {
                merged.Add(envelope);
            }
        }
        return new ExpertExecutionResult(merged, new Dictionary<CanonicalCandidateKey, CandidateMaterial>());
    }

    private static ContextDecisionRuntimeRequest BuildRequest(
        int queryCount, int topK, IReadOnlyList<string> heldIds)
    {
        var seeds = heldIds
            .Select(id => new ContextCandidateEnvelope
            {
                CandidateId = id,
                Source = ContextCandidateSource.Mandatory,
                CanonicalKey = CanonicalCandidateKey.Create(WorkspaceId, CollectionId, "context", id, "v1")
            })
            .ToArray();
        return new ContextDecisionRuntimeRequest
        {
            RequestId = "bench-req",
            Scope = new ContextDecisionScope(WorkspaceId, CollectionId),
            Purpose = ContextDecisionPurpose.Retrieval,
            TokenBudget = 8192,
            TopK = topK,
            SeedCandidates = seeds,
            RetrievalInput = new RetrievalInput
            {
                IncludeContent = false,
                QueryTexts = Enumerable.Range(0, queryCount).Select(k => $"alpha-{k}").ToArray()
            }
        };
    }

    private static CandidateProviderContext BuildContext(
        ContextDecisionRuntimeRequest request, RetrievalExpert expert)
    {
        var bundle = DefaultPolicyBundleFactory.Create();
        var snapshot = new EffectivePolicySnapshot
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
        return new CandidateProviderContext(
            Request: request,
            Policy: snapshot,
            Routing: new ExpertRoutingDecision
            {
                Expert = expert,
                Enabled = true,
                // ResolveTake 优先读 Routing.TopK，必须透传请求的 TopK 才能让维度生效。
                TopK = request.TopK,
                TokenBudget = snapshot.Budget.DefaultTokenBudget,
                Weight = 1.0,
                ReasonCode = "baseline"
            },
            AdaptationContext: new CandidateAdaptationContext
            {
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                ObservedAt = DateTimeOffset.UtcNow
            });
    }

    // ------------------------------------------------------------------
    // 计数与统计
    // ------------------------------------------------------------------

    private static Task<(int Embedding, int VectorSearch, int Storage)> CaptureCallCountsAsync(
        BenchStores stores, FixedEmbeddingProvider embedding, int iterations)
    {
        // 计数在测量循环内累积，除以迭代数得到每次操作的平均调用次数。
        var storageCalls = stores.CountingContext.QueryCalls + stores.CountingContext.BatchCalls;
        var vectorCalls = stores.CountingVector.SearchCalls;
        var embeddingCalls = embedding.CallCount;
        return Task.FromResult((
            embeddingCalls / Math.Max(1, iterations),
            vectorCalls / Math.Max(1, iterations),
            storageCalls / Math.Max(1, iterations)));
    }

    private static double Percentile(IReadOnlyList<double> values, double quantile)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return 0;
        var index = (int)Math.Ceiling(quantile * (sorted.Length - 1));
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    // ------------------------------------------------------------------
    // 计数包装
    // ------------------------------------------------------------------

    private sealed class CountingContextStore : IContextStore, IContextStoreBatchLookup
    {
        private readonly IContextStore _inner;
        private readonly IContextStoreBatchLookup _batch;

        public int QueryCalls;
        public int BatchCalls;

        public CountingContextStore(IContextStore inner)
        {
            _inner = inner;
            _batch = (IContextStoreBatchLookup)inner;
        }

        public void Reset()
        {
            QueryCalls = 0;
            BatchCalls = 0;
        }

        public Task SaveAsync(ContextItem item, CancellationToken cancellationToken = default)
            => _inner.SaveAsync(item, cancellationToken);

        public Task<ContextItem?> GetAsync(
            string workspaceId, string collectionId, string id,
            CancellationToken cancellationToken = default)
            => _inner.GetAsync(workspaceId, collectionId, id, cancellationToken);

        public Task<IReadOnlyList<ContextItem>> QueryAsync(
            ContextQuery query, CancellationToken cancellationToken = default)
        {
            QueryCalls++;
            return _inner.QueryAsync(query, cancellationToken);
        }

        public Task DeleteAsync(
            string workspaceId, string collectionId, string id,
            CancellationToken cancellationToken = default)
            => _inner.DeleteAsync(workspaceId, collectionId, id, cancellationToken);

        public Task<IReadOnlyList<ContextItem>> BatchGetAsync(
            string workspaceId, string collectionId, IReadOnlyList<string> ids,
            CancellationToken cancellationToken = default)
        {
            BatchCalls++;
            return _batch.BatchGetAsync(workspaceId, collectionId, ids, cancellationToken);
        }
    }

    private sealed class CountingVectorStore : IVectorStore
    {
        private readonly IVectorStore _inner;

        public int SearchCalls;

        public CountingVectorStore(IVectorStore inner) => _inner = inner;

        public void Reset() => SearchCalls = 0;

        public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
            VectorQuery query, CancellationToken cancellationToken = default)
        {
            SearchCalls++;
            return _inner.SearchAsync(query, cancellationToken);
        }

        public Task UpsertAsync(VectorRecord record, CancellationToken cancellationToken = default)
            => _inner.UpsertAsync(record, cancellationToken);

        public Task<VectorRecord?> GetAsync(
            string workspaceId, string vectorId, CancellationToken cancellationToken = default)
            => _inner.GetAsync(workspaceId, vectorId, cancellationToken);

        public Task DeleteAsync(
            string workspaceId, string vectorId, CancellationToken cancellationToken = default)
            => _inner.DeleteAsync(workspaceId, vectorId, cancellationToken);
    }

    private sealed class FixedEmbeddingProvider : IEmbeddingProvider
    {
        public int CallCount;

        public void Reset() => CallCount = 0;

        public Task<EmbeddingResult> EmbedAsync(
            EmbeddingRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            var vectors = request.Inputs.Select(input => new EmbeddingVector
            {
                InputId = input.Id,
                SourceRef = string.IsNullOrWhiteSpace(input.SourceRef) ? input.Id : input.SourceRef,
                Values = VectorFor(input.Text),
                Norm = 1.0
            }).ToArray();
            return Task.FromResult(new EmbeddingResult
            {
                OperationId = request.OperationId,
                ModelName = request.ModelName ?? "baseline-fixed",
                Dimensions = 8,
                Succeeded = true,
                Vectors = vectors
            });
        }

        private static float[] VectorFor(string text)
        {
            // 查询文本 "alpha-k" → 第 k 个基向量（与数据集的 one-hot 对齐），保证召回确定。
            var trimmed = text.Trim();
            var dash = trimmed.LastIndexOf('-');
            if (dash >= 0 && int.TryParse(trimmed[(dash + 1)..], out var index) && index >= 0 && index < 8)
            {
                var v = new float[8];
                v[index] = 1f;
                return v;
            }

            // 兜底：文本 FNV 哈希 → 8 维确定性向量。
            var hash = 2166136261u;
            foreach (var c in trimmed)
            {
                hash ^= c;
                hash *= 16777619u;
            }
            var fallback = new float[8];
            for (var i = 0; i < 8; i++)
            {
                fallback[i] = ((hash >> (i * 4)) & 0xF) / 16f;
            }
            return fallback;
        }
    }

    // ------------------------------------------------------------------
    // 结果行
    // ------------------------------------------------------------------

    private sealed record Row
    {
        public required string Provider;
        public required int QueryCount;
        public required int TopK;
        public required int Held;
        public required string Mode;
        public required double P50Ms;
        public required double P95Ms;
        public required double MeanMs;
        public required double OpsPerSec;
        public required double AllocBytesPerOp;
        public required double EmbeddingCallsPerOp;
        public required double VectorSearchCallsPerOp;
        public required double StorageRoundtripsPerOp;
        public required double ValidCandidatesPerOp;
        public required double UnderRecallPerOp;

        public string ToCsv() =>
            string.Join(',', Provider, QueryCount, TopK, Held, Mode,
                F(P50Ms), F(P95Ms), F(MeanMs), F(OpsPerSec), F(AllocBytesPerOp),
                F(EmbeddingCallsPerOp), F(VectorSearchCallsPerOp), F(StorageRoundtripsPerOp),
                F(ValidCandidatesPerOp), F(UnderRecallPerOp));

        public string ToTable() =>
            $"{Provider,-10} q={QueryCount,-2} topK={TopK,-4} held={Held,-4} {Mode,-13} " +
            $"p50={P50Ms,7:F3}ms p95={P95Ms,7:F3}ms mean={MeanMs,7:F3}ms " +
            $"ops/s={OpsPerSec,8:F1} alloc={AllocBytesPerOp,9:F0}B " +
            $"emb={EmbeddingCallsPerOp,3:F0} vec={VectorSearchCallsPerOp,3:F0} rt={StorageRoundtripsPerOp,3:F0} " +
            $"valid={ValidCandidatesPerOp,5:F1} under={UnderRecallPerOp,3:F0}";

        private static string F(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
    }
}
