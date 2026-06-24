# ContextCore Foundation Freeze Report

## Freeze Scope

本报告记录 ContextCore foundation release candidate 的全局冻结边界。该阶段只汇总既有 storage、vector、runtime-change 与 P15 gate 结果，不接 formal retrieval，不切 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

## Frozen Capabilities

- RelationGovernancePostgres：`ReadyForLimitedScopeExpansion`
- LearningFeedbackPostgres：`ReadyForScopedServiceMode`
- JobQueuePostgres：`ReadyForScopedWorkerMode`
- VectorPostgresProvider：`ReadyForPreviewShadowStorage`
- VectorFormalPreview：`ReadyForScopedOptInPreview`

## Runtime Boundary

- `RuntimeSwitchAllowed=false`
- `FormalRetrievalAllowed=false`
- `ReadyForRuntimeSwitch=false`
- `UseForRuntime=false`
- `PackingPolicyChanged=false`
- `PackageOutputChanged=false`

Foundation freeze 通过不等于 runtime enablement。`ScopedPreviewOnly` 仍只允许显式 allowlisted preview / eval / shadow artifact，不允许正式 package 写入或 runtime retrieval 替换。

## Required Gates

- `eval learning-runtime-change-readiness-gate`
- `scripts/eval-gate-p15.ps1`
- `eval vector-formal-preview-freeze-gate`
- `eval foundation-freeze-report`
- `eval foundation-release-candidate-gate`
- `eval foundation-reproducibility-check`

## Outputs

- `foundation/foundation-freeze-report.json`
- `foundation/foundation-freeze-report.md`
- `foundation/foundation-release-candidate-gate.json`
- `foundation/foundation-release-candidate-gate.md`
- `foundation/foundation-reproducibility-check.json`
- `foundation/foundation-reproducibility-check.md`
- `foundation/service-foundation-status-smoke.json`
- `foundation/service-foundation-status-smoke.md`
- `foundation/service-readiness-api-smoke.json`
- `foundation/service-readiness-api-smoke.md`

## RC0 Reproducibility Check

RC0 增加 `foundation-reproducibility-check`，用于 release candidate cleanup 与可复现性确认。该检查只读取当前 gate 报告、P15 报告和 `git status`，不执行 build/test，不改变 runtime。

检查内容：

- source code / tests / docs / generated reports / local config / model files / temp files 分类。
- 关键报告存在性：foundation release candidate、runtime-change gate、vector formal preview freeze、P15、foundation freeze doc。
- 边界：`FormalRetrievalAllowed=false`、`RuntimeSwitchAllowed=false`、`ReadyForRuntimeSwitch=false`、`PackingPolicyChanged=false`、`PackageOutputChanged=false`、适用报告中 `UseForRuntime=false`。
- local secrets / local DB config / large model binaries / temp traces 不应进入 git status。

期望结果：

- `ReproducibilityPassed=true`
- `Recommendation=ReadyForReleaseCandidateReproduction`
- `LocalSecretsDetected=false`

## Next Allowed Phase

- `ScopedRuntimeExperimentPlanning`
- `NextSubsystemDevelopment`

正式 runtime switch、formal retrieval、正式 vector store binding、正式 package write、`PackingPolicy` / package output integration 仍需要后续独立 gate。

## SVC1 Read-only Service/API Hardening

Frozen foundation 增加只读 API，用于运维面读取 release candidate、runtime-change gate、reproducibility、vector formal preview 和 Postgres freeze 状态。

API：

- `GET /api/admin/foundation/status`
- `GET /api/admin/foundation/release-candidate`
- `GET /api/admin/foundation/reproducibility`
- `GET /api/admin/foundation/runtime-change-gate`
- `GET /api/admin/foundation/vector-formal-preview`
- `GET /api/admin/foundation/postgres-freeze-status`

所有 API 返回统一 `FoundationServiceStatusResponse`，其中每个 capability 使用 `CapabilityStatus` 表达 gate、state、recommendation、allowed / forbidden modes 和 runtime boundary flags。

SVC1 只读取报告文件，不切换 runtime，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

## SVC2 Read-only API Auth / Report Navigation Hardening

SVC2 在 frozen foundation API 上增加安全诊断、统一 envelope 和报告导航。

API envelope：

