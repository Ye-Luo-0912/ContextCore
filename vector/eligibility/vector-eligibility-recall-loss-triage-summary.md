# Vector Eligibility Recall Loss Triage Summary

Generated: 2026-06-15T09:43:24.0209176+00:00
- Recommendation: `NeedsMetadataRepair`
- TotalFilteredMustHit: `50`
- CorrectlyBlockedCount: `18`
- RouteToHistoricalCount: `0`
- RouteToAuditCount: `18`
- MetadataRepairNeededCount: `50`
- EvalExpectationReviewNeededCount: `0`
- UnsafeToRecoverCount: `0`
- RecoverableWithoutNormalContextCount: `18`
- RecoverableToNormalContextCount: `0`
- FormalRetrievalAllowed: `False`
- UseForRuntime: `False`

| Dataset | Filtered | CorrectlyBlocked | Historical | Audit | MetadataRepair | EvalReview | Unsafe | RecoverNoNormal | RecoverNormal | Recommendation |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| A3 | 25 | 9 | 0 | 9 | 25 | 0 | 0 | 9 | 0 | NeedsMetadataRepair |
| Extended | 25 | 9 | 0 | 9 | 25 | 0 | 0 | 9 | 0 | NeedsMetadataRepair |

### Category Breakdown

| Category | Count |
|---|---:|
| MetadataLifecycleRepairNeeded | 32 |
| CorrectlyBlockedDeprecated | 18 |

## A3

- ProviderId: `deterministic-hash`
- EmbeddingModel: `deterministic-hash-v1`
- Dimension: `16`
- SampleCount: `50`
- TotalFilteredMustHit: `25`
- CorrectlyBlockedCount: `9`
- RouteToHistoricalCount: `0`
- RouteToAuditCount: `9`
- MetadataRepairNeededCount: `25`
- EvalExpectationReviewNeededCount: `0`
- UnsafeToRecoverCount: `0`
- RecoverableWithoutNormalContextCount: `9`
- RecoverableToNormalContextCount: `0`
- Recommendation: `NeedsMetadataRepair`
- FormalRetrievalAllowed: `False`
- UseForRuntime: `False`

### Category Breakdown

| Category | Count |
|---|---:|
| MetadataLifecycleRepairNeeded | 16 |
| CorrectlyBlockedDeprecated | 9 |

