using System.Text.Json;
using DenpaHub.Features.Ranking;

namespace DenpaHub.Tests;

/// <summary>地上波の地域をまたぐまとめ (docs/ranking.md の「地上波の地域をまたぐもの」)</summary>
public class GroupingTests
{
    const int Kanto = 0x7FE0;
    const int Kinki = 0x7FD0;
    const int Chukyo = 0x7FC0;
    const int Prefecture = 0x7F00;

    // 系列の表にある局 (service_id)
    const int TbsKanto = 1048; // 関東広域 TBS
    const int MbsKinki = 2064; // 近畿広域 MBS毎日放送
    const int CbcChukyo = 3096; // 中京広域 CBC
    const int NtvKanto = 1040; // 関東広域 日テレ
    const int TokyoMx = 23608; // 東京 TOKYO MX1 (独立局)
    const int SunTv = 43056; // 兵庫 サンテレビ (独立局)

    static readonly long Start = DateTimeOffset.Parse("2026-11-22T21:00:00+09:00").ToUnixTimeSeconds();

    static int nextEvent = 1;

    static Candidate Airing(int networkId, int serviceId, string name, long? start = null, int manual = 1, int rule = 0) =>
        new(new AiringId(networkId, serviceId, Interlocked.Increment(ref nextEvent)), manual, rule, name, "局", [3], start ?? Start);

    /// <summary>まとまった放送の組を、event_id の並びで返す (比べやすいように)</summary>
    static List<int[]> Groups(params Candidate[] airings) =>
        [.. Grouping.Group(airings).Select(g => g.Select(c => c.Id.EventId).Order().ToArray()).OrderBy(g => g[0])];

    static int[] Ids(params Candidate[] airings) => [.. airings.Select(a => a.Id.EventId).Order()];

    [Test]
    public async Task 同時ネットの番組は地域をまたいで_1_つにまとめる()
    {
        var tbs = Airing(Kanto, TbsKanto, "[字]例のバラエティ　拡大ＳＰ");
        var mbs = Airing(Kinki, MbsKinki, "例のバラエティ 拡大SP[字][デ]");
        var cbc = Airing(Chukyo, CbcChukyo, "🈑例のバラエティ　拡大ＳＰ");
        var groups = Groups(tbs, mbs, cbc);
        await Assert.That(groups.Count).IsEqualTo(1);
        await Assert.That(groups[0]).IsEquivalentTo(Ids(tbs, mbs, cbc));
    }

    [Test]
    public async Task 地方局が付ける前置きを落としてまとめる()
    {
        await Assert.That(TitleKey.Of("[字]ＴＢＳ日曜劇場「例のドラマ」　第３話").Name).IsEqualTo("日曜劇場例のドラマ第3話");
        await Assert.That(TitleKey.Of("日曜劇場 例のドラマ 第3話[字][デ]").Name).IsEqualTo("日曜劇場例のドラマ第3話");
        await Assert.That(TitleKey.Of("【アニメ】ＭＢＳ例のアニメ　＃５").Name).IsEqualTo("例のアニメ#5");
        await Assert.That(TitleKey.Of("「大阪」ローカルの例の番組").Name).IsEqualTo("ローカルの例の番組");
        // 名前の全部が括弧の中なら、括弧を外すだけで中身は残す
        await Assert.That(TitleKey.Of("「例の特番」").Name).IsEqualTo("例の特番");
        await Assert.That(TitleKey.Of("<アニメギルド>例のアニメ🈟").Name).IsEqualTo("例のアニメ");
        // 英字の略称は、続きが英数字なら別の語の先頭なので落とさない
        await Assert.That(TitleKey.Of("ＴＯＳＨＩＢＡ例の劇場").Name).IsEqualTo("toshiba例の劇場");
        await Assert.That(TitleKey.Of("ＴＯＳ例の劇場").Name).IsEqualTo("例の劇場");

        var tbs = Airing(Kanto, TbsKanto, "[字]ＴＢＳ日曜劇場「例のドラマ」　第３話");
        var mbs = Airing(Kinki, MbsKinki, "日曜劇場 例のドラマ 第3話[字][デ]");
        await Assert.That(Groups(tbs, mbs)).Count().IsEqualTo(1);
    }