- `Success`
- `CapabilityId`
- `Status`
- `Recommendation`
- `Data`
- `Diagnostics`
- `GeneratedAt`
- `SchemaVersion=foundation-api-envelope-v1`

新增报告导航 API：

- `GET /api/admin/foundation/reports`
- `GET /api/admin/foundation/reports/{reportId}`

新增报告：

- `service/service-api-security-diagnostics.json`
- `service/service-api-security-diagnostics.md`
- `service/service-report-navigation-smoke.json`
- `service/service-report-navigation-smoke.md`

安全边界：

- 不在 API response、logs 或 report 中输出 API key。
- report navigation 只暴露 repo 相对路径。
- 禁止返回 `D:\...`、`C:\...`、`/home/...`、`.contextcore/secrets.json`、模型二进制路径。
- 缺失报告以 degraded status 表达：`Status=Degraded`、`Recommendation=RegenerateReport`、`Diagnostics.MissingReportIds=[...]`。
- `FormalRetrievalAllowed=false`、`RuntimeSwitchAllowed=false`、`ReadyForRuntimeSwitch=false` 保持不变。

## SVC3 Service OpenAPI / Client Contract Freeze

SVC3 固化 frozen foundation read-only API contract。

固定 endpoint：

- `GET /api/admin/foundation/status`
- `GET /api/admin/foundation/release-candidate`
- `GET /api/admin/foundation/reproducibility`
- `GET /api/admin/foundation/runtime-change-gate`
- `GET /api/admin/foundation/vector-formal-preview`
- `GET /api/admin/foundation/postgres-freeze-status`
- `GET /api/admin/foundation/reports`
- `GET /api/admin/foundation/reports/{reportId}`

固定 envelope：

- `Success`
- `CapabilityId`
- `Status`
- `Recommendation`
- `Data`
- `Diagnostics`
- `GeneratedAt`
- `SchemaVersion=foundation-api-envelope-v1`

新增报告：

- `service/service-api-contract-report.json`
- `service/service-api-contract-report.md`
- `service/service-api-contract-freeze-gate.json`
- `service/service-api-contract-freeze-gate.md`

Contract freeze 仍保持：

- `FormalRetrievalAllowed=false`
- `RuntimeSwitchAllowed=false`
- `ReadyForRuntimeSwitch=false`
- `PackingPolicyChanged=false`
- `PackageOutputChanged=false`

## Service Foundation Freeze

SVC.F freezes the service/API foundation as a hosted read-only surface over the already frozen ContextCore Foundation.

Required reports:

- `foundation/service-foundation-status-smoke.json`
- `foundation/service-readiness-api-smoke.json`
- `service/service-api-security-diagnostics.json`
- `service/service-report-navigation-smoke.json`
- `service/service-api-contract-freeze-gate.json`
- `service/service-deployment-profile-gate.json`
- `service/openapi/service-api-contract-drift-gate.json`
- `service/hosted/service-hosted-deployment-smoke.json`
- `service/hosted/service-readonly-runtime-smoke.json`
- `service/hosted/service-hosted-api-contract-smoke.json`
- `foundation/foundation-release-candidate-gate.json`
- `foundation/foundation-reproducibility-check.json`
- `learning/readiness/learning-runtime-change-readiness-gate.json`

Current SVC.F output:

- `service/service-foundation-freeze-gate.json`
- `service/service-foundation-freeze-gate.md`

Frozen state:

- `ServiceFoundation=Frozen`
- `FoundationApi=ReadyForHostedReadOnlyService`
- `OpenApiContract=Frozen`
- `AuthDeploymentProfile=Ready`
- `RuntimeMutationAllowed=false`
- `FormalRetrievalAllowed=false`
- `RuntimeSwitchAllowed=false`
- `ReadyForRuntimeSwitch=false`

SVC.F does not allow runtime switch, formal retrieval, formal package write, `PackingPolicy` integration, package output mutation, or non-read-only foundation API behavior. The next allowed phase is V4.5 Explicit Scoped Runtime Experiment Planning.

Production/service mode 下缺 auth 会阻断 contract freeze；development/not-configured 状态必须显式，不允许输出 secret 或本地绝对路径。

## SVC4 Service Auth Enforcement / Deployment Profile

SVC4 为 frozen foundation read-only API 增加 deployment profile 诊断与 gate。

Profiles：

- `Development`
- `Service`
- `Production`

新增报告：

