# Learning Controlled Formal Label Ingestion Staging Pack (Gate)

- ControlledFormalLabelIngestionStagingPackPassed: `True`
- GatePassed: `True`
- TotalCases: `28` PassedCases: `28` FailedCases: `0`

## Staging Outputs
- StagingDatasetReady: `True` StagedFormalLabelCount: `60`
- InvalidCandidateCount: `0` HashMismatchCount: `0`
- DiffPreviewReady: `True` RollbackSnapshotPlanReady: `True` QuarantinePolicyReady: `True`

## Diff Preview
- DiffMode: `PreviewOnlyNoApply`
- FormalDatasetLineCountBefore: `18`
- StagedRowCount: `60`
- WouldAddCount: `60` WouldSkipDuplicateCount: `0` WouldRejectInvalidCount: `0`
- FormalDatasetLineCountAfterIfApplied: `78`
- FormalTrainingSetChanged: `False` AutoIngest: `False`

## Rollback Snapshot Plan
- PlanMode: `PlanOnlyNoSnapshotTaken` Algorithm: `SHA-256`
- FormalDatasetCurrentLineCount: `18`
- SnapshotTaken: `False` FormalTrainingSetChanged: `False`

## Quarantine Policy
- PolicyVersion: `v10.19-formal-label-quarantine/v1` Triggers: `5` Actions: `5` ReleaseConditions: `4`
- FormalTrainingSetChanged: `False` AutoIngest: `False`

## Staging Decision
- StagingOnly: `True` StagedLabelsAreFormal: `False` FormalIngestionApplied: `False`
- RuntimePilotExecutionReadyForSeparateGate: `False`
- BlockedForFormalIngestionBy:
  - FormalIngestionNotApplied: staging is the buffer, V9.5 controlled ingestion is required to write to formal dataset.
  - RollbackSnapshotNotTaken: PlannedSnapshotPath is described but actual copy has not been executed.

## Authority Invariants
- StagingOnly: `True` StagedLabelsAreFormal: `False` FormalTrainingSetChanged: `False`
- AutoIngest: `False` HumanReviewAsGateAuthority: `False` HumanFeedbackAutoIngest: `False`
- MLAuthority: `False` LLMAuthority: `False` RuntimeAuthority: `False` GateAuthority: `False`
- RuntimePromotionApplied: `False` RuntimePilotExecutionApplied: `False`
- RuntimeRerankerChanged: `False` RuntimeRouterChanged: `False` ProductionDecisionChanged: `False`
- PackageOutputChanged: `False` FormalPackageWritten: `False` GlobalDefaultOn: `False`
- V8ScopedActivationPreserved: `True`

- FormalDatasetSizeBeforeBytes: `30022` FormalDatasetSizeAfterBytes: `30022` (must be equal)
- Recommendation: `ProceedToV9.5HumanReviewIngestionGate` NextAllowedPhase: `V9.5HumanReviewIngestionGate-pending-controlled-write`
