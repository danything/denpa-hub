using System.Globalization;
using System.Text.Json;

namespace DenpaHub.Features.Ranking;

/// <summary>放送の識別 (original_network_id・service_id・event_id)</summary>
public readonly record struct AiringId(int NetworkId, int ServiceId, int EventId)
{
    /// <summary>地上波・BS・CS の original_network_id (ARIB TR-B14・TR-B15)。BS は 4、CS は 6・7、地上波は 0x7880〜0x7FE8</summary>
    public static bool IsCountedNetwork(int networkId) => networkId is 4 or 6 or 7 || Terrestrial.IsTerrestrial(networkId);
}

/// <summary>送られた予約 1 件 (<c>add</c> の 1 つ)</summary>
/// <param name="StartAt">始まり (Unix 秒)</param>
/// <param name="Duration">長さ (秒)</param>
/// <param name="Genres">ジャンルの大分類。送られた順のまま、重なりだけを除く</param>
/// <param name="Manual">手動の予約なら true、ルールの予約なら false</param>
public sealed record AddVote(AiringId Id, long StartAt, int Duration, string Name, string ServiceName, IReadOnlyList<int> Genres, bool Manual)
{
    public long EndAt => StartAt + Duration;
}

/// <summary>
/// <c>POST /ranking/votes</c> の本文を読む。全体の形が違えば断り、1 件ずつの中身は確かめて、通らないものは数えずに rejected に数える。
/// 決まりは docs/ranking.md の「エンドポイント」
/// </summary>
public static class VoteRequest
{
    /// <summary>1 回の add の上限。ふつうの家の予約は数百件なので、ルールで広く録る家でも収まる</summary>
    public const int MaxAdd = 2000;
    /// <summary>
    /// 1 回の remove の上限。オフにしたときは送った分を全部 remove で送るので add より多くする。
    /// 識別の 3 つだけで小さいので、1 MiB の本文に収まる
    /// </summary>
    public const int MaxRemove = 5000;
    /// <summary>受ける番組の始まりは今から 9 日先まで (denpa の番組表の幅)</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromDays(9);
    public const int MaxDuration = 24 * 60 * 60;
    public const int MaxName = 120;
    public const int MaxServiceName = 40;

    /// <summary>ISO 8601 の日時。時差 (+09:00 か Z) の無いものは受けない</summary>
    static readonly string[] TimeFormats = ["yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss'Z'"];

    public sealed record Parsed(IReadOnlyList<AddVote> Add, IReadOnlyList<AiringId> Remove, int Rejected);

    /// <summary>読めなければ <paramref name="error"/> に返すエラーの名前を入れて null</summary>
    public static Parsed? Parse(JsonElement root, DateTimeOffset now, out string? error)
    {
        error = "invalid_request";
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != 1)
        {
            error = "unsupported_version";
            return null;
        }
        if (!TryItems(root, "add", out var addItems) || !TryItems(root, "remove", out var removeItems)) return null;
        if (addItems.Length > MaxAdd || removeItems.Length > MaxRemove)
        {
            error = "too_many_items";
            return null;
        }

        var rejected = 0;
        var adds = new List<AddVote>();
        foreach (var item in addItems)
        {
            if (ReadAdd(item, now) is { } vote) adds.Add(vote);
            else rejected++;
        }
        var removes = new List<AiringId>();
        foreach (var item in removeItems)
        {
            if (ReadId(item) is { } id) removes.Add(id);
            else rejected++;
        }
        error = null;
        return new Parsed(adds, removes, rejected);
    }

    /// <summary>無いときは空とみなす (空の送信は add も remove も省いてよい)</summary>
    static bool TryItems(JsonElement root, string name, out JsonElement[] items)
    {
        items = [];
        if (!root.TryGetProperty(name, out var e)) return true;
        if (e.ValueKind != JsonValueKind.Array) return false;
        items = [.. e.EnumerateArray()];
        return true;
    }

    static AddVote? ReadAdd(JsonElement item, DateTimeOffset now)
    {
        if (ReadId(item) is not { } id) return null;
        if (!item.TryGetProperty("start_at", out var startText) || startText.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParseExact(startText.GetString(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start))
            return null;
        // 始まった番組は数えない。先は番組表の幅まで
        if (start <= now || start > now + Horizon) return null;
        if (!Int(item, "duration", 1, MaxDuration, out var duration)) return null;
        if (!Text(item, "name", MaxName, out var name) || !Text(item, "service_name", MaxServiceName, out var serviceName)) return null;
        if (!item.TryGetProperty("genres", out var genreItems) || genreItems.ValueKind != JsonValueKind.Array || genreItems.GetArrayLength() > 16) return null;
        var genres = new List<int>();
        foreach (var genre in genreItems.EnumerateArray())
        {
            if (genre.ValueKind != JsonValueKind.Number || !genre.TryGetInt32(out var g) || g is < 0 or > 15) return null;
            if (!genres.Contains(g)) genres.Add(g);
        }
        if (!item.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String) return null;
        bool manual;
        switch (kind.GetString())
        {
            case "manual": manual = true; break;
            case "rule": manual = false; break;
            default: return null;
        }
        return new AddVote(id, start.ToUnixTimeSeconds(), duration, name, serviceName, genres, manual);
    }

    static AiringId? ReadId(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        if (!Int(item, "network_id", 0, ushort.MaxValue, out var network) || !AiringId.IsCountedNetwork(network)) return null;
        if (!Int(item, "service_id", 1, ushort.MaxValue, out var service)) return null;
        if (!Int(item, "event_id", 1, ushort.MaxValue, out var @event)) return null;
        return new AiringId(network, service, @event);
    }

    static bool Int(JsonElement item, string name, int min, int max, out int value)
    {
        value = 0;
        return item.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value) && value >= min && value <= max;
    }

    /// <summary>前後の空白を除いて 1〜<paramref name="max"/> 文字。制御文字 (改行など) を含むものは受けない</summary>
    static bool Text(JsonElement item, string name, int max, out string value)
    {
        value = "";
        if (!item.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.String) return false;
        value = e.GetString()!.Trim();
        return value.Length > 0 && value.Length <= max && !value.Any(char.IsControl);
    }
}
