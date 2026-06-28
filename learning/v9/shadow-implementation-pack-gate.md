# Learning Shadow Implementation Pack (Gate)

- ShadowImplementationPackPassed: `True`
- GatePassed: `True`
- TotalCases: `21` PassedCases: `21` FailedCases: `0`
- ReadyCases: `1` BlockedCases: `20`

## Authority Invariants
- ShadowOnly: `True` RuntimeAuthority: `False` GateAuthority: `False`
- RuntimeRerankerChanged: `False` RuntimeRouterChanged: `False`
- PackageOutputChanged: `False` FormalPackageWritten: `False` GlobalDefaultOn: `False`
- V8ScopedActivationPreserved: `True`

## Candidate Reranker Baselines
- `WeightedBaseline` ready=True train=195 eval=58 pairwiseAcc=0.862 
- `LogisticBaseline` ready=True train=195 eval=58 pairwiseAcc=1.000 
- `TreeBaseline` ready=True train=195 eval=58 pairwiseAcc=1.000 
- `LightweightMLPShadowCandidate` ready=False train=195 eval=58 pairwiseAcc=0.000 (reason: MLP training deferred to V9.3 dedicated phase to avoid pulling heavy ML dependency. Dataset exported in jsonl form for downstream training.)

## Router Intent Baselines
- `RouterIntentLogistic` ready=True train=130 eval=33 acc=0.121 
- `RouterIntentTree` ready=True train=130 eval=33 acc=0.061 

## Hard Negative Coverage
- HardNegativeCount: `18`
- CoverageRate: `0.159`
- HardNegativeTrainingReady: `False`
- NotReadyReason: `hard-negative count below 50; awaiting V9.4 hard-negative-generation expansion`

## Outputs
- FailureSampleExported: `True` (2 files)
- ShadowComparisonSummaryWritten: `True`
- Recommendation: `ProceedToV9.4FailureDiagnosisAndHardNegativeLoop` NextAllowedPhase: `V9.4FailureDiagnosisAndHardNegativeLoop`
