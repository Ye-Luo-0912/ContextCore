# Attention Order Quality Report

Generated: 2026-06-04

## Scope

This report covers Guarded Attention Rerank Phase 5 selected order quality evaluation and Phase 6 profile tuning / weight sweep.

The experiment remains guarded:

- `Retrieval:AttentionRerank:Enabled=false` by default
- `Mode=SelectedSetPreserving`
- `ProfileId=old-score-anchored-v1`
- PackingPolicy still decides the selected item set
- attention may only reorder already selected items when all guards pass
- Phase 6 sweep profiles remain explicit-eval only and do not enable rerank by default

## Phase E1 Boundary Note

Phase E1 is an extended eval failure triage and package quality round. It does not change attention rerank behavior:

- attention rerank remains default off
- no selected set mutation is allowed
- no retrieval scoring, PackingPolicy, vector, layered retrieval, LLM judge, or NamedPipe change was made

The new triage outputs are separate package quality diagnostics:

- `eval/extended-failure-triage-report.json`
- `eval/extended-failure-triage-report.md`
- `docs/extended-eval-triage-report.md`

## Metrics

- `SelectedOrderMRR`: reciprocal rank of the first selected must-hit item.
- `FirstMustHitSelectedRank`: first must-hit rank inside selected order.
- `MustHitAverageSelectedRank`: average rank of selected must-hit items.
- `ConstraintAverageRank`: average rank of selected constraint items.
- `LifecycleRiskAverageRank`: average rank of selected lifecycle-risk items. Higher means later in order.
- `AttentionOrderDelta`: average absolute selected-order movement.
- `MovedUpMustHitCount`: count of must-hit items moved earlier.
- `MovedDownMustHitCount`: count of must-hit items moved later.

## Safety Gates

| Gate | Required |
|---|---:|
| selected set diff | `0` |
| added / dropped items | `0 / 0` |
| lifecycle violation | `0` |
| hard constraint missing | `0` |

## Sorting Gates

| Gate | Required |
|---|---|
| A3 SelectedOrderMRR | not lower than baseline |
| Extended FirstMustHitSelectedRank | no material regression |
| ConstraintAverageRank | no regression |
| LifecycleRiskAverageRank | must not move earlier |
| MustHitAverageSelectedRank | no regression |

## Phase 6 Profile Sweep

Sources:

- `eval/guarded-attention-profile-sweep-a3.json`
- `eval/guarded-attention-profile-sweep-extended.json`

All Phase 6 profiles preserve selected set membership:

- selected set diff: `0`
- added / dropped: `0 / 0`
- lifecycle violation: `0`
- hard constraint missing: `0`
- safety gates: passed for all profiles
- sorting gates: passed for all profiles

| Profile | Dataset | SelectedOrderMRR | FirstMustHitSelectedRank | MustHitAverageSelectedRank | ConstraintAverageRank | LifecycleRiskAverageRank | AttentionOrderDelta | MovedUpMustHit | MovedDownMustHit | Safety | Sorting |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| old-score-anchored-v1 | A3 50 | 0.8050 | 1.4000 | 1.6433 | 0.9800 | 0.6527 | 0.0000 | 0 | 0 | yes | yes |
| old-score-anchored-v1-light | A3 50 | 0.8050 | 1.4000 | 1.6433 | 0.9800 | 0.6527 | 0.0000 | 0 | 0 | yes | yes |
| old-score-anchored-v1-balanced | A3 50 | 0.8050 | 1.4000 | 1.6433 | 0.9800 | 0.6527 | 0.0000 | 0 | 0 | yes | yes |
| old-score-anchored-v1-strong | A3 50 | 0.8050 | 1.4000 | 1.6433 | 0.9800 | 0.6593 | 0.0200 | 0 | 0 | yes | yes |
| old-score-anchored-v1 | Extended 113 | 0.8204 | 1.6372 | 1.9292 | 1.0000 | 0.4003 | 0.0531 | 2 | 0 | yes | yes |
| old-score-anchored-v1-light | Extended 113 | 0.8189 | 1.6460 | 1.9381 | 1.0000 | 0.3767 | 0.0088 | 1 | 0 | yes | yes |
| old-score-anchored-v1-balanced | Extended 113 | 0.8204 | 1.6372 | 1.9292 | 1.0000 | 0.4065 | 0.0649 | 2 | 0 | yes | yes |
| old-score-anchored-v1-strong | Extended 113 | 0.8309 | 1.6018 | 1.8850 | 0.9823 | 0.4311 | 0.1327 | 7 | 0 | yes | yes |

Phase 6 selected-order winner: `old-score-anchored-v1-strong`. It keeps A3 metrics flat while improving Extended SelectedOrderMRR, FirstMustHitSelectedRank, and MustHitAverageSelectedRank. It does not move any must-hit item down and does not change selected membership.

## A3 Result

Source: `eval/guarded-attention-order-quality-report-a3.json`

| Metric | Baseline | Reranked |
|---|---:|---:|
| Samples | 50 | 50 |
| Applied | 0 | 0 |
| SelectedOrderMRR | 0.8050 | 0.8050 |
| FirstMustHitSelectedRank | 1.4000 | 1.4000 |
| MustHitAverageSelectedRank | 1.6433 | 1.6433 |
| ConstraintAverageRank | 0.9800 | 0.9800 |
| LifecycleRiskAverageRank | 0.6527 | 0.6527 |

Safety result:

- selected set diff：`0`
- added / dropped：`0 / 0`
- lifecycle violation：`0`
- hard constraint missing：`0`
- safety gates：`5 / 5`
- sorting gates：`5 / 5`

## Extended Result

Source: `eval/guarded-attention-order-quality-report-extended.json`

| Metric | Baseline | Reranked |
|---|---:|---:|
| Samples | 113 | 113 |
| Applied | 6 | 6 |
| SelectedOrderMRR | 0.8182 | 0.8204 |
| FirstMustHitSelectedRank | 1.6549 | 1.6372 |
| MustHitAverageSelectedRank | 1.9469 | 1.9292 |
| ConstraintAverageRank | 1.0000 | 1.0000 |
| LifecycleRiskAverageRank | 0.3723 | 0.4003 |

Safety result:

- selected set diff：`0`
- added / dropped：`0 / 0`
- lifecycle violation：`0`
- hard constraint missing：`0`
- safety gates：`5 / 5`
- sorting gates：`5 / 5`

## Conclusion

`old-score-anchored-v1` remains the stable safe profile under selected-set-preserving rerank. Phase 6 identifies `old-score-anchored-v1-strong` as the best tuned candidate for future explicit guarded experiments. No Phase 6 profile changes selected membership, adds or drops selected items, violates lifecycle guards, or loses hard constraints.
