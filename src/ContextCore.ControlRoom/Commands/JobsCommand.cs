using ContextCore.Abstractions;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Commands;

/// <summary>查询并展示作业队列状态的命令。</summary>
public static class JobsCommand
{
    public static async Task ExecuteAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        if (args.Count > 0 && string.Equals(args[0], "show", StringComparison.OrdinalIgnoreCase))
        {
            await ShowCommand.ExecuteAsync(service, args.Skip(1).ToArray(), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        ContextJobState? state = null;
        var stateText = CommandHelpers.GetOption(args, "--state") ?? args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));
        if (Enum.TryParse<ContextJobState>(stateText, ignoreCase: true, out var parsedState))
        {
            state = parsedState;
        }

        var jobs = await service.QueryJobsAsync(
            state,
            CommandHelpers.GetIntOption(args, "--take", 100),
            cancellationToken).ConfigureAwait(false);

        TableRenderer.Render(
            "Jobs",
            ["JobId", "Kind", "State", "Priority", "Retry", "Created", "Error"],
            [.. jobs.Select(job => new[]
            {
                job.JobId,
                job.Kind.ToString(),
                job.State.ToString(),
                job.Priority.ToString(),
                $"{job.RetryCount}/{job.MaxRetryCount}",
                job.CreatedAt.ToString("u"),
                job.ErrorMessage ?? ""
            })]);
    }
}
