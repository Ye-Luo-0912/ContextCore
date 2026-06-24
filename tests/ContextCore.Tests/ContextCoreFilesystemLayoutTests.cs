using ContextCore.Abstractions.Models;
using ContextCore.Abstractions;
using ContextCore.Core.Services.Storage;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;

namespace ContextCore.Tests;

[TestClass]
public sealed class ContextCoreFilesystemLayoutTests
{
    [TestMethod]
    public void StorageResponsibilityRegistry_ShouldClassifyEveryArtifactKind()
    {
        var entries = StorageResponsibilityRegistry.GetEntries();
        var covered = entries
            .Where(item => item.ArtifactKind.HasValue)
            .Select(item => item.ArtifactKind!.Value)
            .ToHashSet();

        foreach (var kind in Enum.GetValues<ArtifactKind>())
        {
            Assert.IsTrue(covered.Contains(kind), $"Missing storage responsibility classification for {kind}.");
        }

        Assert.AreEqual(0, StorageResponsibilityRegistry.BuildReport().Diagnostics.Count);
    }

    [TestMethod]
    public void StorageResponsibilityRegistry_ShouldClassifyOperationalAndReportStores()
    {
        var entries = StorageResponsibilityRegistry.GetEntries();

        var vector = entries.Single(item => item.StoreKind == "VectorIndexEntry");
        Assert.AreEqual(StorageResponsibilityKind.IndexState, vector.Responsibility);
        Assert.AreEqual(StorageResponsibilityKind.DatabaseRecommended, vector.PreferredProvider);

        var relation = entries.Single(item => item.StoreKind == "RelationItem");
        Assert.AreEqual(StorageResponsibilityKind.OperationalState, relation.Responsibility);
        Assert.AreEqual(StorageResponsibilityKind.DatabaseRecommended, relation.PreferredProvider);

        var report = entries.Single(item => item.StoreKind == "EvalReport");
        Assert.AreEqual(StorageResponsibilityKind.ArtifactOnly, report.Responsibility);
        Assert.AreEqual(StorageResponsibilityKind.FileSystemPreferred, report.PreferredProvider);
    }

