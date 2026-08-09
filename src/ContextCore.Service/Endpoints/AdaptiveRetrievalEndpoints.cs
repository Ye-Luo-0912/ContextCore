using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Service.Infrastructure;
using ContextCore.Service.Security;
using Microsoft.AspNetCore.Mvc;

namespace ContextCore.Service.Endpoints;

// ===========================================================================
// Adaptive Retrieval Planner API —— 自适应检索规划器运维面
//
// 目标：
// 为运维提供自适应检索规划器的状态观测与控制接口：
// - 查询当前自适应策略（由规划输入字段服务端派生签名）；
// - 查询 / 记录检索结果反馈（自适应学习信号）；
// - 清除反馈并重置自适应状态（按签名 / 按工作区 / 全局）。
//
// 端点：
// GET /api/retrieval/adaptive/policy 当前自适应策略（规划输入字段 → 服务端派生签名）
// GET /api/retrieval/adaptive/feedback 近期反馈（按规划输入字段派生签名）
// POST /api/retrieval/adaptive/feedback 记录一条检索结果反馈（服务端派生签名）
// POST /api/retrieval/adaptive/reset 清除反馈 / 重置自适应状态（缺省规划输入 = 全局重置）
//
// 设计原则：
// 1. 全部端点要求 Operator 角色（自适应状态属系统内部诊断面）；
//    全局重置（缺省规划输入字段）额外要求 Admin 角色（跨租户清除，更高权限）。
// 2. 服务端派生签名：计划签名一律由规划输入字段 + 请求上下文工作区在服务端派生，
//    不信任客户端提供的裸签名——跨租户伪造签名读取 / 污染 / 重置其他工作区
//    的自适应状态被杜绝（签名含工作区维度，工作区取自认证上下文）。
// 3. Subject（反馈主体）在端点记录时由服务端归属到调用方身份（API Key 名 /
//    ID / 工作区），客户端不得伪造主体归属（策略计算按主体封顶贡献）。
// 4. 处理器提取为 internal static 方法，可直接单元测试（DefaultHttpContext 执行 IResult）。
// 5. IAdaptiveRetrievalPlanner 未注册时返回 503（不静默降级）。
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

        // ── 查询当前模式 + 切换（WP-X：生产启用流程 + 一键回退）────────────
        group.MapGet("/mode", GetModeAsync)
            .WithName("GetAdaptiveRetrievalMode")
            .RequireWorkspaceRole(WorkspaceRole.Operator)
            .WithSummary("查询自适应检索当前运行模式（Disabled / Shadow / Active）+ 最近切换审计")
            .Produces<AdaptiveModeStatusResponse>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/mode", SetModeAsync)
            .WithName("SetAdaptiveRetrievalMode")
            .RequireWorkspaceRole(WorkspaceRole.Operator)
            .WithSummary("切换自适应检索运行模式（Shadow→Active 生产启用；一键回退 = Disabled；审计记录）")
            .Produces<AdaptiveModeTransition>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // ── 查询当前自适应策略 ───────────────────────────────────────────
        group.MapGet("/policy", GetPolicyAsync)
            .WithName("GetAdaptiveRetrievalPolicy")
            .RequireWorkspaceRole(WorkspaceRole.Operator)
            .WithSummary("查询自适应检索策略（预算收敛 / 查询收敛 / 召回增强乘数；由规划输入字段服务端派生签名）")
            .Produces<AdaptiveRetrievalPolicy>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // ── 查询近期反馈 ─────────────────────────────────────────────────
        group.MapGet("/feedback", ListFeedbackAsync)
            .WithName("ListAdaptiveRetrievalFeedback")
            .RequireWorkspaceRole(WorkspaceRole.Operator)
            .WithSummary("列出规划输入对应计划签名最近 N 条检索结果反馈（按记录时间倒序；服务端派生签名）")
            .Produces<AdaptiveRetrievalFeedbackListResponse>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // ── 记录一条反馈 ─────────────────────────────────────────────────
        group.MapPost("/feedback", RecordFeedbackAsync)
            .WithName("RecordAdaptiveRetrievalFeedback")
            .RequireWorkspaceRole(WorkspaceRole.Operator)
            .WithSummary("记录一条检索结果反馈（计划签名由规划输入字段 + 请求工作区服务端派生）")
            .Produces<AdaptiveRetrievalFeedbackRecordResponse>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        // ── 清除反馈 / 重置自适应状态 ───────────────────────────────────
        group.MapPost("/reset", ResetAsync)
            .WithName("ResetAdaptiveRetrieval")
            .RequireWorkspaceRole(WorkspaceRole.Operator)
            .WithSummary("清除检索结果反馈并重置自适应状态（按规划输入字段作用域；缺省 = 全局重置，需 Admin）")
            .Produces<AdaptiveRetrievalResetResponse>(StatusCodes.Status200OK)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ContextCoreErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    /// <summary>查询当前自适应策略：从规划输入字段 + 请求工作区服务端派生签名（不信任客户端裸签名）。
    /// 工作区取自请求上下文（IWorkspaceContextAccessor），其余租户维度经查询参数显式指定
    /// （签名必须包含 workspace / collection / purpose / policy / profile / taskClass）。</summary>
    internal static async Task<IResult> GetPolicyAsync(
        [FromServices] IAdaptiveRetrievalPlanner? planner,
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
        var resolvedSignature = ResolveSignature(originalTask, latestAssistantIntent, goals,
            workspaceId, collectionId, purpose, policyVersion, retrievalProfile, taskClass);
        var policy = await planner.GetPolicyForSignatureAsync(workspaceId ?? string.Empty, resolvedSignature, ct).ConfigureAwait(false);
        return Results.Ok(policy);
    }

    /// <summary>列出规划输入对应签名最近 N 条反馈。签名由服务端派生：客户端不能指定任意签名
    /// 读取其他工作区的反馈（工作区取自认证上下文）。</summary>
    internal static async Task<IResult> ListFeedbackAsync(
        [FromServices] IAdaptiveRetrievalPlanner? planner,
        string? originalTask,
        string? latestAssistantIntent,
        string? goals,
        string? collectionId,
        string? purpose,
        string? policyVersion,
        string? retrievalProfile,
        string? taskClass,
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
        if (!HasAnyPlanningInput(originalTask, latestAssistantIntent, goals, collectionId, purpose, policyVersion, retrievalProfile, taskClass))
        {
            return ContextCoreHttpResultMapper.InvalidRequest(
                httpContext, string.Empty, "retrieval.adaptive.feedback.list",
                "规划输入字段全部为空——无法服务端派生计划签名（至少提供一项：任务 / 意图 / 目标 / 集合 / 用途等）。",
                field: "originalTask");
        }

        var workspaceId = httpContext.RequestServices.GetService<IWorkspaceContextAccessor>()?.Current?.WorkspaceId;
        var resolvedSignature = ResolveSignature(originalTask, latestAssistantIntent, goals,
            workspaceId, collectionId, purpose, policyVersion, retrievalProfile, taskClass);
        var entries = await planner.ListFeedbackAsync(workspaceId ?? string.Empty, resolvedSignature, limit > 0 ? limit : 20, ct).ConfigureAwait(false);
        return Results.Ok(new AdaptiveRetrievalFeedbackListResponse
        {
            PlanSignature = resolvedSignature,
            Entries = entries,
            Count = entries.Count
        });
    }

    /// <summary>记录一条检索结果反馈。计划签名由服务端从规划输入字段 + 请求工作区派生
    /// （请求体中的裸签名不再被信任）；Subject 缺省时归属到调用方身份。</summary>
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
        var goals = JoinGoals(request.Goals);
        if (!HasAnyPlanningInput(request.OriginalTask, request.LatestAssistantIntent, goals, request.CollectionId,
            request.Purpose, request.PolicyVersion, request.RetrievalProfile, request.TaskClass))
        {
            return ContextCoreHttpResultMapper.InvalidRequest(
                httpContext, string.Empty, "retrieval.adaptive.feedback.record",
                "规划输入字段全部为空——无法服务端派生计划签名（至少提供一项：任务 / 意图 / 目标 / 集合 / 用途等）。",
                field: "originalTask");
        }

        var workspaceContext = httpContext.RequestServices.GetService<IWorkspaceContextAccessor>()?.Current;
        var workspaceId = workspaceContext?.WorkspaceId ?? string.Empty;
        var resolvedSignature = ResolveSignature(request.OriginalTask, request.LatestAssistantIntent, goals,
            workspaceId, request.CollectionId, request.Purpose, request.PolicyVersion, request.RetrievalProfile, request.TaskClass);

        await planner.RecordOutcomeAsync(new RetrievalPlanFeedback
        {
            PlanSignature = resolvedSignature,
            WorkspaceId = workspaceId,
            CollectionId = request.CollectionId,
            Purpose = request.Purpose,
            PolicyVersion = request.PolicyVersion,
            RetrievalProfile = request.RetrievalProfile,
            TaskClass = request.TaskClass,
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
            Subject = string.IsNullOrWhiteSpace(request.Subject)
                ? ResolveCallerSubject(workspaceContext)
                : request.Subject
        }, ct).ConfigureAwait(false);

        return Results.Ok(new AdaptiveRetrievalFeedbackRecordResponse
        {
            PlanSignature = resolvedSignature,
            Recorded = true
        });
    }

    /// <summary>清除反馈并重置自适应状态。规划输入字段缺省 = 全局重置（清除全部工作区，
    /// 需 Admin 角色）；提供规划输入字段 = 按服务端派生签名清除当前工作区（Operator 即可）。</summary>
    internal static async Task<IResult> ResetAsync(
        [FromServices] IAdaptiveRetrievalPlanner? planner,
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
                httpContext, string.Empty, "retrieval.adaptive.reset",
                "IAdaptiveRetrievalPlanner 未注册。");
        }

        var workspaceContext = httpContext.RequestServices.GetService<IWorkspaceContextAccessor>()?.Current;
        if (!HasAnyPlanningInput(originalTask, latestAssistantIntent, goals, collectionId, purpose, policyVersion, retrievalProfile, taskClass))
        {
            // 全局重置：清除全部工作区的自适应状态——跨租户操作，要求 Admin 角色
            //（RBAC 强制校验启用时非 Admin 拒绝；未启用时按既有约定放行）。
            var securityOptions = httpContext.RequestServices.GetService<SecurityOptions>();
            if (securityOptions?.Rbac.Enforce == true
                && (workspaceContext is null || !workspaceContext.Roles.Contains(WorkspaceRole.Admin)))
            {
                // 用 StatusCode(403) 而非 Results.Forbid()：Forbid 需要认证服务（IAuthenticationService）
                // 解析默认方案，处理器级校验不依赖认证管道即可表达"已认证但权限不足"。
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var cleared = await planner.ResetAsync(workspaceId: null, planSignature: null, ct).ConfigureAwait(false);
            return Results.Ok(new AdaptiveRetrievalResetResponse
            {
                Cleared = cleared,
                Scope = "all"
            });
        }

        // 按签名作用域重置：服务端派生签名 + 请求工作区（Operator 角色即可，跨租户签名被拒）。
        var workspaceId = workspaceContext?.WorkspaceId ?? string.Empty;
        var resolvedSignature = ResolveSignature(originalTask, latestAssistantIntent, goals,
            workspaceId, collectionId, purpose, policyVersion, retrievalProfile, taskClass);
        var scopedCleared = await planner.ResetAsync(workspaceId, resolvedSignature, ct).ConfigureAwait(false);
        return Results.Ok(new AdaptiveRetrievalResetResponse
        {
            Cleared = scopedCleared,
            Scope = resolvedSignature
        });
    }

    /// <summary>从规划输入字段服务端派生计划签名（工作区取自请求上下文，客户端不可伪造）。</summary>
    private static string ResolveSignature(
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

    /// <summary>规划输入字段是否至少提供一项（服务端派生签名的最低要求）。</summary>
    private static bool HasAnyPlanningInput(
        string? originalTask,
        string? latestAssistantIntent,
        string? goals,
        string? collectionId,
        string? purpose,
        string? policyVersion,
        string? retrievalProfile,
        string? taskClass)
        => !(string.IsNullOrWhiteSpace(originalTask)
            && string.IsNullOrWhiteSpace(latestAssistantIntent)
            && string.IsNullOrWhiteSpace(goals)
            && string.IsNullOrWhiteSpace(collectionId)
            && string.IsNullOrWhiteSpace(purpose)
            && string.IsNullOrWhiteSpace(policyVersion)
            && string.IsNullOrWhiteSpace(retrievalProfile)
            && string.IsNullOrWhiteSpace(taskClass));

    /// <summary>未解决目标列表 → 逗号分隔字符串（与查询参数 goals 的解析格式一致，供签名派生）。</summary>
    private static string JoinGoals(IReadOnlyList<string>? goals)
        => goals is null || goals.Count == 0
            ? string.Empty
            : string.Join(",", goals.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()));

    /// <summary>反馈主体缺省归属：API Key 名 → API Key ID → 工作区（服务端归属，客户端不可伪造）。</summary>
    private static string ResolveCallerSubject(WorkspaceContext? workspaceContext)
    {
        if (!string.IsNullOrWhiteSpace(workspaceContext?.ApiKeyName))
        {
            return workspaceContext.ApiKeyName;
        }
        if (!string.IsNullOrWhiteSpace(workspaceContext?.ApiKeyId))
        {
            return workspaceContext.ApiKeyId;
        }
        return workspaceContext?.WorkspaceId ?? string.Empty;
    }

    /// <summary>查询当前运行模式与最近切换审计。</summary>
    internal static async Task<IResult> GetModeAsync(
        [FromServices] AdaptiveRetrievalModeController? modeController)
    {
        if (modeController is null)
        {
            return Results.Json(
                new ContextCoreErrorResponse { Message = "自适应检索模式控制器未注册。" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(new AdaptiveModeStatusResponse
        {
            CurrentMode = modeController.CurrentMode,
            History = modeController.GetHistory()
        });
    }

    /// <summary>切换运行模式（Shadow→Active 生产启用；一键回退 = Disabled；审计记录）。</summary>
    internal static async Task<IResult> SetModeAsync(
        [FromServices] AdaptiveRetrievalModeController? modeController,
        [FromServices] IWorkspaceContextAccessor workspaceAccessor,
        AdaptiveModeSetRequest request)
    {
        if (modeController is null)
        {
            return Results.Json(
                new ContextCoreErrorResponse { Message = "自适应检索模式控制器未注册。" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (request is null || !Enum.IsDefined(request.Mode))
        {
            return Results.Json(
                new ContextCoreErrorResponse { Message = "目标模式无效（Disabled / Shadow / Active）。" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = workspaceAccessor.Current?.ApiKeyId ?? workspaceAccessor.Current?.WorkspaceId ?? "unknown";
        var transition = modeController.Transition(request.Mode, actor, request.Reason);
        return Results.Ok(transition);
    }
}

/// <summary>记录一条检索结果反馈的请求。</summary>
public sealed class RecordAdaptiveRetrievalFeedbackRequest
{
    /// <summary>规划输入：本轮主导任务（服务端派生签名维度；与其余输入字段共同派生计划签名）。</summary>
    public string? OriginalTask { get; init; }

    /// <summary>规划输入：最新助手意图（服务端派生签名维度）。</summary>
    public string? LatestAssistantIntent { get; init; }

    /// <summary>规划输入：未解决目标列表（服务端派生签名维度）。</summary>
    public IReadOnlyList<string>? Goals { get; init; }

    /// <summary>规划输入：集合 ID（签名租户维度，结构化审计列）。</summary>
    public string? CollectionId { get; init; }

    /// <summary>规划输入：用途（签名租户维度，结构化审计列）。</summary>
    public string? Purpose { get; init; }

    /// <summary>规划输入：策略版本（签名租户维度，结构化审计列）。</summary>
    public string? PolicyVersion { get; init; }

    /// <summary>规划输入：检索画像（签名租户维度，结构化审计列）。</summary>
    public string? RetrievalProfile { get; init; }

    /// <summary>规划输入：任务类别（签名租户维度，结构化审计列）。</summary>
    public string? TaskClass { get; init; }

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

    /// <summary>幂等键（可选）：相同 (PlanSignature, IdempotencyKey) 只保留首条。</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>反馈来源（缺省 Runtime）。</summary>
    public RetrievalFeedbackSource Source { get; init; } = RetrievalFeedbackSource.Runtime;

    /// <summary>置信度（0–1，记录时钳制；缺省 1.0）。</summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>结果质量（0–1，记录时钳制；缺省 1.0）。</summary>
    public double OutcomeQuality { get; init; } = 1.0;

    /// <summary>主体标识（可选；缺省由服务端归属到调用方身份——API Key 名 / ID / 工作区）。</summary>
    public string? Subject { get; init; }
}

/// <summary>近期反馈列表响应。</summary>
public sealed class AdaptiveRetrievalFeedbackListResponse
{
    /// <summary>计划签名（服务端派生）。</summary>
    public string PlanSignature { get; init; } = string.Empty;

    /// <summary>反馈条目（按记录时间倒序）。</summary>
    public IReadOnlyList<RetrievalPlanFeedback> Entries { get; init; } = Array.Empty<RetrievalPlanFeedback>();

    /// <summary>条目数。</summary>
    public int Count { get; init; }
}

/// <summary>模式状态响应。</summary>
public sealed class AdaptiveModeStatusResponse
{
    /// <summary>当前运行模式。</summary>
    public required AdaptiveRetrievalMode CurrentMode { get; init; }

    /// <summary>最近切换审计（时间正序）。</summary>
    public IReadOnlyList<AdaptiveModeTransition> History { get; init; } = Array.Empty<AdaptiveModeTransition>();
}

/// <summary>模式切换请求体。</summary>
public sealed class AdaptiveModeSetRequest
{
    /// <summary>目标模式（Disabled / Shadow / Active）。</summary>
    public required AdaptiveRetrievalMode Mode { get; init; }

    /// <summary>切换原因（审计可解释）。</summary>
    public string? Reason { get; init; }
}

/// <summary>记录反馈响应。</summary>
public sealed class AdaptiveRetrievalFeedbackRecordResponse
{
    /// <summary>计划签名（服务端派生）。</summary>
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