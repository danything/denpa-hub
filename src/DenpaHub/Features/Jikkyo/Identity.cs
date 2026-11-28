using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace DenpaHub.Features.Jikkyo;

/// <summary>
/// **利用者の ID と、NX-Jikkyo での ID。** どれも <c>JIKKYO_KEY</c> から作るので、鍵を持たない人には辿れない (docs/jikkyo.md の「NX-Jikkyo に書く」)
/// </summary>
public sealed class Identity(JikkyoSettings settings)
{
    /// <summary>保存する利用者の ID。denpa が送る <c>author</c> をそのまま持たず、家の ID と合わせて鍵付きのハッシュにする (32 文字の 16 進)</summary>
    public string Author(string home, string author) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(settings.Key, Encoding.UTF8.GetBytes($"author\0{home}\0{author}")).AsSpan(0, 16));

    /// <summary>NX-Jikkyo に書くときに付ける Cookie <c>NX-User-ID</c> の値。利用者ごとに固定</summary>
    public string NxCookie(string author) =>
        Base64Url.EncodeToString(HMACSHA256.HashData(settings.Key, Encoding.UTF8.GetBytes($"nx-user\0{author}")));

    /// <summary>
    /// NX-Jikkyo がその Cookie から付ける <c>user_id</c> (Cookie の値の SHA-1 の 16 進。NX-Jikkyo の <c>GenerateClientID</c>)。
    /// 戻ってきたコメントが hub の利用者のものかを、本文ではなくこれで見分ける
    /// </summary>
    public string NxUserId(string author) =>
        Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(NxCookie(author))));
}

/// <summary>
/// 直近に書いた hub の利用者の NX-Jikkyo での ID。NX-Jikkyo から戻ってきた独自コメントを捨てるために持つ (1 日)。
/// 起動したときは DB の直近 1 日の利用者から作る
/// </summary>
public sealed class HubUsers
{
    public static readonly TimeSpan Span = TimeSpan.FromDays(1);

    readonly ConcurrentDictionary<string, long> ids = new(StringComparer.Ordinal);

    public void Add(string nxUserId, DateTimeOffset now) => ids[nxUserId] = (now + Span).ToUnixTimeMilliseconds();

    public bool Contains(string? nxUserId) => nxUserId is not null && ids.ContainsKey(nxUserId);

    public void Expire(DateTimeOffset now)
    {
        var at = now.ToUnixTimeMilliseconds();
        foreach (var (id, until) in ids)
            if (until < at) ids.TryRemove(id, out _);
    }
}
