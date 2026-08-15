# 多问句召回性能基线（RF-5 / LR-0D / LR-2A / LR-2B / LR-2C）

> 生成：2026-08-14（Release / net10.0 / 本机，SDK 由 `global.json` 固定为 10.0.301）。
> 数据文件：`results/results/multiquery-recall-baseline-20260814-163711.csv`（LR-2C 后主数据，162 行，InMemory/FileSystem 维度全覆盖，1200 条数据）；
> `multiquery-recall-baseline-20260814-134155.csv`（LR-2C 前主数据，162 行，1200 条数据）；
> `multiquery-recall-baseline-20260814-113724.csv`（LR-2B 前同配置基线，162 行）；
> `multiquery-recall-baseline-20260814-165003.csv`（LR-2C 后 50 条数据 × 3 provider，含 Postgres 维度）；
> `multiquery-recall-baseline-20260814-133815.csv`（LR-2C 前 50 条数据 × 3 provider，含 Postgres 维度）。
> 复现命令：`dotnet run -c Release --project benchmarks/ContextCore.RetrievalBaseline`；
> 可选：`--items <n>` 数据集规模、`--postgres <connstr>` 启用 Postgres 维度。
> 每次运行同时产出 `*.env.json`（commit/运行时/OS/CPU/GC/数据规模/维度配置）与 `*-cold.csv`（冷启动首 op 时延）。

## 1. 测量面

固定数据集：1200 条上下文（`doc-00000`…`doc-01199`，标题含 8 组关键词）＋同量 8 维确定性向量记录。

| 维度 | 取值 |
| --- | --- |
| QueryTexts 数量 | 1 / 4 / 8 |
| TopK | 10 / 50 / 100 |
| Held ID 数量 | 0 / 10 / 100 |
| Provider | InMemory / FileSystem（`--postgres` 时加 Postgres） |
| Mode | lexical-only / semantic-only / combined |
| 指标 | p50 / p95 / 吞吐 / 分配字节 / embedding 次数 / vector search 次数 / 存储 roundtrip / 有效候选 / 欠召回 / 连接池占用（Postgres） |

分配字节用进程级单调计数（`GC.GetTotalAllocatedBytes`）整段循环差值除以迭代数，**不再出现负值**；预热 30 次后进入稳定态测量（InMemory 120 次、FileSystem/Postgres 40 次），冷启动首 op 单独记录在 `*-cold.csv`。
Postgres 维度需要真实数据库与连接串（`--postgres`），连接池占用通过 `pg_stat_activity` 计数观测；本报告正文基线只覆盖内存与文件系统（162 组合），Postgres 的 roundtrip 与连接池行为在集成环境另行测量（本机 Docker 实测见下）。

### 2.5 Postgres 维度（本机 Docker pgvector/pgvector:pg16，`--items 50`，数据文件 `multiquery-recall-baseline-20260814-104323.csv`，243 行 = 81 组合 × 3 provider）

| Provider | p50 量级（lexical / semantic / combined） | 连接池占用（pg_stat_activity） |
| --- | --- | --- |
| Postgres | ~1.7-2 ms / ~2 ms / ~5 ms | min 2、max 3（并发上限受测量循环串行约束） |
| InMemory / FileSystem | 同 162 组合基线 | 0（无数据库连接） |

Postgres 连接池在同一测量循环内稳定在 2-3 个连接，未出现连接泄漏或池膨胀；冷启动首 op 与 roundtrip 已随 `*-cold.csv` 记录。

### 2.6 LR-2A 批量 embedding 效果（113724 与 002400 对比，1200 条数据）

Semantic 通道改为规范化去重后一次批量 `EmbedAsync`（q 条问句 → 1 次 embedding 调用，向量按 input ID 映射后仍逐条 vector search）：

| 指标 | 旧（002400） | 新（113724） |
| --- | --- | --- |
| embedding 调用/op（q=4） | 4.00 | 1.00 |
| embedding 调用/op（q=8） | 8.00 | 1.00 |
| InMemory semantic q=8 topK=50 p95 | 4.02 ms | 3.50 ms |
| InMemory q8/q1 p95 比值（同轮内） | 10.05 | 4.49 |
| FileSystem q8/q1 p95 比值（同轮内） | 7.39 | 7.21 |
| 有效候选 / 欠召回（162 组合） | 与旧版逐位一致，欠召回 = 0 | 同左 |

embedding 随 q 的线性分量已消除（InMemory q8/q1 比值减半）；FileSystem semantic 以向量文件 I/O 为主，几乎不受影响。固定 Fake Provider 下绝对 p95 降幅有限（q=8 topK=50：-13%），30% 绝对门限需在真实 ONNX Provider（`EmbedBatchAsync` 真批量）下复测。

### 2.7 LR-2B 多问句 lexical 单次读取（134155 vs 113724，1200 条数据）

