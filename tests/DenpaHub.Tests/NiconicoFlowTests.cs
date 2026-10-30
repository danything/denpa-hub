using System.Net;
using System.Text.Json;
using DenpaHub;
using DenpaHub.Features.Niconico;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace DenpaHub.Tests;

public class FlowTests
{
    static (string Name, string Value) Fragment(HttpResponseMessage response)
    {
        var location = response.Headers.Location!.OriginalString;
        var hash = location.IndexOf('#');
        var pair = location[(hash + 1)..].Split('=', 2);
        return (pair[0], pair[1]);
    }

    [Test]
    public async Task 連携を通すと暗号化したトークンが戻り先のフラグメントに届く()
    {
        await using var h = await Harness.StartAsync();
        var (authorize, cookie) = await h.AuthorizeAsync();
        await Assert.That(authorize.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(cookie).IsNotNull();

        var location = authorize.Headers.Location!;
        var query = QueryHelpers.ParseQuery(location.Query);
        await Assert.That(location.GetLeftPart(UriPartial.Path)).IsEqualTo(FakeProvider.Origin + "/oauth/authorize");
        await Assert.That(query["redirect_uri"].ToString()).IsEqualTo(Harness.RelayOrigin + "/niconico/callback");
        await Assert.That(query["scope"].ToString()).IsEqualTo("openid offline_access");
        await Assert.That(query["code_challenge_method"].ToString()).IsEqualTo("S256");

        var callback = await h.ConsentAsync(location);
        var back = await h.CallbackAsync(callback, cookie);
        await Assert.That(back.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(back.Headers.Location!.OriginalString).StartsWith(Harness.Return + "#result=");
        await Assert.That(back.Headers.CacheControl!.NoStore).IsTrue();
        await Assert.That(back.Headers.GetValues("Referrer-Policy").Single()).IsEqualTo("no-referrer");

        using var token = JsonDocument.Parse(h.Open(Fragment(back).Value));
        await Assert.That(token.RootElement.GetProperty("access_token").GetString()).IsEqualTo("access-1");
        await Assert.That(token.RootElement.GetProperty("refresh_token").GetString()).IsEqualTo("refresh-1");
    }

    [Test]
    public async Task 結果は別の戻り先として開くと読めない()
    {
        await using var h = await Harness.StartAsync();
        var back = await h.LinkAsync();
        await Assert.That(() => h.Open(Fragment(back).Value, "http://192.168.1.21:3000" + ReturnTarget.Path)).Throws<Exception>();
    }

    [Test]
    public async Task 利用者が断るとエラーの名前だけを戻り先へ返す()
    {
        await using var h = await Harness.StartAsync();
        var back = await h.LinkAsync(deny: true);
        await Assert.That(back.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(back.Headers.Location!.OriginalString).IsEqualTo(Harness.Return + "#error=access_denied");
        await Assert.That(h.Provider.TokenRequests).IsEqualTo(0);
    }

    [Test]
    public async Task トークンのエンドポイントが断るとそのエラーの名前を返す()
    {
        await using var h = await Harness.StartAsync();
        h.Provider.TokenOverride = async context =>
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("""{"error":"invalid_grant","error_description":"<script>"}""");
        };
        var back = await h.LinkAsync();
        await Assert.That(back.Headers.Location!.OriginalString).IsEqualTo(Harness.Return + "#error=invalid_grant");
    }

    [Test]
    public async Task access_token_の無い応答は渡さない()
    {
        await using var h = await Harness.StartAsync();
        h.Provider.TokenOverride = context => context.Response.WriteAsync("""{"token_type":"Bearer"}""");
        var back = await h.LinkAsync();
        await Assert.That(back.Headers.Location!.OriginalString).IsEqualTo(Harness.Return + "#error=invalid_token_response");
    }

    [Test]
    public async Task Cookie_が無いコールバックは断る()
    {
        await using var h = await Harness.StartAsync();
        var (authorize, _) = await h.AuthorizeAsync();
        var callback = await h.ConsentAsync(authorize.Headers.Location!);
        var back = await h.CallbackAsync(callback, cookie: null);
        await Assert.That(back.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(h.Provider.TokenRequests).IsEqualTo(0);
    }

    [Test]
    public async Task 別のブラウザの_Cookie_では通らない()
    {
        await using var h = await Harness.StartAsync();
        var (first, _) = await h.AuthorizeAsync();
        var (_, otherCookie) = await h.AuthorizeAsync();
        var callback = await h.ConsentAsync(first.Headers.Location!);
        var back = await h.CallbackAsync(callback, otherCookie);
        await Assert.That(back.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task 書き換えた_state_は読めない()
    {
        await using var h = await Harness.StartAsync();
        var (authorize, cookie) = await h.AuthorizeAsync();
        var callback = await h.ConsentAsync(authorize.Headers.Location!);
        var query = QueryHelpers.ParseQuery(callback.Query);
        var state = query["state"].ToString();
        var tampered = state[..10] + (state[10] == 'A' ? 'B' : 'A') + state[11..];
        var url = new Uri($"{Harness.RelayOrigin}/niconico/callback?code={query["code"]}&state={tampered}");
        var back = await h.CallbackAsync(url, cookie);
        await Assert.That(back.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task 期限を過ぎた_state_は断る()
    {
        await using var h = await Harness.StartAsync();
        var (authorize, cookie) = await h.AuthorizeAsync();
        var callback = await h.ConsentAsync(authorize.Headers.Location!);
        h.Time.Now += NiconicoFeature.StateLifetime + TimeSpan.FromSeconds(1);
        var back = await h.CallbackAsync(callback, cookie);
        await Assert.That(back.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(h.Provider.TokenRequests).IsEqualTo(0);
    }

    [Test]
    public async Task 戻り先が外のアドレスなら認可の画面へ送らない()
    {
        await using var h = await Harness.StartAsync();
        var (response, cookie) = await h.AuthorizeAsync("https://evil.example" + ReturnTarget.Path);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(cookie).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("not-base64!")]
    [Arguments("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task 公開鍵として読めない_key_は断る(string key)
    {
        await using var h = await Harness.StartAsync();
        var (response, _) = await h.AuthorizeAsync(key: key);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task ログにシークレットやコードやトークンやstateを書かない()
    {
        await using var h = await Harness.StartAsync();
        var (authorize, cookie) = await h.AuthorizeAsync();
        var callback = await h.ConsentAsync(authorize.Headers.Location!);
        var back = await h.CallbackAsync(callback, cookie);
        await Assert.That(back.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var refresh = await h.Server.PostAsync("/niconico/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = "refresh-1",
        }));

        var query = QueryHelpers.ParseQuery(callback.Query);
        string[] secrets =
        [
            FakeProvider.ClientSecret, query["code"].ToString(), query["state"].ToString()[..40],
            cookie!.Split('=')[1], "access-1", "refresh-1", "access-2", "192.168.1.20",
        ];
        await Assert.That(h.Logs.Lines).IsNotEmpty();
        foreach (var line in h.Logs.Lines)
            foreach (var secret in secrets)
                await Assert.That(line).DoesNotContain(secret);
    }
}

public class TokenTests
{
    [Test]
    public async Task 更新はシークレットを付けて送り応答をそのまま返す()
    {
        await using var h = await Harness.StartAsync();
        using var response = await h.Server.PostAsync("/niconico/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = "refresh-1",
        }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(json.RootElement.GetProperty("access_token").GetString()).IsEqualTo("access-2");
    }

    [Test]
    public async Task 古い_refresh_token_はニコニコのエラーをそのまま返す()
    {
        await using var h = await Harness.StartAsync();
        using var response = await h.Server.PostAsync("/niconico/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = "stale",
        }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("invalid_grant");
    }

    [Test]
    [Arguments("authorization_code")]
    [Arguments("client_credentials")]
    [Arguments("password")]
    public async Task 更新以外の種類は受けない(string grant)
    {
        await using var h = await Harness.StartAsync();
        using var response = await h.Server.PostAsync("/niconico/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = grant,
            ["code"] = "x",
        }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("unsupported_grant_type");
        await Assert.That(h.Provider.TokenRequests).IsEqualTo(0);
    }

    [Test]
    public async Task client_secret_post_ではフォームにシークレットを入れる()
    {
        await using var h = await Harness.StartAsync(new() { ["NICONICO_TOKEN_AUTH"] = "client_secret_post" });
        string? seen = null;
        h.Provider.TokenOverride = async context =>
        {
            var form = await context.Request.ReadFormAsync();
            seen = $"{form["client_id"]}:{form["client_secret"]}:{context.Request.Headers.Authorization}";
            await context.Response.WriteAsync("""{"access_token":"a"}""");
        };
        using var response = await h.Server.PostAsync("/niconico/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = "r",
        }));
        await Assert.That(seen).IsEqualTo($"{FakeProvider.ClientId}:{FakeProvider.ClientSecret}:");
    }

    [Test]
    public async Task ニコニコに届かなければ_502()
    {
        await using var h = await Harness.StartAsync();
        h.Provider.TokenOverride = _ => throw new HttpRequestException("down");
        using var response = await h.Server.PostAsync("/niconico/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = "refresh-1",
        }));
        await Assert.That((int)response.StatusCode).IsEqualTo(502);
    }

    [Test]
    public async Task 失効をニコニコへ送る()
    {
        await using var h = await Harness.StartAsync();
        using var response = await h.Server.PostAsync("/niconico/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = "refresh-1",
            ["token_type_hint"] = "refresh_token",
        }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(h.Provider.RevokedTokens).Contains("refresh-1");
    }

    [Test]
    public async Task 失効のエンドポイントが無ければ_404()
    {
        await using var h = await Harness.StartAsync(new() { ["NICONICO_REVOKE_URL"] = null });
        using var response = await h.Server.PostAsync("/niconico/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "x" }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task 同じアドレスから続けて来ると_429()
    {
        await using var h = await Harness.StartAsync();
        HttpStatusCode last = 0;
        for (var i = 0; i < 31; i++)
        {
            using var response = await h.Server.GetAsync("/niconico/authorize");
            last = response.StatusCode;
        }
        await Assert.That(last).IsEqualTo(HttpStatusCode.TooManyRequests);
    }
}