- `service/service-auth-diagnostics.json`
- `service/service-auth-diagnostics.md`
- `service/service-auth-enforcement-smoke.json`
- `service/service-auth-enforcement-smoke.md`

## SVC5 OpenAPI / Client Contract Snapshot

SVC5 adds reproducible read-only API contract artifacts:

- `service/openapi/foundation-api.openapi.json`
- `service/openapi/foundation-api-contract-snapshot.json`
- `service/openapi/foundation-client-contract-snapshot.json`
- `service/openapi/service-openapi-contract-report.json`
- `service/openapi/service-openapi-contract-report.md`
- `service/openapi/service-api-contract-drift-gate.json`
- `service/openapi/service-api-contract-drift-gate.md`

Current SVC5 status:

- `EndpointCount=8`
- `ClientMethodCount=13` including compatibility aliases
- `EnvelopeSchemaVersion=foundation-api-envelope-v1`
- `AuthScheme=ApiKeyAuth`
- `BreakingChangeDetected=false`
- `SecretLeakDetected=false`
- `AbsolutePathLeakDetected=false`
- `Recommendation=ReadyForOpenApiContractFreeze`

SVC5 keeps `FormalRetrievalAllowed=false`, `RuntimeSwitchAllowed=false`, `ReadyForRuntimeSwitch=false`, and does not mutate `PackingPolicy` or package output.

## SVC6 Hosted Read-only Runtime Smoke

SVC6 adds hosted deployment/read-only runtime/API-contract smoke reports:

- `service/hosted/service-hosted-deployment-smoke.json`
- `service/hosted/service-hosted-deployment-smoke.md`
- `service/hosted/service-readonly-runtime-smoke.json`
- `service/hosted/service-readonly-runtime-smoke.md`
- `service/hosted/service-hosted-api-contract-smoke.json`
- `service/hosted/service-hosted-api-contract-smoke.md`

The smoke checks all 8 read-only foundation endpoints when a hosted `BaseUrl` is configured. Without a configured hosted service URL, it returns `NeedsHostedServiceConfig` as an explicit degraded validation status.

SVC6 preserves foundation boundaries:

- `FormalRetrievalAllowed=false`
- `RuntimeSwitchAllowed=false`
- `ReadyForRuntimeSwitch=false`
- `RuntimeMutated=false`
- `PackingPolicyChanged=false`
- `PackageOutputChanged=false`
- `service/service-deployment-profile-gate.json`
- `service/service-deployment-profile-gate.md`

部署规则：

- Development profile 可允许 no-auth，但必须显式报告 development-only / not configured。
- Service profile 下 `RequireApiKey=true` 必须配置 API key。
- Production profile 下 `AuthConfigured=false` 必须 gate fail。
- wrong API key 必须 unauthorized，correct API key 才能访问只读 API。
- API key value、本地 secret path、本地绝对路径不允许出现在 response / report / log。

Foundation runtime boundary 继续保持：

- `FormalRetrievalAllowed=false`
- `RuntimeSwitchAllowed=false`
- `ReadyForRuntimeSwitch=false`
- `PackingPolicyChanged=false`
- `PackageOutputChanged=false`

## V4.5 Planning Boundary

Foundation 与 Service Foundation freeze 通过后，下一阶段仅允许 Explicit Scoped Runtime Experiment Planning。

允许：

- scope allowlist planning
- preview profile selection
- rollback plan definition
- observation metrics definition
- dry-run package comparison planning
- shadow artifact only dry-run

禁止：

- runtime switch
- formal `IVectorIndexStore` binding
- formal package write
- `PackingPolicy` integration
- package output mutation
- global default-on
- non-allowlisted scope use

V4.5 gate 必须继续保持 `FormalRetrievalAllowed=false`、`RuntimeSwitchAllowed=false`、`ReadyForRuntimeSwitch=false`、`UseForRuntime=false`。

## V4.6 Dry-run Observation Boundary

V4.6 只允许 Explicit Scoped Runtime Experiment 的 dry-run observation。

- 读取 V4.5 gate、shadow package comparison gate 和 runtime-change gate。
- 输出 `vector/v4/vector-scoped-runtime-experiment-dry-run-observation.json/.md`。
- 输出 `vector/v4/vector-scoped-runtime-experiment-dry-run-observation-gate.json/.md`。
- 必须保持 `FormalRetrievalAllowed=false`、`ReadyForRuntimeSwitch=false`、`UseForRuntime=false`。
- 必须保持 `FormalPackageWritten=false`、`RuntimeMutated=false`、`VectorStoreBindingChanged=false`、`PackingPolicyChanged=false`、`PackageOutputChanged=false`。

