# Vector Lifecycle Metadata Review Batch Apply Preview

- BatchId: `import-smoke`
- CandidateCount: `10`
- DecisionCount: `11`
- WouldWriteSidecarEntryCount: `1`
- UnsafeBlockedCount: `1`
- Normal/Audit/Historical/Diagnostics: `0/1/0/0`
- EffectiveMetadataChangedCount: `1`
- RealSidecarWritten: `False`
- FormalRetrievalAllowed: `False`
- UseForRuntime: `False`
- Recommendation: `BlockedByUnsafeDecision`

## Diagnostics
- vlm-review-024b36309d9d44e8fb9700c6b9b1f0fb:UnknownDecision
- vlm-review-29ebcb461dd188c263c9bbe39b2d8dba:MissingReviewer
- vlm-review-53dfa94c5b79c95ad6288b3fd7d8de82:MissingReviewerReason
- vlm-review-a044237be69c32a0a8f0b4f0b8ce6ae9:MissingEvidenceOrSourceRefs
- vlm-review-f1c8bda76cd62349b5298ad5cde0ca65:UnsafeNormalContextApproval
- vlm-review-26246872fc52e3d7427bfca1e02661fa:DuplicateCandidateDecision