    [TestMethod]
    public void StorageResponsibilityRegistry_ShouldReportUnknownArtifactKindClassification()
    {
        var report = StorageResponsibilityRegistry.BuildReport(
        [
            new StorageResponsibilityEntry
            {
                SubjectId = nameof(ArtifactKind.Report),
                SubjectType = "ArtifactKind",
                ArtifactKind = ArtifactKind.Report,
                Responsibility = StorageResponsibilityKind.ArtifactOnly,
                PreferredProvider = StorageResponsibilityKind.FileSystemPreferred
            }
        ]);

        Assert.IsTrue(report.Diagnostics.Any(item =>
            item.StartsWith("UnknownArtifactKindClassification:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ArtifactKind_ShouldResolveStablePathForEveryKind()
    {
        var root = CreateTempRoot();
        var layout = new ContextCoreDataLayout(new FileStorageOptions { RootPath = root });

        foreach (var kind in Enum.GetValues<ArtifactKind>())
        {
            var descriptor = new ArtifactDescriptor
            {
                Kind = kind,
                WorkspaceId = "workspace-a",
                CollectionId = "collection-a",
                CapabilityId = "capability",
                ReportId = "report",
                Extension = ".json"
            };

            var first = layout.ResolveArtifactPath(descriptor);
            var second = layout.ResolveArtifactPath(descriptor);

            Assert.AreEqual(first, second);
            StringAssert.StartsWith(first, Path.GetFullPath(root));
        }
    }

    [TestMethod]
    public void ArtifactPath_ShouldNotEscapeRoot()
    {
        var root = CreateTempRoot();
        var layout = new ContextCoreDataLayout(new FileStorageOptions { RootPath = root });
        var descriptor = new ArtifactDescriptor
        {
            Kind = ArtifactKind.LearningFeedback,
            WorkspaceId = "../outside",
            CollectionId = "..\\escape",
            CapabilityId = "feedback",
            ReportId = "quality",
            Extension = ".json"
        };

        var path = layout.ResolveArtifactPath(descriptor);

        StringAssert.StartsWith(path, Path.GetFullPath(root));
        StringAssert.Contains(path, "outside");
        Assert.IsFalse(path.Contains("..", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SanitizeSegment_ShouldNormalizeWorkspaceAndCollectionIds()
    {
        var layout = new ContextCoreDataLayout(new FileStorageOptions { RootPath = CreateTempRoot() });

        Assert.AreEqual("workspace-escape", layout.SanitizeSegment("workspace/escape"));
        Assert.AreEqual("default", layout.SanitizeSegment(".."));
        Assert.AreEqual("collection-id", layout.SanitizeSegment(" collection:id "));
    }

    [TestMethod]
    public async Task ArtifactStore_ShouldWriteJsonAtomicallyAndCreateDirectory()
    {
        var root = CreateTempRoot();
        var store = new FileArtifactStore(new FileStorageOptions { RootPath = root });
        var descriptor = CreateDescriptor("atomic-report");

        var path = await store.WriteJsonAsync(descriptor, new { value = 42 });
        var manifest = await store.ListAsync();

        Assert.IsTrue(File.Exists(path));
        Assert.AreEqual(1, manifest.Count);
        StringAssert.Contains(await File.ReadAllTextAsync(path), "42");
    }

    [TestMethod]
    public async Task ArtifactStore_ShouldAppendJsonLineWithoutDestroyingExistingContent()
    {
        var root = CreateTempRoot();
        var store = new FileArtifactStore(new FileStorageOptions { RootPath = root });
        var descriptor = CreateDescriptor("events") with { Extension = ".jsonl" };

        var path = await store.AppendJsonLineAsync(descriptor, new { id = "a" });
        await store.AppendJsonLineAsync(descriptor, new { id = "b" });

        var lines = await File.ReadAllLinesAsync(path);
        Assert.AreEqual(2, lines.Length);
        StringAssert.Contains(lines[0], "\"a\"");
        StringAssert.Contains(lines[1], "\"b\"");
    }

    [TestMethod]
    public async Task ArtifactStore_ShouldUpsertManifestForSameDescriptor()
    {
        var root = CreateTempRoot();
        var store = new FileArtifactStore(new FileStorageOptions { RootPath = root });
        var descriptor = CreateDescriptor("stable-report");

        await store.WriteJsonAsync(descriptor, new { version = 1 });
        await store.WriteJsonAsync(descriptor, new { version = 2 });
        var manifest = await store.ListAsync();

        Assert.AreEqual(1, manifest.Count);
    }

    [TestMethod]
    public void LegacyReportPath_ShouldRouteFirstBatchReports()
    {
        var layout = new ContextCoreDataLayout(new FileStorageOptions { RootPath = CreateTempRoot() });

        var descriptor = layout.CreateDescriptorFromLegacyReportPath(
            "learning/feedback/learning-feedback-quality-report.json",
            "workspace-a",
            "collection-a");
        var path = layout.ResolveArtifactPath(descriptor);

        Assert.AreEqual(ArtifactKind.LearningFeedback, descriptor.Kind);
        StringAssert.Contains(path.Replace('\\', '/'), "workspaces/workspace-a/collections/collection-a/learning/feedback");
    }

    [TestMethod]
    public void ReportArtifactDescriptorFactory_ShouldResolveStableReportPaths()
    {
        var root = CreateTempRoot();
        var layout = new ContextCoreDataLayout(new FileStorageOptions { RootPath = root });
        var factory = new ReportArtifactDescriptorFactory();
        var descriptor = factory.CreateSnapshot(
            "eval/vector-query-shadow-eval-a3.json",
            "workspace-a",
            "collection-a");

        var first = layout.ResolveArtifactPath(descriptor);
        var second = layout.ResolveArtifactPath(descriptor);
        var latest = layout.ResolveArtifactPath(factory.CreateLatest(descriptor));

        Assert.AreEqual(ArtifactKind.Eval, descriptor.Kind);
        Assert.AreEqual("vector", descriptor.CapabilityId);
        Assert.AreEqual(first, second);
        StringAssert.Contains(first.Replace('\\', '/'), "eval/vector/");
        StringAssert.Contains(latest.Replace('\\', '/'), "latest.json");
    }

    [TestMethod]
    public async Task ReportArtifactMirrorWriter_ShouldWriteLegacyAndStandardWithManifest()
    {
        var root = CreateTempRoot();
        var legacyPath = Path.Combine(root, "legacy", "learning", "feedback", "learning-feedback-quality-report.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        await File.WriteAllTextAsync(legacyPath, "{\"ok\":true}");

        var writer = new ReportArtifactMirrorWriter(new FileStorageOptions { RootPath = root });
        var standardPaths = await writer.MirrorAsync(
            "learning/feedback/learning-feedback-quality-report.json",
            "{\"ok\":true}",
            "workspace-a",
            "collection-a",
            sourceCommand: "eval learning-feedback-quality");
        var manifest = await new FileArtifactStore(new FileStorageOptions { RootPath = root }).ListAsync();

        Assert.AreEqual(2, standardPaths.Count);
        Assert.IsTrue(File.Exists(legacyPath));
        Assert.IsTrue(standardPaths.All(File.Exists));
        Assert.AreEqual(2, manifest.Count);
        Assert.IsTrue(manifest.All(entry => entry.SizeBytes > 0));
        Assert.IsTrue(manifest.All(entry => !string.IsNullOrWhiteSpace(entry.ContentHash)));
        Assert.IsTrue(manifest.Any(entry => entry.IsLatest));
        Assert.IsTrue(manifest.Any(entry => entry.IsSnapshot));
        Assert.IsTrue(manifest.All(entry => entry.LegacyPath == "learning/feedback/learning-feedback-quality-report.json"));
    }

    [TestMethod]
    public async Task ReportLayoutDiagnostics_ShouldCountLatestMirrorsAndDuplicateContentHash()
    {
        var root = CreateTempRoot();
        var writer = new ReportArtifactMirrorWriter(new FileStorageOptions { RootPath = root });
        await writer.MirrorAsync(
            "learning/router/router-intent-baseline-report.json",
            "{\"same\":true}",
            "workspace-a",
            "collection-a");
        await writer.MirrorAsync(
            "learning/ranker/candidate-reranker-shadow-eval-a3.json",
            "{\"same\":true}",
            "workspace-a",
            "collection-a");
        var layout = new ContextCoreDataLayout(new FileStorageOptions { RootPath = root });

        var diagnostics = layout.BuildReportLayoutDiagnostics();

        Assert.AreEqual(4, diagnostics.ManifestCount);
        Assert.AreEqual(2, diagnostics.LatestReportCount);
        Assert.AreEqual(4, diagnostics.LegacyMirroredCount);
        Assert.AreEqual(0, diagnostics.MissingStandardArtifactCount);
        Assert.IsTrue(diagnostics.DuplicateContentHashCount > 0);
        Assert.IsTrue(diagnostics.ReportCountByKind.ContainsKey(nameof(ArtifactKind.Router)));
        Assert.IsTrue(diagnostics.ReportCountByKind.ContainsKey(nameof(ArtifactKind.Ranker)));
    }

    [TestMethod]
    public void MemoryArtifactKinds_ShouldResolveExpectedLayerPaths()
    {
        var layout = new ContextCoreDataLayout(new FileStorageOptions { RootPath = CreateTempRoot() });
        var expected = new Dictionary<ArtifactKind, string>
        {
            [ArtifactKind.MemoryShortTermRawEvent] = "memory/short-term/raw-events",
            [ArtifactKind.MemoryShortTermWorkingItem] = "memory/short-term/working-items",
            [ArtifactKind.MemoryShortTermArchive] = "memory/short-term/archive",
            [ArtifactKind.MemoryShortTermCompactionRun] = "memory/short-term/compaction",
            [ArtifactKind.MemoryTemporalItem] = "memory/temporal/items",
            [ArtifactKind.MemoryTemporalArchive] = "memory/temporal/archive",
            [ArtifactKind.MemoryTemporalDiagnostics] = "memory/temporal/diagnostics",
            [ArtifactKind.MemoryCandidateItem] = "memory/candidate/items",
            [ArtifactKind.MemoryCandidateReview] = "memory/candidate/reviews",
            [ArtifactKind.MemoryCandidateDiagnostics] = "memory/candidate/diagnostics",
            [ArtifactKind.MemoryCandidateEvidence] = "memory/candidate/evidence",
            [ArtifactKind.MemoryStableItem] = "memory/stable/items",
            [ArtifactKind.MemoryStableLifecycleReview] = "memory/stable/lifecycle-reviews",
            [ArtifactKind.MemoryStableReplacementChain] = "memory/stable/replacement-chains",
            [ArtifactKind.MemoryStableProvenance] = "memory/stable/provenance",
            [ArtifactKind.MemoryStableDiagnostics] = "memory/stable/diagnostics"
        };

        foreach (var (kind, expectedFragment) in expected)
        {
            var path = layout.ResolveArtifactPath(new ArtifactDescriptor
            {
                Kind = kind,
                WorkspaceId = "workspace-a",
                CollectionId = "collection-a",
                ReportId = "artifact",
                Extension = ".jsonl"
            }).Replace('\\', '/');

            StringAssert.Contains(path, expectedFragment);
            StringAssert.StartsWith(path, Path.GetFullPath(layout.RootPath).Replace('\\', '/'));
        }
    }

    [TestMethod]
    public async Task TemporalPlaceholderPath_ShouldResolveAndBeWritable()
    {
        var root = CreateTempRoot();
        var store = new FileArtifactStore(new FileStorageOptions { RootPath = root });
        var descriptor = new ArtifactDescriptor
        {
            Kind = ArtifactKind.MemoryTemporalDiagnostics,
            WorkspaceId = "workspace-a",
            CollectionId = "collection-a",
            ReportId = "placeholder",
            Extension = ".json"
        };

        var path = await store.WriteJsonAsync(descriptor, new { ready = true });

        StringAssert.Contains(path.Replace('\\', '/'), "memory/temporal/diagnostics");
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public async Task ShortTermStore_ShouldWriteThroughLayoutResolver()
    {
        var root = CreateTempRoot();
        var paths = new FilePathResolver(new FileStorageOptions { RootPath = root });
        var store = new FileShortTermMemoryStore(paths, new FileFormatSerializer(), new ShortTermMemoryPolicy());

        await store.AppendRawEventAsync(new ShortTermRawEvent
        {
            EventId = "raw-1",
            WorkspaceId = "workspace-a",
            CollectionId = "collection-a",
            SessionId = "session-a",
            Source = "test",
            EventKind = "note",
            Content = "通用中文内容"
        });

        var newPath = paths.GetShortTermRawEventsJsonlPath("workspace-a", "collection-a");
        Assert.IsTrue(File.Exists(newPath));
        StringAssert.Contains(newPath.Replace('\\', '/'), "memory/short-term/raw-events");
        Assert.IsFalse(File.Exists(paths.GetLegacyShortTermRawEventsJsonlPath("workspace-a", "collection-a")));
    }

    [TestMethod]
    public async Task ShortTermStore_ShouldReadLegacyRawEvents()
    {
        var root = CreateTempRoot();
        var paths = new FilePathResolver(new FileStorageOptions { RootPath = root });
        var jsonLines = new FileJsonLineStore(new FileFormatSerializer());
        await jsonLines.WriteAsync(paths.GetLegacyShortTermRawEventsJsonlPath("workspace-a", "collection-a"), new[]
        {
            new ShortTermRawEvent
            {
                EventId = "legacy-raw-1",
                WorkspaceId = "workspace-a",
                CollectionId = "collection-a",
                SessionId = "session-a",
                Source = "legacy",
                EventKind = "note",
                Content = "旧路径内容"
            }
        });
        var store = new FileShortTermMemoryStore(paths, new FileFormatSerializer(), new ShortTermMemoryPolicy());

        var results = await store.QueryRawEventsAsync(new ShortTermRawEventQuery
        {
            WorkspaceId = "workspace-a",
            CollectionId = "collection-a",
            Take = 10
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("legacy-raw-1", results[0].EventId);
    }

    [TestMethod]
    public async Task CandidateReviewStore_ShouldWriteThroughLayoutResolver_AndReadLegacyFallback()
    {
        var root = CreateTempRoot();
        var paths = new FilePathResolver(new FileStorageOptions { RootPath = root });
        var serializer = new FileFormatSerializer();
        var store = new FileCandidateMemoryReviewStore(paths, serializer);

        await store.AppendReviewAsync(CreateCandidateReview("review-new", "candidate-a", "workspace-a", "collection-a"));
        await new FileJsonLineStore(serializer).WriteAsync(
            paths.GetLegacyCandidateMemoryReviewsJsonlPath("workspace-a", "collection-a"),
            new[] { CreateCandidateReview("review-legacy", "candidate-a", "workspace-a", "collection-a") });

        var newPath = paths.GetCandidateMemoryReviewsJsonlPath("workspace-a", "collection-a");
        var reviews = await store.QueryReviewsAsync("candidate-a");

        Assert.IsTrue(File.Exists(newPath));
        StringAssert.Contains(newPath.Replace('\\', '/'), "memory/candidate/reviews");
        Assert.AreEqual(2, reviews.Count);
    }

    [TestMethod]
    public async Task StableLifecycleReviewStore_ShouldWriteThroughLayoutResolver_AndReadLegacyFallback()
    {
        var root = CreateTempRoot();
        var paths = new FilePathResolver(new FileStorageOptions { RootPath = root });
        var serializer = new FileFormatSerializer();
        var store = new FileStableLifecycleReviewStore(paths, serializer);

        await store.AppendReviewAsync(CreateStableReview("review-new", "stable-a", "workspace-a", "collection-a"));
        await new FileJsonLineStore(serializer).WriteAsync(
            paths.GetLegacyStableLifecycleReviewsJsonlPath("workspace-a", "collection-a"),
            new[] { CreateStableReview("review-legacy", "stable-a", "workspace-a", "collection-a") });

        var newPath = paths.GetStableLifecycleReviewsJsonlPath("workspace-a", "collection-a");
        var reviews = await store.QueryReviewsAsync("stable-a");

        Assert.IsTrue(File.Exists(newPath));
        StringAssert.Contains(newPath.Replace('\\', '/'), "memory/stable/lifecycle-reviews");
        Assert.AreEqual(2, reviews.Count);
    }

    [TestMethod]
    public async Task StableMemoryStore_ShouldReadLegacyStableMemoryPath()
    {
        var root = CreateTempRoot();
        var paths = new FilePathResolver(new FileStorageOptions { RootPath = root });
        var serializer = new FileFormatSerializer();
        await new FileJsonLineStore(serializer).WriteAsync(
            paths.GetLegacyStableMemoryJsonlPath("workspace-a", "collection-a"),
            new[]
            {
                new ContextMemoryItem
                {
                    Id = "stable-legacy",
                    WorkspaceId = "workspace-a",
                    CollectionId = "collection-a",
                    Layer = ContextMemoryLayer.Stable,
                    Status = ContextMemoryStatus.Stable,
                    Type = "note",
                    Content = "旧稳定记忆",
                    UpdatedAt = DateTimeOffset.UtcNow
                }
            });
        var store = new FileMemoryStore(paths, serializer);

        var results = await store.QueryAsync(new ContextMemoryQuery
        {
            WorkspaceId = "workspace-a",
            CollectionId = "collection-a",
            Layer = ContextMemoryLayer.Stable,
            Take = 10
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("stable-legacy", results[0].Id);
    }

    [TestMethod]
    public void MemoryLayoutDiagnostics_ShouldReportTemporalPlaceholderAndLegacyFallback()
    {
        var root = CreateTempRoot();
        var paths = new FilePathResolver(new FileStorageOptions { RootPath = root });
        var layout = new ContextCoreDataLayout(new FileStorageOptions { RootPath = root });
        Directory.CreateDirectory(Path.GetDirectoryName(paths.GetTemporalMemoryItemsJsonlPath("workspace-a", "collection-a"))!);
        Directory.CreateDirectory(Path.GetDirectoryName(layout.ResolveArtifactPath(new ArtifactDescriptor
        {
            Kind = ArtifactKind.MemoryTemporalArchive,
            WorkspaceId = "workspace-a",
            CollectionId = "collection-a",
            ReportId = "temporal-archive",
            Extension = ".jsonl"
        }))!);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.GetTemporalMemoryDiagnosticsJsonlPath("workspace-a", "collection-a"))!);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.GetLegacyShortTermRawEventsJsonlPath("workspace-a", "collection-a"))!);
        File.WriteAllText(paths.GetLegacyShortTermRawEventsJsonlPath("workspace-a", "collection-a"), string.Empty);

        var diagnostics = layout.BuildMemoryLayoutDiagnostics("workspace-a", "collection-a");

        Assert.IsTrue(diagnostics.TemporalPlaceholderReady);
        Assert.AreEqual(1, diagnostics.LegacyFallbackCount);
        Assert.IsTrue(diagnostics.MemoryLayerPaths.ContainsKey(nameof(ArtifactKind.MemoryTemporalItem)));
    }

    [TestMethod]
    public void TraceArtifactKinds_ShouldResolveExpectedDateShardedPaths()
    {
        var layout = new ContextCoreDataLayout(new FileStorageOptions { RootPath = CreateTempRoot() });
        var expected = new Dictionary<ArtifactKind, string>
        {
            [ArtifactKind.TraceRetrieval] = "traces/retrieval/20260612",
            [ArtifactKind.TracePlanning] = "traces/planning/20260612",
            [ArtifactKind.TraceToolCall] = "traces/tool-calls/20260612/operation-escape",
            [ArtifactKind.TraceRouterShadow] = "traces/router-shadow/20260612",
            [ArtifactKind.TraceRankerShadow] = "traces/ranker-shadow/20260612",
            [ArtifactKind.TraceVectorShadow] = "traces/vector-shadow/20260612",
            [ArtifactKind.TraceGraphShadow] = "traces/graph-shadow/20260612",
            [ArtifactKind.TraceRelationDualWrite] = "traces/relation-dual-write/20260612",
            [ArtifactKind.TraceRelationShadowRead] = "traces/relation-shadow-read/20260612",
            [ArtifactKind.TraceRelationProviderSwitch] = "traces/relation-provider-switch/20260612",
            [ArtifactKind.TraceLearningFeedbackDualWrite] = "traces/learning-feedback-dual-write/20260612",
            [ArtifactKind.TraceLearningFeedbackShadowRead] = "traces/learning-feedback-shadow-read/20260612",
            [ArtifactKind.TraceLearningFeedbackProviderSwitch] = "traces/learning-feedback-provider-switch/20260612",
            [ArtifactKind.TraceJobQueueDualWrite] = "traces/job-queue-dual-write/20260612",
            [ArtifactKind.TraceJobQueueShadowRead] = "traces/job-queue-shadow-read/20260612",
            [ArtifactKind.TraceJobQueueScopedWorkerCanary] = "traces/job-queue-scoped-worker-canary/20260612",
            [ArtifactKind.TraceJobQueueLimitedWorkerScopeObservation] = "traces/job-queue-limited-worker-scope-observation/20260612",
            [ArtifactKind.TracePackageBuild] = "traces/package-build/20260612",
            [ArtifactKind.TraceModelCall] = "traces/model-calls/20260612",
            [ArtifactKind.TraceError] = "traces/errors/20260612"
        };

        foreach (var (kind, expectedFragment) in expected)
        {
            var path = layout.ResolveArtifactPath(new ArtifactDescriptor
            {
                Kind = kind,
                WorkspaceId = "workspace-a",
                CollectionId = "collection-a",
                OperationId = "operation/escape",
                ReportId = "trace",
                DateShard = "20260612",
                Extension = ".jsonl"
            }).Replace('\\', '/');

            StringAssert.Contains(path, expectedFragment);
            Assert.IsFalse(path.Contains("operation/escape", StringComparison.Ordinal));
            StringAssert.StartsWith(path, Path.GetFullPath(layout.RootPath).Replace('\\', '/'));
        }
    }

    [TestMethod]
    public void TraceDescriptorFactory_ShouldProduceStableDateShardPath()
    {
        var root = CreateTempRoot();
        var layout = new ContextCoreDataLayout(new FileStorageOptions { RootPath = root });
        var factory = new TraceArtifactDescriptorFactory();
        var descriptor = factory.Create(
            "workspace-a",
            "collection-a",
            ArtifactKind.TraceGraphShadow,
            dateShard: "20260612");

        var first = layout.ResolveArtifactPath(descriptor);
        var second = layout.ResolveArtifactPath(descriptor);

        Assert.AreEqual(first, second);
        StringAssert.Contains(first.Replace('\\', '/'), "traces/graph-shadow/20260612");
    }

    [TestMethod]
    public async Task TraceArtifactWriter_ShouldAppendJsonLineAndUpsertManifest()
    {
        var root = CreateTempRoot();
        var store = new FileArtifactStore(new FileStorageOptions { RootPath = root });
        var writer = new TraceArtifactWriter(store);

        var path = await writer.AppendTraceJsonLineAsync(
            "workspace-a",
            "collection-a",
            ArtifactKind.TraceVectorShadow,
            new { id = "trace-1" },
            dateShard: "20260612");
        await writer.AppendTraceJsonLineAsync(
            "workspace-a",
            "collection-a",
            ArtifactKind.TraceVectorShadow,
            new { id = "trace-2" },
            dateShard: "20260612");
        var manifest = await store.ListAsync(ArtifactKind.TraceVectorShadow);

        Assert.AreEqual(2, File.ReadAllLines(path).Length);
        Assert.AreEqual(1, manifest.Count);
        StringAssert.Contains(path.Replace('\\', '/'), "traces/vector-shadow/20260612");
    }

    [TestMethod]
    public async Task TraceArtifactWriter_ShouldWriteToolCallArtifactsUnderOperationDirectory()
    {
        var root = CreateTempRoot();
        var writer = new TraceArtifactWriter(new FileArtifactStore(new FileStorageOptions { RootPath = root }));

        var requestPath = await writer.WriteToolCallRequestAsync(
            "workspace-a",
            "collection-a",
            "../operation:1",
            new { tool = "shell" },
            "20260612");
        var responsePath = await writer.WriteToolCallResponseAsync(
            "workspace-a",
            "collection-a",
            "../operation:1",
            new { ok = true },
            "20260612");
        var errorPath = await writer.WriteToolCallErrorAsync(
            "workspace-a",
            "collection-a",
            "../operation:1",
            new { error = "failed" },
            "20260612");

        StringAssert.Contains(requestPath.Replace('\\', '/'), "traces/tool-calls/20260612/operation-1/request.json");
        StringAssert.Contains(responsePath.Replace('\\', '/'), "traces/tool-calls/20260612/operation-1/response.json");
        StringAssert.Contains(errorPath.Replace('\\', '/'), "traces/tool-calls/20260612/operation-1/error.json");
        Assert.IsFalse(requestPath.Contains("..", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RetrievalTraceStore_ShouldWriteThroughLayoutResolver_AndReadLegacyFallback()
    {
        var root = CreateTempRoot();
        var paths = new FilePathResolver(new FileStorageOptions { RootPath = root });
        var serializer = new FileFormatSerializer();
        var store = new FileRetrievalTraceStore(paths, serializer);

        await store.SaveAsync(new ContextRetrievalTrace
        {
            RetrievalId = "trace-new",
            WorkspaceId = "workspace-a",
            CollectionId = "collection-a",
            QueryText = "new",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await new FileJsonLineStore(serializer).WriteAsync(
            paths.GetLegacyRetrievalTraceJsonlPath("workspace-a", "collection-a"),
            new[]
            {
                new ContextRetrievalTrace
                {
                    RetrievalId = "trace-legacy",
                    WorkspaceId = "workspace-a",
                    CollectionId = "collection-a",
                    QueryText = "legacy",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
                }
            });

        var path = paths.GetRetrievalTraceJsonlPath("workspace-a", "collection-a");
        var traces = await store.QueryRecentAsync("workspace-a", "collection-a", 10);

        Assert.IsTrue(File.Exists(path));
        StringAssert.Contains(path.Replace('\\', '/'), "traces/retrieval/");
        Assert.AreEqual(2, traces.Count);
    }

    [TestMethod]
    public void TraceLayoutDiagnostics_ShouldReportToolCallAndLegacyFallback()
    {
        var root = CreateTempRoot();
        var paths = new FilePathResolver(new FileStorageOptions { RootPath = root });
        Directory.CreateDirectory(Path.GetDirectoryName(paths.GetRetrievalTraceJsonlPath("workspace-a", "collection-a"))!);
        File.WriteAllText(paths.GetRetrievalTraceJsonlPath("workspace-a", "collection-a"), "{}");
        Directory.CreateDirectory(Path.Combine(
            root,
            "workspaces",
            "workspace-a",
            "collections",
            "collection-a",
            "traces",
            "tool-calls"));
        Directory.CreateDirectory(Path.GetDirectoryName(paths.GetLegacyRetrievalTraceJsonlPath("workspace-a", "collection-a"))!);
        File.WriteAllText(paths.GetLegacyRetrievalTraceJsonlPath("workspace-a", "collection-a"), string.Empty);
        var layout = new ContextCoreDataLayout(new FileStorageOptions { RootPath = root });

        var diagnostics = layout.BuildTraceLayoutDiagnostics("workspace-a", "collection-a");

        Assert.IsTrue(diagnostics.ToolCallPlaceholderReady);
        Assert.AreEqual(1, diagnostics.RetrievalTraceCount);
        Assert.AreEqual(1, diagnostics.LegacyFallbackCount);
        Assert.IsTrue(diagnostics.TraceCategoryPaths.ContainsKey(nameof(ArtifactKind.TraceToolCall)));
    }

    private static ArtifactDescriptor CreateDescriptor(string reportId)
        => new()
        {
            Kind = ArtifactKind.Report,
            WorkspaceId = "workspace",
            CollectionId = "collection",
            CapabilityId = "tests",
            ReportId = reportId,
            Extension = ".json"
        };

    private static CandidateMemoryReviewRecord CreateCandidateReview(
        string reviewId,
        string candidateId,
        string workspaceId,
        string collectionId)
        => new()
        {
            ReviewId = reviewId,
            CandidateId = candidateId,
            CandidateKind = "CandidateMemory",
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Action = CandidateMemoryReviewActions.NeedsMoreEvidence,
            FromStatus = ContextMemoryStatus.Candidate,
            ToStatus = ContextMemoryStatus.Candidate,
            Reviewer = "tester",
            Reason = "layout test",
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static StableLifecycleReviewRecord CreateStableReview(
        string reviewId,
        string stableItemId,
        string workspaceId,
        string collectionId)
        => new()
        {
            ReviewId = reviewId,
            StableItemId = stableItemId,
            StableKind = "StableMemory",
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Action = StableLifecycleReviewActions.Deprecate,
            FromStatus = ContextMemoryStatus.Stable,
            ToStatus = ContextMemoryStatus.Deprecated,
            FromLifecycle = "Active",
            ToLifecycle = "Deprecated",
            Reviewer = "tester",
            Reason = "layout test",
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "contextcore-layout-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
