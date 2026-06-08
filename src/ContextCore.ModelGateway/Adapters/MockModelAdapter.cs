using ContextCore.Abstractions;
using ContextCore.ModelGateway.Adapters;

namespace ContextCore.ModelGateway;

/// <summary>返回预置回复的 <see cref="IModelAdapter"/> 模拟实现，适用于测试和开发环境。</summary>
/// <remarks>
/// TODO-DEMO [P0-1]：此适配器不调用任何真实 API，仅返回固定字符串。
/// 生产使用前请替换为 <see cref="OpenAiCompatibleModelAdapter"/> 或其他真实实现。
/// 参见：TODO.md → P0-1
/// </remarks>
public sealed class MockModelAdapter : IModelAdapter
{
    private readonly string _content;

    public MockModelAdapter()
        : this("mock")
    {
    }

    public MockModelAdapter(string name, string? content = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "mock" : name;
        _content = string.IsNullOrWhiteSpace(content) ? $"来自 {Name} 的模拟模型响应。" : content;
    }

    public string Name { get; }

    public Task<ModelResponse> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var content = request.Metadata.TryGetValue("mockContent", out var metadataContent)
            && !string.IsNullOrWhiteSpace(metadataContent)
                ? metadataContent
                : $"{_content} 角色：{request.Role}。";

        return Task.FromResult(new ModelResponse
        {
            OperationId = string.IsNullOrWhiteSpace(request.OperationId)
                ? Guid.NewGuid().ToString("N")
                : request.OperationId,
            Content = content,
            InputTokens = EstimateTokens(request.Prompt) + EstimateTokens(request.SystemPrompt),
            OutputTokens = EstimateTokens(content),
            Succeeded = true,
            Metadata = new Dictionary<string, string>
            {
                ["modelName"] = Name,
                ["provider"] = "mock"
            }
        });
    }

    private static int EstimateTokens(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        return Math.Max(1, (value.Length + 1) / 2);
    }
}
