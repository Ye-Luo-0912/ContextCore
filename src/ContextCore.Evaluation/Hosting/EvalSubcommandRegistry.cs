namespace ContextCore.Evaluation.Hosting;

/// <summary>
/// P3-02：评测子命令注册项。替代 EvalCommand 中 490+ 行的 if/else-if 分发链。
/// 每个子命令注册一个异步处理器，通过名称查找分发。
/// </summary>
public sealed class EvalSubcommandRegistration
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required Func<object, string[], CancellationToken, Task> Handler { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
}

/// <summary>
/// P3-02：评测子命令注册表。替代巨型 if/switch 字符串分发。
/// 注册时按名称和别名索引，查找时 OrdinalIgnoreCase 匹配。
/// </summary>
public static class EvalSubcommandRegistry
{
    private static readonly Dictionary<string, EvalSubcommandRegistration> _registrations = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<EvalSubcommandRegistration> _ordered = new();

    public static void Register(EvalSubcommandRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        _registrations[registration.Name] = registration;
        foreach (var alias in registration.Aliases)
        {
            _registrations[alias] = registration;
        }

        _ordered.Add(registration);
    }

    public static bool TryResolve(string name, out EvalSubcommandRegistration registration)
    {
        return _registrations.TryGetValue(name, out registration!);
    }

    public static IReadOnlyList<EvalSubcommandRegistration> GetAll()
    {
        return _ordered;
    }
}
