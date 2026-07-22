using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL Policy Registry 持久化存储。
/// WS-A：替代 in-memory <c>DefaultPolicyRegistry</c>，
/// 让 Postgres provider 在 HA 场景下能持久化 Policy Bundle 注册 + Activation CAS 激活。
/// </summary>
/// <remarks>
/// 设计要点：
///   1. <c>policy_bundles</c> 表：(bundle_id, version) 复合主键 — bundle 全局不可变。
///      <see cref="RegisterBundleAsync"/> 使用 INSERT ON CONFLICT DO NOTHING 实现 insert-if-absent；
///      相同 (BundleId, Version) 已存在时抛 <see cref="InvalidOperationException"/>。
///   2. <c>policy_activations</c> 表：(workspace_id, collection_id) 主键 — 每个作用域仅一条 activation。
///      <see cref="TryActivateAsync"/> 使用 CAS 语义：
///      expectedEpoch=0 时 INSERT ON CONFLICT DO NOTHING（首次激活）；
///      expectedEpoch>0 时 UPDATE WHERE epoch = @expected_epoch（CAS 推进）。
///   3. <see cref="GetActiveBundleAsync"/> 精确读取 (BundleId, BundleVersion)，
///      不漂移到"最新版本"（P1-3 版本固定）；未激活时返回全局默认 bundle。
///   4. 完整对象保存在 <c>data jsonb</c>，由 store 反序列化；反规范化字段用于索引查询。
///   5. 与 PostgresPipelineRunStore / PostgresAgentCheckpointStore 设计模式对齐。
/// </remarks>
public sealed class PostgresPolicyRegistry : PostgresStoreBase, IPolicyRegistry
{
    public PostgresPolicyRegistry(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    // ---------- Bundle 注册 ----------

    /// <inheritdoc />
    public async Task RegisterBundleAsync(
        ContextPolicyBundle bundle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("policy_bundles")} (bundle_id, version, is_superseded, created_at, data)
VALUES (@bundle_id, @version, @is_superseded, @created_at, @data)
ON CONFLICT (bundle_id, version) DO NOTHING;
""";
        command.Parameters.AddWithValue("bundle_id", bundle.BundleId);
        command.Parameters.AddWithValue("version", bundle.Version);
        command.Parameters.AddWithValue("is_superseded", bundle.IsSuperseded);
        command.Parameters.AddWithValue("created_at", bundle.CreatedAt);
        AddJson(command, "data", bundle);
        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rowsAffected == 0)
        {
            throw new InvalidOperationException(
                $"Bundle already registered: BundleId={bundle.BundleId}, Version={bundle.Version}. " +
                "Bundle is immutable; supersede by registering a new bundle with a different BundleId or Version.");
        }
    }

    /// <inheritdoc />
    public async Task<ContextPolicyBundle?> GetBundleAsync(
        string bundleId,
        string? version,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        if (version is not null)
        {
            // 精确版本查找
            command.CommandText = $"""
SELECT data
FROM {Table("policy_bundles")}
WHERE bundle_id = @bundle_id AND version = @version
LIMIT 1;
""";
            command.Parameters.AddWithValue("bundle_id", bundleId);
            command.Parameters.AddWithValue("version", version);
        }
        else
        {
            // version = null → 返回该 BundleId 下最新非 superseded 版本（按 created_at DESC）
            command.CommandText = $"""
SELECT data
FROM {Table("policy_bundles")}
WHERE bundle_id = @bundle_id AND is_superseded = false
ORDER BY created_at DESC
LIMIT 1;
""";
            command.Parameters.AddWithValue("bundle_id", bundleId);
        }

        return await ExecuteScalarJsonAsync<ContextPolicyBundle>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContextPolicyBundle>> ListBundlesAsync(
        bool includeSuperseded = false,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        if (includeSuperseded)
        {
            command.CommandText = $"""
SELECT data
FROM {Table("policy_bundles")}
ORDER BY bundle_id, created_at DESC;
""";
        }
        else
        {
            command.CommandText = $"""
SELECT data
FROM {Table("policy_bundles")}
WHERE is_superseded = false
ORDER BY bundle_id, created_at DESC;
""";
        }
        return await ExecuteReaderJsonAsync<ContextPolicyBundle>(command, cancellationToken).ConfigureAwait(false);
    }

    // ---------- Activation ----------

    /// <inheritdoc />
    public async Task<ContextPolicyBundle> GetActiveBundleAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        // Step 1: 读取 activation 记录
        var activation = await GetActivationAsync(workspaceId, collectionId, cancellationToken).ConfigureAwait(false);
        if (activation is null)
        {
            return CreateDefaultBundle();
        }

        // Step 2: P1-3 — 精确读取 (BundleId, BundleVersion)，不漂移到"最新版本"
        var bundle = await GetBundleAsync(activation.BundleId, activation.BundleVersion, cancellationToken)
            .ConfigureAwait(false);
        if (bundle is not null)
        {
            return bundle;
        }

        // activation 存在但 bundle 已删除 → 返回默认 bundle（防御性）
        return CreateDefaultBundle();
    }

    /// <inheritdoc />
    public async Task<PolicyActivation?> GetActivationAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("policy_activations")}
WHERE workspace_id = @workspace_id AND collection_id = @collection_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        return await ExecuteScalarJsonAsync<PolicyActivation>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TryActivateAsync(
        PolicyActivation next,
        long expectedEpoch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(next);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        if (expectedEpoch == 0)
        {
            // 首次激活：INSERT ON CONFLICT DO NOTHING
            await using var command = connection.CreateCommand();
            command.CommandTimeout = Options.CommandTimeoutSeconds;
            command.CommandText = $"""
INSERT INTO {Table("policy_activations")} (
    workspace_id, collection_id, bundle_id, bundle_version, bundle_content_hash,
    epoch, activated_at, data)
VALUES (
    @workspace_id, @collection_id, @bundle_id, @bundle_version, @bundle_content_hash,
    1, @activated_at, @data)
ON CONFLICT (workspace_id, collection_id) DO NOTHING;
""";
            command.Parameters.AddWithValue("workspace_id", next.WorkspaceId);
            command.Parameters.AddWithValue("collection_id", next.CollectionId);
            command.Parameters.AddWithValue("bundle_id", next.BundleId);
            command.Parameters.AddWithValue("bundle_version", next.BundleVersion);
            command.Parameters.AddWithValue("bundle_content_hash", next.BundleContentHash);
            command.Parameters.AddWithValue("activated_at", next.ActivatedAt);
            // 首次激活：epoch = 1
            var firstActivation = next with { Epoch = 1 };
            AddJson(command, "data", firstActivation);
            var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return rowsAffected > 0;
        }

        // CAS 推进：UPDATE WHERE epoch = @expected_epoch
        await using var updateCommand = connection.CreateCommand();
        updateCommand.CommandTimeout = Options.CommandTimeoutSeconds;
        updateCommand.CommandText = $"""
UPDATE {Table("policy_activations")} SET
    bundle_id = @bundle_id,
    bundle_version = @bundle_version,
    bundle_content_hash = @bundle_content_hash,
    epoch = epoch + 1,
    activated_at = @activated_at,
    data = @data
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND epoch = @expected_epoch;
""";
        updateCommand.Parameters.AddWithValue("workspace_id", next.WorkspaceId);
        updateCommand.Parameters.AddWithValue("collection_id", next.CollectionId);
        updateCommand.Parameters.AddWithValue("bundle_id", next.BundleId);
        updateCommand.Parameters.AddWithValue("bundle_version", next.BundleVersion);
        updateCommand.Parameters.AddWithValue("bundle_content_hash", next.BundleContentHash);
        updateCommand.Parameters.AddWithValue("activated_at", next.ActivatedAt);
        updateCommand.Parameters.AddWithValue("expected_epoch", expectedEpoch);
        // CAS 推进：epoch = expectedEpoch + 1（由 SQL epoch + 1 实现）
        var casActivation = next with { Epoch = expectedEpoch + 1 };
        AddJson(updateCommand, "data", casActivation);
        var rowsUpdated = await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return rowsUpdated > 0;
    }

    // ----------------------------------------------------------------------
    // 默认 bundle 构造（与 ContextCore.Core.Services.Policy.DefaultPolicyBundleFactory 对齐）
    // ----------------------------------------------------------------------

    /// <summary>
    /// 创建全局默认 bundle。与 <c>DefaultPolicyBundleFactory.Create()</c> 保持值对齐。
    /// 此处内联以避免 Postgres 项目引用 ContextCore.Core 程序集。
    /// </summary>
    private static ContextPolicyBundle CreateDefaultBundle()
    {
        return new ContextPolicyBundle
        {
            BundleId = "bundle-default",
            Version = "2026-07/default",
            Policies = new ContextPolicySet(),
            Safety = new SafetyProfile
            {
                ProfileId = "safety-default-v1",
                AllowDeprecatedUsedByActiveChain = true,
                AllowDuplicateReference = false,
                RequiredTags = Array.Empty<string>(),
                ForbiddenTags = Array.Empty<string>()
            },
            Budget = new BudgetProfile
            {
                ProfileId = "budget-default-v1",
                DefaultTokenBudget = 8000,
                DefaultTopK = 50,
                SectionRatios = CreateDefaultSectionRatios(),
                StrictBudgetEnforcement = true
            },
            Routing = new RoutingProfile
            {
                ProfileId = "routing-default-v1",
                EnableModelScoring = false,
                ModelArtifactId = null,
                DeterministicWeight = 1.0,
                ModelWeight = 0.0,
                ModelConfidenceThreshold = 0.70,
                EnabledExperts = Array.Empty<string>()
            },
            ModelArtifacts = Array.Empty<ModelArtifactReference>(),
            Rollout = null,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>默认 section 比例分配（对齐 DefaultPolicyBundleFactory 默认模板）。</summary>
    private static IReadOnlyDictionary<string, double> CreateDefaultSectionRatios()
    {
        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["working_memory"] = 0.30,
            ["recent_context"] = 0.20,
            ["related_context"] = 0.20,
            ["stable_memory"] = 0.20,
            ["global_context"] = 0.10
        };
    }
}
