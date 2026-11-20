namespace Z.Vtuber.Auth.Configuration;

/// <summary>Casdoor Custom OAuth Provider 的 Client 配置（节 <c>OAuthProvider</c>）。</summary>
public sealed class OAuthProviderOptions
{
    public const string SectionName = "OAuthProvider";

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Casdoor 回调地址；精确匹配，不支持通配。</summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>Issuer（发现/审计用）；为空时使用 <see cref="AuthOptions.PublicBaseUrl"/>。</summary>
    public string Issuer { get; set; } = string.Empty;

    public int AccessTokenSeconds { get; set; } = 3600;

    public int RefreshTokenDays { get; set; } = 30;

    public int AuthorizationCodeSeconds { get; set; } = 300;

    /// <summary>是否强制 PKCE；Casdoor 开启 PKCE 时建议为 true。</summary>
    public bool RequirePkce { get; set; }
}

/// <summary>Casdoor 统一登录环境信息（节 <c>Casdoor</c>），仅用于展示/日志/重验跳转。</summary>
public sealed class CasdoorOptions
{
    public const string SectionName = "Casdoor";

    public string Endpoint { get; set; } = string.Empty;

    public string Organization { get; set; } = string.Empty;

    public string ApplicationName { get; set; } = string.Empty;

    /// <summary>账号中心「重新验证」跳转地址；配置为站点/Casdoor 登录入口。</summary>
    public string ReauthorizeUrl { get; set; } = string.Empty;
}
