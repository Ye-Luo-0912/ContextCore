using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.MemoryEvolution;

/// <summary>
/// R29 WP-E-3：训练数据导出器默认实现。
/// </summary>
/// <remarks>
/// 设计原则（对齐澄清 #4 + R29 学习闭环）：
///   1. 导出器是只读边界：通过 <see cref="IUtilityLedgerStore.QueryAsync"/> 查询 ledger 条目，
///      转换为 <see cref="TrainingDataRecord"/>（feature / label / metadata）后写入 JSONL 文件。
///   2. 输出格式对齐 <c>LearningFeatureDatasetService</c>：JSONL，每行一条样本，
///      JsonSerializerDefaults.Web（camelCase）。
///   3. 生成 sidecar manifest（含 SHA-256 哈希 + model artifact 追溯），供下游校验。
///   4. 导出过程幂等：重复执行覆盖输出文件，不修改 ledger 状态。
///   5. 生产路径注入 Postgres-backed IUtilityLedgerStore；开发 / 测试路径注入 InMemory 实现 — 无需修改导出器代码。
/// </remarks>
public sealed class TrainingDataExporter : ITrainingDataExporter
{
    /// <summary>导出 schema 版本（写入 manifest 供下游消费者识别格式）。</summary>
    public const string ExportSchemaVersion = "training-data-export/v1";

    /// <summary>训练数据 JSONL 文件名。</summary>
    public const string DataFileName = "training-data.jsonl";

    /// <summary>清单文件名。</summary>
    public const string ManifestFileName = "training-data.manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IUtilityLedgerStore _ledgerStore;

    public TrainingDataExporter(IUtilityLedgerStore ledgerStore)
    {
        ArgumentNullException.ThrowIfNull(ledgerStore);
        _ledgerStore = ledgerStore;
    }

    /// <inheritdoc />
    public async Task<TrainingDataExportResult> ExportAsync(
        TrainingDataExportRequest request,
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
            IsSelected = request.IsSelected,
            Take = request.Take
        };

        var entries = await _ledgerStore.QueryAsync(query, cancellationToken).ConfigureAwait(false);

        // 转换为训练数据记录（feature / label / metadata 三段式）
        var records = entries.Select(MapToTrainingDataRecord).ToList();

        // 写入 JSONL（全量加载到内存后一次性写入；ledger 规模通常可控）
        var lines = records.Select(r => JsonSerializer.Serialize(r, JsonOptions));
        var content = string.Join(Environment.NewLine, lines);
        await File.WriteAllTextAsync(dataFilePath, content, cancellationToken).ConfigureAwait(false);

        // 计算 SHA-256 哈希（用于校验文件完整性）
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        var sha256Hash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        var exportedAt = DateTimeOffset.UtcNow;

        // 写入 sidecar manifest（含 SHA-256 + model artifact 追溯）
        var manifest = new TrainingDataExportManifest
        {
            ExportedAt = exportedAt,
            SchemaVersion = ExportSchemaVersion,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            Since = request.Since,
            Until = request.Until,
            ModelArtifactId = request.ModelArtifactId,
            EntryCount = records.Count,
            Sha256Hash = sha256Hash,
            DataFileName = DataFileName
        };

        var manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
        await File.WriteAllTextAsync(manifestFilePath, manifestJson, cancellationToken).ConfigureAwait(false);

        return new TrainingDataExportResult
        {
            ExportedAt = exportedAt,
            OutputDirectory = resolvedDirectory,
            DataFilePath = dataFilePath,
            ManifestFilePath = manifestFilePath,
            EntryCount = records.Count,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            ModelArtifactId = request.ModelArtifactId,
            Sha256Hash = sha256Hash,
            SchemaVersion = ExportSchemaVersion
        };
    }

    /// <summary>
    /// 将 UtilityLedgerEntry 转换为训练数据记录（feature / label / metadata 三段式）。
    /// </summary>
    private static TrainingDataRecord MapToTrainingDataRecord(UtilityLedgerEntry entry)
    {
        return new TrainingDataRecord
        {
            // feature
            DeterministicScore = entry.DeterministicScore,
            ModelScore = entry.ModelScore,
            UtilityContribution = entry.UtilityContribution,
            Expert = entry.Expert.ToString(),

            // label
            IsSelected = entry.IsSelected,
            DropReasonCode = entry.DropReasonCode,

            // metadata
            DecisionId = entry.DecisionId,
            CandidateItemId = entry.CandidateItemId,
            WorkspaceId = entry.WorkspaceId,
            CollectionId = entry.CollectionId,
            MaterializedAt = entry.MaterializedAt,
            PolicyVersion = entry.PolicyVersion
        };
    }
}
