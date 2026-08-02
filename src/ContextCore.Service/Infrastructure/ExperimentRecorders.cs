using System.Text.Json;
using System.Text.Json.Serialization;
using ContextCore.Abstractions;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace ContextCore.Service.Infrastructure;

// ===========================================================================
// 阶段 E：IExperimentRecorder 持久化实现
//
// 两个实现并存：
//   1. FileSystemExperimentRecorder — JSON 文件存储（raw fixture）
//      适用：单实例部署、本地调试、FileSystem provider 环境
//   2. PostgresExperimentRecorder — PostgreSQL jsonb 存储（索引列 + 完整 fixture）
//      适用：HA 多实例部署、Postgres provider 环境
//
// 选择策略（StorageExtensions 注册时决定）：
//   - Storage:Provider=postgres 且 CC_EXPERIMENT_RECORDER_BACKEND≠filesystem → Postgres
//   - Storage:Provider=filesystem 且 CC_EXPERIMENT_RECORDER_BACKEND≠postgres → FileSystem
//   - 显式 CC_EXPERIMENT_RECORDER_BACKEND=memory → 始终用 InMemoryExperimentRecorder
//   - 未注入时 CoreExtensions 的 TryAddSingleton 回退到 InMemoryExperimentRecorder
//
// 数据一致性：
//   两端共享 ReplayFixtureJsonSerializer（ContextCore.Core），
//   CanonicalCandidateKey 转换为字符串形式，可互读。
// ===========================================================================

// ---------------------------------------------------------------------------
// §E.1 FileSystemExperimentRecorder — JSON 文件存储
// ---------------------------------------------------------------------------

/// <summary>
/// 阶段 E：FileSystem 持久化 ReplayFixture。
/// 每条 fixture 独立一个 JSON 文件，按月分片目录组织。
/// </summary>
/// <remarks>
/// 设计要点：
///   1. FileSystem 存 raw fixture（完整 JSON，含 WorkingSet + V2Result）。
///   2. 每条 fixture 一个文件：`{root}/experiment_fixtures/{YYYY-MM}/{timestamp}_{sanitizedId}.json`。
///      时间戳前缀让文件名天然按时间排序，便于 GetHistoryAsync 顺序读取。
///   3. RecordAsync 幂等：同 fixtureId 重复记录时覆盖写（保持最新快照）。
///   4. GetHistoryAsync 按文件名时间戳升序返回；解析失败的文件跳过。
///   5. ClearAsync 删除整个 experiment_fixtures 目录树。
///   6. 与 PostgresExperimentRecorder 共享 ReplayFixtureJsonSerializer，保证两端数据格式一致。
/// </remarks>
public sealed class FileSystemExperimentRecorder : IExperimentRecorder
{
    private readonly string _fixturesRoot;
    private readonly FileSystemWriter _writer;

    /// <summary>构造 FileSystem recorder。rootPath 为存储根目录；fixture 文件落在其下 experiment_fixtures/。</summary>
    public FileSystemExperimentRecorder(string rootPath)
        : this(rootPath, new FileSystemWriter())
    {
    }

    /// <summary>构造 FileSystem recorder，允许注入自定义 writer（测试用）。</summary>
    public FileSystemExperimentRecorder(string rootPath, FileSystemWriter writer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _fixturesRoot = Path.Combine(rootPath, "experiment_fixtures");
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    /// <inheritdoc />
    public async ValueTask RecordAsync(
        ReplayFixture fixture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var path = GetFixturePath(fixture);
        var json = ReplayFixtureJsonSerializer.Serialize(fixture);
        await _writer.WriteAllTextAtomicAsync(path, json, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ReplayFixture>> GetHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_fixturesRoot))
        {
            return new ValueTask<IReadOnlyList<ReplayFixture>>(Array.Empty<ReplayFixture>());
        }

        // 按文件名升序（时间戳前缀天然升序 = RecordedAt 升序）
        var files = Directory.EnumerateFiles(
                _fixturesRoot,
                "*.json",
                SearchOption.AllDirectories)
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
            .ToList();

        var fixtures = new List<ReplayFixture>(files.Count);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = File.ReadAllText(file);
                var fixture = ReplayFixtureJsonSerializer.DeserializeOrNull(json);
                if (fixture is not null)
                {
                    fixtures.Add(fixture);
                }
            }
            catch (IOException)
            {
                // 文件被并发删除/移动时跳过
            }
        }

        // 二次按 RecordedAt 排序，避免文件名时间戳与 RecordedAt 不一致
        fixtures.Sort((a, b) => a.RecordedAt.CompareTo(b.RecordedAt));
        return new ValueTask<IReadOnlyList<ReplayFixture>>(fixtures);
    }

    /// <inheritdoc />
    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(_fixturesRoot))
        {
            Directory.Delete(_fixturesRoot, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private string GetFixturePath(ReplayFixture fixture)
    {
        // 按月分片，避免单目录文件过多
        var month = fixture.RecordedAt.ToString("yyyy-MM");
        var sanitizedId = SanitizeFileName(fixture.FixtureId);
        // 时间戳前缀（毫秒精度）保证文件名按时间排序
        var timestamp = fixture.RecordedAt.ToString("yyyyMMddHHmmssfff");
        var fileName = $"{timestamp}_{sanitizedId}.json";
        return Path.Combine(_fixturesRoot, month, fileName);
    }

    private static string SanitizeFileName(string fixtureId)
    {
        if (string.IsNullOrEmpty(fixtureId))
        {
            return "fixture";
        }

        // 替换文件系统不安全字符；保留字母数字与连字符/下划线
        var chars = fixtureId.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
            {
                chars[i] = '_';
            }
        }

        var sanitized = new string(chars);
        // 限制文件名长度（Windows 文件名上限 255 字符，留余量给时间戳前缀）
        return sanitized.Length > 160 ? sanitized[..160] : sanitized;
    }
}

