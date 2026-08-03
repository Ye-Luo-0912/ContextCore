using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;

namespace ContextCore.Tests;

/// <summary>
/// 验证 RealToolDispatcher.GetToolDefinitions 的冻结缓存语义：
/// 冻结后返回稳定快照（不重建列表），注册变更后缓存失效并反映新注册。
/// </summary>
[TestClass]
public class RealToolDispatcherTests
{
    [TestMethod]
    public void GetToolDefinitions_AfterFreeze_ReturnsSameSnapshotInstance()
    {
        var dispatcher = new RealToolDispatcher(
        [
            new FakeHandler("tool-a", "desc-a"),
            new FakeHandler("tool-b", "desc-b")
        ]);
        dispatcher.Freeze();

        var first = dispatcher.GetToolDefinitions();
        var second = dispatcher.GetToolDefinitions();

        Assert.AreSame(first, second, "冻结后应返回同一缓存实例，不重复构建列表");
        Assert.AreEqual(2, first.Count);
        Assert.IsTrue(first.Any(d => d.Name == "tool-a" && d.Description == "desc-a"));
        Assert.IsTrue(first.Any(d => d.Name == "tool-b" && d.Description == "desc-b"));
    }

    [TestMethod]
    public void GetToolDefinitions_ReflectsAddHandler_BeforeFreeze()
    {
        var dispatcher = new RealToolDispatcher([new FakeHandler("tool-a", "desc-a")]);

        Assert.AreEqual(1, dispatcher.GetToolDefinitions().Count);

        dispatcher.AddHandler(new FakeHandler("tool-c", "desc-c"));
        var definitions = dispatcher.GetToolDefinitions();

        Assert.AreEqual(2, definitions.Count, "注册变更后缓存应失效并反映新注册");
        Assert.IsTrue(definitions.Any(d => d.Name == "tool-c"));
    }

    private sealed class FakeHandler : IToolHandler
    {
        public FakeHandler(string toolName, string description)
        {
            ToolName = toolName;
            Description = description;
        }

        public string ToolName { get; }

        public string? Description { get; }

        public string? ParametersJsonSchema => """{"type":"object"}""";

        public ToolDescriptor Descriptor => new() { Name = ToolName };

        public ValueTask<ToolHandlerResult> HandleAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ToolHandlerResult { Succeeded = true, Result = "ok" });
    }
}
