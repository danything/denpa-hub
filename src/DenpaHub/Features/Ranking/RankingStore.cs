using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DenpaHub.Features.Ranking;

/// <summary>トークンの記録。<paramref name="SentAt"/> は最後に送った時刻 (Unix 秒)。まだ送っていなければ null</summary>
public sealed record TokenRecord(long? SentAt);

/// <summary>
/// 放送 1 つ (か、まとめた番組 1 つ) の数。どれも印を入れたトークンの数で、1 つの家は 1。
/// <paramref name="Tokens"/> は種類によらず印のあるトークン、<paramref name="Homes"/> は一覧の数 (予約とライブの和集合)
/// </summary>
public readonly record struct Counts(int Tokens, int Homes, int Recorded, int Live, int Timeshift)
{
    public static Counts Of(Kinds kinds) => new(
        1,
        (kinds & Kinds.Listed) != 0 ? 1 : 0,
        (kinds & Kinds.Recorded) != 0 ? 1 : 0,
        (kinds & Kinds.Live) != 0 ? 1 : 0,
        (kinds & Kinds.Timeshift) != 0 ? 1 : 0);

    public static Counts operator +(Counts a, Counts b) =>
        new(a.Tokens + b.Tokens, a.Homes + b.Homes, a.Recorded + b.Recorded, a.Live + b.Live, a.Timeshift + b.Timeshift);

    public static Counts operator -(Counts a) => new(-a.Tokens, -a.Homes, -a.Recorded, -a.Live, -a.Timeshift);

    public static Counts operator -(Counts a, Counts b) => a + -b;
}

/// <summary>
/// 順位と集計の元になる放送 1 つ。名前などは印を入れたトークンの多いもの。
/// <paramref name="LastEnd"/> は送られた中でいちばん遅い終わり (集計の表に移す時期を決める)
/// </summary>
public sealed record Candidate(AiringId Id, Counts Counts, string Name, string ServiceName, IReadOnlyList<int> Genres, long StartAt, int Duration, long LastEnd)
{
    public long EndAt => StartAt + Duration;
}

/// <summary>
/// 番組ランキングのデータ (SQLite のファイル 1 つ)。表の形と、何を持たないかは docs/ranking.md の「データの持ち方」。
///
/// 接続は 1 つで、ロックで順に使う。1 台のプロセスだけがファイルを開く前提 (Deployment は replicas: 1・Recreate)。
/// 集計の表を読み出すだけのもの (<see cref="Tallies"/>) は、別のプロセスから読み取り専用で開く
/// </summary>
public sealed class RankingStore : IDisposable
{
    /// <summary>表の形のバージョン。変えたら、中身を捨てて作り直す (どれも放送が終われば消すもので、denpa が送り直せば戻るため)</summary>
    public const int SchemaVersion = 2;

    /// <summary>印に使う HMAC の長さ。450 万行でも重なる見込みは無視できる</summary>
    const int MarkLength = 16;
    /// <summary>印に添える、そのトークンが入れた番組名などの HMAC の長さ。1 つの放送の候補の中で見分けられれば足りる</summary>
    const int LabelLength = 8;

    /// <summary>集計の表に残す長さ。前の年の同じ時期と比べられる幅</summary>
    public static readonly TimeSpan TallyLifetime = TimeSpan.FromDays(400);

    /// <summary>番組名・局名・ジャンル・時刻。トークンごとにどれを入れたかを、印に添えて持つ</summary>
    enum Field
    {
        Name = 0,
        ServiceName = 1,
        Genres = 2,
        Time = 3,
    }

    static readonly Field[] Fields = [Field.Name, Field.ServiceName, Field.Genres, Field.Time];

    const long Day = 24 * 60 * 60;

    readonly SqliteConnection db;
    readonly byte[] key;
    readonly Lock gate = new();

    public RankingStore(RankingSettings settings, ILogger<RankingStore> log)
    {
        key = settings.MarkKey;
        var directory = Path.GetDirectoryName(settings.DataPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = settings.DataPath, Pooling = false }.ToString());
        db.Open();
        // 一時ファイルはメモリに置く (ルートのファイルシステムは読み取り専用で、書けるのはデータの置き場だけ)
        Execute("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY; PRAGMA busy_timeout = 5000;");

