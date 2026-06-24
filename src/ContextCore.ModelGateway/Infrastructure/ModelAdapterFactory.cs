using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ModelGateway.Adapters;

namespace ContextCore.ModelGateway.Infrastructure;

/// <summary>根据 <see cref="ModelGatewayOptions"/> 配置创建 <see cref="IModelAdapter"/> 实例列表的工厂。</summary>
public static class ModelAdapterFactory
{
    public static IReadOnlyList<IModelAdapter> CreateAdapters(
        ModelGatewayOptions options,
        ApiKeyResolver? apiKeyResolver = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var resolver = apiKeyResolver ?? new ApiKeyResolver();
        var effectiveOptions = ModelGatewayOptionsMaterializer.Materialize(options);
        var adapters = new List<IModelAdapter>();
        foreach (var model in effectiveOptions.Models.Where(model => model.Enabled))
        {
            if (IsOpenAiCompatibleProvider(model.Provider))
            {
                adapters.Add(new OpenAiCompatibleModelAdapter(model, resolver));
                continue;
            }

            if (IsLocalOpenAiCompatibleProvider(model.Provider))
            {
                adapters.Add(new LocalHttpModelAdapter(model, resolver));
                continue;
            }

            if (string.Equals(model.Provider, "mock", StringComparison.OrdinalIgnoreCase))
            {
                adapters.Add(new MockModelAdapter(model.Name));
            }
        }

        return adapters;
    }

    private static bool IsOpenAiCompatibleProvider(string provider)
    {
        return provider.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("openai", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("bigmodel", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("glm", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("zhipu", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("zhipuai", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("deepseek", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocalOpenAiCompatibleProvider(string provider)
    {
        return provider.Equals("local-http", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("local-openai", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("local-openai-compatible", StringComparison.OrdinalIgnoreCase);
    }
}
