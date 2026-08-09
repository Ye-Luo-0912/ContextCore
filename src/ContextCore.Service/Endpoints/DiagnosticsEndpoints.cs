using ContextCore.Abstractions;
using ContextCore.Service.Infrastructure;
using ContextCore.Service.Security;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace ContextCore.Service.Endpoints;

// ===========================================================================
// Runtime Diagnostics API —— 运行观测面（WP-N）
//
// GET /api/diagnostics/runtime：
// - Schema 版本（当前目标 / 已应用 / 缺失表 / 缺失索引）
// - Learning 物化 outbox 积压（pending 数）
// - 后台负载治理预算（BackgroundDrainBudget 配置）
//
// 设计原则：
// 1. Operator 角色（运行诊断属系统内部治理面）。
// 2. 依赖未注册时对应字段为 null / 0（诊断端点不因缺组件而失败）。
// 3. 处理器 internal static 可单测。
// ===========================================================================

/// <summary>
/// Runtime Diagnostics API 端点。
/// </summary>
internal static class DiagnosticsEndpoints
{
    private const string Tag = "Diagnostics";

    public static IEndpointRouteBuilder MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/diagnostics").WithTags(Tag);

        group.MapGet("/runtime", GetRuntimeDiagnosticsAsync)
            .WithName("GetRuntimeDiagnostics")
            .RequireWorkspaceRole(WorkspaceRole.Operator)
            .WithSummary("运行时诊断：Schema 版本 / 缺失表 / 索引 / Learning 积压 / 后台负载预算")
            .Produces<RuntimeDiagnosticsReport>(StatusCodes.Status200OK);

        return app;
    }

    /// <summary>运行时诊断处理器。</summary>
    /// <remarks>Schema 迁移诊断依赖 Postgres 基础设施（filesystem/内存 provider 下 Postgres 服务为惰性注册，
    /// 不构造连接工厂），由 Postgres 集成测试的 VerifySchemaAsync 覆盖；本端点提供
    /// provider 无关的 Learning 积压与后台负载观测。</remarks>
    internal static async Task<IResult> GetRuntimeDiagnosticsAsync(
        [FromServices] ILearningEventOutboxStore? learningOutbox,
        CancellationToken cancellationToken = default)
    {
        LearningDiagnostics? learning = null;
        if (learningOutbox is not null)
        {
            try
            {
                var counts = await learningOutbox.CountByStateAsync(cancellationToken);
                learning = new LearningDiagnostics
                {
                    PendingEvents = counts.TryGetValue(LearningEventOutboxStates.Pending, out var pending) ? pending : 0,
                    ProcessingEvents = counts.TryGetValue(LearningEventOutboxStates.Processing, out var processing) ? processing : 0,
                    DeadLetterEvents = counts.TryGetValue(LearningEventOutboxStates.DeadLettered, out var dead) ? dead : 0
                };
            }
            catch
            {
                // Learning 诊断查询失败不阻断。
            }
        }

        var report = new RuntimeDiagnosticsReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Schema = null,
            Learning = learning,
            Background = new BackgroundDiagnostics
            {
                DrainBudget = new DrainBudgetDiagnostics
                {
                    MaxBatchesPerBurst = BackgroundDrainBudgetDefaults.MaxBatchesPerBurst,
                    MaxBurstDurationMs = (long)BackgroundDrainBudgetDefaults.MaxBurstDuration.TotalMilliseconds,
                    YieldDelayMs = (long)BackgroundDrainBudgetDefaults.YieldDelay.TotalMilliseconds
                }
            }
        };

        return Results.Ok(report);
    }
}

/// <summary>运行时诊断报告。</summary>
public sealed class RuntimeDiagnosticsReport
{
    public DateTimeOffset GeneratedAt { get; init; }

    public SchemaDiagnostics? Schema { get; init; }

    public LearningDiagnostics? Learning { get; init; }

    public BackgroundDiagnostics? Background { get; init; }
}

/// <summary>Schema 诊断（迁移状态）。</summary>
public sealed class SchemaDiagnostics
{
    public string? TargetVersion { get; init; }

    public string? AppliedVersion { get; init; }

    public int MissingTables { get; init; }

    public int MissingIndexes { get; init; }

    public bool ConnectionAvailable { get; init; }
}

/// <summary>Learning 物化诊断（outbox 积压）。</summary>
public sealed class LearningDiagnostics
{
    public int PendingEvents { get; init; }

    public int ProcessingEvents { get; init; }

    public int DeadLetterEvents { get; init; }
}

/// <summary>后台负载诊断。</summary>
public sealed class BackgroundDiagnostics
{
    public DrainBudgetDiagnostics? DrainBudget { get; init; }
}

/// <summary>后台负载预算配置诊断。</summary>
public sealed class DrainBudgetDiagnostics
{
    public int MaxBatchesPerBurst { get; init; }

    public long MaxBurstDurationMs { get; init; }

    public long YieldDelayMs { get; init; }
}

/// <summary>后台负载预算默认值（诊断用；与 BackgroundDrainBudget 默认一致）。</summary>
internal static class BackgroundDrainBudgetDefaults
{
    public const int MaxBatchesPerBurst = 8;
    public static readonly TimeSpan MaxBurstDuration = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan YieldDelay = TimeSpan.FromMilliseconds(10);
}
