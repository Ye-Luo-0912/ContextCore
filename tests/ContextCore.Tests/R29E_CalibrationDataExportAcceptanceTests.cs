using System.IO;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Commands;
using ContextCore.ControlRoom.Services;
using ContextCore.Core.Services.MemoryEvolution;

namespace ContextCore.Tests;

// ===========================================================================
// R29 WP-E-4：校准数据导出器验收测试
//
// 目标：
//   验证 CalibrationDataExporter 从 IUtilityLedgerStore 查询 ledger 条目，
//   转换为 CalibrationDataRecord（predicted / observed / weight / metadata）后写入 JSONL，
//   并生成含 SHA-256 哈希与正负样本统计的 sidecar manifest。
//
// 设计原则：
//   1. 导出器是只读边界：不修改 ledger 状态；可重复执行（幂等）。
//   2. 输出格式对齐 TrainingDataExporter：JSONL + camelCase JSON。
//   3. predicted / observed / weight / metadata 四段式字段分类对齐 ML 校准流水线。
//   4. SHA-256 哈希验证文件完整性；manifest 追溯 model artifact 版本。
//   5. 默认仅导出 ModelScore 非 null 的条目（校准必须有模型预测分数）。
//
// 验收点：
//   - 导出 JSONL 文件包含所有匹配 ledger 条目（仅 ModelScore 非 null）
//   - predicted 字段（ModelScore / DeterministicScore / FinalScore）正确映射
//   - observed 字段（IsSelected / DropReasonCode）正确映射
//   - weight 字段（UtilityContribution 或 1.0）正确映射
//   - metadata 字段（DecisionId / CandidateItemId / 作用域 / Expert / 时间戳 / PolicyVersion）正确映射
//   - manifest 含 SHA-256 哈希与 model artifact 追溯与正负样本统计
//   - 过滤条件（WorkspaceId / CollectionId / Since / Until / DecisionId）生效
//   - RequireModelScore=true 默认排除 ModelScore=null 的条目
//   - RequireModelScore=false 包含所有条目（诊断用途）
//   - 空结果路径生成空 JSONL 文件但 manifest 仍写入
//   - 重复导出覆盖输出文件（幂等）
//   - CLI 命令 CalibrationDataCommand 端到端可用
// ===========================================================================

