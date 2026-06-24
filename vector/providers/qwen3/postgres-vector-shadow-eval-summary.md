# Postgres Vector Shadow Eval Summary

- Recommendation: `BlockedByProjectionMismatch`
- UseForRuntime: `False`

| Dataset | Samples | Recall | FS Recall | Delta | MRR | Risk | MustNotRisk | LifecycleRisk | FormalChanged | Overlap | OrderingMismatch | ProjectionMismatch | Recommendation |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| A3 | 50 | 54.55% | 54.55% | 0 | 0.5187 | 1 | 1.75% | 0.00% | 0 | 88.60% | 49 | 171 | BlockedByProjectionMismatch |
| Extended | 113 | 76.88% | 77.50% | -0.00625 | 0.7578 | 1 | 0.82% | 0.00% | 0 | 87.79% | 112 | 414 | BlockedByProjectionMismatch |
