# Postgres Learning Feedback Freeze Gate

- Passed: `True`
- LearningFeedbackPostgres: `ReadyForScopedServiceMode`
- DefaultProvider: `FileSystem`
- AllowedMode: `GuardedPostgresPrimary only for allowlisted scopes`
- ReadinessGatePassed: `True`
- ProviderQualityReady: `True`
- ScopedServiceModeGatePassed: `True`
- SelectedNormalScopeCanaryPassed: `True`
- LimitedObservationQualityPassed: `True`
- MismatchCount: `0`
- PostgresFailureCount: `0`
- ScopeLeakCount: `0`
- TrainableCandidateLeakCount: `0`
- ExportProjectionParityPassed: `True`
- SummaryParityPassed: `True`
- ReviewSummaryParityPassed: `True`
- FeatureCandidateParityPassed: `True`
- FallbackRequired: `True`
- ComparisonTraceRequired: `True`
- GlobalDefaultOnForbidden: `True`
- P15GatePassed: `True`
- Recommendation: `ReadyForScopedServiceMode`

## Required
- `fallback`
- `comparison trace`

## Forbidden
- `global default-on`
- `auto-training`
- `auto-readiness-change`

## Blocked Reasons
- (empty)

## Diagnostics
- `DefaultProviderFileSystem`
- `AllowlistedGuardedPostgresPrimaryOnly`
- `NoTrainingNoRuntimePolicyChange`