    [Test]
    public async Task 片方がもう片方を含むときは_8_文字以上で_6_割以上ならまとめる()
    {
        // 「例のドラマ第3話」(8 文字) は「日曜劇場例のドラマ第3話」(12 文字) の 6 割以上
        var longer = Airing(Kanto, TbsKanto, "日曜劇場 例のドラマ 第3話");
        var shorter = Airing(Kinki, MbsKinki, "例のドラマ 第3話");
        await Assert.That(Groups(longer, shorter)).Count().IsEqualTo(1);

        // 8 文字と 13 文字 (6 割は 7.8 文字) はまとめ、8 文字と 14 文字 (8.4 文字) はまとめない
        await Assert.That(Groups(Airing(Kanto, TbsKanto, "あいうえか例のドラマ第3話"), Airing(Kinki, MbsKinki, "例のドラマ第3話"))).Count().IsEqualTo(1);
        await Assert.That(Groups(Airing(Kanto, TbsKanto, "あいうえかき例のドラマ第3話"), Airing(Kinki, MbsKinki, "例のドラマ第3話"))).Count().IsEqualTo(2);

        // 短いほうが 8 文字に満たなければ、6 割以上でもまとめない (「例のドラマ第3」は 7 文字)
        await Assert.That(Groups(Airing(Kanto, TbsKanto, "例のドラマ第3回"), Airing(Kinki, MbsKinki, "例のドラマ第3"))).Count().IsEqualTo(2);
        await Assert.That(Groups(Airing(Kanto, TbsKanto, "ニュース7"), Airing(Kinki, MbsKinki, "ニュース"))).Count().IsEqualTo(2);

        // 話数が読めて違えば、含まれていてもまとめない
        await Assert.That(Groups(Airing(Kanto, TbsKanto, "例の長いドラマ #12"), Airing(Kinki, MbsKinki, "例の長いドラマ #1"))).Count().IsEqualTo(2);
    }

    [Test]
    public async Task 整えた名前が_4_文字に満たなければ地域をまたいでまとめない()
    {
        await Assert.That(Groups(Airing(Kanto, TbsKanto, "ニュー"), Airing(Kinki, MbsKinki, "ニュー"))).Count().IsEqualTo(2);
        // 4 文字ならまとめる
        await Assert.That(Groups(Airing(Kanto, TbsKanto, "ニュース"), Airing(Kinki, MbsKinki, "ニュース"))).Count().IsEqualTo(1);
    }

    [Test]
    public async Task 始まりが_10_分を超えて離れていればまとめない()
    {
        var name = "例のバラエティ拡大SP";
        await Assert.That(Groups(Airing(Kanto, TbsKanto, name), Airing(Kinki, MbsKinki, name, Start + 600))).Count().IsEqualTo(1);
        await Assert.That(Groups(Airing(Kanto, TbsKanto, name), Airing(Kinki, MbsKinki, name, Start + 601))).Count().IsEqualTo(2);
        // 隣の日の同じ名前の回ともまとめない
        await Assert.That(Groups(Airing(Kanto, TbsKanto, name), Airing(Kinki, MbsKinki, name, Start + 24 * 60 * 60))).Count().IsEqualTo(2);
    }

    [Test]
    public async Task 同じ地域から同じ系列の放送が_2_つまとまろうとしたらまとめない()
    {
        var name = "例のバラエティ拡大SP";
        var first = Airing(Kanto, TbsKanto, name);
        var second = Airing(Kanto, TbsKanto, name, Start + 300);
        var mbs = Airing(Kinki, MbsKinki, name);
        var groups = Groups(first, second, mbs);
        await Assert.That(groups.Count).IsEqualTo(3);
    }

    [Test]
    public async Task 独立局と系列の違う局とBSはまとめない()
    {
        var name = "例のバラエティ拡大SP";
        await Assert.That(Groups(Airing(Prefecture, TokyoMx, name), Airing(Prefecture, SunTv, name))).Count().IsEqualTo(2);
        await Assert.That(Groups(Airing(Kanto, TbsKanto, name), Airing(Kanto, NtvKanto, name))).Count().IsEqualTo(2);
        await Assert.That(Groups(Airing(Kanto, TbsKanto, name), Airing(4, 211, name))).Count().IsEqualTo(2);
    }

