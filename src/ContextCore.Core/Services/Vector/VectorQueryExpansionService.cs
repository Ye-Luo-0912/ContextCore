using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>向量查询扩展服务；只组合运行时信号，用于离线 shadow preview。</summary>
public sealed class VectorQueryExpansionService
{
    private readonly IReadOnlyDictionary<string, VectorQueryExpansionProfile> _profiles;

    public VectorQueryExpansionService(IReadOnlyList<VectorQueryExpansionProfile>? profiles = null)
    {
        _profiles = (profiles is null || profiles.Count == 0
                ? CreateDefaultProfiles()
                : profiles)
            .Where(profile => !string.IsNullOrWhiteSpace(profile.ProfileId))
            .GroupBy(profile => profile.ProfileId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToDictionary(profile => profile.ProfileId, profile => profile, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<VectorQueryExpansionProfile> GetProfiles()
    {
        return _profiles.Values
            .OrderBy(profile => profile.ProfileId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public VectorQueryExpansionProfile Resolve(string? profileId)
    {
        if (!string.IsNullOrWhiteSpace(profileId)
            && _profiles.TryGetValue(profileId, out var profile))
        {
            return profile;
        }

        return _profiles[VectorQueryExpansionProfileIds.RawQueryV1];
    }

    public VectorQueryExpansionResult Expand(
        VectorQueryExpansionRequest request,
        string? profileId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.QueryText);

        var profile = Resolve(profileId);
        var queryAnchors = request.QueryAnchors.Count > 0
            ? CleanSignals(request.QueryAnchors, profile.MaxSignalCount)
            : VectorMissSetRepresentationAuditRunner.ExtractAnchors(request.QueryText, profile.MaxSignalCount);
        var signals = new List<string>();
        var warnings = new List<string>();

        if (profile.IncludeMode)
        {
            AddSignal(signals, request.Mode);
        }

        if (profile.IncludeIntent)
        {
            AddSignal(signals, request.RouterIntent);
            AddSignal(signals, request.Intent);
        }

        if (profile.IncludeTaskKind)
        {
            AddSignal(signals, request.TaskKind);
        }

        if (profile.IncludeQueryAnchors)
        {
            AddSignals(signals, queryAnchors, profile.MaxSignalCount);
        }

        if (profile.IncludeWorkingMemoryAnchors)
        {
            AddSignals(signals, request.WorkingMemoryAnchors, profile.MaxSignalCount);
        }

        if (profile.IncludePlanningContext && request.PlanningSnapshot is not null)
        {
            AddPlanningSignals(signals, request.PlanningSnapshot, profile.MaxSignalCount);
        }

        if (profile.IncludeConstraintHints)
        {
            AddSignals(signals, request.ConstraintHints, profile.MaxSignalCount);
        }

        if (profile.IncludeRequestMetadata)
        {
            AddMetadataSignals(signals, request.RequestMetadata, profile.MaxSignalCount);
        }

        var normalizedSignals = CleanSignals(signals, profile.MaxSignalCount);
        if (normalizedSignals.Count == 0 && !profile.IncludeRawQuery)
        {
            warnings.Add("query expansion 没有可用运行时信号，已回退为 raw query。");
        }

        var expanded = profile.IncludeRawQuery || normalizedSignals.Count == 0
            ? JoinText(normalizedSignals.Prepend(request.QueryText))
            : JoinText(normalizedSignals);

        return new VectorQueryExpansionResult
        {
            ProfileId = profile.ProfileId,
            OriginalQuery = request.QueryText,
            ExpandedQuery = expanded,
            QueryAnchors = queryAnchors,
            UsedSignals = normalizedSignals,
            Warnings = warnings
        };
    }

    private static IReadOnlyList<VectorQueryExpansionProfile> CreateDefaultProfiles()
    {
        return
        [
            new VectorQueryExpansionProfile
            {
                ProfileId = VectorQueryExpansionProfileIds.RawQueryV1,
                IncludeRawQuery = true
            },
            new VectorQueryExpansionProfile
            {
                ProfileId = VectorQueryExpansionProfileIds.ModeIntentQueryV1,
                IncludeRawQuery = true,
                IncludeMode = true,
                IncludeIntent = true,
                IncludeTaskKind = true
            },
            new VectorQueryExpansionProfile
            {
                ProfileId = VectorQueryExpansionProfileIds.AnchorQueryV1,
                IncludeRawQuery = true,
                IncludeQueryAnchors = true
            },
            new VectorQueryExpansionProfile
            {
                ProfileId = VectorQueryExpansionProfileIds.IntentAnchorQueryV1,
                IncludeRawQuery = true,
                IncludeIntent = true,
                IncludeQueryAnchors = true,
                IncludeTaskKind = true
            },
            new VectorQueryExpansionProfile
            {
                ProfileId = VectorQueryExpansionProfileIds.PlanningContextQueryV1,
                IncludeRawQuery = true,
                IncludeMode = true,
                IncludeIntent = true,
                IncludePlanningContext = true,
                IncludeWorkingMemoryAnchors = true,
                IncludeTaskKind = true,
                IncludeRequestMetadata = true
            },
            new VectorQueryExpansionProfile
            {
                ProfileId = VectorQueryExpansionProfileIds.ConstraintAwareQueryV1,
                IncludeRawQuery = true,
                IncludeMode = true,
                IncludeIntent = true,
                IncludeQueryAnchors = true,
                IncludeConstraintHints = true,
                IncludeTaskKind = true
            }
        ];
    }

    private static void AddPlanningSignals(
        ICollection<string> signals,
        ContextPlanningSnapshot snapshot,
        int maxSignalCount)
    {
        AddSignals(signals, snapshot.ActiveTasks.Select(item => JoinText(item.Title, item.Summary)), maxSignalCount);
        AddSignals(signals, snapshot.RecentDecisions.Select(item => JoinText(item.Title, item.Summary)), maxSignalCount);
        AddSignals(signals, snapshot.OpenQuestions.Select(item => JoinText(item.Title, item.Summary)), maxSignalCount);
        AddSignals(signals, snapshot.KnownIssues.Select(item => JoinText(item.Title, item.Summary)), maxSignalCount);
        AddSignals(signals, snapshot.StableConstraints.Select(item => JoinText(item.Level.ToString(), item.Content)), maxSignalCount);
        AddSignals(signals, snapshot.StablePreferences.Select(item => JoinText(item.Type, item.Content)), maxSignalCount);
        AddSignals(signals, snapshot.DecisionRecords.Select(item => JoinText(item.Type, item.Content)), maxSignalCount);
    }

    private static void AddMetadataSignals(
        ICollection<string> signals,
        IReadOnlyDictionary<string, string> metadata,
        int maxSignalCount)
    {
        foreach (var item in metadata
                     .Where(item => IsAllowedMetadataKey(item.Key))
                     .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                     .Take(Math.Max(1, maxSignalCount)))
        {
            AddSignal(signals, item.Value);
        }
    }

    private static bool IsAllowedMetadataKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var normalized = key.Trim();
        if (IsForbiddenMetadataKey(normalized))
        {
            return false;
        }

        return normalized.Contains("mode", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("intent", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("task", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("scope", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("constraint", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("anchor", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsForbiddenMetadataKey(string key)
    {
        return key.Contains("sample", StringComparison.OrdinalIgnoreCase)
               || key.Contains("item", StringComparison.OrdinalIgnoreCase)
               || key.Contains("fixture", StringComparison.OrdinalIgnoreCase)
               || key.Contains("file", StringComparison.OrdinalIgnoreCase)
               || key.Contains("mustHit", StringComparison.OrdinalIgnoreCase)
               || key.Contains("mustNot", StringComparison.OrdinalIgnoreCase)
               || key.Contains("label", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddSignals(
        ICollection<string> signals,
        IEnumerable<string> values,
        int maxSignalCount)
    {
        foreach (var value in values.Take(Math.Max(1, maxSignalCount)))
        {
            AddSignal(signals, value);
        }
    }

    private static void AddSignal(ICollection<string> signals, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            signals.Add(value.Trim());
        }
    }

    private static IReadOnlyList<string> CleanSignals(IEnumerable<string> values, int maxSignalCount)
    {
        return values
            .SelectMany(SplitSignal)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Where(value => value.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxSignalCount))
            .ToArray();
    }

    private static IEnumerable<string> SplitSignal(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var builder = new StringBuilder();
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.OtherLetter)
            {
                builder.Append(ch);
                continue;
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static string JoinText(IEnumerable<string?> parts)
    {
        return string.Join(' ', parts
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim()));
    }

    private static string JoinText(params string?[] parts)
    {
        return JoinText((IEnumerable<string?>)parts);
    }
}
