using System.Security.Cryptography;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Inference.Onnx;
using ContextCore.Service.Infrastructure;
using ContextCore.Service.Security;
using Microsoft.AspNetCore.Mvc;

namespace ContextCore.Service.Endpoints;

// ===========================================================================
// P0-6：Model Control Plane API
//
// 目标（对齐 P0-6 Model Control Plane API 规范）：
//   提供完整的模型生命周期管理 REST API：
//     - 模型注册（upload artifact 或指定路径）
//     - 模型验证（schema binding / calibration binding / ONNX 格式）
//     - 模型预热（warmup）
//     - 影子模式运行（Champion/Challenger，不替换 active）
//     - 模型激活（热切换）
//     - 模型回滚（回到上一个 active 模型）
//     - 模型退役
//     - 当前 active 模型查询 / 全量已注册模型列举 / 单模型详情
//     - readiness 检查（模型是否已加载且可推理）
//     - 节点一致性报告（HA 多节点 active model 对账）
//     - 激活审计历史查询
//
// 设计原则：
//   1. 所有端点遵循 ContextCore Minimal API 模式（IEndpointRouteBuilder 扩展方法）。
//   2. 非 RealModel 模式下，IModelActivationManager 未注册 → 激活/warmup/shadow/rollback 端点返回 503。
//   3. 注册/列举/审计端点在所有模式下可用（依赖 IModelArtifactRegistry / IModelActivationAuditStore）。
//   4. 失败返回 ContextCoreErrorResponse，与 AdminEndpoints / HealthEndpoints 一致。
//   5. 不抛异常：激活失败由 ModelActivationResult.Error 携带，转 400/503。
// ===========================================================================

/// <summary>
/// P0-6：Model Control Plane API 端点。
/// </summary>
internal static class ModelControlPlaneEndpoints
{
    private const string Tag = "ModelControlPlane";

    public static IEndpointRouteBuilder MapModelControlPlaneEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/models").WithTags(Tag);

