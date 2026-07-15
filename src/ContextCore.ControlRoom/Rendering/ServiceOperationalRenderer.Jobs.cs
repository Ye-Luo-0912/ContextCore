using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Core.Services;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Rendering;

public static partial class ServiceOperationalRenderer
{
    public static string RenderJobs(ServiceJobsSnapshot snapshot)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Jobs");
        builder.AppendLine($"时间   : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务   : {snapshot.BaseUrl}");
        builder.AppendLine($"作业数 : {snapshot.Jobs.Count}");
        builder.AppendLine();

        foreach (var job in snapshot.Jobs)
        {
            var payload = TryParsePayload(job.PayloadJson);
            builder.AppendLine($"- {job.JobId} [{job.Kind}/{job.State}]");
            builder.AppendLine($"  OperationId : {payload.OperationId ?? job.JobId}");
            builder.AppendLine($"  CreatedAt   : {job.CreatedAt:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"  UpdatedAt   : {(job.CompletedAt ?? job.StartedAt ?? job.CreatedAt):yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"  RetryCount  : {job.RetryCount}/{job.MaxRetryCount}");
            builder.AppendLine($"  Warnings    : {(string.IsNullOrWhiteSpace(job.ErrorMessage) ? "无" : job.ErrorMessage)}");
            if (payload.Metadata.Count > 0)
            {
                builder.AppendLine($"  Metadata    : {string.Join(", ", payload.Metadata.Select(pair => $"{pair.Key}={pair.Value}"))}");
            }
        }

        return builder.ToString();
    }

    public static string RenderJobDetail(ContextJob job)
    {
        var payload = TryParsePayload(job.PayloadJson);
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Job Detail");
        builder.AppendLine($"JobId       : {job.JobId}");
        builder.AppendLine($"Kind        : {job.Kind}");
        builder.AppendLine($"Status      : {job.State}");
        builder.AppendLine($"OperationId : {payload.OperationId ?? job.JobId}");
        builder.AppendLine($"CreatedAt   : {job.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"UpdatedAt   : {(job.CompletedAt ?? job.StartedAt ?? job.CreatedAt):yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"RetryCount  : {job.RetryCount}/{job.MaxRetryCount}");
        builder.AppendLine($"Warnings    : {(string.IsNullOrWhiteSpace(job.ErrorMessage) ? "无" : job.ErrorMessage)}");
        if (payload.Metadata.Count > 0)
        {
            builder.AppendLine("Metadata");
            foreach (var pair in payload.Metadata)
            {
                builder.AppendLine($"- {pair.Key}={pair.Value}");
            }
        }

        return builder.ToString();
    }

    public static string RenderModel(ServiceModelSnapshot snapshot)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Model Status");
        builder.AppendLine($"时间    : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务    : {snapshot.BaseUrl}");
        builder.AppendLine();
        builder.AppendLine("Providers");
        foreach (var provider in snapshot.ModelStatus.ApiProviders)
        {
            builder.AppendLine($"- {provider.Name} [{provider.Provider}] enabled={(provider.Enabled ? "yes" : "no")} endpoint={(provider.EndpointConfigured ? "configured" : "missing")}");
        }

        builder.AppendLine();
        builder.AppendLine("Routes");
        foreach (var route in snapshot.ModelStatus.Routes.Take(10))
        {
            builder.AppendLine($"- role={route.Role} task={route.TaskKind ?? "-"} mode={route.ThinkingMode ?? "-"} primary={route.Primary?.ModelName ?? route.PrimaryModelName ?? "-"} fallback={route.Fallback?.ModelName ?? route.FallbackModelName ?? "-"}");
        }

        if (snapshot.RouteResolution is not null)
        {
            builder.AppendLine();
            builder.AppendLine("Route Resolve");
            builder.AppendLine($"- role={snapshot.RouteResolution.Role}");
            builder.AppendLine($"- selected={snapshot.RouteResolution.Primary?.ModelName ?? "未命中"}");
            builder.AppendLine($"- fallback={snapshot.RouteResolution.Fallback?.ModelName ?? "无"}");
            builder.AppendLine($"- reason={snapshot.RouteResolution.RouteSource}");
        }

        return builder.ToString();
    }

    public static string RenderAdminRuntime(ServiceAdminRuntimeSnapshot snapshot)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Admin / Runtime");
        builder.AppendLine($"时间          : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务          : {snapshot.BaseUrl}");
        builder.AppendLine($"RuntimeStatus : {snapshot.Runtime.Status.Status}/{snapshot.Runtime.Readiness.Status}");
        builder.AppendLine($"Storage       : {snapshot.AdminStatus.Storage.Provider}");
        builder.AppendLine($"RootPath      : {snapshot.AdminStatus.Storage.RootPath ?? "未返回"}");
        builder.AppendLine($"Retrieval     : {snapshot.AdminStatus.RetrievalBaseline}");
        builder.AppendLine($"BackupRoot    : {snapshot.BackupStatus.Root ?? "无"}");
        builder.AppendLine($"BackupExists  : {snapshot.BackupStatus.Exists}");
        builder.AppendLine($"BackupHealthy : {snapshot.BackupValidate.Healthy}");
        builder.AppendLine($"BackupMessage : {snapshot.BackupValidate.Message ?? "无"}");
        builder.AppendLine();
        builder.AppendLine("File Layout Status");
        builder.AppendLine($"DataRoot      : {snapshot.FileLayoutStatus.DataRoot}");
        builder.AppendLine($"Categories    : {snapshot.FileLayoutStatus.ArtifactCategories.Count}");
        builder.AppendLine($"ManifestCount : {snapshot.FileLayoutStatus.ManifestCount}");
        builder.AppendLine($"ReportCount   : {snapshot.FileLayoutStatus.ReportCount}");
        foreach (var sample in snapshot.FileLayoutStatus.ResolvedPathSamples.Take(4))
        {
            builder.AppendLine($"- {sample.Descriptor.Kind}/{sample.Descriptor.CapabilityId}: {sample.RelativePath}");
        }

        if (snapshot.FileLayoutStatus.Diagnostics.Count > 0)
        {
            builder.AppendLine($"Diagnostics   : {string.Join(", ", snapshot.FileLayoutStatus.Diagnostics)}");
        }

        builder.AppendLine();
        builder.AppendLine("Memory Layout Status");
        AppendMemoryLayoutStatus(builder, snapshot.MemoryLayoutDiagnostics);

        builder.AppendLine();
        builder.AppendLine("Trace Layout Status");
        AppendTraceLayoutStatus(builder, snapshot.TraceLayoutDiagnostics);

        builder.AppendLine();
        builder.AppendLine("Report Layout Status");
        AppendReportLayoutStatus(builder, snapshot.ReportLayoutDiagnostics);

        builder.AppendLine();
        builder.AppendLine("Storage Boundary Status");
        AppendStorageBoundaryStatus(builder, snapshot.StorageBoundaryReport);

        builder.AppendLine();
        builder.AppendLine("Postgres Operational Store Status");
        AppendPostgresOperationalStoreStatus(builder, snapshot.PostgresOperationalStoreDiagnostics);

        return builder.ToString();
    }
}
