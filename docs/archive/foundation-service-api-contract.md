# Foundation Service API Contract

SVC3 freezes the read-only foundation API/client contract for the frozen foundation release candidate.

## Scope

The contract is read-only. It only exposes generated freeze/gate/report artifacts and does not mutate runtime state.

Forbidden in this phase:

- runtime switch
- formal retrieval switch
- formal package write
- `PackingPolicy` integration
- package output mutation
- non-allowlisted scoped preview use

## Endpoints

All endpoints return `FoundationApiResponseEnvelope<T>` with `SchemaVersion=foundation-api-envelope-v1`.

- `GET /api/admin/foundation/status`
- `GET /api/admin/foundation/release-candidate`
- `GET /api/admin/foundation/reproducibility`
- `GET /api/admin/foundation/runtime-change-gate`
- `GET /api/admin/foundation/vector-formal-preview`
- `GET /api/admin/foundation/postgres-freeze-status`
- `GET /api/admin/foundation/reports`
- `GET /api/admin/foundation/reports/{reportId}`

Envelope fields:

- `Success`
- `CapabilityId`
- `Status`
- `Recommendation`
- `Data`
- `Diagnostics`
- `GeneratedAt`
- `SchemaVersion`

## Client Methods

- `GetFoundationStatusAsync`
- `GetFoundationReleaseCandidateAsync`
- `GetFoundationReproducibilityAsync`
- `GetRuntimeChangeGateAsync`
- `GetVectorFormalPreviewStatusAsync`
- `GetPostgresFreezeStatusAsync`
- `GetFoundationReportsAsync`
- `GetFoundationReportAsync`

Legacy `...StatusAsync` aliases remain supported for compatibility.

## Auth Contract

Development mode may report `AuthConfigured=false`, but the status must be explicit as `DevelopmentOnly` or `NotConfigured`.

Production/service mode requires configured auth. Missing auth blocks the contract freeze gate.

API keys and secret values are never serialized into responses, reports, or logs.

## Degraded Behavior

Missing reports do not throw unhandled exceptions. Responses remain envelope-shaped and return:

- `Status=Degraded`
- `Recommendation=RegenerateReport`
- `Diagnostics.MissingReportIds=[...]`

Report navigation only exposes safe repo-relative paths.

## Eval

- `eval service-api-contract-report`
- `eval service-api-contract-freeze-gate`

Outputs:

- `service/service-api-contract-report.json`
- `service/service-api-contract-report.md`
- `service/service-api-contract-freeze-gate.json`
- `service/service-api-contract-freeze-gate.md`

## Deployment Profile Auth

SVC4 adds an explicit deployment profile contract for the same read-only endpoints.

Profiles:

- `Development`: no-auth is allowed only when explicitly reported as development-only / not configured.
- `Service`: `RequireApiKey=true` requires a configured API key.
- `Production`: missing auth blocks the deployment profile gate.

Options:

- `Enabled`
- `DeploymentProfile`
- `RequireApiKey`
- `ApiKeyHeaderName`
- `AllowDevelopmentNoAuth`
- `RedactSecrets=true`
- `FailOnSecretLeak=true`

The API key header name may be shown. API key values and local secret paths must never be returned in API responses, reports, or logs.

Eval:

- `eval service-auth-diagnostics`
- `eval service-auth-enforcement-smoke`
- `eval service-deployment-profile-gate`

Outputs:

- `service/service-auth-diagnostics.json`
- `service/service-auth-diagnostics.md`
- `service/service-auth-enforcement-smoke.json`
- `service/service-auth-enforcement-smoke.md`
- `service/service-deployment-profile-gate.json`
- `service/service-deployment-profile-gate.md`

## OpenAPI / Client Snapshot

SVC5 freezes a repo-local OpenAPI export and client SDK snapshot for the eight read-only foundation endpoints.

Commands:

- `eval service-openapi-contract-export`
- `eval service-client-contract-snapshot`
- `eval service-api-contract-drift-gate`

Outputs:

- `service/openapi/foundation-api.openapi.json`
- `service/openapi/foundation-api-contract-snapshot.json`
- `service/openapi/foundation-client-contract-snapshot.json`
- `service/openapi/service-openapi-contract-report.json`
- `service/openapi/service-openapi-contract-report.md`
- `service/openapi/service-api-contract-drift-gate.json`
- `service/openapi/service-api-contract-drift-gate.md`

The OpenAPI contract records:

- envelope schema version `foundation-api-envelope-v1`
- `CapabilityStatus` schema
- report navigation schema
- degraded response schema
- `ApiKeyAuth` header scheme with header name only
- forbidden runtime actions

The client snapshot records the eight frozen read-only methods plus compatibility aliases. Drift gate blocks endpoint deletion, envelope schema mismatch, client method deletion, auth downgrade, forbidden action omission, secret leaks, and local absolute path leaks.

SVC5 remains read-only: it does not enable formal retrieval, runtime switch, formal package write, `PackingPolicy` integration, or package output mutation.

## Hosted Service Smoke

SVC6 adds hosted deployment smoke checks for the frozen read-only foundation API.

Commands:

- `eval service-hosted-deployment-smoke [--base-url <url>]`
- `eval service-readonly-runtime-smoke [--base-url <url>]`
- `eval service-hosted-api-contract-smoke [--base-url <url>]`

Outputs:

- `service/hosted/service-hosted-deployment-smoke.json`
- `service/hosted/service-hosted-deployment-smoke.md`
- `service/hosted/service-readonly-runtime-smoke.json`
- `service/hosted/service-readonly-runtime-smoke.md`
- `service/hosted/service-hosted-api-contract-smoke.json`
- `service/hosted/service-hosted-api-contract-smoke.md`

When no hosted `BaseUrl` is configured, the smoke reports `NeedsHostedServiceConfig` instead of pretending that a runtime deployment was checked. With a configured `BaseUrl`, it probes all 8 foundation endpoints, validates `foundation-api-envelope-v1`, verifies API key behavior for service/production profiles, checks wrong-key unauthorized behavior, and confirms the response surface keeps runtime mutation and formal retrieval disabled.

## Service Foundation Freeze

SVC.F freezes the service foundation after SVC1-SVC6 by aggregating the read-only API smoke, service hardening, API contract freeze, deployment profile gate, OpenAPI/client drift gate, hosted smoke, foundation release candidate gate, reproducibility check, runtime-change gate, and P15 gate.

Command:

- `eval service-foundation-freeze-gate`

Outputs:

- `service/service-foundation-freeze-gate.json`
- `service/service-foundation-freeze-gate.md`

Freeze state:

- `ServiceFoundation=Frozen`
- `FoundationApi=ReadyForHostedReadOnlyService`
- `OpenApiContract=Frozen`
- `AuthDeploymentProfile=Ready`
- `RuntimeMutationAllowed=false`
- `FormalRetrievalAllowed=false`
- `RuntimeSwitchAllowed=false`
- `ReadyForRuntimeSwitch=false`

Passing SVC.F does not enable runtime switch, formal retrieval, formal package writes, `PackingPolicy` integration, or package output mutation. The next allowed phase is V4.5 Explicit Scoped Runtime Experiment Planning.
