using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace DenpaHub.Features.Jikkyo;

/// <summary>購読者に送るもの。コメントか、NX-Jikkyo に繋がっているか</summary>
public abstract record LiveEvent
{
    /// <param name="Seq">最後のコメントの通し番号 (購読を再開するときに渡す)</param>
    public sealed record Comments(IReadOnlyList<Comment> List, long Seq) : LiveEvent;
    public sealed record State(bool NxUp) : LiveEvent;
}

/// <summary>購読 1 本。読み終えたら Dispose で外す</summary>
public sealed class Subscription(NxChannel channel, ChannelReader<LiveEvent> reader, Action leave) : IDisposable
{
    public NxChannel Channel { get; } = channel;
    public ChannelReader<LiveEvent> Reader { get; } = reader;
    int left;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref left, 1) == 0) leave();
    }
}

/// <summary>
/// **局ごとの NX-Jikkyo の受け手をまとめる。** 購読者がいる局にだけ繋ぐ。局は方針の一覧で絞ってから来る。
/// 独自コメントも同じ控えに入れて配る。通し番号は hub の中で 1 つ (局を閉じて開き直しても戻らない)。<see cref="Epoch"/> は起動ごとに替わる
/// </summary>
public sealed class LiveHub(JikkyoSettings settings, NxConnect connect, PolicyHolder policy, HubUsers users, JikkyoStore store, TimeProvider time, ILogger<LiveHub> log) : IDisposable
{
    /// <summary>起動ごとに替わる印。denpa はこれが替わったら通し番号を捨てて最初から受ける</summary>
    public string Epoch { get; } = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));

    long seq;

    readonly ConcurrentDictionary<string, NxChannel> channels = new(StringComparer.Ordinal);
    readonly Lock gate = new();

    /// <summary>購読する。<paramref name="after"/> は前に受けた最後の通し番号 (<paramref name="epoch"/> が今のものと同じときだけ使う)</summary>
    public Subscription Subscribe(string channel, long? after, string? epoch)
    {
        lock (gate)
        {
            if (!channels.TryGetValue(channel, out var live))
            {
                live = new NxChannel(channel, settings, connect, policy, users, time, log, Retire, () => Interlocked.Increment(ref seq));
                // 繋ぐ前の 15 分の独自コメントを控えに入れておく (hub を立て直した直後や、しばらく誰も観ていなかった局)
                var now = time.GetUtcNow().ToUnixTimeMilliseconds();
                live.Seed(store.Comments(channel, now - (long)NxChannel.Keep.TotalMilliseconds, now + 1).Where(c => !c.Removed).Select(c => c.ToComment()));
                channels[channel] = live;
            }
            return live.Join(epoch == Epoch ? after : null);
        }
    }

    /// <summary>独自コメントを配る (購読者のいない局なら何もしない。DB にはある)</summary>
    public void Publish(string channel, Comment comment)
    {
        if (channels.TryGetValue(channel, out var live)) live.Add([comment]);
    }

    /// <summary>方針で消したもの・NG ワードを、控えからも外す</summary>
    public void Apply(Policy next)
    {
        foreach (var live in channels.Values)
        {
            live.Filter(next);
            // 一覧から外した局は閉じる (購読も終わり、denpa が繋ぎ直すと unknown_channel で断る)
            if (!next.Channels.Contains(live.Name))
            {
                lock (gate) channels.TryRemove(new KeyValuePair<string, NxChannel>(live.Name, live));
                live.Dispose();
            }
        }
    }

    /// <summary>NX-Jikkyo に繋がっているか (書き込みを中継するか決める)</summary>
    public bool IsUp(string channel) => channels.TryGetValue(channel, out var live) && live.NxUp;

    /// <summary>購読者のいなくなった局を外す。外したら true (そのあと閉じる)。購読を始めるのと同じロックの中で決める</summary>
    bool Retire(NxChannel live)
    {
        lock (gate)
        {
            if (!live.Idle) return false;
            channels.TryRemove(new KeyValuePair<string, NxChannel>(live.Name, live));
            return true;
        }
    }

    public void Dispose()
    {
        foreach (var live in channels.Values) live.Dispose();
    }
}