通过 V4.6 gate 只代表 dry-run observation 可进入 design freeze；不代表 runtime switch 或正式 retrieval 启用。

## V4.7 Scoped Runtime Experiment Design Freeze

V4.7 freezes the explicit scoped runtime experiment design boundary.

Generated artifacts:

- `vector/v4/vector-scoped-runtime-experiment-design-freeze-gate.json`
- `vector/v4/vector-scoped-runtime-experiment-design-freeze-gate.md`

Expected frozen status:

- `ScopedRuntimeExperimentDesign=Frozen`
- `AllowedMode=ExplicitScopedRuntimeExperimentOnly`
- `ReadyForRuntimeExperimentProposal=true`
- `RuntimeSwitchAllowed=false`
- `FormalRetrievalAllowed=false`
- `ReadyForRuntimeSwitch=false`
- `UseForRuntime=false`
- `FormalPackageWriteAllowed=false`
- `PackingPolicyIntegrationAllowed=false`
- `GlobalDefaultOnAllowed=false`

V4.7 allows only selected-scope proposal preparation, rollback plan validation, and metrics collection planning. It still forbids runtime switch, formal vector store binding, formal package write, `PackingPolicy` integration, package output mutation, global default-on, and non-allowlisted scope use.

## V4.8 Explicit Scoped Runtime Experiment Proposal

V4.8 creates only proposal and config patch preview artifacts for an explicit selected scope.

Generated artifacts:

- `vector/v4/vector-scoped-runtime-experiment-proposal.json`
- `vector/v4/vector-scoped-runtime-experiment-proposal.md`
- `vector/v4/vector-scoped-runtime-experiment-config-preview.json`
- `vector/v4/vector-scoped-runtime-experiment-config-preview.md`
- `vector/v4/vector-scoped-runtime-experiment-proposal-gate.json`
- `vector/v4/vector-scoped-runtime-experiment-proposal-gate.md`

Expected status:

- `Recommendation=ReadyForManualExperimentApproval`
- `ApprovalRequired=true`
- `Approved=false`
- `RuntimeSwitchAllowed=false`
- `FormalRetrievalAllowed=false`
- `ReadyForRuntimeSwitch=false`
- `UseForRuntime=false`
- `WriteFormalPackage=false`

V4.8 does not modify appsettings, does not modify DI binding, does not bind a formal vector store, does not write a formal package, and does not mutate `PackingPolicy` or package output.

## V4.9 Scoped Runtime Experiment Approval / No-op Harness

V4.9 records explicit manual approval for the V4.8 proposal and runs a no-op harness only.

Generated artifacts:

- `vector/v4/runtime-experiment/approval-preview.json`
- `vector/v4/runtime-experiment/approval-preview.md`
- `vector/v4/runtime-experiment/approval-record.json`
- `vector/v4/runtime-experiment/approval-record.md`
- `vector/v4/runtime-experiment/approval-summary.json`
- `vector/v4/runtime-experiment/approval-summary.md`
- `vector/v4/runtime-experiment/noop-harness-report.json`
- `vector/v4/runtime-experiment/noop-harness-report.md`
- `vector/v4/runtime-experiment/noop-harness-gate.json`
- `vector/v4/runtime-experiment/noop-harness-gate.md`

Expected status:

- `ApprovalMode=NoOpHarnessOnly`
- `RuntimeSwitchAllowed=false`
- `FormalRetrievalAllowed=false`
- `ReadyForRuntimeSwitch=false`
- `UseForRuntime=false`
- `FormalPackageWritten=false`
- `RuntimeMutated=false`
- `VectorStoreBindingChanged=false`
- `PackingPolicyChanged=false`
- `PackageOutputChanged=false`

V4.9 does not approve a runtime switch. It only validates that the explicit selected scope can run a no-op harness under the existing frozen boundaries.

## V4.10 Scoped Runtime Experiment Dry-run Harness Freeze

V4.10 freezes the no-op harness boundary for scoped runtime experiment planning.

Generated artifacts:

- `vector/v4/runtime-experiment/harness-freeze-gate.json`
- `vector/v4/runtime-experiment/harness-freeze-gate.md`

