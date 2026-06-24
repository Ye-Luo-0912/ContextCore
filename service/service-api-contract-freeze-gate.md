# Service API Contract Freeze

Generated: `2026-06-16T16:39:00.9621627+00:00`
OperationId: `service-api-contract-d4ff078359ad4212af46f8f75aadc5c0`

- ContractPassed: `True`
- FreezePassed: `True`
- Recommendation: `ReadyForServiceApiContractFreeze`
- EndpointCount: `8`
- ClientMethodCount: `8`
- EnvelopeSchemaVersion: `foundation-api-envelope-v1`
- AuthMode: `NotConfigured`
- AuthConfigured: `False`
- ProductionMode: `False`
- DegradedBehaviorStable: `True`
- ReportNavigationSchemaStable: `True`
- ForbiddenActionsExposed: `True`
- SecretLeakDetected: `False`
- AbsolutePathLeakDetected: `False`
- RuntimeSwitchAllowed: `False`
- FormalRetrievalAllowed: `False`
- ReadyForRuntimeSwitch: `False`
- FormalPackageWritten: `False`
- PackingPolicyChanged: `False`
- PackageOutputChanged: `False`
- RuntimeMutated: `False`

## Endpoints
- `GET /api/admin/foundation/status` -> `FoundationServiceStatusResponse` envelope=`True` readOnly=`True`
- `GET /api/admin/foundation/release-candidate` -> `FoundationServiceStatusResponse` envelope=`True` readOnly=`True`
- `GET /api/admin/foundation/reproducibility` -> `FoundationServiceStatusResponse` envelope=`True` readOnly=`True`
- `GET /api/admin/foundation/runtime-change-gate` -> `FoundationServiceStatusResponse` envelope=`True` readOnly=`True`
- `GET /api/admin/foundation/vector-formal-preview` -> `FoundationServiceStatusResponse` envelope=`True` readOnly=`True`
- `GET /api/admin/foundation/postgres-freeze-status` -> `FoundationServiceStatusResponse` envelope=`True` readOnly=`True`
- `GET /api/admin/foundation/reports` -> `FoundationReportNavigationResponse` envelope=`True` readOnly=`True`
- `GET /api/admin/foundation/reports/{reportId}` -> `FoundationReportNavigationEntry` envelope=`True` readOnly=`True`

## Client Methods
- `GetFoundationStatusAsync` -> `/api/admin/foundation/status` response=`FoundationServiceStatusResponse` envelope=`True`
- `GetFoundationReleaseCandidateAsync` -> `/api/admin/foundation/release-candidate` response=`FoundationServiceStatusResponse` envelope=`True`
- `GetFoundationReproducibilityAsync` -> `/api/admin/foundation/reproducibility` response=`FoundationServiceStatusResponse` envelope=`True`
- `GetRuntimeChangeGateAsync` -> `/api/admin/foundation/runtime-change-gate` response=`FoundationServiceStatusResponse` envelope=`True`
- `GetVectorFormalPreviewStatusAsync` -> `/api/admin/foundation/vector-formal-preview` response=`FoundationServiceStatusResponse` envelope=`True`
- `GetPostgresFreezeStatusAsync` -> `/api/admin/foundation/postgres-freeze-status` response=`FoundationServiceStatusResponse` envelope=`True`
- `GetFoundationReportsAsync` -> `/api/admin/foundation/reports` response=`FoundationReportNavigationResponse` envelope=`True`
- `GetFoundationReportAsync` -> `/api/admin/foundation/reports/{reportId}` response=`FoundationReportNavigationEntry` envelope=`True`

## Envelope Schema
- `Success`
- `CapabilityId`
- `Status`
- `Recommendation`
- `Data`
- `Diagnostics`
- `GeneratedAt`
- `SchemaVersion`

## Report Navigation Schema
- `ReportId`
- `CapabilityId`
- `RelativePath`
- `Exists`
- `GeneratedAt`
- `ContentType`
- `Summary`
- `SafeToExpose`

## Forbidden Actions
- `RuntimeSwitch`
- `FormalRetrieval`
- `FormalPackageWrite`
- `PackingPolicyMutation`
- `PackageOutputMutation`
- `NonAllowlistedScopeUse`

## Blocked Reasons
- (empty)

This contract is read-only and does not allow runtime switch, formal retrieval, formal package write, PackingPolicy integration, or package output mutation.
