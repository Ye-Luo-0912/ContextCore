# V16 Hybrid Scoring Shadow Evaluation

Generated: 2026-07-03T04:32:11.2010326+00:00
Candidates: 17
V14 Gate: PASSED
Coverage Limited: True
Sections covered: current_task, hard_constraints, working_memory, global_context, recent_context, stable_memory, soft_constraints

## Alpha Sweep Results
| Alpha | Neural Wt | Mean Rank Delta | Selection Disagree | Top3 Churn | Top5 Churn | Top10 Churn | Mean Hybrid |
|-------|-----------|-----------------|--------------------|------------|------------|-------------|-------------|
| 1.0 | 0.0 | 0.0000 | 5 (29%) | 0 | 0 | 0 | 0.4807 |
| 0.9 | 0.1 | 0.0000 | 5 (29%) | 0 | 0 | 0 | 0.4821 |
| 0.7 | 0.3 | 0.0000 | 5 (29%) | 0 | 0 | 0 | 0.4851 |
| 0.5 | 0.5 | 0.0000 | 5 (29%) | 0 | 0 | 0 | 0.4880 |

## Runtime Safety
- BlendAlpha: 1.0 (runtime)
- NeuralBiasActive: false
- RuntimeInfluenceAllowed: false
- PackageOutputChanged: false
