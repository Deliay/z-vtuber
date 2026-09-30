using System.Text.Json;
using Z.Vtuber.TestReport;

namespace Z.Vtuber.Tests;

/// <summary>
/// 报告工具（tests/Z.Vtuber.TestReport）的行为测试：trx 解析、耗时排行、5 秒超时护栏与例外清单比对。
/// 以内嵌 trx XML 样本驱动，不依赖真实跑测。
/// </summary>
public sealed class TestReportTests
{
    private const string FastTest = "Z.Vtuber.Tests.FastTests.Quick";
    private const string SlowTest = "Z.Vtuber.Tests.LockTests.LeaseExpires";

    private const string SampleTrx = """
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun id="0" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult executionId="e1" testId="a" testName="Z.Vtuber.Tests.FastTests.Quick" duration="00:00:00.1000000" outcome="Passed" />
            <UnitTestResult executionId="e2" testId="b" testName="Z.Vtuber.Tests.LockTests.LeaseExpires" duration="00:00:06.5000000" outcome="Passed" />
            <UnitTestResult executionId="e3" testId="c" testName="Z.Vtuber.Tests.FastTests.Flaky(value: 1)" duration="00:00:00.2000000" outcome="Failed" />
          </Results>
          <TestDefinitions>
            <UnitTest name="Z.Vtuber.Tests.FastTests.Quick" storage="t.dll" id="a">
              <TestMethod className="Z.Vtuber.Tests.FastTests" name="Quick" />
            </UnitTest>
            <UnitTest name="Z.Vtuber.Tests.LockTests.LeaseExpires" storage="t.dll" id="b">
              <TestMethod className="Z.Vtuber.Tests.LockTests" name="LeaseExpires" />
            </UnitTest>
            <UnitTest name="Z.Vtuber.Tests.FastTests.Flaky(value: 1)" storage="t.dll" id="c">
              <TestMethod className="Z.Vtuber.Tests.FastTests" name="Flaky" />
            </UnitTest>
          </TestDefinitions>
        </TestRun>
        """;

    private const string SecondTrx = """
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun id="0" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult executionId="e4" testId="d" testName="Z.Vtuber.Tests.OtherTests.Another" duration="00:00:00.3000000" outcome="Passed" />
          </Results>
          <TestDefinitions>
            <UnitTest name="Z.Vtuber.Tests.OtherTests.Another" storage="t.dll" id="d">
              <TestMethod className="Z.Vtuber.Tests.OtherTests" name="Another" />
            </UnitTest>
          </TestDefinitions>
        </TestRun>
        """;

    private static string WriteTempFile(string content, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"z-vtuber-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, content);
        return path;
    }

    // ---- TrxParser ----

    [Fact]
    public void Parse_ExtractsDurationOutcomeAndFullName()
    {
        var path = WriteTempFile(SampleTrx, ".trx");

        var report = TrxParser.Parse([path]);

        Assert.Equal(3, report.Cases.Count);
        var quick = report.Cases.Single(c => c.FullName == FastTest);
        Assert.Equal("Z.Vtuber.Tests.FastTests", quick.ClassName);
        Assert.Equal("Quick", quick.MethodName);
        Assert.Equal(TimeSpan.FromMilliseconds(100), quick.Duration);
        Assert.Equal("Passed", quick.Outcome);

        var slow = report.Cases.Single(c => c.FullName == SlowTest);
        Assert.Equal(TimeSpan.FromMilliseconds(6500), slow.Duration);
    }

    [Fact]
    public void Parse_MergesMultipleTrxFiles()
    {
        var first = WriteTempFile(SampleTrx, ".trx");
        var second = WriteTempFile(SecondTrx, ".trx");

        var report = TrxParser.Parse([first, second]);

        Assert.Equal(4, report.Cases.Count);
        Assert.Contains(report.Cases, c => c.FullName == "Z.Vtuber.Tests.OtherTests.Another");
    }

    [Fact]
    public void Parse_ComputesSummaryTotals()
    {
        var path = WriteTempFile(SampleTrx, ".trx");

        var report = TrxParser.Parse([path]);

        Assert.Equal(3, report.TotalCases);
        Assert.Equal(1, report.Failed);
        Assert.Equal(TimeSpan.FromMilliseconds(6800), report.TotalDuration);
    }

    [Fact]
    public void Parse_ThrowsWhenFileMissing()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.trx");

