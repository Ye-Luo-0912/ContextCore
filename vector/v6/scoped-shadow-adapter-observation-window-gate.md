# Scoped Shadow Adapter Observation Window Gate

生成: `2026-06-22T07:42:52.0813508+00:00`
操作: `scoped-shadow-observation-48b9600e274b4e4baf8820a389afed67`

- ObservationPassed: `True`
- Recommendation: `ReadyForShadowObservationFreeze`
- EvalOnlySyntheticScope: `True`
- ResolvedScopeKey: `eval-only:synthetic-alternating`
- SampleCount: `120`
- MainlineInvocations: `120`
- Allowlisted: `60`  NonAllowlisted: `60`
- Hypothetical add/remove: `0/0`
- Applied add/remove: `0/0`
- TraceCompleteness: `100.00%`
- P50/P95 latency: `0/0`

## Diagnostics
- `scopeResolved=eval-only:synthetic-alternating hasRealScope=False`
- `total: 120 allowlisted: 60 nonAllowlisted: 60`
- `hypoAdd=0 hypoRemove=0 appliedAdd=0 appliedRemove=0`

## Blocked
- (empty)

V6.4 shadow observation only. Adapter output discarded. No formal retrieval, package write, selected set change, packing/runtime mutation.
