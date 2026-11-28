using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DenpaHub.Features.Jikkyo;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DenpaHub.Tests;

/// <summary>
/// 偽の NX-Jikkyo (視聴の WS・コメントの WS) と偽の過去ログ API。やり取りの形は NX-Jikkyo の server/app/routers/websocket.py に合わせ、
/// 投稿者の ID は本物と同じく Cookie <c>NX-User-ID</c> の SHA-1 にする
/// </summary>
sealed class FakeNx : IAsyncDisposable
{
    public static readonly Uri Origin = new("wss://nx.test");
    public static readonly Uri KakologOrigin = new("https://kakolog.test");

    readonly WebApplication app;
    readonly ConcurrentDictionary<WebSocket, string> comments = new();

    /// <summary>視聴の WS を断る (NX-Jikkyo が落ちている)</summary>
    public volatile bool Refuse;
    /// <summary>繋いできた視聴の WS の局と Cookie (無ければ null)</summary>
    public ConcurrentQueue<(string Channel, string? Cookie)> Watches { get; } = new();
    /// <summary>受けた書き込み (Cookie と postComment の data)</summary>
    public ConcurrentQueue<(string? Cookie, JsonElement Data)> Posts { get; } = new();
    /// <summary>繋いだときに遡って返す chat</summary>
    public List<string> Backlog { get; } = [];
    /// <summary>過去ログ API が受けたクエリ</summary>
    public ConcurrentQueue<string> KakologQueries { get; } = new();
    /// <summary>過去ログ API の返事 (packet の中身)。null なら {"error": …}</summary>
    public Func<string, string?> Kakolog { get; set; } = _ => "[]";

    public TestServer Server => app.GetTestServer();

    FakeNx(WebApplication app) => this.app = app;

