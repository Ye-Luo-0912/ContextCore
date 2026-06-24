# Vector Lifecycle Metadata Repair Plan Summary

Generated: 2026-06-15T09:43:23.9278306+00:00
- Recommendation: `NeedsHumanReview`
- CandidateCount: `32`
- AutoRepairableCount: `0`
- HumanReviewRequiredCount: `32`
- ForbiddenRepairCount: `0`
- CorrectlyBlockedSkippedCount: `18`
- EstimatedRecallRecovery: `0.00`
- RiskAfterRepairEstimate: `0`
- FormalRetrievalAllowed: `False`
- UseForRuntime: `False`

| Dataset | Candidates | Auto | HumanReview | Forbidden | CorrectlyBlockedSkipped | EstimatedRecallRecovery | RiskEstimate | Recommendation |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| A3 | 16 | 0 | 16 | 0 | 9 | 0.00 | 0 | NeedsHumanReview |
| Extended | 16 | 0 | 16 | 0 | 9 | 0.00 | 0 | NeedsHumanReview |

## A3

- ProviderId: `deterministic-hash`
- EmbeddingModel: `deterministic-hash-v1`
- Dimension: `16`
- CandidateCount: `16`
- AutoRepairableCount: `0`
- HumanReviewRequiredCount: `16`
- ForbiddenRepairCount: `0`
- CorrectlyBlockedSkippedCount: `9`
- EstimatedRecallRecovery: `0.00`
- RiskAfterRepairEstimate: `0`
- Recommendation: `NeedsHumanReview`
- FormalRetrievalAllowed: `False`
- UseForRuntime: `False`

| Sample | MustHit | CurrentLifecycle | ProposedLifecycle | CurrentReview | ProposedReview | CurrentSection | ProposedSection | Provenance | RelationEvidence | ReviewEvidence | Confidence | Auto | HumanReview | ForbiddenReason | RepairReason |
|---|---|---|---|---|---|---|---|---:|---:|---:|---:|---:|---:|---|---|
| automation-sample-001 | doc:automation-guide | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| automation-sample-003 | doc:automation-guide | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| automation-sample-007 | doc:automation-guide | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| automation-sample-010 | doc:automation-guide | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| coding-sample-001 | doc:ipromotioncandidatestore | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| coding-sample-003 | doc:ipromotioncandidatestore | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| coding-sample-007 | doc:ipromotioncandidatestore | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| novel-sample-001 | novel:character-linfeng | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| novel-sample-001 | novel:world-cangqiong | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| novel-sample-007 | novel:character-linfeng | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| novel-sample-010 | novel:character-linfeng | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| project-sample-001 | doc:local-alpha-runbook | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| project-sample-002 | doc:postgres-not-ready | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| project-sample-003 | doc:local-alpha-runbook | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| project-sample-003 | doc:postgres-not-ready | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| project-sample-010 | doc:local-alpha-runbook | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |

## Extended

- ProviderId: `deterministic-hash`
- EmbeddingModel: `deterministic-hash-v1`
- Dimension: `16`
- CandidateCount: `16`
- AutoRepairableCount: `0`
- HumanReviewRequiredCount: `16`
- ForbiddenRepairCount: `0`
- CorrectlyBlockedSkippedCount: `9`
- EstimatedRecallRecovery: `0.00`
- RiskAfterRepairEstimate: `0`
- Recommendation: `NeedsHumanReview`
- FormalRetrievalAllowed: `False`
- UseForRuntime: `False`

| Sample | MustHit | CurrentLifecycle | ProposedLifecycle | CurrentReview | ProposedReview | CurrentSection | ProposedSection | Provenance | RelationEvidence | ReviewEvidence | Confidence | Auto | HumanReview | ForbiddenReason | RepairReason |
|---|---|---|---|---|---|---|---|---:|---:|---:|---:|---:|---:|---|---|
| automation-sample-001 | doc:automation-guide | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| automation-sample-003 | doc:automation-guide | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| automation-sample-007 | doc:automation-guide | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| automation-sample-010 | doc:automation-guide | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| coding-sample-001 | doc:ipromotioncandidatestore | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| coding-sample-003 | doc:ipromotioncandidatestore | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| coding-sample-007 | doc:ipromotioncandidatestore | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| novel-sample-001 | novel:character-linfeng | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| novel-sample-001 | novel:world-cangqiong | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| novel-sample-007 | novel:character-linfeng | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| novel-sample-010 | novel:character-linfeng | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| project-sample-001 | doc:local-alpha-runbook | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| project-sample-002 | doc:postgres-not-ready | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| project-sample-003 | doc:local-alpha-runbook | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| project-sample-003 | doc:postgres-not-ready | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
| project-sample-010 | doc:local-alpha-runbook | Unknown |  |  |  | excluded | diagnostics_only | False | False | False | 0.00 | False | True | MissingProvenance | 缺少 sourceRef / provenance / source fingerprint 等运行时 provenance。 |
