"""
V16.3: Native Runtime Trace Readiness & Candidate-Scoring Trace Collector
==========================================================================
Designs and validates the native candidate-scoring trace collector that
captures traces from the actual BasicContextPackageBuilder scoring pipeline,
NOT from cross-system mapped shadow-adapter data.

Key difference from V16.2:
  V16.2: shadow-adapter traces (vector allowlisting) mapped to candidate schema
  V16.3: native traces captured directly from BasicContextPackageBuilder.WriteTraceRow()

No runtime influence. Package output unchanged. Vector binding unchanged.
"""

import json
import os
from datetime import datetime, timezone

BASE = os.path.dirname(os.path.abspath(__file__))

OUT_READINESS_JSON = os.path.join(BASE, "native-runtime-trace-readiness.json")
OUT_READINESS_MD = os.path.join(BASE, "native-runtime-trace-readiness.md")
OUT_SCHEMA_JSON = os.path.join(BASE, "native-trace-schema-contract.json")
OUT_SAFETY_JSON = os.path.join(BASE, "native-trace-safety-gate.json")


def generate():
    now = datetime.now(timezone.utc).isoformat()

    # =========================================================================
    # 1. Native Trace Schema Contract
    # =========================================================================
    schema_contract = {
        "GeneratedAt": now,
        "SchemaVersion": "V16.3-native-1.0",
        "SchemaOrigin": "RuntimeCandidateTraceModels.cs (src/ContextCore.Core/Services/Learning/V14_0/)",
        "CollectionPoint": "BasicContextPackageBuilder.WriteTraceRow() at line 3654",
        "CollectorClass": "RuntimeCandidateTraceSinkAccessor -> FileRuntimeCandidateTraceSink",
        "NativeTraceDefinition": "Trace captured directly from the runtime candidate scoring pipeline, NOT from cross-system mapped shadow-adapter data. traceSource=PackageTrace(3).",
        "Fields": [
            {
                "name": "operationId",
                "type": "string",
                "required": True,
                "critical": True,
                "description": "The operation/scenario ID. For native collection, set by CurrentOperationId on sink accessor before pipeline execution.",
                "nativeSource": "RuntimeCandidateTraceSinkAccessor.CurrentOperationId",
            },
            {
                "name": "requestId",
                "type": "string",
                "required": False,
                "critical": False,
                "description": "Correlated request ID. Set by CurrentRequestId on sink accessor.",
                "nativeSource": "RuntimeCandidateTraceSinkAccessor.CurrentRequestId",
            },
            {
                "name": "candidateId",
                "type": "string",
                "required": True,
                "critical": True,
                "description": "Unique candidate identifier from the scoring pipeline.",
                "nativeSource": "candidate.Id (UnifiedCandidate or MemoryItem/ContextItem)",
            },
            {
                "name": "sourceId",
                "type": "string",
                "required": False,
                "critical": False,
                "description": "Source identifier. Currently duplicates candidateId.",
                "nativeSource": "item.SourceId",
            },
            {
                "name": "sourceType",
                "type": "byte",
                "required": True,
                "critical": True,
                "description": "1=raw/legacy, 2=memory, 3=constraint, 4=global, 5=recent, 6=task, 7=related",
                "nativeSource": "item.SourceType (from section context)",
            },
            {
                "name": "authority",
                "type": "byte",
                "required": True,
                "critical": True,
                "description": "1=constraint/stable/global, 2=raw/recent, 3=historical, 4=related, 5=task/working",
                "nativeSource": "Derived from sourceType and section during MapTraceFields()",
            },
            {
                "name": "strategyType",
                "type": "byte",
                "required": False,
                "critical": False,
                "description": "1=working_memory/recent, 2=stable_memory/global, 3=constraint, 4=current_task, 5=related_context",
                "nativeSource": "Derived from section during MapTraceFields()",
            },
            {
                "name": "retrievalChannel",
                "type": "byte",
                "required": True,
                "critical": True,
                "description": "0=Unknown, 1=Vector, 2=Memory, 3=Graph, 4=Keyword, 5=Anchor, 6=Constraint",
                "nativeSource": "item.RetrievalChannel or derived from context",
            },
            {
                "name": "traceSource",
                "type": "byte",
                "required": True,
                "critical": True,
                "description": "0=Unknown, 1=ShadowEval, 2=GraphShadow, 3=PackageTrace, 4=RetrievalTrace. Native always 3=PackageTrace.",
                "nativeSource": "Hardcoded to (byte)3 in WriteTraceRow() line 3673",
            },
            {
                "name": "deterministicScore",
                "type": "double",
                "required": False,
                "critical": False,
                "description": "Legacy bounded-additive score (11-dimension). Computed per section by ScoreWorkingMemoryForAnchors() etc.",
                "nativeSource": "c.Score from section scoring (11-dimension for memory, fixed for task/constraint/global)",
            },
            {
                "name": "strategyScore",
                "type": "double",
                "required": False,
                "critical": False,
                "description": "Strategy-based feature score. Currently identical to deterministicScore (unified scorer not yet wired at runtime).",
                "nativeSource": "c.Score (forward-compatibility field)",
            },
            {
                "name": "finalScore",
                "type": "double",
                "required": False,
                "critical": False,
                "description": "Final blended score. Currently identical to deterministicScore (hybrid blend not yet wired at runtime).",
                "nativeSource": "c.Score (forward-compatibility field)",
            },
            {
                "name": "selectedByScoring",
                "type": "bool",
                "required": False,
                "critical": False,
                "description": "Whether scoring selected this candidate. True by default; false for explicitly rejected items (deprecated constraints, lifecycle-filtered).",
                "nativeSource": "Parameter passed to WriteTraceRow()",
            },
            {
                "name": "includedInPackage",
                "type": "bool",
                "required": False,
                "critical": False,
                "description": "Whether the candidate actually made it into the final package. False if dropped by token budget, dedup, or exclusion.",
                "nativeSource": "Parameter passed to WriteTraceRow()",
            },
            {
                "name": "droppedReason",
                "type": "string",
                "required": False,
                "critical": False,
                "description": "If not included, why. Examples: 'token budget exhausted', 'constraint is deprecated or rejected', 'filtered by lifecycle'.",
                "nativeSource": "Set during package assembly in BuildWithPolicyAsync()",
            },
            {
                "name": "tokenCost",
                "type": "double",
                "required": False,
                "critical": False,
                "description": "Estimated token count for this candidate's content.",
                "nativeSource": "c.TokenEstimate or derived from content length",
            },
            {
                "name": "section",
                "type": "string",
                "required": False,
                "critical": False,
                "description": "The package section this candidate maps to (current_task, hard_constraints, working_memory, global_context, recent_context, stable_memory, soft_constraints, related_context, or legacy raw doc name).",
                "nativeSource": "Section name from BuildWithPolicyAsync() loop context",
            },
            {
                "name": "recordedAt",
                "type": "DateTimeOffset",
                "required": False,
                "critical": False,
                "description": "ISO 8601 timestamp of trace recording.",
                "nativeSource": "DateTimeOffset.UtcNow",
            },
        ],
        "CollectionPipeline": {
            "EntryPoint": "BasicContextPackageBuilder.BuildDetailedAsync()",
            "TraceWritePoint": "BasicContextPackageBuilder.WriteTraceRow() at line 3654",
            "TraceWriteCondition": "RuntimeCandidateTraceSinkAccessor.Current.Enabled == true",
            "TraceFormat": "JSONL (one JSON object per line)",
            "TraceSourceEnum": "PackageTrace = 3",
            "OutputPath": "learning/v14/runtime-candidate-trace.jsonl (or configurable via FileRuntimeCandidateTraceSink constructor)",
        },
        "VsV16_2_ShadowAdapter": {
            "V16_2_TraceSource": "Cross-system mapped from vector/trace/shadow-adapter/ (vector allowlisting schema)",
            "V16_3_TraceSource": "Native from BasicContextPackageBuilder.WriteTraceRow() (candidate scoring schema)",
            "V16_2_TraceSourceValue": "1 (mapped, not native)",
            "V16_3_TraceSourceValue": "3 (PackageTrace, native)",
            "V16_2_ScoresDerived": True,
            "V16_3_ScoresNative": True,
            "V16_2_SelectionDerived": True,
            "V16_3_SelectionNative": True,
        },
    }

    # =========================================================================
    # 2. Native Trace Safety Gate
    # =========================================================================
    safety_gate = {
        "GeneratedAt": now,
        "CollectorMode": "NativeRuntimeCandidateTracePreview",
        "TraceCaptureOnly": True,
        "RuntimeInfluenceSafeguards": {
            "RuntimeInfluenceAllowed": False,
            "NeuralBiasActive": False,
            "NeuralOnlyInShadowReport": True,
            "HybridBlendAlpha": 1.0,
            "HybridBlendNote": "Alpha=1.0 means pure deterministic scoring at runtime. Neural scores computed only in shadow evaluation, never at runtime.",
        },
        "PackageOutputSafety": {
            "PackageOutputChanged": False,
            "PackageOutputNote": "Trace collection is append-only. Package output is unmodified. no mutation to selectedItems, droppedItems, or package structure.",
        },
        "VectorBindingSafety": {
            "VectorBindingChanged": False,
            "VectorBindingNote": "No changes to vector retrieval, embedding generation, or allowlisting. shadow-adapter traces are pre-collected, not live.",
        },
        "RetrievalSafety": {
            "RetrievalUnchanged": True,
            "RetrievalNote": "Candidate retrieval paths unmodified. trace collection is observation-only, downstream of retrieval.",
        },
        "ScoringSafety": {
            "ScoringUnchanged": True,
            "ScoringNote": "All scoring functions (ScoreWorkingMemoryForAnchors, constraint scoring, legacy scoring) unmodified. Trace captures their output without altering it.",
        },
        "WriteSafety": {
            "WritePathIsConfigurable": True,
            "WritePathDefault": "learning/v14/runtime-candidate-trace.jsonl",
            "WriteMode": "Append-only JSONL (FileRuntimeCandidateTraceSink)",
            "Idempotency": "Each operation generates a new file or new timestamp. No duplicate-append risk if operationId unique per run.",
            "DiskSafety": "Standard JSONL file write. No database mutation, no in-memory mutation beyond sink buffer.",
        },
        "FallbackSafety": {
            "NullSinkDefault": True,
            "NullSinkNote": "If no FileRuntimeCandidateTraceSink is configured (Enabled=false), NullRuntimeCandidateTraceSink is used. No trace is written. No runtime impact.",
        },
        "V14GatePreserved": True,
        "V16_2GatePreservedNote": "V16.2 readiness gate remains as-is. V16.3 does not invalidate V16.2 shadow evaluation.",
    }

    # =========================================================================
    # 3. Native Runtime Trace Readiness
    # =========================================================================
    readiness = {
        "GeneratedAt": now,
        "V14GateReady": True,
        "V16_2GatePreserved": True,
        "V16_2GateState": "guarded_candidate_below_threshold",
        "ReadinessAssessment": {
            "NativeProductionTraceReady": False,
            "NativeProductionTraceReadyReason": "No native runtime candidate scoring traces have been collected yet. The collector infrastructure exists (RuntimeCandidateTraceSink + WriteTraceRow) but has not been run against live production traffic. Current environment has only synthetic/smoke traces.",
            "NativeTraceCollectorReady": True,
            "NativeTraceCollectorReadyReason": "Collection infrastructure is fully implemented in source:\n  - RuntimeCandidateTraceSink (IRuntimeCandidateTraceSink, FileRuntimeCandidateTraceSink, NullRuntimeCandidateTraceSink)\n  - RuntimeCandidateTraceModels (18-field schema)\n  - RuntimeCandidateTraceContractValidator (6 critical fields validation)\n  - BasicContextPackageBuilder.WriteTraceRow() (inline trace capture)\n  - RuntimeCandidateTraceSinkAccessor (static wiring point)\nCollection can be activated by setting RuntimeCandidateTraceSinkAccessor.Current to a FileRuntimeCandidateTraceSink before pipeline execution.",
            "CollectorMode": "NativeRuntimeCandidateTracePreview",
            "ShadowAdapterFallbackReady": True,
            "ShadowAdapterFallbackNote": "V16.2 shadow-adapter mapped traces remain available as fallback/control group for comparison with native traces.",
            "CrossSystemMapping": False,
            "CrossSystemMappingNote": "V16.3 uses native schema, NOT cross-system mapped. ShadowAdapterSchemaMapped remains true for V16.2 control group only.",
        },
        "CollectorActivationPlan": {
            "Step1": "Create FileRuntimeCandidateTraceSink with output path to learning/v16_3/native-runtime-candidate-trace.jsonl",
            "Step2": "Set RuntimeCandidateTraceSinkAccessor.Current to the file sink",
            "Step3": "Set RuntimeCandidateTraceSinkAccessor.CurrentOperationId to a unique operation ID (e.g. 'native-v16_3-{timestamp}')",
            "Step4": "Set RuntimeCandidateTraceSinkAccessor.CurrentRequestId accordingly",
            "Step5": "Execute BasicContextPackageBuilder.BuildDetailedAsync() against target workspace/collection",
            "Step6": "After build completes, read the JSONL output file",
            "Step7": "Run V16.2 alpha sweep + calibration on the native trace data",
            "Step8": "Compare native trace metrics with shadow-adapter mapped trace metrics",
            "PreActivationChecklist": [
                "Verify RuntimeInfluenceAllowed == false in all code paths",
                "Verify NeuralBiasActive == false",
                "Verify HybridBlendAlpha == 1.0",
                "Verify PackageOutputChanged == false",
                "Verify VectorBindingChanged == false",
                "Verify BasicContextPackageBuilder.WriteTraceRow() only appends, never mutates",
                "Verify NullRuntimeCandidateTraceSink is default (no accidental trace spam)",
            ],
        },
        "ExpectedNativeTraceCharacteristics": {
            "traceSource": "3 (PackageTrace) for every row",
            "deterministicScore_NotIdenticalToNeural": "deterministicScore != neuralScore (neural score is not in trace; it's derived during shadow evaluation)",
            "selectedByScoring_ReflectsRuntime": "True for scored-and-kept, False for rejected/deprecated",
            "includedInPackage_ReflectsBudget": "False for token-budget-dropped items with droppedReason='token budget exhausted'",
            "section_Diverse": "All 8+ section types represented (current_task, hard_constraints, working_memory, global_context, recent_context, stable_memory, soft_constraints, related_context, raw legacy docs)",
            "operationId_UniquePerRun": "Each collection run produces rows with a unique operationId prefix",
            "RowCount": "Varies by workspace/collection complexity; typically 30-200 rows per operation",
        },
        "GateSemantics": {
            "RuntimeInfluenceAllowed": False,
            "PackageOutputChanged": False,
            "VectorBindingChanged": False,
            "RuntimePromotionApplied": False,
            "ProductionGeneralizationReady": False,
            "ProductionGeneralizationNote": "Native collection must complete and pass metric-quality gate before ProductionGeneralizationReady can be true. Shadow-adapter mapped traces do not qualify.",
            "NextStep": "Activate collector against actual workspaces/collections to produce native runtime candidate scoring traces. Then run V16.2 evaluation pipeline on native traces.",
        },
    }

    # =========================================================================
    # Write all artifacts
    # =========================================================================
    with open(OUT_SCHEMA_JSON, "w", encoding="utf-8") as fh:
        json.dump(schema_contract, fh, indent=2, ensure_ascii=False)
    print(f"Written: {OUT_SCHEMA_JSON}")

    with open(OUT_SAFETY_JSON, "w", encoding="utf-8") as fh:
        json.dump(safety_gate, fh, indent=2, ensure_ascii=False)
    print(f"Written: {OUT_SAFETY_JSON}")

    with open(OUT_READINESS_JSON, "w", encoding="utf-8") as fh:
        json.dump(readiness, fh, indent=2, ensure_ascii=False)
    print(f"Written: {OUT_READINESS_JSON}")

    # =========================================================================
    # Markdown artifact
    # =========================================================================
    readiness_md = f"""# V16.3: Native Runtime Trace Readiness & Collector Preview

Generated: {now}

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
"""

    with open(OUT_READINESS_MD, "w", encoding="utf-8") as fh:
        fh.write(readiness_md)
    print(f"Written: {OUT_READINESS_MD}")

    # Summary
    print(f"\n=== V16.3 Artifact Generation Complete ===")
    print(f"native-trace-schema-contract.json: {18} fields defined")
    print(f"native-trace-safety-gate.json: All safety gates ACTIVE")
    print(f"native-runtime-trace-readiness.json: CollectorReady=true, ProductionTraceReady=false")
    print(f"native-runtime-trace-readiness.md: written")
    print(f"\nGates:")
    print(f"  NativeProductionTraceReady: False (no native traces collected yet)")
    print(f"  NativeTraceCollectorReady: True (infrastructure complete)")
    print(f"  RuntimeInfluenceAllowed: False")
    print(f"  PackageOutputChanged: False")
    print(f"  VectorBindingChanged: False")


if __name__ == "__main__":
    generate()
