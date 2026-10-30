using DenpaHub;
using DenpaHub.Features.Niconico;

HubSettings hub;
NiconicoSettings niconico;
try
{
    var env = new Env(Environment.GetEnvironmentVariable);
    hub = HubSettings.Load(env);
    niconico = NiconicoSettings.Load(env, hub);
}
catch (SettingsException e)
{
    Console.Error.WriteLine($"設定の誤り: {e.Message}");
    return 1;
}

var app = Hub.Build(WebApplication.CreateSlimBuilder(args), hub, niconico);
app.Logger.LogInformation("niconico redirect_uri: {RedirectUri}", niconico.RedirectUri);
await app.RunAsync();
return 0;
