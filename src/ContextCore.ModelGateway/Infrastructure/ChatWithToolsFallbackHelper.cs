using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.ModelGateway.Infrastructure;

/// <summary>
/// P0-1：ChatWithToolsAsync 的降级实现辅助器。
/// </summary>
/// <remarks>
/// 当底层 <see cref="IModelAdapter"/> 仅接受 <see cref="ModelRequest"/>（拼接 prompt 字符串）
/// 而不原生支持 OpenAI / Anthropic function calling 时，<see cref="IModelGateway.ChatWithToolsAsync"/>
/// 通过本辅助器将结构化 messages + tool 定义序列化为 prompt，调用 <see cref="IModelGateway.CompleteAsync"/>，
/// 再尝试从响应内容解析 JSON 格式的 Tool 调用。
///
/// 解析协议（与 ModelGatewayAgentModelTransport 约定一致）：
/// 模型若决定调用 Tool，应在响应内容中产出如下 JSON（可被 ```json 代码块包裹）：
/// <code>
/// {
///   "tool_calls": [
///     { "id": "call_1", "name": "search", "arguments": { "query": "..." } }
///   ]
/// }
/// </code>
/// 解析成功 → <see cref="ModelChatFinishReason.ToolCalls"/>；失败 → <see cref="ModelChatFinishReason.Stop"/> + 原始文本。
/// </remarks>
internal static class ChatWithToolsFallbackHelper
{
    /// <summary>
    /// 将结构化对话请求转换为 <see cref="ModelRequest"/>（拼接 prompt + system prompt + tools 描述），
    /// 调用指定 gateway 的 <see cref="IModelGateway.CompleteAsync"/>，再解析响应。
    /// </summary>
    public static async Task<ModelChatResponse> ExecuteViaCompleteAsync(
        IModelGateway gateway,
        ModelChatRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(request);

        var (systemPrompt, userPrompt) = BuildPrompts(request);
        var modelRequest = new ModelRequest
        {
            OperationId = string.IsNullOrWhiteSpace(request.OperationId)
                ? Guid.NewGuid().ToString("N")
                : request.OperationId,
            Role = request.Role,
            Prompt = userPrompt,
            SystemPrompt = systemPrompt,
            ResponseFormat = request.ResponseFormat,
            Metadata = BuildMetadata(request)
        };

        var modelResponse = await gateway.CompleteAsync(modelRequest, cancellationToken).ConfigureAwait(false);

        if (!modelResponse.Succeeded)
        {
            return new ModelChatResponse
            {
                OperationId = modelResponse.OperationId,
                Content = string.Empty,
                ToolCalls = Array.Empty<ModelToolCall>(),
                FinishReason = ModelChatFinishReason.Error,
                InputTokens = modelResponse.InputTokens,
                OutputTokens = modelResponse.OutputTokens,
                Succeeded = false,
                ErrorMessage = modelResponse.ErrorMessage ?? "ModelGateway ChatWithTools 调用失败。",
                ModelId = modelResponse.Metadata.TryGetValue("modelName", out var mn) ? mn : null,
                Metadata = modelResponse.Metadata
            };
        }

        // 尝试从响应内容解析结构化 Tool 调用
        var toolCalls = TryParseToolCalls(modelResponse.Content, out var cleanedContent);
        var finishReason = toolCalls.Count > 0
            ? ModelChatFinishReason.ToolCalls
            : ModelChatFinishReason.Stop;

        return new ModelChatResponse
        {
            OperationId = modelResponse.OperationId,
            Content = toolCalls.Count > 0 ? cleanedContent : modelResponse.Content,
            ToolCalls = toolCalls,
            FinishReason = finishReason,
            InputTokens = modelResponse.InputTokens,
            OutputTokens = modelResponse.OutputTokens,
            Succeeded = true,
            ModelId = modelResponse.Metadata.TryGetValue("modelName", out var modelName) ? modelName : null,
            Metadata = modelResponse.Metadata
        };
    }

    /// <summary>将结构化 messages + tools 序列化为 (systemPrompt, userPrompt) 二元组。</summary>
    private static (string SystemPrompt, string UserPrompt) BuildPrompts(ModelChatRequest request)
    {
        var systemBuilder = new StringBuilder();
        var userBuilder = new StringBuilder();

        // 将 System 角色消息并入 SystemPrompt（OpenAI / Anthropic 兼容）
        foreach (var msg in request.Messages)
        {
            switch (msg.Role)
            {
                case ModelChatRole.System:
                    if (systemBuilder.Length > 0) systemBuilder.Append('\n');
                    systemBuilder.Append(msg.Content);
                    break;
                case ModelChatRole.User:
                    userBuilder.Append("[User]\n").Append(msg.Content).Append("\n---\n");
                    break;
                case ModelChatRole.Assistant:
                    userBuilder.Append("[Assistant]\n").Append(msg.Content).Append("\n---\n");
                    break;
                case ModelChatRole.Tool:
                    userBuilder.Append("[Tool")
                        .Append(!string.IsNullOrEmpty(msg.ToolName) ? ":" + msg.ToolName : string.Empty)
                        .Append("]\n").Append(msg.Content).Append("\n---\n");
                    break;
            }
        }

        // 若声明了 Tools，追加 Tool 描述（让模型可发起 JSON 格式的 Tool 调用）
        if (request.Tools.Count > 0)
        {
            if (systemBuilder.Length > 0) systemBuilder.Append("\n\n");
            systemBuilder.Append(BuildToolsSystemDirective(request.Tools));
        }

        return (systemBuilder.ToString(), userBuilder.ToString().TrimEnd('\n', '-'));
    }

