using ContextCore.Abstractions;

namespace ContextCore.Storage.FileSystem;

/// <summary>
/// 表示存储在 JSONL 元数据文件中的上下文条目轻量级元信息。
/// 仅包含必要的字段，不含原始内容，用于快速查询和过滤。
/// </summary>
public sealed class ContextItemMetadata
{
	/// <summary>条目唯一标识符。</summary>
	public string Id { get; init; } = string.Empty;

	/// <summary>所属工作空间 ID。</summary>
	public string WorkspaceId { get; init; } = string.Empty;

	/// <summary>所属集合 ID。</summary>
	public string CollectionId { get; init; } = string.Empty;

	/// <summary>条目类型。</summary>
	public string Type { get; init; } = string.Empty;

	/// <summary>条目标题。</summary>
	public string? Title { get; init; }

	/// <summary>内容格式。</summary>
	public ContextContentFormat ContentFormat { get; init; } = ContextContentFormat.PlainText;

	/// <summary>标签列表。</summary>
	public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

	/// <summary>引用 ID 列表。</summary>
	public IReadOnlyList<string> Refs { get; init; } = Array.Empty<string>();

	/// <summary>来源引用 ID 列表。</summary>
	public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

	/// <summary>附加元数据键值对。</summary>
	public Dictionary<string, string> Metadata { get; init; } = new();

	/// <summary>重要性分数。</summary>
	public double Importance { get; init; }

	/// <summary>版本号。</summary>
	public long Version { get; init; }

	/// <summary>内容校验和。</summary>
	public string? Checksum { get; init; }

	/// <summary>创建时间。</summary>
	public DateTimeOffset CreatedAt { get; init; }

	/// <summary>最后更新时间。</summary>
	public DateTimeOffset UpdatedAt { get; init; }

	/// <summary>
	/// 从 <see cref="ContextItem"/> 创建元数据对象（不含内容）。
	/// </summary>
	public static ContextItemMetadata FromItem(ContextItem item) => new()
	{
		Id = item.Id,
		WorkspaceId = item.WorkspaceId,
		CollectionId = item.CollectionId,
		Type = item.Type,
		Title = item.Title,
		ContentFormat = item.ContentFormat,
		Tags = item.Tags.ToArray(),
		Refs = item.Refs.ToArray(),
		SourceRefs = item.SourceRefs.ToArray(),
		Metadata = new Dictionary<string, string>(item.Metadata),
		Importance = item.Importance,
		Version = item.Version,
		Checksum = item.Checksum,
		CreatedAt = item.CreatedAt,
		UpdatedAt = item.UpdatedAt
	};

	/// <summary>
	/// 将元数据与内容合并，还原为完整的 <see cref="ContextItem"/>。
	/// </summary>
	/// <param name="content">原始文本内容。</param>
	public ContextItem ToContextItem(string content) => new()
	{
		Id = Id,
		WorkspaceId = WorkspaceId,
		CollectionId = CollectionId,
		Type = Type,
		Title = Title,
		Content = content,
		ContentFormat = ContentFormat,
		Tags = Tags.ToArray(),
		Refs = Refs.ToArray(),
		SourceRefs = SourceRefs.ToArray(),
		Metadata = new Dictionary<string, string>(Metadata),
		Importance = Importance,
		Version = Version,
		Checksum = Checksum,
		CreatedAt = CreatedAt,
		UpdatedAt = UpdatedAt
	};
}
