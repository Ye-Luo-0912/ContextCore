# Hosted Service Deployment Smoke

Generated: `2026-06-16T16:37:04.1017771+00:00`
OperationId: `service-hosted-smoke-58c74c5078af4261865cca9a15d5122c`

- SmokePassed: `True`
- BaseUrl: `http://127.0.0.1:62090`
- DeploymentProfile: `Development`
- EndpointCount: `8`
- SuccessfulEndpointCount: `8`
- FailedEndpointCount: `0`
- AuthPassed: `True`
- UnauthorizedCheckPassed: `True`
- EnvelopeSchemaMatched: `True`
- RuntimeMutated: `False`
- FormalRetrievalAllowed: `False`
- RuntimeSwitchAllowed: `False`
- ReadyForRuntimeSwitch: `False`
- PackingPolicyChanged: `False`
- PackageOutputChanged: `False`
- SecretLeakDetected: `False`
- AbsolutePathLeakDetected: `False`
- Recommendation: `ReadyForHostedReadOnlyService`

## Endpoints
- `GET /api/admin/foundation/status` status=`200` success=`True` envelope=`True`
- `GET /api/admin/foundation/release-candidate` status=`200` success=`True` envelope=`True`
- `GET /api/admin/foundation/reproducibility` status=`200` success=`True` envelope=`True`
- `GET /api/admin/foundation/runtime-change-gate` status=`200` success=`True` envelope=`True`
- `GET /api/admin/foundation/vector-formal-preview` status=`200` success=`True` envelope=`True`
- `GET /api/admin/foundation/postgres-freeze-status` status=`200` success=`True` envelope=`True`
- `GET /api/admin/foundation/reports` status=`200` success=`True` envelope=`True`
- `GET /api/admin/foundation/reports/{reportId}` status=`200` success=`True` envelope=`True`

## Blocked Reasons
- (empty)

Hosted smoke is read-only. It does not enable formal retrieval, runtime switch, formal package write, PackingPolicy integration, or package output mutation.
