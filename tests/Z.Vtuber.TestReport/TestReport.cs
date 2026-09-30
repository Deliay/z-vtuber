namespace Z.Vtuber.TestReport;

/// <summary>单个测试用例的耗时记录。</summary>
/// <param name="FullName">用例全名（<c>类全名.方法名</c>）。</param>
/// <param name="ClassName">测试类全名。</param>
/// <param name="MethodName">测试方法名（Theory 不含参数）。</param>
/// <param name="Duration">执行耗时。</param>
/// <param name="Outcome">结果（Passed/Failed/Skipped...）。</param>
public sealed record TestCaseDuration(
    string FullName,
    string ClassName,
    string MethodName,
    TimeSpan Duration,
    string Outcome);

/// <summary>一次或多次 trx 解析后的测试耗时汇总。</summary>
public sealed class TestReport
{
    public TestReport(IReadOnlyList<TestCaseDuration> cases)
    {
        Cases = cases;
        TotalCases = cases.Count;
        Failed = cases.Count(static c => string.Equals(c.Outcome, "Failed", StringComparison.OrdinalIgnoreCase));
        TotalDuration = cases.Aggregate(TimeSpan.Zero, static (acc, c) => acc + c.Duration);
    }

    public IReadOnlyList<TestCaseDuration> Cases { get; }

    public int TotalCases { get; }

    public int Failed { get; }

    public TimeSpan TotalDuration { get; }
}
