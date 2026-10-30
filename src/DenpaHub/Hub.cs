using DenpaHub.Features.Niconico;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace DenpaHub;

/// <summary>
/// サーバの組み立て。機能をまたぐもの (ログの絞り・応答のヘッダ・プロキシ・レート制限の断り方・/healthz) だけをここに置き、
/// 各機能は Features/&lt;名前&gt;/ の Add と Map で足す
/// </summary>
public static class Hub
{
    public static WebApplication Build(
        WebApplicationBuilder builder,
        HubSettings hub,
        NiconicoSettings niconico,
        HttpMessageHandler? upstream = null,
        TimeProvider? time = null)
    {
        // ASP.NET Core と HttpClient のログは URL (OAuth のコードや state を含む) を書くので、警告より下は出さない
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

        builder.Services.AddSingleton(hub);
        builder.Services.AddSingleton(time ?? TimeProvider.System);
        // 各機能は名前つきの方針を足す。ここは断ったときの返事だけを決める
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (context, ct) =>
            {
                var http = context.HttpContext;
                http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DenpaHub")
                    .LogWarning("rate limited: {Path}", http.Request.Path.Value);
                http.Response.ContentType = "text/plain; charset=utf-8";
                await http.Response.WriteAsync("時間をおいてやり直してください。", ct);
            };
        });
        if (hub.TrustedProxies.Count > 0)
        {
            builder.Services.Configure<ForwardedHeadersOptions>(o =>
            {
                o.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
                o.KnownProxies.Clear();
                o.KnownIPNetworks.Clear();
                foreach (var network in hub.TrustedProxies) o.KnownIPNetworks.Add(network);
            });
        }

        NiconicoFeature.Add(builder.Services, niconico, upstream);

        var app = builder.Build();
        if (hub.TrustedProxies.Count > 0) app.UseForwardedHeaders();
        // 画面は持たない。返すのは JSON・短い文・リダイレクトだけなので、どれにも同じ締めたヘッダを付ける
        app.Use((context, next) =>
        {
            var headers = context.Response.Headers;
            headers.CacheControl = "no-store";
            headers["Referrer-Policy"] = "no-referrer";
            headers.XContentTypeOptions = "nosniff";
            headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            return next(context);
        });
        app.UseRateLimiter();

        app.MapGet("/healthz", (RequestDelegate)(context => context.Response.WriteAsync("ok")));
        NiconicoFeature.Map(app);
        return app;
    }

    /// <summary>レート制限を分ける単位。TRUSTED_PROXIES があれば X-Forwarded-For から取った利用者のアドレス</summary>
    public static string ClientKey(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