    /// <summary>构造向模型声明 Tool 集合的系统指令（约定 JSON 输出格式）。</summary>
    private static string BuildToolsSystemDirective(IReadOnlyList<ModelToolDefinition> tools)
    {
        var sb = new StringBuilder();
        sb.Append("你可以调用以下 Tool 完成任务。若需要调用 Tool，请在响应中**仅**输出如下 JSON（可被 ```json 代码块包裹），不要输出其他文本：\n");
        sb.Append("{\n  \"tool_calls\": [\n    { \"id\": \"<调用ID>\", \"name\": \"<Tool名称>\", \"arguments\": <参数JSON对象> }\n  ]\n}\n\n");
        sb.Append("可用 Tool 列表：\n");
        for (var i = 0; i < tools.Count; i++)
        {
            var tool = tools[i];
            sb.Append("- ").Append(tool.Name);
            if (!string.IsNullOrEmpty(tool.Description))
            {
                sb.Append(": ").Append(tool.Description);
            }
            sb.Append("\n  参数 JSON Schema: ").Append(tool.ParametersJsonSchema).Append('\n');
        }
        sb.Append("\n若无需调用 Tool，请直接产出最终答案文本（不输出上述 JSON）。");
        return sb.ToString();
    }

    private static Dictionary<string, string> BuildMetadata(ModelChatRequest request)
    {
        var metadata = new Dictionary<string, string>(request.Metadata);
        if (!string.IsNullOrWhiteSpace(request.ModelArtifactId))
        {
            metadata["modelArtifactId"] = request.ModelArtifactId!;
        }
        if (request.DeadlineAt is { } deadline)
        {
            metadata["deadlineAt"] = deadline.ToString("O");
        }
        metadata["chatWithTools"] = "true";
        return metadata;
    }

    /// <summary>
    /// 尝试从模型响应内容解析 Tool 调用 JSON。
    /// 成功时返回 Tool 调用列表并将 <paramref name="cleanedContent"/> 设为模型附带的文本（无则空）。
    /// 失败时返回空列表且 <paramref name="cleanedContent"/> = 原始内容。
    /// </summary>
    private static IReadOnlyList<ModelToolCall> TryParseToolCalls(string content, out string cleanedContent)
    {
        cleanedContent = content;
        if (string.IsNullOrWhiteSpace(content))
        {
            return Array.Empty<ModelToolCall>();
        }

        var json = ExtractJsonObject(content);
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<ModelToolCall>();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tool_calls", out var callsEl)
                || callsEl.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ModelToolCall>();
            }

            var calls = new List<ModelToolCall>();
            foreach (var entry in callsEl.EnumerateArray())
            {
                if (!entry.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var name = nameEl.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;

                var id = entry.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString() ?? string.Empty
                    : $"call_{Guid.NewGuid():N}";

                string argumentsJson;
                if (entry.TryGetProperty("arguments", out var argsEl))
                {
                    argumentsJson = argsEl.ValueKind == JsonValueKind.String
                        ? (argsEl.GetString() ?? "{}")
                        : argsEl.GetRawText();
                }
                else
                {
                    argumentsJson = "{}";
                }

                calls.Add(new ModelToolCall
                {
                    Id = id!,
                    Name = name!,
                    ArgumentsJson = argumentsJson
                });
            }

            if (calls.Count == 0)
            {
                return Array.Empty<ModelToolCall>();
            }

            // 提取模型附带的文本（若有 "content" 字段；否则用空字符串）
            cleanedContent = doc.RootElement.TryGetProperty("content", out var contentEl)
                && contentEl.ValueKind == JsonValueKind.String
                ? (contentEl.GetString() ?? string.Empty)
                : string.Empty;
            return calls;
        }
        catch (JsonException)
        {
            return Array.Empty<ModelToolCall>();
        }
    }

    /// <summary>从可能包含 markdown 代码块或额外文本的内容中提取首个 JSON 对象。</summary>
    private static string ExtractJsonObject(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineBreak = trimmed.IndexOf('\n');
            if (firstLineBreak >= 0)
            {
                trimmed = trimmed[(firstLineBreak + 1)..];
            }
            var fenceIndex = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceIndex >= 0)
            {
                trimmed = trimmed[..fenceIndex];
            }
            trimmed = trimmed.Trim();
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return trimmed[start..(end + 1)];
        }
        return string.Empty;
    }
}
