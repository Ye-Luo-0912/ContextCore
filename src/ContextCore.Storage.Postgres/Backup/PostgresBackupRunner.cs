using System.Diagnostics;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Shared;
using Npgsql;
using NpgsqlTypes;

namespace ContextCore.Storage.Postgres.Backup;

/// <summary>
/// PostgreSQL 备份执行器。包装 <c>pg_dump</c> / <c>pg_restore</c> CLI，
/// 并通过 Npgsql 查询元数据以生成备份清单条目。
/// </summary>
/// <remarks>
/// 设计选择：
/// <list type="bullet">
/// <item>不内联 pg_dump 逻辑——直接复用 Postgres 官方工具，避免重复实现格式兼容性。</item>
/// <item>pg_dump / pg_restore 必须在 PATH 中可用，或通过 <see cref="PostgresDumpOptions.BinaryDirectory"/> 指定。</item>
/// <item>custom 格式（-Fc）便于选择性恢复与并行恢复；默认使用之。</item>
/// <item>不存储连接字符串；只读取 <see cref="PostgresOptions"/> 的非敏感字段（如 schema name、provider id）写入清单。</item>
/// <item>Npgsql 用于查询表清单与大小；不执行 dump 内容生成。</item>
/// </list>
/// </remarks>
public sealed class PostgresBackupRunner : IAsyncDisposable
{
    private readonly PostgresConnectionFactory _factory;
    private readonly PostgresDumpOptions _dumpOptions;

    /// <summary>构造方法；接受 <see cref="PostgresOptions"/> 与可选的 dump 选项。</summary>
    public PostgresBackupRunner(PostgresOptions options, PostgresDumpOptions? dumpOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _factory = new PostgresConnectionFactory(options);
        _dumpOptions = dumpOptions ?? new PostgresDumpOptions();
    }

    /// <summary>
    /// 执行 <c>pg_dump</c> 生成 custom 格式转储文件，并返回对应的清单条目。
    /// </summary>
    /// <param name="outputPath">目标 .dump 文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>包含 dump 文件元数据与表清单的备份结果。</returns>
    public async Task<PostgresDumpResult> DumpAsync(
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 1) 先查询表清单与大小，便于写入清单
        var tables = await ListTablesAsync(cancellationToken).ConfigureAwait(false);

        // 2) 调用 pg_dump -Fc -f <outputPath>
        var (dumpArgs, dumpEnv) = BuildDumpArguments(outputPath);
        await RunProcessAsync(
            ResolveBinary("pg_dump"),
            dumpArgs,
            dumpEnv,
            cancellationToken).ConfigureAwait(false);

        // 3) 计算转储文件哈希与大小
        var sizeBytes = new FileInfo(outputPath).Length;
        var hash = Sha256Utility.HashFile(outputPath);

        // 4) 构造清单条目：转储文件本身 + 每张表作为独立条目（便于 drill 时定位）
        var entries = new List<BackupManifestEntry>
        {
            new()
            {
                RelativePath = $"postgres://dump/{Path.GetFileName(outputPath)}",
                SizeBytes = sizeBytes,
                ContentHash = hash,
                StorageKind = BackupStorageKind.Postgres,
                LastModifiedUtc = DateTimeOffset.UtcNow,
                Category = "postgres.dump"
            }
        };
        foreach (var table in tables)
        {
            entries.Add(new BackupManifestEntry
            {
                RelativePath = $"postgres://{table.Schema}.{table.Name}",
                SizeBytes = table.ApproximateBytes,
                ContentHash = string.Empty, // 表级哈希需独立查询，留空表示由 dump 文件统一覆盖
                StorageKind = BackupStorageKind.Postgres,
                LastModifiedUtc = DateTimeOffset.UtcNow,
                Category = "postgres.table"
            });
        }

