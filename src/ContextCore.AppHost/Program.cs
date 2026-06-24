using System.Text.Json;
using System.Text.Json.Serialization;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;

var appOptions = AppHostOptions.Parse(args);
var storageOptions = new FileStorageOptions
{
    RootPath = appOptions.RootPath
};

var resolvedRootPath = storageOptions.ResolvedRootPath;
var logsRootPath = Path.Combine(resolvedRootPath, "logs");
var eventSink = new CompositeContextEventSink(
[
	new FileContextEventSink(logsRootPath)
]);

// TODO-DEMO [P2-5]：所有配置通过 CLI 参数传入，无配置文件支持。参见：TODO.md → P2-5
// TODO-DEMO [P0-4]：所有依赖手动 new 创建，无 DI 容器/生命周期管理。参见：TODO.md → P0-4
var store = new FileContextStore(storageOptions);
var index = new FileContextIndex(storageOptions);
var memoryStore = new FileMemoryStore(storageOptions);
var constraintStore = new FileConstraintStore(storageOptions);
var globalStore = new FileGlobalContextStore(storageOptions);
var relationStore = new FileRelationStore(storageOptions);
var promotionService = new BasicMemoryPromotionService(memoryStore, memoryStore);
var tokenizerResolver = new DefaultContextTokenizerResolver();
var packageBuilder = new BasicContextPackageBuilder(
    store,
    constraintStore,
    globalStore,
    memoryStore,
    relationStore,
    tokenizerResolver: tokenizerResolver,
    workingMemoryService: memoryStore);
var inputIngestionService = new ContextInputIngestionService(
    store,
    new ContextInputNormalizer(),
    new ContextInputValidator(),
    new ContextInputHasher(),
    new ContextInputSequencer());
var runtime = new ContextRuntimeService(
    store,
    memoryStore,
    promotionService,
    packageBuilder,
    inputIngestionService,
    new ContextValidationService(),
    eventSink);
var ingestion = new BasicContextIngestionService(store);
// TODO-DEMO [P0-1]：MockContextCompressor 不调用任何模型 API，生成的摘要无语义价值。
// 生产使用前请替换为真实 LLM 压缩实现。参见：TODO.md → P0-1
var compressor = new MockContextCompressor();
var relationBuilder = new RelationBuilder();

Console.WriteLine("ContextCore AppHost starting.");
Console.WriteLine($"Workspace: {appOptions.WorkspaceId}, Collection: {appOptions.CollectionId}");
// storageOptions.ResolvedRootPath 已展开环境变量并转为绝对路径
Console.WriteLine($"Storage root: {resolvedRootPath}");
Console.WriteLine($"Detailed logs: {logsRootPath}");

if (!appOptions.NoSeed)
{
    await SeedDemoItemsAsync(runtime, appOptions.WorkspaceId, appOptions.CollectionId);
    Console.WriteLine("Seeded base context items.");
}

var compressionInputs = await store.QueryAsync(new ContextQuery
{
    WorkspaceId = appOptions.WorkspaceId,
    CollectionId = appOptions.CollectionId,
    ExcludedTypes = ["summary"],
    IncludeDerived = false,
    Take = 100,
    IncludeContent = true
});

Console.WriteLine($"Compression input items: {compressionInputs.Count} (summary and derived items excluded).");

var compressionResponse = await compressor.CompressAsync(new CompressionRequest
{
    OperationId = appOptions.OperationId,
    WorkspaceId = appOptions.WorkspaceId,
    CollectionId = appOptions.CollectionId,
    TaskKind = CompressionTaskKind.Summarize,
    Inputs = compressionInputs,
    Options = new CompressionOptions
    {
        Depth = CompressionDepth.Light,
        GenerateIndexHints = true,
        PreserveSourceRefs = true,
        TargetTokenBudget = 300
    }
});

await eventSink.EmitAsync(new ContextOperationEvent
{
    EventId = Guid.NewGuid().ToString("N"),
    OperationId = compressionResponse.OperationId,
    OperationName = "compression.mock",
    WorkspaceId = appOptions.WorkspaceId,
    CollectionId = appOptions.CollectionId,
    Level = ContextEventLevel.Information,
    Message = $"Generated {compressionResponse.GeneratedItems.Count} item(s), {compressionResponse.IndexHints.Count} index hint(s).",
    Metadata = new Dictionary<string, string>
    {
        ["inputCount"] = compressionInputs.Count.ToString(),
        ["status"] = compressionResponse.Status.ToString()
    },
    CreatedAt = DateTimeOffset.UtcNow
});

foreach (var generatedItem in compressionResponse.GeneratedItems)
{
    await ingestion.IngestAsync(generatedItem);
}

foreach (var indexHint in compressionResponse.IndexHints)
{
    await index.UpsertAsync(indexHint);
}