        // ── 模型注册 ───────────────────────────────────────────────────
        group.MapPost("/register", async Task<IResult> (
            RegisterModelRequest request,
            IModelArtifactRegistry registry,
            IModelActivationAuditStore auditStore,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.ModelArtifactId)
                || string.IsNullOrWhiteSpace(request.ModelName)
                || string.IsNullOrWhiteSpace(request.ModelVersion)
                || string.IsNullOrWhiteSpace(request.FeatureSchemaVersion)
                || string.IsNullOrWhiteSpace(request.CalibrationVersion))
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext, string.Empty, "models.register",
                    "ModelArtifactId / ModelName / ModelVersion / FeatureSchemaVersion / CalibrationVersion 均为必填。",
                    field: "request");
            }

            // 计算 content_hash：调用方提供 ArtifactPath 时计算文件 SHA-256；否则使用请求提供的 ContentHash。
            string contentHash;
            string? artifactPath = request.ArtifactPath;
            if (!string.IsNullOrWhiteSpace(request.ArtifactPath) && File.Exists(request.ArtifactPath))
            {
                try
                {
                    contentHash = await ComputeSha256Async(request.ArtifactPath, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return ContextCoreHttpResultMapper.InternalError(
                        httpContext, string.Empty, "models.register",
                        $"计算 content_hash 失败：{ex.GetType().Name}: {ex.Message}");
                }
            }
            else if (!string.IsNullOrWhiteSpace(request.ContentHash))
            {
                contentHash = request.ContentHash;
            }
            else
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext, string.Empty, "models.register",
                    "ArtifactPath 文件不存在或未提供 ContentHash；至少需提供其一。",
                    field: "artifact_path_or_content_hash");
            }

            var descriptor = new ModelArtifactDescriptor
            {
                ModelArtifactId = request.ModelArtifactId,
                ModelName = request.ModelName,
                ModelVersion = request.ModelVersion,
                FeatureSchemaVersion = request.FeatureSchemaVersion,
                CalibrationVersion = request.CalibrationVersion,
                EngineKind = request.EngineKind,
                ContentHash = contentHash,
                ArtifactPath = artifactPath,
                Description = request.Description,
                RegisteredAt = DateTimeOffset.UtcNow
            };

            try
            {
                await registry.RegisterAsync(descriptor, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext, string.Empty, "models.register", ex.Message);
            }

            // 审计：注册事件
            await AppendAuditAsync(auditStore, descriptor, ModelActivationOperation.Register,
                succeeded: true, previousModelArtifactId: null,
                request.Operator, request.Reason, httpContext).ConfigureAwait(false);

            return Results.Ok(ToDescriptorResponse(descriptor));
        })
        .WithName("RegisterModel")
        .RequireWorkspacePermission(WorkspacePermission.ModelRegister)
        .WithSummary("注册新模型工件（指定 artifact 路径或显式提供 content_hash）")
        .Produces<ModelArtifactDescriptorResponse>(StatusCodes.Status200OK)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status500InternalServerError);

        // ── 模型验证 ───────────────────────────────────────────────────
        group.MapPost("/{id}/validate", async Task<IResult> (
            string id,
            IModelArtifactRegistry registry,
            IFeatureRegistry featureRegistry,
            ICalibrationValidator calibrationValidator,
            [FromServices] ICalibrationService? calibrationService,
            IModelActivationAuditStore auditStore,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var descriptor = await registry.GetAsync(id, ct).ConfigureAwait(false);
            if (descriptor is null)
            {
                return ContextCoreHttpResultMapper.NotFound(
                    httpContext, string.Empty, "models.validate",
                    $"未找到 ModelArtifactId='{id}'。");
            }

            var errors = new List<string>();

            // schema 存在性验证
            var schema = featureRegistry.Get(descriptor.FeatureSchemaVersion);
            if (schema is null)
            {
                errors.Add($"特征 schema 版本 '{descriptor.FeatureSchemaVersion}' 未在 IFeatureRegistry 中注册。");
            }

            // 校准参数验证（仅当 ICalibrationService 可用且 descriptor 引用了校准版本时）
            CalibrationValidationResult? calResult = null;
            if (calibrationService is not null)
            {
                var parameters = calibrationService.GetParameters(descriptor.ModelArtifactId)
                    ?? calibrationService.GetParameters(descriptor.ModelName);
                if (parameters is not null)
                {
                    calResult = calibrationValidator.Validate(parameters, descriptor.ModelArtifactId);
                    if (!calResult.IsValid)
                    {
                        errors.Add($"校准验证失败：{calResult.Error}");
                    }
                }
            }

            // ONNX 格式验证：仅在 ArtifactPath 指向真实文件时尝试读取首字节 magic number
            //（16 字节后跟 "ONNX" 标识）。失败时不阻止激活（ONNX session 创建时由工厂捕获）。
            var onnxFormatOk = true;
            string? onnxFormatError = null;
            if (!string.IsNullOrWhiteSpace(descriptor.ArtifactPath) && File.Exists(descriptor.ArtifactPath))
            {
                try
                {
                    onnxFormatOk = await CheckOnnxMagicAsync(descriptor.ArtifactPath, ct).ConfigureAwait(false);
                    if (!onnxFormatOk)
                    {
                        onnxFormatError = "ArtifactPath 指向的文件不是有效的 ONNX 格式（magic number 不匹配）。";
                        errors.Add(onnxFormatError);
                    }
                }
                catch (Exception ex)
                {
                    onnxFormatError = $"读取 ONNX 文件失败：{ex.GetType().Name}: {ex.Message}";
                    errors.Add(onnxFormatError);
                    onnxFormatOk = false;
                }
            }

            var succeeded = errors.Count == 0;
            await AppendAuditAsync(auditStore, descriptor, ModelActivationOperation.Validate,
                succeeded, previousModelArtifactId: null,
                null, string.Join("; ", errors), httpContext,
                errorMessage: succeeded ? null : string.Join("; ", errors)).ConfigureAwait(false);

            return Results.Ok(new ValidateModelResponse
            {
                ModelArtifactId = id,
                Succeeded = succeeded,
                Errors = errors,
                SchemaRegistered = schema is not null,
                CalibrationValidation = calResult is null ? null : new CalibrationValidationResponse
                {
                    IsValid = calResult.IsValid,
                    Error = calResult.Error,
                    ErrorCount = calResult.ErrorCount,
                    WarningCount = calResult.WarningCount
                },
                OnnxFormatValid = onnxFormatOk
            });
        })
        .WithName("ValidateModel")
        .WithSummary("验证模型（schema 存在性 / calibration 参数 / ONNX 格式）")
        .Produces<ValidateModelResponse>(StatusCodes.Status200OK)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound);

        // ── 模型预热 ───────────────────────────────────────────────────
        group.MapPost("/{id}/warmup", async Task<IResult> (
            string id,
            IModelArtifactRegistry registry,
            [FromServices] IModelActivationManager? activationManager,
            IModelActivationAuditStore auditStore,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (activationManager is null)
            {
                return ContextCoreHttpResultMapper.StorageUnavailable(
                    httpContext, string.Empty, "models.warmup",
                    "IModelActivationManager 未注册（当前非 RealModel 模式）；无法执行 warmup。");
            }

            var descriptor = await registry.GetAsync(id, ct).ConfigureAwait(false);
            if (descriptor is null)
            {
                return ContextCoreHttpResultMapper.NotFound(
                    httpContext, string.Empty, "models.warmup",
                    $"未找到 ModelArtifactId='{id}'。");
            }

            // warmup 通过 ActivateAsync 实现：ActivateCoreAsync 内部会执行 Golden Probe warmup，
            // 把引擎切换为 ActiveEngine。如果只想 warmup 而不切换 active，应使用 shadow 端点。
            // 这里保留 activate 语义以让 warmup 端点真正触发加载（与 readiness 端点配合）。
            var options = new OnnxInferenceEngineOptions
            {
                InputTensorName = "input",
                ScoreOutputName = "score",
                EnableWarmup = true
            };
            var result = await activationManager.ActivateAsync(id, options, ct).ConfigureAwait(false);
            await AppendAuditAsync(auditStore, descriptor, ModelActivationOperation.Warmup,
                result.Success, activationManager.ActiveDescriptor?.ModelArtifactId,
                null, result.Error, httpContext, errorMessage: result.Error).ConfigureAwait(false);

            if (!result.Success)
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext, string.Empty, "models.warmup",
                    $"warmup 失败：{result.Error}");
            }

            return Results.Ok(new WarmupModelResponse
            {
                ModelArtifactId = id,
                Succeeded = true,
                CalibrationValid = result.CalibrationValidation?.IsValid ?? true,
                SchemaValid = result.SchemaValidationError is null
            });
        })
        .WithName("WarmupModel")
        .WithSummary("预热模型（执行 Golden Probe warmup，激活引擎）")
        .Produces<WarmupModelResponse>(StatusCodes.Status200OK)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // ── 影子模式运行（Challenger）─────────────────────────────────
        group.MapPost("/{id}/shadow", async Task<IResult> (
            string id,
            ShadowModelRequest request,
            IModelArtifactRegistry registry,
            [FromServices] ShadowModelManager? shadowManager,
            IModelActivationAuditStore auditStore,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (shadowManager is null)
            {
                return ContextCoreHttpResultMapper.StorageUnavailable(
                    httpContext, string.Empty, "models.shadow",
                    "ShadowModelManager 未注册（当前非 RealModel 模式）；无法进入影子模式。");
            }

            var descriptor = await registry.GetAsync(id, ct).ConfigureAwait(false);
            if (descriptor is null)
            {
                return ContextCoreHttpResultMapper.NotFound(
                    httpContext, string.Empty, "models.shadow",
                    $"未找到 ModelArtifactId='{id}'。");
            }

            var options = request.Options ?? new OnnxInferenceEngineOptions
            {
                InputTensorName = "input",
                ScoreOutputName = "score",
                EnableWarmup = true
            };
            var result = await shadowManager.ActivateShadowAsync(descriptor, options, ct).ConfigureAwait(false);
            await AppendAuditAsync(auditStore, descriptor, ModelActivationOperation.Shadow,
                result.Success, previousModelArtifactId: null,
                request.Operator, request.Reason, httpContext, errorMessage: result.Error).ConfigureAwait(false);

            if (!result.Success)
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext, string.Empty, "models.shadow",
                    $"Challenger 加载失败：{result.Error}");
            }

            return Results.Ok(new ShadowModelResponse
            {
                ModelArtifactId = id,
                Succeeded = true,
                ChampionModelArtifactId = (registry as dynamic)?.ActiveDescriptor?.ModelArtifactId as string,
                Note = "Challenger 已加载；推理结果不返回给用户，仅用于 Champion/Challenger 对比。"
            });
        })
        .WithName("ShadowModel")
        .WithSummary("加载 Challenger 模型到影子模式（不替换 active；推理结果仅用于对比）")
        .Produces<ShadowModelResponse>(StatusCodes.Status200OK)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // ── 模型激活（热切换）────────────────────────────────────────
        group.MapPost("/{id}/activate", async Task<IResult> (
            string id,
            ActivateModelRequest request,
            IModelArtifactRegistry registry,
            [FromServices] IModelActivationManager? activationManager,
            IModelActivationAuditStore auditStore,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (activationManager is null)
            {
                return ContextCoreHttpResultMapper.StorageUnavailable(
                    httpContext, string.Empty, "models.activate",
                    "IModelActivationManager 未注册（当前非 RealModel 模式）；无法激活。");
            }

            var descriptor = await registry.GetAsync(id, ct).ConfigureAwait(false);
            if (descriptor is null)
            {
                return ContextCoreHttpResultMapper.NotFound(
                    httpContext, string.Empty, "models.activate",
                    $"未找到 ModelArtifactId='{id}'。");
            }

            // 记录 previous active（用于审计），必须在激活前捕获。
            var previousActiveId = activationManager.ActiveDescriptor?.ModelArtifactId;

            var options = request.Options ?? new OnnxInferenceEngineOptions
            {
                InputTensorName = "input",
                ScoreOutputName = "score",
                EnableWarmup = true
            };
            var result = await activationManager.ActivateAsync(id, options, ct).ConfigureAwait(false);
            await AppendAuditAsync(auditStore, descriptor, ModelActivationOperation.Activate,
                result.Success, previousActiveId,
                request.Operator, request.Reason, httpContext, errorMessage: result.Error).ConfigureAwait(false);

            if (!result.Success)
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext, string.Empty, "models.activate",
                    $"激活失败：{result.Error}");
            }

            return Results.Ok(new ActivateModelResponse
            {
                ModelArtifactId = id,
                Succeeded = true,
                PreviousModelArtifactId = previousActiveId,
                ActiveModelVersion = descriptor.ModelVersion,
                ActiveContentHash = descriptor.ContentHash
            });
        })
        .WithName("ActivateModel")
        .RequireWorkspacePermission(WorkspacePermission.ModelActivate)
        .WithSummary("激活模型（热切换，替换当前 active）")
        .Produces<ActivateModelResponse>(StatusCodes.Status200OK)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // ── 模型回滚 ───────────────────────────────────────────────────
        group.MapPost("/{id}/rollback", async Task<IResult> (
            string id,
            RollbackModelRequest request,
            IModelArtifactRegistry registry,
            [FromServices] IModelActivationManager? activationManager,
            IModelActivationAuditStore auditStore,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (activationManager is null)
            {
                return ContextCoreHttpResultMapper.StorageUnavailable(
                    httpContext, string.Empty, "models.rollback",
                    "IModelActivationManager 未注册（当前非 RealModel 模式）；无法回滚。");
            }

            // 回滚语义：把 {id}（应为 previous 模型）重新激活为 active。
            // 当前 ModelActivationManager 未持久化 previous 列表，调用方需明确指定回滚目标。
            // 这里实现为：调用 ActivateAsync(id) 把指定模型激活，记录审计为 Rollback。
            var descriptor = await registry.GetAsync(id, ct).ConfigureAwait(false);
            if (descriptor is null)
            {
                return ContextCoreHttpResultMapper.NotFound(
                    httpContext, string.Empty, "models.rollback",
                    $"未找到 ModelArtifactId='{id}'。");
            }

            var previousActiveId = activationManager.ActiveDescriptor?.ModelArtifactId;
            var options = request.Options ?? new OnnxInferenceEngineOptions
            {
                InputTensorName = "input",
                ScoreOutputName = "score",
                EnableWarmup = true
            };
            var result = await activationManager.ActivateAsync(id, options, ct).ConfigureAwait(false);
            await AppendAuditAsync(auditStore, descriptor, ModelActivationOperation.Rollback,
                result.Success, previousActiveId,
                request.Operator, request.Reason, httpContext, errorMessage: result.Error).ConfigureAwait(false);

            if (!result.Success)
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext, string.Empty, "models.rollback",
                    $"回滚失败：{result.Error}");
            }

            return Results.Ok(new RollbackModelResponse
            {
                ModelArtifactId = id,
                Succeeded = true,
                PreviousModelArtifactId = previousActiveId,
                ActiveModelVersion = descriptor.ModelVersion
            });
        })
        .WithName("RollbackModel")
        .WithSummary("回滚到指定模型（重新激活为 active）")
        .Produces<RollbackModelResponse>(StatusCodes.Status200OK)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // ── 模型退役 ───────────────────────────────────────────────────
        group.MapPost("/{id}/retire", async Task<IResult> (
            string id,
            RetireModelRequest request,
            IModelArtifactRegistry registry,
            [FromServices] IModelActivationManager? activationManager,
            [FromServices] ShadowModelManager? shadowManager,
            IModelActivationAuditStore auditStore,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var descriptor = await registry.GetAsync(id, ct).ConfigureAwait(false);
            if (descriptor is null)
            {
                return ContextCoreHttpResultMapper.NotFound(
                    null, string.Empty, "models.retire",
                    $"未找到 ModelArtifactId='{id}'。");
            }

            var previousActiveId = activationManager?.ActiveDescriptor?.ModelArtifactId;
            var succeeded = true;
            string? errorMessage = null;

            // 若退役的是当前 active 模型，且 activationManager 已注册：
            // 当前 ModelActivationManager 未提供 DeactivateAsync；只能通过激活其他模型间接实现退役。
            // 退役语义在此实现为：若退役模型为当前 active，返回 400（需要先激活其他模型或回滚）；
            // 若退役的是 shadow challenger，清除 shadow。
            if (activationManager is not null
                && string.Equals(activationManager.ActiveDescriptor?.ModelArtifactId, id, StringComparison.Ordinal))
            {
                succeeded = false;
                errorMessage = "不能直接退役当前 active 模型；请先激活其他模型或回滚到上一个版本。";
            }
            else if (shadowManager is not null
                && string.Equals(shadowManager.ShadowDescriptor?.ModelArtifactId, id, StringComparison.Ordinal))
            {
                await shadowManager.ClearShadowAsync().ConfigureAwait(false);
            }

            await AppendAuditAsync(auditStore, descriptor, ModelActivationOperation.Retire,
                succeeded, previousActiveId,
                request.Operator, request.Reason, httpContext, errorMessage: errorMessage).ConfigureAwait(false);

            if (!succeeded)
            {
                return ContextCoreHttpResultMapper.InvalidRequest(
                    httpContext, string.Empty, "models.retire", errorMessage!);
            }

            return Results.Ok(new RetireModelResponse
            {
                ModelArtifactId = id,
                Succeeded = true,
                Note = "模型已退役；如该模型为 Challenger，已清除 shadow 引擎。"
            });
        })
        .WithName("RetireModel")
        .RequireWorkspacePermission(WorkspacePermission.ModelRegister)
        .WithSummary("退役模型（不允许直接退役当前 active 模型）")
        .Produces<RetireModelResponse>(StatusCodes.Status200OK)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound);

        // ── 获取当前 active 模型 ────────────────────────────────────────
        group.MapGet("/active", ([FromServices] IModelActivationManager? activationManager, [FromServices] ShadowModelManager? shadowManager) =>
        {
            var activeDescriptor = activationManager?.ActiveDescriptor;
            var shadowDescriptor = shadowManager?.ShadowDescriptor;
            if (activeDescriptor is null && shadowDescriptor is null)
            {
                return Results.Ok(new ActiveModelResponse
                {
                    HasActive = false,
                    HasShadow = false,
                    Message = "无 active 模型（未激活或非 RealModel 模式）。"
                });
            }

            return Results.Ok(new ActiveModelResponse
            {
                HasActive = activeDescriptor is not null,
                Active = activeDescriptor is null ? null : ToDescriptorResponse(activeDescriptor),
                HasShadow = shadowDescriptor is not null,
                Shadow = shadowDescriptor is null ? null : ToDescriptorResponse(shadowDescriptor)
            });
        })
        .WithName("GetActiveModel")
        .WithSummary("获取当前 active 模型（Champion）与 shadow 模型（Challenger）信息")
        .Produces<ActiveModelResponse>(StatusCodes.Status200OK);

        // ── 列出所有已注册模型 ────────────────────────────────────────
        group.MapGet("/", async (IModelArtifactRegistry registry, CancellationToken ct) =>
        {
            var list = await registry.ListAllAsync(ct).ConfigureAwait(false);
            return Results.Ok(new ListModelsResponse
            {
                Models = list.Select(ToDescriptorResponse).ToArray(),
                Count = list.Count
            });
        })
        .WithName("ListModels")
        .WithSummary("列出所有已注册模型工件描述符")
        .Produces<ListModelsResponse>(StatusCodes.Status200OK);

        // ── 获取模型详情 ───────────────────────────────────────────────
        group.MapGet("/{id}", async Task<IResult> (
            string id,
            IModelArtifactRegistry registry,
            CancellationToken ct) =>
        {
            var descriptor = await registry.GetAsync(id, ct).ConfigureAwait(false);
            if (descriptor is null)
            {
                return ContextCoreHttpResultMapper.NotFound(
                    null, string.Empty, "models.get",
                    $"未找到 ModelArtifactId='{id}'。");
            }
            return Results.Ok(ToDescriptorResponse(descriptor));
        })
        .WithName("GetModel")
        .WithSummary("获取模型工件描述符详情")
        .Produces<ModelArtifactDescriptorResponse>(StatusCodes.Status200OK)
        .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound);

        // ── readiness 检查 ─────────────────────────────────────────────
        group.MapGet("/ready", ([FromServices] IModelActivationManager? activationManager) =>
        {
            var activeDescriptor = activationManager?.ActiveDescriptor;
            // ActiveDescriptor != null 表示 ActivateAsync 已成功（含 Golden Probe warmup），
            // 因此引擎已加载且 warmup 通过。
            var ready = activeDescriptor is not null;
            return Results.Ok(new ModelReadinessResponse
            {
                Ready = ready,
                HasActiveModel = activeDescriptor is not null,
                ActiveModelArtifactId = activeDescriptor?.ModelArtifactId,
                ActiveModelVersion = activeDescriptor?.ModelVersion,
                WarmupCompleted = activeDescriptor is not null,
                Message = ready
                    ? "active 模型已加载且 warmup 通过。"
                    : "无 active 模型（未激活 / 非 RealModel 模式 / warmup 未通过）。"
            });
        })
        .WithName("GetModelReadiness")
        .WithSummary("readiness 检查：模型是否已加载且可推理")
        .Produces<ModelReadinessResponse>(StatusCodes.Status200OK);

        // ── 节点一致性报告 ─────────────────────────────────────────────
        group.MapGet("/consistency", ([FromServices] IModelActivationManager? activationManager, HttpContext httpContext) =>
        {
            var nodeId = ResolveNodeId();
            var activeDescriptor = activationManager?.ActiveDescriptor;
            return Results.Ok(new NodeConsistencyResponse
            {
                NodeId = nodeId,
                ActiveModelArtifactId = activeDescriptor?.ModelArtifactId,
                ActiveModelVersion = activeDescriptor?.ModelVersion,
                ActiveContentHash = activeDescriptor?.ContentHash,
                HasActiveModel = activeDescriptor is not null,
                CheckedAt = DateTimeOffset.UtcNow,
                Note = "本节点报告本地 active 模型；HA 多节点对账请聚合各节点响应的 ActiveContentHash。"
            });
        })
        .WithName("GetNodeConsistency")
        .WithSummary("节点一致性报告：本节点当前 active 模型（HA 多节点对账用）")
        .Produces<NodeConsistencyResponse>(StatusCodes.Status200OK);

        // ── 激活审计历史查询 ──────────────────────────────────────────
        group.MapGet("/{id}/audit", async Task<IResult> (
            string id,
            IModelActivationAuditStore auditStore,
            CancellationToken ct) =>
        {
            var entries = await auditStore.ListByModelAsync(id, ct).ConfigureAwait(false);
            return Results.Ok(new ModelAuditHistoryResponse
            {
                ModelArtifactId = id,
                Entries = entries.Select(ToAuditResponse).ToArray(),
                Count = entries.Count
            });
        })
        .WithName("GetModelAuditHistory")
        .WithSummary("查询模型激活审计历史")
        .Produces<ModelAuditHistoryResponse>(StatusCodes.Status200OK);

        return app;
    }

    // -----------------------------------------------------------------------
    // 辅助方法
    // -----------------------------------------------------------------------

    private static async ValueTask AppendAuditAsync(
        IModelActivationAuditStore auditStore,
        ModelArtifactDescriptor descriptor,
        ModelActivationOperation operation,
        bool succeeded,
        string? previousModelArtifactId,
        string? @operator,
        string? reason,
        HttpContext httpContext,
        string? errorMessage = null)
    {
        // 契约"不抛异常"：审计失败仅记录到日志，不影响主流程。
        try
        {
            await auditStore.AppendAsync(new ModelActivationAuditEntry
            {
                AuditId = Guid.NewGuid().ToString("N"),
                Timestamp = DateTimeOffset.UtcNow,
                ModelArtifactId = descriptor.ModelArtifactId,
                ModelName = descriptor.ModelName,
                Operation = operation,
                PreviousModelArtifactId = previousModelArtifactId,
                Operator = @operator ?? httpContext.User?.Identity?.Name,
                Reason = reason,
                Succeeded = succeeded,
                ErrorMessage = errorMessage,
                NodeId = ResolveNodeId()
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort：审计写入失败不影响激活主流程。
        }
    }

    private static string ResolveNodeId()
        => Environment.GetEnvironmentVariable("CONTEXTCORE_NODE_ID")
            ?? Environment.GetEnvironmentVariable("INSTANCE_ID")
            ?? Environment.MachineName;

    private static async ValueTask<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        using var sha = SHA256.Create();
        var hashBytes = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return "sha256:" + Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// 校验 ONNX 文件 magic number：前 4 字节应为 0x08，随后是 4 字节长度，再后是 "ONNX\0"。
    /// 这里采用宽松校验：在文件前 32 字节中查找 ASCII "ONNX" 子串。
    /// </summary>
    private static async ValueTask<bool> CheckOnnxMagicAsync(string filePath, CancellationToken cancellationToken)
    {
        var buffer = new byte[32];
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        var read = await stream.ReadAsync(buffer.AsMemory(0, 32), cancellationToken).ConfigureAwait(false);
        if (read < 8)
        {
            return false;
        }

        // ONNX 文件以 protobuf message 包装：[0]=8 (field 1 varint length), [1..4]=长度小端，
        // 随后是 "ONNX\0" 5 字节字符串（0x4F 0x4E 0x58 0x00 在 buffer[5..8] 区域）。
        // 宽松匹配：搜索 buffer 中的 "ONNX"（0x4F 0x4E 0x58 0x00）。
        for (var i = 0; i <= read - 4; i++)
        {
            if (buffer[i] == 0x4F && buffer[i + 1] == 0x4E && buffer[i + 2] == 0x58 && buffer[i + 3] == 0x00)
            {
                return true;
            }
        }
        return false;
    }

    private static ModelArtifactDescriptorResponse ToDescriptorResponse(ModelArtifactDescriptor descriptor)
        => new()
        {
            ModelArtifactId = descriptor.ModelArtifactId,
            ModelName = descriptor.ModelName,
            ModelVersion = descriptor.ModelVersion,
            FeatureSchemaVersion = descriptor.FeatureSchemaVersion,
            CalibrationVersion = descriptor.CalibrationVersion,
            EngineKind = descriptor.EngineKind.ToString(),
            ContentHash = descriptor.ContentHash,
            ArtifactPath = descriptor.ArtifactPath,
            Description = descriptor.Description,
            RegisteredAt = descriptor.RegisteredAt
        };

    private static ModelActivationAuditEntryResponse ToAuditResponse(ModelActivationAuditEntry entry)
        => new()
        {
            AuditId = entry.AuditId,
            Timestamp = entry.Timestamp,
            ModelArtifactId = entry.ModelArtifactId,
            ModelName = entry.ModelName,
            Operation = entry.Operation.ToString(),
            PreviousModelArtifactId = entry.PreviousModelArtifactId,
            Operator = entry.Operator,
            Reason = entry.Reason,
            Succeeded = entry.Succeeded,
            ErrorMessage = entry.ErrorMessage,
            NodeId = entry.NodeId
        };
}

// ---------------------------------------------------------------------------
// P0-6：Model Control Plane API 请求 / 响应 DTO
// ---------------------------------------------------------------------------

/// <summary>注册模型请求。</summary>
public sealed class RegisterModelRequest
{
    /// <summary>模型工件 ID（全局唯一，推荐格式：{modelName}-{version}-{shortHash}）。</summary>
    public string ModelArtifactId { get; init; } = string.Empty;

    /// <summary>逻辑模型名。</summary>
    public string ModelName { get; init; } = string.Empty;

    /// <summary>模型版本号（语义化版本）。</summary>
    public string ModelVersion { get; init; } = string.Empty;

    /// <summary>特征 schema 版本号（必须已在 IFeatureRegistry 中注册）。</summary>
    public string FeatureSchemaVersion { get; init; } = string.Empty;

    /// <summary>校准版本号（必须已在 ICalibrationService 中注册）。</summary>
    public string CalibrationVersion { get; init; } = string.Empty;

    /// <summary>推理引擎类型（默认 RealModel）。</summary>
    public InferenceEngineKind EngineKind { get; init; } = InferenceEngineKind.RealModel;

    /// <summary>模型工件存储路径（onnx 文件路径）；提供时由 server 计算 SHA-256。</summary>
    public string? ArtifactPath { get; init; }

    /// <summary>显式提供的 content_hash（ArtifactPath 不存在时必填）。</summary>
    public string? ContentHash { get; init; }

    /// <summary>可选的模型描述。</summary>
    public string? Description { get; init; }

    /// <summary>操作发起者（用于审计）。</summary>
    public string? Operator { get; init; }

    /// <summary>注册原因（用于审计）。</summary>
    public string? Reason { get; init; }
}

/// <summary>模型工件描述符响应。</summary>
public sealed class ModelArtifactDescriptorResponse
{
    public string ModelArtifactId { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public string ModelVersion { get; init; } = string.Empty;
    public string FeatureSchemaVersion { get; init; } = string.Empty;
    public string CalibrationVersion { get; init; } = string.Empty;
    public string EngineKind { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public string? ArtifactPath { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset RegisteredAt { get; init; }
}

/// <summary>验证模型响应。</summary>
public sealed class ValidateModelResponse
{
    public string ModelArtifactId { get; init; } = string.Empty;
    public bool Succeeded { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public bool SchemaRegistered { get; init; }
    public CalibrationValidationResponse? CalibrationValidation { get; init; }
    public bool OnnxFormatValid { get; init; }
}

/// <summary>校准验证结果摘要。</summary>
public sealed class CalibrationValidationResponse
{
    public bool IsValid { get; init; }
    public string? Error { get; init; }
    public int ErrorCount { get; init; }
    public int WarningCount { get; init; }
}

/// <summary>预热模型响应。</summary>
public sealed class WarmupModelResponse
{
    public string ModelArtifactId { get; init; } = string.Empty;
    public bool Succeeded { get; init; }
    public bool CalibrationValid { get; init; }
    public bool SchemaValid { get; init; }
}

/// <summary>影子模式请求。</summary>
public sealed class ShadowModelRequest
{
    /// <summary>可选的 ONNX 推理配置；为 null 时使用默认（InputTensorName="input", ScoreOutputName="score"）。</summary>
    public OnnxInferenceEngineOptions? Options { get; init; }

    /// <summary>操作发起者（用于审计）。</summary>
    public string? Operator { get; init; }

    /// <summary>进入影子模式原因（用于审计）。</summary>
    public string? Reason { get; init; }
}

/// <summary>影子模式响应。</summary>
public sealed class ShadowModelResponse
{
    public string ModelArtifactId { get; init; } = string.Empty;
    public bool Succeeded { get; init; }
    public string? ChampionModelArtifactId { get; init; }
    public string Note { get; init; } = string.Empty;
}

/// <summary>激活模型请求。</summary>
public sealed class ActivateModelRequest
{
    public OnnxInferenceEngineOptions? Options { get; init; }
    public string? Operator { get; init; }
    public string? Reason { get; init; }
}

/// <summary>激活模型响应。</summary>
public sealed class ActivateModelResponse
{
    public string ModelArtifactId { get; init; } = string.Empty;
    public bool Succeeded { get; init; }
    public string? PreviousModelArtifactId { get; init; }
    public string ActiveModelVersion { get; init; } = string.Empty;
    public string ActiveContentHash { get; init; } = string.Empty;
}

/// <summary>回滚模型请求。</summary>
public sealed class RollbackModelRequest
{
    public OnnxInferenceEngineOptions? Options { get; init; }
    public string? Operator { get; init; }
    public string? Reason { get; init; }
}

/// <summary>回滚模型响应。</summary>
public sealed class RollbackModelResponse
{
    public string ModelArtifactId { get; init; } = string.Empty;
    public bool Succeeded { get; init; }
    public string? PreviousModelArtifactId { get; init; }
    public string ActiveModelVersion { get; init; } = string.Empty;
}

/// <summary>退役模型请求。</summary>
public sealed class RetireModelRequest
{
    public string? Operator { get; init; }
    public string? Reason { get; init; }
}

/// <summary>退役模型响应。</summary>
public sealed class RetireModelResponse
{
    public string ModelArtifactId { get; init; } = string.Empty;
    public bool Succeeded { get; init; }
    public string Note { get; init; } = string.Empty;
}

/// <summary>当前 active + shadow 模型信息。</summary>
public sealed class ActiveModelResponse
{
    public bool HasActive { get; init; }
    public ModelArtifactDescriptorResponse? Active { get; init; }
    public bool HasShadow { get; init; }
    public ModelArtifactDescriptorResponse? Shadow { get; init; }
    public string? Message { get; init; }
}

/// <summary>列出所有已注册模型响应。</summary>
public sealed class ListModelsResponse
{
    public IReadOnlyList<ModelArtifactDescriptorResponse> Models { get; init; } = Array.Empty<ModelArtifactDescriptorResponse>();
    public int Count { get; init; }
}

/// <summary>模型 readiness 响应。</summary>
public sealed class ModelReadinessResponse
{
    public bool Ready { get; init; }
    public bool HasActiveModel { get; init; }
    public string? ActiveModelArtifactId { get; init; }
    public string? ActiveModelVersion { get; init; }
    public bool WarmupCompleted { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>节点一致性响应。</summary>
public sealed class NodeConsistencyResponse
{
    public string NodeId { get; init; } = string.Empty;
    public string? ActiveModelArtifactId { get; init; }
    public string? ActiveModelVersion { get; init; }
    public string? ActiveContentHash { get; init; }
    public bool HasActiveModel { get; init; }
    public DateTimeOffset CheckedAt { get; init; }
    public string Note { get; init; } = string.Empty;
}

/// <summary>审计历史响应。</summary>
public sealed class ModelAuditHistoryResponse
{
    public string ModelArtifactId { get; init; } = string.Empty;
    public IReadOnlyList<ModelActivationAuditEntryResponse> Entries { get; init; } = Array.Empty<ModelActivationAuditEntryResponse>();
    public int Count { get; init; }
}

/// <summary>单条审计记录响应。</summary>
public sealed class ModelActivationAuditEntryResponse
{
    public string AuditId { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
    public string ModelArtifactId { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public string? PreviousModelArtifactId { get; init; }
    public string? Operator { get; init; }
    public string? Reason { get; init; }
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public string? NodeId { get; init; }
}
