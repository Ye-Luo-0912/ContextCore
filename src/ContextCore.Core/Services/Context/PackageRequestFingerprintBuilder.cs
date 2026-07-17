using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 构建请求指纹与包依赖 scope 集合的纯函数集合。
/// 指纹仅包含影响构建输出的字段，排除 OperationId/RequestId（per-call GUID）。
/// 相同指纹的请求产生相同 package（在依赖 scope 未变更的前提下）。
/// 使用长度前缀编码防止分隔符碰撞（输入值中包含 | 或 : 不会导致不同输入产生相同指纹）。
/// P0-5.5: 纳入时间桶（5 分钟窗口），确保 Working Memory 评分依赖的时间边界（24h/7d/30d）
/// 跨越后缓存自动失效。P0-5.6: <see cref="BuildHashed"/> 输出 SHA-256 固定长度哈希，避免明文驻留。
/// R13.0 #7: semantic metadata fingerprint——request.Metadata 中仅纳入语义字段
/// （影响 package 模板的字段），排除操作性字段（requestId/traceId 等 per-call 标识）。
/// 语义字段（mustHit/currentTask/tokenizerModel/anchor metadata）已显式提取，
/// 剩余 metadata 按 denylist 排除已知操作性 key，其余视为语义字段纳入指纹。
/// </summary>
internal static class PackageRequestFingerprintBuilder
{
    /// <summary>时间桶大小（秒）。5 分钟窗口平衡 staleness 与命中率。</summary>
    private const long TimeBucketSeconds = 300;

    /// <summary>
    /// R13.0 #7: 操作性 metadata key denylist——这些 key 是 per-call 标识/诊断字段，
    /// 不影响 package 模板内容（仅用于追踪/日志/响应元数据回显）。
    /// 排除后避免相同语义请求因不同 requestId/traceId 导致缓存 miss。
    /// 使用 Ordinal 比较器匹配大小写敏感的 key。
    /// </summary>
    private static readonly HashSet<string> OperationalMetadataKeys = new(StringComparer.Ordinal)
    {
        // per-call 标识
        "requestId", "traceId", "operationId", "callerId", "correlationId", "spanId",
        // 时间戳（per-call，非语义）
        "timestamp", "createdAt", "requestTime", "clientTimestamp", "receivedAt",
        // 客户端/网络诊断
        "clientIp", "userAgent", "sessionId", "clientId", "remoteAddress",
        // 内部追踪（由 Runtime 注入，非请求语义）
        "x-operation-id", "x-trace-id", "x-request-id"
    };

