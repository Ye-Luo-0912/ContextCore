using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.MemoryEvolution;

/// <summary>
/// 校准数据导出器默认实现。
/// </summary>
/// <remarks>
/// 设计原则（对齐 R29 §9.1 校准数据导出目标 + 澄清 #4）：
///   1. 导出器是只读边界：通过 <see cref="IUtilityLedgerStore.QueryAsync"/> 查询 ledger 条目，
///      转换为 <see cref="CalibrationDataRecord"/>（predicted / observed / weight / metadata）后写入 JSONL。
///   2. 输出格式对齐 <see cref="TrainingDataExporter"/>：JSONL，每行一条样本，camelCase。
///   3. 默认仅导出 <see cref="UtilityLedgerEntry.ModelScore"/> 非 null 的条目（校准必须有模型预测）；
///      可通过 <see cref="CalibrationDataExportRequest.RequireModelScore"/>=false 关闭（仅诊断用途）。
///   4. 生成 sidecar manifest（含 SHA-256 + 正负样本统计 + model artifact 追溯），供下游校验。
///   5. 导出过程幂等：重复执行覆盖输出文件，不修改 ledger 状态。
///   6. 生产路径注入 Postgres-backed IUtilityLedgerStore；开发 / 测试路径注入 InMemory 实现 — 无需修改导出器代码。
/// </remarks>
public sealed class CalibrationDataExporter : ICalibrationDataExporter
{
    /// <summary>导出 schema 版本（写入 manifest 供下游消费者识别格式）。</summary>
    public const string ExportSchemaVersion = "calibration-data-export/v1";

    /// <summary>校准数据 JSONL 文件名。</summary>
    public const string DataFileName = "calibration-data.jsonl";

    /// <summary>清单文件名。</summary>
    public const string ManifestFileName = "calibration-data.manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IUtilityLedgerStore _ledgerStore;

    public CalibrationDataExporter(IUtilityLedgerStore ledgerStore)
    {
        ArgumentNullException.ThrowIfNull(ledgerStore);
        _ledgerStore = ledgerStore;
    }

    /// <inheritdoc />
    public async Task<CalibrationDataExportResult> ExportAsync(
        CalibrationDataExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        var resolvedDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(resolvedDirectory);

        var dataFilePath = Path.Combine(resolvedDirectory, DataFileName);
        var manifestFilePath = Path.Combine(resolvedDirectory, ManifestFileName);

        // 构建 ledger 查询：Take=0 表示不限制（导出场景通常需要全量数据）
        var query = new UtilityLedgerQuery
        {
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            Since = request.Since,
            Until = request.Until,
            DecisionId = request.DecisionId,
            // 校准数据集通常包含正负样本（IsSelected 不限制）
            IsSelected = null,
            Take = request.Take
        };

        var entries = await _ledgerStore.QueryAsync(query, cancellationToken).ConfigureAwait(false);

        // 转换为校准数据记录；按 RequireModelScore 过滤
        var records = new List<CalibrationDataRecord>(entries.Count);
        foreach (var entry in entries)
        {
            if (request.RequireModelScore && entry.ModelScore is null)
            {
                // 校准必须有模型预测分数；跳过无模型分数的条目
                continue;
            }

            records.Add(MapToCalibrationDataRecord(entry));
        }

        // 写入 JSONL（全量加载到内存后一次性写入；ledger 规模通常可控）
        var lines = records.Select(r => JsonSerializer.Serialize(r, JsonOptions));
        var content = string.Join(Environment.NewLine, lines);
        await File.WriteAllTextAsync(dataFilePath, content, cancellationToken).ConfigureAwait(false);

        // 计算 SHA-256 哈希（用于校验文件完整性）
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        var sha256Hash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        // 统计正负样本
        var positiveCount = records.Count(r => r.IsSelected);
        var negativeCount = records.Count - positiveCount;
        var positiveRatio = records.Count > 0
            ? (double)positiveCount / records.Count
            : 0.0;

        var exportedAt = DateTimeOffset.UtcNow;

        // 写入 sidecar manifest（含 SHA-256 + model artifact 追溯 + 正负样本统计）
        var manifest = new CalibrationDataExportManifest
        {
            ExportedAt = exportedAt,
            SchemaVersion = ExportSchemaVersion,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            Since = request.Since,
            Until = request.Until,
            ModelArtifactId = request.ModelArtifactId,
            ModelName = request.ModelName,
            EntryCount = records.Count,
            PositiveCount = positiveCount,
            NegativeCount = negativeCount,
            PositiveRatio = positiveRatio,
            Sha256Hash = sha256Hash,
            DataFileName = DataFileName
        };

        var manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
        await File.WriteAllTextAsync(manifestFilePath, manifestJson, cancellationToken).ConfigureAwait(false);

        return new CalibrationDataExportResult
        {
            ExportedAt = exportedAt,
            OutputDirectory = resolvedDirectory,
            DataFilePath = dataFilePath,
            ManifestFilePath = manifestFilePath,
            EntryCount = records.Count,
            PositiveCount = positiveCount,
            NegativeCount = negativeCount,
            PositiveRatio = positiveRatio,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            ModelArtifactId = request.ModelArtifactId,
            ModelName = request.ModelName,
            Sha256Hash = sha256Hash,
            SchemaVersion = ExportSchemaVersion
        };
    }

    /// <summary>
    /// 将 UtilityLedgerEntry 转换为校准数据记录（predicted / observed / weight / metadata 四段式）。
    /// </summary>
    private static CalibrationDataRecord MapToCalibrationDataRecord(UtilityLedgerEntry entry)
    {
        return new CalibrationDataRecord
        {
            // predicted
            ModelScore = entry.ModelScore,
            DeterministicScore = entry.DeterministicScore,
            FinalScore = entry.FinalScore,

            // observed
            IsSelected = entry.IsSelected,
            DropReasonCode = entry.DropReasonCode,

            // weight（使用 Expert 贡献比例作为样本权重）
            Weight = entry.UtilityContribution > 0 ? entry.UtilityContribution : 1.0,

            // metadata
            DecisionId = entry.DecisionId,
            CandidateItemId = entry.CandidateItemId,
            WorkspaceId = entry.WorkspaceId,
            CollectionId = entry.CollectionId,
            Expert = entry.Expert.ToString(),
            MaterializedAt = entry.MaterializedAt,
            PolicyVersion = entry.PolicyVersion
        };
    }
}
