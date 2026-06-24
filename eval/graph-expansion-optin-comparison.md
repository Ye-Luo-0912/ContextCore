# Graph Expansion Opt-in Comparison

## A3

| Metric | Value |
|---|---:|
| TotalSamples | 50 |
| NormalSelectedSetChanged | 0 |
| AuxiliaryGraphSectionChanged | 39 |
| GraphExpansionAppliedCount | 39 |
| AddedAuditContextItems | 60 |
| AddedConflictEvidenceItems | 60 |
| RiskAfterRoutingCount | 0 |
| WrongSectionRiskCount | 0 |
| MustNotHitRiskCount | 0 |
| LifecycleRiskCount | 0 |
| MissingEvidenceCount | 0 |
| FallbackCount | 0 |
| PassRateDelta | 0.0000 |
| WarningDelta | 7 |
| ExpectedWarningDelta | 7 |
| UnexpectedWarningDelta | 0 |
| DisallowedNormalContextInjection | 0 |
| GuardStatus | Passed |

| WarningKind | Count |
|---|---:|
| AuxiliaryGraphSectionAdded | 39 |
| ExpectedAuditContextAdded | 39 |
| ExpectedConflictEvidenceAdded | 39 |

## Extended

| Metric | Value |
|---|---:|
| TotalSamples | 113 |
| NormalSelectedSetChanged | 0 |
| AuxiliaryGraphSectionChanged | 68 |
| GraphExpansionAppliedCount | 68 |
| AddedAuditContextItems | 83 |
| AddedConflictEvidenceItems | 83 |
| RiskAfterRoutingCount | 0 |
| WrongSectionRiskCount | 0 |
| MustNotHitRiskCount | 0 |
| LifecycleRiskCount | 0 |
| MissingEvidenceCount | 0 |
| FallbackCount | 0 |
| PassRateDelta | 0.0000 |
| WarningDelta | 12 |
| ExpectedWarningDelta | 12 |
| UnexpectedWarningDelta | 0 |
| DisallowedNormalContextInjection | 0 |
| GuardStatus | Passed |

| WarningKind | Count |
|---|---:|
| AuxiliaryGraphSectionAdded | 68 |
| ExpectedAuditContextAdded | 68 |
| ExpectedConflictEvidenceAdded | 68 |

## Sample Diffs

