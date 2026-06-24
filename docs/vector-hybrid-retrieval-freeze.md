# Hybrid Retrieval Preview Freeze

更新时间：2026-06-15

## 结论

V3.11.F 冻结 Hybrid Retrieval Preview 结论。本阶段只冻结 preview / shadow / eval 结果，不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

冻结状态：

- `HybridRetrieval=KeepPreviewOnly`
- `Recommendation=BlockedByA3Recall`
- `FreezePassed=true`
- `V4RecheckAllowed=false`
- `FormalRetrievalAllowed=false`
- `UseForRuntime=false`

## V3.11 Preview / Shadow

V3.11 新增 hybrid preview 框架：

- dense candidate
- lexical candidate
- anchor candidate
- deterministic union / dedup
- eligibility metadata preservation
- risk metadata preservation

当前结果：

- A3 hybrid best recall：`4.55%`
- Extended hybrid best recall：`3.13%`
- `RiskAfterPolicy=0`
- `FormalOutputChanged=0`

lexical / anchor 没有带来 recall 增益。当前 hybrid framework 作为 preview 机制是有效的，但当前候选来源对 recall 无实际提升。

## V3.11.1 Regression Audit

Hybrid Retrieval Recall Regression Sanity Audit 已通过：

- `Passed=true`
- `Recommendation=ReadyForHybridFreeze`
- Legacy dense A3 recall：`4.55%`
- Hybrid dense-only A3 recall：`4.55%`
- Hybrid best A3 recall：`4.55%`
- Legacy dense Extended recall：`3.13%`
- Hybrid dense-only Extended recall：`3.13%`
- Hybrid best Extended recall：`3.13%`
- `DenseCandidateDroppedCount=0`
- `EligibilityMismatchCount=0`
- `DedupOverwriteCount=0`
- `RiskAfterPolicy=0`
- `FormalOutputChanged=0`

结论：

- dense-only 与 legacy dense 对齐。
- 没有 dense candidate 被 union / dedup 丢弃。
- eligibility metadata 未出现偏差。
- dedup 未覆盖 dense contribution。

## Freeze Gate

新增 `HybridRetrievalPreviewFreezeRunner` 和 CLI：

```bash
dotnet run --project src/ContextCore.ControlRoom -- eval vector-hybrid-freeze-gate
```

输出：

- `vector/hybrid/vector-hybrid-freeze-gate.json`
- `vector/hybrid/vector-hybrid-freeze-gate.md`

freeze gate 通过条件：

- `vector-hybrid-readiness-gate` 已生成，且未通过原因明确。
- `vector-hybrid-recall-regression-audit` passed。
- `DenseCandidateDroppedCount=0`
- `EligibilityMismatchCount=0`
- `DedupOverwriteCount=0`
- `RiskAfterPolicy=0`
- `FormalOutputChanged=0`
- `UseForRuntime=false`
- `FormalRetrievalAllowed=false`
- P15 gate remains passing。

A3 / Extended recall 未达 `80%` 时，freeze 可以记录为安全冻结通过，但 `V4RecheckAllowed=false`。

## Runtime Boundary

持续禁止：

- HybridRetrieval 接 formal retrieval。
- HybridRetrieval 替代正式 retrieval source。
- HybridRetrieval 绑定正式 `IVectorIndexStore`。
- HybridRetrieval 接入 `PackingPolicy`。
- HybridRetrieval 改变 package output。

进入 V4 前必须先找到有效 recall improvement source，并让 A3 / Extended recall 达到 V4 gate。
