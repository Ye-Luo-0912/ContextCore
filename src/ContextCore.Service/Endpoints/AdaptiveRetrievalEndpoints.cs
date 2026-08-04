using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Service.Infrastructure;
using ContextCore.Service.Security;
using Microsoft.AspNetCore.Mvc;

namespace ContextCore.Service.Endpoints;

// ===========================================================================
// Adaptive Retrieval Planner API —— 自适应检索规划器运维面
//
// 目标：
// 为运维提供自适应检索规划器的状态观测与控制接口：
// - 查询当前自适应策略（按计划签名或按规划输入派生签名）；
// - 查询 / 记录检索结果反馈（自适应学习信号）；
// - 清除反馈并重置自适应状态。
//
// 端点：
// GET /api/retrieval/adaptive/policy 当前自适应策略（signature 或输入字段）
// GET /api/retrieval/adaptive/feedback 近期反馈（按签名）
// POST /api/retrieval/adaptive/feedback 记录一条检索结果反馈
// POST /api/retrieval/adaptive/reset 清除反馈 / 重置自适应状态
//
// 设计原则：
// 1. 全部端点要求 Operator 角色（自适应状态属系统内部诊断面）。
// 2. 处理器提取为 internal static 方法，可直接单元测试（DefaultHttpContext 执行 IResult）。
// 3. IAdaptiveRetrievalPlanner 未注册时返回 503（不静默降级）。
// ===========================================================================

/// <summary>
/// 自适应检索规划器 API 端点。
/// </summary>
internal static class AdaptiveRetrievalEndpoints
{
    private const string Tag = "AdaptiveRetrieval";

    public static IEndpointRouteBuilder MapAdaptiveRetrievalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/retrieval/adaptive").WithTags(Tag);

        // ── 查询当前自适应策略 ───────────────────────────────────────────
        group.MapGet("/policy", GetPolicyAsync)
            .WithName("GetAdaptiveRetrievalPolicy")
            .RequireWorkspaceRole(WorkspaceRole.Operator)
            .WithSummary("查询自适应检索策略（预算收敛 / 查询收敛 / 召回增强乘数；signature 或规划输入字段）")
            .Produces<AdaptiveRetrievalPolicy>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // ── 查询近期反馈 ─────────────────────────────────────────────────
        group.MapGet("/feedback", ListFeedbackAsync)
            .WithName("ListAdaptiveRetrievalFeedback")
            .RequireWorkspaceRole(WorkspaceRole.Operator)
            .WithSummary("列出指定计划签名最近 N 条检索结果反馈（按记录时间倒序）")
            .Produces<AdaptiveRetrievalFeedbackListResponse>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // ── 记录一条反馈 ─────────────────────────────────────────────────
        group.MapPost("/feedback", RecordFeedbackAsync)
            .WithName("RecordAdaptiveRetrievalFeedback")
            .RequireWorkspaceRole(WorkspaceRole.Operator)
            .WithSummary("记录一条检索结果反馈（自适应规划器的学习信号）")
            .Produces<AdaptiveRetrievalFeedbackRecordResponse>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // ── 清除反馈 / 重置自适应状态 ───────────────────────────────────
        group.MapPost("/reset", ResetAsync)
            .WithName("ResetAdaptiveRetrieval")
            .RequireWorkspaceRole(WorkspaceRole.Operator)
            .WithSummary("清除检索结果反馈并重置自适应状态（signature 缺省时清除全部）")
            .Produces<AdaptiveRetrievalResetResponse>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    /// <summary>查询当前自适应策略：优先使用 signature 参数，缺省时从规划输入字段派生签名。
    /// 工作区取自请求上下文（IWorkspaceContextAccessor），其余租户维度可经查询参数显式指定
    /// （P0-16：签名必须包含 workspace / collection / purpose / policy / profile / taskClass）。</summary>
    internal static async Task<IResult> GetPolicyAsync(
        [FromServices] IAdaptiveRetrievalPlanner? planner,
        string? signature,
        string? originalTask,
        string? latestAssistantIntent,
        string? goals,
        string? collectionId,
        string? purpose,
        string? policyVersion,
        string? retrievalProfile,
        string? taskClass,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (planner is null)
        {
            return ContextCoreHttpResultMapper.StorageUnavailable(
                httpContext, string.Empty, "retrieval.adaptive.policy",
                "IAdaptiveRetrievalPlanner 未注册。");
        }

        var workspaceId = httpContext.RequestServices.GetService<IWorkspaceContextAccessor>()?.Current?.WorkspaceId;
        var resolvedSignature = ResolveSignature(signature, originalTask, latestAssistantIntent, goals,
            workspaceId, collectionId, purpose, policyVersion, retrievalProfile, taskClass);
        var policy = await planner.GetPolicyForSignatureAsync(resolvedSignature, ct).ConfigureAwait(false);
        return Results.Ok(policy);
    }

