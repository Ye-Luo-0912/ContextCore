# Vector Lifecycle Metadata Review Batch

- BatchId: `import-smoke`
- WorkspaceId: `__vector_review_batch_import_smoke__`
- CollectionId: `lifecycle-metadata-review-batch-import-smoke`
- CandidateCount: `10`
- Status: `Exported`
- Instructions: Synthetic import smoke batch. Do not use for real review.

| CandidateId | MustHitItemId | CurrentLifecycle | ProposedLifecycle | CurrentTargetSection | ProposedTargetSection | ReviewerDecision | Reviewer |
|---|---|---|---|---|---|---|---|
| `vlm-review-c065f33f72517ed5521502a39c43a0a6` | `batch-approve` | `Unknown` | `Active` | `excluded` | `audit_context` | `ApproveForSidecar` | `import-smoke-reviewer` |
| `vlm-review-2c5fc67a9bb091a0b414b17ec81fc1df` | `batch-reject` | `Unknown` | `Active` | `excluded` | `audit_context` | `Reject` | `import-smoke-reviewer` |
| `vlm-review-f7aeb6a00a7f3fa7befd67a7f1b770b6` | `batch-needs-evidence` | `Unknown` | `Active` | `excluded` | `audit_context` | `NeedsEvidence` | `import-smoke-reviewer` |
| `vlm-review-843e0ed9e8a3e24dc7178fb9fe50a31c` | `batch-supersede` | `Unknown` | `Active` | `excluded` | `audit_context` | `Supersede` | `import-smoke-reviewer` |
| `vlm-review-024b36309d9d44e8fb9700c6b9b1f0fb` | `batch-invalid-decision` | `Unknown` | `Active` | `excluded` | `audit_context` | `NotAValidDecision` | `import-smoke-reviewer` |
| `vlm-review-29ebcb461dd188c263c9bbe39b2d8dba` | `batch-missing-reviewer` | `Unknown` | `Active` | `excluded` | `audit_context` | `ApproveForSidecar` | `` |
| `vlm-review-53dfa94c5b79c95ad6288b3fd7d8de82` | `batch-missing-reason` | `Unknown` | `Active` | `excluded` | `audit_context` | `ApproveForSidecar` | `import-smoke-reviewer` |
| `vlm-review-a044237be69c32a0a8f0b4f0b8ce6ae9` | `batch-missing-evidence` | `Unknown` | `Active` | `excluded` | `audit_context` | `ApproveForSidecar` | `import-smoke-reviewer` |
| `vlm-review-f1c8bda76cd62349b5298ad5cde0ca65` | `batch-unsafe-normal` | `Deprecated` | `Active` | `excluded` | `normal_context` | `ApproveForSidecar` | `import-smoke-reviewer` |
| `vlm-review-26246872fc52e3d7427bfca1e02661fa` | `batch-duplicate` | `Unknown` | `Active` | `excluded` | `audit_context` | `Reject` | `import-smoke-reviewer` |
| `vlm-review-26246872fc52e3d7427bfca1e02661fa` | `batch-duplicate` | `Unknown` | `Active` | `excluded` | `audit_context` | `Reject` | `import-smoke-reviewer` |
