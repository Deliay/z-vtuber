namespace Z.Vtuber.Common.Contracts;

public sealed class NoAvailableCookieException : InvalidOperationException
{
    public NoAvailableCookieException()
        : base("No active cookie available in the pool; crawling is paused until a valid cookie is added.")
    {
    }
}
