using System.Globalization;

namespace Z.Vtuber.TestReport;

/// <summary>
/// 测试耗时报告工具：解析 trx、输出耗时排行，并在 <c>--check</c> 模式下拦截未登记的 &gt;5s 用例。
/// 用法：<c>dotnet run --project tests/Z.Vtuber.TestReport -- &lt;trx...&gt; [--check] [--exceptions &lt;path&gt;] [--top N]</c>
/// </summary>
public static class TestReportCli
{
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(5);
    private const int DefaultTop = 20;

    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            var trxPaths = new List<string>();
            var check = false;
            string? exceptionsPath = null;
            var top = DefaultTop;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--check":
                        check = true;
                        break;
                    case "--exceptions":
                        exceptionsPath = Next(args, ref i);
                        break;
                    case "--top":
                        top = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                        break;
                    default:
                        trxPaths.Add(args[i]);
                        break;
                }
            }

            if (trxPaths.Count == 0)
            {
                stderr.WriteLine("usage: Z.Vtuber.TestReport <trx...> [--check] [--exceptions <path>] [--top N]");
                return 2;
            }

            var report = TrxParser.Parse(trxPaths);
            stdout.Write(ReportWriter.Render(report, top, Threshold));

            if (!check)
            {
                return 0;
            }

            var exceptions = TimeoutExceptionList.Empty;
            if (exceptionsPath is not null)
            {
                if (!File.Exists(exceptionsPath))
                {
                    stderr.WriteLine($"error: exceptions file not found: {exceptionsPath}");
                    return 2;
                }

                exceptions = TimeoutExceptionList.Load(exceptionsPath);
            }

            var result = TimeoutEvaluator.Evaluate(report, exceptions, Threshold);

            if (result.Excepted.Count > 0)
            {
                stdout.WriteLine();
                stdout.WriteLine($"Registered timeout exceptions (>= {Threshold.TotalSeconds:F0}s, allowed): {result.Excepted.Count}");
                foreach (var test in result.Excepted)
                {
                    stdout.WriteLine($"  {test.FullName} ({test.Duration.TotalSeconds:F3}s)");
                }
            }

            if (result.Violations.Count > 0)
            {
                stderr.WriteLine($"FAIL: {result.Violations.Count} test case(s) exceeded {Threshold.TotalSeconds:F0}s and are not registered as exceptions:");
                foreach (var test in result.Violations)
                {
                    stderr.WriteLine($"  {test.FullName} ({test.Duration.TotalSeconds:F3}s)");
                }

                return 1;
            }

            stdout.WriteLine();
            stdout.WriteLine("OK: no untracked test case exceeded the timeout threshold.");
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static string Next(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"missing value after {args[index]}");
        }

        return args[++index];
    }
}
