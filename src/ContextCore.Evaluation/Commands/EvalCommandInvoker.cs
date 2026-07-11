using ContextCore.Client;
using ContextCore.Evaluation.Contracts;

namespace ContextCore.Evaluation.Commands;

/// <summary>
/// EvalCommand 的接口化调用器。ControlRoom 通过 IEvalCommandInvoker 调用 Evaluation，
/// 替代反射 dispatch。委托给静态 EvalCommand.ExecuteAsync。
/// </summary>
public sealed class EvalCommandInvoker : IEvalCommandInvoker
{
    public Task ExecuteAsync(
        IEvalHost host,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
        => EvalCommand.ExecuteAsync(host, args, cancellationToken);
}
