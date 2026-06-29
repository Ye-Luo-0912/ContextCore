# Learning Controlled Formal Label Ingestion Staging R1 Pack

生成: `2026-06-29T06:32:15.2104927+00:00` 操作: `flc-staging-r1-92302ff79eaa466c9a2809d8c55fac66`

## Decision
- R1PackPassed: `True` GatePassed: `False`
- Recommendation: `ProceedToControlledFormalEvidenceIngestion` NextPhase: `ControlledFormalEvidenceIngestion-pending-canonical-hash-v2`

## Staging Summary
- Total: `60` Staged: `60` Invalid: `0` HashMismatch: `0`
- CanonicalHashCoverage: `100%` RankingPairHashCoverage: `100%` ShadowLabelHashCoverage: `100%`
- SourceCandidateLabelPrefix: `flc-r1` HashInputVersion: `v10.16R/canonical-v1`
- LegacyStagingInvalidated: `True` StagingSourceUsesLegacyHash: `True`
- MainRecommendationUsesHumanReview: `False`

## Invariants
- StagedLabelsAreFormal: `False` StagingOnly: `True` AutoIngest: `False`
- FormalTrainingSetChanged: `False` RuntimePilotExecutionApplied: `False`
- V8ScopedActivationPreserved: `True`

V10.19R R1 staging pack regeneration。
