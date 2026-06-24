using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Rendering;

/// <summary>渲染 ControlRoom Service 模式下的运行时仪表盘。</summary>
public static class ServiceDashboardRenderer
{
    public static void Render(ServiceDashboardSnapshot snapshot)
    {
        Console.WriteLine(RenderToString(snapshot));
    }

    public static string RenderToString(ServiceDashboardSnapshot snapshot)
    {
        var builder = new StringBuilder();
        var runtime = snapshot.Snapshot;
        var status = runtime.Status;
        var readiness = runtime.Readiness;
        var deepStatus = runtime.DeepStatus;

        builder.AppendLine("ControlRoom Service Dashboard");
        builder.AppendLine("============================");
        builder.AppendLine($"时间          : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务地址      : {snapshot.BaseUrl}");
        builder.AppendLine($"Service 状态  : {status.Status}");
        builder.AppendLine($"Ready 状态    : {readiness.Status}");
        builder.AppendLine($"存储 Provider : {status.Storage.Provider}");
        builder.AppendLine($"Root Path     : {status.Storage.RootPath ?? "未返回"}");
        builder.AppendLine($"Retrieval 基线: {status.RetrievalBaseline}");
        builder.AppendLine($"Ready 缓存    : {(readiness.FromCache ? "命中" : "实时")} / TTL={readiness.CacheTtlSeconds}s");

        var capabilities = status.Capabilities
            .Concat(readiness.Capabilities)
            .GroupBy(capability => capability.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        AppendCapabilities(builder, capabilities);
        AppendChecks(builder, "Ready Probe Checks", readiness.Checks, readiness.Warnings);
        AppendMaintenance(builder, status.ShortTermMaintenance ?? readiness.ShortTermMaintenance);

        if (deepStatus is null)
        {
            builder.AppendLine();
            builder.AppendLine("Deep Probe");
            builder.AppendLine("----------");
            builder.AppendLine("未加载。输入 D 触发 refresh=true。");
        }
        else
        {
            builder.AppendLine();
            builder.AppendLine("Deep Probe");
            builder.AppendLine("----------");
            builder.AppendLine($"状态      : {deepStatus.Status}");
            builder.AppendLine($"缓存      : {(deepStatus.FromCache ? "命中" : "实时")} / TTL={deepStatus.CacheTtlSeconds}s");
            builder.AppendLine($"作用域    : {deepStatus.ProbeScope ?? "未返回"}");
            AppendChecks(builder, "Deep Probe Checks", deepStatus.Checks, deepStatus.Warnings);
        }

        builder.AppendLine();
        builder.AppendLine("命令");
        builder.AppendLine("----");
        builder.AppendLine("[R] 刷新状态  [D] 深度刷新  [I] Ingest  [G] Query  [V] Package  [J] Jobs  [M] Model  [U] Admin  [Y] Memory  [K] Constraints  [C] ConstraintGaps  [E] CandidateConstraints  [L] Relations  [O] Policy  [T] ShortTerm  [N] Promotion  [H] Learning  [32] PolicyFeedback  [33] LearningFeatures  [X] Planning  [F] Proposal  [34] RankerDebug  [35] CandidateMemory  [36] StableMemory  [37] VectorIndex  [B/0] 返回  [Q] 退出");

        return builder.ToString();
    }

    public static string RenderErrorToString(string baseUrl, ContextCoreApiException exception)
    {
        var builder = new StringBuilder();
        builder.AppendLine("ControlRoom Service Dashboard");
        builder.AppendLine("============================");
        builder.AppendLine($"服务地址 : {baseUrl}");
        builder.AppendLine("运行时错误");
        builder.AppendLine("--------");
        builder.AppendLine($"状态码 : {(int)exception.StatusCode}");
        builder.AppendLine($"错误码 : {exception.ErrorResponse.ErrorCode}");
        builder.AppendLine($"目标   : {exception.ErrorResponse.Target}");
        builder.AppendLine($"消息   : {exception.ErrorResponse.Message}");

        if (exception.ErrorResponse.Details.Count > 0)
        {
            builder.AppendLine("详情");
            foreach (var detail in exception.ErrorResponse.Details)
            {
                builder.AppendLine($"- [{detail.Code}] {detail.Field ?? detail.Target ?? "n/a"}: {detail.Message}");
            }
        }

        return builder.ToString();
    }

    private static void AppendCapabilities(
        StringBuilder builder,
        IReadOnlyList<ProviderCapabilityResponse> capabilities)
    {
        builder.AppendLine();
        builder.AppendLine("Provider Capabilities");
        builder.AppendLine("---------------------");
        if (capabilities.Count == 0)
        {
            builder.AppendLine("(无)");
            return;
        }

        foreach (var capability in capabilities)
        {
            builder.AppendLine($"- {capability.Name,-14} {capability.State,-16} active={(capability.Active ? "yes" : "no"),-3} {capability.Message}");
        }
    }

    private static void AppendChecks(
        StringBuilder builder,
        string title,
        IReadOnlyList<RuntimeProbeCheckResponse> checks,
        IReadOnlyList<string> warnings)
    {
        builder.AppendLine();
        builder.AppendLine(title);
        builder.AppendLine(new string('-', title.Length));
        if (checks.Count == 0)
        {
            builder.AppendLine("(无)");
        }
        else
        {
            foreach (var check in checks)
            {
                builder.AppendLine(
                    $"- {check.Name,-18} {check.Status,-8} severity={check.Severity,-7} sideEffect={(check.HasSideEffect ? "yes" : "no"),-3} duration={check.DurationMs,6:0.0}ms");
                builder.AppendLine($"  message : {check.Message}");
                if (!string.IsNullOrWhiteSpace(check.Warning))
                {
                    builder.AppendLine($"  warning : {check.Warning}");
                }

                if (!string.IsNullOrWhiteSpace(check.Detail))
                {
                    builder.AppendLine($"  detail  : {check.Detail}");
                }
            }
        }

        if (warnings.Count > 0)
        {
            builder.AppendLine("Warnings");
            foreach (var warning in warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }
    }

    private static void AppendMaintenance(
        StringBuilder builder,
        ShortTermMaintenanceStatusResponse? maintenance)
    {
        builder.AppendLine();
        builder.AppendLine("Short-Term Maintenance");
        builder.AppendLine("----------------------");
        if (maintenance is null)
        {
            builder.AppendLine("(无)");
            return;
        }

        builder.AppendLine($"enabled      : {maintenance.Enabled}");
        builder.AppendLine($"running      : {maintenance.IsRunning}");
        builder.AppendLine($"runOnStartup : {maintenance.RunOnStartup}");
        builder.AppendLine($"intervalSec  : {maintenance.IntervalSeconds}");
        builder.AppendLine($"lastError    : {maintenance.LastError ?? "none"}");
        if (maintenance.LastRun is null)
        {
            builder.AppendLine("lastRun      : none");
        }
        else
        {
            builder.AppendLine($"lastRun      : {maintenance.LastRun.RunId} [{maintenance.LastRun.Trigger}]");
        }
    }
}


