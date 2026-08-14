# ContextCore

独立的上下文管理服务：摄入、检索、打包，以及可选的 Agent Run 循环。

**先读 [`docs/LIVE_PATH.md`](docs/LIVE_PATH.md)**。RF 重构/精简/性能阶段已完成，下一份执行清单尚未定义。不要从仓库根上的 `vector/`、`learning/`、freeze 报告推断现行行为。

## 打开仓库看哪

| 路径 | 角色 |
| --- | --- |
| [`src/`](src/) | 代码。宿主是 `ContextCore.Service` |
| [`tests/`](tests/) | 测试 |
| [`docs/LIVE_PATH.md`](docs/LIVE_PATH.md) | **现行调用链** |
| [`docs/NEXT_PHASE_REFACTOR.md`](docs/NEXT_PHASE_REFACTOR.md) | **已完成的重构/精简/性能阶段清单**（RF-1…RF-7，含基线数据） |
| [`docs/`](docs/) | 现行文档与运行手册；不保留已完成阶段文档 |
| [`eval/contexts/`](eval/contexts/) | 评测语料（测试会读） |
| [`service/openapi/`](service/openapi/) | OpenAPI 契约快照（漂移测试会读） |
| [`scripts/`](scripts/) | CI / gate 脚本 |
| [`AGENTS.md`](AGENTS.md) | 注释与测试约定 |
| [`TODO.md`](TODO.md) | 一页式当前路线与历史入口 |

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

## 整理与切流（2026-08-14）

已经做完：活路径文档、误导注释收口、docs freeze 归档、证据目录贴牌子、HTTP 缺省切流 100、召回接线和多轮找回，以及 RF 重构/精简/性能阶段（RF-1…RF-7，见 [`docs/NEXT_PHASE_REFACTOR.md`](docs/NEXT_PHASE_REFACTOR.md) 与 [`benchmarks/results/MULTIQUERY_RECALL_BASELINE.md`](benchmarks/results/MULTIQUERY_RECALL_BASELINE.md)）。

**下一阶段：** 无已定义执行清单。RF 阶段结论：已持有 ID 下推到向量检索（欠召回 0）、通用租约层与旧工厂删除、HTTP 错误映射统一；性能基线建议优先批量 embedding/query 去重与 FileSystem 单请求批量读取。

**没有做、也不在这一步做：** 把 `vector/` / `learning/` 两千多个文件搬走、R46、接原型仓库、打开 Adaptive/Learning。
