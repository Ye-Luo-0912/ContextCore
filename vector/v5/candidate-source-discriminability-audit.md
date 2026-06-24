# Candidate Source Discriminability Audit

Generated: `2026-06-19T07:59:16.3078430+00:00`
OperationId: `vector-candidate-source-discriminability-audit-b28d23e75f6e4f679e27d88c5f025538`

## Protocol
- Version: `retrieval-eval-protocol-v1`
- VectorTopK: `5`  MergedTopK: `8`  FinalTopK: `5`
- ScoreThreshold: `0.0000`
- TieBreak: `score_desc_source_precedence_candidate_id_ordinal`
- Split: `train` / `holdout`

## Summary
- AuditPassed: `True`
- Recommendation: `NeedsInputMetadataEnrichment`
- Baseline recall/MRR: `0.4750` / `0.2101`
- Merged recall/MRR: `0.4750` / `0.2101`
- SourceNonDiscriminativeDetected: `False`  count: `1`
- TemplateHomogeneityScore: `0.2083`  duplicate signatures: `25` / `27`
- Risk/mustNot/lifecycle: `0` / `0` / `0`

## Source Contribution
| source | candidates | unique | unique recovery | recall | mrr | marginal recall | marginal mrr | overlap dense | overlap sources | non-discriminative |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| dense | 591 | 5 | 0 | 0.4750 | 0.2101 | 0.0000 | 0.0000 | 1.0000 | 0.9917 | False |
| lexical | 591 | 10 | 0 | 0.4750 | 0.2101 | 0.0000 | 0.0000 | 0.9833 | 0.9833 | True |
| anchor | 356 | 119 | 1 | 0.1500 | 0.0754 | 0.0000 | 0.0000 | 0.4758 | 0.6750 | False |
| evidence-source | 0 | 0 | 0 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | False |
| relation | 60 | 60 | 0 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | False |
| metadata | 560 | 247 | 1 | 0.2667 | 0.1181 | 0.0000 | 0.0000 | 0.5354 | 0.7346 | False |

## Split / Difficulty
| split | difficulty | samples | unique candidates | unique recovery | marginal recall | marginal mrr | source overlap |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| dev | ambiguous_target_section | 9 | 17 | 0 | 0.0000 | 0.0000 | 0.7116 |
| dev | near_duplicate_distractor | 9 | 45 | 0 | 0.0000 | 0.0000 | 0.4815 |
| holdout | ambiguous_target_section | 3 | 6 | 0 | 0.0000 | 0.0000 | 0.7143 |
| holdout | cross_domain_distractor | 3 | 5 | 0 | 0.0000 | 0.0000 | 0.8148 |
| holdout | direct_lexical | 2 | 0 | 0 | 0.0000 | 0.0000 | 1.0000 |
| holdout | lifecycle_deprecated_trap | 2 | 0 | 0 | 0.0000 | 0.0000 | 1.0000 |
| holdout | metadata_anchor | 2 | 0 | 0 | 0.0000 | 0.0000 | 1.0000 |
| holdout | must_not_negative_constraint | 2 | 2 | 0 | 0.0000 | 0.0000 | 0.8571 |
| holdout | near_duplicate_distractor | 3 | 15 | 0 | 0.0000 | 0.0000 | 0.5000 |
| holdout | paraphrase_semantic | 2 | 10 | 0 | 0.0000 | 0.0000 | 0.5000 |
| holdout | query_with_sparse_tokens | 3 | 15 | 0 | 0.0000 | 0.0000 | 0.5000 |
| holdout | relation_multi_hop | 2 | 10 | 0 | 0.0000 | 0.0000 | 0.5000 |
| test | cross_domain_distractor | 9 | 37 | 0 | 0.0000 | 0.0000 | 0.5496 |
| test | query_with_sparse_tokens | 9 | 45 | 0 | 0.0000 | 0.0000 | 0.4938 |
| train | direct_lexical | 10 | 0 | 0 | 0.0000 | 0.0000 | 1.0000 |
| train | lifecycle_deprecated_trap | 10 | 0 | 0 | 0.0000 | 0.0000 | 1.0000 |
| train | metadata_anchor | 10 | 0 | 0 | 0.0000 | 0.0000 | 1.0000 |
| train | must_not_negative_constraint | 10 | 10 | 0 | 0.0000 | 0.0000 | 0.8679 |
| train | paraphrase_semantic | 10 | 35 | 0 | 0.0000 | 0.0000 | 0.6500 |
| train | relation_multi_hop | 10 | 65 | 0 | 0.0000 | 0.0000 | 0.4404 |


## Blocked Reasons
- (empty)
