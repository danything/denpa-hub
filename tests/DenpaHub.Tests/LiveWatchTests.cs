using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DenpaHub.Features.Niconico;
using Microsoft.AspNetCore.Http;

namespace DenpaHub.Tests;

/// <summary>
/// 実況の視聴の接続先の中継。上流は偽のニコニコ (Harness)。パスと応答の形はテストのための偽物で、設定から渡している
/// </summary>
public class LiveWatchTests
{
    static async Task<HttpResponseMessage> WatchAsync(Harness h, string? token = "access-1", string channel = "jk1", string user = "12345")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, LiveFeature.WatchPath)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["channel"] = channel, ["user"] = user }),
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await h.Server.SendAsync(request);
    }

    static async Task<string?> ErrorOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("error").GetString();
    }

    [Test]
    public async Task 局から番組を引いて視聴の接続先を返す()
    {
        await using var h = await Harness.StartAsync();
        using var response = await WatchAsync(h, channel: "jk211");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(json.RootElement.GetProperty("url").GetString()).IsEqualTo(FakeProvider.WatchUrl);
        // 対応表のチャンネルで番組を引き、その番組と利用者で接続先を引く
        await Assert.That(h.Provider.LiveRequests).IsEquivalentTo(["program ?ch=ch-211", "watch ?program=pg-ch-211&user=12345"]);
    }

    [Test]
    public async Task 対応表に無い局は上流へ送らずに断る()
    {
        await using var h = await Harness.StartAsync();
        using var response = await WatchAsync(h, channel: "jk404");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorOf(response)).IsEqualTo("unknown_channel");
        await Assert.That(h.Provider.LiveRequests).IsEmpty();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    public async Task トークンが無ければ_401(string? token)
    {
        await using var h = await Harness.StartAsync();
        using var response = await WatchAsync(h, token: token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ErrorOf(response)).IsEqualTo("invalid_token");
        await Assert.That(h.Provider.LiveRequests).IsEmpty();
    }

    [Test]
    [Arguments("")]
    [Arguments("12 345")]
    [Arguments("../admin")]
    public async Task 利用者の_ID_が読めなければ_400(string user)
    {
        await using var h = await Harness.StartAsync();
        using var response = await WatchAsync(h, user: user);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorOf(response)).IsEqualTo("invalid_request");
    }

    [Test]
    public async Task 古いトークンは_invalid_token()
    {
        await using var h = await Harness.StartAsync();
        using var response = await WatchAsync(h, token: "expired");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ErrorOf(response)).IsEqualTo("invalid_token");
    }

    [Test]
    public async Task スコープが足りなければ_insufficient_scope()
    {
        await using var h = await Harness.StartAsync();
        using var response = await WatchAsync(h, token: "noscope");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await ErrorOf(response)).IsEqualTo("insufficient_scope");
    }

    [Test]
    public async Task 放送していなければ_not_on_air()
    {
        await using var h = await Harness.StartAsync();
        using var response = await WatchAsync(h, channel: "jk9");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await ErrorOf(response)).IsEqualTo("not_on_air");
    }

    [Test]
    [Arguments(true, 401, 401, "invalid_token")]
    [Arguments(true, 403, 403, "insufficient_scope")]
    [Arguments(true, 429, 429, "temporarily_unavailable")]
    [Arguments(true, 500, 502, "temporarily_unavailable")]
    [Arguments(true, 400, 502, "temporarily_unavailable")]
    [Arguments(false, 404, 404, "not_on_air")]
    [Arguments(false, 401, 401, "invalid_token")]
    [Arguments(false, 503, 502, "temporarily_unavailable")]
    public async Task 上流の失敗を契約の名前にする(bool program, int upstream, int status, string error)
    {
        await using var h = await Harness.StartAsync();
        Func<HttpContext, Task> fail = context =>
        {
            context.Response.StatusCode = upstream;
            return context.Response.WriteAsync("""{"meta":{"status":0}}""");
        };
        if (program) h.Provider.ProgramOverride = fail;
        else h.Provider.WatchOverride = fail;
        using var response = await WatchAsync(h);
        await Assert.That((int)response.StatusCode).IsEqualTo(status);
        await Assert.That(await ErrorOf(response)).IsEqualTo(error);
    }

    [Test]
    public async Task 上流に届かなければ_502()
    {
        await using var h = await Harness.StartAsync();
        h.Provider.ProgramOverride = _ => throw new HttpRequestException("down");
        using var response = await WatchAsync(h);
        await Assert.That((int)response.StatusCode).IsEqualTo(502);
        await Assert.That(await ErrorOf(response)).IsEqualTo("temporarily_unavailable");
    }

    [Test]
    public async Task 応答の形がずれても警告して読める所を読む()
    {
        await using var h = await Harness.StartAsync();
        // 番組の ID は決めた場所に無いが、同じ名前のプロパティはある
        h.Provider.ProgramOverride = context => context.Response.WriteAsync("""{"data":[{"program":"pg-moved"}]}""");
        // 接続先は決めた場所に無く、名前も違う。wss の URL を探して使う
        h.Provider.WatchOverride = context => context.Response.WriteAsync("{\"v2\":{\"links\":[\"https://example.test/\",\"" + FakeProvider.WatchUrl + "\"]}}");
        using var response = await WatchAsync(h);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(json.RootElement.GetProperty("url").GetString()).IsEqualTo(FakeProvider.WatchUrl);
        await Assert.That(h.Provider.LiveRequests).Contains("watch ?program=pg-moved&user=12345");
        await Assert.That(h.Logs.Lines.Count(l => l.Contains("Warning") && l.Contains("version skew"))).IsEqualTo(2);
    }

    [Test]
    [Arguments("""{"endpoint":{"ws":"ws://insecure.test/"}}""")]
    [Arguments("""{"endpoint":{"ws":"https://not-a-socket.test/"}}""")]
    [Arguments("""{"endpoint":{}}""")]
    [Arguments("not json")]
    public async Task wss_の接続先が読めなければ_502(string body)
    {
        await using var h = await Harness.StartAsync();
        h.Provider.WatchOverride = context => context.Response.WriteAsync(body);
        using var response = await WatchAsync(h);
        await Assert.That((int)response.StatusCode).IsEqualTo(502);
        await Assert.That(await ErrorOf(response)).IsEqualTo("temporarily_unavailable");
    }

    [Test]
    public async Task 番組の_ID_が読めなければ_502()
    {
        await using var h = await Harness.StartAsync();
        h.Provider.ProgramOverride = context => context.Response.WriteAsync("""{"result":{"program":"a/b"}}""");
        using var response = await WatchAsync(h);
        await Assert.That((int)response.StatusCode).IsEqualTo(502);
        await Assert.That(h.Provider.LiveRequests.Count(r => r.StartsWith("watch"))).IsEqualTo(0);
    }

    [Test]
    public async Task ログに接続先やトークンや利用者を書かない()
    {
        await using var h = await Harness.StartAsync();
        using var ok = await WatchAsync(h, user: "98765");
        await Assert.That(ok.StatusCode).IsEqualTo(HttpStatusCode.OK);
        h.Provider.ProgramOverride = context => context.Response.WriteAsync("""{"data":{"program":"pg-1"}}""");
        h.Provider.WatchOverride = context => context.Response.WriteAsync("""{"other":"wss://ws.provider.test/x?ticket=ticket-secret-2"}""");
        using var skewed = await WatchAsync(h, user: "98765");
        await Assert.That(skewed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var rejected = await WatchAsync(h, channel: "jk1\nforged log line", user: "98765");

        string[] secrets = ["access-1", "98765", "ticket-secret", "ws.provider.test", "ch-1", "pg-", "forged"];
        await Assert.That(h.Logs.Lines).IsNotEmpty();
        foreach (var line in h.Logs.Lines)
            foreach (var secret in secrets)
                await Assert.That(line).DoesNotContain(secret);
    }

    [Test]
    public async Task 上流への全リクエストに_User_Agent_を付ける()
    {
        await using var h = await Harness.StartAsync();
        using var watch = await WatchAsync(h);
        using var refresh = await h.Server.PostAsync("/niconico/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = "refresh-1",
        }));
        await Assert.That(h.Provider.UserAgents.Count).IsEqualTo(3);
        foreach (var agent in h.Provider.UserAgents)
        {
            await Assert.That(agent).StartsWith("denpa-hub/");
            await Assert.That(agent).EndsWith("(+https://github.com/danything/denpa-hub)");
        }
    }

    [Test]
    public async Task User_Agent_は設定で替えられる()
    {
        await using var h = await Harness.StartAsync(new() { ["NICONICO_USER_AGENT"] = "denpa-hub-staging/1 (ops@example.jp)" });
        using var watch = await WatchAsync(h);
        await Assert.That(h.Provider.UserAgents).IsNotEmpty();
        foreach (var agent in h.Provider.UserAgents)
            await Assert.That(agent).IsEqualTo("denpa-hub-staging/1 (ops@example.jp)");
    }

    [Test]
    public async Task 同じアドレスから続けて来ると_429()
    {
        await using var h = await Harness.StartAsync();
        HttpStatusCode last = 0;
        for (var i = 0; i < 61; i++)
        {
            using var response = await WatchAsync(h, channel: "jk404");
            last = response.StatusCode;
        }
        await Assert.That(last).IsEqualTo(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task 失効の口は無い()
    {
        await using var h = await Harness.StartAsync();
        using var response = await h.Server.PostAsync("/niconico/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "x" }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
}
