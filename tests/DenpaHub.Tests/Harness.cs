using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DenpaHub;
using DenpaHub.Features.Niconico;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace DenpaHub.Tests;

/// <summary>
/// 偽のニコニコ (認可・トークン・失効のエンドポイント)。一般的な OAuth 2.0 の認可サーバとして振る舞い、
/// 中継が送るもの (クライアントの認証・redirect_uri・PKCE) を確かめる。
/// </summary>
sealed class FakeProvider : IAsyncDisposable
{
    public const string ClientId = "test-client";
    public const string ClientSecret = "test-secret-do-not-log";
    public const string Origin = "https://provider.test";

    readonly WebApplication app;
    readonly ConcurrentDictionary<string, (string Challenge, string RedirectUri)> codes = new();

    /// <summary>トークンのエンドポイントが返すもの。差し替えて失敗の形を試す</summary>
    public Func<HttpContext, Task>? TokenOverride { get; set; }
    public List<string> RevokedTokens { get; } = [];
    public int TokenRequests;

    public HttpMessageHandler Handler => app.GetTestServer().CreateHandler();

    FakeProvider(WebApplication app) => this.app = app;

    public static async Task<FakeProvider> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        var provider = new FakeProvider(app);
        app.MapGet("/oauth/authorize", provider.Authorize);
        app.MapPost("/oauth/token", provider.Token);
        app.MapPost("/oauth/revoke", provider.Revoke);
        await app.StartAsync();
        return provider;
    }

    /// <summary>利用者がログインして同意したことにして、コードを付けて redirect_uri へ戻す</summary>
    IResult Authorize(HttpContext context)
    {
        var q = context.Request.Query;
        if (q["client_id"] != ClientId || q["response_type"] != "code" || q["code_challenge_method"] != "S256")
            return Results.BadRequest();
        var redirect = q["redirect_uri"].ToString();
        if (q["deny"] == "1")
            return Results.Redirect(QueryHelpers.AddQueryString(redirect, new Dictionary<string, string?> { ["error"] = "access_denied", ["state"] = q["state"] }));
        var code = "code-" + Guid.NewGuid().ToString("N");
        codes[code] = (q["code_challenge"].ToString(), redirect);
        return Results.Redirect(QueryHelpers.AddQueryString(redirect, new Dictionary<string, string?> { ["code"] = code, ["state"] = q["state"] }));
    }

    async Task Token(HttpContext context)
    {
        Interlocked.Increment(ref TokenRequests);
        if (TokenOverride is not null)
        {
            await TokenOverride(context);
            return;
        }
        if (!Authenticated(context))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new Dictionary<string, string> { ["error"] = "invalid_client" });
            return;
        }
        var form = await context.Request.ReadFormAsync();
        switch (form["grant_type"].ToString())
        {
            case "authorization_code":
                if (!codes.TryRemove(form["code"].ToString(), out var issued)
                    || issued.RedirectUri != form["redirect_uri"]
                    || issued.Challenge != Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString()))))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new Dictionary<string, string> { ["error"] = "invalid_grant" });
                    return;
                }
                await context.Response.WriteAsJsonAsync(new Dictionary<string, object>
                {
                    ["access_token"] = "access-1",
                    ["refresh_token"] = "refresh-1",
                    ["token_type"] = "Bearer",
                    ["expires_in"] = 3600,
                });
                return;
            case "refresh_token" when form["refresh_token"] == "refresh-1":
                await context.Response.WriteAsJsonAsync(new Dictionary<string, object>
                {
                    ["access_token"] = "access-2",
                    ["refresh_token"] = "refresh-2",
                    ["token_type"] = "Bearer",
                    ["expires_in"] = 3600,
                });
                return;
            default:
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new Dictionary<string, string> { ["error"] = "invalid_grant" });
                return;
        }
    }

    async Task Revoke(HttpContext context)
    {
        if (!Authenticated(context))
        {
            context.Response.StatusCode = 401;
            return;
        }
        var form = await context.Request.ReadFormAsync();
        lock (RevokedTokens) RevokedTokens.Add(form["token"].ToString());
    }

    static bool Authenticated(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}"));
    }

    public ValueTask DisposeAsync() => app.DisposeAsync();
}

/// <summary>ログを全部ためておき、秘密の値が混じっていないかを確かめる</summary>
sealed class CapturedLogs : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Dispose() { }

    sealed class Logger(CapturedLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Lines.Enqueue($"{category} {logLevel} {formatter(state, exception)} {exception}");
    }
}

sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>中継・偽のニコニコ・denpa 役 (鍵の組) を揃えたもの</summary>
sealed class Harness : IAsyncDisposable
{
    public const string RelayOrigin = "https://hub.test";
    public const string Return = "http://192.168.1.20:3000" + ReturnTarget.Path;
    public static readonly byte[] StateKey = RandomNumberGenerator.GetBytes(32);