    /// <summary>列出指定签名最近 N 条反馈。</summary>
    internal static async Task<IResult> ListFeedbackAsync(
        [FromServices] IAdaptiveRetrievalPlanner? planner,
        string signature,
        int limit,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (planner is null)
        {
            return ContextCoreHttpResultMapper.StorageUnavailable(
                httpContext, string.Empty, "retrieval.adaptive.feedback.list",
                "IAdaptiveRetrievalPlanner 未注册。");
        }
        if (string.IsNullOrWhiteSpace(signature))
        {
            return ContextCoreHttpResultMapper.InvalidRequest(
                httpContext, string.Empty, "retrieval.adaptive.feedback.list",
                "signature 为必填（计划签名）。", field: "signature");
        }

        var entries = await planner.ListFeedbackAsync(signature, limit > 0 ? limit : 20, ct).ConfigureAwait(false);
        return Results.Ok(new AdaptiveRetrievalFeedbackListResponse
        {
            PlanSignature = signature,
            Entries = entries,
            Count = entries.Count
        });
    }

    /// <summary>记录一条检索结果反馈。</summary>
    internal static async Task<IResult> RecordFeedbackAsync(
        [FromServices] IAdaptiveRetrievalPlanner? planner,
        RecordAdaptiveRetrievalFeedbackRequest request,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (planner is null)
        {
            return ContextCoreHttpResultMapper.StorageUnavailable(
                httpContext, string.Empty, "retrieval.adaptive.feedback.record",
                "IAdaptiveRetrievalPlanner 未注册。");
        }
        if (string.IsNullOrWhiteSpace(request.PlanSignature))
        {
            return ContextCoreHttpResultMapper.InvalidRequest(
                httpContext, string.Empty, "retrieval.adaptive.feedback.record",
                "PlanSignature 为必填（计划签名）。", field: "planSignature");
        }

        await planner.RecordOutcomeAsync(new RetrievalPlanFeedback
        {
            PlanSignature = request.PlanSignature,
            QueryText = request.QueryText ?? string.Empty,
            HitsReturned = Math.Max(0, request.HitsReturned),
            BudgetExceeded = request.BudgetExceeded,
            Effective = request.Effective,
            RecordedAtUtc = DateTimeOffset.UtcNow,
            FeedbackId = string.IsNullOrWhiteSpace(request.FeedbackId) ? null : request.FeedbackId,
            IdempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey,
            Source = request.Source,
            Confidence = request.Confidence,
            OutcomeQuality = request.OutcomeQuality,
            Subject = string.IsNullOrWhiteSpace(request.Subject) ? null : request.Subject
        }, ct).ConfigureAwait(false);

        return Results.Ok(new AdaptiveRetrievalFeedbackRecordResponse
        {
            PlanSignature = request.PlanSignature,
            Recorded = true
        });
    }

