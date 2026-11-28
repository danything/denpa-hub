using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace DenpaHub.Features.Jikkyo;

/// <summary>保存した独自コメント 1 件 (読み出しと <c>denpa-hub comments</c> 用)</summary>
public sealed record StoredComment(string Id, string Channel, long At, string Text, string? Mail, string Home, string Author, bool Relayed, bool Removed = false)
{
    public Comment ToComment() => new(At, Text, Mail, Source.Hub, Id);
}

/// <summary>
/// 実況のデータ (SQLite のファイル 1 つ)。トークンと独自コメント。表の形と、何を持たないかは docs/jikkyo.md の「データの持ち方」。
/// 接続は 1 つで、ロックで順に使う。1 台のプロセスだけがファイルを開く前提 (ランキングと同じ)
/// </summary>
public sealed class JikkyoStore : IDisposable
{
    public const int SchemaVersion = 1;

    /// <summary>使わなければトークンを消す長さ</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(30);
    /// <summary>使った時刻を書き直す間隔。リクエストのたびには書かない</summary>
    static readonly TimeSpan TouchEvery = TimeSpan.FromHours(1);

    readonly SqliteConnection db;
    readonly Lock gate = new();

    public JikkyoStore(JikkyoSettings settings)
    {
        var directory = Path.GetDirectoryName(settings.DataPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = settings.DataPath, Pooling = false }.ToString());
        db.Open();
        Execute("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY; PRAGMA busy_timeout = 5000;");
        Execute($"""
            CREATE TABLE IF NOT EXISTS tokens (
                hash BLOB PRIMARY KEY,
                created_at INTEGER NOT NULL,
                used_at INTEGER NOT NULL
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS comments (
                id TEXT PRIMARY KEY,
                channel TEXT NOT NULL,
                at INTEGER NOT NULL,
                text TEXT NOT NULL,
                mail TEXT,
                home TEXT NOT NULL,
                author TEXT NOT NULL,
                relayed INTEGER NOT NULL,
                removed INTEGER NOT NULL DEFAULT 0
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS comments_channel_at ON comments (channel, at);
            CREATE INDEX IF NOT EXISTS comments_at ON comments (at);
            PRAGMA user_version = {SchemaVersion};
            """);
    }

    /// <summary>トークンの SHA-256</summary>
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.ASCII.GetBytes(token));

    /// <summary>家の ID。トークンの SHA-256 の先頭 8 バイトの 16 進 (方針で止めるときと、削除依頼を調べるときに使う)</summary>
    public static string HomeOf(byte[] hash) => Convert.ToHexStringLower(hash.AsSpan(0, 8));

    public string IssueToken(DateTimeOffset now)
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        lock (gate)
        {
            using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO tokens (hash, created_at, used_at) VALUES ($hash, $now, $now)";
            command.Parameters.AddWithValue("$hash", Hash(token));
            command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        }
        return token;
    }

    /// <summary>トークンを引く。あれば家の ID を返し、使った時刻を (1 時間に 1 回だけ) 書き直す</summary>
    public string? FindToken(string token, DateTimeOffset now)
    {
        var hash = Hash(token);
        lock (gate)
        {
            using var command = db.CreateCommand();
            command.CommandText = "SELECT used_at FROM tokens WHERE hash = $hash";
            command.Parameters.AddWithValue("$hash", hash);
            if (command.ExecuteScalar() is not long used) return null;
            if (now.ToUnixTimeSeconds() - used >= (long)TouchEvery.TotalSeconds)
            {
                using var touch = db.CreateCommand();
                touch.CommandText = "UPDATE tokens SET used_at = $now WHERE hash = $hash";
                touch.Parameters.AddWithValue("$hash", hash);
                touch.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
                touch.ExecuteNonQuery();
            }
        }
        return HomeOf(hash);
    }

    public void AddComment(StoredComment comment)
    {
        lock (gate)
        {
            using var command = db.CreateCommand();
            command.CommandText = """
                INSERT INTO comments (id, channel, at, text, mail, home, author, relayed)
                VALUES ($id, $channel, $at, $text, $mail, $home, $author, 0)
                """;
            command.Parameters.AddWithValue("$id", comment.Id);
            command.Parameters.AddWithValue("$channel", comment.Channel);
            command.Parameters.AddWithValue("$at", comment.At);
            command.Parameters.AddWithValue("$text", comment.Text);
            command.Parameters.AddWithValue("$mail", (object?)comment.Mail ?? DBNull.Value);
            command.Parameters.AddWithValue("$home", comment.Home);
            command.Parameters.AddWithValue("$author", comment.Author);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>NX-Jikkyo に書けた</summary>
    public void MarkRelayed(string id)
    {
        lock (gate)
        {
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE comments SET relayed = 1 WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>局の <c>[from, to)</c> の独自コメント (時刻の順)</summary>
    public List<StoredComment> Comments(string channel, long from, long to)
    {
        lock (gate)
        {
            using var command = db.CreateCommand();
            command.CommandText = """
                SELECT id, channel, at, text, mail, home, author, relayed, removed FROM comments
                WHERE channel = $channel AND at >= $from AND at < $to ORDER BY at, id
                """;
            command.Parameters.AddWithValue("$channel", channel);
            command.Parameters.AddWithValue("$from", from);
            command.Parameters.AddWithValue("$to", to);
            return Read(command);
        }
    }

    /// <summary>家が <paramref name="since"/> より後に書いた利用者の ID の数と、<paramref name="author"/> がその中にあるか</summary>
    public (int Count, bool Known) Authors(string home, string author, long since)
    {
        lock (gate)
        {
            using var command = db.CreateCommand();
            command.CommandText = """
                SELECT COUNT(DISTINCT author), COALESCE(MAX(author = $author), 0) FROM comments WHERE home = $home AND at >= $since
                """;
            command.Parameters.AddWithValue("$home", home);
            command.Parameters.AddWithValue("$author", author);
            command.Parameters.AddWithValue("$since", since);
            using var reader = command.ExecuteReader();
            reader.Read();
            return (reader.GetInt32(0), reader.GetInt64(1) != 0);
        }
    }

    /// <summary><paramref name="since"/> より後に書いた利用者 (起動したとき、NX-Jikkyo から戻ってきたものを見分けるため)</summary>
    public List<string> AuthorsSince(long since)
    {
        lock (gate)
        {
            using var command = db.CreateCommand();
            command.CommandText = "SELECT DISTINCT author FROM comments WHERE at >= $since";
            command.Parameters.AddWithValue("$since", since);
            using var reader = command.ExecuteReader();
            var list = new List<string>();
            while (reader.Read()) list.Add(reader.GetString(0));
            return list;
        }
    }

    /// <summary>
    /// 方針で消すことにした独自コメントを消す。消した数。行は残して本文を空にし、印を付ける (時刻と利用者の ID は、NX-Jikkyo に中継した写しを見分けるのに使う)
    /// </summary>
    public int Remove(IEnumerable<string> ids)
    {
        var removed = 0;
        lock (gate)
        {
            using var transaction = db.BeginTransaction();
            foreach (var id in ids)
            {
                using var command = db.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE comments SET text = '', mail = NULL, removed = 1 WHERE id = $id AND removed = 0";
                command.Parameters.AddWithValue("$id", id);
                removed += command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        return removed;
    }

    /// <summary>保存期間を過ぎた独自コメントと、使われなくなったトークンを消す</summary>
    public (int Comments, int Tokens) Cleanup(DateTimeOffset now, int retentionDays)
    {
        lock (gate)
        {
            using var comments = db.CreateCommand();
            comments.CommandText = "DELETE FROM comments WHERE at < $before";
            comments.Parameters.AddWithValue("$before", (now - TimeSpan.FromDays(retentionDays)).ToUnixTimeMilliseconds());
            var c = comments.ExecuteNonQuery();
            using var tokens = db.CreateCommand();
            tokens.CommandText = "DELETE FROM tokens WHERE used_at < $before";
            tokens.Parameters.AddWithValue("$before", (now - TokenLifetime).ToUnixTimeSeconds());
            var t = tokens.ExecuteNonQuery();
            return (c, t);
        }
    }

    internal static List<StoredComment> Read(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var list = new List<StoredComment>();
        while (reader.Read())
            list.Add(new StoredComment(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt64(7) != 0, reader.GetInt64(8) != 0));
        return list;
    }

    void Execute(string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>ほかのスレッドのトランザクション (毎時の掃除など) が終わってから閉じる</summary>
    public void Dispose()
    {
        lock (gate) db.Dispose();
    }
}
