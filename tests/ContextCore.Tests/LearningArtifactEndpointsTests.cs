using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.MemoryEvolution;
using ContextCore.Service.Endpoints;
using ContextCore.Service.Security;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ContextCore.Tests;

/// <summary>
/// Learning Artifact Plane 控制面端点测试（WP-K）：
/// 快照工件点查（Replay 重建入口）、列表、导出+落库、决策记录点查（审计）。
/// 处理器为 internal static（DefaultHttpContext 直接执行 IResult）。
/// </summary>
[TestClass]
[TestCategory("Decision")]
public sealed class LearningArtifactEndpointsTests
{
    private const string Ws = "ws-learning-api";

    private static FixedWorkspaceAccessor Workspace() => new(Ws);

    [TestMethod]
    public async Task GetArtifact_ExistingSnapshot_ReturnsArtifact()
    {
        var store = new InMemoryLearningArtifactStore();
        await store.SaveAsync(BuildArtifact("snapshot-api-1"));

        var result = await LearningArtifactEndpoints.GetArtifactAsync(store, Workspace(), "snapshot-api-1");
        var (status, artifact) = await ExecuteAsync<DatasetSnapshotArtifact>(result);

        Assert.AreEqual(StatusCodes.Status200OK, status);
        Assert.IsNotNull(artifact);
        Assert.AreEqual("snapshot-api-1", artifact!.Snapshot.SnapshotId);
        Assert.AreEqual("api-hash", artifact.Snapshot.ContentHash);
        Assert.AreEqual(0.8, artifact.Snapshot.CompletenessRatio!.Value, 0.0001);
    }

    [TestMethod]
    public async Task GetArtifact_UnknownSnapshot_Returns404()
    {
        var store = new InMemoryLearningArtifactStore();
        var result = await LearningArtifactEndpoints.GetArtifactAsync(store, Workspace(), "snapshot-missing");
        var (status, _) = await ExecuteAsync<ContextCoreErrorResponse>(result);
        Assert.AreEqual(StatusCodes.Status404NotFound, status);
    }

    [TestMethod]
    public async Task GetArtifact_CrossWorkspace_NotVisible()
    {
        var store = new InMemoryLearningArtifactStore();
        await store.SaveAsync(BuildArtifact("snapshot-api-1"));

        var result = await LearningArtifactEndpoints.GetArtifactAsync(
            store, new FixedWorkspaceAccessor("ws-other"), "snapshot-api-1");
        var (status, _) = await ExecuteAsync<ContextCoreErrorResponse>(result);
        Assert.AreEqual(StatusCodes.Status404NotFound, status);
    }

    [TestMethod]
    public async Task ListArtifacts_ReturnsRecentByWorkspace()
    {
        var store = new InMemoryLearningArtifactStore();
        await store.SaveAsync(BuildArtifact("snapshot-a"));
        await store.SaveAsync(BuildArtifact("snapshot-b"));
        var other = BuildArtifact("snapshot-c") with
        {
            Snapshot = BuildArtifact("snapshot-c").Snapshot with { WorkspaceId = "ws-other" }
        };
        await store.SaveAsync(other);

        var result = await LearningArtifactEndpoints.ListArtifactsAsync(store, Workspace(), take: 20);
        var (status, list) = await ExecuteAsync<LearningArtifactListResponse>(result);

        Assert.AreEqual(StatusCodes.Status200OK, status);
        Assert.IsNotNull(list);
        Assert.AreEqual(2, list!.Entries.Count, "仅返回本工作区工件（租户隔离）。");
        Assert.AreEqual("snapshot-b", list.Entries[0].Snapshot.SnapshotId, "最新入库在前。");
    }

    [TestMethod]
    public async Task ExportAndStore_ProducesSnapshot_AndPersistsArtifact()
    {
        var ledgerStore = new InMemoryUtilityLedgerStore();
        await ledgerStore.AppendEntriesAsync(new[]
        {
            new UtilityLedgerEntry
            {
                EntryId = "e-1",
                WorkspaceId = Ws,
                CollectionId = "col-api",
                CandidateItemId = "item-1",
                Expert = RetrievalExpert.Semantic,
                UtilityContribution = 0.9,
                DeterministicScore = 0.9,
                FinalScore = 0.88,
                IsSelected = true,
                DecisionId = "dec-api-1",
                PolicyVersion = "policy/v1",
                MaterializedAt = DateTimeOffset.UtcNow
            },
            new UtilityLedgerEntry
            {
                EntryId = "e-2",
                WorkspaceId = Ws,
                CollectionId = "col-api",
                CandidateItemId = "item-2",
                Expert = RetrievalExpert.Semantic,
                UtilityContribution = 0.2,
                DeterministicScore = 0.2,
                FinalScore = 0.18,
                IsSelected = false,
                DropReasonCode = "below-threshold",
                DecisionId = "dec-api-1",
                PolicyVersion = "policy/v1",
                MaterializedAt = DateTimeOffset.UtcNow
            }
        }, CancellationToken.None);

        var exporter = new TrainingDataExporter(ledgerStore);
        var artifactStore = new InMemoryLearningArtifactStore();
        using var tempDir = new TempDirectory();

        var result = await LearningArtifactEndpoints.ExportAndStoreAsync(
            exporter, artifactStore, Workspace(),
            new LearningArtifactExportRequest { OutputDirectory = tempDir.Path, ModelArtifactId = "model-api-1" });
        var (status, snapshot) = await ExecuteAsync<DatasetSnapshotReport>(result);

        Assert.AreEqual(StatusCodes.Status200OK, status);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(2, snapshot!.MaterializedCount);
        Assert.AreEqual(1.0, snapshot.CompletenessRatio!.Value, 0.0001, "完整率 = 物化 / 输入。");

        var rebuilt = await artifactStore.GetAsync(Ws, snapshot.SnapshotId);
        Assert.IsNotNull(rebuilt, "导出后工件落库可点查。");
        Assert.AreEqual(snapshot.ContentHash, rebuilt!.Snapshot.ContentHash);
    }

