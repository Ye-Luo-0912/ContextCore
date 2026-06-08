using ContextCore.Abstractions;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Rendering;

/// <summary>将上下文条目或包文件详情渲染为控制台输出的静态工具类。</summary>
public static class DetailRenderer
{
    public static void Render(ControlRoomDetail detail)
    {
        Console.WriteLine();
        Console.WriteLine(detail.Title);
        Console.WriteLine(new string('=', detail.Title.Length));

        foreach (var (key, value) in detail.Fields)
        {
            Console.WriteLine($"{key,-14}: {value}");
        }

        WriteSection("标签", detail.Tags);
        WriteSection("来源引用", detail.SourceRefs);

        if (detail.Metadata.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("元数据");
            foreach (var (key, value) in detail.Metadata)
            {
                Console.WriteLine($"  {key}: {value}");
            }
        }

        if (detail.Relations.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("关系");
            foreach (var relation in detail.Relations)
            {
                Console.WriteLine($"  {relation.SourceId} --{relation.RelationType}({relation.Weight:0.00})--> {relation.TargetId}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("内容预览");
        Console.WriteLine("--------");
        Console.WriteLine(Preview(detail.Content, 1600));
    }

    public static void RenderPackage(ContextPackage package)
    {
        Console.WriteLine();
        Console.WriteLine($"上下文包 {package.PackageId}");
        Console.WriteLine(new string('=', 16 + package.PackageId.Length));
        Console.WriteLine($"工作区 : {package.WorkspaceId}");
        Console.WriteLine($"集合   : {package.CollectionId}");
        Console.WriteLine($"Token  : {package.EstimatedTokens}");
        Console.WriteLine($"估算源 : {MetadataValue(package, ContextTokenizationMetadataKeys.Source)}");
        Console.WriteLine($"估算模型: {MetadataValue(package, ContextTokenizationMetadataKeys.Model)}");
        Console.WriteLine($"是否回退: {MetadataValue(package, ContextTokenizationMetadataKeys.IsFallback)}");
        Console.WriteLine($"来源数 : {package.SourceRefs.Count}");

        TableRenderer.Render(
            "包片段",
            ["名称", "优先级", "Token", "来源", "预览"],
            package.Sections.Select(section => new[]
            {
                section.Name,
                section.Priority.ToString(),
                section.EstimatedTokens.ToString(),
                section.SourceRefs.Count.ToString(),
                Preview(section.Content, 96)
            }).ToArray());

        if (package.SourceRefs.Count > 0)
        {
            WriteSection("来源引用", package.SourceRefs);
        }
    }


    private static string MetadataValue(ContextPackage package, string key)
    {
        return package.Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : "无";
    }
    private static void WriteSection(string title, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine(title);
        foreach (var value in values)
        {
            Console.WriteLine($"  - {value}");
        }
    }

    private static string Preview(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var normalized = value.ReplaceLineEndings(Environment.NewLine);
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength] + "...";
    }
}
