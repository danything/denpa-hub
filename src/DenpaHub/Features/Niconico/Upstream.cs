using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace DenpaHub.Features.Niconico;

/// <summary>ニコニコの応答。本文は大きさの上限までで読み切ったもの</summary>
public sealed record UpstreamResponse(HttpStatusCode Status, string? ContentType, byte[] Body);

/// <summary>
/// ニコニコへのリクエスト。トークンのエンドポイントにはクライアントのシークレットを付けて送る (シークレットを使うのはここだけ)。
/// 実況の API には利用者のアクセストークンを付けて送る。
/// </summary>
public sealed class Upstream(NiconicoSettings settings, HttpClient http)
{
    /// <summary>トークンの応答は ID トークンを含めても数 KB。これを超えるものは読まずに断る</summary>
    const int MaxBody = 64 * 1024;

    public Task<UpstreamResponse> TokenAsync(List<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, settings.TokenEndpoint);
        if (settings.TokenAuth == TokenAuthMethod.ClientSecretBasic)
        {
            // RFC 6749 2.3.1: ID とシークレットはフォームの形でエスケープしてから Basic にする
            var pair = $"{WebUtility.UrlEncode(settings.ClientId)}:{WebUtility.UrlEncode(settings.ClientSecret)}";
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));
        }
        else
        {
            form.Add(new("client_id", settings.ClientId));
            form.Add(new("client_secret", settings.ClientSecret));
        }
        request.Content = new FormUrlEncodedContent(form);
        return SendAsync(request, ct);
    }

    /// <summary>利用者のアクセストークン (Bearer) を付けて GET する</summary>
    public Task<UpstreamResponse> GetAsync(Uri url, string accessToken, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return SendAsync(request, ct);
    }

    async Task<UpstreamResponse> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var _ = request;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBody) throw new HttpRequestException("応答が大きすぎます");
            buffer.Write(chunk, 0, read);
        }
        return new UpstreamResponse(response.StatusCode, response.Content.Headers.ContentType?.MediaType, buffer.ToArray());
    }

    /// <summary>上流へのクライアント。どのリクエストにも User-Agent を付ける (どのソフトからのアクセスかを上流が分かるように)</summary>
    public static HttpClient CreateHttpClient(HttpMessageHandler? handler, string userAgent)
    {
        var client = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        // 値は設定の読み込みで印字できる ASCII に絞ってある。製品とコメントの形に限らず、そのまま送る
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        return client;
    }
}
