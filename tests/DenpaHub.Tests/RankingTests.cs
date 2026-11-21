using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DenpaHub.Features.Ranking;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DenpaHub.Tests;

/// <summary>ランキングを入れた中継と、SQLite のファイル (テストごとに一時の場所)</summary>
sealed class RankingHarness : IAsyncDisposable
{
    public static readonly string MarkKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public required Harness Hub { get; init; }
    public required string DataPath { get; init; }
    public HttpClient Server => Hub.Server;
    public ManualTime Time => Hub.Time;
    public RankingStore Store => Hub.App.Services.GetRequiredService<RankingStore>();
    int nextClient;

    public static async Task<RankingHarness> StartAsync(string? dataPath = null)
    {
        var path = dataPath ?? Path.Combine(Path.GetTempPath(), $"denpa-hub-ranking-{Guid.NewGuid():N}", "ranking.db");
        var hub = await Harness.StartAsync(new() { ["RANKING_DATA"] = path, ["RANKING_MARK_KEY"] = MarkKey });
        return new RankingHarness { Hub = hub, DataPath = path };
    }

    /// <summary>
    /// トークンを発行する。発行のレート制限 (接続元ごとに 5 回/時) に当たらないよう、毎回違う接続元から送る
    /// </summary>
    public async Task<string> IssueAsync()
    {
        var client = Interlocked.Increment(ref nextClient);
        var context = await Hub.App.GetTestServer().SendAsync(c =>
        {
            c.Request.Method = "POST";
            c.Request.Path = "/ranking/tokens";
            c.Connection.RemoteIpAddress = IPAddress.Parse($"198.51.100.{client}");
        });
        if (context.Response.StatusCode != StatusCodes.Status201Created) throw new InvalidOperationException($"issue: {context.Response.StatusCode}");
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        return json.RootElement.GetProperty("token").GetString()!;
    }

    /// <summary>
    /// 送る放送 1 件。<paramref name="kinds"/> を省くと、今の denpa と同じ形 (<c>kind</c> だけ。予約として受ける) で送る
    /// </summary>
    public static JsonObject Airing(int eventId, DateTimeOffset start, string kind = "manual", string name = "例の番組 #7", int networkId = 4, int serviceId = 211,
        int duration = 1800, string serviceName = "BS11イレブン", int[]? genres = null, string[]? kinds = null)
    {
        var airing = new JsonObject
        {
            ["network_id"] = networkId,
            ["service_id"] = serviceId,
            ["event_id"] = eventId,
            ["start_at"] = start.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            ["duration"] = duration,
            ["name"] = name,
            ["service_name"] = serviceName,
            ["genres"] = new JsonArray([.. (genres ?? [7]).Select(g => (JsonNode)g)]),
        };
        if (kinds is null) airing["kind"] = kind;
        else airing["kinds"] = new JsonArray([.. kinds.Select(k => (JsonNode)k)]);
        return airing;
    }

    /// <summary>集計の表に移す処理と順位を作り直す処理を、毎時 0 分と同じ順に動かす</summary>
    public void Tick()
    {
        Store.Cleanup(Time.Now);
        Hub.App.Services.GetRequiredService<RankingBoard>().Rebuild();
    }

    /// <summary>集計の表を、読み出す処理 (<c>denpa-hub tallies</c>) と同じ形で読む</summary>
    public JsonElement[] ReadTallies(DateOnly? from = null, DateOnly? to = null)
    {
        using var buffer = new MemoryStream();
        Tallies.Write(DataPath, from, to, buffer);
        using var json = JsonDocument.Parse(buffer.ToArray());
        return [.. json.RootElement.GetProperty("tallies").EnumerateArray().Select(e => e.Clone())];
    }

    public static JsonObject Id(int eventId, int networkId = 4, int serviceId = 211) => new()
    {
        ["network_id"] = networkId,
        ["service_id"] = serviceId,
        ["event_id"] = eventId,
    };

    public Task<HttpResponseMessage> SendAsync(string? token, JsonObject[]? add = null, JsonObject[]? remove = null) =>
        SendRawAsync(token, new JsonObject
        {
            ["v"] = 1,
            ["add"] = new JsonArray([.. (add ?? []).Select(a => (JsonNode)a.DeepClone())]),
            ["remove"] = new JsonArray([.. (remove ?? []).Select(r => (JsonNode)r.DeepClone())]),
        }.ToJsonString());

    public async Task<HttpResponseMessage> SendRawAsync(string? token, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ranking/votes")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await Server.SendAsync(request);
    }

