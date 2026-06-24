using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.Service.Infrastructure;

/// <summary>Relation governance scoped service mode 的状态与门禁读取服务。</summary>
public sealed class RelationGovernanceScopedServiceModeStatusService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly RelationGovernanceProviderSwitchOptions _options;

    public RelationGovernanceScopedServiceModeStatusService(RelationGovernanceProviderSwitchOptions options)
    {
        _options = options;
    }

    public PostgresRelationScopedServiceModeStatusResponse GetStatus()
    {
        var readiness = ReadJson<PostgresRelationGovernanceReadinessGateReport>(
            "storage/postgres/postgres-relation-governance-readiness-gate.json");
        var switchGate = ReadJson<PostgresRelationProviderSwitchGateReport>(
            "storage/postgres/postgres-relation-provider-switch-gate.json");
        var canary = ReadJson<PostgresRelationRuntimeCanaryReport>(
            "storage/postgres/postgres-relation-runtime-canary-report.json");

        var scopedRules = _options.ScopedRules.Where(static rule => rule.Enabled).ToArray();
        var workspaces = scopedRules.Length == 0
            ? ResolveAllowlist(_options.WorkspaceAllowlist, _options.AllowedWorkspaces)
            : scopedRules.Select(static rule => rule.WorkspaceId).Where(static item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var collections = scopedRules.Length == 0
            ? ResolveAllowlist(_options.CollectionAllowlist, _options.AllowedCollections)
            : scopedRules.Select(static rule => rule.CollectionId).Where(static item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var blocked = new List<string>();
        var diagnostics = new List<string>();
        var readinessPassed = readiness?.Passed == true;
        var switchPassed = switchGate?.Passed == true;
        var canaryPassed = string.Equals(canary?.Recommendation, "ReadyForScopedServiceMode", StringComparison.OrdinalIgnoreCase);
        var mismatchCount = (switchGate?.MismatchCount ?? 0) + (canary?.MismatchCount ?? 0);
        var postgresFailureCount = (switchGate?.PostgresReadFailureCount ?? 0)
                                   + (switchGate?.PostgresWriteFailureCount ?? 0)
                                   + (canary?.PostgresFailureCount ?? 0);

        if (!_options.Enabled)
        {
            diagnostics.Add("ScopedServiceModeDisabled");
        }

        var hasGuardedScopedRule = scopedRules.Any(static rule => rule.Mode == RelationGovernanceProviderMode.GuardedPostgresPrimary);
        if (_options.Mode != RelationGovernanceProviderMode.GuardedPostgresPrimary && !hasGuardedScopedRule)
        {
            blocked.Add("ModeNotGuardedPostgresPrimary");
        }

        if (workspaces.Count == 0 || collections.Count == 0)
        {
            blocked.Add("ScopedAllowlistMissing");
        }

        if (_options.RequireReadinessGate && !readinessPassed)
        {
            blocked.Add("GovernanceReadinessGateNotPassed");
        }

        if (_options.RequireReadinessGate && !switchPassed)
        {
            blocked.Add("ProviderSwitchGateNotPassed");
        }

        if (_options.RequireRuntimeCanaryPassed && !canaryPassed)
        {
            blocked.Add("RuntimeCanaryNotPassed");
        }

        if (mismatchCount > 0)
        {
            blocked.Add("MismatchCountNonZero");
        }

        if (postgresFailureCount > 0)
        {
            blocked.Add("PostgresFailureCountNonZero");
        }

        var enabled = _options.Enabled && blocked.Count == 0;
        return new PostgresRelationScopedServiceModeStatusResponse
        {
            CurrentMode = _options.Enabled
                ? hasGuardedScopedRule ? "ScopedRules" : _options.Mode.ToString()
                : RelationGovernanceProviderMode.FileSystemPrimary.ToString(),
            ActiveRuntimeProvider = enabled ? "PostgresRelationStore(scoped)" : "FileSystemRelationStore",
            AllowlistedWorkspaces = workspaces,
            AllowlistedCollections = collections,
            FallbackEnabled = _options.FallbackToFileSystem,
            ComparisonTraceEnabled = _options.ContinueComparisonTrace,
            GovernanceReadinessGatePassed = readinessPassed,
            ProviderSwitchGatePassed = switchPassed,
            RuntimeCanaryPassed = canaryPassed,
            MismatchCount = mismatchCount,
            PostgresFailureCount = postgresFailureCount,
            Diagnostics = diagnostics,
            BlockedReasons = blocked,
            Recommendation = enabled ? "ReadyForScopedServiceMode" : "FileSystemPrimary"
        };
    }

    public bool ShouldUsePostgresPrimary(string workspaceId, string? collectionId)
    {
        if (!_options.Enabled)
        {
            return false;
        }

        foreach (var rule in _options.ScopedRules)
        {
            if (!rule.Enabled || rule.Mode != RelationGovernanceProviderMode.GuardedPostgresPrimary)
            {
                continue;
            }

            if (string.Equals(rule.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(collectionId)
                && string.Equals(rule.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
            {
                var scopedStatus = GetStatus();
                if (scopedStatus.BlockedReasons.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Relation scoped service mode gate is not satisfied: {string.Join(",", scopedStatus.BlockedReasons)}");
                }

                return true;
            }
        }

        if (_options.Mode != RelationGovernanceProviderMode.GuardedPostgresPrimary)
        {
            return false;
        }

        var workspaces = ResolveAllowlist(_options.WorkspaceAllowlist, _options.AllowedWorkspaces);
        var collections = ResolveAllowlist(_options.CollectionAllowlist, _options.AllowedCollections);
        if (!workspaces.Contains(workspaceId, StringComparer.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(collectionId)
            || !collections.Contains(collectionId, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var status = GetStatus();
        if (status.BlockedReasons.Count > 0)
        {
            throw new InvalidOperationException(
                $"Relation scoped service mode gate is not satisfied: {string.Join(",", status.BlockedReasons)}");
        }

        return true;
    }

    public async Task AppendTraceAsync(RelationGovernanceProviderSwitchTrace trace, CancellationToken cancellationToken)
    {
        if (!_options.ContinueComparisonTrace)
        {
            return;
        }

        var path = Path.Combine("storage", "postgres", "postgres-relation-scoped-service-mode-traces.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var json = JsonSerializer.Serialize(trace, JsonOptions);
        await File.AppendAllTextAsync(path, json + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> ResolveAllowlist(
        IReadOnlyList<string> primary,
        IReadOnlyList<string> fallback)
    {
        var source = primary.Count > 0 ? primary : fallback;
        return [.. source
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static T? ReadJson<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return default;
        }
    }
}

/// <summary>Relation edge 的 scoped Postgres primary 包装；未命中 scope 时保持 FileSystem。</summary>
public sealed class ScopedRelationGovernanceStore : IRelationStore
{
    private readonly FileRelationStore _fileStore;
    private readonly PostgresRelationStore _postgresStore;
    private readonly RelationGovernanceProviderSwitchOptions _options;
    private readonly RelationGovernanceScopedServiceModeStatusService _status;

    public ScopedRelationGovernanceStore(
        FileRelationStore fileStore,
        PostgresRelationStore postgresStore,
        RelationGovernanceProviderSwitchOptions options,
        RelationGovernanceScopedServiceModeStatusService status)
    {
        _fileStore = fileStore;
        _postgresStore = postgresStore;
        _options = options;
        _status = status;
    }

    public Task SaveAsync(ContextRelation relation, CancellationToken cancellationToken = default)
        => ExecuteWriteAsync(
            "service-relation-save",
            relation.WorkspaceId,
            relation.CollectionId,
            "RelationWrite",
            token => _fileStore.SaveAsync(relation, token),
            token => _postgresStore.SaveAsync(relation, token),
            cancellationToken);

    public async Task SaveManyAsync(IEnumerable<ContextRelation> relations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relations);
        foreach (var relation in relations)
        {
            await SaveAsync(relation, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<IReadOnlyList<ContextRelation>> QueryAsync(ContextRelationQuery query, CancellationToken cancellationToken = default)
        => ExecuteReadAsync(
            "service-relation-query",
            query.WorkspaceId,
            query.CollectionId ?? string.Empty,
            "RelationList",
            token => _fileStore.QueryAsync(query, token),
            token => _postgresStore.QueryAsync(query, token),
            cancellationToken);

    public Task<IReadOnlyList<ContextRelation>> QueryForItemAsync(string workspaceId, string collectionId, string itemId, CancellationToken cancellationToken = default)
        => ExecuteReadAsync(
            "service-relation-item-query",
            workspaceId,
            collectionId,
            "RelationItemQuery",
            token => _fileStore.QueryForItemAsync(workspaceId, collectionId, itemId, token),
            token => _postgresStore.QueryForItemAsync(workspaceId, collectionId, itemId, token),
            cancellationToken);

    public Task<IReadOnlyList<ContextRelation>> QueryBySourceAsync(string workspaceId, string collectionId, string sourceId, CancellationToken cancellationToken = default)
        => ExecuteReadAsync(
            "service-relation-source-query",
            workspaceId,
            collectionId,
            "RelationSourceQuery",
            token => _fileStore.QueryBySourceAsync(workspaceId, collectionId, sourceId, token),
            token => _postgresStore.QueryBySourceAsync(workspaceId, collectionId, sourceId, token),
            cancellationToken);

    public Task<IReadOnlyList<ContextRelation>> QueryByTargetAsync(string workspaceId, string collectionId, string targetId, CancellationToken cancellationToken = default)
        => ExecuteReadAsync(
            "service-relation-target-query",
            workspaceId,
            collectionId,
            "RelationTargetQuery",
            token => _fileStore.QueryByTargetAsync(workspaceId, collectionId, targetId, token),
            token => _postgresStore.QueryByTargetAsync(workspaceId, collectionId, targetId, token),
            cancellationToken);

    public Task<IReadOnlyList<ContextRelation>> QueryByTypeAsync(string workspaceId, string collectionId, string relationType, CancellationToken cancellationToken = default)
        => ExecuteReadAsync(
            "service-relation-type-query",
            workspaceId,
            collectionId,
            "RelationTypeQuery",
            token => _fileStore.QueryByTypeAsync(workspaceId, collectionId, relationType, token),
            token => _postgresStore.QueryByTypeAsync(workspaceId, collectionId, relationType, token),
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
        if (!_status.ShouldUsePostgresPrimary(workspaceId, collectionId))
        {
            await fileWrite(cancellationToken).ConfigureAwait(false);
            await EmitTraceAsync(operationId, workspaceId, collectionId, RelationGovernanceProviderMode.FileSystemPrimary, operationKind, "FileSystem", false, false, string.Empty, 0, cancellationToken).ConfigureAwait(false);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var fallbackUsed = false;
        var postgresError = string.Empty;
        try
        {
            await postgresWrite(cancellationToken).ConfigureAwait(false);
            await fileWrite(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (_options.FallbackToFileSystem)
        {
            fallbackUsed = true;
            postgresError = ex.GetType().Name;
            await fileWrite(cancellationToken).ConfigureAwait(false);
        }

        await EmitTraceAsync(operationId, workspaceId, collectionId, _options.Mode, operationKind, "Postgres", fallbackUsed, false, postgresError, stopwatch.Elapsed.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> ExecuteReadAsync<T>(
        string operationId,
        string workspaceId,
        string collectionId,
        string operationKind,
        Func<CancellationToken, Task<T>> fileRead,
        Func<CancellationToken, Task<T>> postgresRead,
        CancellationToken cancellationToken)
    {
        if (!_status.ShouldUsePostgresPrimary(workspaceId, collectionId))
        {
            var fileOnly = await fileRead(cancellationToken).ConfigureAwait(false);
            await EmitTraceAsync(operationId, workspaceId, collectionId, RelationGovernanceProviderMode.FileSystemPrimary, operationKind, "FileSystem", false, false, string.Empty, 0, cancellationToken).ConfigureAwait(false);
            return fileOnly;
        }

        var stopwatch = Stopwatch.StartNew();
        var fallbackUsed = false;
        var mismatch = false;
        var postgresError = string.Empty;
        T postgresResult;
        try
        {
            postgresResult = await postgresRead(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (_options.FallbackToFileSystem)
        {
            fallbackUsed = true;
            postgresError = ex.GetType().Name;
            var fallback = await fileRead(cancellationToken).ConfigureAwait(false);
            await EmitTraceAsync(operationId, workspaceId, collectionId, _options.Mode, operationKind, "Postgres", fallbackUsed, false, postgresError, stopwatch.Elapsed.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            return fallback;
        }

        if (_options.ContinueComparisonTrace)
        {
            var fileResult = await fileRead(cancellationToken).ConfigureAwait(false);
            mismatch = !string.Equals(ComputeStableHash(postgresResult), ComputeStableHash(fileResult), StringComparison.Ordinal);
            if (mismatch)
            {
                await EmitTraceAsync(operationId, workspaceId, collectionId, _options.Mode, operationKind, "Postgres", fallbackUsed, true, postgresError, stopwatch.Elapsed.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
                if (_options.FailClosedOnMismatch)
                {
                    throw new InvalidOperationException("Relation scoped service mode mismatch detected.");
                }

                if (_options.FallbackToFileSystem)
                {
                    fallbackUsed = true;
                    return fileResult;
                }
            }
        }

        await EmitTraceAsync(operationId, workspaceId, collectionId, _options.Mode, operationKind, "Postgres", fallbackUsed, mismatch, postgresError, stopwatch.Elapsed.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
        return postgresResult;
    }

    private Task EmitTraceAsync(
        string operationId,
        string workspaceId,
        string collectionId,
        RelationGovernanceProviderMode mode,
        string operationKind,
        string primaryProvider,
        bool fallbackUsed,
        bool mismatchDetected,
        string postgresError,
        double durationMs,
        CancellationToken cancellationToken)
    {
        return _status.AppendTraceAsync(new RelationGovernanceProviderSwitchTrace
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
            ReadinessGateVersion = "db2.7",
            DurationMs = durationMs,
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    private static string ComputeStableHash<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes);
    }
}

/// <summary>Relation review 的 scoped Postgres primary 包装；未启用或未命中 scope 时走 FileSystem。</summary>
public sealed class ScopedRelationGovernanceReviewStore : IRelationReviewStore
{
    private readonly FileRelationReviewStore _fileStore;
    private readonly PostgresRelationReviewStore _postgresStore;
    private readonly RelationGovernanceProviderSwitchOptions _options;
    private readonly RelationGovernanceScopedServiceModeStatusService _status;

    public ScopedRelationGovernanceReviewStore(
        FileRelationReviewStore fileStore,
        PostgresRelationReviewStore postgresStore,
        RelationGovernanceProviderSwitchOptions options,
        RelationGovernanceScopedServiceModeStatusService status)
    {
        _fileStore = fileStore;
        _postgresStore = postgresStore;
        _options = options;
        _status = status;
    }

    public async Task AppendReviewAsync(RelationReviewRecord record, CancellationToken cancellationToken = default)
    {
        var collectionId = record.CollectionId ?? string.Empty;
        if (!_status.ShouldUsePostgresPrimary(record.WorkspaceId, collectionId))
        {
            await _fileStore.AppendReviewAsync(record, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await _postgresStore.AppendReviewAsync(record, cancellationToken).ConfigureAwait(false);
            await _fileStore.AppendReviewAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch when (_options.FallbackToFileSystem)
        {
            await _fileStore.AppendReviewAsync(record, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<RelationReviewRecord>> QueryReviewsAsync(string relationId, CancellationToken cancellationToken = default)
    {
        var status = _status.GetStatus();
        if (!status.BlockedReasons.Any()
            && _options.Enabled
            && _options.Mode == RelationGovernanceProviderMode.GuardedPostgresPrimary)
        {
            try
            {
                return await _postgresStore.QueryReviewsAsync(relationId, cancellationToken).ConfigureAwait(false);
            }
            catch when (_options.FallbackToFileSystem)
            {
                return await _fileStore.QueryReviewsAsync(relationId, cancellationToken).ConfigureAwait(false);
            }
        }

        return await _fileStore.QueryReviewsAsync(relationId, cancellationToken).ConfigureAwait(false);
    }
}
