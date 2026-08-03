using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 持久化检索计划反馈存储。
/// 记录自适应检索规划器的每轮检索结果反馈（<c>retrieval_plan_feedback</c> 表），
/// 跨进程重启保留，供规划器按计划签名聚合自适应策略。
/// </summary>
/// <remarks>
/// 单条记录为 (plan_signature, query_text, hits_returned, budget_exceeded, effective, recorded_at)；
/// 查询按 recorded_at 倒序返回最新条目，配合 <see cref="ClearAsync"/> 支持自适应状态重置。
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
    (plan_signature, query_text, hits_returned, budget_exceeded, effective, recorded_at)
VALUES (@plan_signature, @query_text, @hits_returned, @budget_exceeded, @effective, @recorded_at);
""";
        command.Parameters.AddWithValue("plan_signature", feedback.PlanSignature);
        command.Parameters.AddWithValue("query_text", (object?)feedback.QueryText ?? string.Empty);
        command.Parameters.AddWithValue("hits_returned", feedback.HitsReturned);
        command.Parameters.AddWithValue("budget_exceeded", feedback.BudgetExceeded);
        command.Parameters.AddWithValue("effective", feedback.Effective);
        command.Parameters.AddWithValue("recorded_at", feedback.RecordedAtUtc);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<RetrievalPlanFeedback>> ListRecentAsync(
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
SELECT plan_signature, query_text, hits_returned, budget_exceeded, effective, recorded_at
FROM {Table("retrieval_plan_feedback")}
WHERE plan_signature = @plan_signature
ORDER BY recorded_at DESC
LIMIT @limit;
""";
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

    public async ValueTask<int> ClearAsync(string? planSignature = null, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        if (string.IsNullOrWhiteSpace(planSignature))
        {
            command.CommandText = $"DELETE FROM {Table("retrieval_plan_feedback")};";
        }
        else
        {
            command.CommandText = $"DELETE FROM {Table("retrieval_plan_feedback")} WHERE plan_signature = @plan_signature;";
            command.Parameters.AddWithValue("plan_signature", planSignature);
        }

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static RetrievalPlanFeedback ReadFeedback(System.Data.Common.DbDataReader reader)
    {
        var signatureOrdinal = reader.GetOrdinal("plan_signature");
        var queryTextOrdinal = reader.GetOrdinal("query_text");
        var hitsOrdinal = reader.GetOrdinal("hits_returned");
        var budgetOrdinal = reader.GetOrdinal("budget_exceeded");
        var effectiveOrdinal = reader.GetOrdinal("effective");
        var recordedAtOrdinal = reader.GetOrdinal("recorded_at");

        return new RetrievalPlanFeedback
        {
            PlanSignature = reader.GetString(signatureOrdinal),
            QueryText = reader.GetString(queryTextOrdinal),
            HitsReturned = reader.GetInt32(hitsOrdinal),
            BudgetExceeded = reader.GetBoolean(budgetOrdinal),
            Effective = reader.GetBoolean(effectiveOrdinal),
            RecordedAtUtc = reader.GetFieldValue<DateTimeOffset>(recordedAtOrdinal)
        };
    }
}
