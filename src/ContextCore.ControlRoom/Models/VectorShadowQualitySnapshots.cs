using System.Text.Json;

namespace ContextCore.ControlRoom.Models;

/// <summary>
/// JsonDocument 容错读取辅助方法，供 ControlRoomService.Storage 的 TryLoad*Summary 方法使用。
/// 字段缺失或类型不匹配时返回默认值，不抛异常。
/// </summary>
internal static class VectorShadowQualitySnapshotReader
{
    public static bool GetBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True && v.GetBoolean();

    public static string GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    public static int GetInt32(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    public static double GetDouble(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    public static IReadOnlyList<string> GetStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s))
                {
                    list.Add(s);
                }
            }
        }

        return list;
    }

    public static string GetNestedString(JsonElement root, string parent, string name)
        => root.TryGetProperty(parent, out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    public static int GetNestedInt32(JsonElement root, string parent, string name)
        => root.TryGetProperty(parent, out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    public static int GetArrayLength(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;
}