| Category | Sample | Mode | Intent | MustHit | Lifecycle | Review | Replacement | CurrentSection | CandidateSection | RemainBlocked | Action | Rationale |
|---|---|---|---|---|---|---|---|---|---|---:|---|---|
| CorrectlyBlockedDeprecated | automation-sample-002 | AutomationMode | - | memory:automation-noise-keyword | Deprecated | - | MissingReplacement | excluded | audit_context | True | RouteToAuditContext | deprecated mustHit 被 normal profile 阻断是正确行为，只能进入 audit/diagnostics 路径。 |
| CorrectlyBlockedDeprecated | automation-sample-009 | AutomationMode | - | memory:automation-stopped-cron | Deprecated | - | MissingReplacement | excluded | audit_context | True | RouteToAuditContext | deprecated mustHit 被 normal profile 阻断是正确行为，只能进入 audit/diagnostics 路径。 |
| CorrectlyBlockedDeprecated | chat-sample-009 | ChatMode | - | memory:chat-deprecated-draft | Deprecated | - | MissingReplacement | excluded | audit_context | True | RouteToAuditContext | deprecated mustHit 被 normal profile 阻断是正确行为，只能进入 audit/diagnostics 路径。 |
| CorrectlyBlockedDeprecated | coding-sample-002 | CodingMode | - | doc:coding-noise-keyword | deprecated | - | MissingReplacement | excluded | audit_context | True | RouteToAuditContext | deprecated mustHit 被 normal profile 阻断是正确行为，只能进入 audit/diagnostics 路径。 |
| CorrectlyBlockedDeprecated | coding-sample-009 | CodingMode | - | memory:coding-deprecated-logger | Deprecated | - | MissingReplacement | excluded | audit_context | True | RouteToAuditContext | deprecated mustHit 被 normal profile 阻断是正确行为，只能进入 audit/diagnostics 路径。 |
| CorrectlyBlockedDeprecated | novel-sample-002 | NovelMode | - | memory:novel-plot-old-draft | Deprecated | - | MissingReplacement | excluded | audit_context | True | RouteToAuditContext | deprecated mustHit 被 normal profile 阻断是正确行为，只能进入 audit/diagnostics 路径。 |
| CorrectlyBlockedDeprecated | novel-sample-003 | NovelMode | - | memory:novel-plot-old-draft | Deprecated | - | MissingReplacement | excluded | audit_context | True | RouteToAuditContext | deprecated mustHit 被 normal profile 阻断是正确行为，只能进入 audit/diagnostics 路径。 |
| CorrectlyBlockedDeprecated | novel-sample-009 | NovelMode | - | memory:novel-plot-deprecated-villain | Deprecated | - | MissingReplacement | excluded | audit_context | True | RouteToAuditContext | deprecated mustHit 被 normal profile 阻断是正确行为，只能进入 audit/diagnostics 路径。 |
| CorrectlyBlockedDeprecated | project-sample-009 | ProjectMode | - | memory:project-deprecated-gateway | Deprecated | - | MissingReplacement | excluded | audit_context | True | RouteToAuditContext | deprecated mustHit 被 normal profile 阻断是正确行为，只能进入 audit/diagnostics 路径。 |
| MetadataLifecycleRepairNeeded | automation-sample-001 | AutomationMode | - | doc:automation-guide | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | automation-sample-003 | AutomationMode | - | doc:automation-guide | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | automation-sample-007 | AutomationMode | - | doc:automation-guide | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | automation-sample-010 | AutomationMode | - | doc:automation-guide | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | coding-sample-001 | CodingMode | - | doc:ipromotioncandidatestore | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | coding-sample-003 | CodingMode | - | doc:ipromotioncandidatestore | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | coding-sample-007 | CodingMode | - | doc:ipromotioncandidatestore | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | novel-sample-001 | NovelMode | - | novel:character-linfeng | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | novel-sample-001 | NovelMode | - | novel:world-cangqiong | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | novel-sample-007 | NovelMode | - | novel:character-linfeng | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | novel-sample-010 | NovelMode | - | novel:character-linfeng | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | project-sample-001 | ProjectMode | - | doc:local-alpha-runbook | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | project-sample-002 | ProjectMode | - | doc:postgres-not-ready | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | project-sample-003 | ProjectMode | - | doc:local-alpha-runbook | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | project-sample-003 | ProjectMode | - | doc:postgres-not-ready | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | project-sample-010 | ProjectMode | - | doc:local-alpha-runbook | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |

## Extended

- ProviderId: `deterministic-hash`
- EmbeddingModel: `deterministic-hash-v1`
- Dimension: `16`
- SampleCount: `113`
- TotalFilteredMustHit: `25`
- CorrectlyBlockedCount: `9`
- RouteToHistoricalCount: `0`
- RouteToAuditCount: `9`
- MetadataRepairNeededCount: `25`
- EvalExpectationReviewNeededCount: `0`
- UnsafeToRecoverCount: `0`
- RecoverableWithoutNormalContextCount: `9`
- RecoverableToNormalContextCount: `0`
- Recommendation: `NeedsMetadataRepair`
- FormalRetrievalAllowed: `False`
- UseForRuntime: `False`

### Category Breakdown

| Category | Count |
|---|---:|
| MetadataLifecycleRepairNeeded | 16 |
| CorrectlyBlockedDeprecated | 9 |

