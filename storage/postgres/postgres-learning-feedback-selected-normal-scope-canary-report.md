# Postgres Learning Feedback Selected Normal Scope Canary

- GatePassed: `True`
- WorkspaceId: `contextcore_selected_normal`
- CollectionId: `learning-feedback-selected-normal`
- ProviderMode: `GuardedPostgresPrimary`
- OperationCount: `18`
- PostgresPrimaryReadCount: `7`
- PostgresPrimaryWriteCount: `10`
- FileSystemFallbackCount: `0`
- ComparisonTraceCount: `18`
- MismatchCount: `0`
- PostgresFailureCount: `0`
- ScopeLeakCount: `0`
- ExportProjectionParityPassed: `True`
- SummaryParityPassed: `True`
- ReviewSummaryParityPassed: `True`
- FeatureCandidateParityPassed: `True`
- CleanupPerformed: `False`
- Recommendation: `ReadyForLimitedFeedbackScope`
- RollbackInstruction: `remove selected learning feedback scope allowlist or set LearningFeedbackProviderSwitchOptions.Enabled=false`

## Mismatches
- (empty)

## Blocked Reasons
- (empty)

## Diagnostics
- `SelectedNormalScopeCanary`
- `NoTrainingNoRuntimeBehaviorChange`