[TestClass]
[TestCategory("R29")]
[TestCategory("WP-E-4")]
public sealed class R29E_CalibrationDataExportAcceptanceTests
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
        DateTimeOffset materializedAt,
        double utilityContribution = 0.8)
    {
        return new UtilityLedgerEntry
        {
            EntryId = entryId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            CandidateItemId = candidateItemId,
            Expert = expert,
            UtilityContribution = utilityContribution,
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
    public async Task ExportAsync_WritesJsonlWithPredictedObservedWeightMetadataFields()
    {
        // 准备：2 个 ledger 条目（1 selected + 1 dropped，均有 ModelScore）。
        var ws = "ws-cal-1";
        var col = "col-cal-1";
        var materializedAt = DateTimeOffset.UtcNow;
        var ledgerStore = new InMemoryUtilityLedgerStore();
        var entries = new[]
        {
            MakeEntry("e1", ws, col, "item-selected", RetrievalExpert.Semantic,
                deterministicScore: 0.9, modelScore: 0.85, finalScore: 0.88,
                isSelected: true, dropReasonCode: null,
                decisionId: "dec-1", policyVersion: "policy/v1",
                materializedAt: materializedAt, utilityContribution: 0.7),
            MakeEntry("e2", ws, col, "item-dropped", RetrievalExpert.Lexical,
                deterministicScore: 0.3, modelScore: 0.25, finalScore: 0.3,
                isSelected: false, dropReasonCode: "SectionQuotaExceeded",
                decisionId: "dec-1", policyVersion: "policy/v1",
                materializedAt: materializedAt, utilityContribution: 0.5)
        };
        await ledgerStore.AppendEntriesAsync(entries, CancellationToken.None);

        var exporter = new CalibrationDataExporter(ledgerStore);
        using var tempDir = new TempDirectory();
        var outDir = tempDir.Path;

        var request = new CalibrationDataExportRequest
        {
            WorkspaceId = ws,
            CollectionId = col,
            OutputDirectory = outDir,
            ModelArtifactId = "model-artifact-001",
            ModelName = "test-classifier"
        };

        // 执行导出。
        var result = await exporter.ExportAsync(request, CancellationToken.None);

        // 验证结果元数据。
        Assert.AreEqual(2, result.EntryCount, "应导出 2 条记录。");
        Assert.AreEqual(1, result.PositiveCount, "正样本（IsSelected=true）应为 1。");
        Assert.AreEqual(1, result.NegativeCount, "负样本（IsSelected=false）应为 1。");
        Assert.AreEqual(0.5, result.PositiveRatio, 0.001, "正样本比例应为 0.5。");
        Assert.AreEqual(ws, result.WorkspaceId);
        Assert.AreEqual(col, result.CollectionId);
        Assert.AreEqual("model-artifact-001", result.ModelArtifactId);
        Assert.AreEqual("test-classifier", result.ModelName);
        Assert.AreEqual("calibration-data-export/v1", result.SchemaVersion);
        Assert.IsFalse(string.IsNullOrEmpty(result.Sha256Hash), "SHA-256 哈希应非空。");
        Assert.IsTrue(File.Exists(result.DataFilePath), "JSONL 文件应存在。");
        Assert.IsTrue(File.Exists(result.ManifestFilePath), "manifest 文件应存在。");

        // 读取 JSONL 并验证字段。
        var lines = await File.ReadAllLinesAsync(result.DataFilePath);
        Assert.AreEqual(2, lines.Length, "JSONL 应有 2 行。");

        var records = lines
            .Select(l => JsonSerializer.Deserialize<CalibrationDataRecord>(l, JsonOptions)!)
            .ToDictionary(r => r.CandidateItemId);

        var selectedRecord = records["item-selected"];
        var droppedRecord = records["item-dropped"];

        // predicted 字段
        Assert.AreEqual(0.85, selectedRecord.ModelScore, "predicted: ModelScore 应正确映射。");
        Assert.AreEqual(0.9, selectedRecord.DeterministicScore, "predicted: DeterministicScore 应正确映射。");
        Assert.AreEqual(0.88, selectedRecord.FinalScore, "predicted: FinalScore 应正确映射。");

        // observed 字段
        Assert.IsTrue(selectedRecord.IsSelected, "observed: IsSelected=true 应正确映射。");
        Assert.IsNull(selectedRecord.DropReasonCode, "observed: 选中条目 DropReasonCode 应为 null。");
        Assert.IsFalse(droppedRecord.IsSelected, "observed: IsSelected=false 应正确映射。");
        Assert.AreEqual("SectionQuotaExceeded", droppedRecord.DropReasonCode, "observed: DropReasonCode 应正确映射。");

        // weight 字段（使用 UtilityContribution）
        Assert.AreEqual(0.7, selectedRecord.Weight, "weight: 应使用 UtilityContribution=0.7。");
        Assert.AreEqual(0.5, droppedRecord.Weight, "weight: 应使用 UtilityContribution=0.5。");

        // metadata 字段
        Assert.AreEqual("dec-1", selectedRecord.DecisionId, "metadata: DecisionId 应正确映射。");
        Assert.AreEqual("item-selected", selectedRecord.CandidateItemId, "metadata: CandidateItemId 应正确映射。");
        Assert.AreEqual(ws, selectedRecord.WorkspaceId, "metadata: WorkspaceId 应正确映射。");
        Assert.AreEqual(col, selectedRecord.CollectionId, "metadata: CollectionId 应正确映射。");
        Assert.AreEqual("Semantic", selectedRecord.Expert, "metadata: Expert 应正确映射为枚举字符串。");
        Assert.AreEqual("policy/v1", selectedRecord.PolicyVersion, "metadata: PolicyVersion 应正确映射。");
    }

    [TestMethod]
    public async Task ExportAsync_DefaultRequireModelScore_ExcludesNullModelScoreEntries()
    {
        // 准备：3 个 ledger 条目（2 个有 ModelScore + 1 个 ModelScore=null）。
        var ws = "ws-cal-2";
        var now = DateTimeOffset.UtcNow;
        var ledgerStore = new InMemoryUtilityLedgerStore();
        await ledgerStore.AppendEntriesAsync(new[]
        {
            MakeEntry("e1", ws, "col", "item-1", RetrievalExpert.Semantic, 0.9, 0.85, 0.88, true, null, "dec-1", "p/v1", now),
            MakeEntry("e2", ws, "col", "item-2", RetrievalExpert.Lexical, 0.3, 0.25, 0.3, false, "QuotaExceeded", "dec-1", "p/v1", now),
            MakeEntry("e3", ws, "col", "item-3", RetrievalExpert.Graph, 0.5, null, 0.5, true, null, "dec-1", "p/v1", now)
        }, CancellationToken.None);

        var exporter = new CalibrationDataExporter(ledgerStore);
        using var tempDir = new TempDirectory();

        // 默认 RequireModelScore=true：应仅导出 2 条（排除 ModelScore=null 的 e3）
        var request = new CalibrationDataExportRequest
        {
            WorkspaceId = ws,
            OutputDirectory = tempDir.Path
        };

        var result = await exporter.ExportAsync(request, CancellationToken.None);

        Assert.AreEqual(2, result.EntryCount, "默认应排除 ModelScore=null 的条目。");
        Assert.AreEqual(1, result.PositiveCount, "正样本应为 1（item-1）。");
        Assert.AreEqual(1, result.NegativeCount, "负样本应为 1（item-2）。");

        var lines = await File.ReadAllLinesAsync(result.DataFilePath);
        Assert.AreEqual(2, lines.Length, "JSONL 应有 2 行（不含 ModelScore=null）。");
    }

    [TestMethod]
    public async Task ExportAsync_RequireModelScoreFalse_IncludesAllEntries()
    {
        // 准备：3 个 ledger 条目（2 个有 ModelScore + 1 个 ModelScore=null）。
        var ws = "ws-cal-3";
        var now = DateTimeOffset.UtcNow;
        var ledgerStore = new InMemoryUtilityLedgerStore();
        await ledgerStore.AppendEntriesAsync(new[]
        {
            MakeEntry("e1", ws, "col", "item-1", RetrievalExpert.Semantic, 0.9, 0.85, 0.88, true, null, "dec-1", "p/v1", now),
            MakeEntry("e2", ws, "col", "item-2", RetrievalExpert.Lexical, 0.3, 0.25, 0.3, false, "QuotaExceeded", "dec-1", "p/v1", now),
            MakeEntry("e3", ws, "col", "item-3", RetrievalExpert.Graph, 0.5, null, 0.5, true, null, "dec-1", "p/v1", now)
        }, CancellationToken.None);

        var exporter = new CalibrationDataExporter(ledgerStore);
        using var tempDir = new TempDirectory();

        // RequireModelScore=false：应导出全部 3 条（诊断用途）
        var request = new CalibrationDataExportRequest
        {
            WorkspaceId = ws,
            OutputDirectory = tempDir.Path,
            RequireModelScore = false
        };

        var result = await exporter.ExportAsync(request, CancellationToken.None);

        Assert.AreEqual(3, result.EntryCount, "RequireModelScore=false 应导出全部 3 条。");
        Assert.AreEqual(2, result.PositiveCount, "正样本应为 2（item-1 + item-3）。");
        Assert.AreEqual(1, result.NegativeCount, "负样本应为 1（item-2）。");

        var lines = await File.ReadAllLinesAsync(result.DataFilePath);
        Assert.AreEqual(3, lines.Length, "JSONL 应有 3 行。");

        var records = lines
            .Select(l => JsonSerializer.Deserialize<CalibrationDataRecord>(l, JsonOptions)!)
            .ToDictionary(r => r.CandidateItemId);

        Assert.IsNull(records["item-3"].ModelScore, "item-3 的 ModelScore 应为 null。");
    }

    [TestMethod]
    public async Task ExportAsync_ZeroUtilityContribution_DefaultsToWeightOne()
    {
        // 准备：UtilityContribution=0 的条目，weight 应默认为 1.0
        var ws = "ws-cal-weight";
        var now = DateTimeOffset.UtcNow;
        var ledgerStore = new InMemoryUtilityLedgerStore();
        await ledgerStore.AppendEntriesAsync(new[]
        {
            MakeEntry("e1", ws, "col", "item-zero", RetrievalExpert.Semantic,
                0.9, 0.85, 0.88, true, null, "dec-1", "p/v1", now, utilityContribution: 0.0)
        }, CancellationToken.None);

        var exporter = new CalibrationDataExporter(ledgerStore);
        using var tempDir = new TempDirectory();

        var request = new CalibrationDataExportRequest
        {
            WorkspaceId = ws,
            OutputDirectory = tempDir.Path
        };

        var result = await exporter.ExportAsync(request, CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(result.DataFilePath);
        var record = JsonSerializer.Deserialize<CalibrationDataRecord>(lines[0], JsonOptions)!;
        Assert.AreEqual(1.0, record.Weight, "UtilityContribution=0 时 weight 应默认为 1.0。");
    }

    [TestMethod]
    public async Task ExportAsync_EmptyResults_StillWritesManifest()
    {
        // 准备：空 ledger
        var ledgerStore = new InMemoryUtilityLedgerStore();
        var exporter = new CalibrationDataExporter(ledgerStore);
        using var tempDir = new TempDirectory();

        var request = new CalibrationDataExportRequest
        {
            WorkspaceId = "ws-empty",
            OutputDirectory = tempDir.Path
        };

        var result = await exporter.ExportAsync(request, CancellationToken.None);

        Assert.AreEqual(0, result.EntryCount, "空 ledger 应导出 0 条。");
        Assert.AreEqual(0, result.PositiveCount);
        Assert.AreEqual(0, result.NegativeCount);
        Assert.AreEqual(0.0, result.PositiveRatio);
        Assert.IsTrue(File.Exists(result.DataFilePath), "空结果仍应生成 JSONL 文件（空内容）。");
        Assert.IsTrue(File.Exists(result.ManifestFilePath), "空结果仍应生成 manifest 文件。");

        var dataContent = await File.ReadAllTextAsync(result.DataFilePath);
        Assert.AreEqual(string.Empty, dataContent, "空结果 JSONL 应为空字符串。");
    }

    [TestMethod]
    public async Task ExportAsync_IsIdempotent_OverwritesExistingFiles()
    {
        var ws = "ws-cal-idem";
        var now = DateTimeOffset.UtcNow;
        var ledgerStore = new InMemoryUtilityLedgerStore();
        await ledgerStore.AppendEntriesAsync(new[]
        {
            MakeEntry("e1", ws, "col", "item-1", RetrievalExpert.Semantic, 0.9, 0.85, 0.88, true, null, "dec-1", "p/v1", now)
        }, CancellationToken.None);

        var exporter = new CalibrationDataExporter(ledgerStore);
        using var tempDir = new TempDirectory();

        var request = new CalibrationDataExportRequest
        {
            WorkspaceId = ws,
            OutputDirectory = tempDir.Path
        };

        // 第一次导出
        var result1 = await exporter.ExportAsync(request, CancellationToken.None);
        var hash1 = result1.Sha256Hash;
        Assert.AreEqual(1, result1.EntryCount);

        // 第二次导出（应覆盖）
        var result2 = await exporter.ExportAsync(request, CancellationToken.None);
        Assert.AreEqual(1, result2.EntryCount);
        Assert.AreEqual(hash1, result2.Sha256Hash, "重复导出应生成相同 SHA-256（幂等）。");
    }

    [TestMethod]
    public async Task ExportAsync_FiltersByTimeRange()
    {
        var ws = "ws-cal-time";
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ledgerStore = new InMemoryUtilityLedgerStore();
        await ledgerStore.AppendEntriesAsync(new[]
        {
            MakeEntry("e1", ws, "col", "item-1", RetrievalExpert.Semantic, 0.9, 0.85, 0.88, true, null, "dec-1", "p/v1", baseTime),
            MakeEntry("e2", ws, "col", "item-2", RetrievalExpert.Lexical, 0.3, 0.25, 0.3, false, "Quota", "dec-1", "p/v1", baseTime.AddHours(12)),
            MakeEntry("e3", ws, "col", "item-3", RetrievalExpert.Graph, 0.5, 0.4, 0.45, true, null, "dec-1", "p/v1", baseTime.AddHours(36))
        }, CancellationToken.None);

        var exporter = new CalibrationDataExporter(ledgerStore);
        using var tempDir = new TempDirectory();

        // 仅导出 baseTime+6h 到 baseTime+30h 之间的条目
        var request = new CalibrationDataExportRequest
        {
            WorkspaceId = ws,
            OutputDirectory = tempDir.Path,
            Since = baseTime.AddHours(6),
            Until = baseTime.AddHours(30)
        };

        var result = await exporter.ExportAsync(request, CancellationToken.None);

        Assert.AreEqual(1, result.EntryCount, "时间范围过滤：应只导出 1 条（item-2）。");

        var lines = await File.ReadAllLinesAsync(result.DataFilePath);
        var record = JsonSerializer.Deserialize<CalibrationDataRecord>(lines[0], JsonOptions)!;
        Assert.AreEqual("item-2", record.CandidateItemId);
    }

    [TestMethod]
    public async Task ExportAsync_FiltersByDecisionId()
    {
        var ws = "ws-cal-dec";
        var now = DateTimeOffset.UtcNow;
        var ledgerStore = new InMemoryUtilityLedgerStore();
        await ledgerStore.AppendEntriesAsync(new[]
        {
            MakeEntry("e1", ws, "col", "item-1", RetrievalExpert.Semantic, 0.9, 0.85, 0.88, true, null, "dec-A", "p/v1", now),
            MakeEntry("e2", ws, "col", "item-2", RetrievalExpert.Lexical, 0.3, 0.25, 0.3, false, "Quota", "dec-B", "p/v1", now)
        }, CancellationToken.None);

        var exporter = new CalibrationDataExporter(ledgerStore);
        using var tempDir = new TempDirectory();

        var request = new CalibrationDataExportRequest
        {
            WorkspaceId = ws,
            OutputDirectory = tempDir.Path,
            DecisionId = "dec-A"
        };

        var result = await exporter.ExportAsync(request, CancellationToken.None);

        Assert.AreEqual(1, result.EntryCount, "DecisionId 过滤：应只导出 1 条。");

        var lines = await File.ReadAllLinesAsync(result.DataFilePath);
        var record = JsonSerializer.Deserialize<CalibrationDataRecord>(lines[0], JsonOptions)!;
        Assert.AreEqual("dec-A", record.DecisionId);
    }

    [TestMethod]
    public async Task ExportAsync_ManifestContainsPositiveNegativeStats()
    {
        var ws = "ws-cal-manifest";
        var now = DateTimeOffset.UtcNow;
        var ledgerStore = new InMemoryUtilityLedgerStore();
        await ledgerStore.AppendEntriesAsync(new[]
        {
            MakeEntry("e1", ws, "col", "item-1", RetrievalExpert.Semantic, 0.9, 0.85, 0.88, true, null, "dec-1", "p/v1", now),
            MakeEntry("e2", ws, "col", "item-2", RetrievalExpert.Lexical, 0.3, 0.25, 0.3, false, "Quota", "dec-1", "p/v1", now),
            MakeEntry("e3", ws, "col", "item-3", RetrievalExpert.Graph, 0.5, 0.4, 0.45, true, null, "dec-1", "p/v1", now)
        }, CancellationToken.None);

        var exporter = new CalibrationDataExporter(ledgerStore);
        using var tempDir = new TempDirectory();

        var request = new CalibrationDataExportRequest
        {
            WorkspaceId = ws,
            OutputDirectory = tempDir.Path,
            ModelArtifactId = "ma-001",
            ModelName = "test-model"
        };

        var result = await exporter.ExportAsync(request, CancellationToken.None);

        var manifestJson = await File.ReadAllTextAsync(result.ManifestFilePath);
        var manifest = JsonSerializer.Deserialize<CalibrationDataExportManifest>(manifestJson, JsonOptions)!;

        Assert.AreEqual(3, manifest.EntryCount);
        Assert.AreEqual(2, manifest.PositiveCount, "正样本应为 2。");
        Assert.AreEqual(1, manifest.NegativeCount, "负样本应为 1。");
        Assert.AreEqual(2.0 / 3.0, manifest.PositiveRatio, 0.001, "正样本比例应为 2/3。");
        Assert.AreEqual("ma-001", manifest.ModelArtifactId);
        Assert.AreEqual("test-model", manifest.ModelName);
        Assert.AreEqual("calibration-data-export/v1", manifest.SchemaVersion);
        Assert.IsFalse(string.IsNullOrEmpty(manifest.Sha256Hash));
        Assert.AreEqual("calibration-data.jsonl", manifest.DataFileName);
    }

    // ===========================================================================
    // CLI 集成测试：通过 CalibrationDataCommand 走完整命令行路径
    // ===========================================================================

    [TestMethod]
    public async Task CalibrationDataCommand_ExportsJsonlAndManifestFromCliArgs()
    {
        var ws = "ws-cli-cal-1";
        var col = "col-cli-cal-1";
        var now = DateTimeOffset.UtcNow;
        var ledgerStore = new InMemoryUtilityLedgerStore();
        await ledgerStore.AppendEntriesAsync(new[]
        {
            MakeEntry("e1", ws, col, "item-sel", RetrievalExpert.Semantic, 0.9, 0.85, 0.88, true, null, "dec-1", "policy/v1", now),
            MakeEntry("e2", ws, col, "item-drop", RetrievalExpert.Lexical, 0.3, 0.25, 0.3, false, "QuotaExceeded", "dec-1", "policy/v1", now)
        }, CancellationToken.None);

        var exporter = new CalibrationDataExporter(ledgerStore);
        var state = new ControlRoomState
        {
            WorkspaceId = ws,
            CollectionId = col,
            StorageKind = "memory",
            CalibrationDataExporter = exporter
        };
        var service = new ControlRoomService(state);
        using var tempDir = new TempDirectory();

        var args = new List<string> { "--out", tempDir.Path, "--model-artifact-id", "ma-cli-1", "--model-name", "cli-model" };
        await CalibrationDataCommand.ExecuteAsync(service, args, CancellationToken.None);

        var dataFile = System.IO.Path.Combine(tempDir.Path, "calibration-data.jsonl");
        var manifestFile = System.IO.Path.Combine(tempDir.Path, "calibration-data.manifest.json");
        Assert.IsTrue(File.Exists(dataFile), "CLI 应生成 JSONL 文件。");
        Assert.IsTrue(File.Exists(manifestFile), "CLI 应生成 manifest 文件。");

        var lines = await File.ReadAllLinesAsync(dataFile);
        Assert.AreEqual(2, lines.Length, "JSONL 应包含 2 行。");

        var manifestJson = await File.ReadAllTextAsync(manifestFile);
        var manifest = JsonSerializer.Deserialize<CalibrationDataExportManifest>(manifestJson, JsonOptions)!;
        Assert.AreEqual(2, manifest.EntryCount);
        Assert.AreEqual(1, manifest.PositiveCount);
        Assert.AreEqual(1, manifest.NegativeCount);
        Assert.AreEqual("ma-cli-1", manifest.ModelArtifactId);
        Assert.AreEqual("cli-model", manifest.ModelName);
    }

    [TestMethod]
    public async Task CalibrationDataCommand_RejectsMissingOutArg()
    {
        var state = new ControlRoomState
        {
            WorkspaceId = "ws",
            CollectionId = "col",
            StorageKind = "memory",
            CalibrationDataExporter = new CalibrationDataExporter(new InMemoryUtilityLedgerStore())
        };
        var service = new ControlRoomService(state);

        var originalExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            await CalibrationDataCommand.ExecuteAsync(service, new List<string>(), CancellationToken.None);
            Assert.AreEqual(2, Environment.ExitCode, "缺少 --out 应返回退出码 2。");
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [TestMethod]
    public async Task CalibrationDataCommand_RejectsServiceModeWithoutExporter()
    {
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
            await CalibrationDataCommand.ExecuteAsync(service, args, CancellationToken.None);
            Assert.AreEqual(2, Environment.ExitCode, "Service 模式未配置导出器应返回退出码 2。");
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [TestMethod]
    public async Task CalibrationDataCommand_IncludesNoModelScoreWhenFlagSet()
    {
        var ws = "ws-cli-cal-2";
        var col = "col-cli-cal-2";
        var now = DateTimeOffset.UtcNow;
        var ledgerStore = new InMemoryUtilityLedgerStore();
        await ledgerStore.AppendEntriesAsync(new[]
        {
            MakeEntry("e1", ws, col, "item-1", RetrievalExpert.Semantic, 0.9, 0.85, 0.88, true, null, "dec-1", "p/v1", now),
            MakeEntry("e2", ws, col, "item-2", RetrievalExpert.Graph, 0.5, null, 0.5, true, null, "dec-1", "p/v1", now)
        }, CancellationToken.None);

        var exporter = new CalibrationDataExporter(ledgerStore);
        var state = new ControlRoomState
        {
            WorkspaceId = ws,
            CollectionId = col,
            StorageKind = "memory",
            CalibrationDataExporter = exporter
        };
        var service = new ControlRoomService(state);
        using var tempDir = new TempDirectory();

        // 默认行为：应排除 ModelScore=null 的条目
        var args = new List<string> { "--out", tempDir.Path };
        await CalibrationDataCommand.ExecuteAsync(service, args, CancellationToken.None);

        var dataFile = System.IO.Path.Combine(tempDir.Path, "calibration-data.jsonl");
        var lines = await File.ReadAllLinesAsync(dataFile);
        Assert.AreEqual(1, lines.Length, "默认应排除 ModelScore=null 的条目。");

        // 清理文件，再次使用 --include-no-model-score 导出
        File.Delete(dataFile);
        File.Delete(System.IO.Path.Combine(tempDir.Path, "calibration-data.manifest.json"));

        args = new List<string> { "--out", tempDir.Path, "--include-no-model-score" };
        await CalibrationDataCommand.ExecuteAsync(service, args, CancellationToken.None);

        lines = await File.ReadAllLinesAsync(dataFile);
        Assert.AreEqual(2, lines.Length, "--include-no-model-score 应包含所有条目。");
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
