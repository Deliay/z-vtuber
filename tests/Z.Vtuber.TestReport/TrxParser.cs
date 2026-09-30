using System.Globalization;
using System.Xml.Linq;

namespace Z.Vtuber.TestReport;

/// <summary>
/// 解析 VSTest trx 报告，提取逐用例耗时。仅使用 BCL（<see cref="System.Xml.Linq"/>），零新增依赖。
/// </summary>
public static class TrxParser
{
    public static TestReport Parse(IEnumerable<string> trxPaths)
    {
        var paths = trxPaths.ToList();
        if (paths.Count == 0)
        {
            throw new InvalidOperationException("no trx files were provided");
        }

        var cases = new List<TestCaseDuration>();
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"trx file not found: {path}", path);
            }

            var document = XDocument.Load(path);
            cases.AddRange(ParseDocument(document));
        }

        if (cases.Count == 0)
        {
            throw new InvalidOperationException("the provided trx file(s) contain no test cases");
        }

        return new TestReport(cases);
    }

    private static IEnumerable<TestCaseDuration> ParseDocument(XDocument document)
    {
        var root = document.Root;
        if (root is null)
        {
            yield break;
        }

        var definitions = new Dictionary<string, (string ClassName, string MethodName)>(StringComparer.Ordinal);
        foreach (var unitTest in root.Descendants().Where(static e => e.Name.LocalName == "UnitTest"))
        {
            var id = (string?)unitTest.Attribute("id");
            var method = unitTest.Elements().FirstOrDefault(static e => e.Name.LocalName == "TestMethod");
            if (id is null || method is null)
            {
                continue;
            }

            definitions[id] = ((string?)method.Attribute("className") ?? string.Empty, (string?)method.Attribute("name") ?? string.Empty);
        }

        foreach (var result in root.Descendants().Where(static e => e.Name.LocalName == "UnitTestResult"))
        {
            var testName = (string?)result.Attribute("testName") ?? string.Empty;
            var testId = (string?)result.Attribute("testId");
            var (className, methodName) = ResolveName(testId, testName, definitions);
            var fullName = className.Length > 0 && methodName.Length > 0 ? $"{className}.{methodName}" : testName;

            yield return new TestCaseDuration(
                fullName,
                className,
                methodName,
                ParseDuration((string?)result.Attribute("duration")),
                (string?)result.Attribute("outcome") ?? string.Empty);
        }
    }

    private static (string ClassName, string MethodName) ResolveName(
        string? testId,
        string testName,
        IReadOnlyDictionary<string, (string ClassName, string MethodName)> definitions)
    {
        if (testId is not null && definitions.TryGetValue(testId, out var definition))
        {
            return definition;
        }

        var separator = testName.LastIndexOf('.');
        return separator > 0
            ? (testName[..separator], testName[(separator + 1)..])
            : (string.Empty, testName);
    }

    private static TimeSpan ParseDuration(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var duration) ? duration : TimeSpan.Zero;
    }
}
