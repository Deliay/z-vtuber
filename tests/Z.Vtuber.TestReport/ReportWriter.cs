using System.Globalization;
using System.Text;

namespace Z.Vtuber.TestReport;

/// <summary>超时评估结果：违规用例与已登记例外。</summary>
public sealed record TimeoutEvaluationResult(
    IReadOnlyList<TestCaseDuration> Violations,
    IReadOnlyList<TestCaseDuration> Excepted);

/// <summary>按 5 秒阈值判定超时用例，区分「未登记违规」与「已登记例外」。</summary>
public static class TimeoutEvaluator
{
    public static TimeoutEvaluationResult Evaluate(TestReport report, TimeoutExceptionList exceptions, TimeSpan threshold)
    {
        var overThreshold = report.Cases
            .Where(c => c.Duration > threshold)
            .OrderByDescending(static c => c.Duration)
            .ToList();

        var violations = overThreshold.Where(c => !exceptions.Contains(c.FullName)).ToList();
        var excepted = overThreshold.Where(c => exceptions.Contains(c.FullName)).ToList();
        return new TimeoutEvaluationResult(violations, excepted);
    }
}

/// <summary>把耗时报告渲染为人类可读文本。</summary>
public static class ReportWriter
{
    public static string Render(TestReport report, int top, TimeSpan threshold)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Test cases: {report.TotalCases}, Failed: {report.Failed}, Total: {report.TotalDuration.TotalSeconds:F2}s");
        builder.AppendLine($"Timeout threshold: {threshold.TotalSeconds:F0}s");
        builder.AppendLine();
        builder.AppendLine($"Top {top} slowest tests:");

        foreach (var test in report.Cases.OrderByDescending(static c => c.Duration).Take(top))
        {
            var marker = test.Duration > threshold ? "  >5s" : string.Empty;
            builder.AppendLine($"{test.Duration.TotalSeconds,9:F3}s  {test.FullName}{marker}");
        }

        return builder.ToString();
    }
}
