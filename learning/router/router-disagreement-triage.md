# Router Disagreement Triage Report

Generated: 2026-06-11T07:28:42.5129467+00:00
PolicyVersion: `router-disagreement-triage-r2.1/v1`

## Summary

| Dataset | Samples | Disagreements | Fixes | Breaks | BothWrong | LowConfidence | HardNegatives | Recommendation |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| `A3` | 35 | 4 | 1 | 3 | 0 | 0 | 3 | `KeepRuleBased` |
| `Extended` | 35 | 4 | 1 | 3 | 0 | 0 | 3 | `KeepRuleBased` |

## A3 Top Confusion Pairs

| Key | Count |
|---|---:|
| `CodingTask->FuzzyQuestion` | 2 |
| `AuditDeprecated->CodingTask` | 1 |
| `FuzzyQuestion->CurrentTask` | 1 |

## Extended Top Confusion Pairs

| Key | Count |
|---|---:|
| `CodingTask->FuzzyQuestion` | 2 |
| `AuditDeprecated->CodingTask` | 1 |
| `FuzzyQuestion->CurrentTask` | 1 |

## A3 Triage Categories

| Key | Count |
|---|---:|
| `ShadowBreaksRuntime` | 3 |
| `ShadowFixesRuntime` | 1 |

## Extended Triage Categories

| Key | Count |
|---|---:|
| `ShadowBreaksRuntime` | 3 |
| `ShadowFixesRuntime` | 1 |

## A3 Disagreements

| Sample | Mode | Expected | Runtime | Shadow | Confidence | Category | Action |
|---|---|---|---|---|---:|---|---|
| `chat-sample-005` | `ChatMode` | `CodingTask` | `CodingTask` | `FuzzyQuestion` | 0.6044 | `ShadowBreaksRuntime` | `AddHardNegative` |
| `chat-sample-005` | `ChatMode` | `CodingTask` | `CodingTask` | `FuzzyQuestion` | 0.486 | `ShadowBreaksRuntime` | `AddHardNegative` |
| `chat-20260529-019` | `ChatMode` | `CurrentTask` | `FuzzyQuestion` | `CurrentTask` | 0.5668 | `ShadowFixesRuntime` | `ReviewRuntimeBoundary` |
| `coding-sample-002` | `CodingMode` | `AuditDeprecated` | `AuditDeprecated` | `CodingTask` | 0.5469 | `ShadowBreaksRuntime` | `AddHardNegative` |

## Extended Disagreements

| Sample | Mode | Expected | Runtime | Shadow | Confidence | Category | Action |
|---|---|---|---|---|---:|---|---|
| `chat-sample-005` | `ChatMode` | `CodingTask` | `CodingTask` | `FuzzyQuestion` | 0.6044 | `ShadowBreaksRuntime` | `AddHardNegative` |
| `chat-sample-005` | `ChatMode` | `CodingTask` | `CodingTask` | `FuzzyQuestion` | 0.486 | `ShadowBreaksRuntime` | `AddHardNegative` |
| `chat-20260529-019` | `ChatMode` | `CurrentTask` | `FuzzyQuestion` | `CurrentTask` | 0.5668 | `ShadowFixesRuntime` | `ReviewRuntimeBoundary` |
| `coding-sample-002` | `CodingMode` | `AuditDeprecated` | `AuditDeprecated` | `CodingTask` | 0.5469 | `ShadowBreaksRuntime` | `AddHardNegative` |

## Runtime Safety

- This report is offline-only.
- It does not replace the runtime router.
- It does not change retrieval, planning, PackingPolicy, scoring, or package output.
- Hard negatives are exported as analysis data only.
