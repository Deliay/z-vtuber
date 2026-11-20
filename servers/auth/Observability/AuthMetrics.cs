using System.Diagnostics.Metrics;

namespace Z.Vtuber.Auth.Observability;

/// <summary>结构化指标（不记录 token/ticket/code/Authorization）。</summary>
public sealed class AuthMetrics
{
    public const string MeterName = "Z.Vtuber.Auth";

    private readonly Counter<long> _loginCreated;
    private readonly Counter<long> _loginVerified;
    private readonly Counter<long> _loginConfirmed;
    private readonly Counter<long> _loginFailed;
    private readonly Counter<long> _roomConnects;
    private readonly Counter<long> _roomReconnects;
    private readonly Counter<long> _roomBackfillEvents;
    private readonly Counter<long> _roomBackfillTruncated;
    private readonly Counter<long> _oauthAuthorize;
    private readonly Counter<long> _oauthToken;
    private readonly Counter<long> _oauthFailures;

    public AuthMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _loginCreated = meter.CreateCounter<long>("auth.login.created");
        _loginVerified = meter.CreateCounter<long>("auth.login.verified");
        _loginConfirmed = meter.CreateCounter<long>("auth.login.confirmed");
        _loginFailed = meter.CreateCounter<long>("auth.login.failed");
        _roomConnects = meter.CreateCounter<long>("auth.danmaku.room.connects");
        _roomReconnects = meter.CreateCounter<long>("auth.danmaku.room.reconnects");
        _roomBackfillEvents = meter.CreateCounter<long>("auth.danmaku.room.backfill.events");
        _roomBackfillTruncated = meter.CreateCounter<long>("auth.danmaku.room.backfill.truncated");
        _oauthAuthorize = meter.CreateCounter<long>("auth.oauth.authorize");
        _oauthToken = meter.CreateCounter<long>("auth.oauth.token");
        _oauthFailures = meter.CreateCounter<long>("auth.oauth.failures");
    }

    public void LoginCreated() => _loginCreated.Add(1);
    public void LoginVerified() => _loginVerified.Add(1);
    public void LoginConfirmed() => _loginConfirmed.Add(1);
    public void LoginFailed() => _loginFailed.Add(1);
    public void RoomConnected() => _roomConnects.Add(1);
    public void RoomReconnect() => _roomReconnects.Add(1);
    public void RoomBackfillProcessed(int count)
    {
        if (count > 0)
        {
            _roomBackfillEvents.Add(count);
        }
    }

    public void RoomBackfillTruncated() => _roomBackfillTruncated.Add(1);
    public void OAuthAuthorize() => _oauthAuthorize.Add(1);
    public void OAuthToken() => _oauthToken.Add(1);
    public void OAuthFailure() => _oauthFailures.Add(1);
}
