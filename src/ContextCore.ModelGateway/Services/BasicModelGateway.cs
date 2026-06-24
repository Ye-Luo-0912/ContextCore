using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.ModelGateway;

/// <summary>
/// <see cref="IModelGateway"/> 的基础实现，按顺序选择第一个可用适配器处理请求。
/// </summary>
/// <remarks>
/// TODO-DEMO [P1]：此实现无路由策略，不支持角色路由、回退与重试。
/// 生产场景请使用 <see cref="ConfigurableModelGateway"/>。参见：TODO.md → P1
/// </remarks>
public sealed class BasicModelGateway : IModelGateway
{
    private readonly IReadOnlyList<IModelAdapter> _adapters;

    public BasicModelGateway()
        : this([new MockModelAdapter()])
    {
    }

    public BasicModelGateway(IEnumerable<IModelAdapter> adapters)
    {
        _adapters = adapters.ToArray();

        if (_adapters.Count == 0)
        {
            throw new ArgumentException("At least one model adapter is required.", nameof(adapters));
        }
    }

    public async Task<ModelResponse> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = ContextCoreDiagnostics.StartOperation("model.basic.complete", request.OperationId);
        activity?.SetTag("contextcore.model.role", request.Role.ToString());

        var adapter = ResolveAdapter(request);
        activity?.SetTag("contextcore.model.adapter", adapter.Name);

        try
        {
            var response = await adapter.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            ContextCoreDiagnostics.SetStatus(activity, response.Succeeded, response.ErrorMessage);
            return response;
        }
        catch (Exception ex)
        {
            ContextCoreDiagnostics.SetStatus(activity, succeeded: false, ex.Message);
            throw;
        }
    }

    private IModelAdapter ResolveAdapter(ModelRequest request)
    {
        if (request.Metadata.TryGetValue("adapter", out var requestedAdapter)
            && !string.IsNullOrWhiteSpace(requestedAdapter))
        {
            var match = _adapters.FirstOrDefault(adapter =>
                string.Equals(adapter.Name, requestedAdapter, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                return match;
            }
        }

        return _adapters[0];
    }
}
