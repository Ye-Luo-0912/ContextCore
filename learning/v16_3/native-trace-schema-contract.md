# V16.3 Native Trace Schema Contract

Generated: 2026-07-03T15:00:59.267282+00:00 | SchemaVersion: V16.3-native-1.0

## Schema Origin

- **Source file**: `RuntimeCandidateTraceModels.cs` (src/ContextCore.Core/Services/Learning/V14_0/)
- **Collection point**: `BasicContextPackageBuilder.WriteTraceRow()` at line 3654
- **Collector class**: `RuntimeCandidateTraceSinkAccessor` -> `FileRuntimeCandidateTraceSink`
- **Definition**: Trace captured directly from the runtime candidate scoring pipeline, NOT cross-system mapped. `traceSource=PackageTrace(3)`.

## Required Fields (11 critical)

| # | Field | Type | Description |
|---|-------|------|-------------|
| 1 | `operationId` | string | Operation/scenario ID from `CurrentOperationId` |
| 2 | `requestId` | string | Correlated request ID from `CurrentRequestId` |
| 3 | `candidateId` | string | Unique candidate identifier from scoring pipeline |
| 4 | `sourceId` | string | Source identifier |
| 5 | `sourceType` | byte | 1=raw, 2=memory, 3=constraint, 4=global, 5=recent, 6=task, 7=related |
| 6 | `section` | string | Package section (current_task, hard_constraints, working_memory, ...) |
| 7 | `deterministicScore` | double | Legacy bounded-additive score (11-dimension) |
| 8 | `selectedByScoring` | bool | Whether scoring selected this candidate |
| 9 | `includedInPackage` | bool | Whether candidate made it into final package |
| 10 | `tokenCost` | double | Estimated token count |
| 11 | `recordedAt` | DateTimeOffset | ISO 8601 timestamp of recording |

## Full Schema (18 fields)

| Field | Type | Critical | Native Source |
|-------|------|----------|---------------|
| operationId | string | Yes | `RuntimeCandidateTraceSinkAccessor.CurrentOperationId` |
| requestId | string | No | `RuntimeCandidateTraceSinkAccessor.CurrentRequestId` |
| candidateId | string | Yes | `candidate.Id` |
| sourceId | string | No | `item.SourceId` |
| sourceType | byte | Yes | `item.SourceType` |
| authority | byte | Yes | Derived from sourceType/section |
| strategyType | byte | No | Derived from section |
| retrievalChannel | byte | Yes | `item.RetrievalChannel` |
| traceSource | byte | Yes | Hardcoded to `(byte)3` (PackageTrace) |
| deterministicScore | double | No | `c.Score` (11-dimension) |
| strategyScore | double | No | `c.Score` (forward-compat) |
| finalScore | double | No | `c.Score` (forward-compat) |
| selectedByScoring | bool | No | Parameter to `WriteTraceRow()` |
| includedInPackage | bool | No | Parameter to `WriteTraceRow()` |
| droppedReason | string | No | Set during package assembly |
| tokenCost | double | No | `c.TokenEstimate` |
| section | string | No | Section name from `BuildWithPolicyAsync()` |
| recordedAt | DateTimeOffset | No | `DateTimeOffset.UtcNow` |

## V16.2 vs V16.3 Trace Source

| Aspect | V16.2 (shadow-adapter) | V16.3 (native) |
|--------|----------------------|----------------|
| Trace source | `vector/trace/shadow-adapter/` | `BasicContextPackageBuilder.WriteTraceRow()` |
| Schema | Cross-system mapped | Native (18-field contract) |
| traceSource | Mapped to 1 | Native 3 (PackageTrace) |
| Scores | Derived from BaselineCount | Actual `c.Score` from pipeline |
| Selection | Derived from Allowlisted | Actual `selectedByScoring` flag |
| Coverage | 8 section categories | Full section diversity |

**Shadow-adapter mapped traces CANNOT impersonate native traces.** `traceSource=1 != 3`.

## Collection Pipeline

- Entry point: `BasicContextPackageBuilder.BuildDetailedAsync()`
- Write condition: `RuntimeCandidateTraceSinkAccessor.Current.Enabled == true`
- Format: JSONL (one JSON object per line)
- Default output: `learning/v14/runtime-candidate-trace.jsonl`
