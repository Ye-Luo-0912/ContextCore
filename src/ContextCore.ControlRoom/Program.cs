using ContextCore.ControlRoom.Commands;
using ContextCore.ControlRoom.Services;
using ContextCore.ControlRoom.Screens;
using ContextCore.Storage.FileSystem;
using System.Text.Json;

var defaults = ControlRoomDefaults.Load();
var parsed = Cli.Parse(args, defaults);
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
    Environment.ExitCode = 0;
};

var selection = ResolveWorkspaceSelection(parsed);
var state = CreateState(parsed, selection);
var service = new ControlRoomService(state);

try
{
    if (string.IsNullOrWhiteSpace(parsed.Command) || string.Equals(parsed.Command, "interactive", StringComparison.OrdinalIgnoreCase))
    {
        await RunInteractiveAsync(parsed, selection, shutdown.Token);
        return;
    }

    await ExecuteCommandAsync(service, parsed.Command, parsed.CommandArgs, shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    Environment.ExitCode = 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ControlRoom 错误：{ex.Message}");
    Environment.ExitCode = 1;
}

static async Task ExecuteCommandAsync(
    ControlRoomService service,
    string command,
    IReadOnlyList<string> commandArgs,
    CancellationToken cancellationToken)
{
    if (service.State.IsServiceMode
        && !IsSupportedServiceModeCommand(command))
    {
        Console.Error.WriteLine($"ControlRoom Service 模式暂不支持命令：{command}");
        Environment.ExitCode = 2;
        return;
    }

    switch (command.ToLowerInvariant())
    {
        case "status":
            await StatusCommand.ExecuteAsync(service, cancellationToken);
            break;
        case "list":
            await ListCommand.ExecuteAsync(service, commandArgs, cancellationToken);
            break;
        case "show":
            await ShowCommand.ExecuteAsync(service, commandArgs, cancellationToken);
            break;
        case "package-preview":
        case "package":
            await PackagePreviewCommand.ExecuteAsync(service, commandArgs, cancellationToken);
            break;
        case "jobs":
            await JobsCommand.ExecuteAsync(service, commandArgs, cancellationToken);
            break;
        case "memory":
            await MemoryCommand.ExecuteAsync(service, commandArgs, cancellationToken);
            break;
        case "promotion":
        case "promotions":
            await PromotionCommand.ExecuteAsync(service, commandArgs, cancellationToken);
            break;
        case "relations":
            await RelationsCommand.ExecuteAsync(service, commandArgs, cancellationToken);
            break;
        case "constraints":
            await ConstraintsCommand.ExecuteAsync(service, commandArgs, cancellationToken);
            break;
        case "index":
            await IndexCommand.ExecuteAsync(service, commandArgs, cancellationToken);
            break;
        case "retrieval":
            await RetrievalCommand.ExecuteAsync(service, commandArgs, cancellationToken);
            break;
        case "model":
            await ModelCommand.ExecuteAsync(service, commandArgs, cancellationToken);
            break;
        case "report":
            await ReportCommand.ExecuteAsync(service, commandArgs, cancellationToken);
            break;
        case "policy":
        case "policies":
            await PolicyCommand.ExecuteAsync(service, commandArgs, cancellationToken);
            break;
        case "eval":
            await EvalCommand.ExecuteAsync(service, commandArgs, cancellationToken);
            break;
        case "backup":
            await BackupCommand.ExecuteAsync(service, commandArgs, cancellationToken);
            break;
        case "help":
        case "--help":
        case "-h":
            PrintHelp();
            break;
        default:
            Console.Error.WriteLine($"未知命令：{command}");
            PrintHelp();
            Environment.ExitCode = 2;
            break;
    }
}

static async Task RunInteractiveAsync(
    Cli parsed,
    WorkspaceSelection selection,
    CancellationToken cancellationToken)
{
    // 启动时显示存储根目录绝对路径，便于确认数据读写位置
    Console.WriteLine(parsed.IsServiceMode
        ? $"[ControlRoom] Service: {parsed.ServiceBaseUrl}"
        : $"[ControlRoom] Storage root: {parsed.RootPath}");
    var autoRefresh = false;
    var refreshSeconds = Math.Max(1, parsed.RefreshSeconds);
    var service = new ControlRoomService(CreateState(parsed, selection));

    while (!cancellationToken.IsCancellationRequested)
    {
        if (!Console.IsInputRedirected && !Console.IsOutputRedirected)
        {
            Console.Clear();
        }
        await DashboardScreen.ShowAsync(service, autoRefresh, refreshSeconds, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine();
        Console.Write("> ");

        var action = ControlRoomInteraction.InterpretDashboardInput(
            await ReadDashboardInputAsync(autoRefresh, refreshSeconds, cancellationToken).ConfigureAwait(false));

        switch (action.Kind)
        {
            case ControlRoomActionKind.Refresh:
                break;
            case ControlRoomActionKind.ToggleAutoRefresh:
                autoRefresh = !autoRefresh;
                break;
            case ControlRoomActionKind.Workspace:
                if (service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("工作区切换");
                    break;
                }
                selection = SelectWorkspace(parsed, selection);
                service = new ControlRoomService(CreateState(parsed, selection));
                break;
            case ControlRoomActionKind.Collection:
                if (service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("集合切换");
                    break;
                }
                selection = SelectCollection(parsed, selection);
                service = new ControlRoomService(CreateState(parsed, selection));
                break;
            case ControlRoomActionKind.OpenContextExplorer:
                if (service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("上下文浏览");
                    break;
                }
                if (await ContextExplorerScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenMemoryLayers:
                if (service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("记忆层浏览");
                    break;
                }
                if (await MemoryLayerScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenPackagePreview:
                if (service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("上下文包预览");
                    break;
                }
                if (await PackagePreviewScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenJobs:
                if (service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("任务浏览");
                    break;
                }
                if (await JobMonitorScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenRelations:
                if (service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("关系浏览");
                    break;
                }
                if (await RelationViewerScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenConstraints:
                if (service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("约束浏览");
                    break;
                }
                if (await ConstraintScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenIndexSearch:
                if (service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("索引检索");
                    break;
                }
                if (await IndexSearchScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenRetrievalDebug:
                if (service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("检索调试");
                    break;
                }
                if (await RetrievalDebugScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenServiceDashboard:
                if (!service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("Service Dashboard 仅在 Service 模式可用");
                    break;
                }
                if (await ServiceDashboardScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenServiceIngest:
                if (!service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("Service Ingest 仅在 Service 模式可用");
                    break;
                }
                if (await ServiceIngestScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenServiceQuery:
                if (!service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("Service Query 仅在 Service 模式可用");
                    break;
                }
                if (await ServiceQueryScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenServicePackage:
                if (!service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("Service Package 仅在 Service 模式可用");
                    break;
                }
                if (await ServicePackageScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenServiceJobs:
                if (!service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("Service Jobs 仅在 Service 模式可用");
                    break;
                }
                if (await ServiceJobsScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenServiceModel:
                if (!service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("Service Model 仅在 Service 模式可用");
                    break;
                }
                if (await ServiceModelScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenServiceAdminRuntime:
                if (!service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("Service Admin/Runtime 仅在 Service 模式可用");
                    break;
                }
                if (await ServiceAdminRuntimeScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenServiceMemory:
                if (!service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("Service Memory 仅在 Service 模式可用");
                    break;
                }
                if (await ServiceMemoryScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenServiceConstraints:
                if (!service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("Service Constraints 仅在 Service 模式可用");
                    break;
                }
                if (await ServiceConstraintsScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenServiceRelations:
                if (!service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("Service Relations 仅在 Service 模式可用");
                    break;
                }
                if (await ServiceRelationsScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenServiceShortTermMemory:
                if (!service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("Service Short-Term Memory 仅在 Service 模式可用");
                    break;
                }
                if (await ServiceShortTermMemoryScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenServicePromotionCandidates:
                if (!service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("Service Promotion Candidates 仅在 Service 模式可用");
                    break;
                }
                if (await ServicePromotionCandidatesScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenServiceStableReviewCandidates:
                if (!service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("Service Stable Review Candidates 仅在 Service 模式可用");
                    break;
                }
                if (await ServiceStableReviewCandidatesScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenServiceLearning:
                if (!service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("Service Learning 仅在 Service 模式可用");
                    break;
                }
                if (await ServiceLearningScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenServicePolicyFeedback:
                if (!service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("Service Policy Feedback 仅在 Service 模式可用");
                    break;
                }
                if (await ServicePolicyFeedbackScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenServiceLearningFeatures:
                if (!service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("Service Learning Features 仅在 Service 模式可用");
                    break;
                }
                if (await ServiceLearningFeaturesScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenServicePlanningSnapshot:
                if (!service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("Service Planning Snapshot 仅在 Service 模式可用");
                    break;
                }
                if (await ServicePlanningSnapshotScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenServicePlanningProposal:
                if (!service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("Service Planning Proposal 仅在 Service 模式可用");
                    break;
                }
                if (await ServicePlanningProposalScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenServiceConstraintGaps:
                if (!service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("Service Constraint Gaps 仅在 Service 模式可用");
                    break;
                }
                if (await ServiceConstraintGapsScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenServiceCandidateConstraints:
                if (!service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("Service Candidate Constraints 仅在 Service 模式可用");
                    break;
                }
                if (await ServiceCandidateConstraintsScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenServiceRankerShadowDebug:
                if (!service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("Service Ranker Shadow Debug 仅在 Service 模式可用");
                    break;
                }
                if (await ServiceRankerShadowDebugScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenServiceCandidateMemory:
                if (!service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("Service Candidate Memory 仅在 Service 模式可用");
                    break;
                }
                if (await ServiceCandidateMemoryScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenServiceStableMemory:
                if (!service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("Service Stable Memory 仅在 Service 模式可用");
                    break;
                }
                if (await ServiceStableMemoryScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenServicePolicies:
                if (!service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("Service Policy 仅在 Service 模式可用");
                    break;
                }
                if (await ServicePolicyScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;

            case ControlRoomActionKind.OpenModelStatus:
                if (service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("模型状态（当前 minimal service mode 仅提供 runtime 观测）");
                    break;
                }
                await ModelCommand.ExecuteAsync(service, ["status"], cancellationToken).ConfigureAwait(false);
                if (WaitForBackOrQuit() == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenReports:
                if (service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("报告导出");
                    break;
                }
                if (await ReportScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenPolicies:
                if (service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("策略管理");
                    break;
                }
                if (await PolicyScreen.ShowAsync(service, cancellationToken).ConfigureAwait(false) == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.OpenEvalReport:
                if (service.State.IsServiceMode)
                {
                    ShowServiceModeUnsupported("评测报告");
                    break;
                }
                await EvalCommand.ExecuteAsync(service, ["report"], cancellationToken).ConfigureAwait(false);
                if (WaitForBackOrQuit() == ControlRoomActionKind.Quit)
                {
                    return;
                }
                break;
            case ControlRoomActionKind.Quit:
                return;
            case ControlRoomActionKind.Unknown:
            default:
                break;
        }
    }
}

static ControlRoomState CreateState(Cli parsed, WorkspaceSelection selection)
{
    return ControlRoomService.CreateState(
        parsed.Storage,
        parsed.RootPath,
        selection.WorkspaceId,
        selection.CollectionId,
        parsed.Mode,
        parsed.ServiceBaseUrl);
}

static WorkspaceSelection ResolveWorkspaceSelection(Cli parsed)
{
    if (parsed.IsServiceMode)
    {
        return new WorkspaceSelection(parsed.WorkspaceId, parsed.CollectionId);
    }

    var discovery = ControlRoomService.DiscoverWorkspaces(parsed.RootPath);
    var workspaceId = parsed.WorkspaceSpecified
        ? parsed.WorkspaceId
        : discovery.Workspaces.Count == 1
            ? discovery.Workspaces[0].WorkspaceId
            : parsed.WorkspaceId;

    var workspace = discovery.Workspaces.FirstOrDefault(item =>
        string.Equals(item.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase));
    var collectionId = parsed.CollectionSpecified
        ? parsed.CollectionId
        : workspace?.CollectionIds.Count == 1
            ? workspace.CollectionIds[0]
            : parsed.CollectionId;

    return new WorkspaceSelection(workspaceId, collectionId);
}

static WorkspaceSelection SelectWorkspace(Cli parsed, WorkspaceSelection current)
{
    if (parsed.IsServiceMode)
    {
        return current;
    }

    var discovery = ControlRoomService.DiscoverWorkspaces(parsed.RootPath);
    if (discovery.Workspaces.Count == 0)
    {
        Console.WriteLine("当前根目录下没有工作区数据。");
        WaitForBackOrQuit();
        return current;
    }

    Console.WriteLine("工作区");
    for (var i = 0; i < discovery.Workspaces.Count; i++)
    {
        Console.WriteLine($"{i + 1}. {discovery.Workspaces[i].WorkspaceId}");
    }

    Console.Write("选择工作区：");
    var input = Console.ReadLine();
    if (!int.TryParse(input, out var index) || index < 1 || index > discovery.Workspaces.Count)
    {
        return current;
    }

    var workspace = discovery.Workspaces[index - 1];
    var collectionId = workspace.CollectionIds.Count == 1 ? workspace.CollectionIds[0] : current.CollectionId;
    return new WorkspaceSelection(workspace.WorkspaceId, collectionId);
}

static WorkspaceSelection SelectCollection(Cli parsed, WorkspaceSelection current)
{
    if (parsed.IsServiceMode)
    {
        return current;
    }

    var discovery = ControlRoomService.DiscoverWorkspaces(parsed.RootPath);
    var workspace = discovery.Workspaces.FirstOrDefault(item =>
        string.Equals(item.WorkspaceId, current.WorkspaceId, StringComparison.OrdinalIgnoreCase));
    if (workspace is null || workspace.CollectionIds.Count == 0)
    {
        Console.WriteLine("当前工作区没有集合数据。");
        WaitForBackOrQuit();
        return current;
    }

    Console.WriteLine("集合");
    for (var i = 0; i < workspace.CollectionIds.Count; i++)
    {
        Console.WriteLine($"{i + 1}. {workspace.CollectionIds[i]}");
    }

    Console.Write("选择集合：");
    var input = Console.ReadLine();
    if (!int.TryParse(input, out var index) || index < 1 || index > workspace.CollectionIds.Count)
    {
        return current;
    }

    return new WorkspaceSelection(current.WorkspaceId, workspace.CollectionIds[index - 1]);
}

static async Task<string> ReadDashboardInputAsync(
    bool autoRefresh,
    int refreshSeconds,
    CancellationToken cancellationToken)
{
    if (!autoRefresh || Console.IsInputRedirected)
    {
        return Console.ReadLine() ?? string.Empty;
    }

    var deadline = DateTimeOffset.UtcNow.AddSeconds(refreshSeconds);
    while (DateTimeOffset.UtcNow < deadline)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Console.KeyAvailable)
        {
            var key = Console.ReadKey(intercept: true);
            Console.WriteLine(key.KeyChar);
            return key.KeyChar.ToString();
        }

        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
    }

    Console.WriteLine();
    return string.Empty;
}

static ControlRoomActionKind WaitForBackOrQuit()
{
    Console.WriteLine();
    Console.Write("输入 B/0 返回，输入 Q 退出：");
    var action = ControlRoomInteraction.InterpretDetailInput(Console.ReadLine());
    return action.Kind == ControlRoomActionKind.Quit ? ControlRoomActionKind.Quit : ControlRoomActionKind.Back;
}

static void ShowServiceModeUnsupported(string feature)
{
    Console.WriteLine($"Service 模式暂不支持：{feature}");
    WaitForBackOrQuit();
}

static bool IsSupportedServiceModeCommand(string command)
{
    return command.Equals("status", StringComparison.OrdinalIgnoreCase)
        || command.Equals("help", StringComparison.OrdinalIgnoreCase)
        || command.Equals("--help", StringComparison.OrdinalIgnoreCase)
        || command.Equals("-h", StringComparison.OrdinalIgnoreCase);
}

static void PrintHelp()
{
    Console.WriteLine("""
    ContextCore.ControlRoom

    用法：
      context room --workspace default --collection test
      context room --workspace default --collection test status
      context room --workspace default --collection test list raw
      context room --workspace default --collection test show <id>
      context room --workspace default --collection test package-preview --token-budget 1200
      context room --workspace default --collection test retrieval debug --query "上下文记忆"
      context room --workspace default --collection test jobs --state failed
      context room --workspace default --collection test model status
      context room --workspace default --collection test report export --out report.md
      context room --workspace default --collection test policy list
      context room --workspace default --collection test policy edit default-context --token-budget 1600
      context room --workspace default --collection test package-preview --policy default-context
      context room eval run --out eval-report.md
      context room eval run --include-batches --out eval-report-all.md
      context room eval report eval-report.json

    选项：
      --workspace <id>       工作区 id。默认：default
      --collection <id>      集合 id。默认：test
      --root <path>          FileSystem 根目录。默认：项目根目录下的 ./context-core-data
      --refresh <seconds>    自动刷新间隔。默认：2
      --storage <kind>       filesystem 或 memory。默认：filesystem
      --service <baseUrl>    启用 Service 模式并连接到指定 ContextCore.Service
    """);
}

/// <summary>ControlRoom 命令行参数解析结果。</summary>
internal sealed class Cli
{
    public ControlRoomMode Mode { get; init; } = ControlRoomMode.Direct;

    public string WorkspaceId { get; init; } = "default";

    public string CollectionId { get; init; } = "test";

    /// <summary>
    /// 存储根目录。默认为 <see cref="FileStorageOptions.DefaultRootPath"/>
    /// （仓库根目录下的 <c>context-core-data</c> 专用目录）。
    /// 可通过 <c>--root &lt;path&gt;</c> CLI 参数覆盖。
    /// </summary>
    public string RootPath { get; init; } = FileStorageOptions.DefaultRootPath;

    public string Storage { get; init; } = "filesystem";

    public int RefreshSeconds { get; init; } = 2;

    public string? ServiceBaseUrl { get; init; }

    public bool WorkspaceSpecified { get; init; }

    public bool CollectionSpecified { get; init; }

    public string? Command { get; init; }

    public IReadOnlyList<string> CommandArgs { get; init; } = Array.Empty<string>();

    public bool IsServiceMode => Mode == ControlRoomMode.Service;

    public static Cli Parse(string[] args, ControlRoomDefaults defaults)
    {
        var tokens = args.ToList();
        if (tokens.Count > 0 && string.Equals(tokens[0], "room", StringComparison.OrdinalIgnoreCase))
        {
            tokens.RemoveAt(0);
        }

        var serviceBaseUrl = ReadOption(tokens, "--service") ?? defaults.ServiceBaseUrl;
        var mode = serviceBaseUrl is not null
            ? ControlRoomMode.Service
            : string.Equals(defaults.Mode, "service", StringComparison.OrdinalIgnoreCase)
                ? ControlRoomMode.Service
                : ControlRoomMode.Direct;
        var workspaceText = ReadOption(tokens, "--workspace");
        var collectionText = ReadOption(tokens, "--collection");
        var workspaceId = workspaceText ?? defaults.WorkspaceId ?? "default";
        var collectionId = collectionText ?? defaults.CollectionId ?? "test";
        var rootPath = FileStorageOptions.ResolveRootPath(ReadOption(tokens, "--root") ?? defaults.RootPath);
        var storage = ReadOption(tokens, "--storage") ?? defaults.Storage ?? "filesystem";
        var refreshSeconds = int.TryParse(ReadOption(tokens, "--refresh"), out var parsedRefresh)
            ? parsedRefresh
            : defaults.RefreshSeconds > 0 ? defaults.RefreshSeconds : 2;

        string? command = null;
        var commandArgs = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (IsGlobalOption(token))
            {
                i++;
                continue;
            }

            command = token;
            commandArgs.AddRange(tokens.Skip(i + 1));
            break;
        }

        return new Cli
        {
            Mode = mode,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            RootPath = rootPath,
            Storage = storage,
            RefreshSeconds = refreshSeconds,
            ServiceBaseUrl = serviceBaseUrl,
            WorkspaceSpecified = workspaceText is not null,
            CollectionSpecified = collectionText is not null,
            Command = command,
            CommandArgs = commandArgs
        };
    }

    private static string? ReadOption(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool IsGlobalOption(string token)
    {
        return token is "--workspace" or "--collection" or "--root" or "--storage" or "--refresh" or "--service";
    }
}

internal sealed record WorkspaceSelection(string WorkspaceId, string CollectionId);

internal sealed class ControlRoomDefaults
{
    public string? Mode { get; init; }

    public string? ServiceBaseUrl { get; init; }

    public string? WorkspaceId { get; init; }

    public string? CollectionId { get; init; }

    public string? RootPath { get; init; }

    public string? Storage { get; init; }

    public int RefreshSeconds { get; init; }

    public static ControlRoomDefaults Load()
    {
        var result = new MutableDefaults();
        ApplyJson(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), result);

        var currentDirectorySettings = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
        if (!string.Equals(currentDirectorySettings, Path.Combine(AppContext.BaseDirectory, "appsettings.json"), StringComparison.OrdinalIgnoreCase))
        {
            ApplyJson(currentDirectorySettings, result);
        }

        return result.ToImmutable();
    }

    private static void ApplyJson(string path, MutableDefaults defaults)
    {
        if (!File.Exists(path))
        {
            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("ControlRoom", out var controlRoom)
            || controlRoom.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        defaults.Mode = ReadString(controlRoom, "Mode") ?? defaults.Mode;
        defaults.ServiceBaseUrl = ReadString(controlRoom, "ServiceBaseUrl") ?? defaults.ServiceBaseUrl;
        defaults.WorkspaceId = ReadString(controlRoom, "WorkspaceId") ?? defaults.WorkspaceId;
        defaults.CollectionId = ReadString(controlRoom, "CollectionId") ?? defaults.CollectionId;
        defaults.RootPath = ReadString(controlRoom, "RootPath") ?? defaults.RootPath;
        defaults.Storage = ReadString(controlRoom, "Storage") ?? defaults.Storage;

        if (defaults.RefreshSeconds <= 0
            && controlRoom.TryGetProperty("RefreshSeconds", out var refresh)
            && refresh.ValueKind == JsonValueKind.Number
            && refresh.TryGetInt32(out var refreshSeconds))
        {
            defaults.RefreshSeconds = refreshSeconds;
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private sealed class MutableDefaults
    {
        public string? Mode { get; set; }
        public string? ServiceBaseUrl { get; set; }
        public string? WorkspaceId { get; set; }
        public string? CollectionId { get; set; }
        public string? RootPath { get; set; }
        public string? Storage { get; set; }
        public int RefreshSeconds { get; set; }

        public ControlRoomDefaults ToImmutable()
        {
            return new ControlRoomDefaults
            {
                Mode = Mode,
                ServiceBaseUrl = ServiceBaseUrl,
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                RootPath = RootPath,
                Storage = Storage,
                RefreshSeconds = RefreshSeconds
            };
        }
    }
}




