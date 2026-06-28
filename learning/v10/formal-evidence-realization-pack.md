# Learning Formal Evidence Realization Pack

- FormalEvidenceRealizationPackPassed: `True`
- GatePassed: `False`
- TotalCases: `31` PassedCases: `31` FailedCases: `0`

## Formal Label Realization Decision
- FormalLabelCandidateCount: `60`
- RealizableFormalLabelCount: `60`
- InvalidBindingCount: `0`
- **FormalLabelCandidatesReady**: `True`
- **FormalLabelsRealized**: `False` (must remain false until controlled formal ingestion)
- **FormalEvidenceSufficient**: `False`
- **RuntimePilotExecutionReadyForSeparateGate**: `False`
- BlockedForRuntimePilotExecutionBy: `FormalLabelsNotRealizedInDataset, FormalEvidenceInsufficientV3`

## Integrity Manifest
- HashAlgorithm: `SHA-256`
- TotalEntries: `60`
- VerifiedEntries: `60`
- MismatchedEntries: `0`
- AnyHashMismatch: `False`

## Rollback Contract
- ContractVersion: `v10.16-formal-label-rollback/v1`
- ContractMode: `SchemaOnlyNoRollbackExecuted`
- Fields: `8` Triggers: `4` Preconditions: `4` Actions: `4`
- FormalTrainingSetChanged: `False` AutoIngest: `False` RuntimeRollbackApplied: `False`

## Authority Invariants
- ShadowOnly: `True` FormalLabelCandidatesAreFormal: `False` FormalTrainingSetChanged: `False`
- AutoIngest: `False` TrainingSetChanged: `False`
- HumanFeedbackAsSignal: `True` HumanReviewAsGateAuthority: `False` HumanFeedbackAutoIngest: `False`
- MLAuthority: `False` LLMAuthority: `False` RuntimeAuthority: `False` GateAuthority: `False`
- RuntimePromotionApplied: `False` RuntimePilotExecutionApplied: `False`
- RuntimeRerankerChanged: `False` RuntimeRouterChanged: `False` ProductionDecisionChanged: `False`
- PackageOutputChanged: `False` FormalPackageWritten: `False` GlobalDefaultOn: `False`
- V8ScopedActivationPreserved: `True`

- Recommendation: `ProceedToControlledFormalLabelIngestionViaV9.5` NextAllowedPhase: `ControlledFormalLabelIngestion-pending-V9.5-human-feedback`
