using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 轻量锚点提取器，从当前输入、请求元数据和短期筛选结果中提取后续召回线索。
/// 该实现不做语义解析，目标是先提供稳定、可解释、低开销的 anchors。
/// </summary>
public sealed class ContextAnchorExtractor
{
    private const int MaxAnchors = 32;
    private const int MaxRecentItemsForAnchor = 8;

    public IReadOnlyList<ContextAnchor> Extract(
        ContextPackageRequest request,
        IReadOnlyList<RecentContextItem> recentItems)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(recentItems);

        var anchors = new List<ContextAnchor>();
        AddMetadataAnchors(anchors, request);
        AddRequestAnchors(anchors, request);
        AddRecentAnchors(anchors, recentItems);

        return [.. anchors
            .Where(anchor => !string.IsNullOrWhiteSpace(anchor.Name))
            .GroupBy(anchor => (Normalize(anchor.Name), anchor.Type))
            .Select(group => group.OrderByDescending(anchor => anchor.Weight).First())
            .OrderByDescending(anchor => anchor.Weight)
            .ThenBy(anchor => anchor.Type)
            .ThenBy(anchor => anchor.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxAnchors)];
    }

    private static void AddMetadataAnchors(ICollection<ContextAnchor> anchors, ContextPackageRequest request)
    {
        AddMetadataAnchor(anchors, request, "mode", AnchorType.Mode, 1.0);
        AddMetadataAnchor(anchors, request, "taskKind", AnchorType.Task, 0.95);
        AddMetadataAnchor(anchors, request, "intent", AnchorType.Intent, 0.95);
        AddMetadataAnchor(anchors, request, "project", AnchorType.Project, 0.9);
        AddMetadataAnchor(anchors, request, "desiredOutputFormat", AnchorType.Intent, 0.75);
        AddMetadataAnchor(anchors, request, "timeRange", AnchorType.TimeRange, 0.75);
    }

    private static void AddMetadataAnchor(
        ICollection<ContextAnchor> anchors,
        ContextPackageRequest request,
        string key,
        AnchorType type,
        double weight)
    {
        if (!request.Metadata.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        anchors.Add(new ContextAnchor(value.Trim(), type, weight, $"metadata:{key}", Array.Empty<string>()));
    }

    private static void AddRequestAnchors(ICollection<ContextAnchor> anchors, ContextPackageRequest request)
    {
        anchors.Add(new ContextAnchor(request.WorkspaceId, AnchorType.Project, 0.7, "request.workspace", Array.Empty<string>()));
        anchors.Add(new ContextAnchor(request.CollectionId, AnchorType.Project, 0.7, "request.collection", Array.Empty<string>()));

        foreach (var tag in request.RequiredTags.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            anchors.Add(new ContextAnchor(tag, AnchorType.Topic, 0.65, "request.requiredTags", Array.Empty<string>()));
        }

        foreach (var type in request.RequiredTypes.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            anchors.Add(new ContextAnchor(type, AnchorType.Entity, 0.6, "request.requiredTypes", Array.Empty<string>()));
        }

        foreach (var term in ExtractTerms(request.QueryText).Take(20))
        {
            anchors.Add(new ContextAnchor(term, GuessType(term), 0.55, "request.query", Array.Empty<string>()));
        }
    }

    private static void AddRecentAnchors(ICollection<ContextAnchor> anchors, IReadOnlyList<RecentContextItem> recentItems)
    {
        foreach (var item in recentItems
            .Where(item => item.ExcludeReason is null)
            .OrderByDescending(item => item.Relevance)
            .Take(MaxRecentItemsForAnchor))
        {
            foreach (var term in ExtractTerms(item.Content).Take(3))
            {
                anchors.Add(new ContextAnchor(
                    term,
                    GuessType(term),
                    Math.Clamp(item.Relevance * 0.5 + item.RecencyWeight * 0.2, 0.1, 0.7),
                    $"recent:{item.SourceItemId}",
                    []));
            }
        }
    }

    private static AnchorType GuessType(string term)
    {
        if (term.Contains("约束", StringComparison.OrdinalIgnoreCase)
            || term.Contains("constraint", StringComparison.OrdinalIgnoreCase))
        {
            return AnchorType.Constraint;
        }

        if (term.Contains("任务", StringComparison.OrdinalIgnoreCase)
            || term.Contains("task", StringComparison.OrdinalIgnoreCase))
        {
            return AnchorType.Task;
        }

        if (term.Contains("模式", StringComparison.OrdinalIgnoreCase)
            || term.EndsWith("Mode", StringComparison.OrdinalIgnoreCase))
        {
            return AnchorType.Mode;
        }

        return AnchorType.Topic;
    }

    private static readonly string[] ChineseKeywords =
    [
        "语言偏好", "语言", "偏好", "输出", "中文", "英文", "开发", "任务", "计划",
        "运行", "配置", "持久化", "后端", "存储", "生产", "生产后端", "林风", "苍穹",
        "大陆", "苍穹大陆", "拍卖行", "九转金丹", "金丹", "药引", "龙魂草", "人设", "剧情",
        "大纲", "工作流", "错误", "恢复", "接口", "单元测试", "测试", "设定", "密钥", "仓库",
        "明文", "安全", "本地", "性能", "性能路径", "重复", "重复 IO", "全量反序列化",
        "反序列化", "风险", "风险点", "快照测试", "覆盖率", "私有配置", "用户私有配置目录",
        "私有", "目录", "王室血脉", "血脉", "规则", "冲突"
    ];

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "的", "了", "在", "是", "我", "你", "他", "她", "它", "们", "这", "那", "都", "和", "与", "或", "而", "何", "如",
        "如何", "什么", "怎么", "哪个", "哪里", "为什么", "谁", "几", "多少", "是吗", "以", "去", "来", "个", "只", "条",
        "吗", "吧", "呢", "呀", "这", "那", "的", "地", "得"
    };

    private static bool ContainsChinese(string text)
    {
        foreach (var c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF)
            {
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<string> SegmentChinese(string text)
    {
        // 1. 优先提取预定义的关键主题词
        foreach (var kw in ChineseKeywords)
        {
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                yield return kw;
            }
        }

        // 2. 提取中文片段
        var sb = new StringBuilder();
        var chSegments = new List<string>();
        foreach (var c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF)
            {
                sb.Append(c);
            }
            else
            {
                if (sb.Length > 0)
                {
                    chSegments.Add(sb.ToString());
                    sb.Clear();
                }
            }
        }
        if (sb.Length > 0)
        {
            chSegments.Add(sb.ToString());
        }

        // 3. 切分 Bigram 词并过滤
        foreach (var seg in chSegments)
        {
            if (seg.Length < 2)
            {
                continue;
            }

            if (seg.Length <= 4)
            {
                if (!StopWords.Contains(seg))
                {
                    yield return seg;
                }
            }

            for (int i = 0; i < seg.Length - 1; i++)
            {
                var bigram = seg.Substring(i, 2);
                if (StopWords.Contains(bigram))
                {
                    continue;
                }
                var containsStopChar = false;
                foreach (var c in bigram)
                {
                    if (StopWords.Contains(c.ToString()))
                    {
                        containsStopChar = true;
                        break;
                    }
                }
                if (!containsStopChar)
                {
                    yield return bigram;
                }
            }
        }
    }

    private static IEnumerable<string> ExtractTerms(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (var raw in text.Split(
            [' ', '\t', '\r', '\n', ',', '，', '.', '。', ':', '：', ';', '；', '/', '\\', '(', ')', '（', '）', '[', ']'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var term = raw.Trim();
            if (term.Length < 2)
            {
                continue;
            }

            if (ContainsChinese(term))
            {
                foreach (var chTerm in SegmentChinese(term))
                {
                    yield return chTerm;
                }
            }
            else if (term.Length <= 32)
            {
                yield return term;
            }
        }
    }

    private static string Normalize(string value)
    {
        return value.Trim().ToLowerInvariant();
    }
}
