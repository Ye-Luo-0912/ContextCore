# Vector Scoped Runtime Experiment Dry-run Route

Generated: `2026-06-17T06:26:18.1201089+00:00`
OperationId: `vector-scoped-runtime-experiment-dry-run-route-a78c42b0cdf640e8ad67e2385d311c1e`

- PreflightPassed: `True`
- Recommendation: `ReadyForGuardedScopedRuntimeExperiment`
- ProposalId: `vsrep-bb5402e39c0f1333`
- ApprovalId: `vsrea-49cee0621cf6bc41`
- Mode: `PreflightAndDryRunRoute`
- KillSwitchAvailable: `True`
- RollbackPlanAvailable: `True`
- TraceSinkAvailable: `True`
- ConfigPatchPreviewed/Written: `True` / `False`
- RuntimeRouteDryRunExecuted: `True`
- DryRunRouteHitCount: `1`
- NonAllowlistedScopeChecked/Leak: `True` / `0`
- RuntimeMutated: `False`
- VectorStoreBindingChanged: `False`
- FormalPackageWritten: `False`
- PackingPolicyChanged: `False`
- PackageOutputChanged: `False`
- FormalRetrievalAllowed: `False`
- RuntimeSwitchAllowed: `False`
- ReadyForRuntimeSwitch: `False`
- RiskAfterPolicy: `0`
- FormalOutputChanged: `0`

## Selected Scopes
- `contextcore_eval/dataset-v2-stress/dataset-v2-stress`

## Blocked Reasons
- (empty)

Route output is DryRunOnly. V4.13 does not write formal packages or mutate runtime bindings.
