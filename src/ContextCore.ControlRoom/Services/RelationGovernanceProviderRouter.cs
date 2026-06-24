using System.Diagnostics;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.ControlRoom.Services;

/// <summary>Relation governance provider 路由器；默认 FileSystemPrimary，Postgres primary 只允许显式 guarded scope。</summary>
public sealed class RelationGovernanceProviderRouter
{
    private readonly FileRelationStore _fileRelationStore;
    private readonly FileRelationReviewStore _fileReviewStore;
    private readonly FileRelationDiagnosticsStore _fileDiagnosticsStore;
    private readonly PostgresRelationStore _postgresRelationStore;
    private readonly PostgresRelationReviewStore _postgresReviewStore;
    private readonly PostgresRelationDiagnosticsStore _postgresDiagnosticsStore;
    private readonly RelationGovernanceProviderSwitchOptions _options;
    private readonly bool _readinessGatePassed;
    private readonly bool _shadowReadQualityReady;
    private readonly Func<RelationGovernanceProviderSwitchTrace, CancellationToken, Task> _traceSink;

    public RelationGovernanceProviderRouter(
        FileRelationStore fileRelationStore,
        FileRelationReviewStore fileReviewStore,
        FileRelationDiagnosticsStore fileDiagnosticsStore,
        PostgresRelationStore postgresRelationStore,
        PostgresRelationReviewStore postgresReviewStore,
        PostgresRelationDiagnosticsStore postgresDiagnosticsStore,
        RelationGovernanceProviderSwitchOptions options,
        bool readinessGatePassed,
        bool shadowReadQualityReady,
        Func<RelationGovernanceProviderSwitchTrace, CancellationToken, Task> traceSink)
    {
        _fileRelationStore = fileRelationStore;
        _fileReviewStore = fileReviewStore;
        _fileDiagnosticsStore = fileDiagnosticsStore;
        _postgresRelationStore = postgresRelationStore;
        _postgresReviewStore = postgresReviewStore;
        _postgresDiagnosticsStore = postgresDiagnosticsStore;
        _options = options;
        _readinessGatePassed = readinessGatePassed;
        _shadowReadQualityReady = shadowReadQualityReady;
        _traceSink = traceSink;
    }

    public Task SaveRelationAsync(string operationId, ContextRelation relation, CancellationToken cancellationToken = default)
        => ExecuteWriteAsync(
            operationId,
            relation.WorkspaceId,
            relation.CollectionId,
            "RelationWrite",
            token => _fileRelationStore.SaveAsync(relation, token),
            token => _postgresRelationStore.SaveAsync(relation, token),
            cancellationToken);

    public Task DeleteRelationAsync(
        string operationId,
        string workspaceId,
        string collectionId,
        string relationId,
        CancellationToken cancellationToken = default)
        => ExecuteWriteAsync(
            operationId,
            workspaceId,
            collectionId,
            "RelationDelete",
            token => _fileRelationStore.DeleteAsync(workspaceId, collectionId, relationId, token),
            token => _postgresRelationStore.DeleteAsync(workspaceId, collectionId, relationId, token),
            cancellationToken);

    public Task AppendReviewAsync(string operationId, RelationReviewRecord review, CancellationToken cancellationToken = default)
        => ExecuteWriteAsync(
            operationId,
            review.WorkspaceId,
            review.CollectionId ?? string.Empty,
            "RelationReviewWrite",
            token => _fileReviewStore.AppendReviewAsync(review, token),
            token => _postgresReviewStore.AppendReviewAsync(review, token),
            cancellationToken);

    public Task WriteDiagnosticsAsync(string operationId, RelationDiagnosticsSnapshot snapshot, CancellationToken cancellationToken = default)
        => ExecuteWriteAsync(
            operationId,
            snapshot.WorkspaceId,
            snapshot.CollectionId ?? string.Empty,
            "RelationDiagnosticsWrite",
            token => _fileDiagnosticsStore.WriteAsync(snapshot, token),
            token => _postgresDiagnosticsStore.WriteAsync(snapshot, token),
            cancellationToken);

    public Task<ContextRelation?> GetRelationAsync(string operationId, string workspaceId, string collectionId, string relationId, CancellationToken cancellationToken = default)
        => ExecuteReadAsync(
            operationId,
            workspaceId,
            collectionId,
            "RelationGet",
            token => _fileRelationStore.GetAsync(workspaceId, collectionId, relationId, token),
            token => _postgresRelationStore.GetAsync(workspaceId, collectionId, relationId, token),
            false,
            cancellationToken);

