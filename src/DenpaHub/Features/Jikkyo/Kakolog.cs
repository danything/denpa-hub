using System.Collections.Concurrent;
using System.Text.Json;

namespace DenpaHub.Features.Jikkyo;

/// <summary>
/// **過去ログ API から取得し、1 時間ごとの塊でメモリに持つ。** 端末ごとに取得していたものを hub でまとめ、運営者の負担を減らす。
/// 決まりと数値のわけは docs/jikkyo.md の「過去ログ」
/// </summary>
public sealed class Kakolog(JikkyoSettings settings, HttpClient http, JikkyoStore store, Identity identity, PolicyHolder policy, TimeProvider time, ILogger<Kakolog> log)
{
    /// <summary>塊の長さ (正時から)</summary>
    public const long Block = 60 * 60_000;
    /// <summary>1 回に取得してよい長さ (過去ログ API の決まり)</summary>
    public const long MaxSpan = 3 * 24 * 60 * 60_000;
    /// <summary>上流への 1 回の取得の長さ。混む局の 3 日分を一度に読むとメモリに収まらないので、6 時間ずつにする</summary>
    public const long FetchSpan = 6 * 60 * 60_000;
    /// <summary>過去ログに入るまでの遅れ。5 分おきに集め、集めるのに 3 分ほどかかる (案内ページ) のに余裕を足した</summary>
    public const long Lag = 10 * 60_000;
    /// <summary>終わって <see cref="Lag"/> たった塊を持つ長さ</summary>
    public static readonly TimeSpan SettledLife = TimeSpan.FromHours(6);
    /// <summary>まだ入りきっていない塊を持つ長さ。denpa は 5 分おきに取り足す</summary>
    public static readonly TimeSpan FreshLife = TimeSpan.FromMinutes(5);
    /// <summary>取得できなかった塊を、取り直さずに失敗として返す長さ (止まっている相手を呼び続けない)</summary>
    public static readonly TimeSpan FailedLife = TimeSpan.FromSeconds(60);
    /// <summary>持つ件数の上限。1 件 150 バイトほどで 30 MB ほど</summary>
    public const int MaxCached = 200_000;
    /// <summary>1 リクエストが取得を待つ上限。Cloudflare の 100 秒より十分短く。間に合わない塊は complete: false にする (取得は続けてキャッシュに入れる)</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    /// <summary>取得を待つ列の上限 (回)。超えた分は取得せず complete: false (失敗はキャッシュに入れない)</summary>
    public const int MaxQueued = 16;
    /// <summary>これより前に使われた塊だけを追い出す (組み立て中のリクエストの塊を追い出さない)</summary>
    const long InUse = 30_000;

    sealed record Entry(IReadOnlyList<Comment>? Comments, long ExpiresAt)
    {
        public long LastUsed;
    }

    readonly ConcurrentDictionary<(string Channel, long Start), Task<Entry>> blocks = new();
    readonly SemaphoreSlim upstream = new(2, 2);
    int cached;
    int queued;
    bool warned;

    /// <summary><c>[from, to)</c> のコメント (時刻の順)。過去ログ API に届かなかったところがあれば complete は false</summary>
    public async Task<(List<Comment> Comments, bool Complete)> GetAsync(string channel, long from, long to, CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        // 過去ログ API には今より後を訊かない (独自コメントは to まで返す)
        var upTo = Math.Min(to, now);
        var starts = new List<long>();
        for (var start = from - from % Block; start < upTo; start += Block) starts.Add(start);

        // 無い塊は、続いたものをまとめて 1 回で取得する
        var tasks = new Dictionary<long, Task<Entry>>();
        List<long> run = [];
        foreach (var start in starts)
        {
            if (TryCached(channel, start, now, out var task)) { Flush(); tasks[start] = task; continue; }
            run.Add(start);
            if (run.Count * Block >= FetchSpan) Flush();
        }
        Flush();

        var complete = true;
        var p = policy.Current;
        var list = new List<Comment>();
        var deadline = time.GetUtcNow() + Deadline;
        foreach (var start in starts)
        {
            Entry entry;
            try
            {
                var left = deadline - time.GetUtcNow();
                entry = await tasks[start].WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero, time, ct);
            }
            catch (TimeoutException)
            {
                complete = false;
                continue;
            }
            entry.LastUsed = now;
            if (entry.Comments is null) { complete = false; continue; }
            foreach (var comment in entry.Comments)
                if (comment.At >= from && comment.At < to && !p.IsNg(comment.Text)) list.Add(comment);
        }
        foreach (var own in store.Comments(channel, from, to))
            if (!own.Removed && !p.IsNg(own.Text)) list.Add(own.ToComment());
        list.Sort((a, b) => a.At.CompareTo(b.At));
        return (list, complete);