    public static async Task<FakeNx> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        var nx = new FakeNx(app);
        app.UseWebSockets();
        app.Map("/api/v1/channels/{channel}/ws/watch", (RequestDelegate)nx.WatchAsync);
        app.Map("/api/v1/channels/{channel}/ws/comment", (RequestDelegate)nx.CommentAsync);
        app.MapGet("/api/kakolog/{channel}", (HttpContext context) =>
        {
            nx.KakologQueries.Enqueue($"{context.Request.RouteValues["channel"]}{context.Request.QueryString}");
            var packets = nx.Kakolog(context.Request.QueryString.Value ?? "");
            return Results.Text(packets is null ? """{"error":"broken"}""" : $$"""{"packet":{{packets}}}""", "application/json");
        });
        await app.StartAsync();
        return nx;
    }

    /// <summary>hub に渡す WebSocket の開き方 (本番の ClientWebSocket の代わり)</summary>
    public NxConnect Connect => async (url, cookie, ct) =>
    {
        var client = Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            if (cookie is not null) request.Headers.Cookie = $"NX-User-ID={cookie}";
        };
        return await client.ConnectAsync(url, ct);
    };

    public static string UserIdOf(string cookie) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(cookie)));

    public static string Chat(string text, DateTimeOffset at, string user = "someone", string? mail = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["thread"] = "1",
            ["date"] = at.ToUnixTimeSeconds(),
            ["date_usec"] = at.Millisecond * 1000,
            ["user_id"] = user,
            ["mail"] = mail,
            ["content"] = text,
        });

    /// <summary>コメントの WS に繋いでいる全部へ流す</summary>
    public async Task EmitAsync(string chat)
    {
        foreach (var ws in comments.Keys)
            await ws.SendAsync(Encoding.UTF8.GetBytes($$"""{"chat":{{chat}}}"""), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public int CommentSockets => comments.Count;

    async Task WatchAsync(HttpContext context)
    {
        var channel = (string)context.Request.RouteValues["channel"]!;
        var cookie = context.Request.Cookies["NX-User-ID"];
        Watches.Enqueue((channel, cookie));
        if (Refuse)
        {
            context.Response.StatusCode = 503;
            return;
        }
        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        while (true)
        {
            var text = await NxSocket.ReceiveAsync(ws, context.RequestAborted);
            if (text is null) break;
            using var json = JsonDocument.Parse(text);
            var type = json.RootElement.GetProperty("type").GetString();
            if (type == "startWatching")
            {
                await NxSocket.SendAsync(ws, """{"type":"seat","data":{"keepIntervalSec":30}}""", default);
                await NxSocket.SendAsync(ws, $$$"""{"type":"room","data":{"messageServer":{"uri":"wss://nx.test/api/v1/channels/{{{channel}}}/ws/comment"},"threadId":"1","yourPostKey":"k","vposBaseTime":"{{{DateTimeOffset.UtcNow.AddHours(-1):O}}}"}}""", default);
            }
            else if (type == "postComment")
            {
                var data = json.RootElement.GetProperty("data").Clone();
                Posts.Enqueue((cookie, data));
                // 本物と同じく、コメントの WS へ流してから返事をする (投稿者の ID は Cookie の SHA-1)
                await EmitAsync(Chat(data.GetProperty("text").GetString()!, DateTimeOffset.UtcNow, cookie is null ? "anonymous" : UserIdOf(cookie), "184"));
                await NxSocket.SendAsync(ws, """{"type":"postCommentResult","data":{"chat":{}}}""", default);
            }
        }
    }

    async Task CommentAsync(HttpContext context)
    {
        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        var first = await NxSocket.ReceiveAsync(ws, context.RequestAborted);
        if (first is null) return;
        await NxSocket.SendAsync(ws, """{"thread":{"resultcode":0,"thread":"1"}}""", default);
        foreach (var chat in Backlog.ToList()) await NxSocket.SendAsync(ws, $$"""{"chat":{{chat}}}""", default);
        comments[ws] = "";
        try
        {
            while (await NxSocket.ReceiveAsync(ws, context.RequestAborted) is not null) { }
        }
        catch
        {
            // 閉じられた
        }
        comments.TryRemove(ws, out _);
    }

    public async ValueTask DisposeAsync() => await app.DisposeAsync();
}

/// <summary>実況の中継を入れた hub と、偽の NX-Jikkyo・過去ログ API・方針のファイル</summary>
sealed class JikkyoHarness : IAsyncDisposable
{
    public static readonly string Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public const string Author = "AAAAAAAAAAAAAAAAAAAAAA";

    public required Harness Hub { get; init; }
    public required FakeNx Nx { get; init; }
    public required string PolicyPath { get; init; }
    public HttpClient Server => Hub.Server;
    int nextClient;

    public static string PolicyJson() => """
        {
          "retentionDays": 30,
          "maxLength": 60,
          "postGapSeconds": 3,
          "relayToNx": true,
          "maxAuthorsPerHome": 3,
          "channels": ["jk1", "jk9"],
          "ngWords": ["ねたばれ"],
          "blockedHomes": [],
          "blockedAuthors": [],
          "removedComments": []
        }
        """;

    public static async Task<JikkyoHarness> StartAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"denpa-hub-jikkyo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var policyPath = Path.Combine(directory, "policy.json");
        await File.WriteAllTextAsync(policyPath, PolicyJson());
        var nx = await FakeNx.StartAsync();
        var hub = await Harness.StartAsync(
            new() { ["JIKKYO_DATA"] = Path.Combine(directory, "jikkyo.db"), ["JIKKYO_KEY"] = Key, ["JIKKYO_POLICY"] = policyPath },
            new JikkyoUpstream(nx.Connect, nx.Server.CreateHandler()),
            FakeNx.Origin,
            FakeNx.KakologOrigin);
        return new JikkyoHarness { Hub = hub, Nx = nx, PolicyPath = policyPath };
    }

    /// <summary>方針を書き換えて、読み直しを 1 回動かす</summary>
    public async Task SetPolicyAsync(string json)
    {
        await File.WriteAllTextAsync(PolicyPath, json);
        File.SetLastWriteTimeUtc(PolicyPath, DateTime.UtcNow.AddSeconds(Interlocked.Increment(ref nextClient)));
        var worker = Hub.App.Services.GetServices<IHostedService>().OfType<JikkyoWorker>().Single();
        var cleaned = DateTimeOffset.MaxValue;
        worker.Run(ref cleaned);
    }

    /// <summary>トークンを発行する (接続元ごとの発行の上限に当たらないよう、毎回違う接続元から)</summary>
    public async Task<string> IssueAsync()
    {
        var client = Interlocked.Increment(ref nextClient);
        var context = await Hub.App.GetTestServer().SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/jikkyo/tokens";
            c.Connection.RemoteIpAddress = IPAddress.Parse($"198.51.100.{client % 250 + 1}");
        });
        if (context.Response.StatusCode != StatusCodes.Status201Created) throw new InvalidOperationException($"issue: {context.Response.StatusCode}");
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        return json.RootElement.GetProperty("token").GetString()!;
    }

    public async Task<HttpResponseMessage> PostAsync(string token, string text, string channel = "jk1", string author = Author, string extra = "")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/jikkyo/channels/{channel}/comments")
        {
            Content = new StringContent($$"""{"author":"{{author}}","text":{{JsonSerializer.Serialize(text)}}{{extra}}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await Server.SendAsync(request);
    }

    public async Task<HttpResponseMessage> GetAsync(string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await Server.SendAsync(request);
    }

    /// <summary>購読を開く</summary>
    public async Task<Sse> SubscribeAsync(string token, string channel = "jk1", string query = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/jikkyo/channels/{channel}/live{query}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await Server.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return new Sse(response);
    }

    public async ValueTask DisposeAsync()
    {
        await Hub.DisposeAsync();
        await Nx.DisposeAsync();
    }
}

/// <summary>SSE を 1 件ずつ読む</summary>
sealed class Sse(HttpResponseMessage response) : IDisposable
{
    readonly StreamReader? reader = response.IsSuccessStatusCode ? new StreamReader(response.Content.ReadAsStream()) : null;
    public HttpResponseMessage Response { get; } = response;

    /// <summary>次のイベント (名前・data・id)。ping は飛ばす</summary>
    public async Task<(string Event, string Data, string? Id)> NextAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            string? name = null, data = null, id = null;
            while (true)
            {
                var line = await reader!.ReadLineAsync(cts.Token) ?? throw new EndOfStreamException();
                if (line.Length == 0) break;
                if (line.StartsWith("event: ")) name = line[7..];
                else if (line.StartsWith("data: ")) data = line[6..];
                else if (line.StartsWith("id: ")) id = line[4..];
            }
            if (name is null || name == "ping") continue;
            return (name, data ?? "", id);
        }
    }

    /// <summary>次の comments のイベントの中身</summary>
    public async Task<JsonElement[]> CommentsAsync(TimeSpan? timeout = null)
    {
        while (true)
        {
            var (name, data, _) = await NextAsync(timeout);
            if (name != "comments") continue;
            using var json = JsonDocument.Parse(data);
            return [.. json.RootElement.EnumerateArray().Select(e => e.Clone())];
        }
    }

    public void Dispose()
    {
        reader?.Dispose();
        Response.Dispose();
    }
}
