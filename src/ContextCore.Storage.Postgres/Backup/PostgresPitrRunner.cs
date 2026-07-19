using System.Diagnostics;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Backup;

/// <summary>
/// R14-PG-10：PostgreSQL PITR（Point-In-Time Recovery）执行器。
/// 通过 <c>ALTER SYSTEM</c> 启用 WAL 归档，<c>pg_basebackup</c> 创建基础备份，
/// 再通过 <c>recovery.signal</c> + <c>restore_command</c> 在目标实例上重放 WAL 至指定时间点。
/// </summary>
/// <remarks>
/// 设计选择：
/// <list type="bullet">
/// <item>不在 ContextCore 内管理 PostgreSQL 数据目录生命周期——调用方提供目标实例路径与连接字符串。</item>
/// <item>不存储连接字符串；只读取 <see cref="PostgresOptions"/> 的非敏感字段写入结果。</item>
/// <item>WAL 归档目录由调用方提供；本类只生成 archive_command 模板与扫描归档文件。</item>
/// <item>需要超级用户权限执行 ALTER SYSTEM；非超级用户应在调用前验证。</item>
/// </list>
/// </remarks>
public sealed class PostgresPitrRunner : IAsyncDisposable
{
    private readonly PostgresConnectionFactory _factory;
    private readonly PostgresDumpOptions _dumpOptions;
    private readonly PostgresPitrOptions _pitrOptions;

    /// <summary>构造方法；接受 <see cref="PostgresOptions"/>、可选的 dump 选项与 PITR 选项。</summary>
    public PostgresPitrRunner(
        PostgresOptions options,
        PostgresDumpOptions? dumpOptions = null,
        PostgresPitrOptions? pitrOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _factory = new PostgresConnectionFactory(options);
        _dumpOptions = dumpOptions ?? new PostgresDumpOptions();
        _pitrOptions = pitrOptions ?? new PostgresPitrOptions();
    }

    /// <summary>
    /// 启用 WAL 归档：通过 ALTER SYSTEM 设置 wal_level=replica、archive_mode=on、archive_command。
    /// 调用方需在之后执行 SELECT pg_reload_conf() 或重启 PostgreSQL 生效。
    /// 需要超级用户权限。
    /// </summary>
    public async Task EnableWalArchivingAsync(CancellationToken cancellationToken = default)
    {
        var archiveDir = _pitrOptions.WalArchiveDirectory
            ?? throw new InvalidOperationException(
                "PostgresPitrOptions.WalArchiveDirectory 必须在启用 WAL 归档前设置。");
        var archiveCommand = _pitrOptions.ResolveArchiveCommand(archiveDir);

        await using var connection = await _factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var (sql, param) in new[]
        {
            ("ALTER SYSTEM SET wal_level = 'replica';", (string?)null),
            ("ALTER SYSTEM SET archive_mode = 'on';", (string?)null),
            ("ALTER SYSTEM SET archive_command = $1;", archiveCommand),
        })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (param is not null)
            {
                command.Parameters.Add(new NpgsqlParameter("p0", param));
            }
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 调用 <c>pg_basebackup -Ft -z -Z6 -D &lt;outputPath&gt;</c> 生成 tar.gz 基础备份。
    /// 输出目录将包含 base.tar.gz（必要时还有 pg_wal.tar.gz）。
    /// </summary>
    /// <param name="outputPath">目标目录（必须不存在或为空）；pg_basebackup 要求目录存在且为空。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task CreateBaseBackupAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        Directory.CreateDirectory(outputPath);

        var connStr = _factory.Options.ConnectionString;
        var builder = new NpgsqlConnectionStringBuilder(connStr);
        var host = builder.Host;
        var username = builder.Username;

        var args = new List<string>
        {
            "--format=tar",
            "--gzip",
            $"--compress={_pitrOptions.BaseBackupCompressionLevel}",
            $"--pgdata={outputPath}",
            "--no-password",
            $"--host={host}",
            $"--username={username}",
            "--wal",
        };

