# Shadow Merge Promotion Decision

生成: `2026-06-22T10:32:22.2508879+00:00`

- FreezePassed: `True`
- PromotionDecisionPassed: `True`
- Recommendation: `ReadyForControlledMergeProposal`
- PromotionDecision: `ReadyForControlledMergeProposal`
- NextAllowedPhase: `ControlledMergeProposal`
- AllowedMode: `ControlledMergeProposalOnly`
- V66GatePassed: `True`
- V67GatePassed: `True`
- ObservationGatePassed: `True`
- RuntimeChangeGatePassed: `True`
- ObservationRunCount: `10`
- SampleObservationCount: `120`
- DeterministicPreviewStable: `True`
- DistinctStableSignatureCount: `1`
- PreviewAddCount min/max: `7` / `7`
- PreviewRemoveCount min/max: `7` / `7`
- AppliedAdd/Remove max: `0` / `0`
- Risk/MustNot/Lifecycle max: `0` / `0` / `0`
- TokenDeltaTotalMax/TokenDeltaMaxMax: `0` / `0`
- PriorityInversionCountTotal: `0`
- SectionMismatchCountTotal: `0`
- FormalSelectedSetChanged: `False`
- FormalOutputChangedMax: `0`
- FormalPackageWritten: `False`
- PackageOutputChanged: `False`
- PackingPolicyChanged: `False`
- RuntimeMutated: `False`
- VectorStoreBindingChanged: `False`
- FormalRetrievalAllowed: `False`
- RuntimeSwitchAllowed: `False`
- ReadyForRuntimeSwitch: `False`

## AllowedActions
- `controlled merge proposal planning`
- `preview merge review`
- `rollback plan validation`

## ForbiddenActions
- `applied merge`
- `formal selected set mutation`
- `formal package write`
- `PackingPolicy mutation`
- `package output mutation`
- `runtime switch`
- `formal retrieval enable`
- `formal IVectorIndexStore binding mutation`
- `global default-on`

## BlockedReasons
- (empty)

This decision freezes preview merge stability only. It does not apply the preview delta, mutate the formal selected set, write a formal package, alter PackingPolicy/package output, switch runtime, or bind a formal vector store.
