# ContextCore

独立的上下文管理服务：摄入、检索、打包，以及可选的 Agent Run 循环。

**先读 [`docs/LIVE_PATH.md`](docs/LIVE_PATH.md)**。下一阶段改召回按 [`docs/RECALL_WIRING.md`](docs/RECALL_WIRING.md) 逐个工作包做。不要从仓库根上的 `vector/`、`learning/`、freeze 报告推断现行行为。

## 打开仓库看哪

| 路径 | 角色 |
| --- | --- |
| [`src/`](src/) | 代码。宿主是 `ContextCore.Service` |
| [`tests/`](tests/) | 测试 |
| [`docs/LIVE_PATH.md`](docs/LIVE_PATH.md) | **现行调用链** |
| [`docs/RECALL_WIRING.md`](docs/RECALL_WIRING.md) | **下一阶段执行清单**（召回接线） |
| [`docs/`](docs/) | 现行文档 + [`archive/`](docs/archive/) 历史快照 |
| [`eval/contexts/`](eval/contexts/) | 评测语料（测试会读） |
| [`service/openapi/`](service/openapi/) | OpenAPI 契约快照（漂移测试会读） |
| [`scripts/`](scripts/) | CI / gate 脚本 |
| [`AGENTS.md`](AGENTS.md) | 注释与测试约定 |
| [`TODO.md`](TODO.md) | R31–R45 完成记录，不是开工清单 |

下面这些是**历史证据堆**，不是活路径。目录里有牌子。不要当现行架构：

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

已经做完：活路径文档、误导注释收口、docs freeze 进 archive、证据目录贴牌子、HTTP 缺省切流 100、Agent 工作集/分条问句/观察实体词。

**下一阶段：** [`docs/RECALL_WIRING.md`](docs/RECALL_WIRING.md)。

**没有做、也不在这一步做：** 把 `vector/` / `learning/` 两千多个文件搬走、R46、接原型仓库、打开 Adaptive/Learning。
