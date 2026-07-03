# V16.3: Native Runtime Trace Readiness & Collector Preview

Generated: 2026-07-03T15:00:59.267282+00:00

## Core Gates
- V14GateReady: True
- V16_2GatePreserved: True (guarded_candidate_below_threshold)
- NativeProductionTraceReady: False
- NativeTraceCollectorReady: True
- ShadowAdapterFallbackReady: True
- CrossSystemMapping (V16.3): False

## Collector Mode
- CollectorMode: NativeRuntimeCandidateTracePreview
- TraceCaptureOnly: True
- RuntimeInfluenceAllowed: False
- NeuralBiasActive: False
- PackageOutputChanged: False
- VectorBindingChanged: False

## Collector Infrastructure
All collection components are implemented and ready:
- **Trace Sink**: `IRuntimeCandidateTraceSink` -> `FileRuntimeCandidateTraceSink` / `NullRuntimeCandidateTraceSink`
- **Trace Models**: `RuntimeCandidateTraceModels.cs` (18 fields, 6 critical)
- **Validation**: `RuntimeCandidateTraceContractValidator.cs`
- **Capture Point**: `BasicContextPackageBuilder.WriteTraceRow()` at line 3654
- **Wiring Point**: `RuntimeCandidateTraceSinkAccessor.Current`

## V16.2 vs V16.3 Trace Source
| Aspect | V16.2 (shadow-adapter) | V16.3 (native) |
|--------|----------------------|----------------|
| Trace source | vector/trace/shadow-adapter/ | BasicContextPackageBuilder.WriteTraceRow() |
| Schema | Cross-system mapped | Native (18-field contract) |
| traceSource | Mapped to 1 | Native 3 (PackageTrace) |
| Scores | Derived from BaselineCount | Actual c.Score from pipeline |
| Selection | Derived from Allowlisted | Actual selectedByScoring flag |
| Coverage | 8 section categories | Full section diversity |

## Native Trace Schema
Full contract in: `native-trace-schema-contract.json`

18 fields: operationId, requestId, candidateId, sourceId, sourceType, authority, strategyType, retrievalChannel, traceSource, deterministicScore, strategyScore, finalScore, selectedByScoring, includedInPackage, droppedReason, tokenCost, section, recordedAt

## Activation Checklist
1. Create FileRuntimeCandidateTraceSink with output path
2. Set RuntimeCandidateTraceSinkAccessor.Current to file sink
3. Set CurrentOperationId (unique per run)
4. Execute BuildDetailedAsync() against target
5. Read JSONL output
6. Re-run V16.2 evaluation on native traces

## Safety: All Gates
- PackageOutputChanged: false
- RuntimePromotionApplied: false
- VectorBindingChanged: false
- RuntimeInfluenceAllowed: false
- ProductionGeneralizationReady: false (requires native trace collection + metric quality)