    [Test]
    public async Task 系列と地域の表()
    {
        await Assert.That(Terrestrial.Affiliations.Count).IsEqualTo(241);
        await Assert.That(Terrestrial.Regions.Count).IsEqualTo(59);
        await Assert.That(Terrestrial.RegionName(new AiringId(Kanto, TbsKanto, 1))).IsEqualTo("関東広域");
        await Assert.That(Terrestrial.RegionName(new AiringId(Prefecture, 63504, 1))).IsEqualTo("沖縄");
        await Assert.That(Terrestrial.RegionName(new AiringId(4, 211, 1))).IsNull();
        await Assert.That(Terrestrial.AffiliationOf(new AiringId(Kinki, MbsKinki, 1))).IsEqualTo(6);
        await Assert.That(Terrestrial.AffiliationOf(new AiringId(Prefecture, TokyoMx, 1))).IsNull();
        // 系列の表の局は、どれも地域の表に地域がある
        foreach (var service in Terrestrial.Affiliations.Keys)
            await Assert.That(Terrestrial.Regions.ContainsKey(service >> 10)).IsTrue();
    }

    [Test]
    public async Task 地域をまたいでまとめた番組は合わせて_5_トークンで順位に出る()
    {
        await using var h = await RankingHarness.StartAsync();
        var start = h.Time.Now.AddDays(1);
        var tbs = RankingHarness.Airing(10, start, name: "[字]ＴＢＳ日曜劇場「例のドラマ」　第３話", networkId: Kanto, serviceId: TbsKanto, serviceName: "TBS");
        var mbs = RankingHarness.Airing(20, start.AddMinutes(5), name: "日曜劇場 例のドラマ 第3話[字][デ]", networkId: Kinki, serviceId: MbsKinki, serviceName: "MBS毎日放送");
        var tokens = await h.VoteManyAsync(3, tbs);
        tokens.AddRange(await h.VoteManyAsync(2, mbs));

        var entries = (await h.RankingAsync(tokens[0])).GetProperty("entries").EnumerateArray().ToArray();
        await Assert.That(entries.Length).IsEqualTo(1);
        var entry = entries[0];
        await Assert.That(entry.GetProperty("votes").GetInt32()).IsEqualTo(5);
        // 名前は票を入れたトークンの多い放送のもの
        await Assert.That(entry.GetProperty("name").GetString()).IsEqualTo("[字]ＴＢＳ日曜劇場「例のドラマ」　第３話");
        await Assert.That(entry.GetProperty("affiliation").GetString()).IsEqualTo("TBS 系");
        var airings = entry.GetProperty("airings").EnumerateArray().ToArray();
        await Assert.That(airings.Length).IsEqualTo(2);
        await Assert.That(airings[0].GetProperty("event_id").GetInt32()).IsEqualTo(10);
        await Assert.That(airings[0].GetProperty("region").GetString()).IsEqualTo("関東広域");
        await Assert.That(airings[0].GetProperty("service_name").GetString()).IsEqualTo("TBS");
        await Assert.That(airings[1].GetProperty("event_id").GetInt32()).IsEqualTo(20);
        await Assert.That(airings[1].GetProperty("region").GetString()).IsEqualTo("近畿広域");
        await Assert.That(airings[1].GetProperty("start_at").GetString())
            .IsEqualTo(start.AddMinutes(5).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd'T'HH:mm:sszzz"));

        // 独立局の放送は、系列なしで地域だけを返す
        var mx = RankingHarness.Airing(30, start.AddHours(2), name: "例の独立局の番組", networkId: Prefecture, serviceId: TokyoMx, serviceName: "TOKYO MX1");
        await h.VoteManyAsync(5, mx);
        var independent = (await h.RankingAsync(tokens[0])).GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("airings")[0].GetProperty("event_id").GetInt32() == 30);
        await Assert.That(independent.GetProperty("affiliation").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(independent.GetProperty("airings")[0].GetProperty("region").GetString()).IsEqualTo("東京");
    }
}
