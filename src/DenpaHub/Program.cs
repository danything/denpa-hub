using DenpaHub;
using DenpaHub.Features.Niconico;
using DenpaHub.Features.Ranking;
using Microsoft.Data.Sqlite;

HubSettings hub;
NiconicoSettings niconico;
RankingSettings? ranking;
try
{
    var env = new Env(Environment.GetEnvironmentVariable);
    hub = HubSettings.Load(env);
    niconico = NiconicoSettings.Load(env, hub);
    ranking = RankingSettings.Load(env);
}
catch (SettingsException e)
{
    Console.Error.WriteLine($"設定の誤り: {e.Message}");
    return 1;
}

var app = Hub.Build(WebApplication.CreateSlimBuilder(args), hub, niconico, ranking: ranking);
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
app.Logger.LogInformation("niconico redirect_uri: {RedirectUri}", niconico.RedirectUri);
app.Logger.LogInformation("ranking: {State}", ranking is null ? "off" : "on");
await app.RunAsync();
return 0;
