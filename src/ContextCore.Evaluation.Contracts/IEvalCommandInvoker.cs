using ContextCore.Client;

namespace ContextCore.Evaluation.Contracts;

/// <summary>Eval 命令调用契约。ControlRoom 通过此接口调用 Evaluation，替代反射 dispatch。</summary>
public interface IEvalCommandInvoker
{
    Task ExecuteAsync(
        IEvalHost host,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken);
}
