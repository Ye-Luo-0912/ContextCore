using System.Text;

namespace ContextCore.Core.Services.Learning.V14_0;

public interface IRuntimeCandidateTraceSink
{
    bool Enabled { get; }
    void Write(RuntimeCandidateTraceRow row);
    int WriteCount { get; }
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>Number of Write calls that threw an exception. Default 0 for sinks that never fail.</summary>
    int WriteFailures => 0;
    /// <summary>Number of rows dropped (e.g. because the underlying writer is unavailable). Default 0.</summary>
    int DroppedCount => 0;
}

public sealed class NullRuntimeCandidateTraceSink : IRuntimeCandidateTraceSink
{
    public bool Enabled => false;
    public int WriteCount => 0;
    public void Write(RuntimeCandidateTraceRow row) { }
    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class FileRuntimeCandidateTraceSink : IRuntimeCandidateTraceSink, IDisposable
{
    private readonly string _path;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private int _writeCount;
    private int _writeFailures;
    private int _droppedDueToNullWriter;
    private bool _disposed;

    public bool Enabled => true;
    public string Path => _path;
    public int WriteCount => _writeCount;
    public int WriteFailures => _writeFailures;
    public int DroppedCount => _droppedDueToNullWriter;

    public FileRuntimeCandidateTraceSink(string? filePath = null)
    {
        _path = filePath ?? System.IO.Path.Combine("learning", "v14", "runtime-candidate-trace.jsonl");
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(dir)) System.IO.Directory.CreateDirectory(dir);
        try { _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8) { AutoFlush = false }; }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[TraceSink] Failed to open {_path}: {ex.Message}");
            _writer = null;
        }
    }

    public void Write(RuntimeCandidateTraceRow row)
    {
        if (_writer is null)
        {
            Interlocked.Increment(ref _droppedDueToNullWriter);
            return;
        }
        try { var line = row.ToJsonLine(); lock (_lock) { _writer.WriteLine(line); _writeCount++; } }
        catch (Exception ex) { Interlocked.Increment(ref _writeFailures); System.Diagnostics.Trace.WriteLine($"[TraceSink] Write failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        try { lock (_lock) { _writer?.Flush(); _writer?.Dispose(); } } catch { }
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_writer is null) return;
        try { lock (_lock) { _writer.Flush(); } await Task.CompletedTask.ConfigureAwait(false); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[TraceSink] Flush failed: {ex.Message}"); }
    }
}

/// Static accessor for shadow instrumentation — null by default (no-op).
/// Set to a FileRuntimeCandidateTraceSink in eval/shadow mode before entering the main pipeline.
public static class RuntimeCandidateTraceSinkAccessor
{
    public static IRuntimeCandidateTraceSink Current { get; set; } = new NullRuntimeCandidateTraceSink();
    public static string? CurrentOperationId { get; set; }
    public static string? CurrentRequestId { get; set; }
}