Expected status:

- `ScopedRuntimeExperimentHarness=ReadyForGuardedRuntimeExperimentPlanning`
- `AllowedMode=NoOpHarnessOnly / ExplicitScopedExperimentPlanningOnly`
- `NextAllowedPhase=GuardedScopedRuntimeExperimentPlan`
- `RuntimeSwitchAllowed=false`
- `FormalRetrievalAllowed=false`
- `ReadyForRuntimeSwitch=false`
- `FormalPackageWritten=false`
- `RuntimeMutated=false`
- `VectorStoreBindingChanged=false`
- `PackingPolicyChanged=false`
- `PackageOutputChanged=false`

V4.10 does not approve runtime use. `NoOpHarnessOnly` is explicitly not a runtime approval mode.

## V4.11 Guarded Scoped Runtime Experiment Plan

V4.11 produces only the guarded scoped runtime experiment plan and activation contract.

Generated artifacts:

- `vector/v4/runtime-experiment/guarded-runtime-experiment-plan.json`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-plan.md`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-plan-gate.json`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-plan-gate.md`

Expected status:

- `PlanPassed=true`
- `Recommendation=ReadyForScopedRuntimeExperimentActivationContract`
- `RequiredApprovalMode=ScopedRuntimeExperiment`
- `RuntimeSwitchAllowed=false`
- `FormalRetrievalAllowed=false`
- `ReadyForRuntimeSwitch=false`
- `UseForRuntime=false`

V4.11 requires Foundation RC, Service Foundation freeze, Vector Formal Preview freeze, V4.7 design freeze, V4.10 harness freeze, runtime-change gate, explicit selected scope, kill switch plan, rollback plan, observation plan, and stop conditions. `NoOpHarnessOnly` approval remains insufficient for runtime approval.

V4.11 does not authorize runtime switch, formal retrieval, formal package writes, DI/vector binding mutation, `PackingPolicy` mutation, package output mutation, non-allowlisted scope use, or global default-on.

## V4.12 Scoped Runtime Experiment Approval Gate

V4.12 records and gates an explicit `ScopedRuntimeExperiment` approval for the V4.11 guarded plan.

Generated artifacts:

- `vector/v4/runtime-experiment/runtime-approval-request-preview.json`
- `vector/v4/runtime-experiment/runtime-approval-request-preview.md`
- `vector/v4/runtime-experiment/runtime-approval-record.json`
- `vector/v4/runtime-experiment/runtime-approval-record.md`
- `vector/v4/runtime-experiment/runtime-approval-gate.json`
- `vector/v4/runtime-experiment/runtime-approval-gate.md`
- `vector/v4/runtime-experiment/runtime-approval-summary.json`
- `vector/v4/runtime-experiment/runtime-approval-summary.md`

Expected status:

- `GatePassed=true`
- `Recommendation=ReadyForActivationPreflight`
- `ApprovalMode=ScopedRuntimeExperiment`
- `RequiredAcknowledgementsPresent=true`
- `RuntimeSwitchAllowed=false`
- `FormalRetrievalAllowed=false`
- `ReadyForRuntimeSwitch=false`
- `UseForRuntime=false`
- `FormalPackageWriteAllowed=false`
- `PackingPolicyIntegrationAllowed=false`

V4.12 approval allows only the next activation preflight phase. It does not authorize runtime switch, formal retrieval, formal package write, `PackingPolicy` integration, or package output mutation.

## V4.13 Activation Preflight + Guarded Runtime Dry-run Route

V4.13 validates the activation preflight and guarded runtime dry-run route for the scoped runtime experiment proposal.

Generated artifacts:

- `vector/v4/runtime-experiment/activation-preflight.json`
- `vector/v4/runtime-experiment/activation-preflight.md`
- `vector/v4/runtime-experiment/dry-run-route-report.json`
- `vector/v4/runtime-experiment/dry-run-route-report.md`
- `vector/v4/runtime-experiment/activation-gate.json`
- `vector/v4/runtime-experiment/activation-gate.md`
- `vector/v4/runtime-experiment/dry-run-route-traces.jsonl`

Expected status:

- `PreflightPassed=true`
- `Recommendation=ReadyForGuardedScopedRuntimeExperiment`
- `RuntimeRouteDryRunExecuted=true`
- `DryRunRouteHitCount=1`
- `KillSwitchAvailable=true`
- `RollbackPlanAvailable=true`
- `TraceSinkAvailable=true`
- `ConfigPatchPreviewed=true`
- `ConfigPatchWritten=false`
- `RuntimeMutated=false`
- `VectorStoreBindingChanged=false`
- `FormalPackageWritten=false`
- `PackingPolicyChanged=false`
- `PackageOutputChanged=false`
- `FormalRetrievalAllowed=false`
- `RuntimeSwitchAllowed=false`
- `ReadyForRuntimeSwitch=false`

V4.13 remains a dry-run/preflight phase. It does not authorize runtime switch, formal retrieval, formal package writes, global DI/vector store binding changes, `PackingPolicy` integration, or package output mutation.

## V4.14 Guarded Scoped Runtime Experiment

V4.14 executes the first guarded scoped shadow runtime experiment for the explicit allowlisted scope. It does not replace formal retrieval results, write formal packages, mutate `PackingPolicy`, change package output, modify DI/vector store binding, or enable global default-on behavior.

Generated artifacts:

- `vector/v4/runtime-experiment/guarded-runtime-experiment-report.json`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-report.md`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-observation.json`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-observation.md`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-rollback-smoke.json`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-rollback-smoke.md`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-gate.json`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-gate.md`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-traces.jsonl`

Expected status:

- `ExperimentPassed=true`
- `Recommendation=ReadyForScopedRuntimeExperimentObservation`
- `ExperimentRouteHitCount > 0`
- `NonAllowlistedScopeLeakCount=0`
- `RiskAfterPolicy=0`
- `MustNotHitRiskAfterPolicy=0`
- `LifecycleRiskAfterPolicy=0`
- `FormalOutputChanged=0`
- `PackageOutputChanged=false`
- `PackingPolicyChanged=false`
- `RuntimeMutated=false`
- `VectorStoreBindingChanged=false`
- `FormalPackageWritten=false`
- `KillSwitchAvailable=true`
- `RollbackVerified=true`

V4.14 remains shadow/observation only. It permits later scoped observation work but still does not authorize runtime switch, formal retrieval, formal package write, global vector binding changes, `PackingPolicy` integration, or package output mutation.

## PATH-HYGIENE 路径输出规则 (OPT-001)

所有 report / response / log / artifact 中禁止输出本地绝对路径。以下为强制规则：

### 禁止的路径模式

- Windows 绝对路径：`C:\...`、`D:\...`
- Unix 绝对路径：`/home/...`、`/Users/...`
- 其他绝对路径前缀：`\\` (UNC)

### 允许的路径模式

- **repo-relative path**：以 repo root 为基的 `src/`、`eval/`、`docs/`、`tests/`、`vector/` 等
- **环境变量占位符**：`%USERPROFILE%\.contextcore\...`（无法使用 repo-relative 的外部资源，如模型文件）
- **网络地址**：`Host=localhost` 等（数据库连接等，非本地 filesystem path）

### sample 配置规范

- Sample config（`*.sample.json`）只使用环境变量占位符或空字符串，不暴露任何真实路径
- 模型路径使用 `%USERPROFILE%\.contextcore\models\...` 作为示例占位符

### 代码约定

- 报告生成代码中使用 `repo-relative` 路径写入 `SourceFile` / `ModelPath` / `ContextsRootPath` / `BaselineReportPath` 等字段
- Console.WriteLine 输出路径用 repo-relative 替代 `Path.GetFullPath()`
- 测试 mock 数据不硬编码绝对路径

### OPT-001 已完成清理

- `appsettings.VectorEmbedding.sample.json`：`D:\Models\...` → `%USERPROFILE%\.contextcore\models\...`
- `patch_eval.py`：绝对路径 → `src/ContextCore.ControlRoom/Commands/EvalCommand.cs`
- `tests/ContextCore.Tests/ContextCoreClientTests.cs`：3 处绝对路径 → repo-relative
- `eval/` 报告 7 份：`SourceFile` / `ModelPath` / `ContextsRootPath` / `BaselineReportPath` / `Repository root` → repo-relative
- `docs/` 文档 4 份：内部链接 → 相对路径；审计对象 → 通用标识
- `docs/vector-embedding-provider-local-runbook.md`：示例模型路径 → `%USERPROFILE%` 模式
