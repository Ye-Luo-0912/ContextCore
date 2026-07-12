using System.Text.Json;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Planning;
using ContextCore.Storage.InMemory;

namespace ContextCore.Tests;

/// <summary>
/// 验证 FileRouterIntentDatasetProvider 的加载、错误处理和可观测性，
/// 以及 RouterIntentShadowService 的数据集信息暴露。
/// </summary>
[TestClass]
[TestCategory("Learning")]
public sealed class RouterIntentDatasetProviderTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static string CreateTempDataset(params ContextPolicyFeatureExample[] examples)
    {
        var path = Path.GetTempFileName();
        using var writer = new StreamWriter(path);
        foreach (var example in examples)
        {
            writer.WriteLine(JsonSerializer.Serialize(example, JsonOptions));
        }
        writer.Flush();
        return path;
    }

    private static string CreateTempDatasetWithLines(params string[] lines)
    {
        var path = Path.GetTempFileName();
        File.WriteAllLines(path, lines);
        return path;
    }

    private static ContextPolicyFeatureExample CreateExample(string id, string mode = "Coding")
    {
        return new ContextPolicyFeatureExample
        {
            ExampleId = id,
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            SourceType = "test",
            SourceId = id,
            TaskKind = "RouterIntent",
            Mode = mode,
            InputSummary = $"test query {id}",
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    [TestMethod]
    public void Load_FileNotFound_ReturnsNotFoundStatus()
    {
        var provider = new FileRouterIntentDatasetProvider("/nonexistent/path/to/router-intent-examples.jsonl");
        var result = provider.Load();

        Assert.AreEqual(RouterIntentDatasetStatus.NotFound, result.Status);
        Assert.AreEqual(0, result.Examples.Count);
        Assert.IsNull(result.ContentHash);
        Assert.IsNull(result.Version);
        Assert.IsNull(result.LastModified);
        Assert.IsTrue(result.IsDegraded);
        Assert.IsNotNull(result.FilePath);
    }

    [TestMethod]
    public void Load_ValidFile_ReturnsLoadedStatusWithExamples()
    {
        var path = CreateTempDataset(
            CreateExample("ex-1"),
            CreateExample("ex-2"),
            CreateExample("ex-3"));

        try
        {
            var provider = new FileRouterIntentDatasetProvider(path);
            var result = provider.Load();

            Assert.AreEqual(RouterIntentDatasetStatus.Loaded, result.Status);
            Assert.AreEqual(3, result.Examples.Count);
            Assert.AreEqual(3, result.TotalLines);
            Assert.AreEqual(3, result.ValidLines);
            Assert.AreEqual(0, result.ErrorCount);
            Assert.IsFalse(result.IsDegraded);
            Assert.IsNotNull(result.ContentHash);
            Assert.AreEqual(64, result.ContentHash.Length);
            Assert.IsNotNull(result.Version);
            Assert.AreEqual(8, result.Version.Length);
            Assert.IsNotNull(result.LastModified);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Load_MalformedJsonLines_ReturnsLoadedWithErrorsAndCountsErrors()
    {
        var validExample = CreateExample("ex-valid");
        var path = CreateTempDatasetWithLines(
            JsonSerializer.Serialize(validExample, JsonOptions),
            "{ this is not valid json }",
            "",
            "{ \"exampleId\": null }",
            JsonSerializer.Serialize(CreateExample("ex-valid-2"), JsonOptions));

        try
        {
            var provider = new FileRouterIntentDatasetProvider(path);
            var result = provider.Load();

            Assert.AreEqual(RouterIntentDatasetStatus.LoadedWithErrors, result.Status);
            Assert.IsTrue(result.ErrorCount >= 1, $"ErrorCount should be >= 1, got {result.ErrorCount}");
            Assert.IsTrue(result.ValidLines >= 1, $"ValidLines should be >= 1, got {result.ValidLines}");
            Assert.IsTrue(result.TotalLines >= 2, $"TotalLines should be >= 2, got {result.TotalLines}");
            Assert.IsNotNull(result.ContentHash);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Load_SameContent_ProducesSameHash()
    {
        var example = CreateExample("ex-hash");
        var path1 = CreateTempDataset(example);
        var path2 = CreateTempDataset(example);

        try
        {
            var provider1 = new FileRouterIntentDatasetProvider(path1);
            var provider2 = new FileRouterIntentDatasetProvider(path2);
            var result1 = provider1.Load();
            var result2 = provider2.Load();

            Assert.AreEqual(result1.ContentHash, result2.ContentHash);
            Assert.AreEqual(result1.Version, result2.Version);
        }
        finally
        {
            File.Delete(path1);
            File.Delete(path2);
        }
    }

    [TestMethod]
    public async Task RouterIntentShadowService_ExposesDatasetInfoAfterClassifierInit()
    {
        var path = CreateTempDataset(
            CreateExample("shadow-ex-1", "Coding"),
            CreateExample("shadow-ex-2", "NovelGeneration"));

        try
        {
            var provider = new FileRouterIntentDatasetProvider(path);
            var service = new RouterIntentShadowService(
                new RouterShadowOptions
                {
                    Enabled = true,
                    TraceCollectionEnabled = true,
                    RecordAgreements = true,
                    RecordDisagreements = true
                },
                new InMemoryRouterIntentShadowTraceStore(),
                new PlanningIntentDetector(),
                provider);

            // DatasetInfo should be null before classifier is initialized
            Assert.IsNull(service.DatasetInfo);

            // Trigger classifier initialization by recording a trace
            await service.RecordAsync(new RouterIntentShadowRecordRequest
            {
                RequestId = "dataset-info-test",
                WorkspaceId = "ws-test",
                CollectionId = "col-test",
                EntryPoint = "planning",
                QueryText = "build verification task",
                RuntimeIntent = PlanningIntentDetector.CodingTask
            });

            // DatasetInfo should now be populated
            Assert.IsNotNull(service.DatasetInfo);
            Assert.AreEqual(RouterIntentDatasetStatus.Loaded, service.DatasetInfo.Status);
            Assert.AreEqual(2, service.DatasetInfo.ValidLines);
            Assert.IsNotNull(service.DatasetInfo.ContentHash);
            Assert.IsNotNull(service.DatasetInfo.Version);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task RouterIntentShadowService_MissingDataset_ReportsNotFoundInDatasetInfo()
    {
        var provider = new FileRouterIntentDatasetProvider("/nonexistent/path/router-intent-examples.jsonl");
        var service = new RouterIntentShadowService(
            new RouterShadowOptions
            {
                Enabled = true,
                TraceCollectionEnabled = true,
                RecordAgreements = true,
                RecordDisagreements = true
            },
            new InMemoryRouterIntentShadowTraceStore(),
            new PlanningIntentDetector(),
            provider);

        Assert.IsNull(service.DatasetInfo);

        await service.RecordAsync(new RouterIntentShadowRecordRequest
        {
            RequestId = "missing-dataset-test",
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            EntryPoint = "planning",
            QueryText = "test query",
            RuntimeIntent = PlanningIntentDetector.CodingTask
        });

        Assert.IsNotNull(service.DatasetInfo);
        Assert.AreEqual(RouterIntentDatasetStatus.NotFound, service.DatasetInfo.Status);
        Assert.IsTrue(service.DatasetInfo.IsDegraded);
        Assert.AreEqual(0, service.DatasetInfo.ValidLines);
    }
}
