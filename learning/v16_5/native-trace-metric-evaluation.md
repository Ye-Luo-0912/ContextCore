# V16.5 Native Trace Metric Evaluation
Generated: 2026-07-04T09:57:29.272554+00:00 | Runs: 1 | Combined Rows: 49 | Alpha1: PASS | AllRowsTraceSource3: True

## Combined Alpha Sweep
| Alpha | NW | RankΔ | SDis | IDis | T3 | T5 | T10 | α1 |
|---|---|---|---|---|---|---|---|---|
| 1.0 | 0.0 | 0.0000 | 4 | 22 | 0 | 0 | 0 | Y |
| 0.9 | 0.1 | 0.2857 | 4 | 22 | 1 | 0 | 0 | Y |
| 0.7 | 0.3 | 0.2857 | 4 | 22 | 1 | 0 | 0 | Y |
| 0.5 | 0.5 | 0.2857 | 4 | 22 | 1 | 0 | 0 | Y |

## Combined Calibration
| Weighted BCE | Unweighted BCE | Weighted Pairwise | Unweighted Pairwise |
|---|---|---|---|
| 0.54555 | 0.66610 | 0.5192 | 0.5319 |

## Metric-Quality Gate
| Metric | Value | Threshold | Pass |
|---|---|---|---|
| Native Weighted Pairwise Acc | 0.5192 | 0.55 | False |
| NativeMetricQualityReady | False | | |
| RuntimeInfluenceReadinessCandidate | False | | |


## Run: repair-002
Trace: learning/v16_4/native-runtime-candidate-trace-repair-002.jsonl | Rows: 49

| Alpha | NW | RankΔ | SDis | IDis | T3 | T5 | T10 | α1 |
|---|---|---|---|---|---|---|---|---|
| 1.0 | 0.0 | 0.0000 | 4 | 22 | 0 | 0 | 0 | Y |
| 0.9 | 0.1 | 0.2857 | 4 | 22 | 1 | 0 | 0 | Y |
| 0.7 | 0.3 | 0.2857 | 4 | 22 | 1 | 0 | 0 | Y |
| 0.5 | 0.5 | 0.2857 | 4 | 22 | 1 | 0 | 0 | Y |

| WBCE | UBCE | WPairAcc | UPairAcc |
|---|---|---|---|
| 0.54555 | 0.66610 | 0.5192 | 0.5319 |

Scoring: 47 selected + 2 rejected = 49 (consistent=True)
Package: 36 included + 13 dropped = 49 (consistent=True)

## Safety
RuntimeInfluenceAllowed: false | PackageOutputChanged: false | VectorBindingChanged: false | RuntimePromotionApplied: false
