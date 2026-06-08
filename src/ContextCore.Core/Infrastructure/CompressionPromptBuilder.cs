using System.Text.Json;
using ContextCore.Abstractions;

namespace ContextCore.Core;

/// <summary>为 LLM 上下文压缩构建只返回 JSON 的结构化提示词。</summary>
public sealed class CompressionPromptBuilder
{
    /// <summary>当前提示词版本，变更 prompt 模板时同步递增（用于质量跟踪）。</summary>
    public const string PromptVersion = "cc-compress-v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ModelRequest Build(CompressionRequest request, string operationId)
    {
        ArgumentNullException.ThrowIfNull(request);

        var taskName = ResolveTaskName(request);
        var thinkingMode = ResolveThinkingMode(request.Options);
        var inputPayload = request.Inputs.Select(input => new
        {
            input.Id,
            input.Title,
            input.Type,
            input.ContentFormat,
            input.Tags,
            input.Refs,
            input.SourceRefs,
            input.Importance,
            input.Metadata,
            input.Content
        });

        var promptPayload = new
        {
            operationId,
            request.WorkspaceId,
            request.CollectionId,
            task = taskName,
            depth = request.Options.Depth.ToString(),
            thinkingMode,
            targetTokenBudget = request.Options.TargetTokenBudget,
            generateIndexHints = request.Options.GenerateIndexHints,
            preserveSourceRefs = request.Options.PreserveSourceRefs,
            request.Metadata,
            request.ExtensionPayloadJson,
            inputs = inputPayload
        };

        return new ModelRequest
        {
            OperationId = operationId,
            Role = ResolveModelRole(request.Options.ModelRole),
            SystemPrompt = BuildSystemPrompt(),
            Prompt = JsonSerializer.Serialize(promptPayload, JsonOptions),
            ResponseFormat = "json",
            Metadata = new Dictionary<string, string>
            {
                ["compressionTask"] = taskName,
                ["compressionDepth"] = request.Options.Depth.ToString(),
                ["thinkingMode"] = thinkingMode,
                ["inputCount"] = request.Inputs.Count.ToString()
            }
        };
    }

    public static ModelRole ResolveModelRole(string? configuredRole)
    {
        return !string.IsNullOrWhiteSpace(configuredRole)
            && Enum.TryParse<ModelRole>(configuredRole, ignoreCase: true, out var role)
                ? role
                : ModelRole.GeneralCompression;
    }

    public static string ResolveTaskName(CompressionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.SubKind))
        {
            return request.SubKind!;
        }

        return request.TaskKind switch
        {
            CompressionTaskKind.Summarize => "Summarize",
            CompressionTaskKind.Extract => "ExtractKeyPoints",
            CompressionTaskKind.RebuildIndex => "GenerateIndexHints",
            _ => request.TaskKind.ToString()
        };
    }

    public static string ResolveThinkingMode(CompressionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.ThinkingMode))
        {
            return options.ThinkingMode!.Trim().ToLowerInvariant();
        }

        return options.Depth switch
        {
            CompressionDepth.Light => "fast",
            CompressionDepth.Deep => "deep",
            CompressionDepth.Audit => "audit",
            _ => "balanced"
        };
    }

    private static string BuildSystemPrompt()
    {
        return """
        你是 ContextCore 的上下文压缩引擎。
        只能返回一个合法 JSON 对象，不要使用 Markdown 代码块包裹。
        请使用以下结构：
        {
          "status": "succeeded | partially_succeeded | requires_review | failed",
          "summary": "Markdown 摘要或压缩后的内容",
          "keyPoints": ["短小、稳定、可复用的要点"],
          "tags": ["tag"],
          "indexHints": [{"key":"检索关键词","kind":"keyword|tag|type","weight":0.8}],
          "warnings": [{"code":"ShortCode","message":"给人看的警告"}],
          "errors": [{"code":"ShortCode","message":"给人看的错误","detail":"可选详情"}],
          "requiresReview": false,
          "confidence": 0.0
        }
        保留重要事实、约束、实体、决策和来源可追溯性。
        遵守 thinkingMode：fast 要简洁；balanced 保持常规；deep 要更谨慎；audit 必须暴露不确定性和复核风险。
        遵守 depth：Light 保留更多上下文；Deep 更强压缩；Audit 明确说明不确定性和风险。
        """;
    }
}
