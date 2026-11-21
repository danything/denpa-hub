using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DenpaHub.Features.Ranking;

/// <summary>
/// **外部に示す集計を読み出す** (<c>denpa-hub tallies</c>)。動いているコンテナの中で実行ファイルを引数付きで動かし、標準出力に JSON を書く
/// (<c>kubectl exec -n denpa-hub deploy/denpa-hub -- /app/denpa-hub tallies --from 2026-11-01 --to 2026-11-30</c>)。
/// HTTP のエンドポイントは作らない。クラスタに入れる人 (Doa) しか読めず、外に開く口も秘密も増えないため。
/// 中身と示し方は docs/ranking.md の「外部に示す集計」
/// </summary>
public static class Tallies
{
    public const string Command = "tallies";

    static readonly TimeSpan Japan = TimeSpan.FromHours(9);

    /// <summary>引数を読んで書き出す。終了コードを返す (引数か設定の誤りは 2)</summary>
    public static int Run(string[] args, RankingSettings? settings, TextWriter output, TextWriter error)
    {
        if (settings is null)
        {
            error.WriteLine("設定の誤り: RANKING_DATA がありません");
            return 2;
        }
        DateOnly? from = null, to = null;
        for (var i = 1; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            if (args[i] is not ("--from" or "--to") || value is null || !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                error.WriteLine("使い方: denpa-hub tallies [--from YYYY-MM-DD] [--to YYYY-MM-DD] (日付は日本時間の放送の始まり。両端を含む)");
                return 2;
            }
            if (args[i] == "--from") from = date;
            else to = date;
            i++;
        }
        if (from > to)
        {
            error.WriteLine("--from は --to と同じか前の日付にしてください");
            return 2;
        }
        using var stream = new MemoryStream();
        Write(settings.DataPath, from, to, stream);
        output.Write(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        output.WriteLine();
        return 0;
    }

    /// <summary>集計の表を読み取り専用で開き、始まりが <paramref name="from"/>〜<paramref name="to"/> (日本時間の日付。両端を含む) の番組を始まりの順に書く</summary>
    public static void Write(string dataPath, DateOnly? from, DateOnly? to, Stream output)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dataPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT start_at, end_at, name, genres, affiliation, airings, homes, recorded, live, timeshift
            FROM tallies WHERE start_at >= $from AND start_at < $to ORDER BY start_at, id
            """;
        command.Parameters.AddWithValue("$from", from is { } f ? Start(f) : long.MinValue);
        command.Parameters.AddWithValue("$to", to is { } t ? Start(t.AddDays(1)) : long.MaxValue);
        using var reader = command.ExecuteReader();
        using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = true });
        json.WriteStartObject();
        json.WriteNumber("v", 1);
        json.WriteStartArray("tallies");
        while (reader.Read())
        {
            json.WriteStartObject();
            json.WriteString("name", reader.GetString(2));
            json.WriteStartArray("genres");
            foreach (var genre in RankingStore.ParseGenres(reader.GetString(3))) json.WriteNumberValue(genre);
            json.WriteEndArray();
            json.WriteString("affiliation", reader.IsDBNull(4) ? null : reader.GetString(4));
            json.WriteString("start_at", RankingBoard.Iso(reader.GetInt64(0)));
            json.WriteString("end_at", RankingBoard.Iso(reader.GetInt64(1)));
            json.WriteNumber("homes", reader.GetInt32(6));
            json.WriteNumber("recorded", reader.GetInt32(7));
            json.WriteNumber("live", reader.GetInt32(8));
            json.WriteNumber("timeshift", reader.GetInt32(9));
            json.WritePropertyName("airings");
            json.WriteRawValue(reader.GetString(5));
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }

    static long Start(DateOnly date) => new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), Japan).ToUnixTimeSeconds();
}
