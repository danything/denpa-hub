using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;

namespace DenpaHub.Features.Jikkyo;

/// <summary>
/// 実況のコメントの中継。NX-Jikkyo のコメントを局ごとに 1 本で受けて各家庭の denpa に配り、過去ログを取得してまとめて持ち、
/// hub の独自コメントを受けて保存し、配り、NX-Jikkyo にも書く。契約は docs/jikkyo.md
/// </summary>
public static partial class JikkyoFeature
{
    /// <summary>書き込みの本文の上限 (JSON 全体)。60 文字の本文と色などが収まる</summary>
    const long MaxBody = 4 * 1024;
    /// <summary>1 つのトークンで同時に開ける購読の数。denpa は局ごとに 1 本</summary>
    public const int MaxStreams = 8;
    /// <summary>購読に何も送るものが無いときに送る間隔。Cloudflare の 100 秒の無通信の切断と、黙って切れた接続を見分けるため</summary>
    public static readonly TimeSpan Ping = TimeSpan.FromSeconds(15);
    /// <summary>利用者の ID を数える長さ (方針の maxAuthorsPerHome)</summary>
    public static readonly TimeSpan AuthorWindow = TimeSpan.FromDays(1);

    static readonly string[] Colors = ["white", "red", "pink", "orange", "yellow", "green", "cyan", "blue"];
    static readonly string[] Positions = ["ue", "naka", "shita"];
    static readonly string[] Sizes = ["big", "medium", "small"];

    [GeneratedRegex("^jk[0-9]{1,4}$")]
    private static partial Regex ChannelShape();

    [GeneratedRegex("^[A-Za-z0-9_-]{22}$")]
    private static partial Regex AuthorShape();

    public static void Add(IServiceCollection services, JikkyoSettings? settings, JikkyoUpstream? upstream = null)
    {
        if (settings is null) return;
        var userAgent = HubSettings.DefaultUserAgent;
        services.AddSingleton(settings);
        services.AddSingleton(upstream?.Connect ?? NxSocket.Client(userAgent));
        services.AddSingleton(sp => new PolicyHolder(settings));
        services.AddSingleton<JikkyoStore>();
        services.AddSingleton<Identity>();
        services.AddSingleton<HubUsers>();
        services.AddSingleton<LiveHub>();
        services.AddSingleton<NxPoster>();
        services.AddSingleton<JikkyoLimits>();
        services.AddSingleton<Streams>();
        services.AddSingleton<Gaps>();
        services.AddSingleton(sp =>
        {
            var http = new HttpClient(upstream?.Http ?? new SocketsHttpHandler(), disposeHandler: upstream?.Http is null);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            return new Kakolog(settings, http, sp.GetRequiredService<JikkyoStore>(), sp.GetRequiredService<Identity>(),
                sp.GetRequiredService<PolicyHolder>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<Kakolog>>());
        });
        services.AddHostedService<JikkyoWorker>();
    }

    /// <summary>設定が無ければ何も足さない (/jikkyo/* は 404)</summary>
    public static void Map(WebApplication app)
    {
        if (app.Services.GetService<JikkyoSettings>() is null) return;
        var limits = app.Services.GetRequiredService<JikkyoLimits>();
        app.MapPost("/jikkyo/tokens", Limited(limits.Issue, Issue));
        app.MapGet("/jikkyo/channels/{channel}/live", Limited(limits.Live, Live));
        app.MapGet("/jikkyo/channels/{channel}/kakolog", Limited(limits.Kakolog, KakologAsync));
        app.MapPost("/jikkyo/channels/{channel}/comments", Limited(limits.Post, Post));
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
        var token = Get<JikkyoStore>(context).IssueToken(Now(context));
        Log(context).LogInformation("jikkyo token issued");
        context.Response.StatusCode = StatusCodes.Status201Created;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync($$"""{"token":"{{token}}"}""");
    }

    /// <summary>トークンを確かめ、局の形を見る。通らなければ返事を書いて null。<paramref name="listed"/> なら方針の局の一覧にあるかも見る</summary>
    static async Task<(string Home, string Channel)?> Admit(HttpContext context, bool listed)
    {
        var token = BearerToken(context);
        var home = token is null ? null : Get<JikkyoStore>(context).FindToken(token, Now(context));
        if (home is null)
        {
            await Hub.ErrorAsync(context, StatusCodes.Status401Unauthorized, "invalid_token");
            return null;
        }
        var channel = context.Request.RouteValues["channel"] as string ?? "";
        if (!ChannelShape().IsMatch(channel))
        {
            await Hub.ErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request");
            return null;
        }
        if (listed && !Get<PolicyHolder>(context).Current.Channels.Contains(channel))
        {
            await Hub.ErrorAsync(context, StatusCodes.Status400BadRequest, "unknown_channel");
            return null;
        }
        return (home, channel);
    }

