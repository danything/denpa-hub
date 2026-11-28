using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DenpaHub.Features.Jikkyo;

/// <summary>NX-Jikkyo に書く 1 件</summary>
public sealed record RelayPost(string Channel, string Author, string Text, string? Color, string? Position, string? Size);

/// <summary>
/// **独自コメントを NX-Jikkyo にも書く。** 利用者 (局と利用者の ID の組) ごとに視聴の WS を 1 本張り、利用者ごとに固定の Cookie <c>NX-User-ID</c> を付ける。
/// NX-Jikkyo の連投の決まり (0.5 秒以内を黙って捨て、3 回を超えると接続ごと捨てる) は接続ごとなので、全員を 1 本で送ると全員が 1 人に見え、まとめて捨てられるため。
/// 2 分書かなければ閉じ、同時に <see cref="MaxConnections"/> 本まで (docs/jikkyo.md の「NX-Jikkyo に書く」)
/// </summary>
public sealed class NxPoster(JikkyoSettings settings, NxConnect connect, Identity identity, TimeProvider time, ILogger<NxPoster> log) : IDisposable
{
    public const int MaxConnections = 200;
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(2);
    /// <summary>繋いでから room を受けるまで、送ってから返事を受けるまで</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    readonly ConcurrentDictionary<(string Channel, string Author), PosterSession> sessions = new();
    readonly Lock gate = new();

    /// <summary>書く。書けたら true。繋がらない・断られた・返事が無いときは false (送り直さない)</summary>
    public async Task<bool> PostAsync(RelayPost post, CancellationToken ct)
    {
        PosterSession session;
        lock (gate)
        {
            if (!sessions.TryGetValue((post.Channel, post.Author), out session!))
            {
                if (sessions.Count >= MaxConnections)
                {
                    // いちばん長く書いていないものを閉じる
                    var oldest = sessions.MinBy(s => s.Value.LastUsed);
                    if (sessions.TryRemove(oldest.Key, out var dropped)) dropped.Dispose();
                }
                session = new PosterSession(settings, connect, identity.NxCookie(post.Author), post.Channel, time);
                sessions[(post.Channel, post.Author)] = session;
            }
        }
        try
        {
            return await session.PostAsync(post, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogInformation("jikkyo relay failed ({Type})", e.GetType().Name);
            Drop(post, session);
            return false;
        }
    }

    void Drop(RelayPost post, PosterSession session)
    {
        if (sessions.TryRemove(new KeyValuePair<(string, string), PosterSession>((post.Channel, post.Author), session))) session.Dispose();
    }

    /// <summary>2 分書かなかった接続を閉じる (<see cref="JikkyoWorker"/> が 30 秒ごとに呼ぶ)</summary>
    public void Sweep()
    {
        var before = time.GetUtcNow() - Idle;
        foreach (var (key, session) in sessions)
            if (session.LastUsed < before && sessions.TryRemove(new KeyValuePair<(string, string), PosterSession>(key, session)))
                session.Dispose();
    }

    public void Dispose()
    {
        foreach (var session in sessions.Values) session.Dispose();
        sessions.Clear();
    }
}

/// <summary>利用者 1 人の、局 1 つへの視聴の WS。書き込みは 1 件ずつ</summary>
sealed class PosterSession(JikkyoSettings settings, NxConnect connect, string cookie, string channel, TimeProvider time) : IDisposable
{
    readonly SemaphoreSlim one = new(1, 1);
    readonly CancellationTokenSource stop = new();
    WebSocket? ws;
    Task? reader;
    TaskCompletionSource<DateTimeOffset>? room;
    TaskCompletionSource<bool>? reply;

    public DateTimeOffset LastUsed { get; private set; } = time.GetUtcNow();

    public async Task<bool> PostAsync(RelayPost post, CancellationToken ct)
    {
        await one.WaitAsync(ct);
        try
        {
            LastUsed = time.GetUtcNow();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
            timeout.CancelAfter(NxPoster.Wait);
            if (ws is null || ws.State != WebSocketState.Open || reader is null || reader.IsCompleted) await OpenAsync(timeout.Token);
            var vposBase = await room!.Task.WaitAsync(timeout.Token);
            var vpos = (long)Math.Round((time.GetUtcNow() - vposBase).TotalMilliseconds / 10);
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            reply = answer;
            await NxSocket.SendAsync(ws!, Message(post, vpos), timeout.Token);
            return await answer.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            reply = null;
            one.Release();
        }
    }

    static string Message(RelayPost post, long vpos)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("type", "postComment");
            json.WriteStartObject("data");
            json.WriteString("text", post.Text);
            json.WriteNumber("vpos", vpos);
            // いつも 184 (匿名)
            json.WriteBoolean("isAnonymous", true);
            if (post.Color is not null) json.WriteString("color", post.Color);
            if (post.Position is not null) json.WriteString("position", post.Position);
            if (post.Size is not null) json.WriteString("size", post.Size);
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    async Task OpenAsync(CancellationToken ct)
    {
        Close();
        room = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        var socket = await connect(NxSocket.WatchUri(settings.NxOrigin, channel), cookie, ct);
        ws = socket;
        await NxSocket.SendAsync(socket, NxSocket.StartWatching, ct);
        reader = Task.Run(() => ReadAsync(socket, room, stop.Token));
    }

    async Task ReadAsync(WebSocket socket, TaskCompletionSource<DateTimeOffset> roomReady, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var text = await NxSocket.ReceiveAsync(socket, ct);
                if (text is null) break;
                var (type, data) = NxSocket.ReadWatch(text, out var document);
                using (document)
                {
                    switch (type)
                    {
                        case "ping":
                            await NxSocket.SendAsync(socket, NxSocket.Pong, ct);
                            break;
                        case "room":
                            roomReady.TrySetResult(NxSocket.VposBase(data) ?? time.GetUtcNow());
                            break;
                        case "postCommentResult":
                            reply?.TrySetResult(true);
                            break;
                        case "error":
                            reply?.TrySetResult(false);
                            // スレッドの替わり目 (毎日 4:00)。閉じて、次の書き込みで張り直す
                            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String && m.GetString() == "NOT_ON_AIR") return;
                            break;
                        case "disconnect":
                            return;
                    }
                }
            }
        }
        catch
        {
            // 次の書き込みで繋ぎ直す
        }
        finally
        {
            roomReady.TrySetException(new WebSocketException("closed"));
            // 張り直したあとに古い接続が閉じても、新しい書き込みの返事にしない
            if (ReferenceEquals(ws, socket)) reply?.TrySetResult(false);
        }
    }

    void Close()
    {
        var socket = ws;
        ws = null;
        reader = null;
        if (socket is not null) _ = NxSocket.CloseAsync(socket);
    }

    public void Dispose()
    {
        if (stop.IsCancellationRequested) return;
        stop.Cancel();
        Close();
    }
}
