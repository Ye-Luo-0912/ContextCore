using BenchmarkDotNet.Attributes;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using GeneratedContextJob = ContextCore.Client.Generated.Models.ContextJob;
using AbstractionsContextPackage = ContextCore.Abstractions.Models.ContextPackage;
using AbstractionsContextJob = ContextCore.Abstractions.ContextJob;

namespace ContextCore.Benchmarks;

// 客户端 DTO 映射基准：测量 Kiota 生成模型 ↔ Abstractions 类型 的 JSON round-trip 映射开销。
// 当前方案：Kiota 序列化 → JSON 字符串 → STJ 反序列化（MapToAbstraction）
// 基准结果用于决定是否保留 Kiota 还是改用直接 STJ 映射。
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class ClientDtoMappingBenchmarks
{
    private GeneratedContextJob _generatedJob = null!;
    private AbstractionsContextPackage _abstractionPackage = null!;

    [Params(1, 10, 50)]
    public int ItemCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // 构建生成模型实例（模拟 API 响应反序列化后的对象）
        _generatedJob = new GeneratedContextJob
        {
            JobId = $"job-bench-{ItemCount}",
            WorkspaceId = "bench-ws",
            CollectionId = "bench-col",
            Kind = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            State = 0
        };

        // 构建 Abstractions 类型实例（模拟需要映射到生成模型的请求对象）
        _abstractionPackage = new AbstractionsContextPackage
        {
            PackageId = $"pkg-bench-{ItemCount}",
            WorkspaceId = "bench-ws",
            CollectionId = "bench-col",
            EstimatedTokens = 2500,
            CreatedAt = DateTimeOffset.UtcNow,
            Sections = Enumerable.Range(0, ItemCount)
                .Select(i => new ContextPackageSection
                {
                    Name = $"section-{i}",
                    Priority = i,
                    Content = new string('x', 200),
                    EstimatedTokens = 50,
                    ItemRefs = [$"item-{i}"],
                    SourceRefs = [$"source-{i}"]
                })
                .ToArray(),
            Metadata = new Dictionary<string, string>
            {
                ["mode"] = "benchmark",
                ["buildId"] = $"build-{ItemCount}"
            }
        };
    }

    // Kiota 生成模型 → Abstractions 类型（响应映射）
    [Benchmark]
    public AbstractionsContextJob? Map_GeneratedToAbstraction()
    {
        return ContextCoreClient.MapToAbstraction<AbstractionsContextJob>(_generatedJob);
    }

    // 直接 STJ 序列化作为对照基线（无 Kiota 开销）
    [Benchmark(Baseline = true)]
    public string? Serialize_STJ_Direct()
    {
        return System.Text.Json.JsonSerializer.Serialize(_abstractionPackage);
    }

    // Kiota 序列化（MapToAbstraction 的前半段开销）
    [Benchmark]
    public string Serialize_Kiota_Parsable()
    {
        return ContextCoreClient.SerializeParsable(_generatedJob);
    }
}
