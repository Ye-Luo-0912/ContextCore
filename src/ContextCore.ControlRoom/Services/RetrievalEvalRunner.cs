using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Embedding;
using ContextCore.Storage.InMemory;

namespace ContextCore.ControlRoom.Services;

/// <summary>
/// 专项检索评测运行器（A5 §7.1）。
/// 使用真实 OnnxEmbeddingProvider（非 Mock），测量 bge-small-zh-v1.5 语义向量召回质量。
/// 支持五个测试维度：向量语义召回、关键词精确召回、废弃项过滤、关系扩展、跨层检索。
/// </summary>
public sealed class RetrievalEvalRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions JsonOutputOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>在指定 contexts 根目录下执行 retrieval 专项评测。</summary>
    /// <param name="contextsRootPath">eval/contexts/ 目录</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task<RetrievalEvalReport> RunAsync(
        string contextsRootPath,
        CancellationToken cancellationToken = default)
    {
        var retrievalDir = Path.Combine(contextsRootPath, "retrieval");
        if (!Directory.Exists(retrievalDir))
        {
            return new RetrievalEvalReport
            {
                ErrorMessage = $"retrieval 评测目录不存在：{retrievalDir}"
            };
        }

        var corpusPath = Path.Combine(retrievalDir, "corpus.json");
        var samplesPath = Path.Combine(retrievalDir, "seed_samples.json");

        if (!File.Exists(samplesPath))
        {
            return new RetrievalEvalReport
            {
                ErrorMessage = $"seed_samples.json 不存在：{samplesPath}"
            };
        }

        var corpus = File.Exists(corpusPath)
            ? JsonSerializer.Deserialize<ContextEvalCorpus>(
                await File.ReadAllTextAsync(corpusPath, cancellationToken).ConfigureAwait(false),
                JsonOptions)
            : new ContextEvalCorpus();

        var samples = JsonSerializer.Deserialize<List<ContextEvalSample>>(
            await File.ReadAllTextAsync(samplesPath, cancellationToken).ConfigureAwait(false),
            JsonOptions);

        if (samples is null || samples.Count == 0)
        {
            return new RetrievalEvalReport { ErrorMessage = "seed_samples.json 为空或反序列化失败" };
        }

        // 1. 初始化 OnnxEmbeddingProvider（真实 ONNX 语义向量）
        Console.WriteLine("[RetrievalEval] 初始化 OnnxEmbeddingProvider...");
        OnnxEmbeddingProvider embeddingProvider;
        try
        {
            var embeddingOptions = new EmbeddingOptions
            {
                ModelName = EmbeddingModelPaths.DefaultModelName,
                MaxBatchSize = 8,
                MaxSequenceLength = 256,
                OnnxIntraOpNumThreads = 1,
                OnnxInterOpNumThreads = 1,
                EnableContentHashCache = false,
                QueryInstruction = BgeQueryInstructions.BgeZhV15
            };
            var sessionManager = new OnnxEmbeddingSessionManager(embeddingOptions);
            embeddingProvider = new OnnxEmbeddingProvider(embeddingOptions, sessionManager);
        }
        catch (Exception ex)
        {
            return new RetrievalEvalReport
            {
                ErrorMessage = $"OnnxEmbeddingProvider 初始化失败：{ex.Message}"
            };
        }

        // 2. 创建 InMemory 存储和检索器
        const string workspaceId = "eval-retrieval";
        const string collectionId = "retrieval";

        var contextStore = new InMemoryContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var vectorStore = new InMemoryVectorStore();
        var relationStore = new InMemoryRelationStore();
        var retriever = new HybridContextRetriever(
            contextStore,
            memoryStore,
            relationStore,
            embeddingProvider,
            vectorStore,
            traceStore: null);

        // 3. 灌入语料
        if (corpus is not null)
        {
            await IngestCorpusAsync(corpus, contextStore, memoryStore, relationStore,
                workspaceId, collectionId, cancellationToken).ConfigureAwait(false);

            // 4. 向量化语料并写入 VectorStore
            Console.WriteLine("[RetrievalEval] 向量化语料...");
            await EmbedCorpusAsync(corpus, embeddingProvider, vectorStore,
                workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
        }

        // 5. 逐样本评测
        Console.WriteLine($"[RetrievalEval] 开始评测 {samples.Count} 条样本...");
        var results = new List<RetrievalSampleResult>();

        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await EvaluateSampleAsync(
                    sample, retriever, embeddingProvider,
                    workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
                results.Add(result);

                var statusMark = result.Passed ? "✅" : "❌";
                Console.WriteLine($"  [{statusMark}] {result.SampleId,-40} Recall@5={result.Recall5:P0} MRR={result.Mrr:F3} Dim={result.Dimension}");
            }
            catch (Exception ex)
            {
                results.Add(new RetrievalSampleResult
                {
                    SampleId = sample.Id,
                    Query = sample.Query,
                    Dimension = sample.Metadata.GetValueOrDefault("retrievalDimension", "unknown"),
                    Passed = false,
                    ErrorMessage = ex.Message
                });
                Console.WriteLine($"  [💥] {sample.Id,-40} Error: {ex.Message}");
            }
        }

        return BuildReport(results);
    }

    // ─── 语料摄入 ───────────────────────────────────────────────────────────────

    private static async Task IngestCorpusAsync(
        ContextEvalCorpus corpus,
        InMemoryContextStore contextStore,
        InMemoryMemoryStore memoryStore,
        InMemoryRelationStore relationStore,
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        foreach (var ctx in corpus.Contexts)
        {
            await contextStore.SaveAsync(new ContextItem
            {
                Id = ctx.Id,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Type = ctx.Type,
                Title = ctx.Title,
                Content = ctx.Content,
                ContentFormat = ctx.ContentFormat,
                Tags = ctx.Tags,
                Refs = ctx.Refs,
                SourceRefs = ctx.SourceRefs,
                Metadata = ctx.Metadata,
                Importance = ctx.Importance,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            }, cancellationToken).ConfigureAwait(false);
        }

        foreach (var mem in corpus.Memories)
        {
            await memoryStore.SaveAsync(new ContextMemoryItem
            {
                Id = mem.Id,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Layer = mem.Layer,
                Status = mem.Status,
                Type = mem.Type,
                Content = mem.Content,
                ContentFormat = mem.ContentFormat,
                Tags = mem.Tags,
                SourceRefs = mem.SourceRefs,
                RelationRefs = mem.RelationRefs,
                Importance = mem.Importance,
                Confidence = mem.Confidence,
                Version = mem.Version <= 0 ? 1 : mem.Version,
                Metadata = mem.Metadata,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            }, cancellationToken).ConfigureAwait(false);
        }

        foreach (var rel in corpus.Relations)
        {
            await relationStore.SaveAsync(new ContextRelation
            {
                Id = rel.Id,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                SourceId = rel.SourceId,
                TargetId = rel.TargetId,
                RelationType = rel.RelationType,
                Weight = rel.Weight,
                Confidence = rel.Confidence,
                CreatedAt = DateTimeOffset.UtcNow
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    // ─── 语料向量化 ─────────────────────────────────────────────────────────────

    private static async Task EmbedCorpusAsync(
        ContextEvalCorpus corpus,
        OnnxEmbeddingProvider provider,
        InMemoryVectorStore vectorStore,
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        // 上下文条目：Document 模式（不加 instruction 前缀）
        var contextInputs = corpus.Contexts.Select(c => new EmbeddingInput
        {
            Id = c.Id,
            Text = string.IsNullOrWhiteSpace(c.Title) ? c.Content : $"{c.Title} {c.Content}",
            SourceRef = c.Id
        }).ToList();

        // 记忆条目：MemoryItem 模式（Document 模式，不加 instruction 前缀）
        var memoryInputs = corpus.Memories.Select(m => new EmbeddingInput
        {
            Id = m.Id,
            Text = m.Content,
            SourceRef = m.Id
        }).ToList();

        if (contextInputs.Count > 0)
        {
            var result = await provider.EmbedAsync(new EmbeddingRequest
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                InputKind = EmbeddingInputKind.ContextItem,
                Inputs = contextInputs
            }, cancellationToken).ConfigureAwait(false);

            await UpsertVectorsAsync(result, vectorStore, workspaceId, collectionId, "context",
                cancellationToken).ConfigureAwait(false);
        }

        if (memoryInputs.Count > 0)
        {
            var result = await provider.EmbedAsync(new EmbeddingRequest
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                InputKind = EmbeddingInputKind.MemoryItem,
                Inputs = memoryInputs
            }, cancellationToken).ConfigureAwait(false);

            // 判断 sourceKind：stable: 前缀用 "memory"，其余也用 "memory"
            await UpsertVectorsAsync(result, vectorStore, workspaceId, collectionId, "memory",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task UpsertVectorsAsync(
        EmbeddingResult result,
        InMemoryVectorStore vectorStore,
        string workspaceId,
        string collectionId,
        string sourceKind,
        CancellationToken cancellationToken)
    {
        foreach (var vec in result.Vectors)
        {
            await vectorStore.UpsertAsync(new VectorRecord
            {
                Id = $"vec-{vec.InputId}",
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                SourceId = vec.SourceRef,
                SourceKind = sourceKind,
                ModelName = result.ModelName,
                Dimensions = result.Dimensions,
                Vector = vec.Values,
                ContentHash = vec.Metadata.TryGetValue("contentHash", out var h) ? h : vec.InputId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    // ─── 单样本评测 ─────────────────────────────────────────────────────────────

    private static async Task<RetrievalSampleResult> EvaluateSampleAsync(
        ContextEvalSample sample,
        HybridContextRetriever retriever,
        OnnxEmbeddingProvider embeddingProvider,
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var dimension = sample.Metadata.GetValueOrDefault("retrievalDimension", "unknown");

        // 向量化 query（使用 Query 模式，会注入 QueryInstruction）
        IReadOnlyList<float> queryVector = Array.Empty<float>();
        var queryEmbedResult = await embeddingProvider.EmbedAsync(new EmbeddingRequest
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            InputKind = EmbeddingInputKind.Query,
            Inputs = [new EmbeddingInput { Id = "q", Text = sample.Query, SourceRef = sample.Id }]
        }, cancellationToken).ConfigureAwait(false);

        if (queryEmbedResult.Succeeded && queryEmbedResult.Vectors.Count > 0)
        {
            queryVector = queryEmbedResult.Vectors[0].Values;
        }

        // 关系扩展：默认开启，"relation-expansion" 维度强制开启
        var includeRelation = dimension != "vector" && dimension != "keyword";

        var request = new ContextRetrievalRequest
        {
            OperationId = Guid.NewGuid().ToString("N"),
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            QueryText = sample.Query,
            QueryVector = queryVector,
            TopK = 15,
            VectorTopK = 20,
            CandidateTake = 50,
            IncludeKeywordRecall = true,
            IncludeVectorRecall = true,
            IncludeRelationExpansion = includeRelation,
            RelationExpansionDepth = 2,
            IncludeWorkingMemory = true,
            IncludeStableMemory = true
        };

        var retrievalResult = await retriever.RetrieveAsync(request, cancellationToken).ConfigureAwait(false);

        var selectedIds = retrievalResult.SelectedItems
            .Select(c => c.SourceId)
            .ToList();

        // 计算 Recall@K 和 MRR
        var mustHitCount = sample.MustHit.Count;
        var recall1 = mustHitCount == 0 ? 1.0 : (double)sample.MustHit.Count(id => selectedIds.Take(1).Contains(id, StringComparer.OrdinalIgnoreCase)) / mustHitCount;
        var recall3 = mustHitCount == 0 ? 1.0 : (double)sample.MustHit.Count(id => selectedIds.Take(3).Contains(id, StringComparer.OrdinalIgnoreCase)) / mustHitCount;
        var recall5 = mustHitCount == 0 ? 1.0 : (double)sample.MustHit.Count(id => selectedIds.Take(5).Contains(id, StringComparer.OrdinalIgnoreCase)) / mustHitCount;
        var recall10 = mustHitCount == 0 ? 1.0 : (double)sample.MustHit.Count(id => selectedIds.Take(10).Contains(id, StringComparer.OrdinalIgnoreCase)) / mustHitCount;

        // MRR：所有 mustHit 中最高排名（最小位置）的倒数
        double mrr = 0.0;
        int bestPos = int.MaxValue;
        for (int i = 0; i < selectedIds.Count; i++)
        {
            if (sample.MustHit.Any(id => string.Equals(id, selectedIds[i], StringComparison.OrdinalIgnoreCase))
                && i < bestPos)
            {
                bestPos = i;
            }
        }
        if (bestPos < int.MaxValue)
        {
            mrr = 1.0 / (bestPos + 1);
        }

        // 噪音违规：mustNotHit 出现在结果中
        var noiseViolations = sample.MustNotHit
            .Where(id => selectedIds.Any(s => string.Equals(s, id, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var mustHitMissed = sample.MustHit
            .Where(id => !selectedIds.Any(s => string.Equals(s, id, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // 通过条件：
        // 1. mustHit 为空时自动通过（只测试 mustNotHit）
        // 2. mustHit 不为空时：Recall@10 == 1.0（所有 mustHit 都命中）
        // 3. 无 mustNotHit 违规
        var mustHitPassed = mustHitCount == 0 || recall10 >= 1.0;
        var noiseFilterPassed = noiseViolations.Count == 0;
        var passed = mustHitPassed && noiseFilterPassed;

        return new RetrievalSampleResult
        {
            SampleId = sample.Id,
            Query = sample.Query,
            Dimension = dimension,
            Passed = passed,
            MustHitCount = mustHitCount,
            MustHitRecalledCount = sample.MustHit.Count(id => selectedIds.Contains(id, StringComparer.OrdinalIgnoreCase)),
            MustHitMissed = mustHitMissed,
            MustNotHitViolations = noiseViolations,
            Recall1 = recall1,
            Recall3 = recall3,
            Recall5 = recall5,
            Recall10 = recall10,
            Mrr = mrr,
            SelectedIds = selectedIds,
            TotalCandidates = retrievalResult.SelectedItems.Count + retrievalResult.DroppedItems.Count,
            GoldenNotes = sample.GoldenNotes
        };
    }

    // ─── 报告汇总 ────────────────────────────────────────────────────────────────

    private static RetrievalEvalReport BuildReport(List<RetrievalSampleResult> results)
    {
        var total = results.Count;
        if (total == 0)
        {
            return new RetrievalEvalReport();
        }

        var passed = results.Count(r => r.Passed);
        var failed = results.Count(r => !r.Passed && string.IsNullOrEmpty(r.ErrorMessage));
        var errored = results.Count(r => !string.IsNullOrEmpty(r.ErrorMessage));

        var validResults = results.Where(r => string.IsNullOrEmpty(r.ErrorMessage)).ToList();

        var byDimension = validResults
            .GroupBy(r => r.Dimension)
            .ToDictionary(
                g => g.Key,
                g => new RetrievalDimensionStats
                {
                    Dimension = g.Key,
                    SampleCount = g.Count(),
                    PassedCount = g.Count(r => r.Passed),
                    AvgRecall5 = g.Any() ? g.Average(r => r.Recall5) : 0,
                    AvgRecall10 = g.Any() ? g.Average(r => r.Recall10) : 0,
                    AvgMrr = g.Any() ? g.Average(r => r.Mrr) : 0,
                    NoiseViolationCount = g.Sum(r => r.MustNotHitViolations.Count)
                });

        return new RetrievalEvalReport
        {
            TotalSamples = total,
            PassedSamples = passed,
            FailedSamples = failed,
            ErroredSamples = errored,
            PassRate = (double)passed / total,
            AvgRecall1 = validResults.Any() ? validResults.Average(r => r.Recall1) : 0,
            AvgRecall3 = validResults.Any() ? validResults.Average(r => r.Recall3) : 0,
            AvgRecall5 = validResults.Any() ? validResults.Average(r => r.Recall5) : 0,
            AvgRecall10 = validResults.Any() ? validResults.Average(r => r.Recall10) : 0,
            AvgMrr = validResults.Any() ? validResults.Average(r => r.Mrr) : 0,
            TotalNoiseViolations = results.Sum(r => r.MustNotHitViolations.Count),
            ByDimension = byDimension,
            Results = results
        };
    }

    /// <summary>将报告序列化为 JSON 并写入文件。</summary>
    public static async Task ExportAsync(
        RetrievalEvalReport report,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(report, JsonOutputOptions);
        await File.WriteAllTextAsync(outputPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>将报告渲染为控制台可读格式。</summary>
    public static void RenderToConsole(RetrievalEvalReport report)
    {
        Console.WriteLine();
        Console.WriteLine("=============================================================");
        Console.WriteLine("         ContextCore Retrieval Eval 专项检索评测报告          ");
        Console.WriteLine("=============================================================");

        if (!string.IsNullOrEmpty(report.ErrorMessage))
        {
            Console.WriteLine($"评测失败：{report.ErrorMessage}");
            return;
        }

        Console.WriteLine($"总样本: {report.TotalSamples} | ✅ 通过: {report.PassedSamples} | ❌ 失败: {report.FailedSamples} | 💥 异常: {report.ErroredSamples} | 通过率: {report.PassRate:P1}");
        Console.WriteLine($"平均指标 → Recall@1: {report.AvgRecall1:P1} | Recall@3: {report.AvgRecall3:P1} | Recall@5: {report.AvgRecall5:P1} | Recall@10: {report.AvgRecall10:P1} | MRR: {report.AvgMrr:F3} | 噪音违规总数: {report.TotalNoiseViolations}");
        Console.WriteLine();
        Console.WriteLine("[按维度分组]");
        Console.WriteLine($"| {"维度",-22} | {"样本",-4} | {"通过",-4} | {"通过率",-6} | {"Recall@5",-8} | {"Recall@10",-9} | {"MRR",-6} | {"噪音违规",-6} |");
        Console.WriteLine($"|{new string('-', 24)}|{new string('-', 6)}|{new string('-', 6)}|{new string('-', 8)}|{new string('-', 10)}|{new string('-', 11)}|{new string('-', 8)}|{new string('-', 8)}|");

        foreach (var kv in report.ByDimension.OrderBy(x => x.Key))
        {
            var s = kv.Value;
            Console.WriteLine($"| {kv.Key,-22} | {s.SampleCount,4} | {s.PassedCount,4} | {(double)s.PassedCount / s.SampleCount,6:P0} | {s.AvgRecall5,8:P0} | {s.AvgRecall10,9:P0} | {s.AvgMrr,6:F3} | {s.NoiseViolationCount,6} |");
        }

        Console.WriteLine();
        Console.WriteLine("[详细结果]");
        Console.WriteLine($"| {"样本 ID",-42} | {"状态",-4} | {"R@5",-5} | {"R@10",-5} | {"MRR",-5} | {"维度",-20} | {"噪音违规"} |");
        Console.WriteLine($"|{new string('-', 44)}|{new string('-', 6)}|{new string('-', 7)}|{new string('-', 7)}|{new string('-', 7)}|{new string('-', 22)}|{new string('-', 10)}|");

        foreach (var r in report.Results)
        {
            var status = !string.IsNullOrEmpty(r.ErrorMessage) ? "💥" : r.Passed ? "✅" : "❌";
            var noise = r.MustNotHitViolations.Count > 0
                ? string.Join(",", r.MustNotHitViolations.Select(v => v.Split(':').Last()))
                : "-";
            Console.WriteLine($"| {r.SampleId,-42} | {status,-4} | {r.Recall5,5:P0} | {r.Recall10,5:P0} | {r.Mrr,5:F3} | {r.Dimension,-20} | {noise,-8} |");
        }

        // 失败样本的 missed mustHit 明细
        var failures = report.Results.Where(r => !r.Passed && string.IsNullOrEmpty(r.ErrorMessage)).ToList();
        if (failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("[失败明细]");
            foreach (var r in failures)
            {
                if (r.MustHitMissed.Count > 0)
                {
                    Console.WriteLine($"  {r.SampleId}: 未命中 mustHit → {string.Join(", ", r.MustHitMissed)}");
                }

                if (r.MustNotHitViolations.Count > 0)
                {
                    Console.WriteLine($"  {r.SampleId}: 噪音违规 mustNotHit → {string.Join(", ", r.MustNotHitViolations)}");
                }
            }
        }

        Console.WriteLine("=============================================================");
        Console.WriteLine();
    }
}

// ─── 评测结果 DTO ─────────────────────────────────────────────────────────────

/// <summary>单条样本的 retrieval 评测结果。</summary>
public sealed class RetrievalSampleResult
{
    public string SampleId { get; init; } = string.Empty;
    public string Query { get; init; } = string.Empty;
    public string Dimension { get; init; } = string.Empty;
    public bool Passed { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
    public int MustHitCount { get; init; }
    public int MustHitRecalledCount { get; init; }
    public List<string> MustHitMissed { get; init; } = [];
    public List<string> MustNotHitViolations { get; init; } = [];
    public double Recall1 { get; init; }
    public double Recall3 { get; init; }
    public double Recall5 { get; init; }
    public double Recall10 { get; init; }
    public double Mrr { get; init; }
    public int TotalCandidates { get; init; }
    public List<string> SelectedIds { get; init; } = [];
    public string GoldenNotes { get; init; } = string.Empty;
}

/// <summary>单个测试维度的汇总统计。</summary>
public sealed class RetrievalDimensionStats
{
    public string Dimension { get; init; } = string.Empty;
    public int SampleCount { get; init; }
    public int PassedCount { get; init; }
    public double AvgRecall5 { get; init; }
    public double AvgRecall10 { get; init; }
    public double AvgMrr { get; init; }
    public int NoiseViolationCount { get; init; }
}

/// <summary>retrieval 专项评测汇总报告。</summary>
public sealed class RetrievalEvalReport
{
    public string ErrorMessage { get; init; } = string.Empty;
    public int TotalSamples { get; init; }
    public int PassedSamples { get; init; }
    public int FailedSamples { get; init; }
    public int ErroredSamples { get; init; }
    public double PassRate { get; init; }
    public double AvgRecall1 { get; init; }
    public double AvgRecall3 { get; init; }
    public double AvgRecall5 { get; init; }
    public double AvgRecall10 { get; init; }
    public double AvgMrr { get; init; }
    public int TotalNoiseViolations { get; init; }
    public Dictionary<string, RetrievalDimensionStats> ByDimension { get; init; } = new();
    public List<RetrievalSampleResult> Results { get; init; } = [];
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}
