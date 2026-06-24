# Retrieval Dataset V2 Generation Report

- CorpusItemCount: `28`
- SampleCount: `21`
- ValidationIssueCount: `0`
- MissingEvidenceCount: `0`
- MissingProvenanceCount: `0`
- MustHitMissingCount: `0`
- MustNotOverlapCount: `0`
- ItemIdLeakageCount: `0`
- RelationInconsistencyCount: `0`
- JudgeWarningCount: `0`
- FormalRetrievalAllowed: `False`
- UseForRuntime: `False`
- Recommendation: `ReadyForDatasetV2ShadowEval`

## Difficulty Breakdown
| Key | Count |
|---|---:|
| ambiguous_query_requiring_target_section | 3 |
| direct_lexical | 3 |
| lifecycle_deprecated_trap | 3 |
| metadata_anchor | 3 |
| must_not_negative_constraint | 3 |
| paraphrase_semantic | 3 |
| relation_multi_hop | 3 |

## Split Breakdown
| Key | Count |
|---|---:|
| dev | 4 |
| test | 4 |
| train | 13 |

## Prompt Templates
- Generate corpus items with sourceRefs, evidenceRefs, provenance, lifecycle, reviewStatus, replacementState, targetSection, relations, tags, anchors, and content.
- Generate retrieval samples whose queryText never contains itemId values; rationale must not be indexed text.
- Choose mustHit and mustNot only from the generated corpus and explain why the positive is correct and the negative is wrong.
- Lifecycle traps require relation evidence and must route deprecated or superseded items away from normal_context.
