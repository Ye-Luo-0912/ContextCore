using System.Text;
using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// R29 WP-E-5：PostgreSQL User Feedback Ledger 持久化实现。
/// </summary>
/// <remarks>
/// 设计原则：
///   1. 实现 <see cref="IUserFeedbackLedger"/>：读 API（QueryFeedbackAsync /
///      GetLatestFeedbackForCandidateAsync）+ 异步写 API（<see cref="AppendFeedbackAsync"/>）。
///   2. 写入由 Service API 端点 POST /api/utility-ledger/feedback 通过 IUserFeedbackLedger.AppendFeedbackAsync 调用；
///      与 <see cref="InMemoryUserFeedbackLedgerStore"/> 实现同一契约，调用方无需感知存储后端。
///   3. 表 <c>user_feedback_entries</c> 反规范化 workspace_id / collection_id / decision_id /
///      candidate_item_id / kind / given_by / given_at 等字段以便索引查询；完整 <see cref="UserFeedbackEntry"/>
///      对象保存在 <c>data jsonb</c>，由 store 反序列化。
///   4. 幂等：同 <see cref="UserFeedbackEntry.IdempotencyKey"/> 重复写入时由 ON CONFLICT DO UPDATE 覆盖（保留最新反馈）。
///   5. 关联校验：写入时通过 EXISTS 子查询验证 (workspace_id, collection_id, decision_id, candidate_item_id)
///      在 utility_ledger_entries 中存在；否则抛出 <see cref="InvalidOperationException"/>（强一致性保证，
///      防止用户反馈对不存在的决策条目）。
///   6. QueryFeedbackAsync 按 given_at DESC 排序（与 InMemory 实现语义一致）。
/// </remarks>
public sealed class PostgresUserFeedbackLedgerStore : PostgresStoreBase, IUserFeedbackLedger
{
    public PostgresUserFeedbackLedgerStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
    public async Task AppendFeedbackAsync(
        UserFeedbackEntry feedback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 关联校验：验证 (workspace_id, collection_id, decision_id, candidate_item_id) 在 utility_ledger_entries 中存在。
            // 防止用户反馈对不存在的决策条目（Postgres 实现做严格校验；InMemory 实现跳过以保持测试友好）。
            using (var checkCommand = connection.CreateCommand())
            {
                checkCommand.Transaction = transaction;
                checkCommand.CommandTimeout = Options.CommandTimeoutSeconds;
                checkCommand.CommandText = $"""
SELECT 1
FROM {Table("utility_ledger_entries")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND decision_id = @decision_id
  AND candidate_item_id = @candidate_item_id
LIMIT 1;
""";
                checkCommand.Parameters.AddWithValue("workspace_id", feedback.WorkspaceId);
                checkCommand.Parameters.AddWithValue("collection_id", feedback.CollectionId);
                checkCommand.Parameters.AddWithValue("decision_id", feedback.DecisionId);
                checkCommand.Parameters.AddWithValue("candidate_item_id", feedback.CandidateItemId);

                var exists = await checkCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (exists is null)
                {
                    throw new InvalidOperationException(
                        $"UserFeedback 关联校验失败：(workspace_id={feedback.WorkspaceId}, collection_id={feedback.CollectionId}, "
                        + $"decision_id={feedback.DecisionId}, candidate_item_id={feedback.CandidateItemId}) "
                        + "在 utility_ledger_entries 中不存在匹配条目；请确保用户反馈对应的决策已完成物化。");
                }
            }

            // 写入反馈条目：idempotency_key 重复时由 ON CONFLICT DO UPDATE 覆盖（保留最新反馈）。
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandTimeout = Options.CommandTimeoutSeconds;
                command.CommandText = $"""
INSERT INTO {Table("user_feedback_entries")} (
    feedback_entry_id, workspace_id, collection_id, decision_id, candidate_item_id,
    kind, feedback_value, feedback_text, given_by, given_at, idempotency_key, data)
VALUES (
    @feedback_entry_id, @workspace_id, @collection_id, @decision_id, @candidate_item_id,
    @kind, @feedback_value, @feedback_text, @given_by, @given_at, @idempotency_key, @data)
ON CONFLICT (idempotency_key) DO UPDATE SET
    feedback_entry_id = EXCLUDED.feedback_entry_id,
    workspace_id = EXCLUDED.workspace_id,
    collection_id = EXCLUDED.collection_id,
    decision_id = EXCLUDED.decision_id,
    candidate_item_id = EXCLUDED.candidate_item_id,
    kind = EXCLUDED.kind,
    feedback_value = EXCLUDED.feedback_value,
    feedback_text = EXCLUDED.feedback_text,
    given_by = EXCLUDED.given_by,
    given_at = EXCLUDED.given_at,
    data = EXCLUDED.data;
""";
                command.Parameters.AddWithValue("feedback_entry_id", feedback.FeedbackEntryId);
                command.Parameters.AddWithValue("workspace_id", feedback.WorkspaceId);
                command.Parameters.AddWithValue("collection_id", feedback.CollectionId);
                command.Parameters.AddWithValue("decision_id", feedback.DecisionId);
                command.Parameters.AddWithValue("candidate_item_id", feedback.CandidateItemId);
                command.Parameters.AddWithValue("kind", feedback.Kind.ToString());
                command.Parameters.AddWithValue("feedback_value", feedback.FeedbackValue);
                command.Parameters.AddWithValue("feedback_text", (object?)feedback.FeedbackText ?? DBNull.Value);
                // given_by DDL 为 NOT NULL；null 规范化为空字符串。
                command.Parameters.AddWithValue("given_by", feedback.GivenBy ?? string.Empty);
                command.Parameters.AddWithValue("given_at", feedback.GivenAt);
                command.Parameters.AddWithValue("idempotency_key", feedback.IdempotencyKey);
                AddJson(command, "data", feedback);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* 不掩盖原始异常 */ }
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserFeedbackEntry>> QueryFeedbackAsync(
        UserFeedbackQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var where = new StringBuilder("WHERE workspace_id = @workspace_id");
        command.Parameters.AddWithValue("workspace_id", query.WorkspaceId);

        if (query.CollectionId is not null)
        {
            where.Append(" AND collection_id = @collection_id");
            command.Parameters.AddWithValue("collection_id", query.CollectionId);
        }
        if (query.DecisionId is not null)
        {
            where.Append(" AND decision_id = @decision_id");
            command.Parameters.AddWithValue("decision_id", query.DecisionId);
        }
        if (query.CandidateItemId is not null)
        {
            where.Append(" AND candidate_item_id = @candidate_item_id");
            command.Parameters.AddWithValue("candidate_item_id", query.CandidateItemId);
        }
        if (query.Kind is not null)
        {
            where.Append(" AND kind = @kind");
            command.Parameters.AddWithValue("kind", query.Kind.Value.ToString());
        }
        if (query.GivenBy is not null)
        {
            where.Append(" AND given_by = @given_by");
            command.Parameters.AddWithValue("given_by", query.GivenBy);
        }
        if (query.Since is not null)
        {
            where.Append(" AND given_at >= @since");
            command.Parameters.AddWithValue("since", query.Since.Value);
        }
        if (query.Until is not null)
        {
            where.Append(" AND given_at <= @until");
            command.Parameters.AddWithValue("until", query.Until.Value);
        }

        var limitClause = query.Take > 0 ? "LIMIT @take" : string.Empty;
        if (query.Take > 0)
        {
            command.Parameters.AddWithValue("take", query.Take);
        }

        command.CommandText = $"""
SELECT data
FROM {Table("user_feedback_entries")}
{where}
ORDER BY given_at DESC
{limitClause};
""";

        return await ExecuteReaderJsonAsync<UserFeedbackEntry>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<UserFeedbackEntry?> GetLatestFeedbackForCandidateAsync(
        string workspaceId,
        string collectionId,
        string candidateItemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateItemId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("user_feedback_entries")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id
  AND candidate_item_id = @candidate_item_id
ORDER BY given_at DESC
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        command.Parameters.AddWithValue("candidate_item_id", candidateItemId);

        return await ExecuteScalarJsonAsync<UserFeedbackEntry>(command, cancellationToken).ConfigureAwait(false);
    }
}
