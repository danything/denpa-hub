using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DenpaHub.Features.Jikkyo;

/// <summary>
/// NX-Jikkyo への WebSocket の開き方。本番は <see cref="ClientWebSocket"/>、テストは偽の NX-Jikkyo (TestServer) に差し替える。
/// <c>cookie</c> は書き込みの接続だけに付ける (<c>NX-User-ID</c>)
/// </summary>
public delegate Task<WebSocket> NxConnect(Uri url, string? cookie, CancellationToken ct);

/// <summary>WebSocket で JSON のテキストを 1 通ずつ送り受けする</summary>
public static class NxSocket
{
    /// <summary>1 通の上限。res_from の 1000 件でも 1 件ずつ来るので、1 件の chat が収まれば足りる</summary>
    const int MaxMessage = 1024 * 1024;

    public static NxConnect Client(string userAgent) => async (url, cookie, ct) =>
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("User-Agent", userAgent);
        if (cookie is not null) ws.Options.SetRequestHeader("Cookie", $"NX-User-ID={cookie}");
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await ws.ConnectAsync(url, timeout.Token);
        }
        catch
        {
            ws.Dispose();
            throw;
        }
        return ws;
    };

    public static Task SendAsync(WebSocket ws, string json, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);

    /// <summary>1 通読む。閉じられたら null</summary>
    public static async Task<string?> ReceiveAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var message = new MemoryStream();
            while (true)
            {
                var result = await ws.ReceiveAsync(buffer.AsMemory(), ct);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessage) throw new WebSocketException("message too large");
                if (result.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>閉じる。閉じられなくても捨てる</summary>
    public static async Task CloseAsync(WebSocket ws)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
        }
        catch
        {
            // 閉じられなくても捨てる
        }
        ws.Dispose();
    }

    /// <summary>視聴の WS の 1 通 (<c>{"type": …, "data": {…}}</c>)。読めなければ type は null</summary>
    public static (string? Type, JsonElement Data) ReadWatch(string text, out JsonDocument? document)
    {
        document = null;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return (null, default);
        }
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            return (null, default);
        return (type.GetString(), root.TryGetProperty("data", out var data) ? data : default);
    }

    /// <summary>
    /// <c>room</c> のコメントの WS の URL。NX-Jikkyo の外 (オリジンが違う) を指していたら null (返事の言うまま別のホストへ繋がない)
    /// </summary>
    public static Uri? CommentUri(JsonElement data, Uri origin)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("messageServer", out var server) || server.ValueKind != JsonValueKind.Object
            || !server.TryGetProperty("uri", out var uri) || uri.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(uri.GetString(), UriKind.Absolute, out var url))
            return null;
        return url.Scheme == origin.Scheme && url.Host == origin.Host && url.Port == origin.Port ? url : null;
    }

    /// <summary>書き込みの vpos の基準 (<c>vposBaseTime</c>)。読めなければ null</summary>
    public static DateTimeOffset? VposBase(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty("vposBaseTime", out var v) && v.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;

    public static Uri WatchUri(Uri origin, string channel) => new(origin, $"/api/v1/channels/{channel}/ws/watch");

    public const string StartWatching = """{"type":"startWatching","data":{"reconnect":false}}""";
    public const string Pong = """{"type":"pong"}""";
    public const string KeepSeat = """{"type":"keepSeat"}""";
}