        void Flush()
        {
            if (run.Count == 0) return;
            var first = run[0];
            var last = run[^1] + Block;
            if (Volatile.Read(ref queued) >= MaxQueued)
            {
                foreach (var start in run) tasks[start] = Task.FromResult(new Entry(null, 0));
                run = [];
                return;
            }
            var sources = run.ToDictionary(s => s, _ => new TaskCompletionSource<Entry>(TaskCreationOptions.RunContinuationsAsynchronously));
            var mine = new List<long>();
            foreach (var (start, source) in sources)
            {
                // ほかのリクエストが同時に取りに行っていれば、そちらを待つ
                var task = Claim((channel, start), source.Task, now);
                tasks[start] = task;
                if (task == source.Task) mine.Add(start);
            }
            run = [];
            if (mine.Count == 0) return;
            _ = FetchAsync(channel, first, last, now, sources);
        }
    }

    /// <summary>
    /// 塊の置き場を取る。取得中か期限の切れていない塊があればそれを返し、無ければ <paramref name="mine"/> を置いて返す。
    /// 期限の切れた塊を置き換えたときは、その件数を数えから引く
    /// </summary>
    Task<Entry> Claim((string, long) key, Task<Entry> mine, long now)
    {
        while (true)
        {
            if (blocks.TryGetValue(key, out var old))
            {
                if (Usable(old, now)) return old;
                if (!blocks.TryUpdate(key, mine, old)) continue;
                if (old.IsCompletedSuccessfully) Interlocked.Add(ref cached, -(old.Result.Comments?.Count ?? 0));
                return mine;
            }
            if (blocks.TryAdd(key, mine)) return mine;
        }
    }

    bool TryCached(string channel, long start, long now, out Task<Entry> task)
    {
        if (blocks.TryGetValue((channel, start), out task!) && Usable(task, now)) return true;
        task = null!;
        return false;
    }

    /// <summary>取得中か、期限の切れていない塊</summary>
    static bool Usable(Task<Entry> task, long now) => !task.IsCompleted || (task.IsCompletedSuccessfully && task.Result.ExpiresAt > now);

    async Task FetchAsync(string channel, long first, long last, long now, Dictionary<long, TaskCompletionSource<Entry>> sources)
    {
        List<Comment>? got = null;
        Interlocked.Increment(ref queued);
        await upstream.WaitAsync();
        Interlocked.Decrement(ref queued);
        try
        {
            got = await ReadAsync(channel, first, Math.Min(last, now), now);
        }
        catch (Exception e)
        {
            log.LogInformation("jikkyo kakolog failed ({Type})", e.GetType().Name);
        }
        finally
        {
            upstream.Release();
        }
        now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var byBlock = sources.Keys.ToDictionary(s => s, _ => new List<Comment>());
        if (got is not null)
            foreach (var comment in got)
            {
                var start = comment.At - comment.At % Block;
                if (byBlock.TryGetValue(start, out var bucket)) bucket.Add(comment);
            }
        foreach (var (start, source) in sources)
        {
            var settled = start + Block + Lag <= now;
            var life = got is null ? FailedLife : settled ? SettledLife : FreshLife;
            var comments = got is null ? null : byBlock[start];
            if (comments is not null) Interlocked.Add(ref cached, comments.Count);
            source.SetResult(new Entry(comments, now + (long)life.TotalMilliseconds) { LastUsed = now });
        }
        Evict(now);
    }

    async Task<List<Comment>> ReadAsync(string channel, long from, long to, long now)
    {
        // 今より後にはできない (エラーになる)
        var end = Math.Min((to + 999) / 1000, now / 1000);
        var url = new Uri(settings.KakologOrigin, $"/api/kakolog/{channel}?starttime={from / 1000}&endtime={end}&format=json");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await http.GetAsync(url, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"status {(int)response.StatusCode}");
        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
        var root = json.RootElement;
        // エラーも HTTP 200 で {"error": …} が返る。{"packet": []} はエラーではなく「コメントなし」
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _) || !root.TryGetProperty("packet", out var packets) || packets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("kakolog shape");
        // hub が NX-Jikkyo に中継した独自コメントの写しは捨てる (独自コメントとして別に返す)。消したものも、写しを捨てるために照合には入れる
        var own = store.Comments(channel, from, to + 1);
        var hubUsers = own.Select(c => identity.NxUserId(c.Author)).ToHashSet(StringComparer.Ordinal);
        var list = new List<Comment>();
        var odd = 0;
        foreach (var packet in packets.EnumerateArray())
        {
            if (packet.ValueKind != JsonValueKind.Object || !packet.TryGetProperty("chat", out var chat)) continue;
            var comment = Comment.FromChat(chat, out var user, out var bad);
            if (bad) odd++;
            if (comment is null || (user is not null && hubUsers.Contains(user))) continue;
            list.Add(comment);
        }
        if (odd > 0 && !warned)
        {
            warned = true;
            log.LogWarning("jikkyo kakolog: 返事の形が一部違います。過去ログ API のバージョンが変わったかもしれません (読めたぶんだけ返します)");
        }
        return list;
    }

    /// <summary>上限を超えたら、いちばん長く使われていない塊から捨てる。期限の切れたものも捨てる</summary>
    void Evict(long now)
    {
        foreach (var (key, task) in blocks)
            if (task.IsCompletedSuccessfully && task.Result.ExpiresAt <= now && blocks.TryRemove(new KeyValuePair<(string, long), Task<Entry>>(key, task)))
                Interlocked.Add(ref cached, -(task.Result.Comments?.Count ?? 0));
        if (Volatile.Read(ref cached) <= MaxCached) return;
        foreach (var (key, task) in blocks.Where(b => b.Value.IsCompletedSuccessfully).Where(b => b.Value.Result.LastUsed < now - InUse).OrderBy(b => b.Value.Result.LastUsed).ToList())
        {
            if (Volatile.Read(ref cached) <= MaxCached) break;
            if (blocks.TryRemove(new KeyValuePair<(string, long), Task<Entry>>(key, task)))
                Interlocked.Add(ref cached, -(task.Result.Comments?.Count ?? 0));
        }
    }

    /// <summary>持っている件数 (テスト用)</summary>
    internal int Cached => Volatile.Read(ref cached);
}
