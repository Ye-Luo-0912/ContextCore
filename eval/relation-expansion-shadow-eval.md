# Relation Expansion Shadow Eval Report

Generated: 2026-06-08T13:59:07.4805853+00:00

## A3 Summary

- Eval samples: `50`
- Profile/sample rows: `200`
- Profiles: `4`
- Formal output changed: `0`
- Selected set changed: `0`

| Profile | Samples | Accepted | Blocked | Would Add | MustHit Gain | MustNotHit Risk | Lifecycle Risk | Missing Evidence | Normal | Historical | Audit | Conflict | Diagnostics | Risk If Normal | Risk After Routing | Historical Audit | Conflict Evidence | Wrong Section | Recommendation |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| audit-v1 | 50 | 60 | 0 | 57 | 0 | 0 | 0 | 0 | 0 | 0 | 60 | 0 | 0 | 57 | 0 | 60 | 0 | 0 | ReadyForAuditShadow |
| conflict-v1 | 50 | 60 | 0 | 57 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 60 | 0 | 57 | 0 | 0 | 60 | 0 | ReadyForConflictShadow |
| current-task-v1 | 50 | 0 | 60 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | KeepPreviewOnly |
| normal-v1 | 50 | 0 | 60 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | KeepPreviewOnly |

## A3 Notable Samples

| Sample | Mode | Intent | Profile | Seeds | Accepted | Blocked | MustHit Gain | MustNotHit Risk | Lifecycle Risk | Risk If Normal | Risk After Routing | Historical Audit | Conflict Evidence | Wrong Section | Top Block Reasons | Recommendation |
|---|---|---|---|---:|---:|---:|---|---|---|---:|---:|---:|---:|---:|---|---|
| automation-sample-001 | AutomationMode | AutomationRecovery | audit-v1 | 8 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| automation-sample-003 | AutomationMode | AutomationRecovery | audit-v1 | 8 | 2 | 0 | - | - | - | 2 | 0 | 2 | 0 | 0 | - | ReadyForAuditShadow |
| automation-sample-004 | AutomationMode | AutomationRecovery | audit-v1 | 4 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| automation-sample-005 | AutomationMode | AutomationRecovery | audit-v1 | 8 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| automation-sample-006 | AutomationMode | AutomationRecovery | audit-v1 | 9 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| automation-sample-008 | AutomationMode | AutomationRecovery | audit-v1 | 6 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| automation-sample-010 | AutomationMode | AutomationRecovery | audit-v1 | 7 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-sample-001 | ChatMode | FuzzyQuestion | audit-v1 | 8 | 2 | 0 | - | - | - | 2 | 0 | 2 | 0 | 0 | - | ReadyForAuditShadow |
| chat-sample-002 | ChatMode | FuzzyQuestion | audit-v1 | 5 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-sample-003 | ChatMode | FuzzyQuestion | audit-v1 | 9 | 2 | 0 | - | - | - | 2 | 0 | 2 | 0 | 0 | - | ReadyForAuditShadow |
| chat-sample-004 | ChatMode | FuzzyQuestion | audit-v1 | 9 | 3 | 0 | - | - | - | 3 | 0 | 3 | 0 | 0 | - | ReadyForAuditShadow |
| chat-sample-005 | ChatMode | CodingTask | audit-v1 | 3 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-sample-006 | ChatMode | FuzzyQuestion | audit-v1 | 9 | 3 | 0 | - | - | - | 3 | 0 | 3 | 0 | 0 | - | ReadyForAuditShadow |
| chat-sample-007 | ChatMode | FuzzyQuestion | audit-v1 | 6 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-sample-008 | ChatMode | FuzzyQuestion | audit-v1 | 9 | 3 | 0 | - | - | - | 3 | 0 | 3 | 0 | 0 | - | ReadyForAuditShadow |
| chat-sample-010 | ChatMode | FuzzyQuestion | audit-v1 | 5 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| coding-sample-001 | CodingMode | CodingTask | audit-v1 | 8 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| coding-sample-003 | CodingMode | CodingTask | audit-v1 | 8 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| coding-sample-004 | CodingMode | CodingTask | audit-v1 | 6 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| coding-sample-005 | CodingMode | CodingTask | audit-v1 | 8 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| coding-sample-006 | CodingMode | CodingTask | audit-v1 | 8 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| coding-sample-008 | CodingMode | CodingTask | audit-v1 | 4 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| coding-sample-010 | CodingMode | CodingTask | audit-v1 | 8 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| novel-sample-001 | NovelMode | NovelGeneration | audit-v1 | 10 | 3 | 0 | - | - | - | 3 | 0 | 3 | 0 | 0 | - | ReadyForAuditShadow |
| novel-sample-004 | NovelMode | NovelGeneration | audit-v1 | 8 | 3 | 0 | - | - | - | 3 | 0 | 3 | 0 | 0 | - | ReadyForAuditShadow |
| novel-sample-005 | NovelMode | NovelGeneration | audit-v1 | 8 | 3 | 0 | - | - | - | 3 | 0 | 3 | 0 | 0 | - | ReadyForAuditShadow |
| novel-sample-006 | NovelMode | NovelGeneration | audit-v1 | 3 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| novel-sample-008 | NovelMode | NovelGeneration | audit-v1 | 7 | 3 | 0 | - | - | - | 3 | 0 | 3 | 0 | 0 | - | ReadyForAuditShadow |
| novel-sample-010 | NovelMode | NovelGeneration | audit-v1 | 8 | 3 | 0 | - | - | - | 3 | 0 | 3 | 0 | 0 | - | ReadyForAuditShadow |
| project-sample-001 | ProjectMode | FuzzyQuestion | audit-v1 | 6 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| project-sample-002 | ProjectMode | FuzzyQuestion | audit-v1 | 6 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| project-sample-003 | ProjectMode | FuzzyQuestion | audit-v1 | 6 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| project-sample-004 | ProjectMode | FuzzyQuestion | audit-v1 | 5 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| project-sample-005 | ProjectMode | FuzzyQuestion | audit-v1 | 9 | 2 | 0 | - | - | - | 2 | 0 | 2 | 0 | 0 | - | ReadyForAuditShadow |
| project-sample-007 | ProjectMode | CodingTask | audit-v1 | 4 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| project-sample-008 | ProjectMode | FuzzyQuestion | audit-v1 | 9 | 2 | 0 | - | - | - | 2 | 0 | 2 | 0 | 0 | - | ReadyForAuditShadow |
| automation-sample-001 | AutomationMode | AutomationRecovery | conflict-v1 | 8 | 1 | 0 | - | - | - | 1 | 0 | 0 | 1 | 0 | - | ReadyForConflictShadow |
| automation-sample-003 | AutomationMode | AutomationRecovery | conflict-v1 | 8 | 2 | 0 | - | - | - | 2 | 0 | 0 | 2 | 0 | - | ReadyForConflictShadow |
| automation-sample-004 | AutomationMode | AutomationRecovery | conflict-v1 | 4 | 1 | 0 | - | - | - | 1 | 0 | 0 | 1 | 0 | - | ReadyForConflictShadow |
| automation-sample-005 | AutomationMode | AutomationRecovery | conflict-v1 | 8 | 1 | 0 | - | - | - | 1 | 0 | 0 | 1 | 0 | - | ReadyForConflictShadow |

