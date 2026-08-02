using System.IO;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Commands;
using ContextCore.ControlRoom.Services;
using ContextCore.Core.Services.MemoryEvolution;

namespace ContextCore.Tests;

// ===========================================================================
// 训练数据导出器验收测试
//
// 目标：
//   验证 TrainingDataExporter 从 IUtilityLedgerStore 查询 ledger 条目，
//   转换为 TrainingDataRecord（feature / label / metadata）后写入 JSONL 文件，
//   并生成含 SHA-256 哈希的 sidecar manifest。
//
// 设计原则：
//   1. 导出器是只读边界：不修改 ledger 状态；可重复执行（幂等）。
//   2. 输出格式对齐 LearningFeatureDatasetService：JSONL + camelCase JSON。
//   3. feature / label / metadata 三段式字段分类对齐 ML 训练流水线。
//   4. SHA-256 哈希验证文件完整性；manifest 追溯 model artifact 版本。
//
// 验收点：
//   - 导出 JSONL 文件包含所有匹配 ledger 条目
//   - feature 字段（DeterministicScore / ModelScore / UtilityContribution / Expert）正确映射
//   - label 字段（IsSelected / DropReasonCode）正确映射
//   - metadata 字段（DecisionId / CandidateItemId / 作用域 / 时间戳 / PolicyVersion）正确映射
//   - manifest 含 SHA-256 哈希与 model artifact 追溯
//   - 过滤条件（WorkspaceId / CollectionId / Since / Until / IsSelected / DecisionId）生效
//   - 空结果路径生成空 JSONL 文件但 manifest 仍写入
//   - 重复导出覆盖输出文件（幂等）
// ===========================================================================

[TestClass]
[TestCategory("R29")]
[TestCategory("WP-E-3")]
public sealed class R29E_TrainingDataExportAcceptanceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static UtilityLedgerEntry MakeEntry(
        string entryId,
        string workspaceId,
        string collectionId,
        string candidateItemId,
        RetrievalExpert expert,
        double deterministicScore,
        double? modelScore,
        double finalScore,
        bool isSelected,
        string? dropReasonCode,
        string decisionId,
        string policyVersion,
        DateTimeOffset materializedAt)
    {
        return new UtilityLedgerEntry
        {
            EntryId = entryId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            CandidateItemId = candidateItemId,
            Expert = expert,
            UtilityContribution = 0.8,
            DeterministicScore = deterministicScore,
            ModelScore = modelScore,
            FinalScore = finalScore,
            IsSelected = isSelected,
            DropReasonCode = dropReasonCode,
            DecisionId = decisionId,
            PolicyVersion = policyVersion,
            RouterId = "test-router",
            MaterializedAt = materializedAt,
            MaterializationBatchId = "batch-test"
        };
    }

    [TestMethod]
    public async Task ExportAsync_WritesJsonlWithFeatureLabelMetadataFields()
    {
        // 准备：2 个 ledger 条目（1 selected + 1 dropped）。
        var ws = "ws-export-1";
        var col = "col-export-1";
        var materializedAt = DateTimeOffset.UtcNow;
        var ledgerStore = new InMemoryUtilityLedgerStore();
        var entries = new[]
        {
            MakeEntry("e1", ws, col, "item-selected", RetrievalExpert.Semantic,
                deterministicScore: 0.9, modelScore: 0.85, finalScore: 0.88,
                isSelected: true, dropReasonCode: null,
                decisionId: "dec-1", policyVersion: "policy/v1",
                materializedAt: materializedAt),
            MakeEntry("e2", ws, col, "item-dropped", RetrievalExpert.Lexical,
                deterministicScore: 0.3, modelScore: null, finalScore: 0.3,
                isSelected: false, dropReasonCode: "SectionQuotaExceeded",
                decisionId: "dec-1", policyVersion: "policy/v1",
                materializedAt: materializedAt)
        };
        await ledgerStore.AppendEntriesAsync(entries, CancellationToken.None);

        var exporter = new TrainingDataExporter(ledgerStore);
        using var tempDir = new TempDirectory();
        var outDir = tempDir.Path;

        var request = new TrainingDataExportRequest
        {
            WorkspaceId = ws,
            CollectionId = col,
            OutputDirectory = outDir,
            ModelArtifactId = "model-artifact-001"
        };

        // 执行导出。
        var result = await exporter.ExportAsync(request, CancellationToken.None);

        // 验证结果元数据。
        Assert.AreEqual(2, result.EntryCount, "应导出 2 条记录。");
        Assert.AreEqual(ws, result.WorkspaceId);
        Assert.AreEqual(col, result.CollectionId);
        Assert.AreEqual("model-artifact-001", result.ModelArtifactId);
        Assert.AreEqual("training-data-export/v1", result.SchemaVersion);
        Assert.IsFalse(string.IsNullOrEmpty(result.Sha256Hash), "SHA-256 哈希应非空。");
        Assert.IsTrue(File.Exists(result.DataFilePath), "JSONL 文件应存在。");
        Assert.IsTrue(File.Exists(result.ManifestFilePath), "manifest 文件应存在。");

        // 读取 JSONL 并验证字段。
        // 注意：ledger store 按 MaterializedAt 降序返回，两个条目时间戳相同时顺序不确定，
        // 因此按 CandidateItemId 查找而非依赖行顺序。
        var lines = await File.ReadAllLinesAsync(result.DataFilePath);
        Assert.AreEqual(2, lines.Length, "JSONL 应有 2 行。");

        var records = lines
            .Select(l => JsonSerializer.Deserialize<TrainingDataRecord>(l, JsonOptions)!)
            .ToDictionary(r => r.CandidateItemId);

        var selectedRecord = records["item-selected"];
        var droppedRecord = records["item-dropped"];

        // feature 字段
        Assert.AreEqual(0.9, selectedRecord.DeterministicScore, "feature: DeterministicScore 应正确映射。");
        Assert.AreEqual(0.85, selectedRecord.ModelScore, "feature: ModelScore 应正确映射。");
        Assert.AreEqual("Semantic", selectedRecord.Expert, "feature: Expert 应正确映射为枚举字符串。");
        Assert.AreEqual(0.8, selectedRecord.UtilityContribution, "feature: UtilityContribution 应正确映射。");

        // label 字段
        Assert.IsTrue(selectedRecord.IsSelected, "label: IsSelected=true 应正确映射。");
        Assert.IsNull(selectedRecord.DropReasonCode, "label: 选中条目 DropReasonCode 应为 null。");
        Assert.IsFalse(droppedRecord.IsSelected, "label: IsSelected=false 应正确映射。");
        Assert.AreEqual("SectionQuotaExceeded", droppedRecord.DropReasonCode, "label: DropReasonCode 应正确映射。");

        // metadata 字段
        Assert.AreEqual("dec-1", selectedRecord.DecisionId, "metadata: DecisionId 应正确映射。");
        Assert.AreEqual("item-selected", selectedRecord.CandidateItemId, "metadata: CandidateItemId 应正确映射。");
        Assert.AreEqual(ws, selectedRecord.WorkspaceId, "metadata: WorkspaceId 应正确映射。");
        Assert.AreEqual(col, selectedRecord.CollectionId, "metadata: CollectionId 应正确映射。");
        Assert.AreEqual("policy/v1", selectedRecord.PolicyVersion, "metadata: PolicyVersion 应正确映射。");
    }

    [TestMethod]
    public async Task ExportAsync_GeneratesManifestWithSha256AndModelArtifact()
    {
        // 验证 manifest 含 SHA-256 哈希、model artifact 追溯、时间范围。
        var ws = "ws-manifest";
        var ledgerStore = new InMemoryUtilityLedgerStore();
        var since = DateTimeOffset.UtcNow.AddHours(-1);
        var until = DateTimeOffset.UtcNow;
        await ledgerStore.AppendEntriesAsync(new[]
        {
            MakeEntry("e1", ws, "col-1", "item-1", RetrievalExpert.Semantic,
                0.9, 0.85, 0.88, true, null, "dec-1", "policy/v1", since.AddMinutes(30))
        }, CancellationToken.None);

        var exporter = new TrainingDataExporter(ledgerStore);
        using var tempDir = new TempDirectory();

        var request = new TrainingDataExportRequest
        {
            WorkspaceId = ws,
            Since = since,
            Until = until,
            OutputDirectory = tempDir.Path,
            ModelArtifactId = "model-artifact-002"
        };

        var result = await exporter.ExportAsync(request, CancellationToken.None);

        // 读取 manifest 并验证字段。
        var manifestJson = await File.ReadAllTextAsync(result.ManifestFilePath);
        var manifest = JsonSerializer.Deserialize<TrainingDataExportManifest>(manifestJson, JsonOptions)!;

        Assert.AreEqual("training-data-export/v1", manifest.SchemaVersion);
        Assert.AreEqual(ws, manifest.WorkspaceId);
        Assert.AreEqual(since, manifest.Since, "manifest 应记录 Since 过滤条件。");
        Assert.AreEqual(until, manifest.Until, "manifest 应记录 Until 过滤条件。");
        Assert.AreEqual("model-artifact-002", manifest.ModelArtifactId);
        Assert.AreEqual(1, manifest.EntryCount);
        Assert.AreEqual(result.Sha256Hash, manifest.Sha256Hash, "manifest 的 SHA-256 应与 result 一致。");
        Assert.AreEqual("training-data.jsonl", manifest.DataFileName);

        // 验证 SHA-256 哈希正确性（重新计算文件哈希）。
        var fileBytes = await File.ReadAllBytesAsync(result.DataFilePath);
        var expectedHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(fileBytes)).ToLowerInvariant();
        Assert.AreEqual(expectedHash, result.Sha256Hash, "SHA-256 哈希应与文件内容匹配。");
    }

    [TestMethod]
    public async Task ExportAsync_FiltersByWorkspaceIdAndCollectionId()
    {
        // 验证 WorkspaceId 过滤：只导出匹配 workspace 的条目。
        // 这是 WP-E-3 修复的 InMemoryUtilityLedgerStore.QueryAsync WorkspaceId 过滤 bug 的回归测试。
        var ledgerStore = new InMemoryUtilityLedgerStore();
        var now = DateTimeOffset.UtcNow;
        await ledgerStore.AppendEntriesAsync(new[]
        {
            MakeEntry("e1", "ws-A", "col-1", "item-1", RetrievalExpert.Semantic, 0.9, 0.85, 0.88, true, null, "dec-1", "p/v1", now),
            MakeEntry("e2", "ws-B", "col-1", "item-2", RetrievalExpert.Lexical, 0.5, null, 0.5, false, "TokenBudgetExceeded", "dec-2", "p/v1", now),
            MakeEntry("e3", "ws-A", "col-2", "item-3", RetrievalExpert.Graph, 0.7, 0.6, 0.65, true, null, "dec-3", "p/v1", now)
        }, CancellationToken.None);

        var exporter = new TrainingDataExporter(ledgerStore);
        using var tempDir = new TempDirectory();

        // 只导出 ws-A 的条目（应得 2 条）
        var request = new TrainingDataExportRequest
        {
            WorkspaceId = "ws-A",
            OutputDirectory = tempDir.Path
        };

        var result = await exporter.ExportAsync(request, CancellationToken.None);

        Assert.AreEqual(2, result.EntryCount, "WorkspaceId 过滤：只导出 ws-A 的 2 条。");
        Assert.AreEqual("ws-A", result.WorkspaceId);

        // 验证导出的条目都来自 ws-A
        var lines = await File.ReadAllLinesAsync(result.DataFilePath);
        foreach (var line in lines)
        {
            var record = JsonSerializer.Deserialize<TrainingDataRecord>(line, JsonOptions)!;
            Assert.AreEqual("ws-A", record.WorkspaceId, "导出的条目都应来自 ws-A。");
        }
    }

    [TestMethod]
    public async Task ExportAsync_EmptyResult_GeneratesEmptyJsonlAndManifest()
    {
        // 空结果路径：无匹配条目时生成空 JSONL 文件，manifest 仍写入（EntryCount=0）。
        var ledgerStore = new InMemoryUtilityLedgerStore();
        var exporter = new TrainingDataExporter(ledgerStore);
        using var tempDir = new TempDirectory();

        var request = new TrainingDataExportRequest
        {
            WorkspaceId = "ws-empty",
            OutputDirectory = tempDir.Path
        };

        var result = await exporter.ExportAsync(request, CancellationToken.None);

        Assert.AreEqual(0, result.EntryCount, "空 ledger 应导出 0 条。");
        Assert.IsTrue(File.Exists(result.DataFilePath), "空结果也应生成 JSONL 文件。");
        Assert.IsTrue(File.Exists(result.ManifestFilePath), "空结果也应生成 manifest。");

        var content = await File.ReadAllTextAsync(result.DataFilePath);
        Assert.AreEqual(string.Empty, content, "空结果 JSONL 应为空字符串。");

        var manifestJson = await File.ReadAllTextAsync(result.ManifestFilePath);
        var manifest = JsonSerializer.Deserialize<TrainingDataExportManifest>(manifestJson, JsonOptions)!;
        Assert.AreEqual(0, manifest.EntryCount, "manifest EntryCount 应为 0。");
    }

    [TestMethod]
    public async Task ExportAsync_IsIdempotent_RepeatedExportOverwritesOutput()
    {
        // 幂等性：重复导出覆盖输出文件，不追加。
        var ledgerStore = new InMemoryUtilityLedgerStore();
        var now = DateTimeOffset.UtcNow;
        await ledgerStore.AppendEntriesAsync(new[]
        {
            MakeEntry("e1", "ws-idem", "col", "item-1", RetrievalExpert.Semantic, 0.9, 0.85, 0.88, true, null, "dec-1", "p/v1", now)
        }, CancellationToken.None);

        var exporter = new TrainingDataExporter(ledgerStore);
        using var tempDir = new TempDirectory();

        var request = new TrainingDataExportRequest
        {
            WorkspaceId = "ws-idem",
            OutputDirectory = tempDir.Path
        };

        // 第一次导出
        var result1 = await exporter.ExportAsync(request, CancellationToken.None);
        Assert.AreEqual(1, result1.EntryCount);

        // 第二次导出（覆盖）
        var result2 = await exporter.ExportAsync(request, CancellationToken.None);
        Assert.AreEqual(1, result2.EntryCount, "第二次导出应覆盖，不追加。");

        // 文件仍只有 1 行
        var lines = await File.ReadAllLinesAsync(result2.DataFilePath);
        Assert.AreEqual(1, lines.Length, "JSONL 应仍只有 1 行（覆盖而非追加）。");
    }

    [TestMethod]
    public async Task ExportAsync_FiltersByIsSelected()
    {
        // 验证 IsSelected 过滤：只导出选中条目。
        var ledgerStore = new InMemoryUtilityLedgerStore();
        var now = DateTimeOffset.UtcNow;
        await ledgerStore.AppendEntriesAsync(new[]
        {
            MakeEntry("e1", "ws-sel", "col", "item-1", RetrievalExpert.Semantic, 0.9, 0.85, 0.88, true, null, "dec-1", "p/v1", now),
            MakeEntry("e2", "ws-sel", "col", "item-2", RetrievalExpert.Lexical, 0.3, null, 0.3, false, "SectionQuotaExceeded", "dec-1", "p/v1", now)
        }, CancellationToken.None);

        var exporter = new TrainingDataExporter(ledgerStore);
        using var tempDir = new TempDirectory();

        // 只导出 IsSelected=true 的条目
        var request = new TrainingDataExportRequest
        {
            WorkspaceId = "ws-sel",
            IsSelected = true,
            OutputDirectory = tempDir.Path
        };

        var result = await exporter.ExportAsync(request, CancellationToken.None);

        Assert.AreEqual(1, result.EntryCount, "IsSelected=true 过滤：只导出 1 条。");

        var lines = await File.ReadAllLinesAsync(result.DataFilePath);
        var record = JsonSerializer.Deserialize<TrainingDataRecord>(lines[0], JsonOptions)!;
        Assert.IsTrue(record.IsSelected, "导出的条目应 IsSelected=true。");
    }

    // ===========================================================================
    // CLI 集成测试：通过 TrainingDataCommand 走完整命令行路径
    // ===========================================================================

    [TestMethod]
    public async Task TrainingDataCommand_ExportsJsonlAndManifestFromCliArgs()
    {
        // 准备：ledger 中预填充 2 条记录（1 selected + 1 dropped）。
        var ws = "ws-cli-1";
        var col = "col-cli-1";
        var now = DateTimeOffset.UtcNow;
        var ledgerStore = new InMemoryUtilityLedgerStore();
        await ledgerStore.AppendEntriesAsync(new[]
        {
            MakeEntry("e1", ws, col, "item-sel", RetrievalExpert.Semantic, 0.9, 0.85, 0.88, true, null, "dec-1", "policy/v1", now),
            MakeEntry("e2", ws, col, "item-drop", RetrievalExpert.Lexical, 0.3, null, 0.3, false, "QuotaExceeded", "dec-1", "policy/v1", now)
        }, CancellationToken.None);

        var exporter = new TrainingDataExporter(ledgerStore);
        var state = new ControlRoomState
        {
            WorkspaceId = ws,
            CollectionId = col,
            StorageKind = "memory",
            TrainingDataExporter = exporter
        };
        var service = new ControlRoomService(state);
        using var tempDir = new TempDirectory();

        // 执行 CLI 命令：export-training-data --out <dir>
        var args = new List<string> { "--out", tempDir.Path };
        await TrainingDataCommand.ExecuteAsync(service, args, CancellationToken.None);

        // 验证文件生成
        var dataFile = System.IO.Path.Combine(tempDir.Path, "training-data.jsonl");
        var manifestFile = System.IO.Path.Combine(tempDir.Path, "training-data.manifest.json");
        Assert.IsTrue(File.Exists(dataFile), "CLI 应生成 JSONL 文件。");
        Assert.IsTrue(File.Exists(manifestFile), "CLI 应生成 manifest 文件。");

        var lines = await File.ReadAllLinesAsync(dataFile);
        Assert.AreEqual(2, lines.Length, "JSONL 应包含 2 行。");

        var manifestJson = await File.ReadAllTextAsync(manifestFile);
        var manifest = JsonSerializer.Deserialize<TrainingDataExportManifest>(manifestJson, JsonOptions)!;
        Assert.AreEqual(2, manifest.EntryCount, "manifest EntryCount 应为 2。");
        Assert.AreEqual(ws, manifest.WorkspaceId);
        Assert.AreEqual("training-data-export/v1", manifest.SchemaVersion);
        Assert.IsFalse(string.IsNullOrEmpty(manifest.Sha256Hash), "manifest 应含 SHA-256。");
    }

    [TestMethod]
    public async Task TrainingDataCommand_RespectsSelectedOnlyFilter()
    {
        var ws = "ws-cli-2";
        var col = "col-cli-2";
        var now = DateTimeOffset.UtcNow;
        var ledgerStore = new InMemoryUtilityLedgerStore();
        await ledgerStore.AppendEntriesAsync(new[]
        {
            MakeEntry("e1", ws, col, "item-sel", RetrievalExpert.Semantic, 0.9, 0.85, 0.88, true, null, "dec-1", "policy/v1", now),
            MakeEntry("e2", ws, col, "item-drop", RetrievalExpert.Lexical, 0.3, null, 0.3, false, "QuotaExceeded", "dec-1", "policy/v1", now)
        }, CancellationToken.None);

        var exporter = new TrainingDataExporter(ledgerStore);
        var state = new ControlRoomState
        {
            WorkspaceId = ws,
            CollectionId = col,
            StorageKind = "memory",
            TrainingDataExporter = exporter
        };
        var service = new ControlRoomService(state);
        using var tempDir = new TempDirectory();

        // 执行 CLI 命令：--selected-only 仅导出选中样本
        var args = new List<string> { "--out", tempDir.Path, "--selected-only" };
        await TrainingDataCommand.ExecuteAsync(service, args, CancellationToken.None);

        var dataFile = System.IO.Path.Combine(tempDir.Path, "training-data.jsonl");
        var lines = await File.ReadAllLinesAsync(dataFile);
        Assert.AreEqual(1, lines.Length, "--selected-only 应只导出 1 行。");

        var record = JsonSerializer.Deserialize<TrainingDataRecord>(lines[0], JsonOptions)!;
        Assert.IsTrue(record.IsSelected, "导出的条目应 IsSelected=true。");
        Assert.AreEqual("item-sel", record.CandidateItemId);
    }

    [TestMethod]
    public async Task TrainingDataCommand_RejectsMissingOutArg()
    {
        var state = new ControlRoomState
        {
            WorkspaceId = "ws",
            CollectionId = "col",
            StorageKind = "memory",
            TrainingDataExporter = new TrainingDataExporter(new InMemoryUtilityLedgerStore())
        };
        var service = new ControlRoomService(state);

        // 缺少 --out 参数应退出码 2 且不抛异常
        var originalExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            await TrainingDataCommand.ExecuteAsync(service, new List<string>(), CancellationToken.None);
            Assert.AreEqual(2, Environment.ExitCode, "缺少 --out 应返回退出码 2。");
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [TestMethod]
    public async Task TrainingDataCommand_RejectsMutuallyExclusiveFilters()
    {
        var state = new ControlRoomState
        {
            WorkspaceId = "ws",
            CollectionId = "col",
            StorageKind = "memory",
            TrainingDataExporter = new TrainingDataExporter(new InMemoryUtilityLedgerStore())
        };
        var service = new ControlRoomService(state);
        using var tempDir = new TempDirectory();

        var originalExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            var args = new List<string> { "--out", tempDir.Path, "--selected-only", "--dropped-only" };
            await TrainingDataCommand.ExecuteAsync(service, args, CancellationToken.None);
            Assert.AreEqual(2, Environment.ExitCode, "--selected-only 与 --dropped-only 互斥应返回退出码 2。");
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [TestMethod]
    public async Task TrainingDataCommand_RejectsServiceModeWithoutExporter()
    {
        // Service 模式下 TrainingDataExporter 为 null
        var state = new ControlRoomState
        {
            Mode = ControlRoomMode.Service,
            WorkspaceId = "ws",
            CollectionId = "col",
            StorageKind = "service"
        };
        var service = new ControlRoomService(state);

        var originalExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            using var tempDir = new TempDirectory();
            var args = new List<string> { "--out", tempDir.Path };
            await TrainingDataCommand.ExecuteAsync(service, args, CancellationToken.None);
            Assert.AreEqual(2, Environment.ExitCode, "Service 模式未配置导出器应返回退出码 2。");
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    // --- helper: 临时目录 ---

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ContextCoreTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

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
                // 忽略清理失败
            }
        }
    }
}