| Category | Sample | Mode | Intent | MustHit | Lifecycle | Review | Replacement | CurrentSection | CandidateSection | RemainBlocked | Action | Rationale |
|---|---|---|---|---|---|---|---|---|---|---:|---|---|
| CorrectlyBlockedDeprecated | automation-sample-002 | AutomationMode | - | memory:automation-noise-keyword | Deprecated | - | MissingReplacement | excluded | audit_context | True | RouteToAuditContext | deprecated mustHit 被 normal profile 阻断是正确行为，只能进入 audit/diagnostics 路径。 |
| CorrectlyBlockedDeprecated | automation-sample-009 | AutomationMode | - | memory:automation-stopped-cron | Deprecated | - | MissingReplacement | excluded | audit_context | True | RouteToAuditContext | deprecated mustHit 被 normal profile 阻断是正确行为，只能进入 audit/diagnostics 路径。 |
| CorrectlyBlockedDeprecated | chat-sample-009 | ChatMode | - | memory:chat-deprecated-draft | Deprecated | - | MissingReplacement | excluded | audit_context | True | RouteToAuditContext | deprecated mustHit 被 normal profile 阻断是正确行为，只能进入 audit/diagnostics 路径。 |
| CorrectlyBlockedDeprecated | coding-sample-002 | CodingMode | - | doc:coding-noise-keyword | deprecated | - | MissingReplacement | excluded | audit_context | True | RouteToAuditContext | deprecated mustHit 被 normal profile 阻断是正确行为，只能进入 audit/diagnostics 路径。 |
| CorrectlyBlockedDeprecated | coding-sample-009 | CodingMode | - | memory:coding-deprecated-logger | Deprecated | - | MissingReplacement | excluded | audit_context | True | RouteToAuditContext | deprecated mustHit 被 normal profile 阻断是正确行为，只能进入 audit/diagnostics 路径。 |
| CorrectlyBlockedDeprecated | novel-sample-002 | NovelMode | - | memory:novel-plot-old-draft | Deprecated | - | MissingReplacement | excluded | audit_context | True | RouteToAuditContext | deprecated mustHit 被 normal profile 阻断是正确行为，只能进入 audit/diagnostics 路径。 |
| CorrectlyBlockedDeprecated | novel-sample-003 | NovelMode | - | memory:novel-plot-old-draft | Deprecated | - | MissingReplacement | excluded | audit_context | True | RouteToAuditContext | deprecated mustHit 被 normal profile 阻断是正确行为，只能进入 audit/diagnostics 路径。 |
| CorrectlyBlockedDeprecated | novel-sample-009 | NovelMode | - | memory:novel-plot-deprecated-villain | Deprecated | - | MissingReplacement | excluded | audit_context | True | RouteToAuditContext | deprecated mustHit 被 normal profile 阻断是正确行为，只能进入 audit/diagnostics 路径。 |
| CorrectlyBlockedDeprecated | project-sample-009 | ProjectMode | - | memory:project-deprecated-gateway | Deprecated | - | MissingReplacement | excluded | audit_context | True | RouteToAuditContext | deprecated mustHit 被 normal profile 阻断是正确行为，只能进入 audit/diagnostics 路径。 |
| MetadataLifecycleRepairNeeded | automation-sample-001 | AutomationMode | - | doc:automation-guide | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | automation-sample-003 | AutomationMode | - | doc:automation-guide | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | automation-sample-007 | AutomationMode | - | doc:automation-guide | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | automation-sample-010 | AutomationMode | - | doc:automation-guide | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | coding-sample-001 | CodingMode | - | doc:ipromotioncandidatestore | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | coding-sample-003 | CodingMode | - | doc:ipromotioncandidatestore | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | coding-sample-007 | CodingMode | - | doc:ipromotioncandidatestore | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | novel-sample-001 | NovelMode | - | novel:character-linfeng | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | novel-sample-001 | NovelMode | - | novel:world-cangqiong | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | novel-sample-007 | NovelMode | - | novel:character-linfeng | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | novel-sample-010 | NovelMode | - | novel:character-linfeng | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | project-sample-001 | ProjectMode | - | doc:local-alpha-runbook | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | project-sample-002 | ProjectMode | - | doc:postgres-not-ready | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | project-sample-003 | ProjectMode | - | doc:local-alpha-runbook | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | project-sample-003 | ProjectMode | - | doc:postgres-not-ready | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
| MetadataLifecycleRepairNeeded | project-sample-010 | ProjectMode | - | doc:local-alpha-runbook | Unknown | - | NotRequired | excluded | diagnostics_only | True | RepairLifecycleMetadata | 缺少可信 lifecycle metadata；修复前不能绕过 eligibility gate。 |