    public required FakeProvider Provider { get; init; }
    public required WebApplication App { get; init; }
    public required HttpClient Server { get; init; }
    public required HttpClient ProviderBrowser { get; init; }
    public required CapturedLogs Logs { get; init; }
    public required ManualTime Time { get; init; }
    /// <summary>denpa のサーバが持つ鍵の組</summary>
    public ECDiffieHellman Denpa { get; } = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    public string DenpaKey => Base64Url.EncodeToString(TokenSeal.ToRaw(Denpa.ExportParameters(false)));

    public static (HubSettings Hub, NiconicoSettings Niconico) Load(Dictionary<string, string?> values)
    {
        var env = new DenpaHub.Env(name => values.GetValueOrDefault(name));
        var hub = HubSettings.Load(env);
        return (hub, NiconicoSettings.Load(env, hub));
    }

    public static Dictionary<string, string?> Env(Dictionary<string, string?>? overrides = null)
    {
        var env = new Dictionary<string, string?>
        {
            ["PUBLIC_ORIGIN"] = RelayOrigin,
            ["NICONICO_STATE_KEY"] = Convert.ToBase64String(StateKey),
            ["NICONICO_CLIENT_ID"] = FakeProvider.ClientId,
            ["NICONICO_CLIENT_SECRET"] = FakeProvider.ClientSecret,
            ["NICONICO_AUTHORIZE_URL"] = FakeProvider.Origin + "/oauth/authorize",
            ["NICONICO_TOKEN_URL"] = FakeProvider.Origin + "/oauth/token",
            ["NICONICO_REVOKE_URL"] = FakeProvider.Origin + "/oauth/revoke",
            ["NICONICO_SCOPE"] = "openid offline_access",
        };
        foreach (var (k, v) in overrides ?? []) env[k] = v;
        return env;
    }

    public static async Task<Harness> StartAsync(Dictionary<string, string?>? overrides = null)
    {
        var provider = await FakeProvider.StartAsync();
        var env = Env(overrides);
        var (hubSettings, settings) = Load(env);
        var logs = new CapturedLogs();
        var time = new ManualTime();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        var app = Hub.Build(builder, hubSettings, settings, provider.Handler, time);
        await app.StartAsync();
        var relay = app.GetTestServer().CreateClient();
        relay.BaseAddress = new Uri(RelayOrigin);
        var browser = new HttpClient(provider.Handler) { BaseAddress = new Uri(FakeProvider.Origin) };
        return new Harness { Provider = provider, App = app, Server = relay, ProviderBrowser = browser, Logs = logs, Time = time };
    }

    /// <summary>denpa の画面から中継へ。ニコニコへのリダイレクト先と Cookie を返す</summary>
    public async Task<(HttpResponseMessage Response, string? Cookie)> AuthorizeAsync(string? returnUrl = Return, string? key = null)
    {
        var url = "/niconico/authorize?" + string.Join('&', new[] { ("return", returnUrl), ("key", key ?? DenpaKey) }
            .Where(p => p.Item2 is not null)
            .Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2!)}"));
        var response = await Server.GetAsync(url);
        var cookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith(NiconicoFeature.BindingCookie + "="))
            : null;
        return (response, cookie);
    }

    /// <summary>ニコニコの認可の画面で同意し、中継のコールバックの URL を受け取る</summary>
    public async Task<Uri> ConsentAsync(Uri authorizeUrl, bool deny = false)
    {
        var url = authorizeUrl.PathAndQuery + (deny ? "&deny=1" : "");
        var response = await ProviderBrowser.GetAsync(url);
        return response.Headers.Location!;
    }

    public async Task<HttpResponseMessage> CallbackAsync(Uri callback, string? cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, callback.PathAndQuery);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        return await Server.SendAsync(request);
    }

    /// <summary>連携の全体を通し、戻り先へのリダイレクトを返す</summary>
    public async Task<HttpResponseMessage> LinkAsync(bool deny = false)
    {
        var (authorize, cookie) = await AuthorizeAsync();
        var callback = await ConsentAsync(authorize.Headers.Location!, deny);
        return await CallbackAsync(callback, cookie);
    }

    /// <summary>denpa のサーバが行う開封 (docs/niconico.md の「暗号の形」をそのまま書いたもの)</summary>
    public string Open(string result, string returnUrl = Return)
    {
        var data = Base64Url.DecodeFromChars(result);
        var ephemeral = data[..TokenSeal.PublicKeyLength];
        var nonce = data[TokenSeal.PublicKeyLength..(TokenSeal.PublicKeyLength + 12)];
        var cipher = data[(TokenSeal.PublicKeyLength + 12)..^16];
        var tag = data[^16..];
        using var peer = ECDiffieHellman.Create(TokenSeal.ToParameters(ephemeral));
        var secret = Denpa.DeriveRawSecretAgreement(peer.PublicKey);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, [.. ephemeral, .. TokenSeal.ToRaw(Denpa.ExportParameters(false))], "denpa-hub niconico token v1"u8.ToArray());
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(returnUrl));
        return Encoding.UTF8.GetString(plain);
    }

    public async ValueTask DisposeAsync()
    {
        Server.Dispose();
        ProviderBrowser.Dispose();
        Denpa.Dispose();
        await App.DisposeAsync();
        await Provider.DisposeAsync();
    }
}