    static async Task Live(HttpContext context)
    {
        if (await Admit(context, listed: true) is not { } admitted) return;
        var (home, channel) = admitted;
        long? after = null;
        var afterText = context.Request.Query["after"].ToString();
        if (afterText.Length > 0)
        {
            if (!long.TryParse(afterText, out var a) || a < 0)
            {
                await Hub.ErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request");
                return;
            }
            after = a;
        }
        var streams = Get<Streams>(context);
        if (!streams.Enter(home))
        {
            await Hub.ErrorAsync(context, StatusCodes.Status429TooManyRequests, "too_many_streams");
            return;
        }
        try
        {
            var live = Get<LiveHub>(context);
            using var subscription = live.Subscribe(channel, after, context.Request.Query["epoch"].ToString());
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            var ct = context.RequestAborted;
            var reader = subscription.Reader;
            while (!ct.IsCancellationRequested)
            {
                LiveEvent? message;
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    wait.CancelAfter(Ping);
                    try
                    {
                        message = await reader.ReadAsync(wait.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        message = null;
                    }
                    catch (System.Threading.Channels.ChannelClosedException)
                    {
                        // 局を閉じた (方針の一覧から外した・hub を止める)。denpa が繋ぎ直す
                        return;
                    }
                }
                var text = message switch
                {
                    null => "event: ping\ndata: 1\n\n",
                    LiveEvent.State s => $$"""event: state{{"\n"}}data: {"nx":"{{(s.NxUp ? "up" : "down")}}","epoch":"{{live.Epoch}}"}{{"\n\n"}}""",
                    LiveEvent.Comments c => $"id: {c.Seq}\nevent: comments\ndata: {Encoding.UTF8.GetString(Comment.ToJson(c.List))}\n\n",
                    _ => "",
                };
                await context.Response.WriteAsync(text, ct);
                await context.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // denpa が閉じた
        }
        finally
        {
            streams.Leave(home);
        }
    }

    static async Task KakologAsync(HttpContext context)
    {
        if (await Admit(context, listed: false) is not { } admitted) return;
        var channel = admitted.Channel;
        var q = context.Request.Query;
        var now = Now(context).ToUnixTimeMilliseconds();
        if (!long.TryParse(q["from"].ToString(), out var from) || !long.TryParse(q["to"].ToString(), out var to)
            || from < 0 || from >= to || to - from > Kakolog.MaxSpan || from >= now)
        {
            await Hub.ErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request");
            return;
        }
        // 取得はリクエストが切れても続けてキャッシュに入れるので、待つのだけをやめる
        var (comments, complete) = await Get<Kakolog>(context).GetAsync(channel, from, to, context.RequestAborted);
        context.Response.ContentType = "application/json";
        var body = $$"""{"comments":{{Encoding.UTF8.GetString(Comment.ToJson(comments))}},"complete":{{(complete ? "true" : "false")}}}""";
        await context.Response.WriteAsync(body, context.RequestAborted);
    }

    static async Task Post(HttpContext context)
    {
        if (await Admit(context, listed: true) is not { } admitted) return;
        var (home, channel) = admitted;
        if (!context.Request.HasJsonContentType())
        {
            await Hub.ErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request");
            return;
        }
        var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = MaxBody;

        string? author, text, color, position, size;
        try
        {
            using var json = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            author = Str(root, "author");
            text = Str(root, "text");
            color = Str(root, "color");
            position = Str(root, "position");
            size = Str(root, "size");
        }
        catch (Exception e) when (e is JsonException or BadHttpRequestException or InvalidOperationException)
        {
            await Hub.ErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request");
            return;
        }
        if (author is null || !AuthorShape().IsMatch(author) || text is null
            || (color is not null && !Colors.Contains(color)) || (position is not null && !Positions.Contains(position)) || (size is not null && !Sizes.Contains(size)))
        {
            await Hub.ErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request");
            return;
        }

        var policy = Get<PolicyHolder>(context).Current;
        var store = Get<JikkyoStore>(context);
        var identity = Get<Identity>(context);
        var now = Now(context);
        var stored = identity.Author(home, author);
        if (policy.BlockedHomes.Contains(home) || policy.BlockedAuthors.Contains(stored))
        {
            Log(context).LogInformation("jikkyo post rejected: blocked");
            await Hub.ErrorAsync(context, StatusCodes.Status403Forbidden, "blocked");
            return;
        }
        var (count, known) = store.Authors(home, stored, (now - AuthorWindow).ToUnixTimeMilliseconds());
        if (!known && count >= policy.MaxAuthorsPerHome)
        {
            Log(context).LogInformation("jikkyo post rejected: authors");
            await Hub.ErrorAsync(context, StatusCodes.Status403Forbidden, "blocked");
            return;
        }
        var body = text.Trim();
        if (Refused(body, policy))
        {
            Log(context).LogInformation("jikkyo post rejected: text");
            await Hub.ErrorAsync(context, StatusCodes.Status400BadRequest, "rejected");
            return;
        }
        if (!Get<Gaps>(context).TryTake(stored, now, TimeSpan.FromSeconds(policy.PostGapSeconds)))
        {
            await Hub.ErrorAsync(context, StatusCodes.Status429TooManyRequests, "wait");
            return;
        }

        var id = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(12));
        var at = now.ToUnixTimeMilliseconds();
        var mail = string.Join(' ', new[] { "184", color, position, size }.Where(c => c is not null));
        store.AddComment(new StoredComment(id, channel, at, body, mail, home, stored, false));
        Get<HubUsers>(context).Add(identity.NxUserId(stored), now);
        var live = Get<LiveHub>(context);
        live.Publish(channel, new Comment(at, body, mail, Source.Hub, id));
        Log(context).LogInformation("jikkyo post accepted");

        if (policy.RelayToNx && live.IsUp(channel))
        {
            var poster = Get<NxPoster>(context);
            // 返事を待たずに中継する。結果は denpa に返さない (送り直さない)
            _ = Task.Run(async () =>
            {
                if (await poster.PostAsync(new RelayPost(channel, stored, body, color, position, size), CancellationToken.None))
                    store.MarkRelayed(id);
            });
        }
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync($$"""{"id":"{{id}}","at":{{at}}}""");
    }

