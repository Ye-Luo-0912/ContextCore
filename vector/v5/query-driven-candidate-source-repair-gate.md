# Query-driven Candidate Source Repair Gate

生成: `2026-06-19T06:27:05.4784561+00:00`
操作: `query-driven-candidate-source-repair-gate-a8cde9e1080941f2b7c26a84936656ae`

## 摘要
- ReportPassed: `False`  GatePassed: `False`
- BestProfile: `baseline` (Baseline)
- 推荐: `BlockedByRecallNotImproved`

## 评分对比
- Dense baseline      : recall=0.3333 MRR=0.1475 belowTopK=0 hits=0/0
-                     : recall=0.0000 MRR=0.0000 belowTopK=0 hits=0/0
-                     : recall=0.0000 MRR=0.0000 belowTopK=0 hits=0/0
-                     : recall=0.0000 MRR=0.0000 belowTopK=0 hits=0/0
-                     : recall=0.0000 MRR=0.0000 belowTopK=0 hits=0/0
- Combined            : recall=0.3333 MRR=0.1475 belowTopK=0 hits=0/0

- TrainBaselineRecall: `0.4167`  TrainDerivedRecall: `0.4167`  delta: `0.0000`
- HoldoutBaselineRecall: `0.5000`  HoldoutDerivedRecall: `0.5000`
- 风险: risk=0  mustNot=0  life=0  section=0
- forbiddenReads: 0

## Blocked
- `DerivedMrrNotImproved`
- `DerivedRecallNotImproved`

查询驱动的候选来源修复。6 种查询驱动的 label-free 候选来源。不做 formal retrieval/package write/selected set change/packing/runtime change。
