using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Service.Infrastructure;
using ContextCore.Service.Security;
using Microsoft.AspNetCore.Mvc;

namespace ContextCore.Service.Endpoints;

// ===========================================================================
// Learning Artifact Plane API —— Learning 控制面（WP-K）
//
// 把 Learning Artifact Plane（DatasetSnapshot 工件 / Decision Evidence 记录 /
// 校准导出）暴露为可操作界面：
// - GET  /api/learning/artifacts/{snapshotId}  按快照 ID 点查工件（Replay 重建入口）
// - GET  /api/learning/artifacts               按工作区列出最近 N 个工件
// - POST /api/learning/artifacts/export        触发训练数据导出 + 工件落库
// - GET  /api/learning/decisions/{decisionId}  决策记录点查（Decision Evidence 审计）
//
// 设计原则：
// 1. 全部端点要求 Operator 角色（Learning 数据属系统内部治理面）。
// 2. 工作区取自请求上下文（IWorkspaceContextAccessor），客户端不得指定其他租户。
// 3. 处理器提取为 internal static 方法，可直接单元测试（DefaultHttpContext 执行 IResult）。
// 4. 依赖未注册时返回 503（不静默降级）。
// ===========================================================================

/// <summary>
/// Learning Artifact Plane API 端点。
/// </summary>
internal static class LearningArtifactEndpoints
{
    private const string Tag = "Learning";

    public static IEndpointRouteBuilder MapLearningArtifactEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/learning").WithTags(Tag);

