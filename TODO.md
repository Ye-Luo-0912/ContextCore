# ContextCore 当前路线

> 更新：2026-08-14。这里只保留当前入口和近期顺序；长期安排见路线文档，已完成细节从 Git 查询。

## 当前目标

长期北极星是：在 token、时延和成本预算内建立高召回、高准确率的上下文系统，最终提高 Agent/LLM 任务成功率。自学习、重排、图/向量检索、性能和代码精简都只为这个目标服务。

近期先把当前 HEAD、生成物策略、SDK、性能测量和质量指标收敛为可复现基线，再做召回批处理、排序与反馈闭环；没有质量归因前不打开 Active Learning，也不大规模删除实验能力。

- 长期路线：[`docs/ROADMAP.md`](docs/ROADMAP.md)
- 现行调用链：[`docs/LIVE_PATH.md`](docs/LIVE_PATH.md)
- 当前性能基线：[`benchmarks/results/MULTIQUERY_RECALL_BASELINE.md`](benchmarks/results/MULTIQUERY_RECALL_BASELINE.md)

## 近期执行顺序

1. LR-0A：收口当前工作树、临时文件、全量测试和跳过项。
2. LR-0B：让评测/脚本不再把历史 Markdown 写回受版本控制目录。
3. LR-0C：用 `global.json` 固定受支持的 .NET 10 SDK。
4. LR-0D：修正 async 分配测量，补 Postgres 多问句基线。
5. LR-0E：固定 Recall@K、Recall@TokenBudget、Precision@K、MRR/nDCG 与任务结果指标。
6. LR-1A：建立分层、隔离、版本化的检索质量评测集。
7. LR-1B：建立候选流诊断，能定位证据在哪一阶段丢失。
8. LR-2A：使用现有批量 embedding 契约，把 q 次调用降为 1 次且质量不回退。
9. LR-2B：FileSystem/InMemory/Postgres 多问句 lexical 单次读取。
10. LR-2C：FileSystem/InMemory/Postgres 批量 vector search（单快照 / 单次枚举 / 单 roundtrip）。
11. LR-2D：按 LR-1 漏失分类处理实体、别名、短语、观察信息、时间范围与多跳关系。
12. LR-2E：按唯一有效命中率与成本统计各通道贡献，无贡献则关停。
13. LR-3A：候选 provenance 与分桶分数校准（公共可比刻度，原始分可审计）。
14. LR-3B：两阶段排序（低成本保召回 + 有限候选重排，先确定性 reranker）。
15. LR-3C：预算、去重与多样性（证据、来源、冲突、token 共同分配）——分配裁掉的 dropped envelope 带原因码与详情、找回问句只恢复预算裁掉条目（已完成）。
16. LR-3D：Runtime 收敛（统一入口，DecisionEngine 区域生产 LOC 净减 10%）。

一个 Agent 一次只执行一个工作包；长期阶段不能越过前置数据门槛提前开工。零引用契约和宿主依赖可在质量基线后精简，自学习必须经过离线、shadow、canary、active 四道门。