    /// <summary>
    /// 构建请求指纹：仅包含影响构建输出的字段，排除 OperationId/RequestId（per-call GUID）。
    /// P0-5.5: 末尾追加时间桶，确保时间依赖评分跨越边界后缓存自动失效。
    /// R13.0 #7: request.Metadata 按 denylist 排除操作性 key，仅纳入语义字段。
    /// </summary>
    internal static string Build(ContextPackageRequest request, ContextPackagePolicy policy)
    {
        var sb = new StringBuilder();
        AppendField(sb, request.WorkspaceId);
        AppendField(sb, request.CollectionId);
        AppendField(sb, request.QueryText);
        AppendSorted(sb, request.RequiredTags);
        AppendSorted(sb, request.RequiredTypes);
        AppendField(sb, request.TokenBudget.ToString());
        AppendField(sb, ((int)request.Mode).ToString());
        AppendField(sb, request.IncludeRecent.ToString());
        AppendField(sb, request.IsAuditMode?.ToString() ?? "null");
        AppendField(sb, PackagePolicyResolver.ResolveTokenizerModel(request));
        // mustHit IDs 影响候选排序与选取
        AppendSorted(sb, PackagePolicyResolver.ResolvePackageMustHitIds(request));
        // currentTask 元数据影响 current_task section 内容
        AppendField(sb, RequestTaskResolver.HasRequestCurrentTaskMetadata(request).ToString());
        if (RequestTaskResolver.HasRequestCurrentTaskMetadata(request))
        {
            AppendField(sb, RequestTaskResolver.ReadRequestMetadata(request, "currentTaskId", "taskId", "current_task.id"));
            AppendField(sb, RequestTaskResolver.ReadRequestMetadata(request, "currentTaskTitle", "taskTitle", "current_task.title"));
            AppendField(sb, RequestTaskResolver.ReadRequestMetadata(request, "currentTaskDescription", "taskDescription", "current_task.description"));
            AppendField(sb, RequestTaskResolver.ReadRequestMetadata(request, "currentTaskStatus", "taskStatus", "current_task.status"));
        }
        // policy 指纹
        AppendField(sb, policy.Id);
        AppendField(sb, ((int)policy.Mode).ToString());
        AppendField(sb, policy.TokenBudget.ToString());
        AppendField(sb, policy.IncludeGlobalContext.ToString());
        AppendField(sb, policy.IncludeHardConstraints.ToString());
        AppendField(sb, policy.IncludeSoftConstraints.ToString());
        AppendField(sb, policy.IncludeWorkingMemory.ToString());
        AppendField(sb, policy.IncludeStableMemory.ToString());
        AppendField(sb, policy.IncludeRecentRawContext.ToString());
        AppendField(sb, policy.MaxRecentItems.ToString());
        AppendField(sb, policy.EnableStrictRelevanceFilter.ToString());
        AppendField(sb, policy.IsAuditMode?.ToString() ?? "null");
        // SectionOrder 必须保持声明顺序（影响最终 section 排列），不能排序
        AppendOrdered(sb, policy.SectionOrder);
        AppendSortedKeyValuePairs(sb, policy.SectionPriorities);
        AppendSortedKeyValuePairs(sb, policy.SectionTokenBudgets);
        AppendSortedStringDictionary(sb, policy.Metadata);
        // R13.0 #7: request.Metadata 按 denylist 排除操作性 key（requestId/traceId 等）。
        // 语义字段（mustHit/currentTask/tokenizerModel）已显式提取，anchor metadata key
        // （mode/taskKind/intent/project 等）不在 denylist 中，仍纳入指纹。
        // 响应 metadata 在 ProjectResult 中从当前 request 重建，缓存命中时使用新 request 的 metadata，
        // 因此操作性 key 不需要进入指纹（不影响模板，仅影响 per-call 响应回显）。
        AppendSemanticMetadata(sb, request.Metadata);
        // P0-5.5: 时间桶 — Working Memory 评分依赖当前时间（24h/7d/30d 边界），
        // 将 UtcNow 按 5 分钟取整纳入指纹，确保跨时间边界后缓存自动失效。
        var timeBucket = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / TimeBucketSeconds;
        AppendField(sb, timeBucket.ToString());
        return sb.ToString();
    }