    /// <summary>清除反馈并重置自适应状态（signature 缺省时清除全部）。</summary>
    internal static async Task<IResult> ResetAsync(
        [FromServices] IAdaptiveRetrievalPlanner? planner,
        string? signature,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (planner is null)
        {
            return ContextCoreHttpResultMapper.StorageUnavailable(
                httpContext, string.Empty, "retrieval.adaptive.reset",
                "IAdaptiveRetrievalPlanner 未注册。");
        }

        var cleared = await planner.ResetAsync(string.IsNullOrWhiteSpace(signature) ? null : signature, ct).ConfigureAwait(false);
        return Results.Ok(new AdaptiveRetrievalResetResponse
        {
            Cleared = cleared,
            Scope = string.IsNullOrWhiteSpace(signature) ? "all" : signature
        });
    }

    private static string ResolveSignature(
        string? signature,
        string? originalTask,
        string? latestAssistantIntent,
        string? goals,
        string? workspaceId = null,
        string? collectionId = null,
        string? purpose = null,
        string? policyVersion = null,
        string? retrievalProfile = null,
        string? taskClass = null)
    {
        if (!string.IsNullOrWhiteSpace(signature))
        {
            return signature;
        }

        var input = new AgentRetrievalPlannerInput
        {
            OriginalTask = originalTask ?? string.Empty,
            LatestAssistantIntent = latestAssistantIntent,
            UnresolvedGoals = string.IsNullOrWhiteSpace(goals)
                ? Array.Empty<string>()
                : goals.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Purpose = purpose,
            PolicyVersion = policyVersion,
            RetrievalProfile = retrievalProfile,
            TaskClass = taskClass
        };
        return AdaptiveRetrievalPlanSignature.Compute(input);
    }
}

/// <summary>记录一条检索结果反馈的请求。</summary>
public sealed class RecordAdaptiveRetrievalFeedbackRequest
{
    /// <summary>计划签名（必填）。</summary>
    public string? PlanSignature { get; init; }

    /// <summary>本轮主导查询文本（诊断用）。</summary>
    public string? QueryText { get; init; }

    /// <summary>本轮返回的命中数。</summary>
    public int HitsReturned { get; init; }

    /// <summary>是否超出 Token 预算。</summary>
    public bool BudgetExceeded { get; init; }

    /// <summary>本轮检索结果是否被实际采用（缺省 true）。</summary>
    public bool Effective { get; init; } = true;

    /// <summary>反馈唯一标识（可选；缺省由规划器生成，用于审计追溯）。</summary>
    public string? FeedbackId { get; init; }

    /// <summary>幂等键（可选）：相同 (PlanSignature, IdempotencyKey) 只保留首条（P0-16）。</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>反馈来源（缺省 Runtime）。</summary>
    public RetrievalFeedbackSource Source { get; init; } = RetrievalFeedbackSource.Runtime;

    /// <summary>置信度（0–1，记录时钳制；缺省 1.0）。</summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>结果质量（0–1，记录时钳制；缺省 1.0）。</summary>
    public double OutcomeQuality { get; init; } = 1.0;

    /// <summary>主体标识（可选：Workspace / 用户 / 评测用例等；策略计算按主体封顶贡献）。</summary>
    public string? Subject { get; init; }
}

/// <summary>近期反馈列表响应。</summary>
public sealed class AdaptiveRetrievalFeedbackListResponse
{
    /// <summary>计划签名。</summary>
    public string PlanSignature { get; init; } = string.Empty;

    /// <summary>反馈条目（按记录时间倒序）。</summary>
    public IReadOnlyList<RetrievalPlanFeedback> Entries { get; init; } = Array.Empty<RetrievalPlanFeedback>();

    /// <summary>条目数。</summary>
    public int Count { get; init; }
}

/// <summary>记录反馈响应。</summary>
public sealed class AdaptiveRetrievalFeedbackRecordResponse
{
    /// <summary>计划签名。</summary>
    public string PlanSignature { get; init; } = string.Empty;

    /// <summary>是否已记录。</summary>
    public bool Recorded { get; init; }
}

/// <summary>重置响应。</summary>
public sealed class AdaptiveRetrievalResetResponse
{
    /// <summary>清除的反馈条数。</summary>
    public int Cleared { get; init; }

    /// <summary>清除范围（"all" 或具体签名）。</summary>
    public string Scope { get; init; } = string.Empty;
}
