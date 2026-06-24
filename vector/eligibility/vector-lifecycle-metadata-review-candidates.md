# Vector Lifecycle Metadata Review Candidates

Generated: 2026-06-15T11:47:47.8224711+00:00
- SourceReportPath: `vector\eligibility\vector-lifecycle-metadata-repair-plan-summary.json`
- CandidateCount: `32`
- PendingCount: `32`
- CorrectlyBlockedSkippedCount: `18`
- Recommendation: `NeedsHumanReview`
- FormalRetrievalAllowed: `False`
- UseForRuntime: `False`

## Count By Status

- PendingReview: `32`

## Count By Layer

- context: `32`

## Count By ItemKind

- documentation: `10`
- guide: `8`
- character: `6`
- interface: `6`
- world-setting: `2`

## Recent Candidates

| CandidateId | EvalSet | Sample | MustHit | Layer | ItemKind | CurrentLifecycle | ProposedLifecycle | ProposedSection | Status | RiskIfApproved | RiskIfRejected |
|---|---|---|---|---|---|---|---|---|---|---|---|
| vlm-review-0c5bb0a503274ec3609d494d84285eb7 | Extended | novel-sample-001 | novel:character-linfeng | context | character | Unknown |  | diagnostics_only | PendingReview | SidecarWriteWouldChangeEligibilityOnlyAfterFutureApproval | RecallRemainsBlockedByLifecycleMetadata |
| vlm-review-0e1d6734228f7f5c6b4f3badce6f5e32 | Extended | project-sample-003 | doc:postgres-not-ready | context | documentation | Unknown |  | diagnostics_only | PendingReview | SidecarWriteWouldChangeEligibilityOnlyAfterFutureApproval | RecallRemainsBlockedByLifecycleMetadata |
| vlm-review-0facb5441007ce5bd1de1d398ca7f125 | Extended | automation-sample-001 | doc:automation-guide | context | guide | Unknown |  | diagnostics_only | PendingReview | SidecarWriteWouldChangeEligibilityOnlyAfterFutureApproval | RecallRemainsBlockedByLifecycleMetadata |
| vlm-review-11f4632683de159e98a5d8315c1d52f1 | A3 | project-sample-003 | doc:postgres-not-ready | context | documentation | Unknown |  | diagnostics_only | PendingReview | SidecarWriteWouldChangeEligibilityOnlyAfterFutureApproval | RecallRemainsBlockedByLifecycleMetadata |
| vlm-review-19a376f5a570e9a1f26de858c8af6a5f | Extended | automation-sample-007 | doc:automation-guide | context | guide | Unknown |  | diagnostics_only | PendingReview | SidecarWriteWouldChangeEligibilityOnlyAfterFutureApproval | RecallRemainsBlockedByLifecycleMetadata |
| vlm-review-2f60f571bcd15ab5f215d2424765bd82 | A3 | coding-sample-003 | doc:ipromotioncandidatestore | context | interface | Unknown |  | diagnostics_only | PendingReview | SidecarWriteWouldChangeEligibilityOnlyAfterFutureApproval | RecallRemainsBlockedByLifecycleMetadata |
| vlm-review-30b544655494109e210ee8840cc461ca | A3 | coding-sample-007 | doc:ipromotioncandidatestore | context | interface | Unknown |  | diagnostics_only | PendingReview | SidecarWriteWouldChangeEligibilityOnlyAfterFutureApproval | RecallRemainsBlockedByLifecycleMetadata |
| vlm-review-3a94e68cf281ad6f0064f246964b6fda | A3 | novel-sample-001 | novel:character-linfeng | context | character | Unknown |  | diagnostics_only | PendingReview | SidecarWriteWouldChangeEligibilityOnlyAfterFutureApproval | RecallRemainsBlockedByLifecycleMetadata |
| vlm-review-40511a0c85db267120cebe63cbb3d55b | Extended | novel-sample-010 | novel:character-linfeng | context | character | Unknown |  | diagnostics_only | PendingReview | SidecarWriteWouldChangeEligibilityOnlyAfterFutureApproval | RecallRemainsBlockedByLifecycleMetadata |
| vlm-review-41c978e2e9b551bcd6532d436d6fef92 | A3 | novel-sample-001 | novel:world-cangqiong | context | world-setting | Unknown |  | diagnostics_only | PendingReview | SidecarWriteWouldChangeEligibilityOnlyAfterFutureApproval | RecallRemainsBlockedByLifecycleMetadata |
| vlm-review-45029051f70faecb445f744b4aae8cd6 | Extended | novel-sample-007 | novel:character-linfeng | context | character | Unknown |  | diagnostics_only | PendingReview | SidecarWriteWouldChangeEligibilityOnlyAfterFutureApproval | RecallRemainsBlockedByLifecycleMetadata |
| vlm-review-49c97fa80466e7c7682489652f53f282 | Extended | coding-sample-003 | doc:ipromotioncandidatestore | context | interface | Unknown |  | diagnostics_only | PendingReview | SidecarWriteWouldChangeEligibilityOnlyAfterFutureApproval | RecallRemainsBlockedByLifecycleMetadata |
| vlm-review-5a97bb206e41afa92746c83f4c4010e6 | A3 | novel-sample-007 | novel:character-linfeng | context | character | Unknown |  | diagnostics_only | PendingReview | SidecarWriteWouldChangeEligibilityOnlyAfterFutureApproval | RecallRemainsBlockedByLifecycleMetadata |
| vlm-review-6b808adc6d174da99eb312b4f335bd41 | A3 | project-sample-003 | doc:local-alpha-runbook | context | documentation | Unknown |  | diagnostics_only | PendingReview | SidecarWriteWouldChangeEligibilityOnlyAfterFutureApproval | RecallRemainsBlockedByLifecycleMetadata |
| vlm-review-6e7b0c2b5ad29684e42bfc745f11f95b | A3 | automation-sample-007 | doc:automation-guide | context | guide | Unknown |  | diagnostics_only | PendingReview | SidecarWriteWouldChangeEligibilityOnlyAfterFutureApproval | RecallRemainsBlockedByLifecycleMetadata |
| vlm-review-7be8c131113f042f0641933844e88088 | A3 | coding-sample-001 | doc:ipromotioncandidatestore | context | interface | Unknown |  | diagnostics_only | PendingReview | SidecarWriteWouldChangeEligibilityOnlyAfterFutureApproval | RecallRemainsBlockedByLifecycleMetadata |
| vlm-review-7d05abce06e2e3c3252eed32057c6c5f | A3 | novel-sample-010 | novel:character-linfeng | context | character | Unknown |  | diagnostics_only | PendingReview | SidecarWriteWouldChangeEligibilityOnlyAfterFutureApproval | RecallRemainsBlockedByLifecycleMetadata |
| vlm-review-7f993eb0b165072d46eab67acbbc15c1 | Extended | project-sample-001 | doc:local-alpha-runbook | context | documentation | Unknown |  | diagnostics_only | PendingReview | SidecarWriteWouldChangeEligibilityOnlyAfterFutureApproval | RecallRemainsBlockedByLifecycleMetadata |
| vlm-review-817355978ec7a1420d11e233773b5707 | Extended | automation-sample-003 | doc:automation-guide | context | guide | Unknown |  | diagnostics_only | PendingReview | SidecarWriteWouldChangeEligibilityOnlyAfterFutureApproval | RecallRemainsBlockedByLifecycleMetadata |
| vlm-review-9010762a08f504911ea416d898879b02 | A3 | automation-sample-001 | doc:automation-guide | context | guide | Unknown |  | diagnostics_only | PendingReview | SidecarWriteWouldChangeEligibilityOnlyAfterFutureApproval | RecallRemainsBlockedByLifecycleMetadata |
