using Cdm.Authentication.OAuth2;

/// <summary>
/// OAuth2 + PKCE provider for app.linn.games's Laravel Passport installation.
/// Public client (no secret) — RFC 8252 native-app pattern, security via PKCE
/// + loopback redirect.
///
/// Client ID matches the Passport migration
/// 2026_05_17_register_oauth_client_dronedetect_desktop.php in app.linn.games.
/// </summary>
public sealed class PassportAuth : AuthorizationCodeFlowWithPkce
{
    public PassportAuth(Configuration configuration) : base(configuration) { }
    public override string authorizationUrl => "https://app.linn.games/oauth/authorize";
    public override string accessTokenUrl   => "https://app.linn.games/oauth/token";
}
