# 文档索引

本目录只保留现行文档、测试固定契约和可执行运行手册；已完成阶段、freeze、旧路线和过期专题不保留副本，需要时从 Git 历史查询。

- [LIVE_PATH.md](LIVE_PATH.md) — ingest / query / retrieve / package / Agent Run 实际调用链
- [ROADMAP.md](ROADMAP.md) — **唯一活动路线**：高召回、高准确率、反馈闭环、自学习与工程收敛
- [router-intent-boundaries.md](router-intent-boundaries.md) — Router 意图边界；测试读取此固定路径
- [private-configuration.md](private-configuration.md) — 本机私有配置位置
- [postgres-local-smoke-runbook.md](postgres-local-smoke-runbook.md) — Postgres 本机冒烟
- [vector-embedding-provider-local-runbook.md](vector-embedding-provider-local-runbook.md) — 本地 embedding
- [runbooks/postgres-backup-restore.md](runbooks/postgres-backup-restore.md) — Postgres 备份恢复

API 的机器可验证契约以 `service/openapi/` 为准。仓库根上的 `vector/`、`learning/`、`eval/`、`foundation/`、`storage/` 和 `service/` 报告属于评测或生成证据，不是现行架构文档。