    static string? Str(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new JsonException();
    }

    /// <summary>本文の決まり (denpa の post.ts の refusal と同じ。長さと NG ワードは方針から)</summary>
    internal static bool Refused(string body, Policy policy) =>
        body.Length == 0
        || body.Any(c => char.IsControl(c) || c is (char)0x2028 or (char)0x2029)
        || Comment.IsOperator(body)
        || Policy.Graphemes(body) > policy.MaxLength
        || policy.IsNg(body);

    /// <summary>Authorization: Bearer の値。トークンの形 (32 バイトの base64url。43 文字) でなければ null</summary>
    internal static string? BearerToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var token = header[prefix.Length..].Trim();
        return token.Length == 43 && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? token : null;
    }

    static T Get<T>(HttpContext context) where T : notnull => context.RequestServices.GetRequiredService<T>();

    static DateTimeOffset Now(HttpContext context) => Get<TimeProvider>(context).GetUtcNow();

    static ILogger Log(HttpContext context) => Get<ILoggerFactory>(context).CreateLogger("DenpaHub.Jikkyo");
}

/// <summary>テストで差し替える上流 (偽の NX-Jikkyo の WebSocket と、偽の過去ログ API)</summary>
public sealed record JikkyoUpstream(NxConnect? Connect, HttpMessageHandler? Http);

/// <summary>トークン (家) ごとに開いている購読の数</summary>
public sealed class Streams
{
    readonly ConcurrentDictionary<string, int> open = new(StringComparer.Ordinal);

    public bool Enter(string home)
    {
        while (true)
        {
            var now = open.GetValueOrDefault(home);
            if (now >= JikkyoFeature.MaxStreams) return false;
            if (now == 0 ? open.TryAdd(home, 1) : open.TryUpdate(home, now + 1, now)) return true;
        }
    }

    public void Leave(string home)
    {
        while (true)
        {
            var now = open.GetValueOrDefault(home);
            if (now <= 1)
            {
                if (open.TryRemove(new KeyValuePair<string, int>(home, now))) return;
                if (now == 0) return;
            }
            else if (open.TryUpdate(home, now - 1, now)) return;
        }
    }
}

/// <summary>利用者ごとに、最後に書いた時刻 (方針の postGapSeconds)</summary>
public sealed class Gaps
{
    readonly ConcurrentDictionary<string, DateTimeOffset> last = new(StringComparer.Ordinal);

    public bool TryTake(string author, DateTimeOffset now, TimeSpan gap)
    {
        while (true)
        {
            if (!last.TryGetValue(author, out var before))
            {
                if (last.TryAdd(author, now)) return true;
                continue;
            }
            if (now - before < gap) return false;
            if (last.TryUpdate(author, now, before)) return true;
        }
    }

