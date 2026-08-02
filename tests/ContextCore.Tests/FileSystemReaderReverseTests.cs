using System.Text;
using ContextCore.Storage.FileSystem;

namespace ContextCore.Tests;

/// <summary>
/// FileSystemReader.ReadLinesReverseAsync 正确性测试。
/// 验证从文件尾部反向 I/O 读取行：newest-first 顺序、空白行跳过、
/// \r\n 与 \n 混合行结束符、跨块边界拼接、UTF-8 多字节字符安全、maxCount 早停。
/// </summary>
[TestClass]
[TestCategory("FileSystem")]
public sealed class FileSystemReaderReverseTests
{
    private string? _rootPath;

    [TestInitialize]
    public void Initialize()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "cc-revreader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_rootPath is not null && Directory.Exists(_rootPath))
        {
            try { Directory.Delete(_rootPath, recursive: true); } catch { /* best-effort */ }
        }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_rootPath!, name);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static IReadOnlyList<string> ReadForward(FileSystemReader reader, string path)
        => reader.ReadAllLinesAsync(path).GetAwaiter().GetResult();

    // ── 基本正确性 ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task FileNotExist_ReturnsEmpty()
    {
        var reader = new FileSystemReader();
        var path = Path.Combine(_rootPath!, "missing.jsonl");

        var lines = await reader.ReadLinesReverseAsync(path, maxCount: 10);

        Assert.AreEqual(0, lines.Count);
    }

    [TestMethod]
    public async Task EmptyFile_ReturnsEmpty()
    {
        var reader = new FileSystemReader();
        var path = WriteFile("empty.jsonl", "");

        var lines = await reader.ReadLinesReverseAsync(path, maxCount: 10);

        Assert.AreEqual(0, lines.Count);
    }

    [TestMethod]
    public async Task MaxCountNonPositive_ReturnsEmpty()
    {
        var reader = new FileSystemReader();
        var path = WriteFile("some.jsonl", "line1\nline2\nline3\n");

        var zero = await reader.ReadLinesReverseAsync(path, maxCount: 0);
        var negative = await reader.ReadLinesReverseAsync(path, maxCount: -5);

        Assert.AreEqual(0, zero.Count);
        Assert.AreEqual(0, negative.Count);
    }

    [TestMethod]
    public async Task SingleLine_NoTrailingNewline_ReturnedAsFirst()
    {
        var reader = new FileSystemReader();
        var path = WriteFile("single.jsonl", "only-line");

        var lines = await reader.ReadLinesReverseAsync(path, maxCount: 10);

        CollectionAssert.AreEqual(new[] { "only-line" }, lines.ToList());
    }

    [TestMethod]
    public async Task ReturnsLines_NewestFirst()
    {
        var reader = new FileSystemReader();
        var path = WriteFile("ordered.jsonl", "line-1\nline-2\nline-3\n");

        var lines = await reader.ReadLinesReverseAsync(path, maxCount: 10);

        // 末尾行在前：line-3 → line-2 → line-1
        CollectionAssert.AreEqual(new[] { "line-3", "line-2", "line-1" }, lines.ToList());
    }

    [TestMethod]
    public async Task MaxCount_LimitsResult_AndStopsIo()
    {
        var reader = new FileSystemReader();
        var path = WriteFile("limit.jsonl", "a\nb\nc\nd\ne\n");

        var lines = await reader.ReadLinesReverseAsync(path, maxCount: 2);

        CollectionAssert.AreEqual(new[] { "e", "d" }, lines.ToList());
    }

    // ── 行结束符与空白行 ────────────────────────────────────────────────

    [TestMethod]
    public async Task CrLfLineEndings_StrippedCorrectly()
    {
        var reader = new FileSystemReader();
        var path = WriteFile("crlf.jsonl", "alpha\r\nbeta\r\ngamma\r\n");

        var lines = await reader.ReadLinesReverseAsync(path, maxCount: 10);

        CollectionAssert.AreEqual(new[] { "gamma", "beta", "alpha" }, lines.ToList());
    }

    [TestMethod]
    public async Task MixedCrLfAndLfLineEndings()
    {
        var reader = new FileSystemReader();
        var path = WriteFile("mixed.jsonl", "lf-line\n" + "crlf-line\r\n" + "tail-line\n");

        var lines = await reader.ReadLinesReverseAsync(path, maxCount: 10);

        CollectionAssert.AreEqual(new[] { "tail-line", "crlf-line", "lf-line" }, lines.ToList());
    }

    [TestMethod]
    public async Task TrailingNewline_EmptyTailLineSkipped()
    {
        var reader = new FileSystemReader();
        // 末尾 \n 之后的字节为空 → 空白行跳过，不产出空字符串
        var path = WriteFile("trailing.jsonl", "real-content\n");

        var lines = await reader.ReadLinesReverseAsync(path, maxCount: 10);

        CollectionAssert.AreEqual(new[] { "real-content" }, lines.ToList());
    }

    [TestMethod]
    public async Task BlankLinesInMiddle_SkippedAndNotCounted()
    {
        var reader = new FileSystemReader();
        var path = WriteFile("blanks.jsonl", "x\n\n\ny\n\nz\n");

        var lines = await reader.ReadLinesReverseAsync(path, maxCount: 10);

        CollectionAssert.AreEqual(new[] { "z", "y", "x" }, lines.ToList());
    }

    [TestMethod]
    public async Task NoTrailingNewline_LastLineReturned()
    {
        var reader = new FileSystemReader();
        // 文件不以 \n 结尾：最后字节是 "end"（无前导 \n）→ 由文件起始 tail 处理
        var path = WriteFile("notrail.jsonl", "first\nmiddle\nend");

        var lines = await reader.ReadLinesReverseAsync(path, maxCount: 10);

        CollectionAssert.AreEqual(new[] { "end", "middle", "first" }, lines.ToList());
    }

    // ── 跨块边界拼接 ────────────────────────────────────────────────────

    [TestMethod]
    public async Task FileSmallerThanChunk_ReadsAllLines()
    {
        var reader = new FileSystemReader();
        // 默认块大小 8KB，小于此的文件单块读完
        var sb = new StringBuilder();
        for (var i = 0; i < 100; i++)
        {
            sb.Append($"line-{i:D3}\n");
        }

        var path = WriteFile("small.jsonl", sb.ToString());

        var lines = await reader.ReadLinesReverseAsync(path, maxCount: 10);

        // 最新 10 行：line-099 → line-090
        var expected = Enumerable.Range(0, 10).Select(i => $"line-{99 - i:D3}").ToArray();
        CollectionAssert.AreEqual(expected, lines.ToList());
    }

    [TestMethod]
    public async Task FileLargerThanChunk_LinesSpanningBoundaryCorrect()
    {
        var reader = new FileSystemReader();
        // 块大小 8KB。构造每行约 100 字节、共 200 行（约 20KB，跨多个块）。
        // 第 50 行的内容刻意放在块边界附近，验证跨块拼接正确。
        var sb = new StringBuilder();
        for (var i = 0; i < 200; i++)
        {
            // 填充至约 100 字节：固定前缀 + 序号 + 一段可识别的尾标
            sb.Append($"row-{i:D4}-");
            sb.Append('x', 90);
            sb.Append('\n');
        }

        var path = WriteFile("large.jsonl", sb.ToString());

        var reverse = await reader.ReadLinesReverseAsync(path, maxCount: 200);
        var forward = ReadForward(reader, path);

        // 反向读取的非空白行数应与正向读取的非空白行数一致
        var forwardNonBlank = forward.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        Assert.AreEqual(forwardNonBlank.Length, reverse.Count,
            "反向读取应返回与正向读取相同的非空白行数");
        // 反向序列 = 正向序列的反转
        CollectionAssert.AreEqual(forwardNonBlank.Reverse().ToArray(), reverse.ToList());
    }

    [TestMethod]
    public async Task LineExceedingChunkSize_AssembledAcrossMultipleChunks()
    {
        var reader = new FileSystemReader();
        // 单行长度 > 8KB（块大小）。该行的头部在更早块、尾部在更晚块，需跨块拼接。
        var longPayload = new string('Q', 20_000);
        var content = "header-line\n" + longPayload + "\n" + "footer-line\n";

        var path = WriteFile("longline.jsonl", content);

        var lines = await reader.ReadLinesReverseAsync(path, maxCount: 10);

        CollectionAssert.AreEqual(new[] { "footer-line", longPayload, "header-line" }, lines.ToList());
    }

    // ── UTF-8 多字节字符跨块边界安全 ────────────────────────────────────

    [TestMethod]
    public async Task Utf8MultibyteAtChunkBoundary_NotCorrupted()
    {
        var reader = new FileSystemReader();
        // 构造内容：使多字节 UTF-8 字符恰好在 8KB 块边界附近被切分。
        // 中文字符 '中' 在 UTF-8 中占 3 字节。块大小 8192。
        // 在第 8190 字节处放一个 '中'，其 3 字节将跨越块边界（8190-8192）。
        var sb = new StringBuilder();
        // 先填充 ASCII 到接近块边界
        while (sb.Length < 8180)
        {
            sb.Append("ascii-fill-");
        }
        // 调整到精确位置
        while (sb.Length < 8189)
        {
            sb.Append('a');
        }
        // 此处插入多字节字符，确保其字节跨越 8192 边界
        sb.Append("边界");
        sb.Append('\n');
        sb.Append("last-line\n");

        var path = WriteFile("utf8.jsonl", sb.ToString());

        var lines = await reader.ReadLinesReverseAsync(path, maxCount: 10);

        // 最后一行
        Assert.AreEqual("last-line", lines[0]);
        // 跨边界的行：必须正确解码 "边界" 而非乱码
        Assert.IsTrue(lines[1].EndsWith("边界", StringComparison.Ordinal),
            $"UTF-8 多字节字符在块边界必须正确拼接，实际末尾: ...{lines[1][^10..]}");
    }

    // ── 等价性：反向读取与正向读取（反转后）一致 ──────────────────────

    [TestMethod]
    public async Task ReverseRead_EquivalentToForwardReadReversed()
    {
        var reader = new FileSystemReader();
        var sb = new StringBuilder();
        var rnd = new Random(42);
        for (var i = 0; i < 50; i++)
        {
            sb.Append($"entry-{i:D3}-");
            // 随机长度填充，模拟真实 JSONL 行
            sb.Append('v', rnd.Next(10, 80));
            sb.Append('\n');
        }

        var path = WriteFile("equiv.jsonl", sb.ToString());

        var reverse = await reader.ReadLinesReverseAsync(path, maxCount: 50);
        var forward = ReadForward(reader, path);
        var forwardNonBlank = forward.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();

        CollectionAssert.AreEqual(forwardNonBlank.Reverse().ToArray(), reverse.ToList(),
            "反向读取应与正向读取反转后的结果完全一致");
    }
}