        await RunProcessAsync(ResolveBinary("pg_basebackup"), args, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 编排 PITR 恢复：
    /// <list type="number">
    ///   <item>解压/复制 base backup 到目标实例 data 目录（调用方负责）—— 本方法接收已就绪的 base.tar.gz 路径</item>
    ///   <item>在 data 目录中创建 <c>recovery.signal</c></item>
    ///   <item>向 <c>postgresql.auto.conf</c> 追加 <c>restore_command</c> 与 <c>recovery_target_time</c></item>
    ///   <item>由调用方启动 PostgreSQL；本方法等待 promotion 完成</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// 本方法不直接控制 PostgreSQL 服务生命周期（启动/停止需调用方执行）；
    /// 仅负责写入 recovery 配置文件并轮询 pg_is_in_recovery() 直到 promotion 完成。
    /// 这样可以避免在不同操作系统/部署形态下假设特定的 service manager。
    /// </remarks>
    /// <param name="baseBackupPath">base.tar.gz 路径（仅用于结果记录；解压由调用方完成）。</param>
    /// <param name="walArchiveDir">WAL 归档目录，用于生成 restore_command。</param>
    /// <param name="targetTime">恢复目标时间（UTC）；为空时恢复到最新可用 WAL。</param>
    /// <param name="targetConnectionString">目标实例（已启动）的连接字符串，用于轮询 promotion 状态。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<PitrRestoreResult> RestoreToPointInTimeAsync(
        string baseBackupPath,
        string walArchiveDir,
        DateTimeOffset? targetTime,
        string targetConnectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBackupPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(walArchiveDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetConnectionString);

        var startedAt = Stopwatch.GetTimestamp();

        // 1) 创建 recovery.signal（在 base backup 同目录，调用方应在 data 目录中触发恢复时存在）
        var dataDir = Path.GetDirectoryName(baseBackupPath)
            ?? throw new InvalidOperationException("无法从 baseBackupPath 推断 data 目录。");
        var recoverySignalPath = Path.Combine(dataDir, "recovery.signal");
        await File.WriteAllTextAsync(recoverySignalPath, string.Empty, cancellationToken).ConfigureAwait(false);

        // 2) 写入 postgresql.auto.conf 中的 restore_command + recovery_target_time
        var autoConfPath = Path.Combine(dataDir, "postgresql.auto.conf");
        var restoreCommand = $"cp {Path.Combine(walArchiveDir, "%f")} %p";
        var lines = new List<string>
        {
            "# ContextCore PITR recovery configuration",
            $"restore_command = '{restoreCommand}'",
            $"recovery_target_action = '{_pitrOptions.RecoveryTargetAction}'",
        };
        if (targetTime is not null)
        {
            var targetTimeStr = targetTime.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
            lines.Add($"recovery_target_time = '{targetTimeStr}'");
        }

        await File.AppendAllLinesAsync(autoConfPath, lines, cancellationToken).ConfigureAwait(false);

        // 3) 轮询目标实例直到 promotion 完成（pg_is_in_recovery 返回 false）
        var targetOptions = new PostgresOptions
        {
            ConnectionString = targetConnectionString,
            AutoMigrate = false,
        };
        await using var targetFactory = new PostgresConnectionFactory(targetOptions);

        var walApplied = 0;
        var maxWait = TimeSpan.FromMinutes(30);
        var pollInterval = TimeSpan.FromSeconds(2);
        var deadline = DateTimeOffset.UtcNow + maxWait;
        bool stillInRecovery;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var connection = await targetFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT pg_is_in_recovery();";
                var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                stillInRecovery = result is true;
                if (!stillInRecovery)
                {
                    // 统计已应用的 WAL 文件数（best-effort，失败时不影响返回）
                    try
                    {
                        await using var statCmd = connection.CreateCommand();
                        statCmd.CommandText = "SELECT count(*) FROM pg_stat_wal_receiver;";
                        walApplied = Convert.ToInt32(await statCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                    }
                    catch
                    {
                        // ignore — best-effort
                    }
                    break;
                }
            }
            catch (NpgsqlException)
            {
                // 目标实例尚未就绪——继续等待
                stillInRecovery = true;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"PITR 恢复在 {maxWait.TotalMinutes:F0} 分钟内未完成 promotion。");
            }
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
        while (stillInRecovery);

        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        return new PitrRestoreResult
        {
            BaseBackupPath = baseBackupPath,
            WalArchiveDir = walArchiveDir,
            TargetTime = targetTime,
            RestoredToTimestamp = DateTimeOffset.UtcNow,
            WALFilesApplied = walApplied,
            Elapsed = elapsed
        };
    }

    /// <summary>
    /// 列出 WAL 归档目录中的所有文件（含大小与最后修改时间）。
    /// </summary>
    public Task<IReadOnlyList<WalArchiveFile>> ListWalArchiveFilesAsync(
        string walArchiveDir,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(walArchiveDir);
        if (!Directory.Exists(walArchiveDir))
        {
            return Task.FromResult<IReadOnlyList<WalArchiveFile>>(Array.Empty<WalArchiveFile>());
        }

        var result = new List<WalArchiveFile>();
        foreach (var file in Directory.EnumerateFiles(walArchiveDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if (!info.Exists) continue;
            result.Add(new WalArchiveFile
            {
                Name = info.Name,
                SizeBytes = info.Length,
                ModifiedUtc = info.LastWriteTimeUtc
            });
        }
        return Task.FromResult<IReadOnlyList<WalArchiveFile>>(result);
    }

    /// <summary>
    /// 验证 PITR 环境：检查 pg_basebackup 二进制可用 + 当前实例 wal_level 设置。
    /// </summary>
    public async Task<(bool Success, string? Error)> ValidatePitrEnvironmentAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var binary = ResolveBinary("pg_basebackup");
            if (!File.Exists(binary))
            {
                return (false, $"未找到 pg_basebackup（已解析路径：{binary}）");
            }
        }
        catch (FileNotFoundException ex)
        {
            return (false, ex.Message);
        }

        try
        {
            await using var connection = await _factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SHOW wal_level;";
            var level = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var levelStr = level?.ToString() ?? string.Empty;
            if (!string.Equals(levelStr, "replica", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(levelStr, "logical", StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"wal_level 当前为 '{levelStr}'，PITR 需要 'replica' 或 'logical'。请先调用 EnableWalArchivingAsync 并重启 PostgreSQL。");
            }
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"校验 wal_level 失败：{ex.GetType().Name}: {ex.Message}");
        }
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
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var candidate = Path.Combine(dir, executable);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private async Task RunProcessAsync(
        string executable,
        IReadOnlyList<string> args,
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

/// <summary>Postgres PITR 选项。</summary>
public sealed class PostgresPitrOptions
{
    /// <summary>
    /// archive_command 模板，必须含 <c>%p</c>（目标路径）与 <c>%f</c>（WAL 文件名）。
    /// 若 <see cref="WalArchiveDirectory"/> 已设置，将自动替换模板中的 <c>{archive_dir}</c> 占位符。
    /// 默认 <c>cp %p {archive_dir}/%f</c>。
    /// </summary>
    public string ArchiveCommand { get; init; } = "cp %p {archive_dir}/%f";

    /// <summary>WAL 归档目录；启用 WAL 归档时必须设置。</summary>
    public string? WalArchiveDirectory { get; init; }

    /// <summary>
    /// recovery_target_action；默认 <c>promote</c>（恢复完成后提升为 primary）。
    /// 可选值：<c>promote</c> / <c>pause</c> / <c>shutdown</c> / <c>pause_at_wal_end</c>。
    /// </summary>
    public string RecoveryTargetAction { get; init; } = "promote";

    /// <summary>pg_basebackup 压缩级别（1-9）；默认 6。</summary>
    public int BaseBackupCompressionLevel { get; init; } = 6;

    /// <summary>
    /// 解析最终 archive_command：若 <see cref="ArchiveCommand"/> 含 <c>{archive_dir}</c> 占位符，
    /// 用 <paramref name="walArchiveDir"/> 替换；否则原样返回。
    /// </summary>
    public string ResolveArchiveCommand(string walArchiveDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(walArchiveDir);
        return ArchiveCommand.Replace("{archive_dir}", walArchiveDir, StringComparison.Ordinal);
    }
}

/// <summary>PITR 恢复结果。</summary>
public sealed record PitrRestoreResult
{
    /// <summary>使用的基础备份路径。</summary>
    public string BaseBackupPath { get; init; } = string.Empty;

    /// <summary>WAL 归档目录。</summary>
    public string WalArchiveDir { get; init; } = string.Empty;

    /// <summary>恢复目标时间（UTC）；为 null 表示恢复到最新可用 WAL。</summary>
    public DateTimeOffset? TargetTime { get; init; }

    /// <summary>恢复完成时间（UTC）。</summary>
    public DateTimeOffset RestoredToTimestamp { get; init; }

    /// <summary>已应用的 WAL 文件数（best-effort，可能为 0）。</summary>
    public int WALFilesApplied { get; init; }

    /// <summary>恢复耗时。</summary>
    public TimeSpan Elapsed { get; init; }
}

/// <summary>WAL 归档目录中的一个文件元数据。</summary>
public sealed record WalArchiveFile
{
    /// <summary>文件名（不含目录）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>字节数。</summary>
    public long SizeBytes { get; init; }

    /// <summary>最后修改时间（UTC）。</summary>
    public DateTimeOffset ModifiedUtc { get; init; }
}
