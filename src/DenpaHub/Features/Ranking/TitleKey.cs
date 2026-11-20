using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DenpaHub.Features.Ranking;

/// <summary>
/// 地域をまたいで番組をまとめるための、整えた番組名と話数 (docs/ranking.md の「整えた番組名」「前置きを落とす」)。
/// denpa の <c>src/lib/server/title.ts</c> の <c>displayTitle</c>・<c>searchable</c>・<c>parseTitle</c> と同じ考えで作る
/// </summary>
public static partial class TitleKey
{
    /// <summary>地域をまたいでまとめる、整えた名前の文字数の下限。短い名前 (「ニュース」) はどの局にもあり、取り違えやすい</summary>
    public const int MinLength = 4;

    /// <summary>
    /// 番組表の飾りの外字 (ARIB の 90 区の囲み文字。🈑 🈟 🈡 など)。飾りなので、ほかの飾りと同じく落とす。
    /// 範囲は denpa の <c>src/lib/fold.ts</c> の表で、飾りに寄せている字
    /// </summary>
    static bool IsDecorationMark(Rune rune) => rune.Value is >= 0x1F210 and <= 0x1F225 or 0x1F131 or 0x1F13D or 0x1F13F or 0x1F142 or 0x1F146 or (>= 0x1F14A and <= 0x1F14E);

    /// <summary>
    /// 地方局が番組名の先頭に付ける局名と略称 (半角に寄せて小文字にしたあとの形)。系列の表 (<see cref="Terrestrial.Affiliations"/>) の局の名前と、その略称。
    /// 長いものから当てる (「テレビ朝日」を「テレ朝」より先に)。NHK は「NHKスペシャル」のように番組名の一部なので入れない。
    /// まとまらなかった名前を見て足していく
    /// </summary>
    static readonly string[] Stations = [.. new[]
    {
        // キー局と準キー局・広域の局
        "日本テレビ", "日テレ", "tbs", "フジテレビ", "テレビ朝日", "テレ朝", "テレビ東京", "テレ東",
        "mbs", "毎日放送", "abcテレビ", "abc", "関西テレビ", "カンテレ", "ktv", "読売テレビ", "ytv",
        "東海テレビ", "cbcテレビ", "cbc", "メ~テレ", "メ〜テレ", "中京テレビ", "テレビ愛知", "テレビ大阪",
        "hbc", "北海道放送", "stv", "札幌テレビ", "htb", "北海道テレビ", "uhb", "tvh",
        "rnc", "西日本テレビ", "ksb", "瀬戸内海放送", "rsk", "tsc", "テレビせとうち", "ohk",
        "山陰中央テレビ", "bss", "日本海テレビ",
        // 県域の局
        "tbc", "仙台放送", "ミヤギテレビ", "khb", "東日本放送",
        "abs", "秋田放送", "akt", "秋田テレビ", "秋田朝日放送",
        "ybc", "山形放送", "yts", "山形テレビ", "tuy", "さくらんぼテレビ",
        "ibc", "テレビ岩手", "めんこいテレビ", "岩手朝日テレビ",
        "福島テレビ", "福島中央テレビ", "kfb", "福島放送", "テレビユー福島",
        "rab", "青森放送", "atv", "青森テレビ", "青森朝日放送",
        "テレビ信州", "abn", "長野朝日放送", "sbc", "信越放送", "nbs", "長野放送",
        "bsn", "nst", "teny", "テレビ新潟", "新潟テレビ21",
        "ybs", "山梨放送", "uty",
        "テレビ金沢", "北陸朝日放送", "mro", "石川テレビ",
        "sbs", "テレビ静岡", "daiichi-tv", "静岡朝日テレビ",
        "fbc", "福井テレビ", "knb", "北日本放送", "bbt", "富山テレビ", "チューリップテレビ",
        "rcc", "広島テレビ", "広島ホームテレビ", "tss",
        "kry", "山口放送", "tys", "テレビ山口", "yab", "山口朝日",
        "南海放送", "愛媛朝日", "あいテレビ", "テレビ愛媛", "四国放送", "高知放送", "テレビ高知", "さんさんテレビ",
        "kbc", "九州朝日放送", "rkb", "fbs", "福岡放送", "tvq", "九州放送", "tnc", "テレビ西日本",
        "rkk", "熊本放送", "tku", "テレビ熊本", "kkt", "くまもと県民", "kab", "熊本朝日放送",
        "nbc", "長崎放送", "ktn", "テレビ長崎", "ncc", "長崎文化放送", "nib", "長崎国際テレビ",
        "mbc", "南日本放送", "kts", "鹿児島テレビ", "kkb", "鹿児島放送", "kyt", "鹿児島読売tv",
        "mrt", "宮崎放送", "umk", "テレビ宮崎", "obs", "大分放送", "tos", "テレビ大分", "oab", "大分朝日放送",
        "sts", "サガテレビ", "rbc", "qab", "琉球朝日放送", "otv", "沖縄テレビ",
    }.OrderByDescending(s => s.Length).ThenBy(s => s, StringComparer.Ordinal)];

    /// <summary>整えた名前と、読めれば話数</summary>
    public readonly record struct Key(string Name, int? Episode)
    {
        /// <summary>整えた名前の文字数 (コードポイントで数える)。まとめるときに何度も比べるので、作るときに 1 度だけ数える</summary>
        public int Length { get; } = Name.EnumerateRunes().Count();
    }

    /// <summary>番組表のとおりの番組名から、整えた名前と話数を作る</summary>
    public static Key Of(string name)
    {
        var folded = Fold(name);
        var plain = Decoration().Replace(folded, "");
        var episode = EpisodeOf(plain);

        var text = Frame().Replace(plain.Trim(), "");
        text = WithoutPrefixes(text);
        return new Key(Symbols().Replace(text, ""), episode);
    }