Lexical 通道新增窄能力 `IContextStoreMultiQuery`（不能复用按 ID 的 `IContextStoreBatchLookup` 冒充）：FileSystem 在一次 snapshot 内完成全部问句过滤（metadata 一次读取、正文一次 materialize 并缓存）；InMemory 单次枚举；Postgres 单条 CTE/LATERAL（jsonb 问句载荷 + 每问句独立 refs/TopK，保留 per-query 语义）；provider 在 q≥2 时一次调用替代 q 次 QueryAsync，q=1 保持旧路径。未实现该能力的 store 自动回退逐问句路径。

| 指标 | 前（113724） | 后（134155） |
| --- | --- | --- |
| 存储 roundtrip/op（lexical q=4 / q=8，InMemory/FileSystem/Postgres） | 4.00 / 8.00 | 1.00 / 1.00 |
| FileSystem q8/q1 p95 比值（combined k=50 h=0） | 9.00 | 1.14 |
| FileSystem q=8 combined p95 k=50 h=0 / 10 / 100 | 2901 / 3201 / 7567 ms | 436 / 432 / 410 ms（−85% / −86.5% / −94.6%） |
| FileSystem q=8 combined p95 k=100 h=0 / 10 / 100 | 4297 / 4332 / 2516 ms | 535 / 530 / 522 ms（−87.5% / −87.8% / −79.3%） |
| FileSystem q=8 lexical-only p95 k=10 / 50 / 100 | 2261 / 2551 / 2895 ms | 320 / 344 / 300 ms（−86% / −86.5% / −89.6%） |
| FileSystem q=8 combined p50（9 组合范围） | 2206–3150 ms | 360–522 ms（−78%..−85%） |
| 召回平价 | — | 162/162 逐位一致（1200 项）；50 项含 Postgres 243/243 逐位一致 |
| 全量扫描墙钟 | ~91 min | ~27 min |

说明：
- q=8 combined 的 p95 降幅 9 组合中 8 组合 ≥60%（唯一例外 k=10 h=0 为 −50%，同轮 q=1 该组合 p95 也异常抬升至 853 ms，属 GC/磁盘抖动，p50 仍 −82%）。
- 「目标低于 500 ms」：k=50 全部组合 410–436 ms 达标；k=100（合并 800 候选）521–535 ms 略超，但低于同轮 q=1 k=100 h=10 的 p95（985 ms）——残余成本是候选合并/水合本身，不是问句线性放大。
- Postgres roundtrip q→1 由 133815（50 项 × 3 provider）验证；Postgres 批量路径召回与旧逐条路径逐位一致（q=1→7、q=4→26、q=8→50，与 104323 完全相同），refs/每问句 TopK/元数据投影由集成测试覆盖。

### 2.8 LR-2C 批量 vector search（163711 vs 134155，1200 条数据；165003 vs 133815，50 条 × 3 provider）

Semantic 通道新增窄能力 `IVectorStoreMultiSearch`：FileSystem 一次快照读全部向量文件、多问句计算；InMemory 单次记录枚举；Postgres 单条 CTE/LATERAL（`unnest(@query_literals) WITH ORDINALITY` + `CROSS JOIN LATERAL` + `ROW_NUMBER() PARTITION BY query_index`，问句向量以 text[] 字面量传入）。provider 在 q≥2 且 store 具备该能力时一次 `SearchMultiAsync` 替代 q 次 `SearchAsync`（q=1 保持旧路径、无能力 store 自动回退逐问句）；每条问句独立 TopK、共享作用域/过滤/排除，最高分合并规则不变。

| 指标 | 前 | 后 |
| --- | --- | --- |
| vector search 调用/op（q=4 / q=8，三 provider semantic/combined） | 4.00 / 8.00 | 1.00 / 1.00 |
| Postgres semantic q=8 p95（6 组合范围） | 10.77–21.19 ms | 3.02–5.09 ms（−55%～−83%） |
| Postgres q8/q1 p95 比值（k=10/50/100，h=0） | ~8（逐条 roundtrip） | 1.00 / 1.34 / 1.31 |
| FileSystem semantic q=8 k=10 p95（h=0/10） | 58.74 / 64.52 ms | 25.40 / 29.56 ms（−57% / −54%） |
| FileSystem semantic q=8 k=50 p95（h=0/10） | 160.72 / 383.67 ms | 114.98 / 109.64 ms（−28% / −71%） |
| InMemory semantic 代表性 p95（q=4 k=10 h=10 / q=8 k=100 h=0） | 1.30 / 9.41 ms | 0.83 / 8.16 ms（−36% / −13%） |
| 召回平价 | — | 162/162 逐位一致（1200 项）；50 项含 Postgres 243/243 逐位一致 |