        var version = Convert.ToInt32(Scalar("PRAGMA user_version"));
        if (version != SchemaVersion && version != 0)
        {
            log.LogWarning("ranking: schema {Old} -> {New}, data dropped", version, SchemaVersion);
            Execute("DROP TABLE IF EXISTS tokens; DROP TABLE IF EXISTS airings; DROP TABLE IF EXISTS labels; DROP TABLE IF EXISTS marks; DROP TABLE IF EXISTS tallies;");
        }
        Execute($"""
            CREATE TABLE IF NOT EXISTS tokens (
                hash BLOB PRIMARY KEY,
                created_on INTEGER NOT NULL,
                sent_at INTEGER
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS airings (
                network_id INTEGER NOT NULL,
                service_id INTEGER NOT NULL,
                event_id INTEGER NOT NULL,
                end_at INTEGER NOT NULL,
                tokens INTEGER NOT NULL,
                homes INTEGER NOT NULL,
                recorded INTEGER NOT NULL,
                live INTEGER NOT NULL,
                timeshift INTEGER NOT NULL,
                PRIMARY KEY (network_id, service_id, event_id)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS airings_end ON airings (end_at);
            CREATE TABLE IF NOT EXISTS labels (
                network_id INTEGER NOT NULL,
                service_id INTEGER NOT NULL,
                event_id INTEGER NOT NULL,
                field INTEGER NOT NULL,
                value TEXT NOT NULL,
                votes INTEGER NOT NULL,
                PRIMARY KEY (network_id, service_id, event_id, field, value)
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS marks (
                mark BLOB PRIMARY KEY,
                kinds INTEGER NOT NULL,
                expires_on INTEGER NOT NULL,
                labels BLOB NOT NULL
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS marks_expires ON marks (expires_on);
            CREATE TABLE IF NOT EXISTS tallies (
                id INTEGER PRIMARY KEY,
                start_at INTEGER NOT NULL,
                end_at INTEGER NOT NULL,
                name TEXT NOT NULL,
                genres TEXT NOT NULL,
                affiliation TEXT,
                airings TEXT NOT NULL,
                homes INTEGER NOT NULL,
                recorded INTEGER NOT NULL,
                live INTEGER NOT NULL,
                timeshift INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS tallies_start ON tallies (start_at);
            PRAGMA user_version = {SchemaVersion};
            """);
    }

    /// <summary>トークンを作り、SHA-256 だけを持つ。返したトークンそのものはどこにも残さない</summary>
    public string IssueToken(DateTimeOffset now)
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        lock (gate)
        {
            using var command = Command("INSERT INTO tokens (hash, created_on, sent_at) VALUES ($hash, $day, NULL)");
            command.Parameters.AddWithValue("$hash", TokenHash(token));
            command.Parameters.AddWithValue("$day", now.ToUnixTimeSeconds() / Day);
            command.ExecuteNonQuery();
        }
        return token;
    }

    public TokenRecord? FindToken(string token)
    {
        var hash = TokenHash(token);
        lock (gate)
        {
            using var command = Command("SELECT sent_at FROM tokens WHERE hash = $hash");
            command.Parameters.AddWithValue("$hash", hash);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            return new TokenRecord(reader.IsDBNull(0) ? null : reader.GetInt64(0));
        }
    }

    public bool DeleteToken(string token)
    {
        lock (gate)
        {
            using var command = Command("DELETE FROM tokens WHERE hash = $hash");
            command.Parameters.AddWithValue("$hash", TokenHash(token));
            return command.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// 票を数えに入れ、最後に送った時刻を書き換える。1 トークン 1 放送 1 つで、同じ放送をもう一度送れば中身 (種類の組・名前・時刻) を書き換える。
    /// remove は、そのトークンの印が無い放送なら何もしない。
    /// トークンが無ければ (取り消された・消された) 何も書かずに false を返す。認証のあとに取り消しが割り込んでも、取り消したトークンの票を残さない
    /// </summary>
    public bool ApplyVotes(string token, IReadOnlyList<AddVote> adds, IReadOnlyList<AiringId> removes, DateTimeOffset now)
    {
        lock (gate)
        {
            using var transaction = db.BeginTransaction();
            using (var command = Command("UPDATE tokens SET sent_at = $now WHERE hash = $hash", transaction))
            {
                command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
                command.Parameters.AddWithValue("$hash", TokenHash(token));
                if (command.ExecuteNonQuery() == 0) return false;
            }
            var tx = new Votes(this, transaction, token);
            foreach (var vote in adds) tx.Add(vote);
            foreach (var id in removes) tx.Remove(id);
            transaction.Commit();
            return true;
        }
    }

    /// <summary>
    /// 一覧 (人気の番組) の元になる放送。終わりが <paramref name="endAfter"/> より後で、一覧の数が <paramref name="minHomes"/> 以上のものと、
    /// 一覧の数のある地上波のものの全部 (地域をまたいでまとめてから数えるため)
    /// </summary>
    public List<Candidate> ReadListed(int minHomes, long endAfter)
    {
        lock (gate) return ReadCandidates(endAfter, long.MaxValue, minHomes, null);
    }

    /// <summary>
    /// 読み出す。名前・局名・ジャンル・時刻は、それぞれ票の多いもの。<paramref name="minHomes"/> が null なら数によらず全部
    /// </summary>
    List<Candidate> ReadCandidates(long endAfter, long endBefore, int? minHomes, SqliteTransaction? transaction)
    {
        var candidates = new List<Candidate>();
        AiringId? id = null;
        Counts count = default;
        long lastEnd = 0;
        var best = new (string? Value, long Votes)[Fields.Length];
        // 放送の順に読み、放送が替わるたびに 1 つ作る
        using var command = Command($"""
            SELECT a.network_id, a.service_id, a.event_id, a.tokens, a.homes, a.recorded, a.live, a.timeshift, a.end_at, l.field, l.value, l.votes
            FROM airings a JOIN labels l USING (network_id, service_id, event_id)
            WHERE a.end_at > $after AND a.end_at <= $before
            {(minHomes is null ? "" : "AND a.homes > 0 AND (a.homes >= $min OR a.network_id BETWEEN $terrestrialFrom AND $terrestrialTo)")}
            ORDER BY a.network_id, a.service_id, a.event_id
            """, transaction);
        command.Parameters.AddWithValue("$after", endAfter);
        command.Parameters.AddWithValue("$before", endBefore);
        if (minHomes is { } min)
        {
            command.Parameters.AddWithValue("$min", min);
            command.Parameters.AddWithValue("$terrestrialFrom", Terrestrial.FirstNetwork);
            command.Parameters.AddWithValue("$terrestrialTo", Terrestrial.LastNetwork);
        }
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var next = new AiringId(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
                if (next != id)
                {
                    Flush();
                    id = next;
                    count = new Counts(reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7));
                    lastEnd = reader.GetInt64(8);
                    Array.Clear(best);
                }
                // 欄ごとに票の多いもの。同じ数なら文字の順で先のもの (毎回同じものを選ぶため)
                var field = reader.GetInt32(9);
                var value = reader.GetString(10);
                var votes = reader.GetInt64(11);
                ref var current = ref best[field];
                if (current.Value is null || votes > current.Votes || (votes == current.Votes && string.CompareOrdinal(value, current.Value) < 0))
                    current = (value, votes);
            }
        }
        Flush();
        return candidates;

        void Flush()
        {
            if (id is not { } airing) return;
            if (best[(int)Field.Name].Value is not { } name || best[(int)Field.ServiceName].Value is not { } service || best[(int)Field.Time].Value is not { } time) return;
            var (startAt, duration) = ParseTime(time);
            candidates.Add(new Candidate(airing, count, name, service, ParseGenres(best[(int)Field.Genres].Value), startAt, duration, lastEnd));
        }
    }

    /// <summary>
    /// 消す時期を過ぎたものを消す (docs/ranking.md の「消す時期」)。終わって 8 日たった放送は、数を決めて集計の表に移してから消す。
    /// 消した件数を返す (<c>Airings</c> は集計の表に移した放送の数、<c>Tallies</c> は作った集計の行の数)
    /// </summary>
    public (int Marks, int Airings, int Tallies, int Tokens) Cleanup(DateTimeOffset now)
    {
        var seconds = now.ToUnixTimeSeconds();
        lock (gate)
        {
            using var transaction = db.BeginTransaction();
            var (airings, tallies) = Settle(seconds, transaction);
            int marks, tokens;
            using (var command = Command("DELETE FROM marks WHERE expires_on < $today", transaction))
            {
                command.Parameters.AddWithValue("$today", seconds / Day);
                marks = command.ExecuteNonQuery();
            }
            using (var command = Command("DELETE FROM tallies WHERE start_at < $before", transaction))
            {
                command.Parameters.AddWithValue("$before", seconds - (long)TallyLifetime.TotalSeconds);
                command.ExecuteNonQuery();
            }
            using (var command = Command("DELETE FROM tokens WHERE coalesce(sent_at, created_on * $day) < $before", transaction))
            {
                command.Parameters.AddWithValue("$day", Day);
                command.Parameters.AddWithValue("$before", seconds - (long)RankingFeature.TokenLifetime.TotalSeconds);
                tokens = command.ExecuteNonQuery();
            }
            transaction.Commit();
            return (marks, airings, tallies, tokens);
        }
    }

    /// <summary>
    /// **数を決める。** 終わって 8 日たち、もう票を受けない放送を、一覧と同じ決まりで番組にまとめ (<see cref="Grouping"/>)、
    /// 4 つの数をそれぞれ 5 の倍数に切り下げて集計の表に書き、放送を消す。4 つとも 0 の番組は書かない。
    /// 地域をまたいでまとめる相手がまだ票を受けている番組は、相手も 8 日たつまで待つ (まとめる前に一部だけを数えないため)
    /// </summary>
    (int Airings, int Tallies) Settle(long now, SqliteTransaction transaction)
    {
        var cutoff = now - (long)VoteRequest.SettleAfter.TotalSeconds;
        // まとめる相手は始まりが 10 分以内で、長さは 24 時間まで。終わりの差はその和に収まるので、2 日先までを読めば相手が揃う
        var candidates = ReadCandidates(long.MinValue, cutoff + 2 * Day, null, transaction);
        var settled = 0;
        var written = 0;
        foreach (var group in Grouping.Group(candidates))
        {
            if (!group.TrueForAll(c => c.LastEnd <= cutoff)) continue;
            var sum = group.Aggregate(default(Counts), (s, c) => s + c.Counts);
            var tally = new Counts(sum.Tokens, Round(sum.Homes), Round(sum.Recorded), Round(sum.Live), Round(sum.Timeshift));
            if (tally.Homes > 0 || tally.Recorded > 0 || tally.Live > 0 || tally.Timeshift > 0)
            {
                WriteTally(group, tally, transaction);
                written++;
            }
            foreach (var airing in group)
            {
                using var command = Command("""
                    DELETE FROM labels WHERE network_id = $n AND service_id = $s AND event_id = $e;
                    DELETE FROM airings WHERE network_id = $n AND service_id = $s AND event_id = $e;
                    """, transaction);
                command.Parameters.AddWithValue("$n", airing.Id.NetworkId);
                command.Parameters.AddWithValue("$s", airing.Id.ServiceId);
                command.Parameters.AddWithValue("$e", airing.Id.EventId);
                command.ExecuteNonQuery();
                settled++;
            }
        }
        // 名前などが揃わず読めなかった放送 (labels の無いもの) は、数えようが無いので消す
        using (var command = Command("""
            DELETE FROM labels WHERE (network_id, service_id, event_id) IN (SELECT network_id, service_id, event_id FROM airings WHERE end_at <= $before);
            DELETE FROM airings WHERE end_at <= $before;
            """, transaction))
        {
            command.Parameters.AddWithValue("$before", cutoff - 2 * Day);
            settled += command.ExecuteNonQuery();
        }
        return (settled, written);
    }

    void WriteTally(List<Candidate> group, Counts tally, SqliteTransaction transaction)
    {
        var airings = group.OrderBy(c => c.StartAt).ThenBy(c => c.Id.ServiceId).ThenBy(c => c.Id.EventId).ToList();
        var lead = airings.MaxBy(c => c.Counts.Tokens)!;
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartArray();
            foreach (var airing in airings)
            {
                json.WriteStartObject();
                json.WriteNumber("network_id", airing.Id.NetworkId);
                json.WriteNumber("service_id", airing.Id.ServiceId);
                json.WriteNumber("event_id", airing.Id.EventId);
                json.WriteString("start_at", RankingBoard.Iso(airing.StartAt));
                json.WriteString("end_at", RankingBoard.Iso(airing.EndAt));
                json.WriteString("service_name", airing.ServiceName);
                json.WriteString("region", Terrestrial.RegionName(airing.Id));
                json.WriteEndObject();
            }
            json.WriteEndArray();
        }
        using var command = Command("""
            INSERT INTO tallies (start_at, end_at, name, genres, affiliation, airings, homes, recorded, live, timeshift)
            VALUES ($start, $end, $name, $genres, $affiliation, $airings, $homes, $recorded, $live, $timeshift)
            """, transaction);
        command.Parameters.AddWithValue("$start", airings[0].StartAt);
        command.Parameters.AddWithValue("$end", airings.Max(c => c.EndAt));
        command.Parameters.AddWithValue("$name", lead.Name);
        command.Parameters.AddWithValue("$genres", string.Join(',', lead.Genres));
        command.Parameters.AddWithValue("$affiliation", Terrestrial.AffiliationOf(lead.Id) is { } a ? Terrestrial.AffiliationNames[a] : DBNull.Value);
        command.Parameters.AddWithValue("$airings", Encoding.UTF8.GetString(buffer.ToArray()));
        command.Parameters.AddWithValue("$homes", tally.Homes);
        command.Parameters.AddWithValue("$recorded", tally.Recorded);
        command.Parameters.AddWithValue("$live", tally.Live);
        command.Parameters.AddWithValue("$timeshift", tally.Timeshift);
        command.ExecuteNonQuery();
    }

    /// <summary>5 の倍数に切り下げる。5 未満は 0 で、1・2 の家の数を見せない</summary>
    public static int Round(int count) => count / RankingBoard.Step * RankingBoard.Step;

    /// <summary>表ごとの行の数。テストが、消す処理と取り消しのあとの行の数を確かめるのに使う</summary>
    internal long Count(string table)
    {
        lock (gate) return Convert.ToInt64(Scalar($"SELECT count(*) FROM {table}"));
    }

    public void Dispose() => db.Dispose();

    static byte[] TokenHash(string token) => SHA256.HashData(Encoding.ASCII.GetBytes(token));

    /// <summary>
    /// 二重に数えない印。HMAC-SHA256(鍵, トークンそのもの ‖ network_id ‖ service_id ‖ event_id) の先頭。
    /// hub はトークンの SHA-256 しか持たないので、DB と鍵が両方漏れても、印からどの家がどの放送に票を入れたかは辿れない
    /// </summary>
    byte[] Mark(string token, AiringId id) => Hmac(token, id, null, null)[..MarkLength];

    /// <summary>そのトークンがその放送の欄に入れた値の印。値の候補と突き合わせて、前に入れた値を見つけるのに使う</summary>
    byte[] Label(string token, AiringId id, Field field, string value) => Hmac(token, id, field, value)[..LabelLength];

    byte[] Hmac(string token, AiringId id, Field? field, string? value)
    {
        var tokenBytes = Encoding.ASCII.GetBytes(token);
        var valueBytes = value is null ? [] : Encoding.UTF8.GetBytes(value);
        var input = new byte[tokenBytes.Length + 6 + (field is null ? 0 : 1 + valueBytes.Length)];
        tokenBytes.CopyTo(input, 0);
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(tokenBytes.Length), (ushort)id.NetworkId);
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(tokenBytes.Length + 2), (ushort)id.ServiceId);
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(tokenBytes.Length + 4), (ushort)id.EventId);
        if (field is not null)
        {
            input[tokenBytes.Length + 6] = (byte)field.Value;
            valueBytes.CopyTo(input, tokenBytes.Length + 7);
        }
        return HMACSHA256.HashData(key, input);
    }

    static string[] Values(AddVote vote) =>
    [
        vote.Name,
        vote.ServiceName,
        string.Join(',', vote.Genres),
        $"{vote.StartAt}/{vote.Duration}",
    ];

    internal static IReadOnlyList<int> ParseGenres(string? value) =>
        string.IsNullOrEmpty(value) ? [] : [.. value.Split(',').Select(int.Parse)];

    static (long StartAt, int Duration) ParseTime(string value)
    {
        var slash = value.IndexOf('/');
        return (long.Parse(value[..slash]), int.Parse(value[(slash + 1)..]));
    }

    SqliteCommand Command(string sql, SqliteTransaction? transaction = null)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    void Execute(string sql)
    {
        using var command = Command(sql);
        command.ExecuteNonQuery();
    }

    object? Scalar(string sql)
    {
        using var command = Command(sql);
        return command.ExecuteScalar();
    }

    /// <summary>1 回の送信の中の書き込み。同じトランザクションの中で 1 件ずつ当てる</summary>
    sealed class Votes(RankingStore store, SqliteTransaction transaction, string token)
    {
        public void Add(AddVote vote)
        {
            var id = vote.Id;
            var mark = store.Mark(token, id);
            var values = Values(vote);
            var labels = new byte[Fields.Length * LabelLength];
            foreach (var field in Fields) store.Label(token, id, field, values[(int)field]).CopyTo(labels, (int)field * LabelLength);

            var previous = ReadMark(mark);
            // 放送が消えたあと (集計の表に移したあと) に印だけが残っているときは、新しい票として数える
            if (previous is not null && !AiringExists(id))
            {
                DeleteMark(mark);
                previous = null;
            }

            long endAt;
            if (previous is null)
            {
                endAt = UpsertAiring(id, vote.EndAt, Counts.Of(vote.Kinds));
                foreach (var field in Fields) AddLabel(id, field, values[(int)field], 1);
            }
            else
            {
                var (kinds, old) = previous.Value;
                // トークンの数は変わらない。種類の組の差だけを足す
                var change = Counts.Of(vote.Kinds) - Counts.Of(kinds);
                endAt = UpsertAiring(id, vote.EndAt, change);
                foreach (var field in Fields)
                {
                    var slot = old.AsSpan((int)field * LabelLength, LabelLength);
                    if (slot.SequenceEqual(labels.AsSpan((int)field * LabelLength, LabelLength))) continue;
                    RemoveLabel(id, field, slot);
                    AddLabel(id, field, values[(int)field], 1);
                }
            }

            // 印は、放送 (候補の中でいちばん遅い終わり) が票を受けなくなった日の翌日の日付まで持つ。放送より先に印が消えると、同じトークンの票を二重に数えうるため
            using var command = store.Command("""
                INSERT INTO marks (mark, kinds, expires_on, labels) VALUES ($mark, $kinds, $expires, $labels)
                ON CONFLICT (mark) DO UPDATE SET kinds = excluded.kinds, expires_on = excluded.expires_on, labels = excluded.labels
                """, transaction);
            command.Parameters.AddWithValue("$mark", mark);
            command.Parameters.AddWithValue("$kinds", (int)vote.Kinds);
            command.Parameters.AddWithValue("$expires", (endAt + (long)VoteRequest.SettleAfter.TotalSeconds + Day) / Day);
            command.Parameters.AddWithValue("$labels", labels);
            command.ExecuteNonQuery();
        }

        public void Remove(AiringId id)
        {
            var mark = store.Mark(token, id);
            if (ReadMark(mark) is not { } previous) return;
            DeleteMark(mark);
            if (!AiringExists(id)) return;
            foreach (var field in Fields) RemoveLabel(id, field, previous.Labels.AsSpan((int)field * LabelLength, LabelLength));
            UpsertAiring(id, 0, -Counts.Of(previous.Kinds));
            // 印が無くなった放送は、名前ごと消す
            using var command = store.Command("""
                DELETE FROM labels WHERE network_id = $n AND service_id = $s AND event_id = $e
                    AND EXISTS (SELECT 1 FROM airings WHERE network_id = $n AND service_id = $s AND event_id = $e AND tokens <= 0);
                DELETE FROM airings WHERE network_id = $n AND service_id = $s AND event_id = $e AND tokens <= 0;
                """, transaction);
            Bind(command, id);
            command.ExecuteNonQuery();
        }

        (Kinds Kinds, byte[] Labels)? ReadMark(byte[] mark)
        {
            using var command = store.Command("SELECT kinds, labels FROM marks WHERE mark = $mark", transaction);
            command.Parameters.AddWithValue("$mark", mark);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            return ((Kinds)reader.GetInt32(0), (byte[])reader.GetValue(1));
        }

        void DeleteMark(byte[] mark)
        {
            using var command = store.Command("DELETE FROM marks WHERE mark = $mark", transaction);
            command.Parameters.AddWithValue("$mark", mark);
            command.ExecuteNonQuery();
        }

        bool AiringExists(AiringId id)
        {
            using var command = store.Command("SELECT 1 FROM airings WHERE network_id = $n AND service_id = $s AND event_id = $e", transaction);
            Bind(command, id);
            return command.ExecuteScalar() is not null;
        }

        /// <summary>数を足し、終わりの時刻を遅いほうに寄せる。書いたあとの終わりの時刻を返す</summary>
        long UpsertAiring(AiringId id, long endAt, Counts change)
        {
            using var command = store.Command("""
                INSERT INTO airings (network_id, service_id, event_id, end_at, tokens, homes, recorded, live, timeshift)
                VALUES ($n, $s, $e, $end, $tokens, $homes, $recorded, $live, $timeshift)
                ON CONFLICT (network_id, service_id, event_id) DO UPDATE SET
                    end_at = max(end_at, excluded.end_at), tokens = tokens + excluded.tokens, homes = homes + excluded.homes,
                    recorded = recorded + excluded.recorded, live = live + excluded.live, timeshift = timeshift + excluded.timeshift
                RETURNING end_at
                """, transaction);
            Bind(command, id);
            command.Parameters.AddWithValue("$end", endAt);
            command.Parameters.AddWithValue("$tokens", change.Tokens);
            command.Parameters.AddWithValue("$homes", change.Homes);
            command.Parameters.AddWithValue("$recorded", change.Recorded);
            command.Parameters.AddWithValue("$live", change.Live);
            command.Parameters.AddWithValue("$timeshift", change.Timeshift);
            return Convert.ToInt64(command.ExecuteScalar());
        }

        void AddLabel(AiringId id, Field field, string value, int votes)
        {
            using var command = store.Command("""
                INSERT INTO labels (network_id, service_id, event_id, field, value, votes) VALUES ($n, $s, $e, $field, $value, $votes)
                ON CONFLICT (network_id, service_id, event_id, field, value) DO UPDATE SET votes = votes + excluded.votes
                """, transaction);
            Bind(command, id);
            command.Parameters.AddWithValue("$field", (int)field);
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$votes", votes);
            command.ExecuteNonQuery();
        }

        /// <summary>前に入れた値を、その放送の候補と印を突き合わせて見つけ、1 つ減らす</summary>
        void RemoveLabel(AiringId id, Field field, ReadOnlySpan<byte> label)
        {
            var candidates = new List<string>();
            using (var command = store.Command("SELECT value FROM labels WHERE network_id = $n AND service_id = $s AND event_id = $e AND field = $field", transaction))
            {
                Bind(command, id);
                command.Parameters.AddWithValue("$field", (int)field);
                using var reader = command.ExecuteReader();
                while (reader.Read()) candidates.Add(reader.GetString(0));
            }
            string? match = null;
            foreach (var candidate in candidates)
            {
                if (!store.Label(token, id, field, candidate).AsSpan().SequenceEqual(label)) continue;
                match = candidate;
                break;
            }
            if (match is null) return;
            using var update = store.Command("""
                UPDATE labels SET votes = votes - 1 WHERE network_id = $n AND service_id = $s AND event_id = $e AND field = $field AND value = $value;
                DELETE FROM labels WHERE network_id = $n AND service_id = $s AND event_id = $e AND field = $field AND value = $value AND votes <= 0;
                """, transaction);
            Bind(update, id);
            update.Parameters.AddWithValue("$field", (int)field);
            update.Parameters.AddWithValue("$value", match);
            update.ExecuteNonQuery();
        }

        static void Bind(SqliteCommand command, AiringId id)
        {
            command.Parameters.AddWithValue("$n", id.NetworkId);
            command.Parameters.AddWithValue("$s", id.ServiceId);
            command.Parameters.AddWithValue("$e", id.EventId);
        }
    }
}
