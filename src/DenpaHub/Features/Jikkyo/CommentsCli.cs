using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DenpaHub.Features.Jikkyo;

/// <summary>
/// **削除依頼を調べるときに、独自コメントを読み出す** (<c>denpa-hub comments &lt;局&gt; &lt;から&gt; &lt;まで&gt;</c>)。
/// 動いているコンテナの中で実行ファイルを引数付きで動かし、タブ区切りで標準出力に書く。サーバは立てない (<c>denpa-hub tallies</c> と同じ)。
/// 消すときは、出した <c>id</c> を方針の <c>removedComments</c> に足す (docs/jikkyo.md の「運営の方針」)
/// </summary>
public static class CommentsCli
{
    public const string Command = "comments";

    public static int Run(string[] args, JikkyoSettings? settings, TextWriter output, TextWriter error)
    {
        if (settings is null)
        {
            error.WriteLine("設定の誤り: JIKKYO_DATA がありません");
            return 2;
        }
        if (args.Length != 4
            || !DateTimeOffset.TryParse(args[2], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var from)
            || !DateTimeOffset.TryParse(args[3], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var to)
            || from >= to)
        {
            error.WriteLine("使い方: denpa-hub comments <局 (jk1 など)> <から> <まで> (時刻は 2026-11-28T21:00+09:00 のような ISO 8601)");
            return 2;
        }
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = settings.DataPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT id, channel, at, text, mail, home, author, relayed, removed FROM comments
            WHERE channel = $channel AND at >= $from AND at < $to ORDER BY at, id
            """;
        command.Parameters.AddWithValue("$channel", args[1]);
        command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
        output.WriteLine("id\tat\thome\tauthor\trelayed\tremoved\ttext");
        foreach (var c in JikkyoStore.Read(command))
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(c.At).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
            output.WriteLine($"{c.Id}\t{at}\t{c.Home}\t{c.Author}\t{(c.Relayed ? 1 : 0)}\t{(c.Removed ? 1 : 0)}\t{c.Text.Replace('\t', ' ')}");
        }
        return 0;
    }
}
