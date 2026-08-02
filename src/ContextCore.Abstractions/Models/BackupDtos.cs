namespace ContextCore.Abstractions.Models;

/// <summary>
/// 备份存储类型。当前仅 FileSystem + Postgres 两种；扩展时新增枚举值。
/// </summary>
public enum BackupStorageKind
{
    FileSystem,
    Postgres
}

/// <summary>
/// 备份清单中的一个条目。每个条目对应备份归档内的一个文件或数据库转储段。
/// </summary>
public sealed record BackupManifestEntry
{
    /// <summary>相对归档根的路径（用 '/' 分隔）。Postgres 转储使用 <c>postgres://schema.table</c> 形式。</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>文件大小（字节）。Postgres 转储为转储流大小。</summary>
    public long SizeBytes { get; init; }

    /// <summary>SHA-256 hex 小写；文件不存在或为目录时为空字符串。</summary>
    public string ContentHash { get; init; } = string.Empty;

    /// <summary>条目来源存储类型。</summary>
    public BackupStorageKind StorageKind { get; init; } = BackupStorageKind.FileSystem;

    /// <summary>最后修改时间（UTC）；Postgres 转储为转储时间。</summary>
    public DateTimeOffset LastModifiedUtc { get; init; }

    /// <summary>可选的条目分类（如 <c>memory.short</c> / <c>relation</c> / <c>postgres.relations</c>）。</summary>
    public string Category { get; init; } = string.Empty;
}

/// <summary>
/// 备份归档清单。归档（ZIP / pg_dump）旁以 JSON 形式保存，
/// 记录每个条目的 SHA-256 与元数据，供 <c>backup verify</c> / <c>backup drill</c> 校验完整性。
/// </summary>
public sealed record BackupManifest
{
    /// <summary>清单 schema 版本；当前固定为 <c>v1</c>。</summary>
    public string SchemaVersion { get; init; } = "v1";

    /// <summary>备份归档的文件名（不含目录），便于归档与清单一起迁移时保持关联。</summary>
    public string ArchiveName { get; init; } = string.Empty;

    /// <summary>归档总字节大小（ZIP 文件大小或 pg_dump 文件大小）。</summary>
    public long ArchiveSizeBytes { get; init; }

    /// <summary>归档本身的 SHA-256（hex 小写）。</summary>
    public string ArchiveHash { get; init; } = string.Empty;

    /// <summary>备份创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>创建备份时的数据根目录或 Postgres 连接目标（不含凭据）。</summary>
    public string SourceDescription { get; init; } = string.Empty;

    /// <summary>备份来源存储类型。决定恢复路径（ZIP 解压 vs pg_restore）。</summary>
    public BackupStorageKind SourceKind { get; init; } = BackupStorageKind.FileSystem;

    /// <summary>清单条目列表。</summary>
    public IReadOnlyList<BackupManifestEntry> Entries { get; init; } = Array.Empty<BackupManifestEntry>();

    /// <summary>备份归档中观察到的全部条目数。</summary>
    public int EntryCount => Entries.Count;

    /// <summary>所有条目的总字节大小（可能与 ArchiveSizeBytes 不同——后者包含 ZIP 元数据）。</summary>
    public long TotalEntryBytes => Entries.Sum(e => e.SizeBytes);
}

/// <summary>
/// 备份验证结果。供 <c>backup verify</c> 子命令返回，便于程序化消费。
/// </summary>
public sealed record BackupVerifyResult
{
    /// <summary>校验的清单文件路径。</summary>
    public string ManifestPath { get; init; } = string.Empty;

    /// <summary>校验的归档文件路径。</summary>
    public string ArchivePath { get; init; } = string.Empty;

    /// <summary>清单中声明的条目数。</summary>
    public int ExpectedEntryCount { get; init; }

    /// <summary>校验通过的条目数（哈希匹配）。</summary>
    public int VerifiedEntryCount { get; init; }

    /// <summary>哈希不匹配的条目列表。</summary>
    public IReadOnlyList<string> HashMismatchedPaths { get; init; } = Array.Empty<string>();

    /// <summary>归档中存在但清单未声明的条目路径。</summary>
    public IReadOnlyList<string> OrphanPaths { get; init; } = Array.Empty<string>();

    /// <summary>清单中声明但归档中缺失的条目路径。</summary>
    public IReadOnlyList<string> MissingPaths { get; init; } = Array.Empty<string>();

    /// <summary>归档本身哈希是否匹配。</summary>
    public bool ArchiveHashMatched { get; init; }

    /// <summary>整体是否通过（无哈希不匹配、无缺失、无孤儿、归档哈希匹配）。</summary>
    public bool IsHealthy => !HashMismatchedPaths.Any()
        && !OrphanPaths.Any()
        && !MissingPaths.Any()
        && ArchiveHashMatched;

    /// <summary>验证耗时。</summary>
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// 恢复演练结果。drill 不实际覆盖现有数据，仅验证备份可解压/可恢复到隔离位置。
/// </summary>
public sealed record BackupDrillResult
{
    /// <summary>演练的备份归档路径。</summary>
    public string ArchivePath { get; init; } = string.Empty;

    /// <summary>隔离恢复目录（演练后自动清理）。</summary>
    public string StagingPath { get; init; } = string.Empty;

    /// <summary>恢复到 staging 的条目数。</summary>
    public int RestoredEntryCount { get; init; }

    /// <summary>恢复后重新计算的哈希匹配条目数。</summary>
    public int HashMatchedEntryCount { get; init; }

    /// <summary>是否通过——恢复成功且所有哈希匹配。</summary>
    public bool IsHealthy => RestoredEntryCount > 0 && RestoredEntryCount == HashMatchedEntryCount;

    /// <summary>演练耗时。</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>Postgres 演练是否跳过（pg_restore 需要独立数据库，默认不在 drill 中执行）。</summary>
    public bool PostgresDrillSkipped { get; init; }
}