    public async Task<HttpResponseMessage> ViewAsync(string? token, string query = "")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ranking" + query);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await Server.SendAsync(request);
    }

    public async Task<HttpResponseMessage> RevokeAsync(string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/ranking/tokens/self");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await Server.SendAsync(request);
    }

    /// <summary>順位を作り直してから見る (ふだんは 10 分ごと)</summary>
    public async Task<JsonElement> RankingAsync(string token, string query = "")
    {
        Hub.App.Services.GetRequiredService<RankingBoard>().Rebuild();
        using var response = await ViewAsync(token, query);
        if (response.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException($"view: {response.StatusCode}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    /// <summary>同じ放送に、それぞれ新しいトークンで票を入れる。入れたトークンを返す</summary>
    public async Task<List<string>> VoteManyAsync(int count, JsonObject airing)
    {
        var tokens = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var token = await IssueAsync();
            using var response = await SendAsync(token, [airing]);
            if (response.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException($"send: {response.StatusCode}");
            tokens.Add(token);
        }
        return tokens;
    }

    public async ValueTask DisposeAsync()
    {
        await Hub.DisposeAsync();
        try
        {
            Directory.Delete(Path.GetDirectoryName(DataPath)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class RankingTests
{
    static async Task<string?> ErrorOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("error").GetString();
    }

    static async Task<JsonElement> BodyOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    static JsonElement[] Entries(JsonElement ranking) => [.. ranking.GetProperty("entries").EnumerateArray()];

    [Test]
    public async Task 発行したトークンは_43_文字で_送るまでは順位を見られない()
    {
        await using var h = await RankingHarness.StartAsync();
        using var response = await h.Server.PostAsync("/ranking/tokens", null);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(response.Headers.CacheControl!.NoStore).IsTrue();
        var token = (await BodyOf(response)).GetProperty("token").GetString()!;
        await Assert.That(token.Length).IsEqualTo(43);

        using var before = await h.ViewAsync(token);
        await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await ErrorOf(before)).IsEqualTo("inactive");

        // 予約が 0 件の家も、空の送信で見られるようになる
        using var sent = await h.SendAsync(token);
        await Assert.That(sent.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var ranking = await h.RankingAsync(token);
        await Assert.That(ranking.GetProperty("v").GetInt32()).IsEqualTo(1);
        await Assert.That(Entries(ranking)).IsEmpty();
    }

    [Test]
    public async Task 最後に送ってから_7_日を過ぎたトークンは見られない()
    {
        await using var h = await RankingHarness.StartAsync();
        var token = await h.IssueAsync();
        using (var sent = await h.SendAsync(token)) await Assert.That(sent.StatusCode).IsEqualTo(HttpStatusCode.OK);

        h.Time.Now += TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1);
        using (var inside = await h.ViewAsync(token)) await Assert.That(inside.StatusCode).IsEqualTo(HttpStatusCode.OK);

        h.Time.Now += TimeSpan.FromMinutes(2);
        using (var outside = await h.ViewAsync(token))
        {
            await Assert.That(outside.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(await ErrorOf(outside)).IsEqualTo("inactive");
        }

        // 送り直せば、また見られる
        using (var again = await h.SendAsync(token)) await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using (var back = await h.ViewAsync(token)) await Assert.That(back.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Arguments(null)]
    [Arguments("short")]
    [Arguments("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task 知らないトークンは_401(string? token)
    {
        await using var h = await RankingHarness.StartAsync();
        using (var view = await h.ViewAsync(token))
        {
            await Assert.That(view.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(await ErrorOf(view)).IsEqualTo("invalid_token");
        }
        using (var send = await h.SendAsync(token)) await Assert.That(send.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using (var revoke = await h.RevokeAsync(token)) await Assert.That(revoke.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task 票を入れたトークンが_5_未満の番組は出さない()
    {
        await using var h = await RankingHarness.StartAsync();
        var start = h.Time.Now.AddDays(1);
        var airing = RankingHarness.Airing(100, start);
        var tokens = await h.VoteManyAsync(4, airing);
        await Assert.That(Entries(await h.RankingAsync(tokens[0]))).IsEmpty();

        tokens.AddRange(await h.VoteManyAsync(1, airing));
        var entries = Entries(await h.RankingAsync(tokens[0]));
        await Assert.That(entries.Length).IsEqualTo(1);
        var entry = entries[0];
        await Assert.That(entry.GetProperty("rank").GetInt32()).IsEqualTo(1);
        await Assert.That(entry.GetProperty("votes").GetInt32()).IsEqualTo(5);
        await Assert.That(entry.GetProperty("name").GetString()).IsEqualTo("例の番組 #7");
        await Assert.That(entry.GetProperty("affiliation").ValueKind).IsEqualTo(JsonValueKind.Null);
        var airings = entry.GetProperty("airings").EnumerateArray().ToArray();
        await Assert.That(airings.Length).IsEqualTo(1);
        await Assert.That(airings[0].GetProperty("event_id").GetInt32()).IsEqualTo(100);
        await Assert.That(airings[0].GetProperty("service_name").GetString()).IsEqualTo("BS11イレブン");
        await Assert.That(airings[0].GetProperty("start_at").GetString())
            .IsEqualTo(start.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd'T'HH:mm:sszzz"));
        await Assert.That(airings[0].GetProperty("region").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task 同じトークンから同じ放送を何度送っても_1_票()
    {
        await using var h = await RankingHarness.StartAsync();
        var airing = RankingHarness.Airing(100, h.Time.Now.AddDays(1));
        var tokens = await h.VoteManyAsync(4, airing);
        // 1 回の送信の中で重ねても、送信を分けても数は増えない
        using (var twice = await h.SendAsync(tokens[0], [airing, airing])) await Assert.That(twice.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using (var again = await h.SendAsync(tokens[0], [airing])) await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Entries(await h.RankingAsync(tokens[0]))).IsEmpty();
        await Assert.That(h.Store.Count("marks")).IsEqualTo(4);
    }

    [Test]
    public async Task remove_で票を取り消せる()
    {
        await using var h = await RankingHarness.StartAsync();
        var airing = RankingHarness.Airing(100, h.Time.Now.AddDays(1));
        var tokens = await h.VoteManyAsync(5, airing);
        await Assert.That(Entries(await h.RankingAsync(tokens[0])).Length).IsEqualTo(1);

        using (var removed = await h.SendAsync(tokens[4], remove: [RankingHarness.Id(100)]))
        {
            await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await BodyOf(removed)).GetProperty("accepted").GetInt32()).IsEqualTo(1);
        }
        await Assert.That(Entries(await h.RankingAsync(tokens[0]))).IsEmpty();
        // 票を入れていない放送の remove は何もしない。ほかのトークンの票は消えない
        var fresh = await h.IssueAsync();
        using (var none = await h.SendAsync(fresh, remove: [RankingHarness.Id(100), RankingHarness.Id(101)])) await Assert.That(none.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(h.Store.Count("marks")).IsEqualTo(4);

        // 取り消したトークンが入れ直せば、また数える
        using (var back = await h.SendAsync(tokens[4], [airing])) await Assert.That(back.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Entries(await h.RankingAsync(tokens[0])).Length).IsEqualTo(1);

        // 全員が取り消すと、放送ごと消える
        foreach (var token in tokens)
        {
            using var response = await h.SendAsync(token, remove: [RankingHarness.Id(100)]);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        await Assert.That(h.Store.Count("airings")).IsEqualTo(0);
        await Assert.That(h.Store.Count("labels")).IsEqualTo(0);
        await Assert.That(h.Store.Count("marks")).IsEqualTo(0);
    }

    [Test]
    public async Task 予約した家とライブで観た家を_1_家_1_で数え_votes_は_5_の倍数に切り下げる()
    {
        await using var h = await RankingHarness.StartAsync();
        var start = h.Time.Now.AddDays(1);
        // 予約 7 = 7 → 5 (手動もルールも同じ 1)
        await h.VoteManyAsync(4, RankingHarness.Airing(1, start, kind: "manual"));
        await h.VoteManyAsync(3, RankingHarness.Airing(1, start, kind: "rule"));
        // 予約 10 = 10
        var tokens = await h.VoteManyAsync(10, RankingHarness.Airing(2, start, kinds: ["reserved"]));
        // 4 → 0 なので出さない
        await h.VoteManyAsync(4, RankingHarness.Airing(3, start));

        var entries = Entries(await h.RankingAsync(tokens[0]));
        await Assert.That(string.Join(" ", entries.Select(e => $"{e.GetProperty("rank")}:{e.GetProperty("votes")}:{e.GetProperty("airings")[0].GetProperty("event_id")}")))
            .IsEqualTo("1:10:2 2:5:1");
    }

    [Test]
    public async Task 終わった番組は予約とライブの和集合で数え_録画とタイムシフトは一覧に入れない()
    {
        await using var h = await RankingHarness.StartAsync();
        var start = h.Time.Now.AddHours(-3);
        // 予約して観た家 3、観ただけの家 2 = 5。録っただけ・録画で観ただけの家 (6) は一覧の数に入らない
        await h.VoteManyAsync(3, RankingHarness.Airing(1, start, kinds: ["reserved", "live", "recorded"]));
        await h.VoteManyAsync(2, RankingHarness.Airing(1, start, kinds: ["live"]));
        await h.VoteManyAsync(6, RankingHarness.Airing(2, start, kinds: ["recorded", "timeshift"]));
        var tokens = await h.VoteManyAsync(1, RankingHarness.Airing(3, start, kinds: ["timeshift"]));
        var entries = Entries(await h.RankingAsync(tokens[0]));
        await Assert.That(entries.Length).IsEqualTo(1);
        await Assert.That(entries[0].GetProperty("votes").GetInt32()).IsEqualTo(5);
        var airing = entries[0].GetProperty("airings")[0];
        await Assert.That(airing.GetProperty("end_at").GetString()).IsEqualTo(RankingBoard.Iso(start.AddMinutes(30)));

        // 種類の組を送り直すと置き換える。観ただけの家がタイムシフトだけにすると、一覧から外れる
        var live = await h.VoteManyAsync(1, RankingHarness.Airing(4, start, kinds: ["live"]));
        await h.VoteManyAsync(4, RankingHarness.Airing(4, start, kinds: ["live"]));
        await Assert.That(Entries(await h.RankingAsync(tokens[0])).Length).IsEqualTo(2);
        using (var changed = await h.SendAsync(live[0], [RankingHarness.Airing(4, start, kinds: ["timeshift"])])) await Assert.That(changed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Entries(await h.RankingAsync(tokens[0])).Length).IsEqualTo(1);
    }

    [Test]
    public async Task 録った_観たは放送が終わるまで受けない()
    {
        await using var h = await RankingHarness.StartAsync();
        var token = await h.IssueAsync();
        var onAir = h.Time.Now.AddMinutes(-10);
        using var response = await h.SendAsync(token,
        [
            RankingHarness.Airing(1, onAir, kinds: ["reserved"]),
            RankingHarness.Airing(2, onAir, kinds: ["live"]),
            RankingHarness.Airing(3, onAir, kinds: ["reserved", "recorded"]),
            RankingHarness.Airing(4, onAir, kinds: ["timeshift"]),
            // 終わったあとなら受ける
            RankingHarness.Airing(5, onAir.AddHours(-1), kinds: ["live", "recorded", "timeshift"]),
        ]);
        var body = await BodyOf(response);
        await Assert.That(body.GetProperty("accepted").GetInt32()).IsEqualTo(2);
        await Assert.That(body.GetProperty("rejected").GetInt32()).IsEqualTo(3);
    }

    [Test]
    public async Task 同じ数の番組は放送の順に並べる()
    {
        await using var h = await RankingHarness.StartAsync();
        var start = h.Time.Now.AddDays(1);
        // 切り下げると同じ 5。切り下げる前の数 (9 と 5) では並べない
        await h.VoteManyAsync(5, RankingHarness.Airing(1, start.AddHours(2)));
        var tokens = await h.VoteManyAsync(9, RankingHarness.Airing(2, start.AddHours(3)));
        await h.VoteManyAsync(5, RankingHarness.Airing(3, start.AddHours(1)));
        var entries = Entries(await h.RankingAsync(tokens[0]));
        await Assert.That(string.Join(" ", entries.Select(e => $"{e.GetProperty("rank")}:{e.GetProperty("airings")[0].GetProperty("event_id")}"))).IsEqualTo("1:3 2:1 3:2");

        var limited = Entries(await h.RankingAsync(tokens[0], "?limit=2"));
        await Assert.That(limited.Length).IsEqualTo(2);
        using (var bad = await h.ViewAsync(tokens[0], "?limit=301")) await Assert.That(bad.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using (var zero = await h.ViewAsync(tokens[0], "?limit=0")) await Assert.That(zero.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task 番組名は票を入れたトークンの多いものを出し_書き換えると前の名前の票を外す()
    {
        await using var h = await RankingHarness.StartAsync();
        var start = h.Time.Now.AddDays(1);
        var tokens = await h.VoteManyAsync(3, RankingHarness.Airing(1, start, name: "本当の名前"));
        var others = await h.VoteManyAsync(2, RankingHarness.Airing(1, start, name: "書き換えた名前"));
        await Assert.That(Entries(await h.RankingAsync(tokens[0]))[0].GetProperty("name").GetString()).IsEqualTo("本当の名前");

        // 1 つのトークンが名前を変えて送り直すと、前の名前から 1 つ外して新しい名前に足す (2 対 3 で入れ替わる)
        using (var renamed = await h.SendAsync(tokens[0], [RankingHarness.Airing(1, start, name: "書き換えた名前")])) await Assert.That(renamed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var entry = Entries(await h.RankingAsync(tokens[0]))[0];
        await Assert.That(entry.GetProperty("name").GetString()).IsEqualTo("書き換えた名前");
        await Assert.That(entry.GetProperty("votes").GetInt32()).IsEqualTo(5);

        // 時刻の書き換え (延長など) も同じ。多いほうの時刻を出す
        var later = start.AddMinutes(30);
        foreach (var token in tokens.Concat(others).Take(3))
        {
            using var response = await h.SendAsync(token, [RankingHarness.Airing(1, later, name: "書き換えた名前")]);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        var moved = Entries(await h.RankingAsync(tokens[0]))[0];
        await Assert.That(moved.GetProperty("airings")[0].GetProperty("start_at").GetString())
            .IsEqualTo(later.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd'T'HH:mm:sszzz"));
        await Assert.That(h.Store.Count("labels")).IsEqualTo(5);
    }

    [Test]
    public async Task 一覧には終わって_24_時間までを出し_終わって_8_日たったら数を決めて集計の表に移す()
    {
        await using var h = await RankingHarness.StartAsync();
        var start = h.Time.Now.AddHours(1);
        var end = start.AddHours(1);
        var tokens = await h.VoteManyAsync(5, RankingHarness.Airing(1, start, duration: 3600, kinds: ["reserved"]));
        await Assert.That(Entries(await h.RankingAsync(tokens[0])).Length).IsEqualTo(1);

        // 放送中も、終わって 24 時間までも出す
        h.Time.Now = start.AddMinutes(1);
        using (var sent = await h.SendAsync(tokens[0])) await Assert.That(sent.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Entries(await h.RankingAsync(tokens[0])).Length).IsEqualTo(1);
        h.Time.Now = end.AddHours(1);
        // 終わったあとに、録った・録画で観たを足す (3 家)。ほかに観ただけの家が 7
        foreach (var token in tokens.Take(3))
        {
            using var response = await h.SendAsync(token, [RankingHarness.Airing(1, start, duration: 3600, kinds: ["reserved", "recorded", "timeshift"])]);
            await Assert.That((await BodyOf(response)).GetProperty("accepted").GetInt32()).IsEqualTo(1);
        }
        await h.VoteManyAsync(7, RankingHarness.Airing(1, start, duration: 3600, kinds: ["live"]));
        await Assert.That(Entries(await h.RankingAsync(tokens[0]))[0].GetProperty("votes").GetInt32()).IsEqualTo(10);
        h.Time.Now = end.AddHours(24).AddMinutes(1);
        await Assert.That(Entries(await h.RankingAsync(tokens[0]))).IsEmpty();

        // 8 日たつまでは票を受け、数は決めない
        h.Time.Now = end.AddDays(8).AddMinutes(-1);
        h.Tick();
        await Assert.That(h.Store.Count("airings")).IsEqualTo(1);
        await Assert.That(h.ReadTallies()).IsEmpty();

        h.Time.Now = end.AddDays(8).AddMinutes(1);
        h.Tick();
        await Assert.That(h.Store.Count("airings")).IsEqualTo(0);
        await Assert.That(h.Store.Count("labels")).IsEqualTo(0);
        // 8 日を過ぎた放送の add はもう受けない
        using (var late = await h.SendAsync(tokens[4], [RankingHarness.Airing(1, start, duration: 3600, kinds: ["timeshift"])]))
            await Assert.That((await BodyOf(late)).GetProperty("rejected").GetInt32()).IsEqualTo(1);

        var tallies = h.ReadTallies();
        await Assert.That(tallies.Length).IsEqualTo(1);
        var tally = tallies[0];
        await Assert.That(tally.GetProperty("name").GetString()).IsEqualTo("例の番組 #7");
        // 一覧の数 12 → 10、録った家 3 → 0、ライブで観た家 7 → 5、タイムシフト 3 → 0
        await Assert.That($"{tally.GetProperty("homes")} {tally.GetProperty("recorded")} {tally.GetProperty("live")} {tally.GetProperty("timeshift")}").IsEqualTo("10 0 5 0");
        await Assert.That(tally.GetProperty("start_at").GetString()).IsEqualTo(RankingBoard.Iso(start));
        await Assert.That(tally.GetProperty("airings")[0].GetProperty("event_id").GetInt32()).IsEqualTo(1);
        // 集計の表には、トークンにつながるもの (印・トークンの数) を持たない
        await Assert.That(tally.TryGetProperty("tokens", out _)).IsFalse();

        // 日付で絞れる (日本時間の放送の始まり)
        var day = DateOnly.FromDateTime(start.ToOffset(TimeSpan.FromHours(9)).DateTime);
        await Assert.That(h.ReadTallies(day, day).Length).IsEqualTo(1);
        await Assert.That(h.ReadTallies(day.AddDays(1)).Length).IsEqualTo(0);

        // 印は、票を受けなくなった日の翌日の日付を過ぎたら消す。集計は 400 日で消す
        h.Time.Now = end.AddDays(10);
        h.Tick();
        await Assert.That(h.Store.Count("marks")).IsEqualTo(0);
        await Assert.That(h.Store.Count("tallies")).IsEqualTo(1);
        h.Time.Now = start + RankingStore.TallyLifetime + TimeSpan.FromMinutes(1);
        h.Tick();
        await Assert.That(h.Store.Count("tallies")).IsEqualTo(0);
    }

    [Test]
    public async Task 地上波の同時ネットは地域をまたいでまとめて集計し_4_つとも_5_未満の番組は持たない()
    {
        await using var h = await RankingHarness.StartAsync();
        var start = h.Time.Now.AddHours(-2);
        // TBS 系の関東広域と、近畿広域の MBS (同じ時刻・同じ名前)。どちらも 3 家だが、まとめると 6
        await h.VoteManyAsync(3, RankingHarness.Airing(1, start, networkId: GroupingTests.Kanto, serviceId: GroupingTests.TbsKanto, serviceName: "TBS", name: "日曜劇場 例のドラマ 第3話", kinds: ["live"]));
        await h.VoteManyAsync(3, RankingHarness.Airing(2, start, networkId: GroupingTests.Kinki, serviceId: GroupingTests.MbsKinki, serviceName: "MBS", name: "日曜劇場 例のドラマ 第3話", kinds: ["live"]));
        // BS の 4 家だけの番組は 4 つとも 0 になるので持たない
        await h.VoteManyAsync(4, RankingHarness.Airing(3, start, kinds: ["live", "recorded", "timeshift"]));
        h.Time.Now = start.AddDays(9);
        h.Tick();
        var tallies = h.ReadTallies();
        await Assert.That(tallies.Length).IsEqualTo(1);
        await Assert.That(tallies[0].GetProperty("affiliation").GetString()).IsEqualTo("TBS 系");
        await Assert.That(tallies[0].GetProperty("live").GetInt32()).IsEqualTo(5);
        await Assert.That(tallies[0].GetProperty("airings").GetArrayLength()).IsEqualTo(2);
        await Assert.That(h.Store.Count("airings")).IsEqualTo(0);
    }

    [Test]
    public async Task 集計を読み出すコマンドは引数を確かめて_JSON_を標準出力に書く()
    {
        await using var h = await RankingHarness.StartAsync();
        var settings = new RankingSettings(h.DataPath, Convert.FromBase64String(RankingHarness.MarkKey));
        using (var output = new StringWriter())
        using (var error = new StringWriter())
        {
            await Assert.That(Tallies.Run(["tallies", "--from", "2026-11-01", "--to", "2026-11-30"], settings, output, error)).IsEqualTo(0);
            using var json = JsonDocument.Parse(output.ToString());
            await Assert.That(json.RootElement.GetProperty("tallies").GetArrayLength()).IsEqualTo(0);
        }
        foreach (string[] args in new[] { new[] { "tallies", "--from" }, ["tallies", "--from", "2026/11/01"], ["tallies", "--since", "2026-11-01"] })
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            await Assert.That(Tallies.Run(args, settings, output, error)).IsEqualTo(2);
            await Assert.That(error.ToString()).Contains("使い方");
        }
        using (var output = new StringWriter())
        using (var error = new StringWriter())
            await Assert.That(Tallies.Run(["tallies"], null, output, error)).IsEqualTo(2);
    }

    [Test]
    public async Task 順位は毎時_0_分に作り直す()
    {
        await Assert.That(RankingWorker.UntilNext(DateTimeOffset.Parse("2026-11-21T09:59:30Z"))).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(RankingWorker.UntilNext(DateTimeOffset.Parse("2026-11-21T10:00:00Z"))).IsEqualTo(TimeSpan.FromHours(1));
        await Assert.That(RankingWorker.UntilNext(DateTimeOffset.Parse("2026-11-21T23:15:00Z"))).IsEqualTo(TimeSpan.FromMinutes(45));
    }

    [Test]
    public async Task 取り消したトークンでは見られず送れない()
    {
        await using var h = await RankingHarness.StartAsync();
        var token = await h.IssueAsync();
        using (var sent = await h.SendAsync(token)) await Assert.That(sent.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using (var revoked = await h.RevokeAsync(token)) await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using (var view = await h.ViewAsync(token)) await Assert.That(view.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using (var send = await h.SendAsync(token)) await Assert.That(send.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using (var twice = await h.RevokeAsync(token)) await Assert.That(twice.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task 認証のあとに取り消されたトークンの票は書かない()
    {
        await using var h = await RankingHarness.StartAsync();
        var token = await h.IssueAsync();
        h.Store.DeleteToken(token);
        var vote = new AddVote(new AiringId(4, 211, 1), h.Time.Now.AddDays(1).ToUnixTimeSeconds(), 1800, "例の番組", "BS11イレブン", [7], Kinds.Reserved);
        await Assert.That(h.Store.ApplyVotes(token, [vote], [], h.Time.Now)).IsFalse();
        await Assert.That(h.Store.Count("marks")).IsEqualTo(0);
        await Assert.That(h.Store.Count("airings")).IsEqualTo(0);
    }

    [Test]
    public async Task 送らないまま_30_日たったトークンは消す()
    {
        await using var h = await RankingHarness.StartAsync();
        var quiet = await h.IssueAsync();
        var active = await h.IssueAsync();
        h.Time.Now += TimeSpan.FromDays(20);
        using (var sent = await h.SendAsync(active)) await Assert.That(sent.StatusCode).IsEqualTo(HttpStatusCode.OK);
        h.Time.Now += TimeSpan.FromDays(12);
        h.Store.Cleanup(h.Time.Now);
        await Assert.That(h.Store.FindToken(quiet)).IsNull();
        await Assert.That(h.Store.FindToken(active)).IsNotNull();
    }

    [Test]
    public async Task 中身の通らない予約は数えずに_rejected_に数える()
    {
        await using var h = await RankingHarness.StartAsync();
        var token = await h.IssueAsync();
        var start = h.Time.Now.AddDays(1);
        JsonObject Bad(Action<JsonObject> change)
        {
            var airing = RankingHarness.Airing(1, start);
            change(airing);
            return airing;
        }
        JsonObject[] add =
        [
            RankingHarness.Airing(1, start),
            // ケーブルの自主放送・スカパー！プレミアムの network_id
            RankingHarness.Airing(2, start, networkId: 0xFFFE),
            RankingHarness.Airing(3, start, networkId: 1),
            // 終わって 8 日を過ぎた番組
            RankingHarness.Airing(4, h.Time.Now.AddDays(-8).AddMinutes(-30)),
            RankingHarness.Airing(5, h.Time.Now.AddDays(9).AddMinutes(1)),
            RankingHarness.Airing(6, start, duration: 24 * 60 * 60 + 1),
            RankingHarness.Airing(7, start, name: new string('あ', 121)),
            RankingHarness.Airing(8, start, name: "  "),
            RankingHarness.Airing(9, start, name: "改行\nを含む"),
            RankingHarness.Airing(10, start, serviceName: new string('あ', 41)),
            RankingHarness.Airing(11, start, genres: [16]),
            // 知っている種類が 1 つも無いもの・放送が終わる前の「観た」
            RankingHarness.Airing(12, start, kinds: ["other"]),
            RankingHarness.Airing(14, start, kinds: ["live"]),
            Bad(a => a["start_at"] = "2026-11-20T21:00:00"),
            Bad(a => a.Remove("genres")),
            // 地上波の範囲の端
            RankingHarness.Airing(13, start, networkId: 0x7880, serviceId: 1024, serviceName: "NHK総合1・東京"),
        ];
        using var response = await h.SendAsync(token, add, [RankingHarness.Id(1, networkId: 0xFFFE)]);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await BodyOf(response);
        await Assert.That(body.GetProperty("accepted").GetInt32()).IsEqualTo(2);
        await Assert.That(body.GetProperty("rejected").GetInt32()).IsEqualTo(15);
        await Assert.That(h.Store.Count("airings")).IsEqualTo(2);
    }

    [Test]
    [Arguments("not json")]
    [Arguments("[]")]
    [Arguments("""{"v":1,"add":{}}""")]
    public async Task 全体の形が違う送信は_400(string body)
    {
        await using var h = await RankingHarness.StartAsync();
        var token = await h.IssueAsync();
        using var response = await h.SendRawAsync(token, body);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorOf(response)).IsEqualTo("invalid_request");
        // 断った送信では、最後に送った時刻を書き換えない
        using var view = await h.ViewAsync(token);
        await Assert.That(view.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task バージョンの違う送信と多すぎる送信は断る()
    {
        await using var h = await RankingHarness.StartAsync();
        var token = await h.IssueAsync();
        using (var version = await h.SendRawAsync(token, """{"v":2}"""))
        {
            await Assert.That(version.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await ErrorOf(version)).IsEqualTo("unsupported_version");
        }
        var start = h.Time.Now.AddDays(1);
        var add = Enumerable.Range(1, VoteRequest.MaxAdd + 1).Select(i => RankingHarness.Airing(i, start)).ToArray();
        using (var many = await h.SendAsync(token, add))
        {
            await Assert.That(many.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await ErrorOf(many)).IsEqualTo("too_many_items");
        }
        // 上限ちょうどは受ける
        using (var full = await h.SendAsync(token, add[..VoteRequest.MaxAdd]))
        {
            await Assert.That(full.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await BodyOf(full)).GetProperty("accepted").GetInt32()).IsEqualTo(VoteRequest.MaxAdd);
        }
    }

    [Test]
    public async Task 発行は接続元ごとに_1_時間_5_回まで()
    {
        await using var h = await RankingHarness.StartAsync();
        for (var i = 0; i < 5; i++)
        {
            using var ok = await h.Server.PostAsync("/ranking/tokens", null);
            await Assert.That(ok.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }
        using var limited = await h.Server.PostAsync("/ranking/tokens", null);
        await Assert.That(limited.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(await limited.Content.ReadAsStringAsync()).IsEqualTo("時間をおいてやり直してください。");
    }

    [Test]
    public async Task 送信はトークンごとに_1_分_4_回まで()
    {
        await using var h = await RankingHarness.StartAsync();
        var token = await h.IssueAsync();
        var other = await h.IssueAsync();
        for (var i = 0; i < 4; i++)
        {
            using var ok = await h.SendAsync(token);
            await Assert.That(ok.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        using (var limited = await h.SendAsync(token)) await Assert.That(limited.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        // 取り消しも同じ枠
        using (var revoke = await h.RevokeAsync(token)) await Assert.That(revoke.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        // ほかのトークンは別に数える
        using (var fine = await h.SendAsync(other)) await Assert.That(fine.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task 再起動してもトークンと票は残る()
    {
        var path = Path.Combine(Path.GetTempPath(), $"denpa-hub-ranking-{Guid.NewGuid():N}", "ranking.db");
        var first = await RankingHarness.StartAsync(path);
        var tokens = await first.VoteManyAsync(5, RankingHarness.Airing(1, first.Time.Now.AddDays(1)));
        var now = first.Time.Now;
        // ファイルは消さずに、中継だけを閉じる (RankingHarness の DisposeAsync はファイルも消す)
        await first.Hub.DisposeAsync();

        await using var second = await RankingHarness.StartAsync(path);
        second.Time.Now = now;
        await Assert.That(Entries(await second.RankingAsync(tokens[0])).Length).IsEqualTo(1);
    }

    [Test]
    public async Task ログにトークンと番組を書かない()
    {
        await using var h = await RankingHarness.StartAsync();
        var start = h.Time.Now.AddDays(1);
        var tokens = await h.VoteManyAsync(5, RankingHarness.Airing(1, start, name: "ログに出ない番組"));
        await h.RankingAsync(tokens[0]);
        using (var revoked = await h.RevokeAsync(tokens[1])) await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var lines = string.Join('\n', h.Hub.Logs.Lines);
        await Assert.That(lines).Contains("ranking votes: add=1 remove=0 rejected=0");
        foreach (var token in tokens) await Assert.That(lines).DoesNotContain(token);
        await Assert.That(lines).DoesNotContain("ログに出ない番組");
        await Assert.That(lines).DoesNotContain("198.51.100.");
    }

    [Test]
    public async Task RANKING_DATA_が無ければ_ranking_は_404()
    {
        await using var h = await Harness.StartAsync();
        using var response = await h.Server.PostAsync("/ranking/tokens", null);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    [Arguments(null, "RANKING_MARK_KEY")]
    [Arguments("short", "RANKING_MARK_KEY")]
    public async Task RANKING_DATA_があれば鍵が要る(string? key, string expected)
    {
        var env = new DenpaHub.Env(name => name switch
        {
            "RANKING_DATA" => "/data/ranking.db",
            "RANKING_MARK_KEY" => key,
            _ => null,
        });
        var error = await Assert.That(() => RankingSettings.Load(env)).Throws<SettingsException>();
        await Assert.That(error!.Message).Contains(expected);
    }

    [Test]
    public async Task RANKING_DATA_は絶対パス()
    {
        var env = new DenpaHub.Env(name => name switch
        {
            "RANKING_DATA" => "ranking.db",
            "RANKING_MARK_KEY" => RankingHarness.MarkKey,
            _ => null,
        });
        var error = await Assert.That(() => RankingSettings.Load(env)).Throws<SettingsException>();
        await Assert.That(error!.Message).Contains("RANKING_DATA");
    }
}
