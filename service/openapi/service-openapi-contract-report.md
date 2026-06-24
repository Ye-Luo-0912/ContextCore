# Service OpenAPI / Client Contract Snapshot

Generated: `2026-06-16T16:36:15.9888773+00:00`
OperationId: `service-openapi-contract-876e8b8fd94a451c9c57ba589975de94`

- EndpointCount: `8`
- ClientMethodCount: `13`
- EnvelopeSchemaVersion: `foundation-api-envelope-v1`
- AuthScheme: `ApiKeyAuth`
- ApiKeyHeaderName: `X-ContextCore-Key`
- RequestSchemaCount: `0`
- ResponseSchemaCount: `9`
- ForbiddenActionCount: `6`
- BreakingChangeDetected: `False`
- SecretLeakDetected: `False`
- AbsolutePathLeakDetected: `False`
- Recommendation: `ReadyForOpenApiContractFreeze`

## Endpoints
- `GET /api/admin/foundation/postgres-freeze-status`
- `GET /api/admin/foundation/release-candidate`
- `GET /api/admin/foundation/reports`
- `GET /api/admin/foundation/reports/{reportId}`
- `GET /api/admin/foundation/reproducibility`
- `GET /api/admin/foundation/runtime-change-gate`
- `GET /api/admin/foundation/status`
- `GET /api/admin/foundation/vector-formal-preview`

## Blocked Reasons
- (empty)

OpenAPI/snapshot artifacts are read-only contracts. They do not enable runtime switch, formal retrieval, formal package write, PackingPolicy integration, or package output mutation.
