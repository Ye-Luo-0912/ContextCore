# ContextCore

独立的上下文管理服务：摄入、检索、打包，以及可选的 Agent Run 循环。

**先读 [`docs/LIVE_PATH.md`](docs/LIVE_PATH.md)**。长期目标与阶段任务见 [`docs/ROADMAP.md`](docs/ROADMAP.md)：在预算内建立高召回、高准确率的上下文系统。不要从仓库根上的 `vector/`、`learning/`、freeze 报告推断现行行为。

## 打开仓库看哪

| 路径 | 角色 |
| --- | --- |
| [`src/`](src/) | 代码。宿主是 `ContextCore.Service` |
| [`tests/`](tests/) | 测试 |
| [`docs/LIVE_PATH.md`](docs/LIVE_PATH.md) | **现行调用链** |
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | **唯一活动路线**：质量、召回、排序、反馈、自学习与长期精简 |
| [`docs/`](docs/) | 现行文档与运行手册；不保留已完成阶段文档 |
| [`eval/contexts/`](eval/contexts/) | 评测语料（测试会读） |
| [`service/openapi/`](service/openapi/) | OpenAPI 契约快照（漂移测试会读） |
| [`scripts/`](scripts/) | CI / gate 脚本 |
| [`AGENTS.md`](AGENTS.md) | 注释与测试约定 |
| [`TODO.md`](TODO.md) | 一页式当前执行顺序 |

下面这些目录只保存机器可读或可重新生成的历史证据，不是活路径，不要当现行架构：

- [`vector/`](vector/)
- [`learning/`](learning/)
- [`eval/`](eval/)（除 `contexts/`）
- [`service/`](service/)（除 `openapi/`）
- [`foundation/`](foundation/)
- [`storage/`](storage/)（仓库内的 postgres 冒烟报告，不是 `src/ContextCore.Storage.*`）

根上的 `build_*.log` / `test_*.log` 已被 gitignore，不必提交。

## 默认怎么跑

```bash
dotnet run --project src/ContextCore.Service
```

默认：`Storage:Provider=filesystem`，确定性模型，Echo 工具。
HTTP 检索/打包与 Agent 构建上下文默认都走决策运行时（切流 100）。
设 `CC_CUTOVER_PERCENTAGE=0` 可把 HTTP 两条切回混合检索与基础打包器。详见活路径文档。

## 当前方向（2026-08-14）

当前先完成可复现基线与质量指标，再做批量 embedding、多问句单次读取、排序准确率、反馈数据闭环和受控自学习。性能基线见 [`benchmarks/results/MULTIQUERY_RECALL_BASELINE.md`](benchmarks/results/MULTIQUERY_RECALL_BASELINE.md)。

代码精简是支撑轨：零引用公共契约、重复 DTO、宿主依赖、DI 和大状态机按质量门逐步收敛；没有质量贡献与生产 owner 的实验能力最终删除。当前不直接打开 Adaptive/Learning，也不凭内部 `FinalScore` 判断召回质量。
