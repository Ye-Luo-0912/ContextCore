using System.Text.Json;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.DecisionEngine.FlowDiagnostics;

/// <summary>
/// 候选流诊断装饰器：透明包装 <see cref="IContextDecisionRuntime"/>。
/// 关闭时是纯透传（零开销）；开启且采样命中时，把净化后的漏失归因报告
/// 写入输出目录（JSON），绝不影响主流程（写失败静默吞掉）。
/// 报告只含 ID/通道/结局/分数/token，不泄露正文或敏感数据。
/// </summary>
public sealed class FlowDiagnosticsRuntimeDecorator : IContextDecisionRuntime
{
    private readonly IContextDecisionRuntime _inner;
    private readonly FlowDiagnosticsOptions _options;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FlowDiagnosticsRuntimeDecorator(IContextDecisionRuntime inner, FlowDiagnosticsOptions options)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public ValueTask<ContextDecisionResult> ExecuteAsync(
        ContextDecisionRuntimeRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.ExecuteAsync(request, cancellationToken);

    public async ValueTask<ContextDecisionExecutionResult> ExecuteWithWorkingSetAsync(
        ContextDecisionRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.ExecuteWithWorkingSetAsync(request, cancellationToken).ConfigureAwait(false);
        if (_options.Enabled && _options.ShouldSample(request.RequestId))
        {
            await TryWriteAsync(request, result).ConfigureAwait(false);
        }
        return result;
    }

    private async Task TryWriteAsync(
        ContextDecisionRuntimeRequest request,
        ContextDecisionExecutionResult result)
    {
        try
        {
            var report = CandidatesFlowDiagnosticBuilder.Build(request, result);
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            var safeId = new string(request.RequestId
                .Select(c => char.IsLetterOrDigit(c) ? c : '-')
                .ToArray());
            var fileName = $"flow-{safeId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.json";
            var fullPath = Path.Combine(_options.OutputDirectory, fileName);

            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(_options.OutputDirectory);
                await File.WriteAllTextAsync(fullPath, json).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch
        {
            // 诊断是旁路：任何失败都不影响决策主流程。
        }
    }
}
