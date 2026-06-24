# Enriched Candidate Source Repair Recheck

OperationId: `vector-enriched-candidate-source-repair-recheck-c483150efc844334a2dcc429c336b645`
CreatedAt: `2026-06-19T09:17:12.1860430+00:00`

## Summary
- RecheckPassed: `False`
- GatePassed: `False`
- Recommendation: `BlockedByQualityRegression`
- QualityImproved: `True`
- EnrichedSourceRepairPassed: `False`
- MetadataCoverageDelta: `1680`
- V5.12 enrichment gate passed: `True`
- Derivation gate passed: `True`
- Runtime change gate passed: `True`

## Source Repair Before / After Enrichment
- Train derived recall: `0.4167` -> `0.4583` delta `+0.0417`
- Train derived MRR: `0.1844` -> `0.2040` delta `+0.0196`
- Holdout derived recall: `0.5000` -> `0.4167` delta `-0.0833`
- Holdout derived MRR: `0.2181` -> `0.1903` delta `-0.0278`
- Must-hit below topK: `56` -> `52` delta `-4`
- Best profile: `baseline` -> `baseline`

## Safety Invariants
- Risk/mustNot/lifecycle: `0` / `0` / `0`
- FormalOutputChanged: `0`
- FormalPackageWritten: `False`
- PackageOutputChanged: `False`
- PackingPolicyChanged: `False`
- RuntimeMutated: `False`
- VectorStoreBindingChanged: `False`
- FormalRetrievalAllowed: `False`
- RuntimeSwitchAllowed: `False`
- ReadyForRuntimeSwitch: `False`
- UseForRuntime: `False`

## Blocked Reasons
- `EnrichedSourceRepairRegression`

## Quality Blocked Reasons
- `EnrichedSourceRepairGateNotPassed`
