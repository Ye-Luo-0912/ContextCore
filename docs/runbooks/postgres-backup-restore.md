# PostgreSQL 备份/恢复 Runbook（R14-PG-10）

本 runbook 描述 ContextCore 在 PostgreSQL 后端下的备份/恢复策略、PITR 配置、演练流程与故障处理。
适用于 R14-PG（Postgres Runtime Parity & HA Gate）阶段二及之后的所有 Postgres 部署。

---

## 1. 目标

- **RPO（Recovery Point Objective）**：可接受的数据丢失上限。
  - Dump-only 备份：RPO = 备份间隔（推荐 **每日** 一次，最多丢失 24h 数据）。
  - PITR：RPO = WAL 归档间隔（默认 `archive_timeout = 5min`，最多丢失 5 分钟 WAL）。
- **RTO（Recovery Time Objective）**：可接受的恢复时间上限。
  - Dump 恢复：5–30 分钟（取决于 dump 大小与 IO 子系统）。
  - PITR 恢复：基础备份恢复（5–30 分钟）+ WAL 重放（变长，取决于目标时间距基础备份的时间差与 WAL 量）。
  - **RTO 测量方法**：执行 `backup pg-drill` 时记录 `Elapsed`；每月一次例行演练并归档结果。

---

## 2. 备份策略（三层）

### Level 1 — 每日逻辑备份（pg_dump）

- 命令：`backup pg-create --connection-string <cs> [--output <dir>]`
- 输出：`<backup-dir>/postgres_<timestamp>.dump` + `.manifest.json`
- 清单：含 dump 文件 SHA-256 + 每张表的元数据条目（`postgres://schema.table`）。
- 适用场景：日常恢复、跨版本迁移、单表/单 schema 选择性恢复。
- 保留策略：建议滚动保留 7 天（每日 1 份）+ 每周 1 份保留 4 周。

### Level 2 — 持续 WAL 归档（archive_mode=on）

- 命令：`backup pg-pitr-prepare --wal-archive-dir <dir> [--output <dir>]`
- 行为：
  1. `ALTER SYSTEM SET wal_level = 'replica';`
  2. `ALTER SYSTEM SET archive_mode = 'on';`
  3. `ALTER SYSTEM SET archive_command = 'cp %p <archive_dir>/%f';`
  4. `pg_basebackup -Ft -z -Z6 -D <output>` 生成 tar.gz 基础备份。
- 生效：需要重启 PostgreSQL 或执行 `SELECT pg_reload_conf();`（archive_mode 需要重启）。
- WAL 归档目录监控：必须监控磁盘空间；WAL 文件每 16MB 一个，繁忙数据库每分钟可产生多个。
- 保留策略：至少保留 2 个完整基础备份周期之间的所有 WAL。

### Level 3 — PITR（Point-In-Time Recovery）

- 命令：`backup pg-pitr-restore --base-backup <path> --wal-archive-dir <dir> --target-time <ISO8601> --target-connection-string <cs>`
- 行为：
  1. 调用方先停止目标 PostgreSQL 实例。
  2. 解压 `base.tar.gz` 到目标实例 data 目录。
  3. 本命令创建 `recovery.signal` 并向 `postgresql.auto.conf` 追加：
     - `restore_command = 'cp <archive_dir>/%f %p'`
     - `recovery_target_time = '<UTC ISO 8601>'`
     - `recovery_target_action = 'promote'`
  4. 调用方启动 PostgreSQL；本命令轮询 `pg_is_in_recovery()` 直到 promotion 完成。
- 适用场景：误操作回滚、数据丢失恢复、跨时间点对比。

---

## 3. 恢复流程

### 3.1 从 .dump 文件恢复（破坏性）

```bash
# ControlRoom CLI（推荐）
controlroom backup pg-restore /backups/postgres_20260720_120000.dump --connection-string <cs> --confirm

# 或直接使用 pg_restore
pg_restore --clean --if-exists -d <db> /backups/postgres_20260720_120000.dump
```

