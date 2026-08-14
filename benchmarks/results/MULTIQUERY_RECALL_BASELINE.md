# 多问句召回性能基线（RF-5）

> 生成：2026-08-14（Release / net10.0 / 本机）。数据文件：`results/results/multiquery-recall-baseline-20260814-002400.csv`（162 行，维度全覆盖）。
> 复现命令：`dotnet run -c Release --project benchmarks/ContextCore.RetrievalBaseline`。

## 1. 测量面

固定数据集：1200 条上下文（`doc-00000`…`doc-01199`，标题含 8 组关键词）＋同量 8 维确定性向量记录。

| 维度 | 取值 |
| --- | --- |
| QueryTexts 数量 | 1 / 4 / 8 |
| TopK | 10 / 50 / 100 |
| Held ID 数量 | 0 / 10 / 100 |
| Provider | InMemory / FileSystem |
| Mode | lexical-only / semantic-only / combined |
| 指标 | p50 / p95 / 吞吐 / 分配字节 / embedding 次数 / vector search 次数 / 存储 roundtrip / 有效候选 / 欠召回 |

Postgres 维度需要真实数据库与连接串，本基线只覆盖内存与文件系统（SQL 路径的 roundtrip 与连接池行为需在集成环境另行测量）。

## 2. 核心数据（p50，ms）

### 2.1 模式成本（InMemory，q=1，topK=50，held=0）

| Mode | p50 | p95 | ops/s | 分配 |
| --- | --- | --- | --- | --- |
| lexical-only | 0.46 | 0.73 | 1953 | ~1.0 MB |
| semantic-only | 0.23 | 0.40 | 3985 | ~0.33 MB |
| combined | 0.73 | 1.21 | 1238 | ~1.4 MB |

### 2.2 问句数量线性放大（InMemory，topK=50，held=0）

| Mode | q=1 | q=4 | q=8 |
| --- | --- | --- | --- |
| lexical-only | 0.46 | 1.74 | 4.13 |
| semantic-only | 0.23 | 0.97 | 2.08 |
| combined | 0.73 | 3.29 | 6.98 |

### 2.3 Provider 差异（q=1，topK=50，held=0）

| Mode | InMemory | FileSystem |
| --- | --- | --- |
| lexical-only | 0.46 | 200.06 |
| semantic-only | 0.23 | 15.65 |
| combined | 0.73 | 201.99 |

FileSystem lexical 逐问句全量读文件（q=8 → ~1.6-2.2s/op）；semantic 单次 jsonl 读取明显更轻。

### 2.4 调用次数（每 op）

| Mode | embedding | vector search | 存储 roundtrip |
| --- | --- | --- | --- |
| lexical-only | 0 | 0 | q |
| semantic-only | q | q | 1（检索）＋1（水合批量） |
| combined | q | q | q＋1（水合批量） |

## 3. 数据结论（按决策规则）

1. **embedding 与 vector search 随 QueryTexts 线性增长（各 q 次），且在 semantic / combined 中占主导** → 优先方向：批量 embedding / query 去重缓存。8 条问句 = 8 次 embed + 8 次 search，是 semantic 路径随 q 线性放大的直接原因。
2. **FileSystem lexical 的存储 roundtrip 随 q 线性放大（逐问句全量扫描文件）** → 优先方向：单请求批量读取（`FileContextStore` 已实现 `IContextStoreBatchLookup`，但 `QueryAsync` 路径仍逐条全扫）；不应并行放大 IO。
3. **semantic 分条合并未做最终截断**：合并候选数可达 q×TopK（q=8、topK=100 → 800 条），combined 分配最高（~1.2-1.8 MB/op）。若下游分配成本成为瓶颈，可评估合并后按 TopK 截断；本次不改变行为（分配器预算已兜底）。
4. **欠召回 = 0（全部 162 组合）**：RF-1 后 held ID 在排序/截断前排除，TopK 始终由新候选补足；三 provider 语义一致。
5. **TopK 与 Held 数量对时延影响小**（滤波器主导的 O(N) 扫描内抖动），held=100 时 lexical 偶见小幅上升，量级可忽略。
6. **分配字节测量噪声大**（`GetAllocatedBytesForCurrentThread` 受 GC 影响，部分为负值），仅作量级参考：semantic < lexical < combined。p95 未显著改善的优化不进入主线。

## 4. 下一步数据结论

- 若要降多问句时延：先做批量 embedding / query 去重（InMemory semantic q=8 从 2.08ms 的线性部分主要来自 q 次 embed+search）。
- 若要降 FileSystem 时延：先做单请求批量读取，而不是并行放大文件 IO。
- 候选合并截断是候选优化项，需先确认分配器预算后是否仍有可测收益，否则不做。
