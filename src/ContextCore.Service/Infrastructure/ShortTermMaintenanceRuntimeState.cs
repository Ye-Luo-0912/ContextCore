using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Service.Infrastructure;

/// <summary>维护短期记忆后台维护任务的运行时状态，供 runtime snapshot 与 ControlRoom 展示。</summary>
internal sealed class ShortTermMaintenanceRuntimeState
{
    private readonly object _gate = new();
    private ShortTermCompactionRun? _lastRun;
    private string? _lastError;
    private bool _enabled;
    private bool _isRunning;
    private bool _runOnStartup;
    private int _intervalSeconds;

    public void Configure(ShortTermMaintenanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            _enabled = options.Enabled;
            _runOnStartup = options.RunOnStartup;
            _intervalSeconds = Math.Max(1, options.IntervalSeconds);
        }
    }

    public void MarkRunning()
    {
        lock (_gate)
        {
            _isRunning = true;
        }
    }

    public void MarkIdle()
    {
        lock (_gate)
        {
            _isRunning = false;
        }
    }

    public void RecordRun(ShortTermCompactionRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (_gate)
        {
            _lastRun = run;
            _lastError = null;
        }
    }

    public void RecordError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
        {
            _lastError = $"{exception.GetType().Name}: {exception.Message}";
        }
    }

    public ShortTermMaintenanceStatusResponse Snapshot()
    {
        lock (_gate)
        {
            return new ShortTermMaintenanceStatusResponse
            {
                Enabled = _enabled,
                IsRunning = _isRunning,
                RunOnStartup = _runOnStartup,
                IntervalSeconds = _intervalSeconds,
                LastError = _lastError,
                LastRun = _lastRun
            };
        }
    }
}
