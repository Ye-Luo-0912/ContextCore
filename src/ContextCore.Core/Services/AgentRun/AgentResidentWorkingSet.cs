using System.Text.Json;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

// 跨轮 Resident：只带上上一轮选中的条目及其正文。
// 未选中的不进入下一轮请求，靠本轮搜索再找回。分配器仍按预算裁剪，所以这不是 append-only。
// 序列化进 AgentRun。上下文构建成功后就会随 Run 快照落库，模型调用中途崩溃也能恢复种子。

internal static class AgentResidentWorkingSet
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 从上一轮执行结果抽出 Resident 种子。没有选中项时返回 null（首轮 / 空召回）。
    /// </summary>
    public static CandidateWorkingSet? FromLastDecision(ContextDecisionExecutionResult? last)
    {
        if (last is null)
        {
            return null;
        }

        var selected = last.Decision.SelectedEnvelopes;
        if (selected.Count == 0)
        {
            return null;
        }

        var materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(selected.Count);
        foreach (var envelope in selected)
        {
            if (last.WorkingSet.Materials.TryGetValue(envelope.CanonicalKey, out var material))
            {
                materials[envelope.CanonicalKey] = material;
            }
        }

        return new CandidateWorkingSet
        {
            Envelopes = selected,
            Materials = materials
        };
    }

    /// <summary>
    /// 从种子里拿掉排除 ID。匹配 CandidateId、CanonicalKey.EntityId，以及 ExpertKind:id 后缀。
    /// 全部拿掉时返回 null。
    /// </summary>
    public static CandidateWorkingSet? WithoutIds(
        CandidateWorkingSet? seed,
        IReadOnlyList<string>? excludedIds)
    {
        if (seed is null || seed.Envelopes.Count == 0
            || excludedIds is null || excludedIds.Count == 0)
        {
            return seed;
        }

        var excluded = new HashSet<string>(excludedIds, StringComparer.OrdinalIgnoreCase);
        List<ContextCandidateEnvelope>? kept = null;
        foreach (var envelope in seed.Envelopes)
        {
            if (IsExcluded(envelope, excluded))
            {
                continue;
            }

            kept ??= new List<ContextCandidateEnvelope>(seed.Envelopes.Count);
            kept.Add(envelope);
        }

        if (kept is null || kept.Count == 0)
        {
            return null;
        }

        if (kept.Count == seed.Envelopes.Count)
        {
            return seed;
        }

        var materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(kept.Count);
        foreach (var envelope in kept)
        {
            if (seed.Materials.TryGetValue(envelope.CanonicalKey, out var material))
            {
                materials[envelope.CanonicalKey] = material;
            }
        }

        return new CandidateWorkingSet
        {
            Envelopes = kept,
            Materials = materials
        };
    }

    private static bool IsExcluded(ContextCandidateEnvelope envelope, HashSet<string> excluded)
    {
        if (excluded.Contains(envelope.CandidateId)
            || excluded.Contains(envelope.CanonicalKey.EntityId))
        {
            return true;
        }

        var candidateId = envelope.CandidateId;
        var separator = candidateId.LastIndexOf(':');
        return separator >= 0 && separator < candidateId.Length - 1
            && excluded.Contains(candidateId[(separator + 1)..]);
    }

    /// <summary>本轮决策种子：内存中的上一轮结果优先，否则用 Run 上持久化的 Resident。</summary>
    public static CandidateWorkingSet? ResolveSeed(
        ContextDecisionExecutionResult? lastDecision,
        string? persistedJson)
        => FromLastDecision(lastDecision) ?? TryParse(persistedJson);

    public static string? Serialize(CandidateWorkingSet? seed)
    {
        if (seed is null || seed.Envelopes.Count == 0)
        {
            return null;
        }

        var dto = new ResidentWorkingSetDto
        {
            Envelopes = seed.Envelopes,
            Materials = seed.Materials.Values.ToArray()
        };
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    public static CandidateWorkingSet? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<ResidentWorkingSetDto>(json, JsonOptions);
            if (dto?.Envelopes is null || dto.Envelopes.Count == 0)
            {
                return null;
            }

            var materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(dto.Envelopes.Count);
            if (dto.Materials is not null)
            {
                foreach (var material in dto.Materials)
                {
                    materials[material.Key] = material;
                }
            }

            return new CandidateWorkingSet
            {
                Envelopes = dto.Envelopes,
                Materials = materials
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class ResidentWorkingSetDto
    {
        public IReadOnlyList<ContextCandidateEnvelope> Envelopes { get; set; }
            = Array.Empty<ContextCandidateEnvelope>();

        public IReadOnlyList<CandidateMaterial>? Materials { get; set; }
    }
}