    /// <summary>古いものを捨てる (<see cref="JikkyoWorker"/>)</summary>
    public void Sweep(DateTimeOffset now)
    {
        foreach (var (author, at) in last)
            if (now - at > TimeSpan.FromHours(1)) last.TryRemove(new KeyValuePair<string, DateTimeOffset>(author, at));
    }
}

/// <summary>/jikkyo/* のレート制限。値とわけは docs/jikkyo.md の「レート制限」</summary>
public sealed class JikkyoLimits : IDisposable
{
    readonly List<IDisposable> owned = [];

    public PartitionedRateLimiter<HttpContext> Issue { get; }
    public PartitionedRateLimiter<HttpContext> Live { get; }
    public PartitionedRateLimiter<HttpContext> Kakolog { get; }
    public PartitionedRateLimiter<HttpContext> Post { get; }

    public JikkyoLimits()
    {
        var perClient = Own(ByClient(120, TimeSpan.FromMinutes(1)));
        Issue = Own(PartitionedRateLimiter.CreateChained(perClient, Own(ByClient(5, TimeSpan.FromHours(1))), Own(ByClient(20, TimeSpan.FromDays(1)))));
        Live = Own(PartitionedRateLimiter.CreateChained(perClient, Own(ByToken(30, TimeSpan.FromMinutes(1)))));
        Kakolog = Own(PartitionedRateLimiter.CreateChained(perClient, Own(ByToken(30, TimeSpan.FromMinutes(1)))));
        Post = Own(PartitionedRateLimiter.CreateChained(perClient, Own(ByToken(20, TimeSpan.FromMinutes(1)))));
    }

    T Own<T>(T item) where T : IDisposable
    {
        owned.Add(item);
        return item;
    }

    static PartitionedRateLimiter<HttpContext> ByClient(int permits, TimeSpan window) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(Hub.ClientKey(context), _ => Window(permits, window)));

    /// <summary>トークンごと。トークンそのものを鍵に持たないよう SHA-256 で分ける。トークンの形でないものは接続元で分ける</summary>
    static PartitionedRateLimiter<HttpContext> ByToken(int permits, TimeSpan window) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var token = JikkyoFeature.BearerToken(context);
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
        foreach (var item in owned) item.Dispose();
    }
}

/// <summary>
/// 方針の読み直し (30 秒ごと)・中継の接続の掃除・保存期間を過ぎたものの掃除 (1 時間ごと)。
/// 起動したときに、直近 1 日に書いた利用者の NX-Jikkyo での ID を DB から作る
/// </summary>
public sealed class JikkyoWorker(PolicyHolder policy, JikkyoStore store, LiveHub live, NxPoster poster, HubUsers users, Identity identity, Gaps gaps, TimeProvider time, ILogger<JikkyoWorker> log) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    static readonly TimeSpan CleanupEvery = TimeSpan.FromHours(1);
    bool warned;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            var now = time.GetUtcNow();
            foreach (var author in store.AuthorsSince((now - HubUsers.Span).ToUnixTimeMilliseconds())) users.Add(identity.NxUserId(author), now);
            Apply(policy.Current);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 止めない。戻ってきた写しを見分けられないのは直近に書いた人の分だけで、方針は次の読み直しで当てる
            log.LogError("jikkyo worker start failed ({Type})", e.GetType().Name);
        }
        var cleaned = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Run(ref cleaned);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError("jikkyo worker failed ({Type})", e.GetType().Name);
            }
            await Task.Delay(Tick, time, stoppingToken);
        }
    }

    /// <summary>1 回分 (テストからも呼ぶ)</summary>
    public void Run(ref DateTimeOffset cleaned)
    {
        var now = time.GetUtcNow();
        try
        {
            if (policy.Reload() is { } next)
            {
                log.LogInformation("jikkyo policy reloaded");
                warned = false;
                Apply(next);
            }
        }
        catch (Exception e) when (e is PolicyException or IOException or UnauthorizedAccessException)
        {
            // 前の方針を使い続ける
            if (!warned) log.LogWarning("jikkyo policy not reloaded ({Reason})", e is PolicyException p ? p.Message : e.GetType().Name);
            warned = true;
        }
        poster.Sweep();
        users.Expire(now);
        gaps.Sweep(now);
        if (now - cleaned >= CleanupEvery)
        {
            cleaned = now;
            var (comments, tokens) = store.Cleanup(now, policy.Current.RetentionDays);
            log.LogInformation("jikkyo cleanup: comments={Comments} tokens={Tokens}", comments, tokens);
        }
    }

    void Apply(Policy next)
    {
        var removed = store.Remove(next.RemovedComments);
        if (removed > 0) log.LogInformation("jikkyo comments removed: {Count}", removed);
        live.Apply(next);
    }
}