    /// <summary>
    /// 飾りの外字を落とし、全角の英数・記号・空白を半角に寄せ、小文字にする。
    /// 寄せるのは NFKC のうち番組名に出るこの分だけ。hub は ICU を持たずに動くので (<c>InvariantGlobalization</c>)、<c>string.Normalize</c> は全角を寄せない
    /// </summary>
    static string Fold(string name)
    {
        var builder = new StringBuilder(name.Length);
        Span<char> units = stackalloc char[2];
        foreach (var rune in name.EnumerateRunes())
        {
            if (IsDecorationMark(rune)) continue;
            var c = rune.Value switch
            {
                >= 0xFF01 and <= 0xFF5E => new Rune(rune.Value - 0xFEE0), // ！Ａ～ → !A~
                0x3000 => new Rune(' '),
                0xFF65 => new Rune('・'), // 半角の中黒
                _ => rune,
            };
            builder.Append(units[..c.EncodeToUtf16(units)]);
        }
        return builder.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// 先頭の局名・<c>【】</c> の前置き・続きのある <c>「」</c> <c>『』</c> を、無くなるまで落とす。名前の途中と後ろは触らない。
    /// 落とすと何も残らないものは落とさない
    /// </summary>
    static string WithoutPrefixes(string text)
    {
        while (true)
        {
            text = text.TrimStart();
            var next = StripPrefix(text).TrimStart();
            if (next.Length == 0 || next == text) return text;
            text = next;
        }
    }

    static string StripPrefix(string text)
    {
        foreach (var station in Stations)
        {
            if (!text.StartsWith(station, StringComparison.Ordinal)) continue;
            // 英字で終わる略称は、続きが英数字なら別の語の先頭なので落とさない (`tos` と `toshiba…`、`rab` と `rabbit…`)
            if (char.IsAsciiLetterOrDigit(station[^1]) && text.Length > station.Length && char.IsAsciiLetterOrDigit(text[station.Length])) continue;
            return text[station.Length..];
        }
        if (LeadingLabel().Match(text) is { Success: true } label) return text[label.Length..];
        // 括弧の後ろが話数と副題だけなら、括弧の中が番組名なので落とさない (`「例のドラマ」#5「副題」`)
        if (LeadingQuote().Match(text) is { Success: true } quote && HasTitle(text[quote.Length..])) return text[quote.Length..];
        return text;
    }

    /// <summary>話数と末尾の副題を除いても、名前が残るか</summary>
    static bool HasTitle(string text) => Symbols().Replace(TrailingSubtitle().Replace(Episode().Replace(text, ""), ""), "").Length > 0;

    /// <summary>
    /// 話数。denpa の <c>parseTitle</c> と同じ読み方で、末尾の <c>「副題」</c> と期 (<c>第2期</c>) を除いてから、
    /// <c>#12</c> <c>第12話</c> を、無ければ英語の話数 (<c>Episode 2</c> など) を読む
    /// </summary>
    static int? EpisodeOf(string text)
    {
        var series = TrailingSubtitle().Replace(text, "");
        series = Season().Replace(series, " ");
        var match = Episode().Match(series);
        if (!match.Success) match = LabeledEpisode().Match(series);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>番組表の飾り (<c>[字]</c> <c>【新】</c> <c>(再)</c> など)。denpa の <c>title.ts</c> の <c>DECORATION</c> と同じ字。半角に寄せたあとなので括弧は半角</summary>
    [GeneratedRegex(@"[【\[(]\s*(?:新|終|再|字|解|デ|二|多|ss|初|最終回|無料|無|生|映)\s*[】\])]")]
    private static partial Regex Decoration();

    /// <summary>先頭の枠の名前 (<c>&lt;アニメギルド&gt;</c>)</summary>
    [GeneratedRegex(@"^<[^<>]{1,20}>\s*")]
    private static partial Regex Frame();

    /// <summary>先頭の <c>【】</c> の前置き (<c>【ドラマ】</c> <c>【特番】</c>)。中身によらず落とす</summary>
    [GeneratedRegex(@"^【[^】]*】")]
    private static partial Regex LeadingLabel();

    /// <summary>先頭の <c>「」</c> <c>『』</c> で、後ろに続きがあるもの (<c>「大阪」ローカルの…</c>)。続きが無ければ当たらない (括弧は下で外す)</summary>
    [GeneratedRegex(@"^[「『][^「『」』]*[」』](?=\s*\S)")]
    private static partial Regex LeadingQuote();

    /// <summary>空白と、落とす記号。長音 (ー) と数字は残す。「･」は「・」に、「～」は「~」に寄せてある</summary>
    [GeneratedRegex(@"[\s・!?~〜:|\-「」『』()\[\]【】♪★☆◆◇▼▽]")]
    private static partial Regex Symbols();

    [GeneratedRegex(@"[「『][^「『」』]+[」』]\s*$")]
    private static partial Regex TrailingSubtitle();

    [GeneratedRegex(@"\s*(?:[【\[(]\s*)?第\s*(?:[0-9]{1,4}|[一二三四五六七八九十]{1,3})\s*(?:期|シーズン|クール|章|部)(?:\s*[】\])])?(?=\s*(?:#|第\s*[0-9]|$))")]
    private static partial Regex Season();

    [GeneratedRegex(@"\s*(?:#|第)\s*([0-9]{1,4})\s*(?:話|回)?")]
    private static partial Regex Episode();

    [GeneratedRegex(@"\s*(?<![a-z0-9【\[(])(?:chapter|episode|ep|request|file|case|act|lesson|mission|karte)\s*\.?\s*([0-9]{1,4})(?!\.?[0-9]|年)\.?")]
    private static partial Regex LabeledEpisode();
}
