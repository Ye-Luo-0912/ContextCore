# Router Intent Shadow Eval Report

| Dataset | Samples | Agreement | Disagreement | LowConfidence | Abstain | Fixes | Breaks | NetGain | Recommendation |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---|
| `A3` | 35 | 88.57 % | 11.43 % | 0 | 0 | 1 | 3 | -2 | `KeepRuleBased` |
| `Extended` | 35 | 88.57 % | 11.43 % | 0 | 0 | 1 | 3 | -2 | `KeepRuleBased` |

## Top Confusion Pairs

## A3

| Key | Count |
|---|---:|
| `CodingTask->FuzzyQuestion` | 2 |
| `AuditDeprecated->CodingTask` | 1 |
| `FuzzyQuestion->CurrentTask` | 1 |

## Extended

| Key | Count |
|---|---:|
| `CodingTask->FuzzyQuestion` | 2 |
| `AuditDeprecated->CodingTask` | 1 |
| `FuzzyQuestion->CurrentTask` | 1 |

## Runtime Safety

- Shadow eval is offline-only.
- It does not replace the runtime router.
- It does not change retrieval, planning, PackingPolicy, scoring, or package output.
