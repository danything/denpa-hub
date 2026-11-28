namespace DenpaHub.Features.Jikkyo;

/// <summary>実況のコメントの中継の設定。名前と意味は docs/jikkyo.md の「設定」</summary>
/// <param name="DataPath">独自コメントとトークンを置く SQLite のファイル</param>
/// <param name="Key">利用者の ID と、NX-Jikkyo に渡す Cookie を作る鍵 (32 バイト)</param>
/// <param name="PolicyPath">運営の方針の JSON のファイル</param>
/// <param name="NxOrigin">NX-Jikkyo の WebSocket のオリジン (テストでは偽物)</param>
/// <param name="KakologOrigin">ニコニコ実況 過去ログ API のオリジン (テストでは偽物)</param>
public sealed record JikkyoSettings(string DataPath, byte[] Key, string PolicyPath, Uri NxOrigin, Uri KakologOrigin)
{
    public static readonly Uri DefaultNxOrigin = new("wss://nx-jikkyo.tsukumijima.net");
    public static readonly Uri DefaultKakologOrigin = new("https://jikkyo.tsukumijima.net");

    /// <summary><c>JIKKYO_DATA</c> が無ければ null (この機能を外す)</summary>
    public static JikkyoSettings? Load(Env env)
    {
        var path = env.Get("JIKKYO_DATA");
        if (path is null) return null;
        if (!Path.IsPathFullyQualified(path))
            throw new SettingsException("JIKKYO_DATA は /data/jikkyo.db のような絶対パスにしてください");

        byte[] key;
        try
        {
            key = Convert.FromBase64String(env.Get("JIKKYO_KEY")
                ?? throw new SettingsException("JIKKYO_DATA があるときは JIKKYO_KEY が要ります"));
        }
        catch (FormatException)
        {
            throw new SettingsException("JIKKYO_KEY は base64 で書いてください");
        }
        if (key.Length != 32)
            throw new SettingsException("JIKKYO_KEY は 32 バイトにしてください (openssl rand -base64 32)");

        var policy = env.Get("JIKKYO_POLICY")
            ?? throw new SettingsException("JIKKYO_DATA があるときは JIKKYO_POLICY が要ります");
        if (!Path.IsPathFullyQualified(policy))
            throw new SettingsException("JIKKYO_POLICY は /config/jikkyo-policy.json のような絶対パスにしてください");
        return new JikkyoSettings(path, key, policy, DefaultNxOrigin, DefaultKakologOrigin);
    }
}
