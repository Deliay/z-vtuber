using System.Text.RegularExpressions;

namespace Z.Vtuber.Tests;

/// <summary>
/// 流水线守护测试：断言 8 条镜像流水线的 paths 与该服务引用的领域项目目录一一对应，
/// 防止 paths 漏改导致「改了却没重建」。
/// </summary>
public class WorkflowPathsGuardTests
{
    private sealed record WorkflowExpectation(string Service, string[] Domains, bool IncludesSelf);

    private static readonly Dictionary<string, WorkflowExpectation> Expectations = new()
    {
        ["build-api-image.yml"] = new("api", ["core", "live", "cookie", "weibo", "recording"], false),
        ["build-crawler-image.yml"] = new("crawler", ["core", "live", "weibo"], false),
        ["build-cookie-image.yml"] = new("cookie", ["core", "cookie"], false),
        ["build-notifier-image.yml"] = new("notifier", ["core", "notification"], false),
        ["build-recording-image.yml"] = new("recording", ["core", "live", "recording"], false),
        ["build-recording-transcript-api-image.yml"] = new("recording-transcript-api", ["core", "live", "recording", "transcript"], true),
        ["build-recording-transcript-consumer-image.yml"] = new("recording-transcript-consumer", ["core", "live", "recording", "transcript"], true),
        ["build-training-image.yml"] = new("training", ["core", "training", "transcript"], true),
    };

    [Fact]
    public void WorkflowPaths_MatchReferenceClosure()
    {
        var root = ProjectReferenceGuardTests.FindRepoRoot();
        foreach (var (workflow, expected) in Expectations)
        {
            var path = Path.Combine(root, ".github", "workflows", workflow);
            Assert.True(File.Exists(path), $"missing workflow {workflow}");

            var content = File.ReadAllText(path);
            var pathsBlock = ExtractPathsBlock(content);
            var entries = Regex.Matches(pathsBlock, @"-\s*'([^']+)'").Select(m => m.Groups[1].Value).ToList();

            Assert.Contains($"servers/{expected.Service}/**", entries);

            var commonDomains = entries
                .Where(e => e.StartsWith("servers/common/"))
                .Select(e => e["servers/common/".Length..].Split('/')[0])
                .ToList();
            Assert.True(
                expected.Domains.OrderBy(x => x).SequenceEqual(commonDomains.OrderBy(x => x)),
                $"{workflow} expected common [{string.Join(",", expected.Domains)}] but was [{string.Join(",", commonDomains)}]");

            var self = $".github/workflows/{workflow}";
            if (expected.IncludesSelf)
            {
                Assert.Contains(self, entries);
            }
            else
            {
                Assert.DoesNotContain(self, entries);
            }
        }
    }

    private static string ExtractPathsBlock(string content)
    {
        var marker = "paths:";
        var index = content.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "paths: not found");
        var rest = content[(index + marker.Length)..];
        // paths entries are a contiguous list of '- ...' lines
        var lines = rest.Split('\n').Skip(1).TakeWhile(l => l.TrimStart().StartsWith("-")).ToList();
        return string.Join('\n', lines);
    }
}
