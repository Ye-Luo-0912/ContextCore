using System.Text.Json;
using System.Text.Json.Serialization;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// 阶段 E：Replay fixture JSON 序列化辅助
//
// 目标：
//   1. 让 ReplayFixture（含 WorkingSet + V2Result）可往返 JSON 序列化。
//   2. 处理 CanonicalCandidateKey（readonly record struct）：
//      - 作为 CandidateMaterial.Key 属性值时，序列化为字符串。
//      - 作为 Dictionary<CanonicalCandidateKey, CandidateMaterial> 键时，
//        通过 WriteAsPropertyName/ReadAsPropertyName 写为 JSON 属性名。
//   3. 统一 FileSystemExperimentRecorder 与 PostgresExperimentRecorder 的序列化约定，
//      保证两端落盘数据可互读（FileSystem 存 raw fixture JSON，PostgreSQL 存 jsonb）。
//
// 设计原则：
//   1. CanonicalCandidateKey 五个字段均为字符串，使用 0x1F（ASCII Unit Separator）作分隔符，
//      正常业务 ID 不会出现该字符。读侧按分隔符拆分，字段为空时直接返回 default struct
//      （由调用方 IsValid 判断；不抛异常以兼容历史 fixture）。
//   2. 枚举使用 JsonStringEnumConverter，与 PostgresJsonSerializer 保持一致。
//   3. DefaultIgnoreCondition = WhenWritingNull，与 PostgresJsonSerializer 保持一致。
// ===========================================================================

/// <summary>
/// CanonicalCandidateKey 的 JSON 转换器。
/// 序列化为字符串形式 `{WorkspaceId}\x1F{CollectionId}\x1F{EntityKind}\x1F{EntityId}\x1F{EntityVersion}`。
/// 支持作为属性值与 Dictionary 键两种使用方式。
/// </summary>
public sealed class CanonicalCandidateKeyJsonConverter : JsonConverter<CanonicalCandidateKey>
{
    /// <summary>字段分隔符（ASCII Unit Separator 0x1F，正常业务 ID 不会出现）。</summary>
    public const char Separator = '\x1F';

    /// <inheritdoc />
    public override CanonicalCandidateKey Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"Expected string token for CanonicalCandidateKey, got {reader.TokenType}.");
        }

        var raw = reader.GetString() ?? string.Empty;
        return Parse(raw);
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        CanonicalCandidateKey value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(Format(value));
    }

    /// <inheritdoc />
    public override CanonicalCandidateKey ReadAsPropertyName(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var raw = reader.GetString() ?? string.Empty;
        return Parse(raw);
    }

    /// <inheritdoc />
    public override void WriteAsPropertyName(
        Utf8JsonWriter writer,
        CanonicalCandidateKey value,
        JsonSerializerOptions options)
    {
        writer.WritePropertyName(Format(value));
    }

    /// <summary>将 key 格式化为字符串。</summary>
    public static string Format(CanonicalCandidateKey value)
        => string.Join(Separator,
            value.WorkspaceId,
            value.CollectionId,
            value.EntityKind,
            value.EntityId,
            value.EntityVersion);

    /// <summary>从字符串解析 key。字段缺失时返回 default（IsValid=false）。</summary>
    public static CanonicalCandidateKey Parse(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return default;
        }

        var parts = raw.Split(Separator);
        if (parts.Length != 5)
        {
            throw new JsonException(
                $"Invalid CanonicalCandidateKey format: expected 5 parts separated by 0x1F, got {parts.Length}.");
        }

        return new CanonicalCandidateKey(
            parts[0],
            parts[1],
            parts[2],
            parts[3],
            parts[4]);
    }
}

/// <summary>
/// ReplayFixture 专用 JSON 序列化器。
/// 同时被 FileSystemExperimentRecorder 与 PostgresExperimentRecorder 使用，
/// 保证两端落盘数据格式一致，可互读。
/// </summary>
public static class ReplayFixtureJsonSerializer
{
    private static readonly JsonSerializerOptions _options = CreateOptions();

    /// <summary>创建配置好的 JsonSerializerOptions（含 CanonicalCandidateKey + 枚举字符串转换）。</summary>
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters =
            {
                new CanonicalCandidateKeyJsonConverter(),
                new JsonStringEnumConverter()
            }
        };
        return options;
    }

    /// <summary>序列化 ReplayFixture 为 JSON 字符串。</summary>
    public static string Serialize(ReplayFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        return JsonSerializer.Serialize(fixture, _options);
    }

    /// <summary>反序列化 JSON 字符串为 ReplayFixture。</summary>
    public static ReplayFixture Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<ReplayFixture>(json, _options)
            ?? throw new InvalidOperationException("Failed to deserialize ReplayFixture.");
    }

    /// <summary>反序列化（返回 null 而非抛异常，用于历史 fixture 兼容场景）。</summary>
    public static ReplayFixture? DeserializeOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ReplayFixture>(json, _options);
        }
        catch (JsonException)
        {
            // 历史 fixture 格式不兼容时返回 null，由调用方决定跳过或告警
            return null;
        }
    }
}
