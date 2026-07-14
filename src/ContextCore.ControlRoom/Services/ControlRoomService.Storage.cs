using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Core.Services.Storage;
using ContextCore.Embedding;
using ContextCore.Embedding.Providers;
using ContextCore.ModelGateway;
using ContextCore.ModelGateway.Infrastructure;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.ControlRoom.Services;

public sealed partial class ControlRoomService
{

    private async Task<PostgresOperationalStoreDiagnostics> GetPostgresStorageDiagnosticsSafeAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetServiceClient()
                .GetPostgresStorageDiagnosticsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ContextCoreApiException or InvalidOperationException)
        {
            return new PostgresOperationalStoreDiagnostics
            {
                Status = "Unavailable",
                ProviderCapabilityStatus = "Unavailable",
                Diagnostics = [$"PostgresDiagnosticsUnavailable:{ex.GetType().Name}"]
            };
        }
    }

    private static FileLayoutStatus BuildFileLayoutStatus(string rootPath)
    {
        try
        {
            var options = new FileStorageOptions { RootPath = rootPath };
            return new FileArtifactStore(options).BuildStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new FileLayoutStatus
            {
                DataRoot = rootPath,
                Diagnostics = [$"FileLayoutStatusUnavailable:{ex.GetType().Name}"]
            };
        }
    }

    private static MemoryLayoutDiagnostics BuildMemoryLayoutDiagnostics(
        string rootPath,
        string workspaceId,
        string collectionId)
    {
        try
        {
            var options = new FileStorageOptions { RootPath = rootPath };
            return new ContextCoreDataLayout(options).BuildMemoryLayoutDiagnostics(workspaceId, collectionId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new MemoryLayoutDiagnostics
            {
                DataRoot = rootPath,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Diagnostics = [$"MemoryLayoutDiagnosticsUnavailable:{ex.GetType().Name}"]
            };
        }
    }

    private static TraceLayoutDiagnostics BuildTraceLayoutDiagnostics(
        string rootPath,
        string workspaceId,
        string collectionId)
    {
        try
        {
            var options = new FileStorageOptions { RootPath = rootPath };
            return new ContextCoreDataLayout(options).BuildTraceLayoutDiagnostics(workspaceId, collectionId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new TraceLayoutDiagnostics
            {
                DataRoot = rootPath,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Diagnostics = [$"TraceLayoutDiagnosticsUnavailable:{ex.GetType().Name}"]
            };
        }
    }

    private static ReportLayoutDiagnostics BuildReportLayoutDiagnostics(string rootPath)
    {
        try
        {
            var options = new FileStorageOptions { RootPath = rootPath };
            return new ContextCoreDataLayout(options).BuildReportLayoutDiagnostics();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new ReportLayoutDiagnostics
            {
                DataRoot = rootPath,
                Diagnostics = [$"ReportLayoutDiagnosticsUnavailable:{ex.GetType().Name}"]
            };
        }
    }

    private static StorageBoundaryReport BuildStorageBoundaryReport()
    {
        try
        {
            return StorageResponsibilityRegistry.BuildReport();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new StorageBoundaryReport
            {
                Diagnostics = [$"StorageBoundaryReportUnavailable:{ex.GetType().Name}"]
            };
        }
    }
}
