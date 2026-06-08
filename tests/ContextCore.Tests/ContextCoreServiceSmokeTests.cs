using System.Net.Http.Json;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Core;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ContextCore.Tests;

/// <summary>验证 ContextCore.Service + ContextCore.Client 的 HTTP 级端到端可用性。</summary>
[TestClass]
public sealed class ContextCoreServiceSmokeTests
{
    [TestMethod]
    public async Task HttpApiSmoke_ShouldSupportP1ContextLifecycle()
    {
        var rootPath = CreateTestRootPath();

        try
        {
            await SeedFilesystemStateAsync(rootPath);

            using var factory = new ContextCoreServiceFactory(rootPath);
            using var http = factory.CreateClient();
            var client = new ContextCoreClient(http);

            var status = await client.GetStatusAsync();
            Assert.AreEqual("filesystem", status.Storage.Provider);
            Assert.AreEqual(FileStorageOptions.ResolveRootPath(rootPath), status.Storage.RootPath);

            var ingested = await client.IngestAsync(new ContextItem
            {
                Id = "http-raw-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Type = "note",
                Title = "HTTP raw item",
                Content = "HTTP raw context for smoke testing.",
                Tags = new[] { "p1", "http" },
                SourceRefs = new[] { "source:http-raw-1" },
                Importance = 1.0,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            var promotedSourceMemory = await client.AddMemoryAsync(new ContextMemoryItem
            {
                Id = "http-memory-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Layer = ContextMemoryLayer.Working,
                Status = ContextMemoryStatus.Verified,
                Type = "memory",
                Content = "HTTP working memory item.",
                ContentFormat = ContextContentFormat.PlainText,
                Tags = new[] { "p1", "working" },
                SourceRefs = new[] { "source:http-memory-1" },
                RelationRefs = new[] { "relation:http-memory-1" },
                Importance = 0.6,
                Confidence = 0.8,
                Version = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            var workingMemory = await client.AddMemoryAsync(new ContextMemoryItem
            {
                Id = "http-memory-2",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Layer = ContextMemoryLayer.Working,
                Status = ContextMemoryStatus.Verified,
                Type = "memory",
                Content = "HTTP working memory item kept in the working layer.",
                ContentFormat = ContextContentFormat.PlainText,
                Tags = new[] { "p1", "working" },
                SourceRefs = new[] { "source:http-memory-2" },
                RelationRefs = new[] { "relation:http-memory-2" },
                Importance = 0.5,
                Confidence = 0.75,
                Version = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            var shortTermMemory = await client.AddWorkingMemoryItemAsync(new WorkingMemoryItem
            {
                Id = "http-working-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Type = "task-note",
                Content = "HTTP working memory item through the dedicated working endpoint.",
                ContentFormat = ContextContentFormat.PlainText,
                Tags = new[] { "p1", "working" },
                SourceRefs = new[] { "source:http-working-1" },
                RelationRefs = new[] { "relation:http-working-1" },
                Importance = 0.7,
                Confidence = 0.85,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            var activeContext = await client.SetWorkingMemoryActiveContextAsync(new WorkingMemoryActiveContext
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                CurrentTaskId = "task-http-1",
                Summary = "HTTP active context for P1 smoke testing.",
                MemoryRefs = new[] { shortTermMemory.Id },
                ContextRefs = new[] { ingested.Id },
                Metadata = new Dictionary<string, string>
                {
                    ["source"] = "http-smoke"
                },
                UpdatedAt = DateTimeOffset.UtcNow
            });

            var currentTask = await client.SetWorkingMemoryCurrentTaskAsync(new WorkingMemoryCurrentTask
            {
                TaskId = "task-http-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Title = "HTTP P1 smoke task",
                Description = "Verify working-memory endpoints through ContextCore.Client.",
                Status = "active",
                Tags = new[] { "p1", "http" },
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            var promoted = await client.PromoteMemoryAsync(new ContextCoreMemoryPromotionRequest
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                SourceMemoryId = promotedSourceMemory.Id,
                Strategy = "manual",
                Reason = "Promote via HTTP smoke test",
                Confidence = 0.95
            });

            var compression = await client.RunCompressionAsync(new CompressionRequest
            {
                OperationId = "http-compress-1",
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TaskKind = CompressionTaskKind.Summarize,
                Inputs = new[] { ingested },
                Options = new CompressionOptions
                {
                    GenerateIndexHints = true,
                    PreserveSourceRefs = true
                }
            });

            var stableMemoryQuery = await client.QueryMemoryAsync(new ContextMemoryQuery
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Layer = ContextMemoryLayer.Stable,
                Status = ContextMemoryStatus.Stable,
                Take = 10
            });

            var workingMemoryQuery = await client.QueryMemoryAsync(new ContextMemoryQuery
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Layer = ContextMemoryLayer.Working,
                Status = ContextMemoryStatus.Verified,
                Take = 10
            });

            var recentWorkingMemory = await client.GetRecentWorkingMemoryAsync(
                "workspace-test",
                "collection-test",
                take: 10);
            var activeContextQuery = await client.GetWorkingMemoryActiveContextAsync(
                "workspace-test",
                "collection-test");
            var currentTaskQuery = await client.GetWorkingMemoryCurrentTaskAsync(
                "workspace-test",
                "collection-test");

            var relationQuery = await client.QueryRelationsAsync(
                "http-raw-1",
                "workspace-test",
                "collection-test");

            var hardConstraints = await client.QueryConstraintsAsync(
                "workspace-test",
                "collection-test",
                level: ConstraintLevel.Hard,
                take: 10);

            var package = await client.BuildPackageAsync(new ContextPackageRequest
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                QueryText = "HTTP raw",
                RequiredTags = new[] { "p1" },
                TokenBudget = 1200,
                Policy = new ContextPackagePolicy
                {
                    WorkspaceId = "workspace-test",
                    CollectionId = "collection-test",
                    TokenBudget = 1200,
                    IncludeGlobalContext = true,
                    IncludeHardConstraints = true,
                    IncludeSoftConstraints = true,
                    IncludeWorkingMemory = true,
                    IncludeStableMemory = true,
                    IncludeRecentRawContext = true,
                    MaxRecentItems = 10,
                    SectionOrder = new[]
                    {
                        "hard_constraints",
                        "global_context",
                        "working_memory",
                        "recent_context",
                        "stable_memory",
                        "soft_constraints",
                        "related_context"
                    },
                    SectionTokenBudgets = new Dictionary<string, int>
                    {
                        ["recent_context"] = 200
                    }
                }
            });

            CollectionAssert.AreEqual(
                new[] { "hard_constraints", "global_context", "working_memory", "recent_context", "stable_memory", "soft_constraints", "related_context" },
                package.Sections.Select(section => section.Name).ToArray());
            StringAssert.Contains(
                package.Sections.Single(section => section.Name == "global_context").Content,
                "Global preference: keep context compact.");
            StringAssert.Contains(
                package.Sections.Single(section => section.Name == "hard_constraints").Content,
                "Preserve source context for API smoke.");
            Assert.IsTrue(package.EstimatedTokens <= 1200);
            Assert.AreEqual("http-raw-1", ingested.Id);
            Assert.AreEqual("http-memory-1", promotedSourceMemory.Id);
            Assert.AreEqual("http-memory-2", workingMemory.Id);
            Assert.AreEqual(ContextMemoryLayer.Working, workingMemory.Layer);
            Assert.AreEqual(ContextMemoryStatus.Verified, workingMemory.Status);
            Assert.AreEqual("http-working-1", shortTermMemory.Id);
            Assert.AreEqual("task-http-1", activeContext.CurrentTaskId);
            Assert.AreEqual("task-http-1", currentTask.TaskId);
            Assert.AreEqual(ContextMemoryStatus.Stable, promoted.ToStatus);
            Assert.AreEqual(1, compression.GeneratedItems.Count);
            Assert.AreEqual(1, stableMemoryQuery.Count);
            Assert.AreEqual("http-memory-1", stableMemoryQuery[0].Id);
            Assert.AreEqual(1, workingMemoryQuery.Count);
            Assert.AreEqual("http-memory-2", workingMemoryQuery[0].Id);
            Assert.AreEqual(ContextMemoryStatus.Verified, workingMemoryQuery[0].Status);
            CollectionAssert.Contains(recentWorkingMemory.Select(item => item.Id).ToArray(), "http-working-1");
            Assert.AreEqual("task-http-1", activeContextQuery?.CurrentTaskId);
            Assert.AreEqual("task-http-1", currentTaskQuery?.TaskId);
            Assert.IsTrue(relationQuery.Incoming.Any(item => item.SourceId == compression.GeneratedItems[0].Id));
            Assert.IsTrue(hardConstraints.Count >= 1);
            CollectionAssert.Contains(relationQuery.Incoming.Select(item => item.TargetId).ToArray(), "http-raw-1");

            await client.ClearWorkingMemoryAsync("workspace-test", "collection-test");
            var clearedRecent = await client.GetRecentWorkingMemoryAsync("workspace-test", "collection-test", take: 10);
            var clearedActiveException = await Assert.ThrowsExceptionAsync<ContextCoreApiException>(() =>
                client.GetWorkingMemoryActiveContextAsync("workspace-test", "collection-test"));
            var clearedTaskException = await Assert.ThrowsExceptionAsync<ContextCoreApiException>(() =>
                client.GetWorkingMemoryCurrentTaskAsync("workspace-test", "collection-test"));

            Assert.AreEqual(0, clearedRecent.Count);
            Assert.AreEqual(ContextCoreErrorCodes.NotFound, clearedActiveException.ErrorResponse.ErrorCode);
            Assert.AreEqual(ContextCoreErrorCodes.NotFound, clearedTaskException.ErrorResponse.ErrorCode);
        }
        finally
        {
            DeleteTestRoot(rootPath);
        }
    }

    [TestMethod]
    public async Task ModelHttpApi_ShouldExposeSafeStatusAndRoutePreview()
    {
        var rootPath = CreateTestRootPath();
        var previousDeepSeekKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");

        try
        {
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "contextcore-http-test-secret");

            using var factory = new ContextCoreServiceFactory(rootPath);
            using var http = factory.CreateClient();

            var statusJson = await http.GetStringAsync("/api/model/status");
            using var statusDocument = JsonDocument.Parse(statusJson);
            var statusRoot = statusDocument.RootElement;

            Assert.IsFalse(statusJson.Contains("contextcore-http-test-secret", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(statusJson.Contains("\"apiKey\"", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(statusRoot.GetProperty("apiProviders").GetArrayLength() > 0);
            Assert.IsTrue(statusRoot.GetProperty("modelProfiles").GetArrayLength() > 0);
            Assert.IsTrue(statusRoot.GetProperty("models").GetArrayLength() > 0);
            Assert.IsTrue(statusRoot.GetProperty("routes").GetArrayLength() > 0);
            Assert.IsTrue(statusRoot.GetProperty("modelProfiles").EnumerateArray().Any(profile =>
                profile.GetProperty("name").GetString() == "mock"
                && profile.GetProperty("category").GetString() == "mock"));

            var routeResponse = await http.PostAsJsonAsync("/api/model/route/resolve", new
            {
                role = "GeneralCompression",
                taskKind = "Summarize",
                thinkingMode = "balanced",
                requiredCapabilities = new[] { "compression" }
            });
            routeResponse.EnsureSuccessStatusCode();

            var routeJson = await routeResponse.Content.ReadAsStringAsync();
            using var routeDocument = JsonDocument.Parse(routeJson);
            var routeRoot = routeDocument.RootElement;
            var primary = routeRoot.GetProperty("primary");

            Assert.AreEqual("角色精确匹配", routeRoot.GetProperty("routeSource").GetString());
            Assert.IsTrue(primary.GetProperty("found").GetBoolean(), routeJson);
            Assert.AreEqual("mock", primary.GetProperty("modelName").GetString());
            Assert.AreEqual("mock", primary.GetProperty("category").GetString());
            Assert.IsTrue(primary.GetProperty("capabilities").EnumerateArray().Any(capability =>
                capability.GetString() == "compression"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", previousDeepSeekKey);
            DeleteTestRoot(rootPath);
        }
    }

    private static async Task SeedFilesystemStateAsync(string rootPath)
    {
        var options = new FileStorageOptions { RootPath = rootPath };

        await new FileConstraintStore(options).SaveAsync(new ContextConstraint
        {
            Id = "hard-http-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Scope = ContextScope.Collection,
            Level = ConstraintLevel.Hard,
            Content = "Preserve source context for API smoke.",
            SourceRefs = new[] { "source:hard-http-1" },
            Status = ContextMemoryStatus.Verified,
            Confidence = 1.0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await new FileConstraintStore(options).SaveAsync(new ContextConstraint
        {
            Id = "soft-http-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Scope = ContextScope.Collection,
            Level = ConstraintLevel.Soft,
            Content = "Prefer compact packages.",
            SourceRefs = new[] { "source:soft-http-1" },
            Status = ContextMemoryStatus.Verified,
            Confidence = 1.0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await new FileGlobalContextStore(options).SaveAsync(new ContextGlobalItem
        {
            Id = "global-http-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Scope = ContextScope.Workspace,
            Type = "preference",
            Content = "Global preference: keep context compact.",
            ContentFormat = ContextContentFormat.PlainText,
            Tags = new[] { "global" },
            SourceRefs = new[] { "source:global-http-1" },
            Importance = 1.0,
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    private static string CreateTestRootPath()
    {
        return Path.Combine(
            Directory.GetCurrentDirectory(),
            "context-core-http-smoke-data",
            Guid.NewGuid().ToString("N"));
    }

    private static void DeleteTestRoot(string rootPath)
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private sealed class ContextCoreServiceFactory : WebApplicationFactory<Program>
    {
        private readonly string _rootPath;

        public ContextCoreServiceFactory(string rootPath)
        {
            _rootPath = rootPath;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:RootPath", _rootPath);
            builder.UseSetting("Compression:Provider", "mock");
            builder.UseSetting("JobWorker:Enabled", "false");
        }
    }
}
