# V16.1 Hybrid Scoring Shadow Evaluation

Generated: 2026-07-03T05:02:49.0377144+00:00
Candidates: 33
V14 Gate: PASSED
Coverage Limited: False
Sections covered: current_task, global_context, hard_constraints, recent_context, related_context, SmokeDoc_01, SmokeDoc_02, SmokeDoc_03, SmokeDoc_04, SmokeDoc_05, SmokeDoc_06, SmokeDoc_07, SmokeDoc_08, SmokeDoc_09, SmokeDoc_10, SmokeDoc_11, SmokeDoc_12, SmokeDoc_13, SmokeDoc_14, soft_constraints, stable_memory, working_memory

## Alpha Sweep Results (Fixed: sorted ranking, top-K threshold)
| Alpha | NeurWt | Thrsh Mode | RankΔ | SelDisagree | T3Churn | T5Churn | T10Churn | MeanHyb |
|-------|--------|------------|-------|-------------|---------|---------|----------|---------|
| 1.0 | 0.0 | top-K | 0.0000 | 4 | 1 | 2 | 3 | 0.3693 |
| 0.9 | 0.1 | top-K | 0.0606 | 4 | 1 | 2 | 3 | 0.3824 |
| 0.7 | 0.3 | top-K | 0.0606 | 4 | 1 | 2 | 3 | 0.4085 |
| 0.5 | 0.5 | top-K | 0.1818 | 4 | 1 | 2 | 3 | 0.4347 |

## Runtime Safety
- BlendAlpha: 1.0 (runtime)
- NeuralBiasActive: false
- RuntimeInfluenceAllowed: false
