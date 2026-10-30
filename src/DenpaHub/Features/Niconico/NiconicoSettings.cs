namespace DenpaHub.Features.Niconico;

/// <summary>トークンのエンドポイントにクライアントを名乗る方法 (RFC 6749 2.3.1。名前は RFC 8414 の登録名)</summary>
public enum TokenAuthMethod
{
    ClientSecretBasic,
    ClientSecretPost,
}

/// <summary>
/// ニコニコの OAuth の中継の設定。名前と意味は docs/niconico.md の「設定」。
/// ニコニコ固有の値 (エンドポイントの URL・スコープ) はコードに書かず、ここから渡す。
/// </summary>
public sealed record NiconicoSettings(
    string RedirectUri,
    byte[] StateKey,
    string ClientId,
    string ClientSecret,
    Uri AuthorizeEndpoint,
    Uri TokenEndpoint,
    Uri? RevocationEndpoint,
    string? Scope,
    TokenAuthMethod TokenAuth,
    IReadOnlySet<string> AllowedReturnHosts)
{
    public static NiconicoSettings Load(Env env, HubSettings hub)
    {
        byte[] stateKey;
        try
        {
            stateKey = Convert.FromBase64String(env.Require("NICONICO_STATE_KEY"));
        }
        catch (FormatException)
        {
            throw new SettingsException("NICONICO_STATE_KEY は base64 で書いてください");
        }
        if (stateKey.Length != 32)
            throw new SettingsException("NICONICO_STATE_KEY は 32 バイトにしてください (openssl rand -base64 32)");

        var auth = env.Get("NICONICO_TOKEN_AUTH") switch
        {
            null or "client_secret_basic" => TokenAuthMethod.ClientSecretBasic,
            "client_secret_post" => TokenAuthMethod.ClientSecretPost,
            _ => throw new SettingsException("NICONICO_TOKEN_AUTH は client_secret_basic か client_secret_post です"),
        };

        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in env.GetList("NICONICO_ALLOWED_RETURN_HOSTS"))
        {
            if (Uri.CheckHostName(host) != UriHostNameType.Dns)
                throw new SettingsException("NICONICO_ALLOWED_RETURN_HOSTS にはホスト名だけをカンマ区切りで書いてください");
            hosts.Add(host);
        }

        return new NiconicoSettings(
            RedirectUri: hub.PublicOrigin + NiconicoFeature.CallbackPath,
            StateKey: stateKey,
            ClientId: env.Require("NICONICO_CLIENT_ID"),
            ClientSecret: env.Require("NICONICO_CLIENT_SECRET"),
            AuthorizeEndpoint: env.RequireUrl("NICONICO_AUTHORIZE_URL"),
            TokenEndpoint: env.RequireUrl("NICONICO_TOKEN_URL"),
            RevocationEndpoint: env.GetUrl("NICONICO_REVOKE_URL"),
            Scope: env.Get("NICONICO_SCOPE"),
            TokenAuth: auth,
            AllowedReturnHosts: hosts);
    }
}
