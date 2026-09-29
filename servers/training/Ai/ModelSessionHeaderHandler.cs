namespace Z.Vtuber.Training.Ai;

/// <summary>
/// 模型服务请求固定头 <c>x-opencode-session</c>（同名两个值）：
/// agent 标识 <c>z-vtuber-agent/1.0</c> + 每次请求新生成的随机 guid。
/// 挂在 named client <c>AiModel</c>（<see cref="ChatModelClientFactory.ClientName"/>）上，
/// 覆盖**所有**模型服务调用（情绪识别、重新识别、模型发现），与调用场景无关；
/// 同名多值由 HTTP 折叠为 <c>z-vtuber-agent/1.0, &lt;guid&gt;</c> 一行发送。
/// </summary>
public sealed class ModelSessionHeaderHandler : DelegatingHandler
{
    /// <summary>固定请求头名（HTTP 头不区分大小写）。</summary>
    public const string SessionHeaderName = "x-opencode-session";

    /// <summary>agent 标识（固定值）。</summary>
    public const string AgentIdentifier = "z-vtuber-agent/1.0";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation(SessionHeaderName, AgentIdentifier);
        request.Headers.TryAddWithoutValidation(SessionHeaderName, Guid.NewGuid().ToString("D"));
        return base.SendAsync(request, cancellationToken);
    }
}
