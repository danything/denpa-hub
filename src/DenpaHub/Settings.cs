using System.Net;

namespace DenpaHub;

/// <summary>設定の誤り。メッセージには設定の名前だけを入れ、値は入れない (シークレットをログに出さない)</summary>
public sealed class SettingsException(string message) : Exception(message);

/// <summary>
/// 環境変数を読む。<c>NAME</c> が空なら <c>NAME_FILE</c> が指すファイルの中身を使う
/// (Docker や Kubernetes のシークレットをファイルで渡すため)。
/// </summary>
public sealed class Env(Func<string, string?> source)
{
    public string? Get(string name)
    {
        var value = source(name);
        if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        var file = source(name + "_FILE");
        if (string.IsNullOrWhiteSpace(file)) return null;
        try
        {
            var content = File.ReadAllText(file.Trim()).Trim();
            return content.Length == 0 ? null : content;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SettingsException($"{name}_FILE のファイルを読めません ({e.GetType().Name})");
        }
    }

    public string Require(string name) => Get(name) ?? throw new SettingsException($"{name} が設定されていません");

    /// <summary>https だけを受ける。手元で試すときのために、ループバックのホストだけは http も通す</summary>
    public Uri RequireUrl(string name) => ParseUrl(Require(name), name);

    public Uri? GetUrl(string name) => Get(name) is { } value ? ParseUrl(value, name) : null;

    public IEnumerable<string> GetList(string name) =>
        (Get(name) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static Uri ParseUrl(string value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url))
            throw new SettingsException($"{name} が URL になっていません");
        if (url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback)) return url;
        throw new SettingsException($"{name} は https の URL にしてください");
    }
}

/// <summary>機能をまたいで使う設定。名前と意味は README.md の「設定」</summary>
/// <param name="PublicOrigin">外から見えるこのサーバの origin (末尾の / なし)。リダイレクト URI はここから決まる</param>
/// <param name="TrustedProxies">X-Forwarded-For を信じる手前のプロキシ</param>
public sealed record HubSettings(string PublicOrigin, IReadOnlyList<IPNetwork> TrustedProxies)
{
    public static HubSettings Load(Env env)
    {
        var origin = env.RequireUrl("PUBLIC_ORIGIN");
        if (origin.AbsolutePath != "/" || origin.Query.Length > 0 || origin.Fragment.Length > 0 || origin.UserInfo.Length > 0)
            throw new SettingsException("PUBLIC_ORIGIN はパスを含まない https://<ドメイン> の形にしてください");

        var proxies = new List<IPNetwork>();
        foreach (var cidr in env.GetList("TRUSTED_PROXIES"))
        {
            if (!IPNetwork.TryParse(cidr, out var network))
                throw new SettingsException("TRUSTED_PROXIES は 10.0.0.0/8 のような CIDR をカンマ区切りで書いてください");
            proxies.Add(network);
        }
        return new HubSettings(origin.GetLeftPart(UriPartial.Authority), proxies);
    }
}
