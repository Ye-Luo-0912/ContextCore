using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Client;

public sealed partial class ContextCoreClient
{
    public async Task<ContextCoreModelStatusResponse> GetModelStatusAsync(CancellationToken cancellationToken = default)
    {
        // ModelStatus 响应包含 UntypedNode 字段，Kiota JSON 序列化器无法正确处理 null UntypedNode，
        // 保留直接 HttpClient + STJ 反序列化。
        return await GetRequiredAsync<ContextCoreModelStatusResponse>("api/model/status", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextCoreModelRouteResolveResponse> ResolveModelRouteAsync(
        ContextCoreModelRouteResolveRequest request,
        CancellationToken cancellationToken = default)
    {
        // ModelRouteResolve 响应包含 IComposedTypeWrapper 字段（OpenAPI union type），
        // Kiota 序列化器无法正确处理空组合类型包装器，保留直接 HttpClient + STJ 反序列化。
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<ContextCoreModelRouteResolveRequest, ContextCoreModelRouteResolveResponse>(
            "api/model/route/resolve", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextCoreAdminStatusResponse> GetAdminStatusAsync(
        string? workspaceId = null,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _generated.Api.Admin.Status.GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.CollectionId = collectionId;
            }, cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ContextCoreAdminStatusResponse>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for GET api/admin/status.");
        }
        catch (ContextCore.Client.Generated.Models.ContextCoreErrorResponse ex)
        {
            throw ToApiException(ex);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex)
        {
            throw ToApiException(ex);
        }
    }

    public async Task<ContextCoreBackupStatusResponse> GetBackupStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _generated.Api.Admin.Backup.Status.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ContextCoreBackupStatusResponse>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for GET api/admin/backup/status.");
        }
        catch (ContextCore.Client.Generated.Models.ContextCoreErrorResponse ex)
        {
            throw ToApiException(ex);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex)
        {
            throw ToApiException(ex);
        }
    }

    public async Task<ContextCoreBackupValidateResponse> ValidateBackupAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _generated.Api.Admin.Backup.Validate.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ContextCoreBackupValidateResponse>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for GET api/admin/backup/validate.");
        }
        catch (ContextCore.Client.Generated.Models.ContextCoreErrorResponse ex)
        {
            throw ToApiException(ex);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex)
        {
            throw ToApiException(ex);
        }
    }

    public async Task<PostgresStorageStatusResponse> GetPostgresStorageStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _generated.Api.Admin.Storage.Postgres.Status.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<PostgresStorageStatusResponse>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for GET api/admin/storage/postgres/status.");
        }
        catch (ContextCore.Client.Generated.Models.ContextCoreErrorResponse ex)
        {
            throw ToApiException(ex);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex)
        {
            throw ToApiException(ex);
        }
    }

    public async Task<PostgresOperationalStoreDiagnostics> GetPostgresStorageDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _generated.Api.Admin.Storage.Postgres.Diagnostics.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<PostgresOperationalStoreDiagnostics>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for GET api/admin/storage/postgres/diagnostics.");
        }
        catch (ContextCore.Client.Generated.Models.ContextCoreErrorResponse ex)
        {
            throw ToApiException(ex);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex)
        {
            throw ToApiException(ex);
        }
    }

    public async Task<PostgresMigrationPlanResponse> PreviewPostgresMigrationsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _generated.Api.Admin.Storage.Postgres.Migrations.DryRun.PostAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<PostgresMigrationPlanResponse>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for POST api/admin/storage/postgres/migrations/dry-run.");
        }
        catch (ContextCore.Client.Generated.Models.ContextCoreErrorResponse ex)
        {
            throw ToApiException(ex);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex)
        {
            throw ToApiException(ex);
        }
    }

    public async Task<PostgresMigrationApplyResponse> ApplyPostgresMigrationsAsync(
        bool confirm,
        CancellationToken cancellationToken = default)
    {
        var abstractionRequest = new PostgresMigrationRequest { Confirm = confirm };
        var generatedRequest = await MapToGenerated(abstractionRequest, ContextCore.Client.Generated.Models.PostgresMigrationRequest.CreateFromDiscriminatorValue).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Failed to map PostgresMigrationRequest to generated model.");
        try
        {
            var result = await _generated.Api.Admin.Storage.Postgres.Migrations.Apply.PostAsync(generatedRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<PostgresMigrationApplyResponse>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for POST api/admin/storage/postgres/migrations/apply.");
        }
        catch (ContextCore.Client.Generated.Models.ContextCoreErrorResponse ex)
        {
            throw ToApiException(ex);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex)
        {
            throw ToApiException(ex);
        }
    }

    public async Task<PostgresRelationScopedServiceModeStatusResponse> GetRelationProviderStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _generated.Api.Admin.Storage.RelationProvider.Status.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<PostgresRelationScopedServiceModeStatusResponse>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for GET api/admin/storage/relation-provider/status.");
        }
        catch (ContextCore.Client.Generated.Models.ContextCoreErrorResponse ex)
        {
            throw ToApiException(ex);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex)
        {
            throw ToApiException(ex);
        }
    }

    public async Task<PostgresRelationScopedServiceModeStatusResponse> GetRelationProviderScopedDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _generated.Api.Admin.Storage.RelationProvider.ScopedDiagnostics.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<PostgresRelationScopedServiceModeStatusResponse>(result)
                ?? throw new InvalidOperationException("ContextCore returned an empty response for GET api/admin/storage/relation-provider/scoped-diagnostics.");
        }
        catch (ContextCore.Client.Generated.Models.ContextCoreErrorResponse ex)
        {
            throw ToApiException(ex);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex)
        {
            throw ToApiException(ex);
        }
    }
}
