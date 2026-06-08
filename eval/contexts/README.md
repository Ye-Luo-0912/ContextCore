# ContextCore 上下文评测集

本目录用于存放真实中文上下文评测样本。每个子目录代表一种主要使用场景，样本必须遵循 `context-eval-sample.schema.json`。

## 目录

| 目录 | 模式 | 目标数量 |
|---|---|---|
| `chat/` | `ChatMode` | 30-50 条 query |
| `project/` | `ProjectMode` | 30-50 条 query |
| `novel/` | `NovelMode` | 30-50 条 query |
| `automation/` | `AutomationMode` | 20-30 条 query |
| `coding-mode/` | `CodingMode` | 20-30 条 query |

## 样本要求

- 使用中文真实或近真实 query。
- `mustHit` 记录必须召回或必须进入上下文包的引用。
- `mustNotHit` 记录不应进入结果的噪音、过期项或错误分支。
- `goldenNotes` 写人工判断理由，便于失败时定位问题。