/// <summary>
/// **NX-Jikkyo の局 1 つに 1 本で繋ぎ、受けたものを控えて購読者に配る。** やり取りは端末の nx.ts だったものと同じ
/// (docs/jikkyo.md の「NX-Jikkyo から受ける」)。最後の購読者が去って <see cref="Linger"/> たったら閉じる
/// </summary>
public sealed class NxChannel : IDisposable
{
    /// <summary>控えに持つ長さ。録画中の追っかけが過去ログに入る前 (10 分) を埋められるだけ</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromMinutes(15);
    /// <summary>控えの件数の上限</summary>
    public const int KeepCount = 3000;
    /// <summary>購読を始めたときに送る件数の上限</summary>
    public const int SendCount = 1000;
    /// <summary>最後の購読者が去ってから閉じるまで。局を行き来する人のために少し残す</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(60);
    /// <summary>続けて何回失敗したら「NX-Jikkyo に繋がらない」にするか</summary>
    const int FailuresToDown = 5;
    static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    /// <summary>繋いだときに遡る件数 (NX-Jikkyo の上限)</summary>
    const int ResFrom = 1000;

    readonly string channel;
    readonly JikkyoSettings settings;
    readonly NxConnect connect;
    readonly PolicyHolder policy;
    readonly HubUsers users;
    readonly TimeProvider time;
    readonly ILogger log;
    readonly Func<NxChannel, bool> retire;
    readonly Lock gate = new();
    readonly List<(long Seq, Comment C)> ring = [];
    readonly Func<long> next;
    readonly HashSet<(long, string)> keys = [];
    readonly List<Channel<LiveEvent>> subscribers = [];
    readonly CancellationTokenSource stop = new();
    ITimer? linger;
    Task? loop;
    bool up = true;

    public string Name => channel;

    public bool NxUp
    {
        get { lock (gate) return up; }
    }

    internal NxChannel(string channel, JikkyoSettings settings, NxConnect connect, PolicyHolder policy, HubUsers users, TimeProvider time, ILogger log, Func<NxChannel, bool> retire, Func<long> next)
    {
        this.next = next;
        this.channel = channel;
        this.settings = settings;
        this.connect = connect;
        this.policy = policy;
        this.users = users;
        this.time = time;
        this.log = log;
        this.retire = retire;
    }

    internal void Seed(IEnumerable<Comment> comments)
    {
        lock (gate) foreach (var comment in comments) Insert(comment);
    }

    /// <summary>購読を始める。<paramref name="after"/> より後の通し番号のものを送る (無い・控えより古いときは控えの全部)</summary>
    internal Subscription Join(long? after)
    {
        var queue = Channel.CreateBounded<LiveEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (gate)
        {
            linger?.Dispose();
            linger = null;
            Trim();
            queue.Writer.TryWrite(new LiveEvent.State(up));
            var p = policy.Current;
            var from = after is { } a && ring.Count > 0 && a >= ring[0].Seq - 1 ? a : long.MinValue;
            var recent = ring.Where(e => e.Seq > from && !p.IsNg(e.C.Text)).TakeLast(SendCount).ToList();
            if (recent.Count > 0) queue.Writer.TryWrite(new LiveEvent.Comments([.. recent.Select(e => e.C)], recent.Max(e => e.Seq)));
            subscribers.Add(queue);
            loop ??= Task.Run(() => RunAsync(stop.Token));
        }
        return new Subscription(this, queue.Reader, () => Leave(queue));
    }

