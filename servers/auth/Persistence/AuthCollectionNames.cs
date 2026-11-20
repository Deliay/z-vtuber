namespace Z.Vtuber.Auth.Persistence;

public static class AuthCollectionNames
{
    public const string LoginRequests = "auth_login_requests";
    public const string Users = "auth_users";
    public const string ProviderSessions = "auth_provider_sessions";
    public const string ProviderTickets = "auth_provider_tickets";
    public const string OAuthCodes = "auth_oauth_codes";
    public const string ProviderTokens = "auth_provider_tokens";
    public const string Sessions = "auth_sessions";
    public const string AuditLogs = "auth_audit_logs";
}

public static class LoginStatus
{
    public const string Pending = "pending";
    public const string Verified = "verified";
    public const string Confirming = "confirming";
    public const string Consumed = "consumed";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
}

public static class AuthUserStatus
{
    public const string Active = "active";
    public const string Disabled = "disabled";
}
