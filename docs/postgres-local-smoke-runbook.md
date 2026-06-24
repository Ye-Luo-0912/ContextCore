# Postgres Local Smoke Runbook

Phase DB1.1 / DB1.2 只验证本地 Postgres schema baseline。它不迁移 ContextCore 数据，不切换 provider，也不改变 retrieval / planning / scoring / PackingPolicy / package output。

## Preflight

先确认本地 Postgres 已运行：

```powershell
Test-NetConnection -ComputerName localhost -Port 5432
Get-Service | Where-Object { $_.Name -match 'postgres|pgsql' -or $_.DisplayName -match 'PostgreSQL|Postgres' }
```

如果 `TcpTestSucceeded=False` 且没有运行中的 Postgres 服务，DB1.2 真实 smoke apply 不能执行。此时 `postgres-migration-smoke` 应保持 `NotConfigured` 或 `BlockedByConnection`，不能伪造 `ReadyForProviderDevelopment`。

## 配置

1. 在本地 Postgres 创建测试数据库和账号。
2. 使用独立 schema，例如 `contextcore_smoke`。
3. 参考 `appsettings.Postgres.sample.json`，但不要提交真实 connection string、密码或本地路径。
4. 推荐把真实连接串放在统一用户私有配置文件：

```text
%USERPROFILE%\.contextcore\secrets.json
```

格式：

```json
{
  "PostgresStore": {
    "Enabled": true,
    "ConnectionString": "Host=localhost;Port=55432;Database=contextcore;Username=contextcore;Password=<local-password>",
    "SchemaName": "contextcore_smoke",
    "AutoMigrate": false,
    "CommandTimeoutSeconds": 30,
    "ProviderId": "postgres-local-smoke"
  }
}
```

CLI 读取顺序：

1. `--connection-string`
2. `CONTEXTCORE_POSTGRES_CONNECTION_STRING`
3. `%USERPROFILE%\.contextcore\secrets.json`

也可以临时通过环境变量传入连接串：

```powershell
$env:CONTEXTCORE_POSTGRES_CONNECTION_STRING="Host=localhost;Port=5432;Database=contextcore;Username=contextcore;Password=<local-password>"
```

## Dry Run

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-migration-preview --schema contextcore_smoke
```

Dry-run 只列出 pending migration、required tables 和 missing tables，不写数据库。

## Smoke Apply

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-migration-smoke --schema contextcore_smoke --confirm
```

`--confirm` 是必须的显式写入开关。没有该参数时，smoke 命令只做连接测试和 dry-run，并输出 `ConfirmRequired`。

DB1.2 真实 smoke 成功条件：

- `ProviderEnabled=true`
- `ConnectionAvailable=true`
- `CurrentSchemaVersion=cc-schema-v3`
- `MissingRequiredTableCount=0`
- `MissingIndexCount=0`
- `Recommendation=ReadyForProviderDevelopment`

输出：

- `storage/postgres/postgres-schema-verification-report.json`
- `storage/postgres/postgres-schema-verification-report.md`

## Schema Verification

报告会检查：

- connection available
- current schema version
- required tables
- required indexes
- applied migration count

推荐结论：

- `NotConfigured`：未配置连接串或 provider disabled。
- `BlockedByConnection`：连接不可用。
- `SchemaIncomplete`：连接可用，但表、索引或 schema version 不完整。
- `MigrationFailed`：apply 执行失败。
- `ReadyForProviderDevelopment`：baseline schema 完整，可进入 provider CRUD 开发。

## Cleanup

只允许清理独立 smoke schema：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-migration-smoke --schema contextcore_smoke --confirm --drop-confirm
```

`--drop-confirm` 会执行 `DROP SCHEMA IF EXISTS contextcore_smoke CASCADE`。禁止在默认 schema 或共享 schema 上使用。

## 安全规则

- 不提交真实 connection string。
- 不提交真实密码。
- 不开启 `AutoMigrate` 作为默认行为。
- smoke schema 与生产 schema 必须隔离。
