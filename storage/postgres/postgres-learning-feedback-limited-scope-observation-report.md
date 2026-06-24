# Postgres Learning Feedback Limited Scope Observation

- GatePassed: `True`
- WorkspaceId: `contextcore_selected_normal`
- CollectionId: `learning-feedback-selected-normal`
- ObservationWindowMinutes: `10`
- ProviderMode: `GuardedPostgresPrimary`
- OperationCount: `18`
- PostgresPrimaryReadCount: `7`
- PostgresPrimaryWriteCount: `10`
- FileSystemFallbackCount: `0`
- ComparisonTraceCount: `18`
- MismatchCount: `0`
- PostgresFailureCount: `0`
- ScopeLeakCount: `0`
- ErrorRate: `0`
- FallbackRate: `0`
- ExportProjectionParityPassed: `True`
- SummaryParityPassed: `True`
- ReviewSummaryParityPassed: `True`
- FeatureCandidateParityPassed: `True`
- TrainableCandidateLeakCount: `0`
- SmokeCandidateExcludedCount: `1`
- CleanupPerformed: `False`
- Recommendation: `ReadyForFreezeGate`
- RollbackInstruction: `remove limited learning feedback scope allowlist or set LearningFeedbackProviderSwitchOptions.Enabled=false`

## Mismatches
- (empty)

## Blocked Reasons
- (empty)

## Diagnostics
- `SelectedNormalScopeCanary`
- `NoTrainingNoRuntimeBehaviorChange`
