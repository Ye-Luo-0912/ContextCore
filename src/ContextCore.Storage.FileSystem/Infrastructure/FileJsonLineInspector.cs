using System.Text.Json;

namespace ContextCore.Storage.FileSystem;

/// <summary>
/// JSONL 文件检查工具，用于发现空行以外的损坏 JSON 行。
/// </summary>
public sealed class FileJsonLineInspector
{
    private readonly FileSystemReader _reader;

    public FileJsonLineInspector()
        : this(new FileSystemReader())
    {
    }

    public FileJsonLineInspector(FileSystemReader reader)
    {
        _reader = reader;
    }

    public async Task<FileJsonLineInspectionReport> InspectAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var lines = await _reader.ReadAllLinesAsync(path, cancellationToken)
            .ConfigureAwait(false);
        var issues = new List<FileJsonLineIssue>();
        var validCount = 0;
        var blankCount = 0;

        for (var index = 0; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                blankCount++;
                continue;
            }

            try
            {
                using var _ = JsonDocument.Parse(line);
                validCount++;
            }
            catch (JsonException ex)
            {
                issues.Add(new FileJsonLineIssue
                {
                    LineNumber = index + 1,
                    Message = ex.Message,
                    Preview = line.Length <= 160 ? line : line[..160]
                });
            }
        }

        return new FileJsonLineInspectionReport
        {
            Path = path,
            TotalLines = lines.Count,
            ValidLines = validCount,
            BlankLines = blankCount,
            CorruptLines = issues.Count,
            Issues = issues
        };
    }
}

/// <summary>JSONL 文件检查报告。</summary>
public sealed class FileJsonLineInspectionReport
{
    public string Path { get; init; } = string.Empty;

    public int TotalLines { get; init; }

    public int ValidLines { get; init; }

    public int BlankLines { get; init; }

    public int CorruptLines { get; init; }

    public IReadOnlyList<FileJsonLineIssue> Issues { get; init; } = Array.Empty<FileJsonLineIssue>();

    public bool IsHealthy => CorruptLines == 0;
}

/// <summary>JSONL 损坏行信息。</summary>
public sealed class FileJsonLineIssue
{
    public int LineNumber { get; init; }

    public string Message { get; init; } = string.Empty;

    public string Preview { get; init; } = string.Empty;
}
