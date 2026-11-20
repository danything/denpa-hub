using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace DenpaHub.Features.Ranking;

/// <summary>トークンの記録。<paramref name="SentAt"/> は最後に送った時刻 (Unix 秒)。まだ送っていなければ null</summary>
public sealed record TokenRecord(long? SentAt);

/// <summary>順位の元になる放送 1 つ。名前などは票を入れたトークンの多いもの</summary>
public sealed record Candidate(AiringId Id, int Manual, int Rule, string Name, string ServiceName, IReadOnlyList<int> Genres, long StartAt);

/// <summary>
/// 予約数ランキングのデータ (SQLite のファイル 1 つ)。表の形と、何を持たないかは docs/ranking.md の「データの持ち方」。
///
/// 接続は 1 つで、ロックで順に使う。1 台のプロセスだけがファイルを開く前提 (Deployment は replicas: 1・Recreate)
/// </summary>
public sealed class RankingStore : IDisposable
{
    /// <summary>表の形のバージョン。変えたら、中身を捨てて作り直す (どれも放送が終われば消すもので、denpa が送り直せば戻るため)</summary>
    const int SchemaVersion = 1;

    /// <summary>印に使う HMAC の長さ。300 万行でも重なる見込みは無視できる</summary>
    const int MarkLength = 16;
    /// <summary>印に添える、そのトークンが入れた番組名などの HMAC の長さ。1 つの放送の候補の中で見分けられれば足りる</summary>
    const int LabelLength = 8;

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
            Execute("DROP TABLE IF EXISTS tokens; DROP TABLE IF EXISTS airings; DROP TABLE IF EXISTS labels; DROP TABLE IF EXISTS marks;");
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
                manual INTEGER NOT NULL,
                rule INTEGER NOT NULL,
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
                kind INTEGER NOT NULL,
                expires_on INTEGER NOT NULL,
                labels BLOB NOT NULL
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS marks_expires ON marks (expires_on);
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
    /// 票を数えに入れ、最後に送った時刻を書き換える。1 トークン 1 放送 1 票で、同じ放送をもう一度送れば中身 (手かルールか・名前・時刻) を書き換える。
    /// remove は、そのトークンの票が無い放送なら何もしない。
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
    /// まだ始まっていない放送のうち、票を入れたトークンが <paramref name="minTokens"/> 以上のものと、地上波のものの全部 (地域をまたいでまとめてから数えるため)。
    /// 名前・局名・ジャンル・時刻は、それぞれ票の多いもの
    /// </summary>
    public List<Candidate> ReadCandidates(int minTokens, DateTimeOffset now)
    {
        var seconds = now.ToUnixTimeSeconds();
        var candidates = new List<Candidate>();
        AiringId? id = null;
        (int Manual, int Rule) count = default;
        var best = new (string? Value, long Votes)[Fields.Length];
        lock (gate)
        {
            // 放送の順に読み、放送が替わるたびに 1 つ作る。終わった放送は始まりを読むまでもなく外す
            using var command = Command("""
                SELECT a.network_id, a.service_id, a.event_id, a.manual, a.rule, l.field, l.value, l.votes
                FROM airings a JOIN labels l USING (network_id, service_id, event_id)
                WHERE a.end_at > $now AND (a.manual + a.rule >= $min OR a.network_id BETWEEN $terrestrialFrom AND $terrestrialTo)
                ORDER BY a.network_id, a.service_id, a.event_id
                """);
            command.Parameters.AddWithValue("$now", seconds);
            command.Parameters.AddWithValue("$min", minTokens);
            command.Parameters.AddWithValue("$terrestrialFrom", Terrestrial.FirstNetwork);
            command.Parameters.AddWithValue("$terrestrialTo", Terrestrial.LastNetwork);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var next = new AiringId(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
                if (next != id)
                {
                    Flush();
                    id = next;
                    count = (reader.GetInt32(3), reader.GetInt32(4));
                    Array.Clear(best);
                }
                // 欄ごとに票の多いもの。同じ数なら文字の順で先のもの (毎回同じものを選ぶため)
                var field = reader.GetInt32(5);
                var value = reader.GetString(6);
                var votes = reader.GetInt64(7);
                ref var current = ref best[field];
                if (current.Value is null || votes > current.Votes || (votes == current.Votes && string.CompareOrdinal(value, current.Value) < 0))
                    current = (value, votes);
            }
            Flush();
        }
        return candidates;

        void Flush()
        {
            if (id is not { } airing) return;
            if (best[(int)Field.Name].Value is not { } name || best[(int)Field.ServiceName].Value is not { } service || best[(int)Field.Time].Value is not { } time) return;
            var startAt = ParseTime(time).StartAt;
            if (startAt <= seconds) return;
            candidates.Add(new Candidate(airing, count.Manual, count.Rule, name, service, ParseGenres(best[(int)Field.Genres].Value), startAt));
        }
    }

