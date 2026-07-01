# CMPBP (Pilot)

生成: 2026-07-01T02:52:57.6743849+00:00 操作: cmpbp-fba241921a6e422a96919a7c7916535f

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

V11.14 - post-pilot operational hardening bundle。All audits pass, scope intact。