        Assert.Throws<FileNotFoundException>(() => TrxParser.Parse([missing]));
    }

    [Fact]
    public void Parse_ThrowsWhenNoResults()
    {
        var path = WriteTempFile(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results />
            </TestRun>
            """,
            ".trx");

        var ex = Assert.Throws<InvalidOperationException>(() => TrxParser.Parse([path]));
        Assert.Contains("no test cases", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- TimeoutExceptionList ----

    [Fact]
    public void ExceptionList_ParsesEntriesAndMatchesExactly()
    {
        var path = WriteTempFile(
            """
            [
              { "test": "Z.Vtuber.Tests.LockTests.LeaseExpires", "reason": "验证锁租约过期需真实时间流逝", "since": "2026-09-30" }
            ]
            """,
            ".json");

        var list = TimeoutExceptionList.Load(path);

        var entry = Assert.Single(list.Entries);
        Assert.Equal("验证锁租约过期需真实时间流逝", entry.Reason);
        Assert.Equal("2026-09-30", entry.Since);
        Assert.True(list.Contains(SlowTest));
        Assert.False(list.Contains(FastTest));
        Assert.False(list.Contains("Z.Vtuber.Tests.LockTests.LeaseExpires "));
    }

    [Fact]
    public void ExceptionList_EmptyJsonMeansNoExceptions()
    {
        var path = WriteTempFile("[]", ".json");

        var list = TimeoutExceptionList.Load(path);

        Assert.Empty(list.Entries);
    }

    // ---- TimeoutEvaluator + render ----

    [Fact]
    public void Evaluate_FlagsUntrackedOverThresholdCases()
    {
        var report = TrxParser.Parse([WriteTempFile(SampleTrx, ".trx")]);
        var exceptions = TimeoutExceptionList.Load(WriteTempFile("[]", ".json"));

        var result = TimeoutEvaluator.Evaluate(report, exceptions, TimeSpan.FromSeconds(5));

        var violation = Assert.Single(result.Violations);
        Assert.Equal(SlowTest, violation.FullName);
        Assert.Empty(result.Excepted);
    }

    [Fact]
    public void Evaluate_ExceptedCaseIsNotAViolation()
    {
        var report = TrxParser.Parse([WriteTempFile(SampleTrx, ".trx")]);
        var exceptions = TimeoutExceptionList.Load(WriteTempFile(
            """
            [ { "test": "Z.Vtuber.Tests.LockTests.LeaseExpires", "reason": "锁租约", "since": "2026-09-30" } ]
            """,
            ".json"));

        var result = TimeoutEvaluator.Evaluate(report, exceptions, TimeSpan.FromSeconds(5));

        Assert.Empty(result.Violations);
        var excepted = Assert.Single(result.Excepted);
        Assert.Equal(SlowTest, excepted.FullName);
    }

    [Fact]
    public void Evaluate_UnderThresholdCaseIsNeitherViolationNorExcepted()
    {
        var report = TrxParser.Parse([WriteTempFile(SecondTrx, ".trx")]);

        var result = TimeoutEvaluator.Evaluate(report, TimeoutExceptionList.Empty, TimeSpan.FromSeconds(5));

        Assert.Empty(result.Violations);
        Assert.Empty(result.Excepted);
    }

    [Fact]
    public void Render_ListsSlowestFirstAndMarksTimeouts()
    {
        var report = TrxParser.Parse([WriteTempFile(SampleTrx, ".trx")]);

        var text = ReportWriter.Render(report, top: 10, threshold: TimeSpan.FromSeconds(5));

        var slowIndex = text.IndexOf(SlowTest, StringComparison.Ordinal);
        var quickIndex = text.IndexOf(FastTest, StringComparison.Ordinal);
        Assert.True(slowIndex >= 0, "慢用例应出现在排行中");
        Assert.True(quickIndex > slowIndex, "排行应按耗时降序，慢用例在前");
        Assert.Contains(">5s", text);
        Assert.Contains("Total", text);
    }

    [Fact]
    public void Render_RespectsTopCount()
    {
        var report = TrxParser.Parse([WriteTempFile(SampleTrx, ".trx")]);

        var text = ReportWriter.Render(report, top: 1, threshold: TimeSpan.FromSeconds(5));

        Assert.Contains(SlowTest, text);
        Assert.DoesNotContain(FastTest, text);
    }

    // ---- Program / CLI ----

    [Fact]
    public void Run_CheckMode_ReturnsNonZeroWhenViolationExists()
    {
        var trx = WriteTempFile(SampleTrx, ".trx");
        var exceptions = WriteTempFile("[]", ".json");
        var outWriter = new StringWriter();
        var errWriter = new StringWriter();

        var exitCode = TestReportCli.Run([trx, "--check", "--exceptions", exceptions], outWriter, errWriter);

        Assert.NotEqual(0, exitCode);
        Assert.Contains(SlowTest, outWriter.ToString() + errWriter.ToString());
    }

    [Fact]
    public void Run_CheckMode_ReturnsZeroWhenAllWithinThresholdOrExcepted()
    {
        var trx = WriteTempFile(SampleTrx, ".trx");
        var exceptions = WriteTempFile(
            """
            [ { "test": "Z.Vtuber.Tests.LockTests.LeaseExpires", "reason": "锁租约", "since": "2026-09-30" } ]
            """,
            ".json");
        var outWriter = new StringWriter();
        var errWriter = new StringWriter();

        var exitCode = TestReportCli.Run([trx, "--check", "--exceptions", exceptions], outWriter, errWriter);

        Assert.Equal(0, exitCode);
        Assert.Contains("exception", outWriter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_WithoutCheck_ReturnsZeroEvenWithSlowCase()
    {
        var trx = WriteTempFile(SampleTrx, ".trx");
        var outWriter = new StringWriter();
        var errWriter = new StringWriter();

        var exitCode = TestReportCli.Run([trx], outWriter, errWriter);

        Assert.Equal(0, exitCode);
        Assert.Contains(SlowTest, outWriter.ToString());
    }
}
