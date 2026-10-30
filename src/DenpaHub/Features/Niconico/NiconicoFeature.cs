using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;

namespace DenpaHub.Features.Niconico;

/// <summary>
/// ニコニコの OAuth の中継。denpa の利用者が自分のアカウントでニコニコ実況に書き込めるよう、
/// Doa のクライアントで認可を取り、トークンをその家の denpa へ渡す。契約は docs/niconico.md
/// </summary>
public static class NiconicoFeature
{
    public const string CallbackPath = "/niconico/callback";

    /// <summary>認可の画面でログインして同意するまでの時間。それより長いものはやり直してもらう</summary>
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 認可を始めたブラウザに置く Cookie。コールバックが同じブラウザから来たことを確かめる。
    /// <c>__Host-</c> を付けると Secure・Path=/・Domain なしが強制される
    /// </summary>
    public const string BindingCookie = "__Host-denpa-hub-niconico";

    const string BrowserLimit = "niconico-browser";
    const string ServerLimit = "niconico-server";

    public static void Add(IServiceCollection services, NiconicoSettings settings, HttpMessageHandler? upstream)
    {
        services.AddSingleton(settings);
        services.AddSingleton(new StateCodec(settings.StateKey));
        services.AddSingleton(new Upstream(settings, Upstream.CreateHttpClient(upstream)));
        services.Configure<RateLimiterOptions>(o =>
        {
            // 認可の画面への出入り。人の操作なので 1 分に 30 回あれば足りる
            o.AddPolicy(BrowserLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(Hub.ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
            }));
            // denpa のサーバからの更新。1 つの家は 1 時間に数回だが、CGNAT で多くの家が 1 つのアドレスを共有しうる
            o.AddPolicy(ServerLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(Hub.ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
            }));
        });
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/niconico/authorize", (RequestDelegate)Authorize).RequireRateLimiting(BrowserLimit);
        app.MapGet(CallbackPath, (RequestDelegate)Callback).RequireRateLimiting(BrowserLimit);
        app.MapPost("/niconico/token", (RequestDelegate)Token).RequireRateLimiting(ServerLimit);
        app.MapPost("/niconico/revoke", (RequestDelegate)Revoke).RequireRateLimiting(ServerLimit);
    }

    /// <summary>denpa の画面から飛んでくる。state を作ってニコニコの認可の画面へ送る</summary>
    static async Task Authorize(HttpContext context)
    {
        var settings = context.RequestServices.GetRequiredService<NiconicoSettings>();
        var query = context.Request.Query;
        var target = ReturnTarget.Check(query["return"], settings.AllowedReturnHosts);
        if (target.Url is null)
        {
            Log(context).LogInformation("authorize rejected: return");
            await Text(context, StatusCodes.Status400BadRequest, target.Error!);
            return;
        }
        if (!TokenSeal.TryParsePublicKey(query["key"], out var key))
        {
            Log(context).LogInformation("authorize rejected: key");
            await Text(context, StatusCodes.Status400BadRequest, "key が P-256 の公開鍵になっていません。");
            return;
        }

        var binding = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var now = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var state = context.RequestServices.GetRequiredService<StateCodec>().Seal(new StatePayload(
            Return: target.Url,
            Key: Base64Url.EncodeToString(key),
            Verifier: verifier,
            Expires: now.Add(StateLifetime).ToUnixTimeSeconds(),
            Binding: Hash(binding)));

        context.Response.Cookies.Append(BindingCookie, binding, new CookieOptions
        {
            Path = "/",
            Secure = true,
            HttpOnly = true,
            // ニコニコからのリダイレクト (別サイトからのトップレベルの GET) で送られる強さ
            SameSite = SameSiteMode.Lax,
            MaxAge = StateLifetime,
        });

        var parameters = new List<KeyValuePair<string, string?>>
        {
            new("response_type", "code"),
            new("client_id", settings.ClientId),
            new("redirect_uri", settings.RedirectUri),
            new("state", state),
            new("code_challenge", Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))),
            new("code_challenge_method", "S256"),
        };
        if (settings.Scope is not null) parameters.Add(new("scope", settings.Scope));

        Log(context).LogInformation("authorize: return={Kind}", target.Kind);
        context.Response.Redirect(QueryHelpers.AddQueryString(settings.AuthorizeEndpoint.AbsoluteUri, parameters));
    }

    /// <summary>ニコニコから戻ってくる。コードをトークンに替え、denpa の公開鍵で暗号化して戻り先へ渡す</summary>
    static async Task Callback(HttpContext context)
    {
        var query = context.Request.Query;
        var payload = context.RequestServices.GetRequiredService<StateCodec>().Open(query["state"]);
        if (payload is null)
        {
            Log(context).LogInformation("callback rejected: state");
            await Text(context, StatusCodes.Status400BadRequest, "連携の情報が読めません。denpa の設定からやり直してください。");
            return;
        }
        var now = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().ToUnixTimeSeconds();
        if (now > payload.Expires)
        {
            Log(context).LogInformation("callback rejected: expired");
            await Text(context, StatusCodes.Status400BadRequest, "時間が経ちすぎました。denpa の設定からやり直してください。");
            return;
        }
        var cookie = context.Request.Cookies[BindingCookie];
        if (cookie is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(cookie)), Encoding.ASCII.GetBytes(payload.Binding)))
        {
            Log(context).LogInformation("callback rejected: binding");
            await Text(context, StatusCodes.Status400BadRequest, "連携を始めたブラウザと違います。denpa の設定からやり直してください。");
            return;
        }
        context.Response.Cookies.Delete(BindingCookie, new CookieOptions { Path = "/", Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax });

        // 利用者が断ったときなど。RFC 6749 4.1.2.1 の error をそのまま denpa へ返す
        var error = query["error"].ToString();
        if (error.Length > 0)
        {
            Log(context).LogInformation("callback: provider error {Error}", ErrorCode(error));
            Back(context, payload.Return, "error", ErrorCode(error));
            return;
        }
        var code = query["code"].ToString();
        if (code.Length == 0)
        {
            Log(context).LogInformation("callback: no code");
            Back(context, payload.Return, "error", "invalid_request");
            return;
        }

        var settings = context.RequestServices.GetRequiredService<NiconicoSettings>();
        UpstreamResponse response;
        try
        {
            response = await context.RequestServices.GetRequiredService<Upstream>().TokenAsync(
            [
                new("grant_type", "authorization_code"),
                new("code", code),
                new("redirect_uri", settings.RedirectUri),
                new("code_verifier", payload.Verifier),
            ], context.RequestAborted);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Log(context).LogWarning("callback: token endpoint unreachable ({Type})", e.GetType().Name);
            Back(context, payload.Return, "error", "temporarily_unavailable");
            return;
        }
        if ((int)response.Status is < 200 or >= 300)
        {
            var upstreamError = ReadError(response.Body);
            Log(context).LogWarning("callback: token endpoint {Status} {Error}", (int)response.Status, upstreamError);
            Back(context, payload.Return, "error", upstreamError ?? "token_exchange_failed");
            return;
        }
        if (!HasAccessToken(response.Body))
        {
            Log(context).LogWarning("callback: token response without access_token");
            Back(context, payload.Return, "error", "invalid_token_response");
            return;
        }

        // 応答の JSON は手を加えずに渡す (形が変わっても denpa が読むものだけ読めばよい)
        TokenSeal.TryParsePublicKey(payload.Key, out var key);
        var sealedToken = TokenSeal.Seal(key, response.Body, Encoding.UTF8.GetBytes(payload.Return));
        Log(context).LogInformation("callback: delivered");
        Back(context, payload.Return, "result", sealedToken);
    }

    /// <summary>denpa のサーバからの更新 (refresh_token の付け替え)。シークレットを付けて送り、応答をそのまま返す</summary>
    static async Task Token(HttpContext context)
    {
        var form = await ReadForm(context);
        if (form is null)
        {
            await Json(context, StatusCodes.Status400BadRequest, "invalid_request");
            return;
        }
        // コードとの交換はコールバックの中だけで行う。ここで受けるのは更新だけ
        if (form["grant_type"] != "refresh_token")
        {
            await Json(context, StatusCodes.Status400BadRequest, "unsupported_grant_type");
            return;
        }
        var refreshToken = form["refresh_token"].ToString();
        if (refreshToken.Length == 0)
        {
            await Json(context, StatusCodes.Status400BadRequest, "invalid_request");
            return;
        }
        List<KeyValuePair<string, string>> parameters = [new("grant_type", "refresh_token"), new("refresh_token", refreshToken)];
        var scope = form["scope"].ToString();
        if (scope.Length > 0) parameters.Add(new("scope", scope));
        await Forward(context, "token", u => u.TokenAsync(parameters, context.RequestAborted));
    }

    /// <summary>連携を外すときの失効 (RFC 7009)。NICONICO_REVOKE_URL が無ければ 404</summary>
    static async Task Revoke(HttpContext context)
    {
        if (context.RequestServices.GetRequiredService<NiconicoSettings>().RevocationEndpoint is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var form = await ReadForm(context);
        var token = form?["token"].ToString() ?? "";
        if (token.Length == 0)
        {
            await Json(context, StatusCodes.Status400BadRequest, "invalid_request");
            return;
        }
        List<KeyValuePair<string, string>> parameters = [new("token", token)];
        var hint = form!["token_type_hint"].ToString();
        if (hint.Length > 0) parameters.Add(new("token_type_hint", hint));
        await Forward(context, "revoke", u => u.RevokeAsync(parameters, context.RequestAborted));
    }

    static async Task Forward(HttpContext context, string name, Func<Upstream, Task<UpstreamResponse>> send)
    {
        UpstreamResponse response;
        try
        {
            response = await send(context.RequestServices.GetRequiredService<Upstream>());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Log(context).LogWarning("{Name}: upstream unreachable ({Type})", name, e.GetType().Name);
            await Json(context, StatusCodes.Status502BadGateway, "temporarily_unavailable");
            return;
        }
        Log(context).LogInformation("{Name}: upstream {Status}", name, (int)response.Status);
        context.Response.StatusCode = (int)response.Status;
        if (response.Body.Length == 0) return;
        context.Response.ContentType = response.ContentType ?? "application/json";
        await context.Response.Body.WriteAsync(response.Body, context.RequestAborted);
    }

    /// <summary>フォームを読む。中身はトークン 1 つ分なので、大きいものは読まない</summary>
    static async Task<IFormCollection?> ReadForm(HttpContext context)
    {
        if (!context.Request.HasFormContentType) return null;
        var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = 16 * 1024;
        try
        {
            return await context.Request.ReadFormAsync(context.RequestAborted);
        }
        catch (Exception e) when (e is InvalidDataException or BadHttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// 戻り先へ 303 で返す。値はフラグメントに入れる — フラグメントはブラウザから先へ送られないので、
    /// denpa の手前のプロキシのログや Referer に残らない
    /// </summary>
    static void Back(HttpContext context, string returnUrl, string name, string value)
    {
        context.Response.StatusCode = StatusCodes.Status303SeeOther;
        context.Response.Headers.Location = $"{returnUrl}#{name}={value}";
    }

    /// <summary>error の値は英小文字・数字・_ だけにして長さも切る (戻り先の URL にそのまま入れるので)</summary>
    static string ErrorCode(string value)
    {
        var cleaned = new string(value.Where(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_').Take(64).ToArray());
        return cleaned.Length == 0 ? "unknown_error" : cleaned;
    }

    static string? ReadError(byte[] body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                ? ErrorCode(error.GetString()!)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static bool HasAccessToken(byte[] body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("access_token", out var token)
                && token.ValueKind == JsonValueKind.String;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    static string Hash(string value) => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(value)));


    static ILogger Log(HttpContext context) => context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DenpaHub.Niconico");

    static Task Text(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(message);
    }

    static Task Json(HttpContext context, int status, string error)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync($$"""{"error":"{{error}}"}""");
    }
}
