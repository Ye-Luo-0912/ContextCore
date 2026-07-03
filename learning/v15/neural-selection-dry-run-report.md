# V15 Neural Selection Dry-Run Report

Generated: 2026-07-03T06:04:29.5152765+00:00

## Summary
- Candidates processed: 33
- Model: 10→8→3 MLP, seeded deterministic weights (seed=42)
- Spearman rho (det vs neural): 0.8327
- Selection direction agreement: 66.67%
- Mean deterministic (normalized): 0.7063
- Mean neural selection score: 0.4992

## Safety
- NeuralBiasActive: false
- PackageOutputChanged: false
- VectorBindingChanged: false
- RuntimePromotionApplied: false
- Neural scores: shadow artifacts only, not in runtime pipeline

## Per-Candidate Comparison
| Candidate | Section | Deterministic | Neural Selection | Neural Rank | Drop Prob | Agrees |
|-----------|---------|---------------|------------------|-------------|-----------|--------|
| task-smoke | current_task | 1.0000 | 0.5000 | 0.5000 | 0.5000 | yes |
| hc-dep | hard_constraints | 0.9227 | 0.5000 | 0.5000 | 0.5000 | no |
| hc-01 | hard_constraints | 0.9500 | 0.5000 | 0.5000 | 0.5000 | yes |
| hc-02 | hard_constraints | 0.9500 | 0.5000 | 0.5000 | 0.5000 | yes |
| wm-03 | working_memory | 0.4235 | 0.4846 | 0.4878 | 0.4794 | no |
| wm-02 | working_memory | 0.4217 | 0.4846 | 0.4877 | 0.4794 | no |
| wm-01 | working_memory | 0.4198 | 0.4846 | 0.4877 | 0.4793 | no |
| gc-02 | global_context | 0.0855 | 0.4864 | 0.4745 | 0.4845 | no |
| gc-01 | global_context | 0.0836 | 0.4865 | 0.4744 | 0.4846 | no |
| ctx-07 | recent_context | 0.7182 | 0.5000 | 0.5000 | 0.5000 | yes |
| ctx-08 | recent_context | 0.7182 | 0.5000 | 0.5000 | 0.5000 | yes |
| ctx-09 | recent_context | 0.7182 | 0.5000 | 0.5000 | 0.5000 | yes |
| sm-01 | stable_memory | 0.1391 | 0.4855 | 0.4850 | 0.4812 | no |
| sm-02 | stable_memory | 0.1391 | 0.4855 | 0.4850 | 0.4812 | no |
| sc-dep | soft_constraints | 0.1455 | 0.5000 | 0.5000 | 0.5000 | no |
| sc-01 | soft_constraints | 0.1682 | 0.4932 | 0.4946 | 0.4908 | no |
| sc-02 | soft_constraints | 0.1682 | 0.4932 | 0.4946 | 0.4908 | no |
