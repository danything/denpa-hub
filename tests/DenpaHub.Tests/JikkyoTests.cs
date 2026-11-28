using System.Net;
using System.Text.Json;
using DenpaHub.Features.Jikkyo;
using Microsoft.Extensions.DependencyInjection;

namespace DenpaHub.Tests;

/// <summary>
/// 実況のコメントの中継 (docs/jikkyo.md)。上流は偽の NX-Jikkyo と偽の過去ログ API (JikkyoHarness)。本物には繋がない
/// </summary>
public class JikkyoTests
{
    static async Task<string?> ErrorOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("error").GetString();
    }

    static async Task WaitAsync(Func<bool> done, string what)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(50);
        if (!done()) throw new TimeoutException(what);
    }

    [Test]
    public async Task トークンが無ければどれも_401()
    {
        await using var h = await JikkyoHarness.StartAsync();
        foreach (var path in new[] { "/jikkyo/channels/jk1/live", "/jikkyo/channels/jk1/kakolog?from=1&to=2" })
        {
            using var response = await h.Server.GetAsync(path);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
        using var post = await h.PostAsync("A".PadRight(43, 'A'), "こんにちは");
        await Assert.That(post.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task 方針の一覧に無い局は購読も書き込みも断る()
    {
        await using var h = await JikkyoHarness.StartAsync();
        var token = await h.IssueAsync();
        using var live = await h.GetAsync(token, "/jikkyo/channels/jk404/live");
        await Assert.That(live.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorOf(live)).IsEqualTo("unknown_channel");
        using var post = await h.PostAsync(token, "こんにちは", channel: "jk404");
        await Assert.That(await ErrorOf(post)).IsEqualTo("unknown_channel");
        using var shape = await h.GetAsync(token, "/jikkyo/channels/nx1/live");
        await Assert.That(await ErrorOf(shape)).IsEqualTo("invalid_request");
        await Assert.That(h.Nx.Watches).IsEmpty();
    }

    [Test]
    public async Task NX_Jikkyo_のコメントを局ごとに_1_本で受けて配る()
    {
        await using var h = await JikkyoHarness.StartAsync();
        var first = await h.IssueAsync();
        var second = await h.IssueAsync();
        using var a = await h.SubscribeAsync(first);
        using var b = await h.SubscribeAsync(second);
        var (name, state, _) = await a.NextAsync();
        await Assert.That(name).IsEqualTo("state");
        await Assert.That(state).Contains("\"nx\":\"up\"");
        await WaitAsync(() => h.Nx.CommentSockets == 1, "comment ws");

        var now = DateTimeOffset.UtcNow;
        await h.Nx.EmitAsync(FakeNx.Chat("ふつうのコメント", now, mail: "184 red"));
        await h.Nx.EmitAsync(FakeNx.Chat("公式のコメント", now.AddMilliseconds(1), user: "nicolive:123"));
        await h.Nx.EmitAsync(FakeNx.Chat("/nicoad {}", now));
        await h.Nx.EmitAsync(FakeNx.Chat("ねたばれ注意", now));
        var got = (await a.CommentsAsync()).ToList();
        if (got.Count < 2) got.AddRange(await a.CommentsAsync());
        await Assert.That(got.Select(c => c.GetProperty("text").GetString()!)).IsEquivalentTo(["ふつうのコメント", "公式のコメント"]);
        await Assert.That(got[0].GetProperty("src").GetString()).IsEqualTo("nx");
        await Assert.That(got[0].GetProperty("mail").GetString()).IsEqualTo("184 red");
        await Assert.That(got[1].GetProperty("src").GetString()).IsEqualTo("nicolive");
        // 利用者の ID は配らない
        await Assert.That(got[0].TryGetProperty("user_id", out _)).IsFalse();
        await Assert.That((await b.CommentsAsync()).Length).IsGreaterThanOrEqualTo(1);
        // 2 つの家が観ていても、NX-Jikkyo に繋ぐのは 1 本
        await Assert.That(h.Nx.Watches.Count(w => w.Cookie is null)).IsEqualTo(1);
    }

    [Test]
    public async Task 繋ぎ直したときは通し番号の続きから送る()
    {
        await using var h = await JikkyoHarness.StartAsync();
        var token = await h.IssueAsync();
        string epoch;
        string id;
        using (var a = await h.SubscribeAsync(token))
        {
            var (_, state, _) = await a.NextAsync();
            epoch = JsonDocument.Parse(state).RootElement.GetProperty("epoch").GetString()!;
            await WaitAsync(() => h.Nx.CommentSockets == 1, "comment ws");
            await h.Nx.EmitAsync(FakeNx.Chat("1 つめ", DateTimeOffset.UtcNow));
            string? got = null;
            while (got is null) (_, _, got) = await a.NextAsync();
            id = got;
        }
        await h.Nx.EmitAsync(FakeNx.Chat("2 つめ", DateTimeOffset.UtcNow.AddMilliseconds(5)));
        await Task.Delay(200);
        using var b = await h.SubscribeAsync(token, query: $"?after={id}&epoch={epoch}");
        var resumed = await b.CommentsAsync();
        await Assert.That(resumed.Select(c => c.GetProperty("text").GetString()!)).IsEquivalentTo(["2 つめ"]);
        // epoch が違えば控えの全部
        using var c = await h.SubscribeAsync(token, query: $"?after={id}&epoch=other");
        var all = await c.CommentsAsync();
        await Assert.That(all.Select(x => x.GetProperty("text").GetString()!)).IsEquivalentTo(["1 つめ", "2 つめ"]);
    }

    [Test]
    public async Task 書き込みは保存して配り_利用者ごとの_Cookie_で_NX_Jikkyo_にも書く_戻ってきた写しは配らない()
    {
        await using var h = await JikkyoHarness.StartAsync();
        var token = await h.IssueAsync();
        using var live = await h.SubscribeAsync(token);
        await live.NextAsync();
        await WaitAsync(() => h.Nx.CommentSockets == 1, "comment ws");

        using var response = await h.PostAsync(token, "  自分の書き込み ", extra: ""","color":"red","position":"shita","size":"big" """);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetString()!;
        var got = await live.CommentsAsync();
        await Assert.That(got.Length).IsEqualTo(1);
        await Assert.That(got[0].GetProperty("src").GetString()).IsEqualTo("hub");
        await Assert.That(got[0].GetProperty("id").GetString()).IsEqualTo(id);
        await Assert.That(got[0].GetProperty("text").GetString()!).IsEqualTo("自分の書き込み");
        await Assert.That(got[0].GetProperty("mail").GetString()).IsEqualTo("184 red shita big");

        await WaitAsync(() => !h.Nx.Posts.IsEmpty, "relay");
        var (cookie, data) = h.Nx.Posts.Single();
        await Assert.That(cookie).IsNotNull();
        await Assert.That(data.GetProperty("isAnonymous").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("color").GetString()).IsEqualTo("red");
        await Assert.That(data.GetProperty("vpos").GetInt64()).IsGreaterThan(0);
        // NX-Jikkyo から戻ってきた写し (user_id が Cookie の SHA-1) は配らない
        await Assert.That(async () => await live.CommentsAsync(TimeSpan.FromSeconds(1))).Throws<OperationCanceledException>();
        // 中継できたことを覚える
        var store = h.Hub.App.Services.GetRequiredService<JikkyoStore>();
        await WaitAsync(() => store.Comments("jk1", 0, long.MaxValue).Single().Relayed, "relayed");

        // 同じ利用者は同じ Cookie、ほかの利用者は別の Cookie (全員が 1 人に見えない)
        h.Hub.Time.Now += TimeSpan.FromSeconds(5);
        using var again = await h.PostAsync(token, "もう一度");
        using var other = await h.PostAsync(token, "ほかの人", author: "BBBBBBBBBBBBBBBBBBBBBB");
        await WaitAsync(() => h.Nx.Posts.Count == 3, "relay x3");
        var cookies = h.Nx.Posts.Select(p => p.Cookie).ToList();
        await Assert.That(cookies[1]).IsEqualTo(cookies[0]);
        await Assert.That(cookies[2]).IsNotEqualTo(cookies[0]);
        // 1 人に 1 本の接続
        await Assert.That(h.Nx.Watches.Count(w => w.Cookie is not null)).IsEqualTo(2);
    }

    [Test]
    public async Task NX_Jikkyo_が落ちていても独自コメントは受けて配る()
    {
        await using var h = await JikkyoHarness.StartAsync();
        h.Nx.Refuse = true;
        var token = await h.IssueAsync();
        using var live = await h.SubscribeAsync(token);
        await live.NextAsync();
        await WaitAsync(() => !h.Nx.Watches.IsEmpty, "watch tried");
        using var response = await h.PostAsync(token, "落ちていても");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var got = await live.CommentsAsync();
        await Assert.That(got[0].GetProperty("text").GetString()!).IsEqualTo("落ちていても");
    }

    [Test]
    public async Task 本文の決まり_間隔_止めた利用者_利用者の数で断る()
    {
        await using var h = await JikkyoHarness.StartAsync();
        var token = await h.IssueAsync();
        foreach (var text in new[] { "", "   ", "改行\nあり", "/nicoad 運営", new string('あ', 61), "ねたばれ注意", "ﾈﾀﾊﾞﾚ じゃなくてネタバレ" })
        {
            using var refused = await h.PostAsync(token, text);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await ErrorOf(refused)).IsEqualTo("rejected");
        }
        using var color = await h.PostAsync(token, "色", extra: ""","color":"rainbow" """);
        await Assert.That(await ErrorOf(color)).IsEqualTo("invalid_request");
        using var badAuthor = await h.PostAsync(token, "だれ", author: "short");
        await Assert.That(await ErrorOf(badAuthor)).IsEqualTo("invalid_request");

        using var ok = await h.PostAsync(token, "1 件め");
        await Assert.That(ok.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var fast = await h.PostAsync(token, "すぐ 2 件め");
        await Assert.That(fast.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(await ErrorOf(fast)).IsEqualTo("wait");

        // 家ごとの利用者の ID の数 (方針は 3)
        foreach (var author in new[] { "BBBBBBBBBBBBBBBBBBBBBB", "CCCCCCCCCCCCCCCCCCCCCC" })
        {
            using var r = await h.PostAsync(token, "べつの人", author: author);
            await Assert.That(r.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        using var fourth = await h.PostAsync(token, "4 人め", author: "DDDDDDDDDDDDDDDDDDDDDD");
        await Assert.That(fourth.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await ErrorOf(fourth)).IsEqualTo("blocked");

        // 方針で止めた利用者
        var stored = h.Hub.App.Services.GetRequiredService<JikkyoStore>().Comments("jk1", 0, long.MaxValue).First(c => c.Text == "1 件め").Author;
        await h.SetPolicyAsync(JikkyoHarness.PolicyJson().Replace("\"blockedAuthors\": []", $"\"blockedAuthors\": [\"{stored}\"]"));
        h.Hub.Time.Now += TimeSpan.FromSeconds(10);
        using var blocked = await h.PostAsync(token, "止められた");
        await Assert.That(await ErrorOf(blocked)).IsEqualTo("blocked");
    }

    [Test]
    public async Task 過去ログは塊でキャッシュし_独自コメントを合わせ_中継した写しを外す()
    {
        await using var h = await JikkyoHarness.StartAsync();
        var token = await h.IssueAsync();
        var now = h.Hub.Time.Now;
        using (var post = await h.PostAsync(token, "独自のコメント"))
            await Assert.That(post.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var store = h.Hub.App.Services.GetRequiredService<JikkyoStore>();
        var own = store.Comments("jk1", 0, long.MaxValue).Single();
        var copy = FakeNx.UserIdOf(h.Hub.App.Services.GetRequiredService<Identity>().NxCookie(own.Author));
        h.Nx.Kakolog = _ => "[" + string.Join(",", new[]
        {
            FakeNx.Chat("過去のコメント", now.AddMinutes(-30)),
            FakeNx.Chat("独自のコメント", now, user: copy),
            FakeNx.Chat("ねたばれ", now.AddMinutes(-20)),
        }.Select(c => $$"""{"chat":{{c}}}""")) + "]";

        var from = now.AddHours(-1).ToUnixTimeMilliseconds();
        var to = now.AddMinutes(1).ToUnixTimeMilliseconds();
        using var response = await h.GetAsync(token, $"/jikkyo/channels/jk1/kakolog?from={from}&to={to}");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(json.RootElement.GetProperty("complete").GetBoolean()).IsTrue();
        var comments = json.RootElement.GetProperty("comments").EnumerateArray().ToList();
        await Assert.That(comments.Select(c => (c.GetProperty("text").GetString()!, c.GetProperty("src").GetString()!)))
            .IsEquivalentTo([("過去のコメント", "nx"), ("独自のコメント", "hub")]);
        var asked = h.Nx.KakologQueries.Count;
        // 今より後は取りに行かない
        await Assert.That(h.Nx.KakologQueries.First()).Contains($"endtime={now.ToUnixTimeSeconds()}");

        // 2 度目はキャッシュから
        using var again = await h.GetAsync(token, $"/jikkyo/channels/jk1/kakolog?from={from}&to={to}");
        await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(h.Nx.KakologQueries.Count).IsEqualTo(asked);
    }

    [Test]
    public async Task まだ入りきっていない塊を取り直しても_持っている件数は増え続けない()
    {
        await using var h = await JikkyoHarness.StartAsync();
        var token = await h.IssueAsync();
        var now = h.Hub.Time.Now;
        h.Nx.Kakolog = _ => "[" + string.Join(",", Enumerable.Range(0, 5).Select(i => $$"""{"chat":{{FakeNx.Chat($"c{i}", now.AddSeconds(-i - 1))}}}""")) + "]";
        var path = $"/jikkyo/channels/jk1/kakolog?from={now.AddMinutes(-5).ToUnixTimeMilliseconds()}&to={now.ToUnixTimeMilliseconds()}";
        var kakolog = h.Hub.App.Services.GetRequiredService<Kakolog>();
        using (var first = await h.GetAsync(token, path)) await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var cached = kakolog.Cached;
        await Assert.That(cached).IsEqualTo(5);
        // 5 分たつと取り直す (期限の切れた塊を置き換える)
        h.Hub.Time.Now += TimeSpan.FromMinutes(6);
        using (var again = await h.GetAsync(token, path)) await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(h.Nx.KakologQueries.Count).IsEqualTo(2);
        await Assert.That(kakolog.Cached).IsEqualTo(cached);
    }

    [Test]
    public async Task 過去ログ_API_が失敗したら独自コメントだけを返して_complete_を_false_にする()
    {
        await using var h = await JikkyoHarness.StartAsync();
        var token = await h.IssueAsync();
        h.Nx.Kakolog = _ => null;
        using (var post = await h.PostAsync(token, "独自のコメント")) { }
        var now = h.Hub.Time.Now;
        using var response = await h.GetAsync(token, $"/jikkyo/channels/jk1/kakolog?from={now.AddHours(-2).ToUnixTimeMilliseconds()}&to={now.ToUnixTimeMilliseconds() + 1}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(json.RootElement.GetProperty("complete").GetBoolean()).IsFalse();
        await Assert.That(json.RootElement.GetProperty("comments").GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    [Arguments("from=10&to=5")]
    [Arguments("from=0&to=300000000")]
    [Arguments("from=abc&to=5")]
    public async Task 過去ログの区間が違えば_400(string query)
    {
        await using var h = await JikkyoHarness.StartAsync();
        var token = await h.IssueAsync();
        using var response = await h.GetAsync(token, $"/jikkyo/channels/jk1/kakolog?{query}");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task 方針で消したものは配らず_過去ログにも出さない()
    {
        await using var h = await JikkyoHarness.StartAsync();
        var token = await h.IssueAsync();
        using var post = await h.PostAsync(token, "消されるコメント");
        var id = JsonDocument.Parse(await post.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();
        await h.SetPolicyAsync(JikkyoHarness.PolicyJson().Replace("\"removedComments\": []", $"\"removedComments\": [\"{id}\"]"));
        var stored = h.Hub.App.Services.GetRequiredService<JikkyoStore>().Comments("jk1", 0, long.MaxValue).Single();
        await Assert.That(stored.Removed).IsTrue();
        await Assert.That(stored.Text).IsEqualTo("");
        var now = h.Hub.Time.Now;
        using var response = await h.GetAsync(token, $"/jikkyo/channels/jk1/kakolog?from={now.AddMinutes(-5).ToUnixTimeMilliseconds()}&to={now.ToUnixTimeMilliseconds() + 1}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(json.RootElement.GetProperty("comments").GetArrayLength()).IsEqualTo(0);
        using var live = await h.SubscribeAsync(token);
        await live.NextAsync();
        await Assert.That(async () => await live.CommentsAsync(TimeSpan.FromMilliseconds(500))).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task 方針の形が違えば起動しない_動いている間は前のものを使い続ける()
    {
        await Assert.That(() => Policy.Parse("""{"retentionDays":30}"""u8)).Throws<PolicyException>();
        await using var h = await JikkyoHarness.StartAsync();
        await h.SetPolicyAsync("{ broken");
        await Assert.That(h.Hub.App.Services.GetRequiredService<PolicyHolder>().Current.MaxLength).IsEqualTo(60);
    }

    [Test]
    public async Task 記録にトークンや本文や利用者を書かない()
    {
        await using var h = await JikkyoHarness.StartAsync();
        var token = await h.IssueAsync();
        using (var live = await h.SubscribeAsync(token)) await live.NextAsync();
        using (await h.PostAsync(token, "秘密の本文")) { }
        await WaitAsync(() => !h.Nx.Posts.IsEmpty || h.Nx.Watches.Count >= 2, "relay tried");
        var all = string.Join("\n", h.Hub.Logs.Lines);
        await Assert.That(all).DoesNotContain(token);
        await Assert.That(all).DoesNotContain("秘密の本文");
        await Assert.That(all).DoesNotContain(JikkyoHarness.Author);
        await Assert.That(all).Contains("jikkyo post accepted");
    }

    [Test]
    public void 正規化は全角と半角_大小_ひらがなとカタカナを区別しない()
    {
        if (Policy.Fold("ＡＢＣ") != "abc" || Policy.Fold("ネタバレ") != "ねたばれ") throw new Exception("fold");
    }
}
