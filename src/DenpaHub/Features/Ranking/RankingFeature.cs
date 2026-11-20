using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;

namespace DenpaHub.Features.Ranking;

/// <summary>
/// 予約数ランキング。参加した denpa の予約を放送ごとに数え、よく予約されている番組の順位を、送ったトークンだけに返す。
/// 契約は docs/ranking.md。地上波の同時ネットは、地域をまたいで 1 つの番組にまとめて数える (<see cref="Grouping"/>)
/// </summary>
public static class RankingFeature
{
    /// <summary>GET /ranking を返す、最後に送ってからの長さ。止めていた家が戻ってすぐ見られなくならないように長めにする</summary>
    public static readonly TimeSpan ViewWindow = TimeSpan.FromDays(7);
    /// <summary>送らなければトークンを消す長さ。見る権利より長くし、取り直しを減らす</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(30);

    public const int DefaultLimit = 50;

    /// <summary>本文の上限。add の 2000 件 (1 件 300 バイトほど) と remove の 5000 件が収まる</summary>
    const long MaxBody = 1024 * 1024;

    public static void Add(IServiceCollection services, RankingSettings? settings)
    {
        if (settings is null) return;
        services.AddSingleton(settings);
        services.AddSingleton<RankingStore>();
        services.AddSingleton<RankingBoard>();
        services.AddSingleton<RankingLimits>();
        services.AddHostedService<RankingWorker>();
    }

    /// <summary>設定が無ければ何も足さない (/ranking/* は 404)</summary>
    public static void Map(WebApplication app)
    {
        if (app.Services.GetService<RankingSettings>() is null) return;
        var limits = app.Services.GetRequiredService<RankingLimits>();
        app.MapPost("/ranking/tokens", Limited(limits.Issue, Issue));
        app.MapDelete("/ranking/tokens/self", Limited(limits.Send, Revoke));
        app.MapPost("/ranking/votes", Limited(limits.Send, Votes));
        app.MapGet("/ranking", Limited(limits.View, View));
    }

    static RequestDelegate Limited(PartitionedRateLimiter<HttpContext> limiter, RequestDelegate next) => async context =>
    {
        using var lease = await limiter.AcquireAsync(context, 1, context.RequestAborted);
        if (!lease.IsAcquired)
        {
            await Hub.RejectAsync(context, context.RequestAborted);
            return;
        }
        await next(context);
    };

    static async Task Issue(HttpContext context)
    {
        var token = Store(context).IssueToken(Now(context));
        Log(context).LogInformation("ranking token issued");
        context.Response.StatusCode = StatusCodes.Status201Created;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync($$"""{"token":"{{token}}"}""");
    }

    static async Task Revoke(HttpContext context)
    {
        var token = BearerToken(context);
        if (token is null || !Store(context).DeleteToken(token))
        {
            await Hub.ErrorAsync(context, StatusCodes.Status401Unauthorized, "invalid_token");
            return;
        }
        Log(context).LogInformation("ranking token revoked");
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    static async Task Votes(HttpContext context)
    {
        var store = Store(context);
        var token = BearerToken(context);
        if (token is null || store.FindToken(token) is null)
        {
            await Hub.ErrorAsync(context, StatusCodes.Status401Unauthorized, "invalid_token");
            return;
        }
        if (!context.Request.HasJsonContentType())
        {
            await Hub.ErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request");
            return;
        }
        var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = MaxBody;

        var now = Now(context);
        VoteRequest.Parsed? parsed;
        string? error;
        try
        {
            using var json = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            parsed = VoteRequest.Parse(json.RootElement, now, out error);
        }
        catch (Exception e) when (e is JsonException or BadHttpRequestException)
        {
            parsed = null;
            error = "invalid_request";
        }
        if (parsed is null)
        {
            Log(context).LogInformation("ranking votes rejected: {Error}", error);
            await Hub.ErrorAsync(context, StatusCodes.Status400BadRequest, error!);
            return;
        }

        if (!store.ApplyVotes(token, parsed.Add, parsed.Remove, now))
        {
            await Hub.ErrorAsync(context, StatusCodes.Status401Unauthorized, "invalid_token");
            return;
        }
        var accepted = parsed.Add.Count + parsed.Remove.Count;
        // 件数だけを書く。トークン・番組・接続元は書かない
        Log(context).LogInformation("ranking votes: add={Add} remove={Remove} rejected={Rejected}", parsed.Add.Count, parsed.Remove.Count, parsed.Rejected);
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync($$"""{"accepted":{{accepted}},"rejected":{{parsed.Rejected}}}""");
    }

    static async Task View(HttpContext context)
    {
        var token = BearerToken(context);
        var record = token is null ? null : Store(context).FindToken(token);
        if (record is null)
        {
            await Hub.ErrorAsync(context, StatusCodes.Status401Unauthorized, "invalid_token");
            return;
        }
        // 参加していない denpa には見せない。送ったのが 7 日より前なら、送ってから見直してもらう
        if (record.SentAt is not { } sentAt || DateTimeOffset.FromUnixTimeSeconds(sentAt) + ViewWindow < Now(context))
        {
            await Hub.ErrorAsync(context, StatusCodes.Status403Forbidden, "inactive");
            return;
        }
        var limitText = context.Request.Query["limit"].ToString();
        var limit = DefaultLimit;
        if (limitText.Length > 0 && (!int.TryParse(limitText, out limit) || limit is < 1 or > RankingBoard.MaxEntries))
        {
            await Hub.ErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request");
            return;
        }
        var body = context.RequestServices.GetRequiredService<RankingBoard>().Write(limit);
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(body, context.RequestAborted);
    }

    /// <summary>Authorization: Bearer の値。トークンの形 (32 バイトの base64url。43 文字) でなければ null</summary>
    internal static string? BearerToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var token = header[prefix.Length..].Trim();
        return token.Length == 43 && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? token : null;
    }

