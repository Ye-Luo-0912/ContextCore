using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Core.Services;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Rendering;

/// <summary>渲染 Service 模式下的 jobs / model / admin-runtime 页面。</summary>
public static partial class ServiceOperationalRenderer
{
    private static void AppendHeader(StringBuilder builder, string title)
    {
        builder.AppendLine(title);
        builder.AppendLine(new string('=', title.Length));
    }

    private static void AppendStringSection(StringBuilder builder, string title, IReadOnlyList<string> values)
    {
        if (values.Count == 0) return;
        builder.AppendLine(title);
        foreach (var value in values)
        {
            builder.AppendLine($"- {value}");
        }
    }

    private static void AppendStatusLine(StringBuilder builder, string value)
    {
        AppendLabeledLine(builder, "status", value);
    }

    private static void AppendMetricLine(StringBuilder builder, string label, string value)
    {
        AppendLabeledLine(builder, label, value);
    }

    private static void AppendBooleanInvariantLine(StringBuilder builder, string label, bool value)
    {
        AppendLabeledLine(builder, label, value.ToString());
    }

    private static void AppendRecommendationLine(StringBuilder builder, string? value)
    {
        AppendLabeledLine(builder, "recommendation", BlankDash(value));
    }

    private static void AppendBlockedLine(StringBuilder builder, IReadOnlyList<string> blockedReasons, string label = "blocked")
    {
        AppendLabeledLine(
            builder,
            label,
            blockedReasons.Count == 0 ? "-" : string.Join(", ", blockedReasons));
    }

    private static void AppendLabeledLine(StringBuilder builder, string label, string value)
    {
        builder.AppendLine($"- {label,-14}: {value}");
    }

    private static string BlankDash(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static string FormatList(IReadOnlyList<string> values)
    {
        return values.Count == 0 ? "-" : string.Join(", ", values);
    }

    private static string FormatMap(IReadOnlyDictionary<string, string> values, int maxItems = 6)
    {
        if (values.Count == 0)
        {
            return "-";
        }

        return string.Join("; ", values
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxItems))
            .Select(static pair => $"{pair.Key}={pair.Value}"));
    }

    public static string RenderError(ContextCoreApiException exception)
    {
        return ServiceOperationRenderer.RenderError(exception);
    }

    private static void AppendStringList(StringBuilder builder, string title, IReadOnlyList<string> values)
    {
        builder.AppendLine();
        builder.AppendLine(title);
        builder.AppendLine(new string('-', title.Length));
        if (values.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var value in values.Take(20))
        {
            builder.AppendLine($"- {value}");
        }
    }

    private static void AppendWorkingItems(StringBuilder builder, string title, IReadOnlyList<ShortTermWorkingItem> items)
    {
        builder.AppendLine(title);
        builder.AppendLine(new string('-', title.Length));
        if (items.Count == 0)
        {
            builder.AppendLine("- (empty)");
            builder.AppendLine();
            return;
        }

        foreach (var item in items.Take(10))
        {
            builder.AppendLine($"- {item.ItemId} [{item.Kind}/{item.Status}/{item.Lifecycle}] importance={item.Importance:0.00}");
            builder.AppendLine($"  title   : {item.Title}");
            builder.AppendLine($"  summary : {Compact(item.Summary, 160)}");
            builder.AppendLine($"  refs    : {string.Join(", ", item.SourceRefs.Concat(item.Refs).Distinct(StringComparer.OrdinalIgnoreCase).Take(8))}");
        }

        builder.AppendLine();
    }

    private static void AppendConstraints(StringBuilder builder, string title, IReadOnlyList<ContextConstraint> items)
    {
        builder.AppendLine(title);
        builder.AppendLine(new string('-', title.Length));
        if (items.Count == 0)
        {
            builder.AppendLine("- (empty)");
            builder.AppendLine();
            return;
        }

        foreach (var item in items.Take(10))
        {
            builder.AppendLine($"- {item.Id} [{item.Level}/{item.Status}/{item.Scope}] confidence={item.Confidence:0.00}");
            builder.AppendLine($"  content : {Compact(item.Content, 160)}");
            builder.AppendLine($"  refs    : {string.Join(", ", item.SourceRefs.Take(8))}");
        }

        builder.AppendLine();
    }

    private static void AppendMemoryItems(StringBuilder builder, string title, IReadOnlyList<ContextMemoryItem> items)
    {
        builder.AppendLine(title);
        builder.AppendLine(new string('-', title.Length));
        if (items.Count == 0)
        {
            builder.AppendLine("- (empty)");
            builder.AppendLine();
            return;
        }

        foreach (var item in items.Take(10))
        {
            builder.AppendLine($"- {item.Id} [{item.Type}/{item.Status}] importance={item.Importance:0.00}");
            builder.AppendLine($"  content : {Compact(item.Content, 160)}");
            builder.AppendLine($"  refs    : {string.Join(", ", item.SourceRefs.Take(8))}");
        }

        builder.AppendLine();
    }

    private static string Compact(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
    }

    private static JobPayloadInfo TryParsePayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new JobPayloadInfo();
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? operationId = null;

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("OperationId") || property.NameEquals("operationId"))
                    {
                        operationId = property.Value.GetString();
                    }

                    if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    {
                        metadata[property.Name] = property.Value.ToString();
                    }
                }
            }

            return new JobPayloadInfo
            {
                OperationId = operationId,
                Metadata = metadata
            };
        }
        catch
        {
            return new JobPayloadInfo();
        }
    }

    private sealed class JobPayloadInfo
    {
        public string? OperationId { get; init; }

        public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    }

    private static void AppendWorkingSection(
        StringBuilder builder,
        string title,
        IReadOnlyList<ShortTermWorkingItem> items)
    {
        builder.AppendLine(title);
        if (items.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var item in items)
        {
            builder.AppendLine($"- {item.ItemId} [{item.Kind}/{item.Status}] {item.Summary}");
        }
    }

    private static void AppendMaintenanceSection(
        StringBuilder builder,
        ShortTermMaintenanceStatusResponse? maintenance)
    {
        builder.AppendLine("Maintenance");
        if (maintenance is null)
        {
            builder.AppendLine("- (unavailable)");
            return;
        }

        builder.AppendLine($"- Enabled       : {maintenance.Enabled}");
        builder.AppendLine($"- Running       : {maintenance.IsRunning}");
        builder.AppendLine($"- RunOnStartup  : {maintenance.RunOnStartup}");
        builder.AppendLine($"- IntervalSec   : {maintenance.IntervalSeconds}");
        builder.AppendLine($"- LastError     : {maintenance.LastError ?? "none"}");
        builder.AppendLine($"- LastRun       : {maintenance.LastRun?.RunId ?? "none"}");
    }

    private static string FormatDictionaryCompact(IReadOnlyDictionary<string, int> values)
    {
        return values.Count == 0
            ? "-"
            : string.Join(", ", values
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .Select(static pair => $"{pair.Key}={pair.Value}"));
    }

    private static string TrimHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        return value.Length <= 16 ? value : value[..16];
    }

    private static string FormatEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static string ReadMetadata(ContextConstraint item, string key)
    {
        return item.Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : "-";
    }
}