    /// <summary>消す時期を過ぎたものを消す (docs/ranking.md の「消す時期」)。消した件数を返す</summary>
    public (int Marks, int Airings, int Tokens) Cleanup(DateTimeOffset now)
    {
        var seconds = now.ToUnixTimeSeconds();
        lock (gate)
        {
            using var transaction = db.BeginTransaction();
            int marks, airings, tokens;
            using (var command = Command("DELETE FROM marks WHERE expires_on < $today", transaction))
            {
                command.Parameters.AddWithValue("$today", seconds / Day);
                marks = command.ExecuteNonQuery();
            }
            using (var command = Command("""
                DELETE FROM labels WHERE (network_id, service_id, event_id) IN
                    (SELECT network_id, service_id, event_id FROM airings WHERE end_at < $before)
                """, transaction))
            {
                command.Parameters.AddWithValue("$before", seconds - Day);
                command.ExecuteNonQuery();
            }
            using (var command = Command("DELETE FROM airings WHERE end_at < $before", transaction))
            {
                command.Parameters.AddWithValue("$before", seconds - Day);
                airings = command.ExecuteNonQuery();
            }
            using (var command = Command("DELETE FROM tokens WHERE coalesce(sent_at, created_on * $day) < $before", transaction))
            {
                command.Parameters.AddWithValue("$day", Day);
                command.Parameters.AddWithValue("$before", seconds - (long)RankingFeature.TokenLifetime.TotalSeconds);
                tokens = command.ExecuteNonQuery();
            }
            transaction.Commit();
            return (marks, airings, tokens);
        }
    }

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

    static IReadOnlyList<int> ParseGenres(string? value) =>
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
            // 放送が消えたあとに印だけが残っているときは、新しい票として数える
            if (previous is not null && !AiringExists(id))
            {
                DeleteMark(mark);
                previous = null;
            }

            long endAt;
            if (previous is null)
            {
                endAt = UpsertAiring(id, vote.EndAt, vote.Manual ? 1 : 0, vote.Manual ? 0 : 1);
                foreach (var field in Fields) AddLabel(id, field, values[(int)field], 1);
            }
            else
            {
                var (kind, old) = previous.Value;
                var move = kind == vote.Manual ? 0 : vote.Manual ? 1 : -1;
                endAt = UpsertAiring(id, vote.EndAt, move, -move);
                foreach (var field in Fields)
                {
                    var slot = old.AsSpan((int)field * LabelLength, LabelLength);
                    if (slot.SequenceEqual(labels.AsSpan((int)field * LabelLength, LabelLength))) continue;
                    RemoveLabel(id, field, slot);
                    AddLabel(id, field, values[(int)field], 1);
                }
            }

            // 印は放送 (候補の中でいちばん遅い終わり) の翌日の日付まで持つ。放送より先に印が消えると、同じトークンの票を二重に数えうるため
            using var command = store.Command("""
                INSERT INTO marks (mark, kind, expires_on, labels) VALUES ($mark, $kind, $expires, $labels)
                ON CONFLICT (mark) DO UPDATE SET kind = excluded.kind, expires_on = excluded.expires_on, labels = excluded.labels
                """, transaction);
            command.Parameters.AddWithValue("$mark", mark);
            command.Parameters.AddWithValue("$kind", vote.Manual ? 1 : 0);
            command.Parameters.AddWithValue("$expires", (endAt + Day) / Day);
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
            using (var command = store.Command("UPDATE airings SET manual = manual - $manual, rule = rule - $rule WHERE network_id = $n AND service_id = $s AND event_id = $e", transaction))
            {
                Bind(command, id);
                command.Parameters.AddWithValue("$manual", previous.Manual ? 1 : 0);
                command.Parameters.AddWithValue("$rule", previous.Manual ? 0 : 1);
                command.ExecuteNonQuery();
            }
            // 票が無くなった放送は、名前ごと消す
            using (var command = store.Command("""
                DELETE FROM labels WHERE network_id = $n AND service_id = $s AND event_id = $e
                    AND EXISTS (SELECT 1 FROM airings WHERE network_id = $n AND service_id = $s AND event_id = $e AND manual + rule <= 0);
                DELETE FROM airings WHERE network_id = $n AND service_id = $s AND event_id = $e AND manual + rule <= 0;
                """, transaction))
            {
                Bind(command, id);
                command.ExecuteNonQuery();
            }
        }

        (bool Manual, byte[] Labels)? ReadMark(byte[] mark)
        {
            using var command = store.Command("SELECT kind, labels FROM marks WHERE mark = $mark", transaction);
            command.Parameters.AddWithValue("$mark", mark);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            return (reader.GetInt32(0) == 1, (byte[])reader.GetValue(1));
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
        long UpsertAiring(AiringId id, long endAt, int manual, int rule)
        {
            using var command = store.Command("""
                INSERT INTO airings (network_id, service_id, event_id, end_at, manual, rule) VALUES ($n, $s, $e, $end, $manual, $rule)
                ON CONFLICT (network_id, service_id, event_id) DO UPDATE SET
                    end_at = max(end_at, excluded.end_at), manual = manual + excluded.manual, rule = rule + excluded.rule
                RETURNING end_at
                """, transaction);
            Bind(command, id);
            command.Parameters.AddWithValue("$end", endAt);
            command.Parameters.AddWithValue("$manual", manual);
            command.Parameters.AddWithValue("$rule", rule);
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