    /// <summary>
    /// 构建固定长度的哈希指纹（SHA-256，64 字符 hex）。
    /// P0-5.6: 用于缓存 key，避免明文查询/metadata 驻留与超长 dictionary key。
    /// 相同输入产生相同哈希；不同输入碰撞概率可忽略（2^-128）。
    /// </summary>
    internal static string BuildHashed(ContextPackageRequest request, ContextPackagePolicy policy)
    {
        var canonical = Build(request, policy);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// 构建包依赖的 scope 集合。任一 store 在相关 workspace+collection 上写入即失效缓存。
    /// 包含 WorkingMemoryService 以覆盖 SetCurrentTaskAsync 等操作导致的 current_task section 变更。
    /// GlobalContextStore 同时订阅 collection-level 和 workspace-level scope，
    /// 因为全局数据写入时 CollectionId 可能为空（workspace 级），decorator 会用 string.Empty 作为 CollectionId。
    /// </summary>
    internal static DependencyScopeSet BuildDependencyScopes(string workspaceId, string collectionId)
    {
        return new DependencyScopeSet(
            new CacheInvalidationKey("ContextStore", workspaceId, collectionId, null),
            new CacheInvalidationKey("MemoryStore", workspaceId, collectionId, null),
            new CacheInvalidationKey("ConstraintStore", workspaceId, collectionId, null),
            // collection 级全局数据
            new CacheInvalidationKey("GlobalContextStore", workspaceId, collectionId, null),
            // workspace 级全局数据（CollectionId=null 的全局条目写入时 decorator 用 string.Empty）
            new CacheInvalidationKey("GlobalContextStore", workspaceId, string.Empty, null),
            new CacheInvalidationKey("RelationStore", workspaceId, collectionId, null),
            new CacheInvalidationKey("WorkingMemoryService", workspaceId, collectionId, null));
    }

    /// <summary>长度前缀编码：len:value| 格式，防止值中包含分隔符导致碰撞。</summary>
    private static void AppendField(StringBuilder sb, string? value)
    {
        var v = value ?? string.Empty;
        sb.Append(v.Length).Append(':').Append(v).Append('|');
    }

    private static void AppendSorted(StringBuilder sb, IEnumerable<string>? values)
    {
        if (values is null)
        {
            sb.Append("-|");
            return;
        }
        // 避免在空集合上分配数组
        if (values is ICollection<string> { Count: 0 })
        {
            sb.Append("0:|");
            return;
        }
        var sorted = values.OrderBy(v => v, StringComparer.Ordinal).ToArray();
        sb.Append(sorted.Length).Append(':');
        foreach (var v in sorted)
        {
            sb.Append(v.Length).Append(':').Append(v).Append(',');
        }
        sb.Append('|');
    }

    /// <summary>保持声明顺序写入（用于 SectionOrder 等顺序敏感字段）。</summary>
    private static void AppendOrdered(StringBuilder sb, IEnumerable<string>? values)
    {
        if (values is null)
        {
            sb.Append("-|");
            return;
        }
        if (values is ICollection<string> { Count: 0 })
        {
            sb.Append("0:|");
            return;
        }
        var arr = values.ToArray();
        sb.Append(arr.Length).Append(':');
        foreach (var v in arr)
        {
            sb.Append(v.Length).Append(':').Append(v).Append(',');
        }
        sb.Append('|');
    }

    /// <summary>对 string 字典排序后写入指纹（key=value 格式）。</summary>
    private static void AppendSortedStringDictionary(StringBuilder sb, IReadOnlyDictionary<string, string>? dict)
    {
        if (dict is null || dict.Count == 0)
        {
            sb.Append("-|");
            return;
        }
        var keys = dict.Keys.ToArray();
        Array.Sort(keys, StringComparer.Ordinal);
        sb.Append(keys.Length).Append(':');
        foreach (var key in keys)
        {
            var entry = key + "=" + dict[key];
            sb.Append(entry.Length).Append(':').Append(entry).Append(',');
        }
        sb.Append('|');
    }

    /// <summary>
    /// R13.0 #7: 将 request.Metadata 中语义字段（非操作性 key）按 Ordinal 排序后写入指纹。
    /// 排除 <see cref="OperationalMetadataKeys"/> 中的 per-call 标识/诊断字段（requestId/traceId 等），
    /// 仅保留影响 package 模板的语义字段（如 mode/taskKind/intent/project/desiredOutputFormat/timeRange
    /// 及未在 denylist 中的自定义业务字段）。
    /// 写入格式与 <see cref="AppendSortedStringDictionary"/> 一致（len:count + len:entry,），
    /// count 为过滤后语义字段数，确保相同语义请求即使携带不同 requestId 也产生相同指纹。
    /// </summary>
    private static void AppendSemanticMetadata(StringBuilder sb, IReadOnlyDictionary<string, string>? dict)
    {
        // 先过滤操作性 key，仅保留语义字段。语义字段数为 0 时统一写 "-|"，
        // 使 "无 metadata"、"空字典"、"仅含操作性 key" 三种情况产生相同指纹——
        // 避免 per-call 操作性 key 的存在性泄漏到指纹（仅 per-call 标识不同不应影响缓存命中）。
        if (dict is null || dict.Count == 0)
        {
            sb.Append("-|");
            return;
        }
        // 过滤操作性 key，仅保留语义字段
        var semanticKeys = dict.Keys.Where(k => !OperationalMetadataKeys.Contains(k)).ToArray();
        if (semanticKeys.Length == 0)
        {
            // 仅含操作性 key——与无 metadata 等价，避免操作性 key 存在性泄漏到指纹
            sb.Append("-|");
            return;
        }
        Array.Sort(semanticKeys, StringComparer.Ordinal);
        sb.Append(semanticKeys.Length).Append(':');
        foreach (var key in semanticKeys)
        {
            var entry = key + "=" + dict[key];
            sb.Append(entry.Length).Append(':').Append(entry).Append(',');
        }
        sb.Append('|');
    }

    /// <summary>
    /// 对键值对集合排序后写入指纹，避免 LINQ Select 分配中间字符串数组和 ToArray。
    /// 直接在 StringBuilder 上拼接 "key:value" 格式。
    /// </summary>
    private static void AppendSortedKeyValuePairs(StringBuilder sb, IReadOnlyDictionary<string, int>? pairs)
    {
        if (pairs is null || pairs.Count == 0)
        {
            sb.Append("-|");
            return;
        }
        // 复用 pairs.Keys 排序，避免分配 KeyValuePair 数组
        var keys = pairs.Keys.ToArray();
        Array.Sort(keys, StringComparer.Ordinal);
        sb.Append(keys.Length).Append(':');
        foreach (var key in keys)
        {
            var entry = key + ":" + pairs[key].ToString();
            sb.Append(entry.Length).Append(':').Append(entry).Append(',');
        }
        sb.Append('|');
    }
}
