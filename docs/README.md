# 文档索引

现行导航只有一份：[LIVE_PATH.md](LIVE_PATH.md)。
下一阶段要改召回/Agent 工作集时按 [RECALL_WIRING.md](RECALL_WIRING.md) 的工作包执行。

改代码、查「现在走哪条链」时读 LIVE_PATH。不要从本目录里其它文件的标题推断主路径。

## 现行

- [LIVE_PATH.md](LIVE_PATH.md) — 一次 ingest / query / retrieve / package / Agent Run 实际调用什么
- [RECALL_WIRING.md](RECALL_WIRING.md) — 召回接线阶段：给后续 agent 按包执行的清单、禁令与验收
- [private-configuration.md](private-configuration.md) — 本机私有配置位置
- [postgres-local-smoke-runbook.md](postgres-local-smoke-runbook.md) — Postgres 本机冒烟
- [postgres-operational-store.md](postgres-operational-store.md) — Postgres 操作面
- [vector-embedding-provider-local-runbook.md](vector-embedding-provider-local-runbook.md) — 本地 embedding
- [runbooks/postgres-backup-restore.md](runbooks/postgres-backup-restore.md) — 备份恢复

下面这些是专题说明，**未逐条对照当前 DI**。改行为前以 LIVE_PATH 和源码为准：

- 存储：`storage-boundary-current.md`、`storage-provider-capability-matrix.md`
- 记忆：`short-term-working-memory.md`、`working-memory-promotion-policy.md`、`mid-term-memory-governance.md`、`stable-memory-governance.md`
- 检索/打包：`graph-foundation.md`、`context-package-builder-main-flow.md`、`retrieval-orchestration-baseline-v1.md`、`context-attention-scoring.md`、`router-intent-boundaries.md`
- 其它：`context-learning-loop.md`、`strategy-scoring-design.md`、`constraints-gap-review.md`、`learning-feature-dataset.md`、`policy-feedback-dataset.md`

## 历史快照

[archive/](archive/) 里是 freeze、阶段报告、过期设计稿、旧路线图。只供考古，不当合同。

仓库根上还有证据目录（`vector/`、`learning/`、`eval/` 除 `contexts/`、`foundation/`、`storage/` 报告、`service/` 除 `openapi/`）。各目录 README 已标明。不要当现行文档。
