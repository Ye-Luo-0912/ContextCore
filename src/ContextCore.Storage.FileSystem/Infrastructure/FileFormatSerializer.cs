using System.Text.Json;
using System.Text.Json.Serialization;
using ContextCore.Abstractions;

namespace ContextCore.Storage.FileSystem;

/// <summary>
/// 提供文件系统存储所使用的 JSON 序列化与反序列化功能。
/// 内部统一使用宽松的 JSON 选项，支持枚举字符串、忽略空值等。
/// </summary>
public sealed class FileFormatSerializer
{
	private static readonly JsonSerializerOptions _options = new()
	{
		WriteIndented = false,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { new JsonStringEnumConverter() }
	};

	private static readonly JsonSerializerOptions _prettyOptions = new()
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { new JsonStringEnumConverter() }
	};

	// ── Collection ────────────────────────────────────────────────────────────

	/// <summary>将集合对象序列化为 JSON 字符串（带缩进格式，便于阅读）。</summary>
	public string SerializeCollection(ContextCollection collection)
		=> JsonSerializer.Serialize(collection, _prettyOptions);

	/// <summary>将 JSON 字符串反序列化为集合对象。</summary>
	public ContextCollection? DeserializeCollection(string json)
		=> JsonSerializer.Deserialize<ContextCollection>(json, _options);

	// ── Item Metadata ─────────────────────────────────────────────────────────

	/// <summary>将条目元数据序列化为单行 JSON 字符串。</summary>
	public string SerializeItemMetadata(ContextItemMetadata metadata)
		=> JsonSerializer.Serialize(metadata, _options);

	/// <summary>将单行 JSON 字符串反序列化为条目元数据。</summary>
	public ContextItemMetadata? DeserializeItemMetadata(string line)
		=> JsonSerializer.Deserialize<ContextItemMetadata>(line, _options);

	// ── Index Entry ───────────────────────────────────────────────────────────

	/// <summary>将索引条目序列化为单行 JSON 字符串。</summary>
	public string SerializeIndexEntry(ContextIndexEntry entry)
		=> JsonSerializer.Serialize(entry, _options);

	/// <summary>将单行 JSON 字符串反序列化为索引条目。</summary>
	public ContextIndexEntry? DeserializeIndexEntry(string line)
		=> JsonSerializer.Deserialize<ContextIndexEntry>(line, _options);

	// ── Generic ───────────────────────────────────────────────────────────────

	/// <summary>将任意对象序列化为单行 JSON 字符串。</summary>
	public string Serialize<T>(T value)
		=> JsonSerializer.Serialize(value, _options);

	/// <summary>将单行 JSON 字符串反序列化为指定类型。</summary>
	public T? Deserialize<T>(string line)
		=> JsonSerializer.Deserialize<T>(line, _options);
}
