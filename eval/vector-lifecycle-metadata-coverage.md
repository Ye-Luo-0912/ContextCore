# Vector Lifecycle Metadata Coverage Report

Generated: 2026-06-10T07:13:40.6468729+00:00

## Summary

- Workspace: `eval-vector`
- Collection: `corpus`
- Provider: `onnx-local`
- Model: `bge-small-zh-v1.5`
- Dimension: `512`
- TotalVectorSourceItems: `158`
- KnownLifecycleCount: `158`
- UnknownLifecycleCount: `0`
- MissingReviewStatusCount: `149`
- MissingReplacementInfoCount: `13`
- LegacySourceWithoutLifecycleCount: `0`
- DeprecatedSourceWithoutLifecycleCount: `0`
- LifecycleCoverageRate: `100.00%`
- Recommendation: `ReadyForVectorShadowEval`

## Coverage By Layer

| Key | Total | Known | Unknown | MissingReview | MissingReplacement | LegacyNoLifecycle | DeprecatedNoLifecycle | Coverage |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| context | 11 | 11 | 0 | 2 | 4 | 0 | 0 | 100.00 % |
| Stable | 25 | 25 | 0 | 25 | 0 | 0 | 0 | 100.00 % |
| Working | 122 | 122 | 0 | 122 | 9 | 0 | 0 | 100.00 % |

## Coverage By Item Kind

| Key | Total | Known | Unknown | MissingReview | MissingReplacement | LegacyNoLifecycle | DeprecatedNoLifecycle | Coverage |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| api-contract | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| assertion | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| belief | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| blocker | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| build-error | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| capability-matrix | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| character | 3 | 3 | 0 | 2 | 0 | 0 | 0 | 100.00 % |
| character-detail | 4 | 4 | 0 | 4 | 0 | 0 | 0 | 100.00 % |
| character-knowledge | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| character-state | 3 | 3 | 0 | 3 | 0 | 0 | 0 | 100.00 % |
| comment-rule | 2 | 2 | 0 | 2 | 0 | 0 | 0 | 100.00 % |
| compatibility | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| conclusion | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| config-detail | 4 | 4 | 0 | 4 | 0 | 0 | 0 | 100.00 % |
| confirmation | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| confirmed-rule | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| constraint | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| cron-log | 1 | 1 | 0 | 1 | 1 | 0 | 0 | 100.00 % |
| deal | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| dedupe | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| documentation | 7 | 7 | 0 | 3 | 5 | 0 | 0 | 100.00 % |
| draft | 1 | 1 | 0 | 1 | 1 | 0 | 0 | 100.00 % |
| ending-plan | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| env-info | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| environment | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| error-log | 3 | 3 | 0 | 3 | 0 | 0 | 0 | 100.00 % |
| event-log | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| fix-report | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| foreshadow | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| guide | 1 | 1 | 0 | 0 | 0 | 0 | 0 | 100.00 % |
| interface | 1 | 1 | 0 | 0 | 0 | 0 | 0 | 100.00 % |
| item-state | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| job-state | 2 | 2 | 0 | 2 | 0 | 0 | 0 | 100.00 % |
| linting-rule | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| location | 2 | 2 | 0 | 2 | 0 | 0 | 0 | 100.00 % |
| milestone | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| motive | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| performance-rule | 2 | 2 | 0 | 2 | 0 | 0 | 0 | 100.00 % |
| plan | 3 | 3 | 0 | 3 | 0 | 0 | 0 | 100.00 % |
| plot | 4 | 4 | 0 | 4 | 3 | 0 | 0 | 100.00 % |
| plot-hook | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| plot-scope | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| pm-contact | 2 | 2 | 0 | 2 | 0 | 0 | 0 | 100.00 % |
| policy | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| postgres-config | 2 | 2 | 0 | 2 | 0 | 0 | 0 | 100.00 % |
| precheck | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| preference | 12 | 12 | 0 | 12 | 1 | 0 | 0 | 100.00 % |
| preference-pattern | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| promotion-candidate | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| promotion-policy | 3 | 3 | 0 | 3 | 0 | 0 | 0 | 100.00 % |
| queue-state | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| recovery-point | 2 | 2 | 0 | 2 | 0 | 0 | 0 | 100.00 % |
| relationship | 2 | 2 | 0 | 2 | 0 | 0 | 0 | 100.00 % |
| report | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| resume | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| retry-state | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| revision | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| risk | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| roadmap | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| run-log | 2 | 2 | 0 | 2 | 1 | 0 | 0 | 100.00 % |
| run-report | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| runbook | 2 | 2 | 0 | 2 | 1 | 0 | 0 | 100.00 % |
| safety | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| scene | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| schema | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| scope | 2 | 2 | 0 | 2 | 0 | 0 | 0 | 100.00 % |
| security-constraint | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| security-rule | 2 | 2 | 0 | 2 | 0 | 0 | 0 | 100.00 % |
| signature | 4 | 4 | 0 | 4 | 0 | 0 | 0 | 100.00 % |
| source-scope | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| step-state | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| story-arc | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| stress-test | 5 | 5 | 0 | 5 | 0 | 0 | 0 | 100.00 % |
| style | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| subplot | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| system-intro | 1 | 1 | 0 | 0 | 0 | 0 | 0 | 100.00 % |
| task | 2 | 2 | 0 | 2 | 0 | 0 | 0 | 100.00 % |
| task-state | 2 | 2 | 0 | 2 | 0 | 0 | 0 | 100.00 % |
| test-failure | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| test-rule | 2 | 2 | 0 | 2 | 0 | 0 | 0 | 100.00 % |
| testing-rule | 2 | 2 | 0 | 2 | 0 | 0 | 0 | 100.00 % |
| todo | 2 | 2 | 0 | 2 | 0 | 0 | 0 | 100.00 % |
| tool-error | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| uncertainty | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| user-location | 2 | 2 | 0 | 2 | 0 | 0 | 0 | 100.00 % |
| verification | 5 | 5 | 0 | 5 | 0 | 0 | 0 | 100.00 % |
| worker | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| worker-stats | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |
| world-rule | 3 | 3 | 0 | 3 | 0 | 0 | 0 | 100.00 % |
| world-setting | 1 | 1 | 0 | 0 | 0 | 0 | 0 | 100.00 % |
| world-state | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 100.00 % |

## Coverage By Source Type

| Key | Total | Known | Unknown | MissingReview | MissingReplacement | LegacyNoLifecycle | DeprecatedNoLifecycle | Coverage |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| context | 11 | 11 | 0 | 2 | 4 | 0 | 0 | 100.00 % |
| memory | 147 | 147 | 0 | 147 | 9 | 0 | 0 | 100.00 % |

## Warnings

- 存在历史或替代链相关 source 缺少 replacement metadata。
