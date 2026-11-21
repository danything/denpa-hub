using System.Text.Encodings.Web;
using System.Text.Json;

namespace DenpaHub.Features.Ranking;

/// <summary>
/// メモリに置く順位 (人気の番組)。毎時 0 分に作り直し、全部のトークンに同じものを返す。
/// 数えるのは家の数 (予約した家とライブで観た家の和集合。1 家 1)。決まり (5 トークン未満は出さない・5 の倍数への切り下げ) は docs/ranking.md の「少ない人数の番組は出さない」。
/// 地上波の地域をまたぐ放送は、同じ番組を 1 つにまとめてから数える (<see cref="Grouping"/>)
/// </summary>
public sealed class RankingBoard(RankingStore store, TimeProvider time)
{
    /// <summary>出す番組の、一覧の数に入るトークンの数の下限。1・2 の家の予約を推し量れないようにする</summary>
    public const int MinTokens = 5;
    /// <summary>返す votes の刻み。1 票ずつの増減を見せない</summary>
    public const int Step = 5;
    /// <summary>持っておく件数。GET /ranking の limit の上限</summary>
    public const int MaxEntries = 300;
    /// <summary>一覧に入れる、終わった放送の幅。昨日よく観られた番組から、ルールを作れるように</summary>
    public static readonly TimeSpan ListAfterEnd = TimeSpan.FromHours(24);

    static readonly TimeSpan Japan = TimeSpan.FromHours(9);

    sealed record Snapshot(DateTimeOffset GeneratedAt, IReadOnlyList<Entry> Entries);

    /// <summary>順位の 1 件。<paramref name="Lead"/> は名前とジャンルを出す放送 (まとめた中で一覧の数のいちばん多いもの)</summary>
    /// <param name="Airings">まとめた放送の全部。始まりの順</param>
    /// <param name="Affiliation">地上波の系列の名前 (「TBS 系」)。無ければ null</param>
    public sealed record Entry(int Rank, int Votes, Candidate Lead, IReadOnlyList<Candidate> Airings, string? Affiliation);

    Snapshot current = new(DateTimeOffset.MinValue, []);

    public IReadOnlyList<Entry> Entries => current.Entries;

    /// <summary>順位を作り直す。これから・放送中・終わって 24 時間以内の放送を、数の多い順に混ぜて並べる</summary>
    public void Rebuild()
    {
        var now = time.GetUtcNow();
        // 地上波は 1 つの放送ではトークンが足りなくても、地域をまたいでまとめると足りることがあるので、全部を読んでからまとめる
        var groups = Grouping.Group(store.ReadListed(MinTokens, (now - ListAfterEnd).ToUnixTimeSeconds()));
        var entries = groups
            .Select(g =>
            {
                var airings = g.OrderBy(c => c.StartAt).ThenBy(c => c.Id.ServiceId).ThenBy(c => c.Id.EventId).ToList();
                var lead = airings.MaxBy(c => c.Counts.Homes)!;
                var affiliation = Terrestrial.AffiliationOf(lead.Id) is { } a ? Terrestrial.AffiliationNames[a] : null;
                // 数えるのはまとめた放送の合計
                return (Homes: g.Sum(c => c.Counts.Homes), Lead: lead, Airings: airings, Affiliation: affiliation);
            })
            .Where(e => e.Homes >= MinTokens)
            // 切り下げた数で並べ、同じ数の中は放送の順にする。切り下げる前の数で並べると、並びから 1 つずつの差が見えるため
            .Select(e => (Votes: RankingStore.Round(e.Homes), e.Lead, e.Airings, e.Affiliation))
            .OrderByDescending(e => e.Votes)
            .ThenBy(e => e.Airings[0].StartAt)
            .ThenBy(e => e.Airings[0].Id.NetworkId)
            .ThenBy(e => e.Airings[0].Id.ServiceId)
            .ThenBy(e => e.Airings[0].Id.EventId)
            .Take(MaxEntries)
            .Select((e, i) => new Entry(i + 1, e.Votes, e.Lead, e.Airings, e.Affiliation))
            .ToList();
        current = new Snapshot(now, entries);
    }

    /// <summary>GET /ranking の本文 (docs/ranking.md の形)</summary>
    public byte[] Write(int limit)
    {
        var snapshot = current;
        using var buffer = new MemoryStream();
        // 番組名は日本語なので、`\uXXXX` にエスケープせずに書く (応答は JSON だけで、HTML には埋めない)
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteNumber("v", 1);
            json.WriteString("generated_at", Iso(snapshot.GeneratedAt));
            json.WriteStartArray("entries");
            foreach (var entry in snapshot.Entries.Take(limit))
            {
                json.WriteStartObject();
                json.WriteNumber("rank", entry.Rank);
                json.WriteNumber("votes", entry.Votes);
                json.WriteString("name", entry.Lead.Name);
                json.WriteStartArray("genres");
                foreach (var genre in entry.Lead.Genres) json.WriteNumberValue(genre);
                json.WriteEndArray();
                json.WriteString("affiliation", entry.Affiliation);
                json.WriteStartArray("airings");
                foreach (var airing in entry.Airings)
                {
                    json.WriteStartObject();
                    json.WriteNumber("network_id", airing.Id.NetworkId);
                    json.WriteNumber("service_id", airing.Id.ServiceId);
                    json.WriteNumber("event_id", airing.Id.EventId);
                    json.WriteString("start_at", Iso(airing.StartAt));
                    json.WriteString("end_at", Iso(airing.EndAt));
                    json.WriteString("service_name", airing.ServiceName);
                    json.WriteString("region", Terrestrial.RegionName(airing.Id));
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>日本時間の ISO 8601 (<c>2026-11-20T21:00:00+09:00</c>)</summary>
    public static string Iso(DateTimeOffset value) => value.ToOffset(Japan).ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Unix 秒を日本時間の ISO 8601 に</summary>
    public static string Iso(long seconds) => Iso(DateTimeOffset.FromUnixTimeSeconds(seconds));
}

/// <summary>
/// 毎時 0 分に、消す時期を過ぎたものを消し (終わって 8 日たった放送は集計の表に移す)、順位を作り直す。起動したときにも 1 回する。
/// 順位を 1 時間ごとにまとめるのは、1 つの家の送信の前後で数が動いたのを見て、その家の観た番組の組を推し量れないようにするため
/// (家ごとの送る位置は 6 時間に散っているので、1 時間に何軒かの送信が混ざる)
/// </summary>
public sealed class RankingWorker(RankingStore store, RankingBoard board, TimeProvider time, ILogger<RankingWorker> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary><paramref name="now"/> の次の毎時 0 分までの長さ</summary>
    public static TimeSpan UntilNext(DateTimeOffset now)
    {
        var next = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset) + Interval;
        return next - now;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 最初の回も起動 (Kestrel が受け始めるまで) を止めずに、後ろで動かす
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var (marks, airings, tallies, tokens) = store.Cleanup(time.GetUtcNow());
                log.LogInformation("ranking cleanup: marks={Marks} airings={Airings} tallies={Tallies} tokens={Tokens}", marks, airings, tallies, tokens);
                board.Rebuild();
                log.LogInformation("ranking rebuilt: entries={Entries}", board.Entries.Count);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // 次の回に作り直す。順位は前のものを返し続ける
                log.LogError("ranking rebuild failed ({Type})", e.GetType().Name);
            }
            await Task.Delay(UntilNext(time.GetUtcNow()), time, stoppingToken);
        }
    }
}
