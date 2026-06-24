# Shadow Adapter Delta Diagnostics

生成: `2026-06-22T07:36:41.5713542+00:00`

- DiagnosticsPassed: `True`
- SampleCount: `120`
- BaselinePool: `600` ShadowPool: `1200`
- Overlap: `600/600` (100.00%)
- BaselineOnly: `0` ShadowOnly: `600`
- Filters: eligibility=0 lifecycle=90 belowTopK=600 dup=0
- DeltaZeroCauses: `CandidateSourceNoUniqueContribution, ShadowCandidateBelowTopK, ShadowCandidateFiltered, ScopeSyntheticDatasetLimitation`

## Diagnostics
- `samples=120 baselinePool=600 shadowPool=1200`
- `overlap=600/600 (100.00%)`
- `baselineOnly=0 shadowOnly=600`
- `filters: eligibility=0 lifecycle=90 belowTopK=600 duplicate=0`
- `deltaZeroCauses: CandidateSourceNoUniqueContribution, ShadowCandidateBelowTopK, ShadowCandidateFiltered, ScopeSyntheticDatasetLimitation`

## Blocked
- (empty)

V6.5 delta diagnostics only. No formal retrieval, package write, selected set change, packing/runtime mutation.
