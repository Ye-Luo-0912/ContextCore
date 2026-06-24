# ContextCore Foundation Reproducibility Check

Generated: `2026-06-17T03:12:03.4834086+00:00`
OperationId: `foundation-reproducibility-7658913645fe4dbfa85adb18017d4d09`

## Summary

- ReproducibilityPassed: `True`
- Recommendation: `ReadyForReleaseCandidateReproduction`
- FoundationGateStatus: `Passed`
- RuntimeChangeGateStatus: `Passed`
- P15GateStatus: `Passed`
- LocalSecretsDetected: `False`
- LocalSecretPathCount: `0`

## Commands

- Build: `dotnet build`
- Test: `dotnet test`
- P15: `scripts/eval-gate-p15.ps1`
- Runtime change gate: `dotnet run --project src/ContextCore.ControlRoom -- eval learning-runtime-change-readiness-gate`
- Foundation gate: `dotnet run --project src/ContextCore.ControlRoom -- eval foundation-release-candidate-gate`
- Reproducibility check: `dotnet run --project src/ContextCore.ControlRoom -- eval foundation-reproducibility-check`

## Expected Output

Build/test/P15/runtime-change/foundation gates pass; runtime switch and formal retrieval remain disabled; no critical report or secret leakage.

## Critical Report Coverage
- docs\ContextCore_Foundation_Freeze_Report.md: `True`
- eval\eval-report-p15-a3.json: `True`
- eval\eval-report-p15-extended.json: `True`
- foundation\foundation-release-candidate-gate.json: `True`
- foundation\foundation-release-candidate-gate.md: `True`
- learning\readiness\learning-runtime-change-readiness-gate.json: `True`
- learning\readiness\learning-runtime-change-readiness-gate.md: `True`
- vector\v4\vector-formal-preview-freeze-gate.md: `True`

## Boundary Checks
- FormalRetrievalAllowedFalse: `True`
- FoundationReleaseCandidateGatePassed: `True`
- P15GatePassed: `True`
- PackageOutputChangedFalse: `True`
- PackingPolicyChangedFalse: `True`
- ReadyForRuntimeSwitchFalse: `True`
- RuntimeChangeGatePassed: `True`
- RuntimeSwitchAllowedFalse: `True`
- UseForRuntimeFalseWhereApplicable: `True`

## Git Status Categories
- docs: `5`
  - `docs/ContextCore_Foundation_Freeze_Report.md`
  - `docs/ContextCore_\346\226\260\351\230\266\346\256\265\346\211\247\350\241\214\346\212\245\345\221\212.md`
  - `docs/controlroom-service-mode.md`
  - `docs/vector-index-foundation.md`
  - `docs/vector-preview-shadow-freeze.md`
- generated reports: `17`
  - `eval/eval-report-latest.json`
  - `eval/eval-report-p15-a3.json`
  - `eval/eval-report-p15-extended.json`
  - `eval/extended-failure-triage-report.json`
  - `eval/extended-failure-triage-report.md`
  - `foundation/foundation-release-candidate-gate.json`
  - `foundation/foundation-release-candidate-gate.md`
  - `learning/readiness/learning-readiness-freeze-report.json`
  - `learning/readiness/learning-readiness-freeze-report.md`
  - `learning/readiness/learning-runtime-change-readiness-gate.json`
  - `learning/readiness/learning-runtime-change-readiness-gate.md`
  - `vector/v4/vector-scoped-runtime-experiment-config-preview.json`
  - `vector/v4/vector-scoped-runtime-experiment-config-preview.md`
  - `vector/v4/vector-scoped-runtime-experiment-proposal-gate.json`
  - `vector/v4/vector-scoped-runtime-experiment-proposal-gate.md`
  - `vector/v4/vector-scoped-runtime-experiment-proposal.json`
  - `vector/v4/vector-scoped-runtime-experiment-proposal.md`
- local config / secrets: `0`
- model files: `0`
- other: `0`
- source code: `5`
  - `src/ContextCore.Abstractions/Models/VectorIndexDtos.cs`
  - `src/ContextCore.ControlRoom/Commands/EvalCommand.cs`
  - `src/ContextCore.ControlRoom/Rendering/ServiceOperationalRenderer.cs`
  - `src/ContextCore.ControlRoom/Services/ControlRoomService.cs`
  - `src/ContextCore.Core/Services/Vector/ExplicitScopedRuntimeExperimentProposalRunner.cs`
- temporary files: `0`
- tests: `1`
  - `tests/ContextCore.Tests/ContextCoreRetrievalDatasetV2MetadataContractTests.cs`

## Blocked Reasons
- (empty)

## Runtime Boundary

- 该检查不接 formal retrieval，不切 runtime，不绑定正式 `IVectorIndexStore`。
- release candidate 通过也不允许 `PackingPolicy` 或 package output mutation。
