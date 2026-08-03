using ContextCore.Evaluation.Hosting;
using ContextCore.Evaluation.Models;

namespace ContextCore.Evaluation.Commands;

/// <summary>Eval 子命令处理器委托。</summary>
/// <param name="service">评测宿主服务。</param>
/// <param name="args">原始命令行参数（包含子命令名本身）。</param>
/// <param name="subcommand">已解析的子命令名。</param>
/// <param name="cancellationToken">取消令牌。</param>
public delegate Task EvalSubcommandHandler(
    IEvalHost service,
    IReadOnlyList<string> args,
    string subcommand,
    CancellationToken cancellationToken);

/// <summary>Eval 子命令注册条目。</summary>
public sealed record EvalSubcommandEntry
{
    /// <summary>子命令名（大小写不敏感）。</summary>
    public required string Name { get; init; }

    /// <summary>帮助文本（用于 eval 无参数时的 usage 输出）。</summary>
    public string? Description { get; init; }

    /// <summary>
    /// usage 行（如 " eval run [--category &lt;name&gt;] [--out &lt;path&gt;]"）。
    /// 为 null 时 PrintUsage 自动生成 " eval &lt;name&gt;"。
    /// </summary>
    public string? UsageLine { get; init; }

    /// <summary>处理委托。</summary>
    public required EvalSubcommandHandler Handler { get; init; }
}

/// <summary>
/// Eval 子命令注册表。替代原先的 s_knownSubcommands HashSet + 470 分支 if-chain，
/// 提供 O(1) 字典查找分发。
/// </summary>
public sealed class EvalSubcommandRegistry
{
    private readonly Dictionary<string, EvalSubcommandEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>注册一个子命令。重复注册将抛出异常。</summary>
    public void Register(EvalSubcommandEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new ArgumentException("子命令名不能为空。", nameof(entry));
        }

        _entries.Add(entry.Name, entry);
    }

    /// <summary>注册一个子命令（便捷重载）。</summary>
    public void Register(string name, EvalSubcommandHandler handler, string? description = null)
    {
        Register(new EvalSubcommandEntry
        {
            Name = name,
            Handler = handler,
            Description = description
        });
    }

    /// <summary>
    /// 仅注册命令名（不含 handler）。用于命令名存在性检查（替代 s_knownSubcommands），
    /// 实际分发由 TryDispatchSubcommandAsync if-chain 处理。
    /// </summary>
    /// <param name="usageLine">usage 行；为 null 时 PrintUsage 自动生成 " eval &lt;name&gt;"。</param>
    public void RegisterCommandOnly(string name, string? description = null, string? usageLine = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("子命令名不能为空。", nameof(name));
        }

        _entries[name] = new EvalSubcommandEntry
        {
            Name = name,
            Handler = (_, _, _, _) => Task.CompletedTask,
            Description = description,
            UsageLine = usageLine
        };
    }

    /// <summary>
    /// 注册命令名并携带 usage 行（不含 handler）。用于 PrintUsage 自动生成。
    /// </summary>
    public void RegisterWithUsage(string name, string usageLine, string? description = null)
    {
        RegisterCommandOnly(name, description, usageLine);
    }

    /// <summary>将多个别名注册到同一个 handler。</summary>
    public void RegisterAliases(
        IReadOnlyList<string> names,
        EvalSubcommandHandler handler,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(handler);
        foreach (var name in names)
        {
            Register(name, handler, description);
        }
    }

    /// <summary>尝试获取子命令条目。</summary>
    public bool TryGetEntry(string subcommand, out EvalSubcommandEntry entry) =>
        _entries.TryGetValue(subcommand, out entry!);

    /// <summary>判断子命令是否已注册。</summary>
    public bool Contains(string subcommand) => _entries.ContainsKey(subcommand);

    /// <summary>获取所有已注册的子命令名（按字母排序）。</summary>
    public IReadOnlyList<string> GetAllNames() =>
        _entries.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>获取所有已注册条目（按名称排序）。</summary>
    public IReadOnlyList<EvalSubcommandEntry> GetAllEntries() =>
        _entries.Values
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
