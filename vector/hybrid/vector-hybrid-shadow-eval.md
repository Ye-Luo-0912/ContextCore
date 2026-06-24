# Hybrid Retrieval Preview Report

Generated: 2026-06-15T03:50:50.3301609+00:00
- OperationId: `hybrid-retrieval-preview-89887582f27745f99836590722084c05`
- Options: UseForRuntime=`False` MaxRiskAllowed=`0` DenseTopK=`10` LexicalTopK=`10` AnchorTopK=`10` UnionTopK=`10`
- Recommendation: `BlockedByA3Recall`

| Dataset | Variant | Samples | Dense | Lexical | Anchor | Union | Recall | MRR | Risk | MustNotHitRisk | LifecycleRisk | FormalChanged | RecDelta | RiskDelta | Recommendation |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| A3 | Dense | 50 | 500 | 0 | 0 | 500 | 4.55% | 0.0169 | 0 | 0 | 0 | 0 | 0.00% | 0 | BlockedByA3Recall |
| A3 | DenseLexical | 50 | 500 | 0 | 0 | 500 | 4.55% | 0.0169 | 0 | 0 | 0 | 0 | 0.00% | 0 | BlockedByA3Recall |
| A3 | DenseAnchor | 50 | 500 | 0 | 11 | 500 | 4.55% | 0.0169 | 0 | 0 | 0 | 0 | 0.00% | 0 | BlockedByA3Recall |
| A3 | DenseLexicalAnchor | 50 | 500 | 0 | 11 | 500 | 4.55% | 0.0169 | 0 | 0 | 0 | 0 | 0.00% | 0 | BlockedByA3Recall |
| Extended | Dense | 113 | 1130 | 0 | 0 | 1130 | 3.12% | 0.0107 | 0 | 0 | 0 | 0 | 0.00% | 0 | BlockedByA3Recall |
| Extended | DenseLexical | 113 | 1130 | 0 | 0 | 1130 | 3.12% | 0.0107 | 0 | 0 | 0 | 0 | 0.00% | 0 | BlockedByA3Recall |
| Extended | DenseAnchor | 113 | 1130 | 0 | 59 | 1130 | 3.12% | 0.0107 | 0 | 0 | 0 | 0 | 0.00% | 0 | BlockedByA3Recall |
| Extended | DenseLexicalAnchor | 113 | 1130 | 0 | 59 | 1130 | 3.12% | 0.0107 | 0 | 0 | 0 | 0 | 0.00% | 0 | BlockedByA3Recall |

## Contribution Breakdown
- DenseOnly: `6520`
- LexicalOnly: `0`
- AnchorOnly: `140`
- DenseAndLexical: `6520`
- DenseAndAnchor: `6660`
- LexicalAndAnchor: `140`
- AllThree: `6660`
