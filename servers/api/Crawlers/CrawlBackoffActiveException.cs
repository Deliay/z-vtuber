namespace Z.Vtuber.Crawlers;

public sealed class CrawlBackoffActiveException : Exception
{
    public TimeSpan Wait { get; }

    public CrawlBackoffActiveException(string message, TimeSpan wait)
        : base(message)
    {
        Wait = wait;
    }
}
