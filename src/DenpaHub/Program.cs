using DenpaHub;
using DenpaHub.Features.Jikkyo;
using DenpaHub.Features.Niconico;
using DenpaHub.Features.Ranking;
using Microsoft.Data.Sqlite;

var env = new Env(Environment.GetEnvironmentVariable);

// 外部に示す集計を読み出す (Doa がコンテナの中で動かす)。サーバは立てない
if (args is [Tallies.Command, ..])
{
    try
    {
        return Tallies.Run(args, RankingSettings.Load(env), Console.Out, Console.Error);
    }
    catch (SettingsException e)
    {
        Console.Error.WriteLine($"設定の誤り: {e.Message}");
        return 2;
    }
    catch (SqliteException e)
    {
        Console.Error.WriteLine($"RANKING_DATA のファイルを読めません (SQLite のエラー {e.SqliteErrorCode})");
        return 1;
    }
}

// 削除依頼を調べるときに独自コメントを読み出す (Doa がコンテナの中で動かす)。サーバは立てない
if (args is [CommentsCli.Command, ..])
{
    try
    {
        return CommentsCli.Run(args, JikkyoSettings.Load(env), Console.Out, Console.Error);
    }
    catch (SettingsException e)
    {
        Console.Error.WriteLine($"設定の誤り: {e.Message}");
        return 2;
    }
    catch (SqliteException e)
    {
        Console.Error.WriteLine($"JIKKYO_DATA のファイルを読めません (SQLite のエラー {e.SqliteErrorCode})");
        return 1;
    }
}

HubSettings hub;
NiconicoSettings niconico;
RankingSettings? ranking;
JikkyoSettings? jikkyo;
try
{
    hub = HubSettings.Load(env);
    niconico = NiconicoSettings.Load(env, hub);
    ranking = RankingSettings.Load(env);
    jikkyo = JikkyoSettings.Load(env);
}
catch (SettingsException e)
{
    Console.Error.WriteLine($"設定の誤り: {e.Message}");
    return 1;
}

var app = Hub.Build(WebApplication.CreateSlimBuilder(args), hub, niconico, ranking: ranking, jikkyo: jikkyo);
if (ranking is not null)
{
    // データのファイルは起動のときに開く。開けなければ、設定の誤りと同じく名前だけを書いて止まる
    try
    {
        app.Services.GetRequiredService<RankingStore>();
    }
    catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"設定の誤り: RANKING_DATA のファイルを開けません ({(e is SqliteException s ? $"SQLite のエラー {s.SqliteErrorCode}" : e.GetType().Name)})");
        return 1;
    }
}
if (jikkyo is not null)
{
    // 方針のファイルとデータのファイルは起動のときに読む。読めなければ名前だけを書いて止まる
    try
    {
        app.Services.GetRequiredService<PolicyHolder>();
        app.Services.GetRequiredService<JikkyoStore>();
    }
    catch (SettingsException e)
    {
        Console.Error.WriteLine($"設定の誤り: {e.Message}");
        return 1;
    }
    catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"設定の誤り: JIKKYO_DATA のファイルを開けません ({(e is SqliteException s ? $"SQLite のエラー {s.SqliteErrorCode}" : e.GetType().Name)})");
        return 1;
    }
}
app.Logger.LogInformation("niconico redirect_uri: {RedirectUri}", niconico.RedirectUri);
app.Logger.LogInformation("ranking: {State}", ranking is null ? "off" : "on");
app.Logger.LogInformation("jikkyo: {State}", jikkyo is null ? "off" : "on");
await app.RunAsync();
return 0;
