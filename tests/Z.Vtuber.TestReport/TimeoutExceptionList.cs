using System.Text.Json;

namespace Z.Vtuber.TestReport;

/// <summary>例外清单条目：确需超过 5 秒的用例及其理由。</summary>
public sealed record TimeoutExceptionEntry(string Test, string Reason, string Since);

/// <summary>例外清单，按用例全名精确匹配。</summary>
public sealed class TimeoutExceptionList
{
    public static readonly TimeoutExceptionList Empty = new([]);

    private readonly HashSet<string> _names;

    public TimeoutExceptionList(IReadOnlyList<TimeoutExceptionEntry> entries)
    {
        Entries = entries;
        _names = entries.Select(static e => e.Test).ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<TimeoutExceptionEntry> Entries { get; }

    public bool Contains(string testName) => _names.Contains(testName);

    public static TimeoutExceptionList Load(string path)
    {
        using var stream = File.OpenRead(path);
        var entries = JsonSerializer.Deserialize<List<TimeoutExceptionEntry>>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        return new TimeoutExceptionList(entries);
    }
}
