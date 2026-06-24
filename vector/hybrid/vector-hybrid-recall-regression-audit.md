# Hybrid Retrieval Recall Regression Audit

Generated: 2026-06-15T07:02:25.3900837+00:00
- Passed: `True`
- LegacyDenseRecallA3: `4.55%`
- HybridDenseOnlyRecallA3: `4.55%`
- HybridBestRecallA3: `4.55%`
- LegacyDenseRecallExtended: `3.12%`
- HybridDenseOnlyRecallExtended: `3.12%`
- HybridBestRecallExtended: `3.12%`
- CandidateLossCount: `0`
- DenseCandidateDroppedCount: `0`
- EligibilityMismatchCount: `0`
- ProviderScopeMismatchCount: `0`
- TopKConfigMismatchCount: `0`
- QueryVectorMismatchCount: `0`
- DedupOverwriteCount: `0`
- UseForRuntime: `False`
- FormalOutputChanged: `0`
- Recommendation: `ReadyForHybridFreeze`

| Profile | Dataset | Samples | Candidates | Eligible | Blocked | Recall | MRR | Risk | Dropped | EligMismatch |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| legacy-dense-baseline | A3 | 50 | 500 | 380 | 120 | 4.55% | 0.0000 | 0 | 0 | 0 |
| legacy-dense-baseline | Extended | 113 | 1130 | 863 | 267 | 3.12% | 0.0000 | 0 | 0 | 0 |
| hybrid-dense-only | A3 | 50 | 500 | 380 | 120 | 4.55% | 0.0000 | 0 | 0 | 0 |
| hybrid-dense-only | Extended | 113 | 1130 | 863 | 267 | 3.12% | 0.0000 | 0 | 0 | 0 |
| hybrid-dense-plus-lexical | A3 | 50 | 500 | 380 | 120 | 4.55% | 0.0000 | 0 | 0 | 0 |
| hybrid-dense-plus-lexical | Extended | 113 | 1130 | 863 | 267 | 3.12% | 0.0000 | 0 | 0 | 0 |
| hybrid-dense-plus-anchor | A3 | 50 | 500 | 380 | 120 | 4.55% | 0.0000 | 0 | 0 | 0 |
| hybrid-dense-plus-anchor | Extended | 113 | 1130 | 863 | 267 | 3.12% | 0.0000 | 0 | 0 | 0 |
| hybrid-dense-plus-lexical-anchor | A3 | 50 | 500 | 380 | 120 | 4.55% | 0.0000 | 0 | 0 | 0 |
| hybrid-dense-plus-lexical-anchor | Extended | 113 | 1130 | 863 | 267 | 3.12% | 0.0000 | 0 | 0 | 0 |
| lexical-only | A3 | 50 | 0 | 0 | 0 | 0.00% | 0.0000 | 0 | 0 | 0 |
| lexical-only | Extended | 113 | 0 | 0 | 0 | 0.00% | 0.0000 | 0 | 0 | 0 |
| anchor-only | A3 | 50 | 11 | 0 | 11 | 0.00% | 0.0000 | 0 | 0 | 0 |
| anchor-only | Extended | 113 | 59 | 0 | 59 | 0.00% | 0.0000 | 0 | 0 | 0 |
