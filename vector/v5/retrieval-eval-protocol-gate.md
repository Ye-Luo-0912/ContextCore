# Retrieval Eval Protocol Gate

Generated: `2026-06-19T09:51:49.2194835+00:00`
OperationId: `vector-retrieval-eval-protocol-gate-959fc64fb92f41d785df592a70c9484c`

## Protocol
- Version: `retrieval-eval-protocol-v1`
- VectorTopK: `5`  MergedTopK: `8`  FinalTopK: `5`
- ScoreThreshold: `0.0000`
- TieBreak: `score_desc_source_precedence_candidate_id_ordinal`
- Split: `train` / `holdout`

## Gate
- GatePassed: `True`
- Recommendation: `NeedsInputMetadataEnrichment`
- BaselineProtocolReproducible: `True`
- TieBreakDeterministic: `True`
- HashOrderSensitivityCount: `0`
- EvalLabelScoringDetected: `False`
- EvalLabelCandidateGenerationDetected: `False`
- RuntimeChangeGatePassed: `True`
- Risk/mustNot/lifecycle: `0` / `0` / `0`
- Runtime invariants: formalRetrievalAllowed=`False`, runtimeSwitchAllowed=`False`, readyForRuntimeSwitch=`False`, useForRuntime=`False`

## Blocked Reasons
- (empty)