    static RankingStore Store(HttpContext context) => context.RequestServices.GetRequiredService<RankingStore>();

    static DateTimeOffset Now(HttpContext context) => context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();

    static ILogger Log(HttpContext context) => context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DenpaHub.Ranking");
}

/// <summary>
/// /ranking/* のレート制限。どれも固定の枠で数える。値とわけは docs/ranking.md の「レート制限」。
/// 接続元ごとの合計の枠と、エンドポイントごとの枠を両方通ったものだけを通す
/// </summary>
public sealed class RankingLimits : IDisposable
{
    readonly PartitionedRateLimiter<HttpContext> perClient;
    readonly PartitionedRateLimiter<HttpContext> issueHour;
    readonly PartitionedRateLimiter<HttpContext> issueDay;
    readonly PartitionedRateLimiter<HttpContext> sendPerToken;
    readonly PartitionedRateLimiter<HttpContext> viewPerToken;

    /// <summary>トークンの発行。1 つの家は一度だけ。CGNAT で多くの家が 1 つのアドレスを共有しうるので、1 回にはしない</summary>
    public PartitionedRateLimiter<HttpContext> Issue { get; }
    /// <summary>票の送信とトークンの取り消し。1 つの家は 6 時間に 1 回なので、失敗したときの再試行の分を見込む</summary>
    public PartitionedRateLimiter<HttpContext> Send { get; }
    /// <summary>順位。人が検索の画面を開いたときと並べ替えたとき (denpa のサーバは 10 分ためておく)</summary>
    public PartitionedRateLimiter<HttpContext> View { get; }

    public RankingLimits()
    {
        perClient = ByClient(120, TimeSpan.FromMinutes(1));
        issueHour = ByClient(5, TimeSpan.FromHours(1));
        issueDay = ByClient(20, TimeSpan.FromDays(1));
        sendPerToken = ByToken(4, TimeSpan.FromMinutes(1));
        viewPerToken = ByToken(30, TimeSpan.FromMinutes(1));
        Issue = PartitionedRateLimiter.CreateChained(perClient, issueHour, issueDay);
        Send = PartitionedRateLimiter.CreateChained(perClient, sendPerToken);
        View = PartitionedRateLimiter.CreateChained(perClient, viewPerToken);
    }

    static PartitionedRateLimiter<HttpContext> ByClient(int permits, TimeSpan window) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(Hub.ClientKey(context), _ => Window(permits, window)));

    /// <summary>
    /// トークンごと。トークンそのものを鍵に持たないよう、SHA-256 で分ける。トークンの形でないものは接続元で分ける
    /// (認証で断られるが、そこまでの回数は抑える)
    /// </summary>
    static PartitionedRateLimiter<HttpContext> ByToken(int permits, TimeSpan window) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var token = RankingFeature.BearerToken(context);
            var key = token is null ? "client " + Hub.ClientKey(context) : Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
            return RateLimitPartition.GetFixedWindowLimiter(key, _ => Window(permits, window));
        });

    static FixedWindowRateLimiterOptions Window(int permits, TimeSpan window) => new()
    {
        PermitLimit = permits,
        Window = window,
        QueueLimit = 0,
    };

    public void Dispose()
    {
        Issue.Dispose();
        Send.Dispose();
        View.Dispose();
        perClient.Dispose();
        issueHour.Dispose();
        issueDay.Dispose();
        sendPerToken.Dispose();
        viewPerToken.Dispose();
    }
}
