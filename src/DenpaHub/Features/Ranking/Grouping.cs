namespace DenpaHub.Features.Ranking;

/// <summary>
/// 同じ番組の放送を 1 つにまとめる (docs/ranking.md の「名寄せ」)。BS・CS と地上波の同じ地域の中は放送の識別のまま 1 つずつで、
/// 地上波の地域をまたぐものだけをここでまとめる。まとめるのは順位を作るときだけで、票は放送ごとに持つ (決まりを直せば次の回から戻る)。
///
/// まとめきれないものは別の番組として数える。違う番組を 1 つにまとめるより、同じ番組が 2 行に分かれるほうが害が小さいため
/// </summary>
public static class Grouping
{
    /// <summary>地域をまたいでまとめる始まりの時刻の幅。番組表の区切りのずれを許し、隣の日の回とはまとめない</summary>
    public static readonly TimeSpan StartWindow = TimeSpan.FromMinutes(10);
    /// <summary>含まれるかで見るときの、短いほうの名前の文字数の下限</summary>
    public const int MinContainedLength = 8;
    /// <summary>含まれるかで見るときの、短いほうの名前の長さの下限 (長いほうに対する割合。10 分の 6)</summary>
    public const int MinContainedTenths = 6;

    /// <summary>まとめるかを見る、系列のある地上波の局の放送 1 つ。整えた名前が短いものは地域をまたがない</summary>
    sealed record Item(Candidate Airing, int Affiliation, TitleKey.Key Key)
    {
        public bool Crosses => Key.Length >= TitleKey.MinLength;
    }

    sealed class Cluster(Item first)
    {
        public List<Item> Members { get; } = [first];
        /// <summary>同じ局の放送がもう 1 つまとまろうとした。最後にばらす</summary>
        public bool Conflicted { get; set; }
    }

    /// <summary>放送をまとめる。返す順は決まっていない (並べるのは順位を作る側)</summary>
    public static List<List<Candidate>> Group(IEnumerable<Candidate> candidates)
    {
        var result = new List<List<Candidate>>();
        var crossing = new List<Item>();
        foreach (var candidate in candidates)
        {
            // 名前を整えるのは、地域をまたぎうる放送 (系列のある地上波の局) だけ
            if (Terrestrial.AffiliationOf(candidate.Id) is { } affiliation && new Item(candidate, affiliation, TitleKey.Of(candidate.Name)) is { Crosses: true } item)
                crossing.Add(item);
            else
                result.Add([candidate]);
        }

        // 毎回同じまとまりになるよう、始まりの順 (同じなら識別の順。地上波の service_id は上位のビットが地域) に入れていく
        crossing.Sort((a, b) => (a.Airing.StartAt, a.Airing.Id.ServiceId, a.Airing.Id.EventId)
            .CompareTo((b.Airing.StartAt, b.Airing.Id.ServiceId, b.Airing.Id.EventId)));
        var groups = new List<Cluster>();
        var alone = new List<Item>();
        // 探す先は、系列ごとの、最初の放送の始まりが 10 分の幅に入っているまとまりだけ。始まりの順に入れるので、幅を外れたまとまりにはもう入らない。
        // 全国の地上波 (9 日分で数万件) を、まとまりの全部と比べずに済ませるため
        var open = new Dictionary<int, List<Cluster>>();
        var window = (long)StartWindow.TotalSeconds;
        foreach (var item in crossing)
        {
            var targets = open.TryGetValue(item.Affiliation, out var list) ? list : open[item.Affiliation] = [];
            targets.RemoveAll(g => g.Members[0].Airing.StartAt < item.Airing.StartAt - window);
            // まとまりの全部の放送と同じ番組とみなせるときだけ入れる。名前が同じものを、含まれるかで当たるものより先に選ぶ
            Cluster? group = null;
            foreach (var target in targets)
            {
                if (!target.Members.TrueForAll(m => Same(m, item))) continue;
                if (target.Members.Exists(m => m.Key.Name == item.Key.Name))
                {
                    group = target;
                    break;
                }
                group ??= target;
            }
            if (group is null)
            {
                var cluster = new Cluster(item);
                groups.Add(cluster);
                targets.Add(cluster);
                continue;
            }
            // 1 つの局から 2 つ以上の放送がまとまろうとしたら、まとめない (同じ名前の短い番組を続けて流すなど)。同じ局の家が違う放送を予約しているので、別の番組とみる。
            // 同じ地域の同じ系列の別の局 (福岡の NHK の福岡と北九州) は、同じ番組を流しているのでまとめる
            if (group.Members.Exists(m => m.Airing.Id.ServiceId == item.Airing.Id.ServiceId))
            {
                group.Conflicted = true;
                alone.Add(item);
                continue;
            }
            group.Members.Add(item);
        }

        foreach (var group in groups)
        {
            if (group.Conflicted) result.AddRange(group.Members.Select(m => new List<Candidate> { m.Airing }));
            else result.Add([.. group.Members.Select(m => m.Airing)]);
        }
        result.AddRange(alone.Select(m => new List<Candidate> { m.Airing }));
        return result;
    }

    /// <summary>
    /// 地域をまたいで同じ番組とみなすか。同じ系列で、始まりが 10 分以内で、整えた名前が同じか、片方がもう片方を含むもの。
    /// 含まれるかで見るのは、短いほうが 8 文字以上で長いほうの 6 割以上の長さがあり、話数が読めるなら話数が同じときだけ
    /// </summary>
    static bool Same(Item a, Item b)
    {
        if (a.Affiliation != b.Affiliation || Math.Abs(a.Airing.StartAt - b.Airing.StartAt) > (long)StartWindow.TotalSeconds) return false;
        if (a.Key.Name == b.Key.Name) return true;
        var (shorter, longer) = a.Key.Length <= b.Key.Length ? (a.Key, b.Key) : (b.Key, a.Key);
        if (shorter.Length < MinContainedLength || shorter.Length * 10 < longer.Length * MinContainedTenths) return false;
        if (shorter.Episode is { } x && longer.Episode is { } y && x != y) return false;
        return longer.Name.Contains(shorter.Name, StringComparison.Ordinal);
    }
}
