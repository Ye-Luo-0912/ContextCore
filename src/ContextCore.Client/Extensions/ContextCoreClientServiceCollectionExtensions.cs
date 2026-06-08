using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ContextCore.Client.Extensions;

/// <summary>将 ContextCore HTTP 客户端注册到 Microsoft DI 容器的扩展方法。</summary>
public static class ContextCoreClientServiceCollectionExtensions
{
    /// <summary>
    /// 注册类型化 <see cref="ContextCoreClient"/>，并允许调用方覆盖服务根地址等配置。
    /// </summary>
    public static IServiceCollection AddContextCoreClient(
        this IServiceCollection services,
        Action<ContextCoreClientOptions>? configure = null)
    {
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddHttpClient<ContextCoreClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<ContextCoreClientOptions>>().Value;
            client.BaseAddress = options.BaseAddress;
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation(options.ApiKeyHeaderName, options.ApiKey);
            }
        });

        return services;
    }
}