| Scope | Sample | Mode | Applied | Fallback | Added | Sections | NormalChanged | ExpectedWarn | UnexpectedWarn | Guard | RiskChecks | Kinds |
|---|---|---|---:|---:|---|---|---:|---:|---:|---|---|---|
| A3 | chat-sample-001 | ChatMode | yes | no | memory:chat-drink-preference-v1, memory:chat-old-topic | audit_context, conflict_evidence | no | 1 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | chat-sample-002 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | chat-sample-003 | ChatMode | yes | no | memory:chat-drink-preference-v1, memory:chat-old-topic | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | chat-sample-004 | ChatMode | yes | no | memory:chat-drink-preference-v1, memory:chat-old-topic, memory:chat-version-conflict-v1 | audit_context, conflict_evidence | no | 1 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | chat-sample-005 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | chat-sample-006 | ChatMode | yes | no | memory:chat-drink-preference-v1, memory:chat-old-topic, memory:chat-version-conflict-v1 | audit_context, conflict_evidence | no | 1 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | chat-sample-007 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | chat-sample-008 | ChatMode | yes | no | memory:chat-drink-preference-v1, memory:chat-old-topic, memory:chat-version-conflict-v1 | audit_context, conflict_evidence | no | 1 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | chat-sample-010 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | project-sample-001 | ProjectMode | yes | no | memory:project-pool-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | project-sample-002 | ProjectMode | yes | no | memory:project-pool-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | project-sample-003 | ProjectMode | yes | no | memory:project-pool-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | project-sample-004 | ProjectMode | yes | no | memory:project-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | project-sample-005 | ProjectMode | yes | no | memory:project-conflict-v1, memory:project-pool-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | project-sample-007 | ProjectMode | yes | no | memory:project-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | project-sample-008 | ProjectMode | yes | no | memory:project-conflict-v1, memory:project-pool-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | novel-sample-001 | NovelMode | yes | no | memory:novel-conflict-v1, memory:novel-plot-old-draft, memory:novel-weapon-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | novel-sample-002 | NovelMode | yes | no | memory:novel-plot-old-draft | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | novel-sample-003 | NovelMode | yes | no | memory:novel-plot-old-draft | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | novel-sample-004 | NovelMode | yes | no | memory:novel-conflict-v1, memory:novel-plot-old-draft, memory:novel-weapon-v1 | audit_context, conflict_evidence | no | 1 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | novel-sample-005 | NovelMode | yes | no | memory:novel-conflict-v1, memory:novel-plot-old-draft, memory:novel-weapon-v1 | audit_context, conflict_evidence | no | 1 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | novel-sample-006 | NovelMode | yes | no | memory:novel-plot-old-draft | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | novel-sample-008 | NovelMode | yes | no | memory:novel-conflict-v1, memory:novel-plot-old-draft, memory:novel-weapon-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | novel-sample-010 | NovelMode | yes | no | memory:novel-conflict-v1, memory:novel-plot-old-draft, memory:novel-weapon-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | automation-sample-001 | AutomationMode | yes | no | memory:automation-backup-strategy-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | automation-sample-003 | AutomationMode | yes | no | memory:automation-backup-strategy-v1, memory:automation-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | automation-sample-004 | AutomationMode | yes | no | memory:automation-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | automation-sample-005 | AutomationMode | yes | no | memory:automation-backup-strategy-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | automation-sample-006 | AutomationMode | yes | no | memory:automation-backup-strategy-v1 | audit_context, conflict_evidence | no | 1 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | automation-sample-008 | AutomationMode | yes | no | memory:automation-backup-strategy-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | automation-sample-010 | AutomationMode | yes | no | memory:automation-backup-strategy-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | coding-sample-001 | CodingMode | yes | no | memory:coding-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | coding-sample-002 | CodingMode | yes | no | memory:coding-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | coding-sample-003 | CodingMode | yes | no | memory:coding-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | coding-sample-004 | CodingMode | yes | no | memory:coding-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | coding-sample-005 | CodingMode | yes | no | memory:coding-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | coding-sample-006 | CodingMode | yes | no | memory:coding-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | coding-sample-008 | CodingMode | yes | no | memory:coding-timeout-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| A3 | coding-sample-010 | CodingMode | yes | no | memory:coding-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-sample-001 | ChatMode | yes | no | memory:chat-drink-preference-v1, memory:chat-old-topic | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-sample-002 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-sample-003 | ChatMode | yes | no | memory:chat-drink-preference-v1, memory:chat-old-topic | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-sample-004 | ChatMode | yes | no | memory:chat-drink-preference-v1, memory:chat-old-topic, memory:chat-version-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-sample-005 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-sample-006 | ChatMode | yes | no | memory:chat-drink-preference-v1, memory:chat-old-topic, memory:chat-version-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-sample-007 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-sample-008 | ChatMode | yes | no | memory:chat-drink-preference-v1, memory:chat-old-topic, memory:chat-version-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-sample-010 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-20260529-001 | ChatMode | yes | no | memory:chat-drink-preference-v1, memory:chat-old-topic | audit_context, conflict_evidence | no | 1 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-20260529-002 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-20260529-003 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-20260529-004 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 1 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-20260529-005 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-20260529-007 | ChatMode | yes | no | memory:chat-drink-preference-v1, memory:chat-old-topic | audit_context, conflict_evidence | no | 1 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-20260529-008 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 1 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-20260529-009 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-20260529-010 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-20260529-011 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 1 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-20260529-012 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-20260529-013 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-20260529-014 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-20260529-015 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-20260529-016 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-20260529-017 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-20260529-018 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 1 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-20260529-019 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | chat-20260529-020 | ChatMode | yes | no | memory:chat-old-topic | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | project-sample-001 | ProjectMode | yes | no | memory:project-pool-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | project-sample-002 | ProjectMode | yes | no | memory:project-pool-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | project-sample-003 | ProjectMode | yes | no | memory:project-pool-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | project-sample-004 | ProjectMode | yes | no | memory:project-conflict-v1 | audit_context, conflict_evidence | no | 1 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | project-sample-005 | ProjectMode | yes | no | memory:project-conflict-v1, memory:project-pool-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | project-sample-008 | ProjectMode | yes | no | memory:project-conflict-v1, memory:project-pool-v1 | audit_context, conflict_evidence | no | 1 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | project-20260529-001 | ProjectMode | yes | no | memory:project-pool-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | project-20260529-002 | ProjectMode | yes | no | memory:project-pool-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | project-20260529-003 | ProjectMode | yes | no | memory:project-conflict-v1, memory:project-pool-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | novel-sample-001 | NovelMode | yes | no | memory:novel-plot-old-draft | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | novel-sample-002 | NovelMode | yes | no | memory:novel-plot-old-draft | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | novel-sample-003 | NovelMode | yes | no | memory:novel-plot-old-draft | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | novel-sample-004 | NovelMode | yes | no | memory:novel-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | novel-sample-006 | NovelMode | yes | no | memory:novel-plot-old-draft | audit_context, conflict_evidence | no | 1 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | novel-sample-008 | NovelMode | yes | no | memory:novel-weapon-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | novel-20260529-010 | NovelMode | yes | no | memory:novel-weapon-v1 | audit_context, conflict_evidence | no | 1 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | novel-20260529-013 | NovelMode | yes | no | memory:novel-weapon-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | novel-20260529-017 | NovelMode | yes | no | memory:novel-conflict-v1, memory:novel-weapon-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | automation-sample-003 | AutomationMode | yes | no | memory:automation-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | automation-sample-004 | AutomationMode | yes | no | memory:automation-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | automation-sample-008 | AutomationMode | yes | no | memory:automation-backup-strategy-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | automation-20260529-006 | AutomationMode | yes | no | memory:automation-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | coding-sample-001 | CodingMode | yes | no | memory:coding-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | coding-sample-002 | CodingMode | yes | no | memory:coding-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | coding-sample-003 | CodingMode | yes | no | memory:coding-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | coding-sample-004 | CodingMode | yes | no | memory:coding-conflict-v1 | audit_context, conflict_evidence | no | 1 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | coding-sample-005 | CodingMode | yes | no | memory:coding-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | coding-sample-006 | CodingMode | yes | no | memory:coding-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | coding-sample-008 | CodingMode | yes | no | memory:coding-timeout-v1 | audit_context, conflict_evidence | no | 1 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | coding-sample-010 | CodingMode | yes | no | memory:coding-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | coding-20260529-001 | CodingMode | yes | no | memory:coding-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | coding-20260529-002 | CodingMode | yes | no | memory:coding-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | coding-20260529-003 | CodingMode | yes | no | memory:coding-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | coding-20260529-004 | CodingMode | yes | no | memory:coding-conflict-v1, memory:coding-timeout-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | coding-20260529-005 | CodingMode | yes | no | memory:coding-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | coding-20260529-006 | CodingMode | yes | no | memory:coding-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | coding-20260529-007 | CodingMode | yes | no | memory:coding-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | coding-20260529-008 | CodingMode | yes | no | memory:coding-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | coding-20260529-009 | CodingMode | yes | no | memory:coding-timeout-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
| Extended | coding-20260529-010 | CodingMode | yes | no | memory:coding-conflict-v1 | audit_context, conflict_evidence | no | 0 | 0 | Passed | after=0; wrong=0; mustNotHit=0; lifecycle=0; missingEvidence=0 | AuxiliaryGraphSectionAdded, ExpectedAuditContextAdded, ExpectedConflictEvidenceAdded |
