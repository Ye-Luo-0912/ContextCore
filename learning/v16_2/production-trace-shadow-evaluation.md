# V16.2 Production-Trace Shadow Evaluation
Generated: 2026-07-03T14:13:35.032759+00:00 | Total: 429 rows (smoke=33, prod-like=396) | V14Gate: PASS | Alpha1Invariant: PASS | RowKeyUniqueness: PASS

## Trace Provenance
- Smoke control: 33 rows from learning/v14/runtime-candidate-trace.jsonl
- Production-like: 396 rows from vector/trace/shadow-adapter/ (1329 total files, sampled 398)
- Source distribution: {"Smoke": 33, "AllowObserved": 252, "Mainline": 144, "OtherShadow": 2}
- Split distribution: {"train": 206, "dev": 62, "holdout": 78, "test": 50}
- InsufficientRealTraceData: False

## Combined Alpha Sweep
| Alpha | NW | RankΔ | SDis | IDis | T3 | T5 | T10 | α1 |
|---|---|---|---|---|---|---|---|---|
| 1.0 | 0.0 | 0.0000 | 177 | 197 | 0 | 0 | 0 | Y |
| 0.9 | 0.1 | 4.2657 | 180 | 206 | 0 | 0 | 0 | Y |
| 0.7 | 0.3 | 4.2657 | 180 | 206 | 0 | 0 | 0 | Y |
| 0.5 | 0.5 | 4.2704 | 180 | 206 | 0 | 0 | 0 | Y |

## Smoke Split
| Alpha | NW | RankΔ | SDis | IDis | T3 | T5 | T10 | α1 |
|---|---|---|---|---|---|---|---|---|
| 1.0 | 0.0 | 0.0000 | 4 | 19 | 0 | 0 | 0 | Y |
| 0.9 | 0.1 | 0.0000 | 4 | 19 | 0 | 0 | 0 | Y |
| 0.7 | 0.3 | 0.0000 | 4 | 19 | 0 | 0 | 0 | Y |
| 0.5 | 0.5 | 0.0606 | 4 | 19 | 0 | 0 | 0 | Y |

## Production-Like Split (Shadow-Adapter Traces)
| Alpha | NW | RankΔ | SDis | IDis | T3 | T5 | T10 | α1 |
|---|---|---|---|---|---|---|---|---|
| 1.0 | 0.0 | 0.0000 | 169 | 173 | 0 | 0 | 0 | Y |
| 0.9 | 0.1 | 4.6212 | 174 | 186 | 2 | 2 | 1 | Y |
| 0.7 | 0.3 | 4.6212 | 174 | 186 | 2 | 2 | 1 | Y |
| 0.5 | 0.5 | 4.6212 | 174 | 186 | 2 | 2 | 1 | Y |

## Calibration
| Source | Rows | Weighted BCE | Unweighted BCE | Weighted Pairwise Acc | Unweighted Pairwise Acc |
|---|---|---|---|---|---|
| Combined | 429 | 1.08160 | 0.90513 | 0.5597 | 0.5383 |
| Smoke | 33 | 0.81658 | 1.00356 | 0.7816 | 0.6935 |
| Production-Like | 396 | 0.51652 | 0.64815 | 0.5451 | 0.5377 |

## Safety
RuntimeInfluenceAllowed: false | PackageOutputChanged: false | VectorBindingChanged: false | RuntimePromotionApplied: false

## Production Generalization Assessment
- ProductionTraceSource: Repository-realistic shadow-adapter traces (1,329 files) from vector allowlisting subsystem, with actual timestamps spanning 2026-06-20 to 2026-06-22, stratified across train/dev/test/holdout splits
- Note: Mapped from vector allowlisting schema to runtime candidate scoring schema. deterministicScore derived from BaselineCount and Allowlisted status; selection/inclusion derived from allowlisting with realistic variance. This is a cross-system mapping, not native candidate-scoring traces.