// ---------------------------------------------------------------------------
// §E.2 PostgresExperimentRecorder — PostgreSQL jsonb 存储
// ---------------------------------------------------------------------------

/// <summary>
/// 阶段 E：PostgreSQL 持久化 ReplayFixture。
/// 索引列（标量字段）+ jsonb 列（完整 fixture，含 WorkingSet + V2Result）。
/// </summary>
/// <remarks>
/// 设计要点：
///   1. PostgreSQL 存索引和状态（标量列 + jsonb），与 FileSystem 存 raw fixture 形成分工。
///   2. 索引列：fixture_id（PK）、recorded_at、purpose、jaccard_index、parity_level 等，
///      支持 WHERE 过滤与 ORDER BY 排序，无需解析 jsonb。
///   3. jsonb 列：完整 ReplayFixture（含 WorkingSet + V2Result），供离线 replay 使用。
///   4. RecordAsync 幂等：ON CONFLICT (fixture_id) DO NOTHING（同 fixtureId 不覆盖，保留首次记录）。
///   5. GetHistoryAsync：SELECT data ORDER BY recorded_at ASC（按时间升序）。
///   6. ClearAsync：DELETE FROM experiment_replay_fixtures（清空表，不 DROP）。
///   7. 共享 ReplayFixtureJsonSerializer，与 FileSystemExperimentRecorder 互读。
/// </remarks>
public sealed class PostgresExperimentRecorder : PostgresStoreBase, IExperimentRecorder
{
    private const string TableSuffix = "experiment_replay_fixtures";

    public PostgresExperimentRecorder(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
    public async ValueTask RecordAsync(
        ReplayFixture fixture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table(TableSuffix)} (
    fixture_id, recorded_at, purpose,
    legacy_selected_count, v2_selected_count, common_selected_count,
    only_in_legacy_count, only_in_v2_count, jaccard_index,
    legacy_token_total, v2_token_total, working_set_candidate_count,
    parity_level, notes, data)
VALUES (
    @fixture_id, @recorded_at, @purpose,
    @legacy_selected_count, @v2_selected_count, @common_selected_count,
    @only_in_legacy_count, @only_in_v2_count, @jaccard_index,
    @legacy_token_total, @v2_token_total, @working_set_candidate_count,
    @parity_level, @notes, @data)
ON CONFLICT (fixture_id) DO NOTHING;
""";

        command.Parameters.AddWithValue("fixture_id", fixture.FixtureId);
        command.Parameters.AddWithValue("recorded_at", fixture.RecordedAt);
        command.Parameters.AddWithValue("purpose", fixture.Purpose);
        command.Parameters.AddWithValue("legacy_selected_count", fixture.LegacySelectedCount);
        command.Parameters.AddWithValue("v2_selected_count", fixture.V2SelectedCount);
        command.Parameters.AddWithValue("common_selected_count", fixture.CommonSelectedCount);
        command.Parameters.AddWithValue("only_in_legacy_count", fixture.OnlyInLegacyCount);
        command.Parameters.AddWithValue("only_in_v2_count", fixture.OnlyInV2Count);
        command.Parameters.AddWithValue("jaccard_index", fixture.JaccardIndex);
        command.Parameters.AddWithValue("legacy_token_total", fixture.LegacyTokenTotal);
        command.Parameters.AddWithValue("v2_token_total", fixture.V2TokenTotal);
        command.Parameters.AddWithValue("working_set_candidate_count", fixture.WorkingSetCandidateCount);
        command.Parameters.AddWithValue("parity_level", fixture.ParityLevel.ToString());
        command.Parameters.AddWithValue("notes", fixture.Notes ?? string.Empty);
        AddReplayFixtureJson(command, "data", fixture);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ReplayFixture>> GetHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data FROM {Table(TableSuffix)} ORDER BY recorded_at ASC;
""";

        var results = new List<ReplayFixture>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var json = reader.GetString(0);
            var fixture = ReplayFixtureJsonSerializer.DeserializeOrNull(json);
            if (fixture is not null)
            {
                results.Add(fixture);
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"DELETE FROM {Table(TableSuffix)};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 使用 ReplayFixtureJsonSerializer 序列化 fixture 为 JSON，写入 jsonb 参数。
    /// 与 PostgresJsonSerializer 区别：使用 CanonicalCandidateKey + JsonStringEnumConverter，
    /// 保证 Dictionary&lt;CanonicalCandidateKey, CandidateMaterial&gt; 可序列化。
    /// </summary>
    private static NpgsqlParameter AddReplayFixtureJson(
        NpgsqlCommand command,
        string name,
        ReplayFixture fixture)
    {
        var parameter = command.Parameters.Add(name, NpgsqlDbType.Jsonb);
        parameter.Value = ReplayFixtureJsonSerializer.Serialize(fixture);
        return parameter;
    }
}
