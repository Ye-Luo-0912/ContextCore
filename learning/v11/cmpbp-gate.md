# CMPBP (Gate)

生成: 2026-06-30T17:20:49.6550908+00:00 操作: cmpbp-699b3aa67f814e038a45eb1fe3f525f7

## Decision
- PackPassed: True GatePassed: True
- Canary: True Regression(Raw): 41 Regression(Calibrated): 0
- CalibratedScoresComparable: True CalibrationContractReady: True
- ShadowCoverage: 60/60 (100.0%)
- BackfillGateAuthority: True (realInference: 60, generated: 0)
- MetricMismatch(diagnostic): True (legacy, not blocking when calibrated)
- PromotionBoundary: True PilotPreflight: True
- PilotAuthorized: false PilotHold: true
- Next action: explicit pilot authorization required

V11.12 - pilot readiness bundle。Shadow canary passed, live pilot not yet authorized。