    [TestMethod]
    public async Task GetDecision_PersistedRecord_ReturnsRecord()
    {
        var trace = new InMemoryDecisionTraceStore();
        await trace.SaveAsync(new ContextDecisionRecord
        {
            DecisionId = "decision-api-1",
            Source = ContextDecisionSource.Retrieval,
            WorkspaceId = Ws,
            CollectionId = Ws,
            QueryText = "api query",
            Candidates = Array.Empty<ContextDecisionCandidate>(),
            PolicyVersion = "decision-schema/2.0",
            CreatedAt = DateTimeOffset.UtcNow
        });

        var result = await LearningArtifactEndpoints.GetDecisionAsync(trace, Workspace(), "decision-api-1");
        var (status, record) = await ExecuteAsync<ContextDecisionRecord>(result);

        Assert.AreEqual(StatusCodes.Status200OK, status);
        Assert.IsNotNull(record);
        Assert.AreEqual("decision-api-1", record!.DecisionId);
        Assert.AreEqual("api query", record.QueryText);
    }

    [TestMethod]
    public async Task GetDecision_Unknown_Returns404()
    {
        var trace = new InMemoryDecisionTraceStore();
        var result = await LearningArtifactEndpoints.GetDecisionAsync(trace, Workspace(), "decision-missing");
        var (status, _) = await ExecuteAsync<ContextCoreErrorResponse>(result);
        Assert.AreEqual(StatusCodes.Status404NotFound, status);
    }

    [TestMethod]
    public async Task ExportAndStore_EmptyDataset_QualityGateBlocksPersistence()
    {
        // WP-U：空数据集 → 质量闸门 Blocked → 不落库 + 422。
        var ledgerStore = new InMemoryUtilityLedgerStore();
        var exporter = new TrainingDataExporter(ledgerStore);
        var artifactStore = new InMemoryLearningArtifactStore();
        using var tempDir = new TempDirectory();

        var result = await LearningArtifactEndpoints.ExportAndStoreAsync(
            exporter, artifactStore, Workspace(),
            new LearningArtifactExportRequest { OutputDirectory = tempDir.Path, ModelArtifactId = "model-api" });
        var (status, _) = await ExecuteAsync<ContextCoreErrorResponse>(result);

        Assert.AreEqual(StatusCodes.Status422UnprocessableEntity, status, "空数据集应被质量闸门阻断。");
        Assert.AreEqual(0, (await artifactStore.ListRecentAsync(Ws)).Count, "Blocked 数据集不落库。");
    }

    // ── 辅助 ─────────────────────────────────────────────────────────────

    private static DefaultHttpContext Http()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.TraceIdentifier = "test-trace";
        httpContext.Response.Body = new MemoryStream();
        httpContext.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        return httpContext;
    }

    private static async Task<(int Status, T? Body)> ExecuteAsync<T>(IResult result) where T : class
    {
        var context = Http();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        // API 序列化默认 Web（camelCase + case-insensitive）。
        var body = await System.Text.Json.JsonSerializer.DeserializeAsync<T>(
            context.Response.Body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        return (context.Response.StatusCode, body);
    }

    private static DatasetSnapshotArtifact BuildArtifact(string snapshotId) => new()
    {
        Snapshot = new DatasetSnapshotReport
        {
            SnapshotId = snapshotId,
            SchemaVersion = "training-data-export/v1",
            CreatedAt = DateTimeOffset.UtcNow,
            WorkspaceId = Ws,
            CollectionId = "col-api",
            ModelArtifactId = "model-api",
            InputEvidenceCount = 10,
            MaterializedCount = 8,
            CompletenessRatio = 0.8,
            MissingCount = 2,
            MissingReasons = new[] { "below-threshold" },
            ContentHash = "api-hash",
            PolicyVersions = new[] { "policy/v1" },
            LineageDecisionCount = 4
        },
        DataFilePath = "/tmp/api-data.jsonl",
        ManifestFilePath = "/tmp/api-manifest.json",
        StoredAt = DateTimeOffset.UtcNow
    };

    private sealed class FixedWorkspaceAccessor : IWorkspaceContextAccessor
    {
        private readonly string _workspaceId;

        public FixedWorkspaceAccessor(string workspaceId) => _workspaceId = workspaceId;

        public WorkspaceContext? Current => new()
        {
            WorkspaceId = _workspaceId,
            Source = "test",
            ApiKeyId = "key-test",
            Roles = new[] { WorkspaceRole.Operator },
            IsAuthenticated = true
        };

        public void Set(WorkspaceContext context) { }

        public void Clear() { }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "cc-learning-api-" + Guid.NewGuid().ToString("N"));
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
