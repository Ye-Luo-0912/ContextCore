using ContextCore.Inference.Onnx;
using ContextCore.Service.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace ContextCore.Tests;

/// <summary>
/// ModelArtifactOptionsReader 配置解析测试：tensor 名与 Execution Provider / GPU 设备
/// 从 IConfiguration 正确解析，非法/缺失值回退默认（fail-safe）。
/// </summary>
[TestClass]
[TestCategory("Model-Control-Plane")]
public sealed class ModelArtifactOptionsReaderTests
{
    [TestMethod]
    public void ResolveInputTensorName_Configured_ReturnsValue()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["ModelArtifact:DefaultInputTensorName"] = "input_ids"
        });

        Assert.AreEqual("input_ids", ModelArtifactOptionsReader.ResolveInputTensorName(config));
    }

    [TestMethod]
    public void ResolveInputTensorName_Missing_FallsBackToInput()
    {
        var config = BuildConfig(new Dictionary<string, string?>());

        Assert.AreEqual("input", ModelArtifactOptionsReader.ResolveInputTensorName(config));
    }

    [TestMethod]
    public void ResolveScoreOutputName_Missing_FallsBackToScore()
    {
        var config = BuildConfig(new Dictionary<string, string?>());

        Assert.AreEqual("score", ModelArtifactOptionsReader.ResolveScoreOutputName(config));
    }

    [TestMethod]
    public void ResolveExecutionProvider_Cuda_ReturnsCuda()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["ModelArtifact:ExecutionProvider"] = "CUDA"
        });

        Assert.AreEqual(OnnxExecutionProvider.CUDA, ModelArtifactOptionsReader.ResolveExecutionProvider(config));
    }

    [TestMethod]
    public void ResolveExecutionProvider_InvalidValue_FallsBackToCpu()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["ModelArtifact:ExecutionProvider"] = "not-a-provider"
        });

        Assert.AreEqual(OnnxExecutionProvider.CPU, ModelArtifactOptionsReader.ResolveExecutionProvider(config),
            "未知配置值必须回退 CPU（fail-safe：错误配置不得导致激活失败）。");
    }

    [TestMethod]
    public void ResolveExecutionProvider_Missing_FallsBackToCpu()
    {
        var config = BuildConfig(new Dictionary<string, string?>());

        Assert.AreEqual(OnnxExecutionProvider.CPU, ModelArtifactOptionsReader.ResolveExecutionProvider(config));
    }

    [TestMethod]
    public void ResolveExecutionProviderDeviceId_Configured_ReturnsValue()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["ModelArtifact:ExecutionProviderDeviceId"] = "2"
        });

        Assert.AreEqual(2, ModelArtifactOptionsReader.ResolveExecutionProviderDeviceId(config));
    }

    [TestMethod]
    public void ResolveExecutionProviderDeviceId_Invalid_FallsBackToZero()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["ModelArtifact:ExecutionProviderDeviceId"] = "-1"
        });

        Assert.AreEqual(0, ModelArtifactOptionsReader.ResolveExecutionProviderDeviceId(config));
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
