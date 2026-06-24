# Input Metadata Enrichment Preview

OperationId: `vector-input-metadata-enrichment-preview-cff71e0f834a49bb928377165257baac`
CreatedAt: `2026-06-19T08:33:00.8607688+00:00`

## Summary
- PreviewPassed: `True`
- GatePassed: `False`
- Recommendation: `ReadyForSourceRepairRecheck`
- Protocol: `retrieval-eval-protocol-v1` vector/merged/final topK `5/8/5`
- Metadata coverage delta: `1680`
- Recall before/after/delta: `0.4750` / `0.4750` / `0.0000`
- MRR before/after/delta: `0.2101` / `0.2101` / `0.0000`
- Holdout marginal recall before/after/delta: `0.0000` / `0.0000` / `0.0000`
- Independent non-dense source count: `3`
- Risk/mustNot/lifecycle: `0` / `0` / `0`
- Runtime/package invariants: formalPackage=`False`, packageOutput=`False`, packingPolicy=`False`, runtime=`False`, vectorBinding=`False`

## Coverage
### Before
- CoverageScore: `720`
- Source/Evidence/Provenance/Fingerprint: `120` / `120` / `120` / `120`
- Relation/Lifecycle/Canonical/Query anchors: `120` / `120` / `0` / `0`
### After
- CoverageScore: `2400`
- Source/Evidence/Provenance/Fingerprint: `120` / `120` / `120` / `120`
- Relation/Lifecycle/Canonical/Query anchors: `120` / `120` / `1560` / `120`

## Source Contribution After Enrichment
| Source | Candidate | Unique | Unique must-hit recovery | Marginal recall | Marginal MRR | Dense overlap | Non-discriminative |
|---|---:|---:|---:|---:|---:|---:|---|
| `anchor` | 356 | 119 | 1 | 0.0000 | 0.0000 | 0.4758 | False |
| `dense` | 591 | 5 | 0 | 0.0000 | 0.0000 | 1.0000 | False |
| `evidence-source` | 0 | 0 | 0 | 0.0000 | 0.0000 | 0.0000 | False |
| `lexical` | 591 | 10 | 0 | 0.0000 | 0.0000 | 0.9833 | True |
| `metadata` | 560 | 247 | 1 | 0.0000 | 0.0000 | 0.5354 | False |
| `relation` | 60 | 60 | 0 | 0.0000 | 0.0000 | 0.0000 | False |

## Blocked Reasons
- (none)