    public Task<IReadOnlyList<ContextRelation>> QueryRelationsAsync(string operationId, string workspaceId, string collectionId, CancellationToken cancellationToken = default)
        => ExecuteReadAsync(
            operationId,
            workspaceId,
            collectionId,
            "RelationList",
            token => _fileRelationStore.QueryAsync(new ContextRelationQuery { WorkspaceId = workspaceId, CollectionId = collectionId, Take = int.MaxValue }, token),
            token => _postgresRelationStore.QueryAsync(new ContextRelationQuery { WorkspaceId = workspaceId, CollectionId = collectionId, Take = int.MaxValue }, token),
            false,
            cancellationToken);

    public Task<IReadOnlyList<ContextRelation>> QueryBySourceAsync(string operationId, string workspaceId, string collectionId, string sourceId, CancellationToken cancellationToken = default)
        => ExecuteReadAsync(
            operationId,
            workspaceId,
            collectionId,
            "RelationSourceQuery",
            token => _fileRelationStore.QueryBySourceAsync(workspaceId, collectionId, sourceId, token),
            token => _postgresRelationStore.QueryBySourceAsync(workspaceId, collectionId, sourceId, token),
            false,
            cancellationToken);

    public Task<IReadOnlyList<ContextRelation>> QueryByTargetAsync(string operationId, string workspaceId, string collectionId, string targetId, CancellationToken cancellationToken = default)
        => ExecuteReadAsync(
            operationId,
            workspaceId,
            collectionId,
            "RelationTargetQuery",
            token => _fileRelationStore.QueryByTargetAsync(workspaceId, collectionId, targetId, token),
            token => _postgresRelationStore.QueryByTargetAsync(workspaceId, collectionId, targetId, token),
            false,
            cancellationToken);

    public Task<IReadOnlyList<ContextRelation>> QueryByTypeAsync(string operationId, string workspaceId, string collectionId, string relationType, CancellationToken cancellationToken = default)
        => ExecuteReadAsync(
            operationId,
            workspaceId,
            collectionId,
            "RelationTypeQuery",
            token => _fileRelationStore.QueryByTypeAsync(workspaceId, collectionId, relationType, token),
            token => _postgresRelationStore.QueryByTypeAsync(workspaceId, collectionId, relationType, token),
            false,
            cancellationToken);

    public Task<IReadOnlyList<ContextRelation>> QueryByLifecycleAsync(string operationId, string workspaceId, string collectionId, string lifecycle, CancellationToken cancellationToken = default)
        => ExecuteReadAsync(
            operationId,
            workspaceId,
            collectionId,
            "RelationLifecycleQuery",
            async token => FilterRelationsByMetadata(
                await _fileRelationStore.QueryAsync(new ContextRelationQuery { WorkspaceId = workspaceId, CollectionId = collectionId, Take = int.MaxValue }, token).ConfigureAwait(false),
                ["lifecycle", "Lifecycle"],
                lifecycle),
            token => _postgresRelationStore.QueryByLifecycleAsync(workspaceId, collectionId, lifecycle, token),
            false,
            cancellationToken);

    public Task<IReadOnlyList<ContextRelation>> QueryByReviewStatusAsync(string operationId, string workspaceId, string collectionId, string reviewStatus, CancellationToken cancellationToken = default)
        => ExecuteReadAsync(
            operationId,
            workspaceId,
            collectionId,
            "RelationReviewStatusQuery",
            async token => FilterRelationsByMetadata(
                await _fileRelationStore.QueryAsync(new ContextRelationQuery { WorkspaceId = workspaceId, CollectionId = collectionId, Take = int.MaxValue }, token).ConfigureAwait(false),
                ["reviewStatus", "ReviewStatus"],
                reviewStatus),
            token => _postgresRelationStore.QueryByReviewStatusAsync(workspaceId, collectionId, reviewStatus, token),
            false,
            cancellationToken);

