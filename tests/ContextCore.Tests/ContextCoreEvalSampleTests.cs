using System.Text.Json;
using ContextCore.Abstractions.Models;

namespace ContextCore.Tests;

/// <summary>覆盖 A3 上下文评测样本结构。</summary>
[TestClass]
public sealed class ContextCoreEvalSampleTests
{
    [TestMethod]
    public void ContextEvalSample_ShouldSerializeAndDeserialize()
    {
        var sample = new ContextEvalSample
        {
            Id = "coding-001",
            Query = "继续实现 Promotion Eval，并验证测试结果。",
            Mode = "CodingMode",
            MustHit = ["task:promotion-eval"],
            MustNotHit = ["noise:old-branch"],
            ExpectedScopes = ["collection", "task"],
            ExpectedEntities = ["ContextCore", "PromotionEvalRunner"],
            ExpectedConstraints = ["输出、日志、注释和提示信息保持中文"],
            ExpectedUncertainties = ["真实语料覆盖不足"],
            GoldenNotes = "应召回当前 A2/A3 任务，不应召回旧分支。",
            Metadata = new Dictionary<string, string>
            {
                ["category"] = "coding-mode"
            }
        };

        var json = JsonSerializer.Serialize(sample);
        var actual = JsonSerializer.Deserialize<ContextEvalSample>(json);

        Assert.IsNotNull(actual);
        Assert.AreEqual("CodingMode", actual!.Mode);
        CollectionAssert.Contains(actual.MustHit.ToArray(), "task:promotion-eval");
        CollectionAssert.Contains(actual.ExpectedEntities.ToArray(), "PromotionEvalRunner");
        Assert.AreEqual("coding-mode", actual.Metadata["category"]);
    }
}