**警告**：`--clean --if-exists` 会先删除现有对象再恢复，生产环境务必先备份当前数据。

### 3.2 PITR 恢复流程

```bash
# 1. 停止目标 PostgreSQL 实例（systemctl stop postgresql 或 docker stop）
# 2. 清空 data 目录，解压基础备份
rm -rf /var/lib/postgresql/data/*
tar -xzf /backups/pitr/basebackup_20260720_120000/base.tar.gz -C /var/lib/postgresql/data/

# 3. 执行 PITR 恢复（自动创建 recovery.signal + 写入 postgresql.auto.conf）
controlroom backup pg-pitr-restore \
  --base-backup /backups/pitr/basebackup_20260720_120000/base.tar.gz \
  --wal-archive-dir /backups/pitr/wal \
  --target-time 2026-07-20T12:30:00Z \
  --target-connection-string "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=..." \
  --connection-string <source-cs-for-options>

# 4. 启动 PostgreSQL（systemctl start postgresql 或 docker start）
# 步骤 3 的命令会自动轮询直到 promotion 完成
```

---

## 4. 验证流程

### 4.1 清单验证（不连接数据库）

```bash
controlroom backup pg-verify /backups/postgres_20260720_120000.dump.manifest.json
```

输出：
- 归档哈希期望 vs 实际（SHA-256 比对）
- 表清单：期望表数 vs 实际表数（需连接数据库）
- 缺失表 / 孤儿表列表

### 4.2 演练（Drill，staging 数据库）

```bash
controlroom backup pg-drill /backups/postgres_20260720_120000.dump \
  --staging-connection-string "Host=localhost;Database=cc_staging;Username=test;Password=..." \
  --manifest /backups/postgres_20260720_120000.dump.manifest.json
```

**安全性**：
- 必须提供 `--staging-connection-string`，且与源数据库连接串不同（避免覆盖生产数据）。
- staging 数据库需预先创建且为空（或允许 `--clean --if-exists` 清理）。
- 完成后不自动删除 staging 数据库（调用方决定是否清理）。

### 4.3 完整 roundtrip 验证

参考 `tests/ContextCore.IntegrationTests/PostgresBackupIntegrationTests.cs` 中的
`PostgresBackup_DumpAndRestore_RoundtripsThroughStagingDb` 测试，覆盖：
1. 源数据库 schema 迁移 + 测试数据写入
2. `PostgresBackupRunner.DumpAsync` 生成 .dump
3. `BackupManifestGenerator.ForPostgresDumpAsync` 生成清单
4. 重新哈希 .dump 对比清单
5. staging 数据库恢复（`RestoreAsync(cleanBeforeRestore: true)`）
6. 校验 staging 数据库表清单与源一致

---

## 5. 演练规程（Drill Procedure）

### 5.1 频率

- **月度演练**：每月执行一次完整 drill，记录 RTO。
- **季度演练**：每季度执行一次 PITR drill（需 staging PostgreSQL 实例）。

### 5.2 步骤

1. 从最近一次 `pg-create` 备份中选取 .dump 文件。
2. 准备 staging 数据库（同版本 PostgreSQL，空数据库）。
3. 执行 `backup pg-drill`，记录 `Elapsed` 字段。
4. 校验 staging 数据库表数与源一致。
5. 清理 staging 数据库。
6. 归档演练结果（含 RTO、表数、dump 大小）。

### 5.3 验收标准

- 至少一次成功 drill 执行完成。
- drill 结果记录含 RTO 测量值。
- staging 表清单与源数据库完全一致（无缺失、无孤儿）。

---

## 6. 故障处理

### 6.1 pg_dump 二进制未找到

**症状**：`pg-create` 失败，错误：`未找到 pg_dump 可执行文件。请安装 postgresql-client 或在 PostgresDumpOptions.BinaryDirectory 中指定目录。`