    public Task<IReadOnlyList<ContextRelation>> QueryReplacementChainRelationsAsync(string operationId, string workspaceId, string collectionId, string itemId, CancellationToken cancellationToken = default)
        => ExecuteReadAsync(
            operationId,
            workspaceId,
            collectionId,
            "RelationReplacementChainQuery",
            async token =>
            {
                var relations = await _fileRelationStore.QueryForItemAsync(workspaceId, collectionId, itemId, token).ConfigureAwait(false);
                return [.. relations.Where(IsReplacementRelation)];
            },
            token => _postgresRelationStore.QueryReplacementChainRelationsAsync(workspaceId, collectionId, itemId, token),
            false,
            cancellationToken);

    public Task<IReadOnlyList<RelationReviewRecord>> QueryReviewsAsync(string operationId, string workspaceId, string collectionId, string relationId, CancellationToken cancellationToken = default)
        => ExecuteReadAsync(
            operationId,
            workspaceId,
            collectionId,
            "RelationReviewList",
            token => _fileReviewStore.QueryReviewsAsync(relationId, token),
            token => _postgresReviewStore.QueryReviewsAsync(relationId, token),
            false,
            cancellationToken);

    public Task<RelationReviewRecord?> GetLatestReviewAsync(string operationId, string workspaceId, string collectionId, string relationId, CancellationToken cancellationToken = default)
        => ExecuteReadAsync(
            operationId,
            workspaceId,
            collectionId,
            "RelationReviewLatest",
            token => _fileReviewStore.GetLatestReviewAsync(relationId, token),
            token => _postgresReviewStore.GetLatestReviewAsync(relationId, token),
            false,
            cancellationToken);

    public Task<IReadOnlyList<RelationDiagnosticsSnapshot>> QueryDiagnosticsByRelationAsync(string operationId, string workspaceId, string collectionId, string relationId, CancellationToken cancellationToken = default)
        => ExecuteReadAsync(
            operationId,
            workspaceId,
            collectionId,
            "RelationDiagnosticsByRelation",
            token => _fileDiagnosticsStore.QueryByRelationAsync(workspaceId, collectionId, relationId, token),
            token => _postgresDiagnosticsStore.QueryByRelationAsync(workspaceId, collectionId, relationId, token),
            false,
            cancellationToken);

    public Task<IReadOnlyList<RelationDiagnosticsSnapshot>> QueryDiagnosticsByItemAsync(string operationId, string workspaceId, string collectionId, string itemId, CancellationToken cancellationToken = default)
        => ExecuteReadAsync(
            operationId,
            workspaceId,
            collectionId,
            "RelationDiagnosticsByItem",
            token => _fileDiagnosticsStore.QueryByItemAsync(workspaceId, collectionId, itemId, token),
            token => _postgresDiagnosticsStore.QueryByItemAsync(workspaceId, collectionId, itemId, token),
            false,
            cancellationToken);

    public Task<IReadOnlyList<RelationDiagnosticsSnapshot>> QueryDiagnosticsByKindAsync(string operationId, string workspaceId, string collectionId, string diagnosticKind, CancellationToken cancellationToken = default)
        => ExecuteReadAsync(
            operationId,
            workspaceId,
            collectionId,
            "RelationDiagnosticsByKind",
            token => _fileDiagnosticsStore.QueryByKindAsync(workspaceId, collectionId, diagnosticKind, token),
            token => _postgresDiagnosticsStore.QueryByKindAsync(workspaceId, collectionId, diagnosticKind, token),
            false,
            cancellationToken);

    public Task<IReadOnlyList<RelationDiagnosticsSnapshot>> QueryDiagnosticsBySeverityAsync(string operationId, string workspaceId, string collectionId, string severity, CancellationToken cancellationToken = default)
        => ExecuteReadAsync(
            operationId,
            workspaceId,
            collectionId,
            "RelationDiagnosticsBySeverity",
            token => _fileDiagnosticsStore.QueryBySeverityAsync(workspaceId, collectionId, severity, token),
            token => _postgresDiagnosticsStore.QueryBySeverityAsync(workspaceId, collectionId, severity, token),
            false,
            cancellationToken);

