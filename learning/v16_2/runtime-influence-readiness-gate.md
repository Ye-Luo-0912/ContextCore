# V16.2 Runtime-Influence Readiness Gate (Repair B)
Generated: 2026-07-03T14:29:43.913698+00:00

## Core Gates
- V16_2ProductionTraceShadowReady: True
- V16_2MetricIntegrityReady: True
- Alpha1InvariantPassed: True
- RowKeyUniqueness: True

## Trace Provenance Boundary
- NativeProductionTraceReady: False
- NativeProductionTrace: False
- RepositoryRealisticShadowAdapterReady: True
- ShadowAdapterSchemaMapped: True
- CrossSystemMapping: True

## Metric-Quality Gate
- ProductionLikeWeightedPairwiseAcc: 0.5451 (threshold: 0.55)
- PairwiseAboveThreshold: False
- ProductionLikeCalibrationUseful: False
- MetricQualityBlocked: True
- Reason: ProductionLikeWeightedPairwiseAcc=0.5451 < threshold=0.55. Cross-system mapped calibration signal is too weak to substantiate readiness. Native production candidate-scoring traces are required for meaningful calibration.

## Split Metric Stability
- SplitMetricStabilityScore: 0.3333
- SplitStable: False
- Smoke MeanRankΔ (non-α1): [0.0, 0.0, 0.0606]
- Prod MeanRankΔ (non-α1): [4.6212, 4.6212, 4.6212]

## Readiness
- RuntimeInfluenceAllowed: False
- RuntimeInfluenceReadinessCandidate: guarded_candidate_below_threshold
- ProductionGeneralizationReady: False
- InsufficientRealTraceData: False

## Safety
- PackageOutputChanged: False
- RuntimePromotionApplied: False
- VectorBindingChanged: False
- V14GatePreserved: True

## Collector
- CollectorMode: PythonReproducibleEvaluator
