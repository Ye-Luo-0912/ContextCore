# CMPBP (Pilot)

生成: 2026-07-01T06:30:19.1859892+00:00 操作: cmpbp-e00627d900f6425c979c25c5faa325d8

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

V11.15 - post-pilot observation & closeout。Stable, no regressions, ready for closeout freeze。
