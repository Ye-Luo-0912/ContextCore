using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Evaluation.Learning;

/// <summary>
/// 从反馈事件生成不可变快照：内容寻址（相同输入产出相同 SnapshotId）、
/// 排除删除请求与已撤销事件、按事件 ID 哈希稳定切分训练/评测（隔离）、
/// 记录 lineage（源事件 ID、排除清单、特征版本、切分比例）。
/// 任何基于快照的训练结果都必须能追到 SnapshotId。
/// </summary>
public sealed class LearningFeedbackSnapshotBuilder
{
    public const string DefaultFeatureVersion = "learning-feedback-snapshot/v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 构建不可变快照。devFraction 为调参分桶比例、evalFraction 为评测分桶比例，
    /// 按事件 ID + 特征版本稳定哈希分桶，训练/调参/评测集合互不重叠。
    /// </summary>
    /// <param name="events">全部候选反馈事件（含撤销事件与待删除事件）。</param>
    /// <param name="deletionRequests">删除请求；命中的事件不进快照。</param>
    /// <param name="devFraction">调参分桶比例。</param>
    /// <param name="evalFraction">评测分桶比例。</param>
    /// <param name="featureVersion">特征版本；缺省用当前快照版本。</param>
    /// <param name="policyVersion">快照针对的策略版本；为空表示覆盖全部。</param>
    public LearningFeedbackSnapshot Build(
        IReadOnlyList<LearningFeedbackEvent> events,
        IReadOnlyList<FeedbackDeletionRequest> deletionRequests,
        double devFraction = 0.1,
        double evalFraction = 0.2,
        string? featureVersion = null,
        string? policyVersion = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(deletionRequests);

        var resolvedFeatureVersion = string.IsNullOrWhiteSpace(featureVersion)
            ? DefaultFeatureVersion
            : featureVersion.Trim();
        var resolvedPolicyVersion = policyVersion?.Trim() ?? string.Empty;
        var devPercent = (int)Math.Round(Math.Clamp(devFraction, 0.0, 1.0) * 100);
        var evalPercent = (int)Math.Round(Math.Clamp(evalFraction, 0.0, 1.0) * 100);

        var deletedIds = deletionRequests
            .Select(request => request.FeedbackId.Trim())
            .Where(static id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 撤销事件本身是治理记录，不进入数据；其目标是已撤销事件，同样排除。
        var revokedTargetIds = events
            .Where(item => string.Equals(item.FeedbackKind, LearningFeedbackKinds.Revoke, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.RevokesFeedbackId.Trim())
            .Where(static id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var includedEvents = events
            .Where(item => !string.Equals(item.FeedbackKind, LearningFeedbackKinds.Revoke, StringComparison.OrdinalIgnoreCase))
            .Where(item => !deletedIds.Contains(item.FeedbackId))
            .Where(item => !revokedTargetIds.Contains(item.FeedbackId))
            .OrderBy(item => item.FeedbackId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var splitAssignment = new Dictionary<string, LearningSnapshotSplit>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in includedEvents)
        {
            splitAssignment[item.FeedbackId] = Bucket(item.FeedbackId, resolvedFeatureVersion, devPercent, evalPercent);
        }

        var sourceEventIds = events
            .Select(item => item.FeedbackId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var lineageSignature = ComputeLineageSignature(
            resolvedFeatureVersion,
            resolvedPolicyVersion,
            events,
            deletedIds,
            revokedTargetIds,
            devPercent,
            evalPercent);

        return new LearningFeedbackSnapshot
        {
            SnapshotId = "snap_" + lineageSignature[..24].ToLowerInvariant(),
            CreatedAt = DateTimeOffset.UtcNow,
            FeatureVersion = resolvedFeatureVersion,
            PolicyVersion = resolvedPolicyVersion,
            Events = includedEvents,
            SourceEventIds = sourceEventIds,
            DeletedEventIds = deletedIds.OrderBy(static id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
            RevokedEventIds = revokedTargetIds.OrderBy(static id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
            SplitAssignment = splitAssignment,
            DevPercent = devPercent,
            EvalPercent = evalPercent,
            TrainCount = splitAssignment.Values.Count(split => split == LearningSnapshotSplit.Train),
            DevCount = splitAssignment.Values.Count(split => split == LearningSnapshotSplit.Dev),
            EvalCount = splitAssignment.Values.Count(split => split == LearningSnapshotSplit.Eval),
            LineageSignature = lineageSignature
        };
    }

    /// <summary>从反馈事件存储构建快照的便捷重载。</summary>
    public async Task<LearningFeedbackSnapshot> BuildAsync(
        ILearningFeedbackStore store,
        LearningFeedbackEventQuery query,
        IReadOnlyList<FeedbackDeletionRequest> deletionRequests,
        double devFraction = 0.1,
        double evalFraction = 0.2,
        string? featureVersion = null,
        string? policyVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var events = await store.QueryAsync(query, cancellationToken).ConfigureAwait(false);
        return Build(events, deletionRequests, devFraction, evalFraction, featureVersion, policyVersion);
    }

    /// <summary>
    /// 校验快照可追溯性：用给定事件集重算输入指纹与源事件 ID 集合，
    /// 与快照记录一致时返回 true（快照是该事件集的一个忠实、不可变推导）。
    /// </summary>
    public static bool Verify(
        LearningFeedbackSnapshot snapshot,
        IReadOnlyList<LearningFeedbackEvent> events)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(events);

        var sourceEventIds = events
            .Select(item => item.FeedbackId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!sourceEventIds.SequenceEqual(snapshot.SourceEventIds, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var deletedIds = snapshot.DeletedEventIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var revokedIds = snapshot.RevokedEventIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var recomputed = ComputeLineageSignature(
            snapshot.FeatureVersion,
            snapshot.PolicyVersion,
            events,
            deletedIds,
            revokedIds,
            snapshot.DevPercent,
            snapshot.EvalPercent);
        return string.Equals(recomputed, snapshot.LineageSignature, StringComparison.Ordinal);
    }

    /// <summary>
    /// 离线重放：按策略版本过滤快照事件；可再按训练/评测分桶过滤。
    /// 快照未限定策略版本（PolicyVersion 为空）时，返回该版本的全部事件。
    /// </summary>
    public static IReadOnlyList<LearningFeedbackEvent> Replay(
        LearningFeedbackSnapshot snapshot,
        string? policyVersion,
        LearningSnapshotSplit? split = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!string.IsNullOrWhiteSpace(policyVersion)
            && !string.IsNullOrWhiteSpace(snapshot.PolicyVersion)
            && !string.Equals(snapshot.PolicyVersion, policyVersion, StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<LearningFeedbackEvent>();
        }

        return snapshot.Events
            .Where(item =>
                string.IsNullOrWhiteSpace(policyVersion)
                || string.Equals(item.PolicyVersion, policyVersion, StringComparison.OrdinalIgnoreCase))
            .Where(item => split is null
                || (snapshot.SplitAssignment.TryGetValue(item.FeedbackId, out var assigned) && assigned == split))
            .ToArray();
    }

    private static LearningSnapshotSplit Bucket(
        string feedbackId,
        string featureVersion,
        int devPercent,
        int evalPercent)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{feedbackId}\u001f{featureVersion}"));
        var bucket = BitConverter.ToUInt32(bytes, 0) % 100;
        if (bucket < devPercent)
        {
            return LearningSnapshotSplit.Dev;
        }

        return bucket < devPercent + evalPercent
            ? LearningSnapshotSplit.Eval
            : LearningSnapshotSplit.Train;
    }

    private static string ComputeLineageSignature(
        string featureVersion,
        string policyVersion,
        IReadOnlyList<LearningFeedbackEvent> events,
        IReadOnlySet<string> deletedIds,
        IReadOnlySet<string> revokedIds,
        int devPercent,
        int evalPercent)
    {
        var eventFingerprints = events
            .OrderBy(item => item.FeedbackId, StringComparer.OrdinalIgnoreCase)
            .Select(item => ComputeEventFingerprint(item));
        var input = string.Join(
            "\u001f",
            featureVersion,
            policyVersion,
            devPercent.ToString(CultureInfo.InvariantCulture),
            evalPercent.ToString(CultureInfo.InvariantCulture),
            string.Join(",", eventFingerprints),
            string.Join(",", deletedIds.OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)),
            string.Join(",", revokedIds.OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash);
    }

    private static string ComputeEventFingerprint(LearningFeedbackEvent item)
    {
        var json = JsonSerializer.Serialize(item, JsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash);
    }
}
