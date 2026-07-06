# V16.16 Native Production Trace Execution Endpoint Implementation Plan

Generated: 2026-07-06T13:50:00.0000000+00:00

## Purpose

Endpoint implementation plan only — no actual implementation code. No production trace collected. No FileRuntimeCandidateTraceSink wired. No BuildDetailedAsync called.

## Plan Status

| Field | Value |
|---|---|
| EndpointImplementationPlanReady | **true** |
| EndpointImplementationAllowed | **false** |
| EndpointImplemented | **false** |

## Target Files

- `EvalCommand.VectorV8.cs` — New method `ExecuteV16_16NativeProductionTraceExecutionEndpointAsync`
- Authorization validation: `ValidateAllSevenAuthorizationFactors`

## CLI Shape

```
eval v16_16-native-production-trace-execution-endpoint
  --confirm-live-capture
  --capture-token <token>
  --workspaceId <real>
  --collectionId <real>
  --runId <unique>
  [--dry-run]
```

## Guard Order (7 guards)

1. confirmLiveCapture → block if missing
2. captureToken → block if missing
3. workspaceId/collectionId → block if missing
4. synthetic rejection → block if synthetic
5. runId → block if missing
6. RejectExistingRunId → block if file exists
7. safety invariants → hard abort if violated

## Sink Lifecycle (10 steps)

Wire-up (steps 2-5) → Execute (step 6) → Flush/Dispose (steps 7-8) → Restore NullSink (steps 9-10)

## Failure Rollback

- BuildError: dispose, restore, delete partial, log
- Idempotency: return immediately (no sink created)
- Validation: dispose, restore, delete, mark INVALID

## Test Plan (7 unit tests)

Authorization factors, synthetic rejection, idempotency, sink lifecycle, guard-gated BuildDetailedAsync, blocked path, runtime influence invariant.

## Gates

| Gate | Value |
|---|---|
| EndpointImplementationPlanReady | true |
| EndpointImplementationAllowed | false |
| EndpointImplemented | false |
| RuntimeInfluenceAllowed | false (permanent) |
| PackageOutputChanged | false |
| VectorBindingChanged | false |