## Extended Summary

- Eval samples: `113`
- Profile/sample rows: `452`
- Profiles: `4`
- Formal output changed: `0`
- Selected set changed: `0`

| Profile | Samples | Accepted | Blocked | Would Add | MustHit Gain | MustNotHit Risk | Lifecycle Risk | Missing Evidence | Normal | Historical | Audit | Conflict | Diagnostics | Risk If Normal | Risk After Routing | Historical Audit | Conflict Evidence | Wrong Section | Recommendation |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| audit-v1 | 113 | 83 | 0 | 77 | 0 | 0 | 0 | 0 | 0 | 0 | 83 | 0 | 0 | 77 | 0 | 83 | 0 | 0 | ReadyForAuditShadow |
| conflict-v1 | 113 | 83 | 0 | 77 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 83 | 0 | 77 | 0 | 0 | 83 | 0 | ReadyForConflictShadow |
| current-task-v1 | 113 | 0 | 83 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | KeepPreviewOnly |
| normal-v1 | 113 | 0 | 83 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | KeepPreviewOnly |

## Extended Notable Samples

| Sample | Mode | Intent | Profile | Seeds | Accepted | Blocked | MustHit Gain | MustNotHit Risk | Lifecycle Risk | Risk If Normal | Risk After Routing | Historical Audit | Conflict Evidence | Wrong Section | Top Block Reasons | Recommendation |
|---|---|---|---|---:|---:|---:|---|---|---|---:|---:|---:|---:|---:|---|---|
| automation-20260529-006 | AutomationMode | AutomationRecovery | audit-v1 | 21 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| automation-sample-003 | AutomationMode | AutomationRecovery | audit-v1 | 21 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| automation-sample-004 | AutomationMode | AutomationRecovery | audit-v1 | 19 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| automation-sample-008 | AutomationMode | AutomationRecovery | audit-v1 | 20 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-20260529-001 | ChatMode | LongTermPreference | audit-v1 | 26 | 2 | 0 | - | - | - | 2 | 0 | 2 | 0 | 0 | - | ReadyForAuditShadow |
| chat-20260529-002 | ChatMode | FuzzyQuestion | audit-v1 | 22 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-20260529-003 | ChatMode | CurrentTask | audit-v1 | 25 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-20260529-004 | ChatMode | FuzzyQuestion | audit-v1 | 22 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-20260529-005 | ChatMode | FuzzyQuestion | audit-v1 | 20 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-20260529-007 | ChatMode | FuzzyQuestion | audit-v1 | 27 | 2 | 0 | - | - | - | 2 | 0 | 2 | 0 | 0 | - | ReadyForAuditShadow |
| chat-20260529-008 | ChatMode | FuzzyQuestion | audit-v1 | 21 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-20260529-009 | ChatMode | FuzzyQuestion | audit-v1 | 20 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-20260529-010 | ChatMode | CodingTask | audit-v1 | 22 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-20260529-011 | ChatMode | FuzzyQuestion | audit-v1 | 22 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-20260529-012 | ChatMode | FuzzyQuestion | audit-v1 | 20 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-20260529-013 | ChatMode | FuzzyQuestion | audit-v1 | 22 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-20260529-014 | ChatMode | FuzzyQuestion | audit-v1 | 22 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-20260529-015 | ChatMode | CodingTask | audit-v1 | 23 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-20260529-016 | ChatMode | FuzzyQuestion | audit-v1 | 22 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-20260529-017 | ChatMode | FuzzyQuestion | audit-v1 | 22 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-20260529-018 | ChatMode | FuzzyQuestion | audit-v1 | 22 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-20260529-019 | ChatMode | CurrentTask | audit-v1 | 25 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-20260529-020 | ChatMode | FuzzyQuestion | audit-v1 | 21 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-sample-001 | ChatMode | FuzzyQuestion | audit-v1 | 26 | 2 | 0 | - | - | - | 2 | 0 | 2 | 0 | 0 | - | ReadyForAuditShadow |
| chat-sample-002 | ChatMode | FuzzyQuestion | audit-v1 | 24 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-sample-003 | ChatMode | FuzzyQuestion | audit-v1 | 26 | 2 | 0 | - | - | - | 2 | 0 | 2 | 0 | 0 | - | ReadyForAuditShadow |
| chat-sample-004 | ChatMode | FuzzyQuestion | audit-v1 | 27 | 3 | 0 | - | - | - | 3 | 0 | 3 | 0 | 0 | - | ReadyForAuditShadow |
| chat-sample-005 | ChatMode | CodingTask | audit-v1 | 3 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-sample-006 | ChatMode | FuzzyQuestion | audit-v1 | 26 | 3 | 0 | - | - | - | 3 | 0 | 3 | 0 | 0 | - | ReadyForAuditShadow |
| chat-sample-007 | ChatMode | FuzzyQuestion | audit-v1 | 25 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| chat-sample-008 | ChatMode | FuzzyQuestion | audit-v1 | 26 | 3 | 0 | - | - | - | 3 | 0 | 3 | 0 | 0 | - | ReadyForAuditShadow |
| chat-sample-010 | ChatMode | FuzzyQuestion | audit-v1 | 24 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| coding-20260529-001 | CodingMode | CodingTask | audit-v1 | 21 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| coding-20260529-002 | CodingMode | CodingTask | audit-v1 | 23 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| coding-20260529-003 | CodingMode | CodingTask | audit-v1 | 20 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| coding-20260529-004 | CodingMode | CodingTask | audit-v1 | 20 | 2 | 0 | - | - | - | 2 | 0 | 2 | 0 | 0 | - | ReadyForAuditShadow |
| coding-20260529-005 | CodingMode | CodingTask | audit-v1 | 19 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| coding-20260529-006 | CodingMode | CodingTask | audit-v1 | 25 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| coding-20260529-007 | CodingMode | CodingTask | audit-v1 | 22 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |
| coding-20260529-008 | CodingMode | CodingTask | audit-v1 | 23 | 1 | 0 | - | - | - | 1 | 0 | 1 | 0 | 0 | - | ReadyForAuditShadow |

