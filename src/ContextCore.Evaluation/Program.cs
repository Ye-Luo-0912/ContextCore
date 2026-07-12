using ContextCore.Evaluation.Commands;
using ContextCore.Evaluation.Hosting;

namespace ContextCore.Evaluation;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            EvalCommand.PrintUsage();
            return 0;
        }

        try
        {
            // Evaluation 独立运行时使用本地 InMemory 状态，
            // 不再依赖 ControlRoom 的 IEvalHost。
            var workspaceId = Environment.GetEnvironmentVariable("CONTEXT_WORKSPACE_ID") ?? "default";
            var collectionId = Environment.GetEnvironmentVariable("CONTEXT_COLLECTION_ID") ?? "default";

            var host = new LocalEvalHost(workspaceId, collectionId);
            await EvalCommand.ExecuteAsync(host, args, CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"eval: {ex.Message}");
            return 1;
        }
    }
}