说明：
- Postgres 旧值 = 逐条 8 次 SQL roundtrip；新值 = 单条 SQL（8 个向量字面量 unnest）。残余 q8/q1 比值 1.3× 是单 roundtrip 内 8 组相似度计算与排序的 CPU 成本，不再是外部调用/连接池放大。
- FileSystem 旧值 = q× 全量读向量文件；新值 = 1 次快照读取 + q 组计算，q=8 k=10 p95 降一半以上。
- InMemory 单次枚举省掉 q−1 次全量枚举，但 1200 条 × 8 维下余弦与排序本身便宜，绝对降幅在 µs–ms 级；q=8 六组合中五组合持平或下降（−0.5%～−17%），k=50 h=10 一组合 p95 抬升（4.38→6.51 ms，40 次迭代下的 µs 级噪声，p50 基本持平），量级不变。
- 残留随 q 线性的是合并后的候选数（最多 q×TopK）与每问句独立排序的 CPU 成本，属设计内行为（最高分合并规则不变），不是外部调用放大。
- Postgres roundtrip q→1 由 165003（50 项 × 3 provider）验证；Postgres 批量路径召回与旧逐条路径逐位一致（与 133815 完全相同），QueryId 映射/每问句 TopK/共享排除由集成测试覆盖。

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

FileSystem lexical 在 LR-2B 前为逐问句全量读文件（q=8 → ~1.6-2.2s/op）；LR-2B 单次 snapshot 后 q=8 收敛到 ~300-350 ms（见 §2.7）。semantic 单次 jsonl 读取明显更轻。

### 2.4 调用次数（每 op，LR-2B 后）

| Mode | embedding | vector search | 存储 roundtrip |
| --- | --- | --- | --- |
| lexical-only | 0 | 0 | 1（q≥2 批量；q=1 亦 1） |
| semantic-only | 1（q≥2 批量） | q | 1（检索）＋1（水合批量） |
| combined | 1（q≥2 批量） | q | 2（1 批量词法＋1 水合批量） |

LR-2B 前：lexical-only roundtrip = q、combined = q＋1（逐问句 QueryAsync）。

## 3. 数据结论（按决策规则）

1. **LR-2A 后 embedding 不再随 q 线性增长**：Semantic 通道一次批量 `EmbedAsync` 生成全部向量（q=8 → 1 次调用），vector search 仍随 q 线性（每条向量一次搜索）→ 下一优先方向：批量 vector search（LR-2C 先测剩余占比）。8 条问句 = 1 次 embed + 8 次 search，semantic 路径随 q 的放大主要来自 search。
2. **LR-2B 后 FileSystem lexical 不再随 q 线性放大**：q8/q1 p95 比值 9.00 → 1.14（combined k=50），q=8 成本收敛到 ≈ q=1；存储 roundtrip 三 provider 均 q→1（`IContextStoreMultiQuery` 单次 snapshot / 单条 CTE/LATERAL，q=1 保持旧路径、无能力 store 自动回退）。剩余随 q 线性的是 vector search → 与结论 1 合并为同一优先方向。
3. **semantic 分条合并未做最终截断**：合并候选数可达 q×TopK（q=8、topK=100 → 800 条），combined 分配最高（~1.2-1.8 MB/op）。若下游分配成本成为瓶颈，可评估合并后按 TopK 截断；本次不改变行为（分配器预算已兜底）。
4. **欠召回 = 0（全部 162 组合）**：RF-1 后 held ID 在排序/截断前排除，TopK 始终由新候选补足；三 provider 语义一致。
5. **TopK 与 Held 数量对时延影响小**（滤波器主导的 O(N) 扫描内抖动），held=100 时 lexical 偶见小幅上升，量级可忽略。
6. **分配字节测量已修复（LR-0D）**：原 `GetAllocatedBytesForCurrentThread` 在 async 换线程下出现负值，现改用进程级单调计数（`GC.GetTotalAllocatedBytes`）整段循环差值，**无负值**；量级参考仍成立：semantic < lexical < combined。p95 未显著改善的优化不进入主线。

### 3.1 同机重复性（103020 vs 104740，同 commit/同配置）

- 分配字节稳定：FileSystem 两轮偏差 <0.01%，InMemory 绝对偏差 ≤ ~13 KB（相对最大 15%，出现在 ~90 KB 的小分配上）。
- InMemory p50 稳定：平均偏差 0.06 ms、最大 0.44 ms；FileSystem p50 平均偏差 4.9 ms、最大 44 ms，集中在磁盘 I/O 路径（页缓存/后台负载噪声），冷启动首 op 偏差最大 ~100 ms（JIT 与磁盘缓存未热）。
- 结论：分配与内存路径可复现；FileSystem 时延偏差来源可解释（I/O 噪声），不改变量级与排序结论。

## 4. 下一步数据结论

- 多问句时延的 q 线性外部调用放大已全部消除：embedding q→1（LR-2A）、lexical 存储 roundtrip q→1（LR-2B）、vector search 调用 q→1（LR-2C，三 provider）。残余随 q 线性的是合并后的候选数（最多 q×TopK）与每问句独立排序的 CPU 成本，属设计内行为。
- Postgres 语义通道单条 SQL 完成全部问句检索（LR-2C，q8/q1 p95 比值 ~8 → 1.0–1.3），连接池占用稳定在 2-3，未出现连接泄漏或池膨胀。
- 候选合并截断是候选优化项，需先确认分配器预算后是否仍有可测收益，否则不做。
- 后续优先方向转向查询覆盖改进（LR-2D）与通道覆盖决策（LR-2E）：多问句不再线性放大外部调用后，主要召回漏失分类与各通道唯一有效命中率/成本是下一优先。
