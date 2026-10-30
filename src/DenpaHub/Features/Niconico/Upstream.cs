using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace DenpaHub.Features.Niconico;

/// <summary>ニコニコの応答。本文は大きさの上限までで読み切ったもの</summary>
public sealed record UpstreamResponse(HttpStatusCode Status, string? ContentType, byte[] Body);

/// <summary>
/// ニコニコのトークン・失効のエンドポイントへ、クライアントのシークレットを付けて送る。
/// シークレットを使うのはここだけ。
/// </summary>
public sealed class Upstream(NiconicoSettings settings, HttpClient http)
{
    /// <summary>トークンの応答は ID トークンを含めても数 KB。これを超えるものは読まずに断る</summary>
    const int MaxBody = 64 * 1024;

    public Task<UpstreamResponse> TokenAsync(List<KeyValuePair<string, string>> form, CancellationToken ct) =>
        PostAsync(settings.TokenEndpoint, form, ct);

    public Task<UpstreamResponse> RevokeAsync(List<KeyValuePair<string, string>> form, CancellationToken ct) =>
        PostAsync(settings.RevocationEndpoint ?? throw new InvalidOperationException("失効のエンドポイントが設定されていません"), form, ct);

    async Task<UpstreamResponse> PostAsync(Uri url, List<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
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

    public static HttpClient CreateHttpClient(HttpMessageHandler? handler) =>
        new(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
}