        // ── 按快照 ID 点查工件（Replay 重建入口）─────────────────────────
        group.MapGet("/artifacts/{snapshotId}", GetArtifactAsync)
            .WithName("GetLearningArtifact")
            .RequireWorkspaceRole(WorkspaceRole.Operator)
            .WithSummary("按快照 ID 点查数据集快照工件（完整性 / 血缘 / 内容哈希；Replay 重建入口）")
            .Produces<DatasetSnapshotArtifact>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // ── 按工作区列出最近工件 ─────────────────────────────────────────
        group.MapGet("/artifacts", ListArtifactsAsync)
            .WithName("ListLearningArtifacts")
            .RequireWorkspaceRole(WorkspaceRole.Operator)
            .WithSummary("列出工作区最近 N 个数据集快照工件（按入库时间倒序）")
            .Produces<LearningArtifactListResponse>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // ── 触发训练数据导出 + 工件落库 ──────────────────────────────────
        group.MapPost("/artifacts/export", ExportAndStoreAsync)
            .WithName("ExportLearningArtifact")
            .RequireWorkspaceRole(WorkspaceRole.Operator)
            .WithSummary("触发训练数据导出（DatasetSnapshot + 完整性报告）并落库为工件")
            .Produces<DatasetSnapshotReport>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // ── 决策记录点查（Decision Evidence 审计）────────────────────────
        group.MapGet("/decisions/{decisionId}", GetDecisionAsync)
            .WithName("GetLearningDecision")
            .RequireWorkspaceRole(WorkspaceRole.Operator)
            .WithSummary("按稳定主键点查决策记录（Decision Evidence Plane durable 归档审计）")
            .Produces<ContextDecisionRecord>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    /// <summary>按快照 ID 点查工件（工作区取自请求上下文；客户端不得指定其他租户）。</summary>
    internal static async Task<IResult> GetArtifactAsync(
        [FromServices] ILearningArtifactStore artifactStore,
        [FromServices] IWorkspaceContextAccessor workspaceAccessor,
        string snapshotId)
    {
        if (artifactStore is null)
        {
            return Results.Json(new ContextCoreErrorResponse { Message = "Learning Artifact Store 未注册。" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var workspaceId = workspaceAccessor.Current?.WorkspaceId;
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return Results.Json(new ContextCoreErrorResponse { Message = "无法解析工作区（认证上下文缺失）。" }, statusCode: StatusCodes.Status403Forbidden);
        }

        var artifact = await artifactStore.GetAsync(workspaceId, snapshotId);
        return artifact is null
            ? Results.Json(new ContextCoreErrorResponse { Message = $"快照工件不存在：{snapshotId}。" }, statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(artifact);
    }

    /// <summary>按工作区列出最近 N 个工件。</summary>
    internal static async Task<IResult> ListArtifactsAsync(
        [FromServices] ILearningArtifactStore artifactStore,
        [FromServices] IWorkspaceContextAccessor workspaceAccessor,
        int take = 20)
    {
        if (artifactStore is null)
        {
            return Results.Json(new ContextCoreErrorResponse { Message = "Learning Artifact Store 未注册。" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var workspaceId = workspaceAccessor.Current?.WorkspaceId;
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return Results.Json(new ContextCoreErrorResponse { Message = "无法解析工作区（认证上下文缺失）。" }, statusCode: StatusCodes.Status403Forbidden);
        }

        var artifacts = await artifactStore.ListRecentAsync(workspaceId, take > 0 ? take : 20);
        return Results.Ok(new LearningArtifactListResponse { Entries = artifacts });
    }

    /// <summary>触发训练数据导出（DatasetSnapshot）并落库为工件。</summary>
    internal static async Task<IResult> ExportAndStoreAsync(
        [FromServices] ITrainingDataExporter exporter,
        [FromServices] ILearningArtifactStore artifactStore,
        [FromServices] IWorkspaceContextAccessor workspaceAccessor,
        LearningArtifactExportRequest request)
    {
        if (exporter is null || artifactStore is null)
        {
            return Results.Json(new ContextCoreErrorResponse { Message = "训练数据导出器或工件存储未注册。" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var workspaceId = workspaceAccessor.Current?.WorkspaceId;
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return Results.Json(new ContextCoreErrorResponse { Message = "无法解析工作区（认证上下文缺失）。" }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (string.IsNullOrWhiteSpace(request?.OutputDirectory))
        {
            return Results.Json(new ContextCoreErrorResponse { Message = "OutputDirectory 必填。" }, statusCode: StatusCodes.Status400BadRequest);
        }

        var export = await exporter.ExportAsync(new TrainingDataExportRequest
        {
            WorkspaceId = workspaceId,
            CollectionId = request.CollectionId,
            Since = request.Since,
            Until = request.Until,
            DecisionId = request.DecisionId,
            ModelArtifactId = request.ModelArtifactId,
            OutputDirectory = request.OutputDirectory,
            Take = request.Take
        });

        // 快照工件落库（Learning Artifact Plane 持久化；SnapshotId 确定性 → 可重建）。
        if (export.DatasetSnapshot is { } snapshot)
        {
            await artifactStore.SaveAsync(new DatasetSnapshotArtifact
            {
                Snapshot = snapshot,
                DataFilePath = export.DataFilePath,
                ManifestFilePath = export.ManifestFilePath,
                StoredAt = DateTimeOffset.UtcNow
            });
        }

        return Results.Ok(export.DatasetSnapshot);
    }

    /// <summary>决策记录点查（Decision Evidence 审计；工作区取请求上下文）。</summary>
    internal static async Task<IResult> GetDecisionAsync(
        [FromServices] IDecisionTraceStore decisionTraceStore,
        [FromServices] IWorkspaceContextAccessor workspaceAccessor,
        string decisionId)
    {
        if (decisionTraceStore is null)
        {
            return Results.Json(new ContextCoreErrorResponse { Message = "决策记录存储未注册。" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var workspaceId = workspaceAccessor.Current?.WorkspaceId;
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return Results.Json(new ContextCoreErrorResponse { Message = "无法解析工作区（认证上下文缺失）。" }, statusCode: StatusCodes.Status403Forbidden);
        }

        var record = await decisionTraceStore.GetAsync(workspaceId, workspaceId, decisionId);
        return record is null
            ? Results.Json(new ContextCoreErrorResponse { Message = $"决策记录不存在：{decisionId}。" }, statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(record);
    }
}

/// <summary>工件列表响应。</summary>
public sealed class LearningArtifactListResponse
{
    public IReadOnlyList<DatasetSnapshotArtifact> Entries { get; init; } = Array.Empty<DatasetSnapshotArtifact>();
}

/// <summary>导出请求体。</summary>
public sealed class LearningArtifactExportRequest
{
    public string? CollectionId { get; init; }
    public DateTimeOffset? Since { get; init; }
    public DateTimeOffset? Until { get; init; }
    public string? DecisionId { get; init; }
    public string? ModelArtifactId { get; init; }
    public int Take { get; init; }
    public required string OutputDirectory { get; init; }
}