var compressionRelations = relationBuilder.BuildForCompressionResponse(compressionResponse);
foreach (var relation in compressionRelations)
{
    await relationStore.SaveAsync(relation);
}

Console.WriteLine($"Saved generated items: {compressionResponse.GeneratedItems.Count}; index hints: {compressionResponse.IndexHints.Count}; relations: {compressionRelations.Count}.");

var package = await runtime.BuildPackageAsync(new ContextPackageRequest
{
    WorkspaceId = appOptions.WorkspaceId,
    CollectionId = appOptions.CollectionId,
    RequiredTags = [],
    RequiredTypes = [],
    TokenBudget = appOptions.TokenBudget,
    IncludeRecent = true,
    Metadata = new Dictionary<string, string>
    {
        ["host"] = "ContextCore.AppHost"
    }
});

Console.WriteLine($"Package built: {package.PackageId}, sections: {package.Sections.Count}, estimated tokens: {package.EstimatedTokens}.");

var packageRelations = relationBuilder.BuildForPackage(package);
foreach (var relation in packageRelations)
{
    await relationStore.SaveAsync(relation);
}

Console.WriteLine($"Package relations saved: {packageRelations.Count}.");

if (appOptions.PrintPackage)
{
    var jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true
    };
    jsonOptions.Converters.Add(new JsonStringEnumConverter());

    Console.WriteLine(JsonSerializer.Serialize(package, jsonOptions));
}

Console.WriteLine("ContextCore AppHost completed.");

// TODO-DEMO：此函数注入 3 条硬编码演示数据，不代表真实业务场景。

static async Task SeedDemoItemsAsync(
    IContextRuntimeService runtime,
    string workspaceId,
    string collectionId)
{
    var now = DateTimeOffset.UtcNow;
    var items = new[]
    {
        new ContextItem
        {
            Id = "item-markdown",
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Type = "note",
            Title = "Workspace Overview",
            Content = """
            # Workspace Overview

            ContextCore stores independent context items that upper systems can organize for their own workflows.
            """,
            ContentFormat = ContextContentFormat.Markdown,
            Tags = ["overview", "workspace"],
            SourceRefs = ["sample:overview"],
            Importance = 0.9,
            CreatedAt = now,
            UpdatedAt = now
        },
        new ContextItem
        {
            Id = "item-json",
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Type = "config",
            Title = "Routing Preferences",
            Content = """
            {
              "defaultModelRole": "GeneralCompression",
              "storage": "FileSystem",
              "format": "Json"
            }
            """,
            ContentFormat = ContextContentFormat.Json,
            Tags = ["config", "model"],
            SourceRefs = ["sample:config"],
            Importance = 0.7,
            CreatedAt = now,
            UpdatedAt = now
        },
        new ContextItem
        {
            Id = "item-text",
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Type = "log",
            Title = "Ingestion Notes",
            Content = "The file storage backend writes metadata to JSONL and stores raw item content by content format.",
            ContentFormat = ContextContentFormat.PlainText,
            Tags = ["storage", "ingestion"],
            SourceRefs = ["sample:ingestion"],
            Importance = 0.8,
            CreatedAt = now,
            UpdatedAt = now
        }
    };

    foreach (var item in items)
    {
        await runtime.IngestAsync(item);
    }
}

/// <summary>AppHost 演示程序的命令行参数。</summary>
internal sealed class AppHostOptions
{
    /// <summary>
    /// 存储根目录。默认为 <see cref="FileStorageOptions.DefaultRootPath"/>
    /// （仓库根目录下的 <c>context-core-data</c> 专用目录）。
    /// 可通过 <c>--root &lt;path&gt;</c> CLI 参数覆盖。
    /// </summary>
    public string RootPath { get; init; } = FileStorageOptions.DefaultRootPath;

    public string WorkspaceId { get; init; } = "demo-workspace";

    public string CollectionId { get; init; } = "demo-collection";

    public string OperationId { get; init; } = "demo-compression";

    public int TokenBudget { get; init; } = 500;

    public bool NoSeed { get; init; }

    public bool PrintPackage { get; init; }

    public static AppHostOptions Parse(string[] args)
    {
        return new AppHostOptions
        {
            RootPath = FileStorageOptions.ResolveRootPath(GetOption(args, "--root")),
            WorkspaceId = GetOption(args, "--workspace") ?? "demo-workspace",
            CollectionId = GetOption(args, "--collection") ?? "demo-collection",
            OperationId = GetOption(args, "--operation") ?? "demo-compression",
            TokenBudget = int.TryParse(GetOption(args, "--token-budget"), out var tokenBudget) ? tokenBudget : 500,
            NoSeed = HasFlag(args, "--no-seed"),
            PrintPackage = HasFlag(args, "--print-package")
        };
    }

    private static string? GetOption(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool HasFlag(IReadOnlyList<string> args, string name)
    {
        return args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
    }
}
