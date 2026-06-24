# Vector Lifecycle Metadata Review Batch Validation

- BatchId: `import-smoke`
- CandidateCount: `10`
- RowCount: `11`
- DecisionCount: `11`
- ApprovalCount: `5`
- RejectCount: `2`
- NeedsEvidenceCount: `1`
- SupersedeCount: `1`
- ValidationErrorCount: `6`
- UnsafeDecisionCount: `1`
- MissingEvidenceCount: `1`
- MissingReviewerCount: `1`
- MissingReviewerReasonCount: `1`
- LastWriteWins: `False`
- Recommendation: `BlockedByUnsafeDecision`
- FormalRetrievalAllowed: `False`
- UseForRuntime: `False`

## Issues
- `vlm-review-024b36309d9d44e8fb9700c6b9b1f0fb` UnknownDecision: Unknown reviewer decision: NotAValidDecision
- `vlm-review-29ebcb461dd188c263c9bbe39b2d8dba` MissingReviewer: ApproveForSidecar requires reviewer.
- `vlm-review-53dfa94c5b79c95ad6288b3fd7d8de82` MissingReviewerReason: ApproveForSidecar requires reviewer reason.
- `vlm-review-a044237be69c32a0a8f0b4f0b8ce6ae9` MissingEvidenceOrSourceRefs: ApproveForSidecar requires evidenceRefs or sourceRefs.
- `vlm-review-f1c8bda76cd62349b5298ad5cde0ca65` UnsafeNormalContextApproval: Deprecated, historical, superseded, rejected, or non-active lifecycle cannot approve into normal_context.
- `vlm-review-26246872fc52e3d7427bfca1e02661fa` DuplicateCandidateDecision: Duplicate candidate decisions are not allowed; last-write-wins is disabled.