    void Leave(Channel<LiveEvent> queue)
    {
        lock (gate)
        {
            subscribers.Remove(queue);
            queue.Writer.TryComplete();
            if (subscribers.Count > 0 || stop.IsCancellationRequested) return;
            linger = time.CreateTimer(_ => Close(), null, Linger, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>購読者がいない (閉じてよい)</summary>
    internal bool Idle
    {
        get { lock (gate) return subscribers.Count == 0; }
    }

    void Close()
    {
        if (retire(this)) Dispose();
    }

    /// <summary>控えに足して配る。同じ時刻・同じ本文のものと、NG ワードを含むものは配らない</summary>
    internal void Add(IEnumerable<Comment> comments)
    {
        var p = policy.Current;
        List<(long Seq, Comment C)> added = [];
        lock (gate)
        {
            foreach (var comment in comments)
                if (Insert(comment) is { } seq && !p.IsNg(comment.Text)) added.Add((seq, comment));
            Trim();
            if (added.Count == 0) return;
            var message = new LiveEvent.Comments([.. added.Select(e => e.C)], added.Max(e => e.Seq));
            foreach (var subscriber in subscribers) subscriber.Writer.TryWrite(message);
        }
    }

    internal void Filter(Policy next)
    {
        lock (gate)
        {
            for (var i = ring.Count - 1; i >= 0; i--)
            {
                var c = ring[i].C;
                if ((c.Id is not null && next.RemovedComments.Contains(c.Id)) || next.IsNg(c.Text))
                {
                    keys.Remove(c.Key);
                    ring.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>控えに入れる。入れたら通し番号、もうあれば null</summary>
    long? Insert(Comment comment)
    {
        if (!keys.Add(comment.Key)) return null;
        // 時刻の順に入れる (繋いだときの遡りは古いものが後から来る)
        var at = ring.Count;
        while (at > 0 && ring[at - 1].C.At > comment.At) at--;
        var seq = next();
        ring.Insert(at, (seq, comment));
        return seq;
    }

    void Trim()
    {
        var before = time.GetUtcNow().ToUnixTimeMilliseconds() - (long)Keep.TotalMilliseconds;
        var drop = 0;
        while (drop < ring.Count && (ring[drop].C.At < before || ring.Count - drop > KeepCount)) drop++;
        for (var i = 0; i < drop; i++) keys.Remove(ring[i].C.Key);
        ring.RemoveRange(0, drop);
    }

    void SetUp(bool next)
    {
        lock (gate)
        {
            if (up == next) return;
            up = next;
            var message = new LiveEvent.State(next);
            foreach (var subscriber in subscribers) subscriber.Writer.TryWrite(message);
        }
        log.LogInformation("jikkyo nx: {State}", next ? "up" : "down");
    }

    async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var (live, clean) = (false, false);
            try
            {
                (live, clean) = await SessionAsync(() => { failures = 0; SetUp(true); }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                log.LogDebug("jikkyo nx session failed ({Type})", e.GetType().Name);
            }
            if (!(clean && live)) failures++;
            if (failures >= FailuresToDown) SetUp(false);
            var wait = clean && live
                ? TimeSpan.FromSeconds(1)
                : TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Max(0, failures - 1))));
            try
            {
                await Task.Delay(wait, time, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>視聴の WS → コメントの WS の 1 回分。スレッドを受けたら <paramref name="settled"/> を呼ぶ。(受けたか, きれいに閉じられたか)</summary>
    async Task<(bool Live, bool Clean)> SessionAsync(Action settled, CancellationToken outer)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ct = session.Token;
        var watch = await connect(NxSocket.WatchUri(settings.NxOrigin, channel), null, ct);
        var live = false;
        Task<bool>? comments = null;
        Task? keep = null;
        try
        {
            await NxSocket.SendAsync(watch, NxSocket.StartWatching, ct);
            while (true)
            {
                var receive = NxSocket.ReceiveAsync(watch, ct);
                var done = comments is null ? await Task.WhenAny(receive) : await Task.WhenAny(receive, comments);
                if (done == comments)
                {
                    // コメントの WS が閉じた。視聴の WS からやり直す
                    var clean = await comments;
                    return (live, clean);
                }
                var text = await receive;
                if (text is null)
                    return (live, watch.CloseStatus == System.Net.WebSockets.WebSocketCloseStatus.NormalClosure);
                var (type, data) = NxSocket.ReadWatch(text, out var document);
                using (document)
                {
                    switch (type)
                    {
                        case "ping":
                            await NxSocket.SendAsync(watch, NxSocket.Pong, ct);
                            break;
                        case "seat":
                            var every = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("keepIntervalSec", out var k) && k.TryGetInt32(out var s) && s > 0 ? s : 30;
                            keep ??= KeepSeatAsync(watch, TimeSpan.FromSeconds(every), ct);
                            break;
                        case "room":
                            var uri = NxSocket.CommentUri(data, settings.NxOrigin);
                            var thread = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("threadId", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                            if (uri is null || thread is null)
                            {
                                log.LogWarning("jikkyo nx: room の形が違います。NX-Jikkyo のバージョンが変わったかもしれません");
                                break;
                            }
                            comments ??= CommentsAsync(uri, thread, () => { live = true; settled(); }, ct);
                            break;
                        case "disconnect":
                            // スレッドの終わり (毎日 4:00)。視聴の WS から繋ぎ直す
                            return (live, true);
                    }
                }
            }
        }
        finally
        {
            session.Cancel();
            await NxSocket.CloseAsync(watch);
            if (comments is not null) await comments.ContinueWith(_ => { }, TaskScheduler.Default);
            if (keep is not null) await keep.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    async Task KeepSeatAsync(System.Net.WebSockets.WebSocket watch, TimeSpan every, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(every, time, ct);
            await NxSocket.SendAsync(watch, NxSocket.KeepSeat, ct);
        }
    }

    /// <summary>コメントの WS。閉じたら、きれいに閉じられたかを返す</summary>
    async Task<bool> CommentsAsync(Uri uri, string thread, Action settled, CancellationToken ct)
    {
        var ws = await connect(uri, null, ct);
        try
        {
            await NxSocket.SendAsync(ws, $$$"""[{"ping":{"content":"rs:0"}},{"ping":{"content":"ps:0"}},{"thread":{"version":"20061206","thread":"{{{JsonEncodedText.Encode(thread)}}}","threadkey":"","res_from":-{{{ResFrom}}}}},{"ping":{"content":"pf:0"}},{"ping":{"content":"rf:0"}}]""", ct);
            var warned = false;
            while (true)
            {
                var text = await NxSocket.ReceiveAsync(ws, ct);
                if (text is null) return ws.CloseStatus == System.Net.WebSockets.WebSocketCloseStatus.NormalClosure;
                List<Comment> got = [];
                var odd = false;
                try
                {
                    using var document = JsonDocument.Parse(text);
                    var root = document.RootElement;
                    IEnumerable<JsonElement> items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : [root];
                    foreach (var item in items)
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        if (item.TryGetProperty("thread", out _)) settled();
                        if (!item.TryGetProperty("chat", out var chat)) continue;
                        var comment = Comment.FromChat(chat, out var user, out var bad);
                        odd |= bad;
                        // hub の利用者が書いたもの (もう配った独自コメント) は捨てる
                        if (comment is not null && !users.Contains(user)) got.Add(comment);
                    }
                }
                catch (JsonException)
                {
                    odd = true;
                }
                if (odd && !warned)
                {
                    warned = true;
                    log.LogWarning("jikkyo nx: chat の形が一部違います。NX-Jikkyo のバージョンが変わったかもしれません (読めたぶんだけ配ります)");
                }
                if (got.Count > 0) Add(got);
            }
        }
        finally
        {
            await NxSocket.CloseAsync(ws);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (stop.IsCancellationRequested) return;
            stop.Cancel();
            linger?.Dispose();
            foreach (var subscriber in subscribers) subscriber.Writer.TryComplete();
            subscribers.Clear();
        }
    }
}
