using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContextCore.Storage.Postgres;

/// <summary>PostgreSQL jsonb 存储使用的统一 JSON 序列化器。</summary>
public sealed class PostgresJsonSerializer
{
    private readonly JsonSerializerOptions _options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, _options);
    }

    public T Deserialize<T>(string json)
    {
        var value = JsonSerializer.Deserialize<T>(json, _options);
        return value ?? throw new InvalidOperationException($"无法反序列化 PostgreSQL jsonb：{typeof(T).Name}");
    }

    public T Clone<T>(T value)
    {
        return Deserialize<T>(Serialize(value));
    }
}