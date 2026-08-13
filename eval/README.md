# eval/

两套东西叠在同一目录，不要混读。

| 路径 | 角色 |
| --- | --- |
| [`contexts/`](contexts/) | **现行评测语料**。`ContextEvalSampleLoader` / `ContextCoreEvalRunnerTests` 会定位这里 |
| 本目录其它 `*.json` / `*.md` | 历史 freeze、评测报告快照，不当现行依据 |

现行调用链：[`../docs/LIVE_PATH.md`](../docs/LIVE_PATH.md)。
