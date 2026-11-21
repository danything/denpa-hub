using System.Globalization;
using System.Text.Json;

namespace DenpaHub.Features.Ranking;

/// <summary>放送の識別 (original_network_id・service_id・event_id)</summary>
public readonly record struct AiringId(int NetworkId, int ServiceId, int EventId)
{
    /// <summary>地上波・BS・CS の original_network_id (ARIB TR-B14・TR-B15)。BS は 4、CS は 6・7、地上波は 0x7880〜0x7FE8</summary>
    public static bool IsCountedNetwork(int networkId) => networkId is 4 or 6 or 7 || Terrestrial.IsTerrestrial(networkId);
}

/// <summary>
/// その家がその放送をどうしたか (<c>add</c> の <c>kinds</c>)。印に 4 ビットで持つ。
/// 一覧 (人気の番組) の数は予約とライブの和集合 (<see cref="Listed"/>)、録画・ライブ・タイムシフトは外部に示す集計 (<c>tallies</c>) の数
/// </summary>
[Flags]
public enum Kinds
{
    None = 0,
    /// <summary>予約した (録る予定。録画中・録り終えたあとも)</summary>
    Reserved = 1,
    /// <summary>その局のライブで合わせて 5 分以上観た (10 分より短い番組は長さの半分)</summary>
    Live = 2,
    /// <summary>録画を録り終えた</summary>
    Recorded = 4,
    /// <summary>録画を観る画面か追っかけで合わせて 5 分以上再生した (放送が終わって 7 日以内)</summary>
    Timeshift = 8,
    /// <summary>一覧の数に入れる種類</summary>
    Listed = Reserved | Live,
    /// <summary>放送が終わってからしか受けない種類。放送中の「いま観ている」を hub に届かせない</summary>
    AfterEnd = Live | Recorded | Timeshift,
}

/// <summary>送られた放送 1 件 (<c>add</c> の 1 つ)</summary>
/// <param name="StartAt">始まり (Unix 秒)</param>
/// <param name="Duration">長さ (秒)</param>
/// <param name="Genres">ジャンルの大分類。送られた順のまま、重なりだけを除く</param>
/// <param name="Kinds">その家の、その放送についての今の種類の組。送り直すと置き換える</param>
public sealed record AddVote(AiringId Id, long StartAt, int Duration, string Name, string ServiceName, IReadOnlyList<int> Genres, Kinds Kinds)
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
    /// <summary>
    /// 受ける番組の終わりは今から 8 日前より後。タイムシフトを数える 7 日に、6 時間ごとの送信と再試行の分の 1 日を足した幅。
    /// これを過ぎた放送は数を決めて集計の表に移す (<see cref="RankingStore.Cleanup"/>)
    /// </summary>
    public static readonly TimeSpan AfterEnd = TimeSpan.FromDays(8);
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
        // 先は番組表の幅まで。終わった番組はタイムシフトを数える間まで
        if (start > now + Horizon) return null;
        if (!Int(item, "duration", 1, MaxDuration, out var duration)) return null;
        var end = start.AddSeconds(duration);
        if (end <= now - AfterEnd) return null;
        if (!Text(item, "name", MaxName, out var name) || !Text(item, "service_name", MaxServiceName, out var serviceName)) return null;
        if (!item.TryGetProperty("genres", out var genreItems) || genreItems.ValueKind != JsonValueKind.Array || genreItems.GetArrayLength() > 16) return null;
        var genres = new List<int>();
        foreach (var genre in genreItems.EnumerateArray())
        {
            if (genre.ValueKind != JsonValueKind.Number || !genre.TryGetInt32(out var g) || g is < 0 or > 15) return null;
            if (!genres.Contains(g)) genres.Add(g);
        }
        if (ReadKinds(item) is not { } kinds) return null;
        // 録った・観たは、放送が終わってから送るもの。放送中に届けば、送った時刻が「いま観ている」に近くなる
        if ((kinds & Kinds.AfterEnd) != 0 && end > now) return null;
        return new AddVote(id, start.ToUnixTimeSeconds(), duration, name, serviceName, genres, kinds);
    }

    /// <summary>
    /// <c>kinds</c> を読む。知らない種類は読み飛ばし (新しい denpa が足した種類で、全体を断らないため)、知っている種類が 1 つも無ければ null。
    /// <c>kinds</c> の無いもの (<c>kind</c> で手動かルールかを送っていた denpa) は予約として受ける。<c>kind</c> の中身は使わない
    /// </summary>
    static Kinds? ReadKinds(JsonElement item)
    {
        if (!item.TryGetProperty("kinds", out var items)) return Kinds.Reserved;
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 16) return null;
        var kinds = Kinds.None;
        foreach (var e in items.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String) return null;
            kinds |= e.GetString() switch
            {
                "reserved" => Kinds.Reserved,
                "live" => Kinds.Live,
                "recorded" => Kinds.Recorded,
                "timeshift" => Kinds.Timeshift,
                _ => Kinds.None,
            };
        }
        return kinds == Kinds.None ? null : kinds;
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
