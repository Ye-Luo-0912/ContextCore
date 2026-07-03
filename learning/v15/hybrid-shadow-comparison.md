# V15 Hybrid Shadow Comparison

Generated: 2026-07-03T11:58:48.5973647+00:00

## Deterministic vs Neural Score Comparison
- Spearman rank correlation: 0.7266
- Direction agreement: 68.97%
- Mean deterministic (normalized): 0.5830
- Mean neural selection: 0.4979

## Scoring Pipeline Status
- BlendAlpha: 1.0 (pure deterministic, V15 dry-run)
- Neural scores: shadow-only, not in runtime
- V14 foundation gate: PASSED

## Safety Gates
- PackageOutputChanged: false
- VectorBindingChanged: false
- RuntimePromotionApplied: false
- NeuralBiasActive: false
