# CMPBP (Pilot)

生成: 2026-07-01T07:04:09.0952900+00:00 操作: cmpbp-3515d17b42e84232a7aabfdefca02073

## Decision
- PackPassed: True GatePassed: True
- Canary: True Regression(Raw): 41 Regression(Calibrated): 0
- CalibratedScoresComparable: True CalibrationContractReady: True
- ShadowCoverage: 60/60 (100.0%)
- BackfillGateAuthority: True (realInference: 60, generated: 0)
- MetricMismatch(diagnostic): True (legacy, not blocking when calibrated)
- PromotionBoundary: True PilotPreflight: True
- PilotAuthorized: True PilotExecuted: True Scope: demo-workspace/demo-collection
- GlobalDefaultOn: False RollbackReady: True PostPilotAudit: True
- RuntimePilotExecutionApplied: True

V11.16 - controlled pilot closeout freeze。Closed out, wider pilot requires explicit authorization。
