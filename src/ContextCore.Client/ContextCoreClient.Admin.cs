using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Client;

public sealed partial class ContextCoreClient
{
    public async Task<ContextCoreModelStatusResponse> GetModelStatusAsync(CancellationToken cancellationToken = default)
    {
        return await GetRequiredAsync<ContextCoreModelStatusResponse>("api/model/status", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextCoreModelRouteResolveResponse> ResolveModelRouteAsync(
        ContextCoreModelRouteResolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<ContextCoreModelRouteResolveRequest, ContextCoreModelRouteResolveResponse>(
            "api/model/route/resolve", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextCoreAdminStatusResponse> GetAdminStatusAsync(
        string? workspaceId = null,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        var qs = new QueryBuilder()
            .Add("workspaceId", workspaceId)
            .Add("collectionId", collectionId);
        return await GetRequiredAsync<ContextCoreAdminStatusResponse>($"api/admin/status{qs}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextCoreBackupStatusResponse> GetBackupStatusAsync(CancellationToken cancellationToken = default)
    {
        return await GetRequiredAsync<ContextCoreBackupStatusResponse>("api/admin/backup/status", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextCoreBackupValidateResponse> ValidateBackupAsync(CancellationToken cancellationToken = default)
    {
        return await GetRequiredAsync<ContextCoreBackupValidateResponse>("api/admin/backup/validate", cancellationToken).ConfigureAwait(false);
    }

    public async Task<PostgresStorageStatusResponse> GetPostgresStorageStatusAsync(CancellationToken cancellationToken = default)
    {
        return await GetRequiredAsync<PostgresStorageStatusResponse>("api/admin/storage/postgres/status", cancellationToken).ConfigureAwait(false);
    }

    public async Task<PostgresOperationalStoreDiagnostics> GetPostgresStorageDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        return await GetRequiredAsync<PostgresOperationalStoreDiagnostics>("api/admin/storage/postgres/diagnostics", cancellationToken).ConfigureAwait(false);
    }

    public async Task<PostgresMigrationPlanResponse> PreviewPostgresMigrationsAsync(CancellationToken cancellationToken = default)
    {
        return await PostRequiredNoBodyAsync<PostgresMigrationPlanResponse>("api/admin/storage/postgres/migrations/dry-run", cancellationToken).ConfigureAwait(false);
    }

    public async Task<PostgresMigrationApplyResponse> ApplyPostgresMigrationsAsync(
        bool confirm,
        CancellationToken cancellationToken = default)
    {
        var request = new PostgresMigrationRequest { Confirm = confirm };
        return await PostRequiredAsync<PostgresMigrationRequest, PostgresMigrationApplyResponse>(
            "api/admin/storage/postgres/migrations/apply", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PostgresRelationScopedServiceModeStatusResponse> GetRelationProviderStatusAsync(CancellationToken cancellationToken = default)
    {
        return await GetRequiredAsync<PostgresRelationScopedServiceModeStatusResponse>("api/admin/storage/relation-provider/status", cancellationToken).ConfigureAwait(false);
    }

    public async Task<PostgresRelationScopedServiceModeStatusResponse> GetRelationProviderScopedDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        return await GetRequiredAsync<PostgresRelationScopedServiceModeStatusResponse>("api/admin/storage/relation-provider/scoped-diagnostics", cancellationToken).ConfigureAwait(false);
    }
}
