# V16 Neural Calibration Shadow Report

Generated: 2026-07-03T04:32:11.2010326+00:00
Method: Binary logistic regression
Coefficients: a=0.180299, b=0.376995
Final BCE loss: 0.651755
Pairwise ranking accuracy: 36.36% (48/132 pairs)

## Per-Candidate Calibration
| Candidate | Neural Score | Calibrated Score | Label | Calibrated Prob |
|-----------|-------------|-----------------|-------|----------------|
| task-smoke | 0.5000 | 0.6147 | 1 | 0.6147 |
| hc-dep | 0.5000 | 0.6147 | 0 | 0.6147 |
| hc-01 | 0.5000 | 0.6147 | 1 | 0.6147 |
| hc-02 | 0.5000 | 0.6147 | 1 | 0.6147 |
| wm-03 | 0.4846 | 0.6140 | 1 | 0.6140 |
| wm-02 | 0.4846 | 0.6140 | 1 | 0.6140 |
| wm-01 | 0.4846 | 0.6140 | 1 | 0.6140 |
| gc-02 | 0.4864 | 0.6141 | 1 | 0.6141 |
| gc-01 | 0.4865 | 0.6141 | 1 | 0.6141 |
| ctx-07 | 0.5000 | 0.6147 | 1 | 0.6147 |
| ctx-08 | 0.5000 | 0.6147 | 1 | 0.6147 |
| ctx-09 | 0.5000 | 0.6147 | 1 | 0.6147 |
| sm-01 | 0.4969 | 0.6146 | 0 | 0.6146 |
| sm-02 | 0.4969 | 0.6146 | 0 | 0.6146 |
| sc-dep | 0.5000 | 0.6147 | 0 | 0.6147 |
| sc-01 | 0.5000 | 0.6147 | 0 | 0.6147 |
| sc-02 | 0.5000 | 0.6147 | 0 | 0.6147 |

## Note
Offline shadow calibration only. Not deployed to runtime.