        return new PostgresDumpResult
        {
            DumpPath = outputPath,
            DumpSizeBytes = sizeBytes,
            DumpHash = hash,
            Tables = tables,
            Entries = entries
        };
    }

    /// <summary>
    /// 执行 <c>pg_restore</c> 从 custom 格式转储文件恢复到目标数据库。
    /// </summary>
    /// <param name="dumpPath">.dump 文件路径。</param>
    /// <param name="cleanBeforeRestore">是否在恢复前清理现有对象（--clean --if-exists）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task RestoreAsync(
        string dumpPath,
        bool cleanBeforeRestore = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpPath);
        if (!File.Exists(dumpPath))
        {
            throw new FileNotFoundException("Postgres 转储文件不存在。", dumpPath);
        }

        var (args, env) = BuildRestoreArguments(dumpPath, cleanBeforeRestore);
        await RunProcessAsync(ResolveBinary("pg_restore"), args, env, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 列出当前 schema 下的所有 ContextCore 表与其大致字节数。
    /// 用于在 dump 之前记录将要备份的内容。
    /// </summary>
    public async Task<IReadOnlyList<PostgresTableInfo>> ListTablesAsync(
        CancellationToken cancellationToken = default)
    {
        var schema = _factory.Options.SchemaName;
        var tablePrefix = _factory.Options.TablePrefix;
        var result = new List<PostgresTableInfo>();

        await using var connection = await _factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                c.relname AS table_name,
                COALESCE(pg_total_relation_size(c.oid), 0) AS total_bytes,
                n.nspname AS schema_name
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = 'r'
              AND (@schema_name IS NULL OR n.nspname = @schema_name)
              AND (@table_prefix IS NULL OR c.relname LIKE @table_prefix_pattern)
            ORDER BY n.nspname, c.relname;
            """;
        command.Parameters.Add(new NpgsqlParameter("schema_name",
            string.IsNullOrEmpty(schema) ? DBNull.Value : schema)
        { NpgsqlDbType = NpgsqlDbType.Name });
        command.Parameters.Add(new NpgsqlParameter("table_prefix",
            string.IsNullOrEmpty(tablePrefix) ? DBNull.Value : tablePrefix)
        { NpgsqlDbType = NpgsqlDbType.Name });
        command.Parameters.Add(new NpgsqlParameter("table_prefix_pattern",
            string.IsNullOrEmpty(tablePrefix) ? DBNull.Value : tablePrefix + "%")
        { NpgsqlDbType = NpgsqlDbType.Name });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var tableName = reader.GetString(0);
            var bytes = reader.GetInt64(1);
            var schemaName = reader.GetString(2);
            result.Add(new PostgresTableInfo
            {
                Schema = schemaName,
                Name = tableName,
                ApproximateBytes = bytes
            });
        }

        return result;
    }

    /// <summary>验证连接是否可用；同时检查 pg_dump / pg_restore 二进制是否在 PATH 中。</summary>
    public async Task<(bool Success, string? Error)> ValidateEnvironmentAsync(
        CancellationToken cancellationToken = default)
    {
        var pingResult = await _factory.PingAsync(cancellationToken).ConfigureAwait(false);
        if (!pingResult.Success)
        {
            return (false, $"Postgres 连接失败：{pingResult.ErrorMessage}");
        }

        foreach (var binary in new[] { "pg_dump", "pg_restore" })
        {
            try
            {
                var path = ResolveBinary(binary);
                if (!File.Exists(path))
                {
                    return (false, $"未找到 {binary}（已解析路径：{path}）");
                }
            }
            catch (FileNotFoundException ex)
            {
                return (false, ex.Message);
            }
        }

        return (true, null);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync().ConfigureAwait(false);
    }

    private string ResolveBinary(string name)
    {
        var dir = _dumpOptions.BinaryDirectory;
        var executable = OperatingSystem.IsWindows() ? $"{name}.exe" : name;
        if (!string.IsNullOrEmpty(dir))
        {
            var path = Path.Combine(dir, executable);
            if (File.Exists(path))
            {
                return path;
            }
        }

        // 回退到 PATH 解析
        var fullPath = FindInPath(executable);
        if (fullPath is not null)
        {
            return fullPath;
        }

        throw new FileNotFoundException(
            $"未找到 {name} 可执行文件。请安装 postgresql-client 或在 PostgresDumpOptions.BinaryDirectory 中指定目录。",
            name);
    }

    private static string? FindInPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir))
            {
                continue;
            }
            var candidate = Path.Combine(dir, executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 把 Npgsql 连接字符串拆分为 libpq 可识别的连接参数与密码环境变量。
    /// libpq 不识别 Npgsql 风格的键（Host/Database/Username），须转换为
    /// host/dbname/user，密码通过 PGPASSWORD 传递以避免出现在命令行中。
    /// </summary>
    private (IReadOnlyList<string> Args, IReadOnlyDictionary<string, string>? Env) BuildConnectionArguments()
    {
        var builder = new NpgsqlConnectionStringBuilder(_factory.Options.ConnectionString);
        var args = new List<string>();
        if (!string.IsNullOrEmpty(builder.Host))
        {
            args.Add($"--host={builder.Host}");
        }
        if (builder.Port != 0)
        {
            args.Add($"--port={builder.Port}");
        }
        if (!string.IsNullOrEmpty(builder.Database))
        {
            args.Add($"--dbname={builder.Database}");
        }
        if (!string.IsNullOrEmpty(builder.Username))
        {
            args.Add($"--username={builder.Username}");
        }

        IReadOnlyDictionary<string, string>? env = null;
        if (!string.IsNullOrEmpty(builder.Password))
        {
            env = new Dictionary<string, string> { ["PGPASSWORD"] = builder.Password };
        }
        return (args, env);
    }

    private (IReadOnlyList<string> Args, IReadOnlyDictionary<string, string>? Env) BuildDumpArguments(string outputPath)
    {
        // 连接参数拆分传入；--no-password 防止交互式阻塞
        var (connectionArgs, env) = BuildConnectionArguments();
        var args = new List<string>
        {
            "--format=custom",
            "--no-password",
            $"--file={outputPath}"
        };
        args.AddRange(connectionArgs);
        // 默认 schema 为 public 时不传 --schema：pg_dump 在 schema 过滤模式下会排除扩展
        // （如 vector），导致转储在全新数据库中无法恢复（类型缺失）。
        // 自定义 schema 时仍按需过滤。
        if (!string.IsNullOrEmpty(_factory.Options.SchemaName) &&
            !string.Equals(_factory.Options.SchemaName, "public", StringComparison.OrdinalIgnoreCase))
        {
            args.Add($"--schema={_factory.Options.SchemaName}");
        }
        return (args, env);
    }

    private (IReadOnlyList<string> Args, IReadOnlyDictionary<string, string>? Env) BuildRestoreArguments(string dumpPath, bool cleanBeforeRestore)
    {
        var (connectionArgs, env) = BuildConnectionArguments();
        var args = new List<string>
        {
            "--no-password"
        };
        args.AddRange(connectionArgs);
        args.Add(dumpPath);
        if (cleanBeforeRestore)
        {
            args.Add("--clean");
            args.Add("--if-exists");
        }
        // 与 dump 一致：默认 public schema 不传 --schema，保证扩展随转储一并恢复
        if (!string.IsNullOrEmpty(_factory.Options.SchemaName) &&
            !string.Equals(_factory.Options.SchemaName, "public", StringComparison.OrdinalIgnoreCase))
        {
            args.Add($"--schema={_factory.Options.SchemaName}");
        }
        return (args, env);
    }

    private async Task RunProcessAsync(
        string executable,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
        {
            startInfo.ArgumentList.Add(a);
        }
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动 {executable}。");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await using var _ = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* ignore */ }
        });

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{executable} 退出码 {process.ExitCode}。stderr: {stderr}");
        }
    }
}

/// <summary>Postgres 转储元数据，包含 dump 文件信息与表清单。</summary>
public sealed record PostgresDumpResult
{
    /// <summary>转储文件路径。</summary>
    public string DumpPath { get; init; } = string.Empty;

    /// <summary>转储文件字节数。</summary>
    public long DumpSizeBytes { get; init; }

    /// <summary>转储文件 SHA-256（hex 小写）。</summary>
    public string DumpHash { get; init; } = string.Empty;

    /// <summary>转储包含的表清单。</summary>
    public IReadOnlyList<PostgresTableInfo> Tables { get; init; } = Array.Empty<PostgresTableInfo>();

    /// <summary>对应清单条目（dump 文件 + 每张表一条）。</summary>
    public IReadOnlyList<BackupManifestEntry> Entries { get; init; } = Array.Empty<BackupManifestEntry>();
}

/// <summary>Postgres 表的元数据。</summary>
public sealed record PostgresTableInfo
{
    /// <summary>schema 名称。</summary>
    public string Schema { get; init; } = string.Empty;

    /// <summary>表名（不含 schema 前缀）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>表的近似字节数（含索引）；由 pg_total_relation_size 提供。</summary>
    public long ApproximateBytes { get; init; }
}

/// <summary>Postgres 备份选项；可指定 pg_dump / pg_restore 二进制目录。</summary>
public sealed class PostgresDumpOptions
{
    /// <summary>pg_dump / pg_restore 二进制所在目录；为空时从 PATH 解析。</summary>
    public string? BinaryDirectory { get; init; }
}
