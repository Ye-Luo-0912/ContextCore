# Relation Expansion Profile Shadow Report

Generated: 2026-06-08T13:55:29.6709869+00:00

## Summary

- Profile count: `4`
- Sample count: `16`
- Accepted relations: `35`
- Blocked relations: `39`

## Profiles

| Profile | Samples | Accepted | Blocked | Normal | Historical | Audit | Conflict | Diagnostics | Risk If Normal | Risk After Routing | Historical Audit | Conflict Evidence | Wrong Section | Top Block Reasons |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| audit-v1 | 4 | 18 | 1 | 0 | 0 | 18 | 0 | 0 | 2 | 0 | 2 | 0 | 0 | MissingEvidence: 1 |
| conflict-v1 | 4 | 6 | 13 | 0 | 0 | 0 | 6 | 0 | 2 | 0 | 0 | 6 | 0 | RelationTypeNotAllowed: 11<br>FanoutExceeded: 3<br>ConfidenceTooLow: 1<br>MissingEvidence: 1 |
| current-task-v1 | 4 | 3 | 15 | 3 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | RelationTypeNotAllowed: 11<br>FanoutExceeded: 9<br>BackwardReplacementTraversalBlocked: 2<br>DeprecatedTargetBlocked: 2<br>ConfidenceTooLow: 1<br>HistoricalTargetBlocked: 1 |
| normal-v1 | 4 | 8 | 10 | 8 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | FanoutExceeded: 7<br>BackwardReplacementTraversalBlocked: 2<br>DeprecatedTargetBlocked: 2<br>HistoricalTargetBlocked: 2<br>ConfidenceTooLow: 1 |

## Samples

| Item | Profile | Accepted | Blocked | Normal | Historical | Audit | Conflict | Risk If Normal | Risk After Routing | Wrong Section | Top Block Reasons |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| item-audit | audit-v1 | 1 | 0 | 0 | 0 | 1 | 0 | 1 | 0 | 0 | - |
| item-depth | audit-v1 | 2 | 0 | 0 | 0 | 2 | 0 | 0 | 0 | 0 | - |
| item-normal | audit-v1 | 14 | 1 | 0 | 0 | 14 | 0 | 1 | 0 | 0 | MissingEvidence:1 |
| item-old | audit-v1 | 1 | 0 | 0 | 0 | 1 | 0 | 0 | 0 | 0 | - |
| item-audit | conflict-v1 | 1 | 0 | 0 | 0 | 0 | 1 | 1 | 0 | 0 | - |
| item-depth | conflict-v1 | 2 | 0 | 0 | 0 | 0 | 2 | 0 | 0 | 0 | - |
| item-normal | conflict-v1 | 2 | 13 | 0 | 0 | 0 | 2 | 1 | 0 | 0 | RelationTypeNotAllowed:11<br>FanoutExceeded:3<br>ConfidenceTooLow:1<br>MissingEvidence:1 |
| item-old | conflict-v1 | 1 | 0 | 0 | 0 | 0 | 1 | 0 | 0 | 0 | - |
| item-audit | current-task-v1 | 0 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | BackwardReplacementTraversalBlocked:1<br>DeprecatedTargetBlocked:1<br>HistoricalTargetBlocked:1 |
| item-depth | current-task-v1 | 1 | 0 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | - |
| item-normal | current-task-v1 | 1 | 14 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | RelationTypeNotAllowed:11<br>FanoutExceeded:9<br>BackwardReplacementTraversalBlocked:1<br>ConfidenceTooLow:1<br>DeprecatedTargetBlocked:1 |
| item-old | current-task-v1 | 1 | 0 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | - |
| item-audit | normal-v1 | 0 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | BackwardReplacementTraversalBlocked:1<br>DeprecatedTargetBlocked:1<br>HistoricalTargetBlocked:1 |
| item-depth | normal-v1 | 1 | 0 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | - |
| item-normal | normal-v1 | 6 | 9 | 6 | 0 | 0 | 0 | 0 | 0 | 0 | FanoutExceeded:7<br>BackwardReplacementTraversalBlocked:1<br>ConfidenceTooLow:1<br>DeprecatedTargetBlocked:1<br>HistoricalTargetBlocked:1 |
| item-old | normal-v1 | 1 | 0 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | - |
