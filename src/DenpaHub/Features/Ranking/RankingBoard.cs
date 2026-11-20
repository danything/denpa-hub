using System.Text.Encodings.Web;
using System.Text.Json;

namespace DenpaHub.Features.Ranking;

/// <summary>
/// メモリに置く順位。10 分ごとに作り直し、全部のトークンに同じものを返す。
/// 決まり (5 トークン未満は出さない・ルールの予約の重み・5 の倍数への切り下げ) は docs/ranking.md の「少ない人数の番組は出さない」と「ルールの予約の重み」。
/// 地上波の地域をまたぐ放送は、同じ番組を 1 つにまとめてから数える (<see cref="Grouping"/>)
/// </summary>
public sealed class RankingBoard(RankingStore store, TimeProvider time)
{
    /// <summary>出す番組の、票を入れたトークンの数の下限 (重みを掛ける前)。1・2 の家の予約を推し量れないようにする</summary>
    public const int MinTokens = 5;
    /// <summary>ルールの予約の重み。広い条件で当たる分を軽くする</summary>
    public const double RuleWeight = 0.5;
    /// <summary>返す votes の刻み。1 票ずつの増減を見せない</summary>
    public const int Step = 5;
    /// <summary>持っておく件数。GET /ranking の limit の上限</summary>
    public const int MaxEntries = 300;

    static readonly TimeSpan Japan = TimeSpan.FromHours(9);

    sealed record Snapshot(DateTimeOffset GeneratedAt, IReadOnlyList<Entry> Entries);

    /// <summary>順位の 1 件。<paramref name="Lead"/> は名前とジャンルを出す放送 (まとめた中で票を入れたトークンのいちばん多いもの)</summary>
    /// <param name="Airings">まとめた放送の全部。始まりの順</param>
    /// <param name="Affiliation">地上波の系列の名前 (「TBS 系」)。無ければ null</param>
    public sealed record Entry(int Rank, int Votes, Candidate Lead, IReadOnlyList<Candidate> Airings, string? Affiliation);

    Snapshot current = new(DateTimeOffset.MinValue, []);

    public IReadOnlyList<Entry> Entries => current.Entries;

    /// <summary>順位を作り直す。始まった放送は入れない</summary>
    public void Rebuild()
    {
        var now = time.GetUtcNow();
        // 地上波は 1 つの放送ではトークンが足りなくても、地域をまたいでまとめると足りることがあるので、全部を読んでからまとめる
        var groups = Grouping.Group(store.ReadCandidates(MinTokens, now));
        var entries = groups
            // 数えるのはトークンの数 (重みを掛ける前)。まとめた放送の合計
            .Where(g => g.Sum(c => c.Manual + c.Rule) >= MinTokens)
            .Select(g =>
            {
                var airings = g.OrderBy(c => c.StartAt).ThenBy(c => c.Id.ServiceId).ThenBy(c => c.Id.EventId).ToList();
                var lead = airings.MaxBy(c => c.Manual + c.Rule)!;
                var affiliation = Terrestrial.AffiliationOf(lead.Id) is { } a ? Terrestrial.AffiliationNames[a] : null;
                return (Votes: Round(g.Sum(c => c.Manual) + g.Sum(c => c.Rule) * RuleWeight), Lead: lead, Airings: airings, Affiliation: affiliation);
            })
            // 切り下げて 0 になる番組 (ルールの予約だけで 5〜9 トークン) は出さない
            .Where(e => e.Votes > 0)
            // 切り下げた数で並べ、同じ数の中は放送の順にする。切り下げる前の数で並べると、並びから 1 票ずつの差が見えるため
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

    static int Round(double weighted) => (int)Math.Floor(weighted / Step) * Step;

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
                    json.WriteString("start_at", Iso(DateTimeOffset.FromUnixTimeSeconds(airing.StartAt)));
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

    static string Iso(DateTimeOffset value) => value.ToOffset(Japan).ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>順位を 10 分ごとに作り直し、消す時期を過ぎたものを 1 時間ごとに消す</summary>
public sealed class RankingWorker(RankingStore store, RankingBoard board, TimeProvider time, ILogger<RankingWorker> log) : BackgroundService
{
    public static readonly TimeSpan RebuildInterval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 最初の回も起動 (Kestrel が受け始めるまで) を止めずに、後ろで動かす
        await Task.Yield();
        var lastCleanup = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(RebuildInterval);
        do
        {
            try
            {
                var now = time.GetUtcNow();
                if (now - lastCleanup >= CleanupInterval)
                {
                    var (marks, airings, tokens) = store.Cleanup(now);
                    lastCleanup = now;
                    log.LogInformation("ranking cleanup: marks={Marks} airings={Airings} tokens={Tokens}", marks, airings, tokens);
                }
                board.Rebuild();
                log.LogInformation("ranking rebuilt: entries={Entries}", board.Entries.Count);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // 次の回に作り直す。順位は前のものを返し続ける
                log.LogError("ranking rebuild failed ({Type})", e.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
