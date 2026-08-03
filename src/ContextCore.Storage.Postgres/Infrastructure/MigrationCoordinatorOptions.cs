namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// HA 迁移协调器配置（绑定自配置节 "MigrationCoordinator"）。
/// </summary>
public sealed class MigrationCoordinatorOptions
{
    /// <summary>
    /// 启动时是否主动执行 schema 迁移协调（默认 true）。
    /// 关闭后仅在存储首次访问时惰性迁移（<c>EnsureMigratedAsync</c>），
    /// 失去「启动即单执行者协调 + 失败快速退出」的语义。
    /// </summary>
    public bool StartupRunEnabled { get; set; } = true;

    /// <summary>
    /// 启动协调的超时（秒，默认 300）。等待迁移锁/执行 DDL 超过该时长视为失败。
    /// </summary>
    public int StartupTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// 实例 ID（区分多实例部署中哪个实例执行了迁移）。默认每次构造生成新 GUID。
    /// </summary>
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");
}
