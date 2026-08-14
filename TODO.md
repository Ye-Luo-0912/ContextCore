# ContextCore 当前路线

> 更新：2026-08-14。本文只保留当前入口；已完成阶段文档已删除，历史可从 Git 查询。

## 当前目标

功能、公开契约与存储语义保持不变。上一份执行清单（RF-1…RF-7：向量排除下推、租约层收敛、旧工厂删除、HTTP 错误映射、多问句召回基线）已完成，下一份执行清单尚未定义。

- 现行调用链：[`docs/LIVE_PATH.md`](docs/LIVE_PATH.md)
- 上一份执行清单（已完成）：[`docs/NEXT_PHASE_REFACTOR.md`](docs/NEXT_PHASE_REFACTOR.md)
- 多问句召回基线报告：[`benchmarks/results/MULTIQUERY_RECALL_BASELINE.md`](benchmarks/results/MULTIQUERY_RECALL_BASELINE.md)

## 执行顺序

上一阶段执行顺序（均已完成）：

1. Gate 0：锁定多轮召回、公开 API 与存储行为基线。
2. RF-1：把已持有 ID 下推到向量检索，避免 TopK 后过滤造成欠召回。
3. RF-2：让 Canary 只依赖专用租约接口，删除未形成复用价值的通用租约层。
4. RF-3：删除无引用的旧执行产物工厂。
5. RF-4：取消（两个 outbox 的重复是事务边界样板，提取不净减代码）。
6. RF-5：重跑多问句召回性能矩阵，形成 162 组合基线报告。
7. RF-6：取消（Actor 终态路径差异大，抽取即「参数众多的万能方法」）。
8. RF-7：HTTP 冲突错误统一走 `ContextCoreHttpResultMapper.Conflict`。

每个工作包单独交付。不要并行修改 `CandidateProviders`、`AgentRunActor` 和公共 Contracts；不要以“拆文件”冒充精简。
