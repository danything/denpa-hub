namespace DenpaHub.Features.Ranking;

/// <summary>予約数ランキングの設定。名前と意味は docs/ranking.md の「データの持ち方」</summary>
/// <param name="DataPath">SQLite のファイルの場所</param>
/// <param name="MarkKey">二重に数えない印を作る鍵 (32 バイト)</param>
public sealed record RankingSettings(string DataPath, byte[] MarkKey)
{
    /// <summary><c>RANKING_DATA</c> が無ければ null (この機能を外す)</summary>
    public static RankingSettings? Load(Env env)
    {
        var path = env.Get("RANKING_DATA");
        if (path is null) return null;
        if (!Path.IsPathFullyQualified(path))
            throw new SettingsException("RANKING_DATA は /data/ranking.db のような絶対パスにしてください");

        byte[] key;
        try
        {
            key = Convert.FromBase64String(env.Get("RANKING_MARK_KEY")
                ?? throw new SettingsException("RANKING_DATA があるときは RANKING_MARK_KEY が要ります"));
        }
        catch (FormatException)
        {
            throw new SettingsException("RANKING_MARK_KEY は base64 で書いてください");
        }
        if (key.Length != 32)
            throw new SettingsException("RANKING_MARK_KEY は 32 バイトにしてください (openssl rand -base64 32)");
        return new RankingSettings(path, key);
    }
}
