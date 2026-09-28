using System.Xml.Linq;

namespace Z.Vtuber.Tests;

/// <summary>
/// 边界守护测试：断言 8 个服务的 ProjectReference 闭包等于技术提案 §2.3 引用矩阵，
/// 且 7 个领域项目只引用 Core（无域间引用、无环）。
/// </summary>
public class ProjectReferenceGuardTests
{
    private static readonly Dictionary<string, string[]> ServiceMatrix = new()
    {
        ["api"] = ["core", "live", "cookie", "weibo", "recording"],
        ["crawler"] = ["core", "live", "weibo"],
        ["cookie"] = ["core", "cookie"],
        ["notifier"] = ["core", "notification"],
        ["recording"] = ["core", "live", "recording"],
        ["recording-transcript-api"] = ["core", "live", "recording", "transcript"],
        ["recording-transcript-consumer"] = ["core", "live", "recording", "transcript"],
        ["training"] = ["core", "training", "transcript"],
    };

    private static readonly string[] DomainDirs =
        ["core", "live", "cookie", "weibo", "recording", "transcript", "training", "notification"];

    [Fact]
    public void ServiceProjectReferences_MatchMatrix()
    {
        var root = FindRepoRoot();
        foreach (var (service, expected) in ServiceMatrix)
        {
            var csproj = Directory.GetFiles(Path.Combine(root, "servers", service), "*.csproj").Single();
            var actual = CommonDomainReferences(csproj);
            Assert.True(
                expected.OrderBy(x => x).SequenceEqual(actual.OrderBy(x => x)),
                $"service '{service}' expected [{string.Join(",", expected)}] but was [{string.Join(",", actual)}]");
        }
    }

    [Fact]
    public void DomainProjects_OnlyReferenceCore()
    {
        var root = FindRepoRoot();
        foreach (var domain in DomainDirs)
        {
            var csproj = Directory.GetFiles(Path.Combine(root, "servers", "common", domain), "*.csproj").Single();
            var refs = ProjectReferences(csproj)
                .Select(p => NormalizeRelative(Path.GetDirectoryName(csproj)!, p))
                .ToList();

            if (domain == "core")
            {
                Assert.DoesNotContain(refs, r => r.Contains($"{Path.DirectorySeparatorChar}common{Path.DirectorySeparatorChar}"));
                continue;
            }

            var commonRefs = refs
                .Where(r => r.Contains($"{Path.DirectorySeparatorChar}common{Path.DirectorySeparatorChar}"))
                .ToList();
            Assert.Single(commonRefs);
            Assert.EndsWith($"{Path.DirectorySeparatorChar}common{Path.DirectorySeparatorChar}core{Path.DirectorySeparatorChar}Z.Vtuber.Common.Core.csproj", commonRefs[0]);
        }
    }

    private static List<string> CommonDomainReferences(string csproj)
    {
        var baseDir = Path.GetDirectoryName(csproj)!;
        return ProjectReferences(csproj)
            .Select(p => NormalizeRelative(baseDir, p))
            .Where(p => p.Contains($"{Path.DirectorySeparatorChar}servers{Path.DirectorySeparatorChar}common{Path.DirectorySeparatorChar}"))
            .Select(p => p.Split($"{Path.DirectorySeparatorChar}common{Path.DirectorySeparatorChar}")[1]
                .Split(Path.DirectorySeparatorChar)[0])
            .ToList();
    }

    private static IEnumerable<string> ProjectReferences(string csproj) =>
        XDocument.Load(csproj).Descendants("ProjectReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))!;

    private static string NormalizeRelative(string baseDir, string relative) =>
        Path.GetFullPath(Path.Combine(baseDir, relative.Replace('\\', Path.DirectorySeparatorChar)));

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "servers", "Z.Vtuber.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