    private async Task ExecuteWriteAsync(
        string operationId,
        string workspaceId,
        string collectionId,
        string operationKind,
        Func<CancellationToken, Task> fileWrite,
        Func<CancellationToken, Task> postgresWrite,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync<object?>(
            operationId,
            workspaceId,
            collectionId,
            operationKind,
            async token =>
            {
                await fileWrite(token).ConfigureAwait(false);
                return null;
            },
            async token =>
            {
                await postgresWrite(token).ConfigureAwait(false);
                return null;
            },
            true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> ExecuteReadAsync<T>(
        string operationId,
        string workspaceId,
        string collectionId,
        string operationKind,
        Func<CancellationToken, Task<T>> fileRead,
        Func<CancellationToken, Task<T>> postgresRead,
        bool isWrite,
        CancellationToken cancellationToken)
    {
        return await ExecuteAsync(
            operationId,
            workspaceId,
            collectionId,
            operationKind,
            fileRead,
            postgresRead,
            isWrite,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> ExecuteAsync<T>(
        string operationId,
        string workspaceId,
        string collectionId,
        string operationKind,
        Func<CancellationToken, Task<T>> fileOperation,
        Func<CancellationToken, Task<T>> postgresOperation,
        bool isWrite,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var mode = ResolveMode(workspaceId, collectionId);
        var primaryProvider = mode == RelationGovernanceProviderMode.GuardedPostgresPrimary ? "Postgres" : "FileSystem";
        var fallbackUsed = false;
        var mismatchDetected = false;
        var postgresError = string.Empty;
        T result;

        if (mode == RelationGovernanceProviderMode.DualWriteOnly)
        {
            result = await fileOperation(cancellationToken).ConfigureAwait(false);
            if (isWrite)
            {
                try
                {
                    await postgresOperation(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (_options.FallbackToFileSystem)
                {
                    fallbackUsed = true;
                    postgresError = ex.GetType().Name;
                }
            }
        }
        else if (mode == RelationGovernanceProviderMode.ShadowRead)
        {
            result = await fileOperation(cancellationToken).ConfigureAwait(false);
            if (!isWrite && _options.ContinueComparisonTrace)
            {
                try
                {
                    var postgresResult = await postgresOperation(cancellationToken).ConfigureAwait(false);
                    var fileHash = RelationGovernanceShadowReadCoordinator.ComputeStableHash(result);
                    var postgresHash = RelationGovernanceShadowReadCoordinator.ComputeStableHash(postgresResult);
                    mismatchDetected = !string.Equals(fileHash, postgresHash, StringComparison.Ordinal);
                    if (mismatchDetected && _options.FailClosedOnMismatch)
                    {
                        await EmitTraceAsync(operationId, workspaceId, collectionId, mode, operationKind, primaryProvider, fallbackUsed, mismatchDetected, postgresError, stopwatch, cancellationToken).ConfigureAwait(false);
                        throw new InvalidOperationException("Relation provider switch mismatch detected.");
                    }
                }
                catch (Exception ex) when (_options.FallbackToFileSystem && ex is not InvalidOperationException)
                {
                    fallbackUsed = true;
                    postgresError = ex.GetType().Name;
                }
            }
        }
        else if (mode == RelationGovernanceProviderMode.GuardedPostgresPrimary)
        {
            try
            {
                result = await postgresOperation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (_options.FallbackToFileSystem)
            {
                fallbackUsed = true;
                postgresError = ex.GetType().Name;
                result = await fileOperation(cancellationToken).ConfigureAwait(false);
                await EmitTraceAsync(operationId, workspaceId, collectionId, mode, operationKind, primaryProvider, fallbackUsed, mismatchDetected, postgresError, stopwatch, cancellationToken).ConfigureAwait(false);
                return result;
            }

            if (_options.ContinueComparisonTrace)
            {
                var fileResult = await fileOperation(cancellationToken).ConfigureAwait(false);
                var postgresHash = RelationGovernanceShadowReadCoordinator.ComputeStableHash(result);
                var fileHash = RelationGovernanceShadowReadCoordinator.ComputeStableHash(fileResult);
                mismatchDetected = !string.Equals(postgresHash, fileHash, StringComparison.Ordinal);
                if (mismatchDetected)
                {
                    if (_options.FailClosedOnMismatch)
                    {
                        await EmitTraceAsync(operationId, workspaceId, collectionId, mode, operationKind, primaryProvider, fallbackUsed, mismatchDetected, postgresError, stopwatch, cancellationToken).ConfigureAwait(false);
                        throw new InvalidOperationException("Relation provider switch mismatch detected.");
                    }

                    fallbackUsed = _options.FallbackToFileSystem;
                    if (fallbackUsed)
                    {
                        result = fileResult;
                    }
                }
            }
        }
        else
        {
            result = await fileOperation(cancellationToken).ConfigureAwait(false);
        }

        await EmitTraceAsync(operationId, workspaceId, collectionId, mode, operationKind, primaryProvider, fallbackUsed, mismatchDetected, postgresError, stopwatch, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private RelationGovernanceProviderMode ResolveMode(string workspaceId, string collectionId)
    {
        if (!_options.Enabled)
        {
            return RelationGovernanceProviderMode.FileSystemPrimary;
        }

        var scopedRule = ResolveScopedRule(workspaceId, collectionId);
        if (scopedRule is not null)
        {
            EnsureGuardedScopeAllowed(workspaceId, collectionId, scopedRule);
            return scopedRule.Mode;
        }

        if (_options.Mode == RelationGovernanceProviderMode.FileSystemPrimary)
        {
            return RelationGovernanceProviderMode.FileSystemPrimary;
        }

        if (_options.Mode is RelationGovernanceProviderMode.DualWriteOnly
            or RelationGovernanceProviderMode.ShadowRead
            or RelationGovernanceProviderMode.GuardedPostgresPrimary)
        {
            EnsureGuardedScopeAllowed(workspaceId, collectionId, scopedRule: null);
            return _options.Mode;
        }

        return RelationGovernanceProviderMode.FileSystemPrimary;
    }

    private RelationGovernanceScopedRule? ResolveScopedRule(string workspaceId, string collectionId)
    {
        foreach (var rule in _options.ScopedRules)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            if (string.Equals(rule.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(rule.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
            {
                return rule;
            }
        }

        return null;
    }

    private void EnsureGuardedScopeAllowed(
        string workspaceId,
        string collectionId,
        RelationGovernanceScopedRule? scopedRule)
    {
        if (_options.RequireReadinessGate && (!_readinessGatePassed || !_shadowReadQualityReady))
        {
            throw new InvalidOperationException("Relation provider switch readiness gate is not satisfied.");
        }

        if (scopedRule is not null)
        {
            return;
        }

        if (_options.AllowedWorkspaces.Count == 0 || _options.AllowedCollections.Count == 0)
        {
            throw new InvalidOperationException("Relation provider switch requires explicit workspace and collection allowlist.");
        }

        if (!_options.AllowedWorkspaces.Contains(workspaceId, StringComparer.OrdinalIgnoreCase)
            || !_options.AllowedCollections.Contains(collectionId, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Relation provider switch scope is not allowlisted.");
        }
    }

    private static IReadOnlyList<ContextRelation> FilterRelationsByMetadata(
        IReadOnlyList<ContextRelation> relations,
        IReadOnlyList<string> metadataKeys,
        string expectedValue)
    {
        return
        [
            .. relations.Where(relation =>
                relation.Metadata.Any(pair =>
                    metadataKeys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)
                    && string.Equals(pair.Value, expectedValue, StringComparison.OrdinalIgnoreCase)))
        ];
    }

    private static bool IsReplacementRelation(ContextRelation relation)
    {
        return string.Equals(relation.RelationType, ContextRelationTypes.SupersededBy, StringComparison.OrdinalIgnoreCase)
               || string.Equals(relation.RelationType, ContextRelationTypes.Replaces, StringComparison.OrdinalIgnoreCase)
               || string.Equals(relation.RelationType, ContextRelationTypes.ReplacedBy, StringComparison.OrdinalIgnoreCase);
    }

    private async Task EmitTraceAsync(
        string operationId,
        string workspaceId,
        string collectionId,
        RelationGovernanceProviderMode mode,
        string operationKind,
        string primaryProvider,
        bool fallbackUsed,
        bool mismatchDetected,
        string postgresError,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        stopwatch.Stop();
        var trace = new RelationGovernanceProviderSwitchTrace
        {
            OperationId = operationId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Mode = mode.ToString(),
            OperationKind = operationKind,
            PrimaryProvider = primaryProvider,
            FallbackUsed = fallbackUsed,
            MismatchDetected = mismatchDetected,
            PostgresError = postgresError,
            ReadinessGateVersion = "db2.5",
            DurationMs = stopwatch.Elapsed.TotalMilliseconds,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _traceSink(trace, cancellationToken).ConfigureAwait(false);
    }
}
