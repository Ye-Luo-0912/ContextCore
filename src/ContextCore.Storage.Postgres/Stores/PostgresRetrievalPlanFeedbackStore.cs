using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 持久化检索计划反馈存储。
/// 记录自适应检索规划器的每轮检索结果反馈（<c>retrieval_plan_feedback</c> 表），
/// 跨进程重启保留，供规划器按计划签名聚合自适应策略。
/// </summary>
/// <remarks>
/// 单条记录为 (plan_signature, workspace_id, collection_id, purpose, policy_version,
/// retrieval_profile, task_class, query_text, hits_returned, budget_exceeded, effective,
/// recorded_at, feedback_id, idempotency_key, source, confidence, outcome_quality, subject)；
/// 查询以工作区为隔离边界（WHERE workspace_id = @ws），配合 <see cref="ClearAsync"/>
/// 支持按签名 / 按工作区 / 全局重置自适应状态。
/// 幂等（P0-16）：<c>(plan_signature, idempotency_key)</c> 部分唯一索引
/// （WHERE idempotency_key IS NOT NULL）+ INSERT ... ON CONFLICT DO NOTHING——
/// 重放 / 重复提交不产生重复反馈。
/// </remarks>
public sealed class PostgresRetrievalPlanFeedbackStore : PostgresStoreBase, IRetrievalPlanFeedbackStore
{
    public PostgresRetrievalPlanFeedbackStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async ValueTask RecordAsync(RetrievalPlanFeedback feedback, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedback.PlanSignature);

        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("retrieval_plan_feedback")}
    (plan_signature, workspace_id, collection_id, purpose, policy_version, retrieval_profile, task_class,
     query_text, hits_returned, budget_exceeded, effective, recorded_at,
     feedback_id, idempotency_key, source, confidence, outcome_quality, subject)
VALUES (@plan_signature, @workspace_id, @collection_id, @purpose, @policy_version, @retrieval_profile, @task_class,
        @query_text, @hits_returned, @budget_exceeded, @effective, @recorded_at,
        @feedback_id, @idempotency_key, @source, @confidence, @outcome_quality, @subject)
ON CONFLICT (plan_signature, idempotency_key) WHERE idempotency_key IS NOT NULL DO NOTHING;
""";
        command.Parameters.AddWithValue("plan_signature", feedback.PlanSignature);
        command.Parameters.AddWithValue("workspace_id", (object?)(feedback.WorkspaceId ?? string.Empty) ?? string.Empty);
        command.Parameters.AddWithValue("collection_id", (object?)feedback.CollectionId ?? DBNull.Value);
        command.Parameters.AddWithValue("purpose", (object?)feedback.Purpose ?? DBNull.Value);
        command.Parameters.AddWithValue("policy_version", (object?)feedback.PolicyVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("retrieval_profile", (object?)feedback.RetrievalProfile ?? DBNull.Value);
        command.Parameters.AddWithValue("task_class", (object?)feedback.TaskClass ?? DBNull.Value);
        command.Parameters.AddWithValue("query_text", (object?)feedback.QueryText ?? string.Empty);
        command.Parameters.AddWithValue("hits_returned", feedback.HitsReturned);
        command.Parameters.AddWithValue("budget_exceeded", feedback.BudgetExceeded);
        command.Parameters.AddWithValue("effective", feedback.Effective);
        command.Parameters.AddWithValue("recorded_at", feedback.RecordedAtUtc);
        command.Parameters.AddWithValue("feedback_id", (object?)feedback.FeedbackId ?? DBNull.Value);
        command.Parameters.AddWithValue("idempotency_key", (object?)feedback.IdempotencyKey ?? DBNull.Value);
        command.Parameters.AddWithValue("source", (int)feedback.Source);
        command.Parameters.AddWithValue("confidence", feedback.Confidence);
        command.Parameters.AddWithValue("outcome_quality", feedback.OutcomeQuality);
        command.Parameters.AddWithValue("subject", (object?)feedback.Subject ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<RetrievalPlanFeedback>> ListRecentAsync(
        string workspaceId,
        string planSignature,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(planSignature))
        {
            return Array.Empty<RetrievalPlanFeedback>();
        }

        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT plan_signature, workspace_id, collection_id, purpose, policy_version, retrieval_profile, task_class,
       query_text, hits_returned, budget_exceeded, effective, recorded_at,
       feedback_id, idempotency_key, source, confidence, outcome_quality, subject
FROM {Table("retrieval_plan_feedback")}
WHERE workspace_id = @workspace_id AND plan_signature = @plan_signature
ORDER BY recorded_at DESC
LIMIT @limit;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId ?? string.Empty);
        command.Parameters.AddWithValue("plan_signature", planSignature);
        command.Parameters.AddWithValue("limit", Math.Max(1, limit));

        var results = new List<RetrievalPlanFeedback>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(ReadFeedback(reader));
        }
        return results;
    }

    public async ValueTask<int> ClearAsync(string? workspaceId, string? planSignature = null, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        if (workspaceId is null)
        {
            // 全局重置：清除全部工作区的反馈。
            command.CommandText = $"DELETE FROM {Table("retrieval_plan_feedback")};";
        }
        else if (string.IsNullOrWhiteSpace(planSignature))
        {
            // 按工作区清除。
            command.CommandText = $"DELETE FROM {Table("retrieval_plan_feedback")} WHERE workspace_id = @workspace_id;";
            command.Parameters.AddWithValue("workspace_id", workspaceId.Trim());
        }
        else
        {
            // 按工作区 + 签名清除。
            command.CommandText = $"DELETE FROM {Table("retrieval_plan_feedback")} WHERE workspace_id = @workspace_id AND plan_signature = @plan_signature;";
            command.Parameters.AddWithValue("workspace_id", workspaceId.Trim());
            command.Parameters.AddWithValue("plan_signature", planSignature);
        }

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static RetrievalPlanFeedback ReadFeedback(System.Data.Common.DbDataReader reader)
    {
        return new RetrievalPlanFeedback
        {
            PlanSignature = reader.GetString(reader.GetOrdinal("plan_signature")),
            WorkspaceId = reader.GetString(reader.GetOrdinal("workspace_id")),
            CollectionId = ReadNullableString(reader, "collection_id"),
            Purpose = ReadNullableString(reader, "purpose"),
            PolicyVersion = ReadNullableString(reader, "policy_version"),
            RetrievalProfile = ReadNullableString(reader, "retrieval_profile"),
            TaskClass = ReadNullableString(reader, "task_class"),
            QueryText = reader.GetString(reader.GetOrdinal("query_text")),
            HitsReturned = reader.GetInt32(reader.GetOrdinal("hits_returned")),
            BudgetExceeded = reader.GetBoolean(reader.GetOrdinal("budget_exceeded")),
            Effective = reader.GetBoolean(reader.GetOrdinal("effective")),
            RecordedAtUtc = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("recorded_at")),
            FeedbackId = ReadNullableString(reader, "feedback_id"),
            IdempotencyKey = ReadNullableString(reader, "idempotency_key"),
            Source = (RetrievalFeedbackSource)reader.GetInt32(reader.GetOrdinal("source")),
            Confidence = reader.GetDouble(reader.GetOrdinal("confidence")),
            OutcomeQuality = reader.GetDouble(reader.GetOrdinal("outcome_quality")),
            Subject = ReadNullableString(reader, "subject")
        };
    }

    private static string? ReadNullableString(System.Data.Common.DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}
