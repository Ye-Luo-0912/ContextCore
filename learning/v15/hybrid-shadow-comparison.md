# V15 Hybrid Shadow Comparison

Generated: 2026-07-03T06:04:29.5152765+00:00

## Deterministic vs Neural Score Comparison
- Spearman rank correlation: 0.8327
- Direction agreement: 66.67%
- Mean deterministic (normalized): 0.7063
- Mean neural selection: 0.4992

## Scoring Pipeline Status
- BlendAlpha: 1.0 (pure deterministic, V15 dry-run)
- Neural scores: shadow-only, not in runtime
- V14 foundation gate: PASSED

## Safety Gates
- PackageOutputChanged: false
- VectorBindingChanged: false
- RuntimePromotionApplied: false
- NeuralBiasActive: false