**处理**：
- 安装 postgresql-client 包（与 PostgreSQL 服务端版本一致或更高）。
- 或在 `PostgresDumpOptions.BinaryDirectory` 中显式指定 pg_dump 所在目录。
- 验证：`pg_dump --version` 输出与 PostgreSQL 服务端主版本号一致。

### 6.2 WAL 归档磁盘满

**症状**：PostgreSQL 日志出现 `archive_command failed`；WAL 归档目录磁盘使用率 100%。

**处理**：
- 监控：在 WAL 归档目录所在磁盘设置 80% 告警。
- 紧急清理：保留最近 2 个基础备份周期之间的 WAL，删除更早的 WAL 文件。
- 长期：调整 `archive_timeout` 或扩容归档目录磁盘。
- 验证：执行 `SELECT * FROM pg_stat_archiver;` 检查 `failed_count`。

### 6.3 连接字符串含特殊字符

**症状**：`pg_dump` 或 `pg_restore` 失败，错误：连接被拒绝或密码错误。

**处理**：
- 使用 `NpgsqlConnectionStringBuilder` 构造连接字符串（自动转义特殊字符）。
- 密码含 `;` `=` 空格等字符时，必须用单引号包裹：`Password='my;pass'`。
- 避免在命令行直接传 `--connection-string`，改用环境变量或配置文件。

### 6.4 PITR 恢复超时

**症状**：`pg-pitr-restore` 抛出 `TimeoutException: PITR 恢复在 30 分钟内未完成 promotion`。

**处理**：
- 检查目标 PostgreSQL 实例是否已启动并接受连接。
- 检查 `recovery.signal` 文件是否存在于 data 目录。
- 检查 `postgresql.auto.conf` 中的 `restore_command` 路径是否正确。
- 查看 PostgreSQL 日志中的 WAL replay 进度（`<` 表示正在 replay）。
- 增大超时（修改 `PostgresPitrRunner` 中的 `maxWait` 常量）。

---

## 7. 自动化与监控建议

| 项目 | 工具 | 频率 |
|------|------|------|
| 每日 dump | cron + `backup pg-create` | 每日 02:00 |
| WAL 归档检查 | `SELECT * FROM pg_stat_archiver;` | 每 5 分钟 |
| WAL 归档目录磁盘 | Prometheus node_exporter | 每 1 分钟 |
| 月度 drill | CI 任务 + `backup pg-drill` | 每月 1 日 |
| 清单验证 | `backup pg-verify` | 每次 dump 后 |
| 备份目录磁盘 | Prometheus node_exporter | 每 1 分钟 |

---

## 8. 参考实现

| 组件 | 路径 | 说明 |
|------|------|------|
| `PostgresBackupRunner` | `src/ContextCore.Storage.Postgres/Backup/PostgresBackupRunner.cs` | pg_dump / pg_restore 包装 |
| `PostgresPitrRunner` | `src/ContextCore.Storage.Postgres/Backup/PostgresPitrRunner.cs` | WAL 归档 + pg_basebackup + PITR 编排 |
| `BackupManifestGenerator` | `src/ContextCore.ControlRoom/Backup/BackupManifestGenerator.cs` | 清单生成（ZIP + Postgres dump） |
| `BackupCommand` | `src/ContextCore.ControlRoom/Commands/BackupCommand.cs` | CLI pg-* 子命令 |
| `AdminEndpoints` | `src/ContextCore.Service/Endpoints/AdminEndpoints.cs` | POST /backup/pg-create, /backup/pg-restore |
| 单元测试 | `tests/ContextCore.Tests/PostgresPitrRunnerTests.cs` | PITR 选项、StripCredentials、清单 |
| 集成测试 | `tests/ContextCore.IntegrationTests/PostgresBackupIntegrationTests.cs` | Testcontainers dump→restore roundtrip |

---

## 9. 变更历史

| 日期 | 版本 | 变更 |
|------|------|------|
| 2026-07-20 | R14-PG-10 | 初始版本：PITR + CLI + AdminEndpoints + 演练规程 |
