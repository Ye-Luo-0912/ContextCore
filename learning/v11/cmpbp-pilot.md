# CMPBP (Pilot)

生成: 2026-07-01T08:16:43.9126555+00:00 操作: cmpbp-2ec903935c9e4e78bb1e8a2550e24888

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

V11.18 - closeout archive & wider-pilot authorization gate。Archived, wider pilot requires explicit gate pass。
